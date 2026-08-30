using System;
using Modding;
using Modding.Modules;
using UnityEngine;

namespace BraidsSynth
{
    /// <summary>
    /// The synth block: maps the block's settings onto Braids' macro-oscillator and
    /// renders it straight into Unity's audio stream.
    ///
    /// Braids' controls keep their meaning here -- a MODEL, a pitch, and TIMBRE and
    /// COLOR, which mean whatever the model decides. What the module has and this
    /// does not is a front panel, so the note comes from a slider and a key gates it.
    ///
    /// Everything the mapper offers is also on the UI Factory panel, which is a soft
    /// dependency -- see <see cref="BraidsPanel"/>. The mapper is what the block
    /// saves through either way.
    /// </summary>
    public class BraidsBehaviour : BlockModuleBehaviour<SynthModule>
    {
        /// <summary>How many samples the panel's scope can draw. A power of two.</summary>
        public const int ScopeSize = 1024;

        /// <summary>
        /// How much further than RANGE the sound carries before it is cut off. At
        /// 1/d that is 35 dB down, so the cut lands where there was nothing to hear.
        /// </summary>
        private const float FalloffSpan = 60f;

        private MKey PlayKey;
        private MMenu ModelMenu;
        private MSlider PitchSlider;
        private MSlider FineSlider;
        private MSlider Timbre;
        private MSlider Colour;
        private MSlider VolumeSlider;
        private MSlider AttackSlider;
        private MSlider ReleaseSlider;
        private MSlider RangeSlider;
        private MColourSlider NoteColour;
        private MToggle PushToggle;

        /// <summary>The block's grey, as the shipped texture paints it.</summary>
        private static readonly Color Grey = new Color(0.314f, 0.314f, 0.314f, 1f);

        /// <summary>The note with the gate shut: black, lifted to keep its form.</summary>
        private static readonly Color Resting = new Color(0.071f, 0.071f, 0.071f, 1f);

        // This block's own material and the two-by-two texture under it. The note is
        // part of the block's mesh and takes its colour from a single texel, so one
        // texel per corner is all it takes to light the note without touching the
        // cage -- and being this block's own, lighting one synth block does not light
        // every other one on the machine.
        private MeshRenderer worn;
        private Material paint;
        private Texture2D skin;
        private Color inked;
        private bool complained;

        /// <summary>
        /// What every synth block's AudioSource plays. Never heard -- the filter
        /// overwrites the stream -- but a source has to be playing or Unity does not
        /// run the filter chain at all. One sample of silence, looped, shared by all.
        /// </summary>
        private static AudioClip silence;

        private AudioSource source;
        private AudioListener ear;
        private MacroOscillator oscillator;
        private DcBlocker blocker;
        private int rate;

        // Written by the game thread, read by the audio thread. Plain fields of
        // primitive type: a torn read costs one block of slightly wrong timbre,
        // which is not worth a lock on the audio callback.
        private volatile bool gateOpen;
        private volatile bool previewing;
        private volatile int wantModel;
        private volatile int wantPitch;
        private volatile int wantTimbre;
        private volatile int wantColour;
        private volatile float wantVolume;
        private volatile float attackPerSample;
        private volatile float releasePerSample;
        /// <summary>Besiege's own master volume, or 1 where the game is already
        /// applying it. Read on the game thread by <see cref="Place"/>, as every
        /// other number the audio thread uses is.</summary>
        private volatile float wantMaster = 1f;

        private volatile float wantLeft = 1f;
        private volatile float wantRight = 1f;

        private short[] block;
        private float level;
        private float heldLeft = 1f;
        private float heldRight = 1f;
        private bool playing;

        // The emulated half of the key, filled in by Besiege's own emulation pass
        // and consumed by the next SimulateUpdateAlways. The edges are latched
        // rather than read live, because the two run at different rates.
        private bool emulatedPressPending;
        private bool emulatedDown;

        // The scope's ring buffer. Filled on the audio thread and read on the game
        // thread without a lock: a torn read costs one frame of a picture that is
        // redrawn several times a second.
        private readonly float[] scope = new float[ScopeSize];
        private volatile int scopeWrite;

        public override void SafeAwake()
        {
            PlayKey = AddKey("Play", "Activate", KeyCode.B);
            ModelMenu = AddMenu("ShapeKey", MacroOscillator.ModelTripleSaw,
                                BraidsModels.MenuItems(), false);
            PitchSlider = AddSlider("Note", "PitchKey", 60f, 24f, 96f);
            FineSlider = AddSlider("Fine", "FineKey", 0f, -100f, 100f);
            Timbre = AddSlider("Timbre", "TimbreKey", 0.5f, 0f, 1f);
            Colour = AddSlider("Color", "ColorKey", 0.5f, 0f, 1f);
            VolumeSlider = AddSlider("Volume", "VolumeKey", 0.5f, 0f, 1f);
            // These reach further than their dials do. The panel's sliders keep the
            // travel worth dragging over -- 2 s, 4 s, 100 m -- and a value past the
            // end of one can be typed instead. The limit is the setting's, so what
            // is stored is always inside the bounds it declares.
            AttackSlider = AddSlider("Attack", "AttackKey", 0.01f, 0f, 600f);
            ReleaseSlider = AddSlider("Release", "ReleaseKey", 0.05f, 0f, 600f);
            RangeSlider = AddSlider("Range", "RangeKey", 8f, 1f, 100000f);
            // Stays on Besiege's own mapper rather than moving to the panel: it says
            // nothing about the sound, and it is the one setting worth reaching
            // without opening the block up.
            NoteColour = AddColourSlider("Note", "NoteColorKey",
                                         new Color(0.012f, 1f, 0.847f, 1f), false);
            PushToggle = AddToggle("Toggle", "ToggleKey", false);

            // Everything but the key and the toggle belongs to the panel, which has
            // room to say what each control means. They stay mapper settings, so the
            // machine still saves them: DisplayInMapper is read by the mapper's own
            // controllers and by nothing in the serialiser.
            PanelOnly(ModelMenu, PitchSlider, FineSlider, Timbre, Colour,
                      VolumeSlider, AttackSlider, ReleaseSlider, RangeSlider);

            rate = AudioSettings.outputSampleRate;
            if (rate <= 0)
            {
                rate = BraidsResources.NativeSampleRate;
            }
            // Every table the oscillator reads, built on the game thread so the audio
            // thread never allocates one under itself.
            BraidsResources.Prepare(rate);

            oscillator = new MacroOscillator(rate);
            blocker = new DcBlocker(rate);
            // Sized for the largest buffer Unity will ask for; growing it on the audio
            // thread would be a collection under a running note.
            block = new short[4096];
            PushSettings();

            source = GetComponent<AudioSource>();
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
            }
            source.clip = Silence(rate);
            source.loop = true;
            source.playOnAwake = false;
            // 2D, because the block places itself -- see Place. Unity's own 3D stage
            // runs *before* this component's filter, so it would only ever pan the
            // silence above; and feeding the samples in earlier, through a streaming
            // clip, buys the panning back at the cost of that clip's read-ahead,
            // which is a note that arrives late.
            source.spatialBlend = 0f;

            Dress();

            // No skin picker on this block. `BlockMapper.RefreshLists` shows that
            // control when the prefab's CanGetNewVisuals is true, which is
            // SkinCanBeChanged && (CanChangeMesh || CanChangeTexture) -- and this
            // block wears a mesh and a texture of its own, generated to be looked
            // at, with one texel of that texture carrying the note's colour. A skin
            // over the top replaces both and takes the lit note with it.
            //
            // The prefab is shared by every block of this type, so the first one to
            // awake settles it for all of them, and the flag is not written to disk.
            if (BlockBehaviour != null && BlockBehaviour.Prefab != null)
            {
                BlockBehaviour.Prefab.SkinCanBeChanged = false;
            }
        }

        /// <summary>
        /// Gives the block its own material and a two-by-two texture to wear, so its
        /// note can be lit without lighting every other synth block on the machine.
        ///
        /// Point sampled, so the four texels cannot bleed into one another: the note
        /// looks below a half in u and v, the cage's unwrap sits above 0.61 in both,
        /// and that is one corner each with nothing in between to blur.
        ///
        /// Returns quietly if Besiege has not built the block's mesh yet and Update
        /// tries again, which is cheaper than depending on an order of events this
        /// block has already been wrong about once.
        /// </summary>
        private void Dress()
        {
            if (worn == null)
            {
                // The one whose material *has* a main texture. That finds the mesh
                // the block is actually seen as, and settles in passing that the
                // shader takes its picture from where this code puts one.
                MeshRenderer[] found = GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] != null && found[i].sharedMaterial != null
                        && found[i].sharedMaterial.mainTexture != null)
                    {
                        worn = found[i];
                        break;
                    }
                }
                if (worn == null)
                {
                    if (!complained && oscillator != null)
                    {
                        complained = true;
                        Log.Warn("no textured renderer on the block, so its note "
                                 + "cannot be lit. It will stay black.");
                    }
                    return;
                }
            }

            if (worn.sharedMaterial == paint && skin != null)
            {
                return;
            }

            // Reached again whenever something else has put a material on the block:
            // a repaint, or Besiege building the visual after this block first
            // looked. Checking rather than remembering is what makes either survive
            // -- the old code dressed the block once and never noticed being undone.
            if (skin == null)
            {
                skin = new Texture2D(2, 2, TextureFormat.RGB24, false);
                skin.filterMode = FilterMode.Point;
                skin.wrapMode = TextureWrapMode.Clamp;
                skin.hideFlags = HideFlags.HideAndDontSave;
                inked = new Color(-1f, -1f, -1f, -1f);
                Ink(Resting);
            }

            // Copied from whatever is on the block now, so a paint colour survives
            // this, and only the picture underneath it is this block's own.
            Material next = new Material(worn.sharedMaterial);
            next.hideFlags = HideFlags.HideAndDontSave;
            next.mainTexture = skin;
            if (paint != null)
            {
                UnityEngine.Object.Destroy(paint);
            }
            paint = next;
            worn.sharedMaterial = paint;
        }

        /// <summary>Repaints the one texel the note's vertices look at.</summary>
        private void Ink(Color note)
        {
            if (skin == null || note == inked)
            {
                return;
            }
            skin.SetPixel(0, 0, note);   // below a half in u and v: the note
            skin.SetPixel(1, 0, Grey);
            skin.SetPixel(0, 1, Grey);
            skin.SetPixel(1, 1, Grey);   // above it: the cage
            skin.Apply();
            inked = note;
        }

        /// <summary>The material and its texture are this block's, so they go with it.</summary>
        private void OnDestroy()
        {
            if (paint != null)
            {
                UnityEngine.Object.Destroy(paint);
                paint = null;
            }
            if (skin != null)
            {
                UnityEngine.Object.Destroy(skin);
                skin = null;
            }
        }

        private static void PanelOnly(params MapperType[] settings)
        {
            for (int i = 0; i < settings.Length; i++)
            {
                settings[i].DisplayInMapper = false;
            }
        }

        private static AudioClip Silence(int rate)
        {
            if (silence == null)
            {
                silence = AudioClip.Create("BraidsSilence", 1, 1, rate, false);
                // Out of the scene and out of UnloadUnusedAssets' reach, since the
                // only thing referencing it is this static field.
                silence.hideFlags = HideFlags.HideAndDontSave;
            }
            return silence;
        }

        // ---- what the panel talks to ------------------------------------------

        public MMenu Model { get { return ModelMenu; } }
        public MSlider Note { get { return PitchSlider; } }
        public MSlider Fine { get { return FineSlider; } }
        public MSlider TimbreSlider { get { return Timbre; } }
        public MSlider ColourSlider { get { return Colour; } }
        public MSlider Volume { get { return VolumeSlider; } }
        public MSlider Attack { get { return AttackSlider; } }
        public MSlider Release { get { return ReleaseSlider; } }
        public MSlider Range { get { return RangeSlider; } }

        /// <summary>True while the block is making a sound.</summary>
        public bool IsPlaying { get { return playing; } }

        public bool IsPreviewing { get { return previewing; } }

        /// <summary>
        /// The panel's LISTEN, for choosing a model by ear while the machine is being
        /// built. It only records the wish; <see cref="Update"/> acts on it.
        /// </summary>
        public void SetPreview(bool on)
        {
            if (on == previewing)
            {
                return;
            }
            // A run owns the gate, so LISTEN does not start one there.
            previewing = on && !StatMaster.levelSimulating;
            if (previewing)
            {
                // The standing start a run gets, so a model sounds the same however
                // it is heard: without the Init the oscillator carries the state it
                // was left in, which the stateful models are audibly not clean of.
                level = 0f;
                oscillator.Init();
                blocker.Reset();
                PushSettings();
            }
        }

        /// <summary>
        /// Copies the scope's ring buffer out in order, oldest first. Returns how
        /// many samples were written, which is all of them.
        /// </summary>
        public int ReadScope(float[] into)
        {
            if (into == null || into.Length < ScopeSize)
            {
                return 0;
            }
            int at = scopeWrite;
            for (int i = 0; i < ScopeSize; i++)
            {
                into[i] = scope[(at + i) & (ScopeSize - 1)];
            }
            return ScopeSize;
        }

        // ---- the game thread ---------------------------------------------------

        /// <summary>
        /// The one rule: the source plays while a run is on or the panel is
        /// auditioning, and is stopped otherwise. Checked every frame rather than
        /// switched from the callbacks that change it, because most of those never
        /// reach this object -- Besiege runs a simulation on a *clone* of the machine,
        /// so OnSimulateStart and OnSimulateStop land elsewhere and this block's own
        /// IsSimulating stays false throughout; and BuildingUpdate, the hook meant for
        /// this, is never called by the game at all. A rule that is re-checked cannot
        /// be left on the wrong side of an event that went missing.
        ///
        /// Unity's own Update, for the same reason.
        /// </summary>
        private void Update()
        {
            if (source == null)
            {
                return;
            }

            // The global flag is the only simulation signal that gets here.
            bool simulating = StatMaster.levelSimulating;
            if (simulating)
            {
                previewing = false;
            }

            // The source outlives the gate. Closing the gate leaves the voice its
            // release to play, and stopping the source at that moment is what cut it
            // off under LISTEN -- while a run, which holds the source up for its
            // whole length, let the same release through. `playing` is the audio
            // thread saying it has not reached silence yet.
            bool wanted = previewing || simulating || (playing && source.isPlaying);

            if (wanted)
            {
                if (!simulating)
                {
                    // So the panel's dials are heard as they move, release included.
                    PushSettings();
                }
                Place();
            }

            if (wanted != source.isPlaying)
            {
                if (wanted) { source.Play(); }
                else { source.Stop(); }
            }
            if (!source.isPlaying)
            {
                // Nothing is driving the audio callback now, so it cannot clear this
                // itself -- and the panel would go on drawing the last waveform.
                playing = false;
            }

            // The note lights on the same condition that opens the gate, so a key, a
            // variable and the panel's LISTEN all light it alike. On the simulation's
            // clone that is the gate; on the block the panel edits it is the preview.
            Dress();
            Ink(gateOpen || previewing ? NoteColour.Value : Resting);
        }

        /// <summary>
        /// Works out how loud the block is in each ear, from where it stands relative
        /// to the listener. This is Unity's job normally, but its 3D stage runs before
        /// the filter that produces the sound, so the block does its own: 1/d past
        /// RANGE, and panned by how far round the listener it sits.
        ///
        /// Game thread only -- a transform may not be touched from the audio thread.
        /// </summary>
        /// <summary>
        /// Besiege's master volume, when the game is not applying it to this block.
        ///
        /// Besiege has two kinds of volume control and they arrive differently. The
        /// per-category sliders -- BLOCKS, SFX, MUSIC -- are exposed parameters on
        /// an `AudioMixer`, written by `MusicController.LateUpdate`, and they reach
        /// this block because its `AudioSource` is routed through a mixer group like
        /// any other block's. The **master** slider is not: it sets
        /// `AudioListener.volume`, and Unity does not apply that to audio coming out
        /// of a mixer. So the one slider a player reaches for first did nothing
        /// here.
        ///
        /// The block applies it itself, and only where the game does not: a source
        /// with no mixer group is one the listener's own volume still scales, and
        /// doubling it there would work the slider twice.
        /// </summary>
        private float MasterVolume()
        {
            if (source == null || source.outputAudioMixerGroup == null)
            {
                return 1f;
            }
            BesiegeConfig config = OptionsMaster.BesiegeConfig;
            if (config == null)
            {
                return 1f;
            }
            if (!saidMaster)
            {
                saidMaster = true;
                Log.Info("the master volume slider does not reach audio through "
                         + "Besiege's mixer, so the synth blocks apply it themselves.");
            }
            // A percentage, as `OptionsMaster.SetMasterVolume` reads it.
            return Mathf.Clamp01(config.MasterVolume / 100f);
        }

        /// <summary>Said once, so the log records which case this install is.</summary>
        private static bool saidMaster;

        private void Place()
        {
            wantMaster = MasterVolume();

            if (ear == null || !ear.isActiveAndEnabled)
            {
                // Besiege swaps cameras between building and running, and the
                // listener goes with them, so a held one goes stale rather than null.
                ear = (AudioListener)UnityEngine.Object.FindObjectOfType(typeof(AudioListener));
                if (ear == null)
                {
                    wantLeft = 1f;
                    wantRight = 1f;
                    return;
                }
            }

            Transform head = ear.transform;
            Vector3 delta = transform.position - head.position;
            float distance = delta.magnitude;

            // RANGE is the radius the block is at full volume within; past it the
            // 1/d falloff and the cutoff both scale with it, so turning it up makes
            // the block louder at any distance as well as audible from further off.
            float near = RangeSlider.Value;
            if (near < 0.1f) { near = 0.1f; }
            float far = near * FalloffSpan;

            float gain;
            if (distance <= near) { gain = 1f; }
            else if (distance >= far) { gain = 0f; }
            else { gain = near / distance; }

            // -1 hard left, +1 hard right. Each ear keeps full gain until the block
            // crosses to the other side, so a block straight ahead is as loud as it
            // was before any of this.
            float pan = 0f;
            if (distance > 0.001f)
            {
                pan = Vector3.Dot(head.right, delta / distance);
            }
            wantLeft = gain * Mathf.Min(1f, 1f - pan);
            wantRight = gain * Mathf.Min(1f, 1f + pan);
        }

        /// <summary>Runs on the simulation's clone of the block, not on the panel's.</summary>
        public override void OnSimulateStart()
        {
            gateOpen = false;
            emulatedPressPending = false;
            emulatedDown = false;
            level = 0f;
            oscillator.Init();
            blocker.Reset();
            PushSettings();
        }

        public override void OnSimulateStop()
        {
            // Cleared at both ends of a run: Besiege keeps a behaviour alive between
            // them, so an edge caught as one ends would fire as the next begins.
            gateOpen = false;
            emulatedPressPending = false;
            emulatedDown = false;
        }

        public override void SimulateUpdateAlways()
        {
            PushSettings();

            // The keyboard ORed with the emulation KeyEmulationUpdate latched, which
            // is what lets a variable drive the block in place of a key. The
            // physical half stays here, in Update, where key edges belong; the
            // emulated half cannot be read here at all -- see KeyEmulationUpdate.
            bool held = PlayKey.IsHeld || emulatedDown;
            bool pressed = PlayKey.IsPressed || emulatedPressPending;
            emulatedPressPending = false;

            if (PushToggle.IsActive)
            {
                if (pressed)
                {
                    gateOpen = !gateOpen;
                }
            }
            else
            {
                // Held, rather than the press and release edges it used to watch: a
                // gate cannot then be left open by a release nobody was looking for.
                gateOpen = held;
            }
        }

        /// <summary>
        /// Besiege's own emulation pass, and the only place the emulated edges are
        /// worth reading.
        ///
        /// `Machine.FixedUpdate` runs this once per fixed step, and in order: every
        /// block's `SendEmulationUpdateBlock` first, so each emulator and each
        /// variable has raised its count, then `EmulationUpdateBlock` on everything
        /// registered for it -- which for a modded block is always, since
        /// `BlockPrefabCreator.SetupBehaviour` sets `RegisterEmulationUpdate`
        /// unconditionally. `ModBlockBehaviourHandler` forwards it to here.
        ///
        /// The cadence is the point. `MKey.CheckEmulation` latches its previous
        /// state against `Time.fixedTime`, so an edge exists for exactly one fixed
        /// step. `SimulateUpdateAlways` is an Update: it can run several times in
        /// one fixed step and see the same press repeatedly, or none at all and
        /// miss it. Reading here and latching for the next Update is what makes a
        /// variable-driven block behave like a played one.
        /// </summary>
        public override void KeyEmulationUpdate()
        {
            if (PlayKey.EmulationPressed())
            {
                emulatedPressPending = true;
            }
            emulatedDown = PlayKey.EmulationHeld(true);
        }

        /// <summary>Hands the block's current settings to the audio thread.</summary>
        private void PushSettings()
        {
            wantModel = ModelMenu.Value;
            // Braids counts pitch in 1/128ths of a semitone; FINE is in cents, which
            // is 1/100th of the same semitone.
            wantPitch = Mathf.RoundToInt(PitchSlider.Value * 128f
                                         + FineSlider.Value * 1.28f);
            wantTimbre = Mathf.RoundToInt(Timbre.Value * 32767f);
            wantColour = Mathf.RoundToInt(Colour.Value * 32767f);
            wantVolume = VolumeSlider.Value;
            attackPerSample = RampRate(AttackSlider.Value);
            releasePerSample = RampRate(ReleaseSlider.Value);
        }

        /// <summary>
        /// How far the gate moves per sample to cross its whole travel in
        /// <paramref name="seconds"/>. Zero still takes a couple of milliseconds: a
        /// gate that switches is a click, which is what a ramp is here to avoid.
        /// </summary>
        private float RampRate(float seconds)
        {
            float shortest = 0.002f;
            if (seconds < shortest)
            {
                seconds = shortest;
            }
            return 1f / (seconds * rate);
        }

        // ---- the audio thread ---------------------------------------------------

        /// <summary>
        /// Runs on Unity's audio thread, not the game thread: nothing here may touch
        /// the mapper, the transform, or anything else Unity guards.
        ///
        /// A filter, so the samples are produced where they are about to be played and
        /// the note starts when the key does. Braids renders int16, and the tables
        /// were built for Unity's rate, so a block of samples comes out at the right
        /// pitch with no resampling.
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (oscillator == null || channels <= 0)
            {
                return;
            }

            int frames = data.Length / channels;
            if (block == null || block.Length < frames)
            {
                block = new short[frames];
            }

            oscillator.SetModel(wantModel);
            oscillator.SetPitch((short)wantPitch);
            oscillator.SetTimbre((short)wantTimbre);
            oscillator.SetColour((short)wantColour);

            bool open = gateOpen || previewing;
            float target = open ? wantVolume * wantMaster : 0f;
            if (!open && level <= 0.0001f)
            {
                // The stream is the silent clip's, so leaving it is silence.
                playing = false;
                level = 0f;
                return;
            }
            playing = true;

            oscillator.Render(null, block, frames);

            // The placement is a frame old and moves in steps; slide onto it across
            // the block, or a turning camera is heard as a staircase.
            float leftTo = wantLeft;
            float rightTo = wantRight;
            float left = heldLeft;
            float right = heldRight;
            float leftStep = (leftTo - left) / frames;
            float rightStep = (rightTo - right) / frames;

            float step = target > level ? attackPerSample : releasePerSample;
            int write = scopeWrite;
            for (int i = 0; i < frames; i++)
            {
                if (level < target)
                {
                    level += step;
                    if (level > target) { level = target; }
                }
                else if (level > target)
                {
                    level -= step;
                    if (level < target) { level = target; }
                }

                // The DC blocker stands in for the module's output capacitor, and runs
                // whether the gate is open or not: it is the offset it removes that
                // would otherwise step the speaker as the gate moves.
                float s = blocker.Process(block[i] * (1f / 32768f)) * level;
                if (s > 1f) { s = 1f; }
                else if (s < -1f) { s = -1f; }

                scope[write] = s;
                write = (write + 1) & (ScopeSize - 1);

                left += leftStep;
                right += rightStep;

                int at = i * channels;
                float both = s * (left + right) * 0.5f;
                if (channels == 1)
                {
                    data[at] = both;
                }
                else
                {
                    data[at] = s * left;
                    data[at + 1] = s * right;
                    for (int c = 2; c < channels; c++)
                    {
                        data[at + c] = both;
                    }
                }
            }
            scopeWrite = write;
            heldLeft = leftTo;
            heldRight = rightTo;
        }
    }
}
