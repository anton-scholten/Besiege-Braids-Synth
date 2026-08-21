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
    /// Braids has three controls and this keeps their meaning: a MODEL, a coarse
    /// pitch, and TIMBRE and COLOR, which mean whatever the chosen model decides
    /// they mean. What the module does *not* have is Braids' front panel, so the
    /// note comes from a slider and a key gates it.
    ///
    /// Everything the mapper offers is also on the UI Factory panel, which is a soft
    /// dependency -- see <see cref="BraidsPanel"/>. The mapper is what the block
    /// saves through either way.
    /// </summary>
    public class BraidsBehaviour : BlockModuleBehaviour<SynthModule>
    {
        /// <summary>How many samples the panel's scope can draw. A power of two.</summary>
        public const int ScopeSize = 1024;

        private MKey PlayKey;
        private MMenu ModelMenu;
        private MSlider PitchSlider;
        private MSlider FineSlider;
        private MSlider Timbre;
        private MSlider Colour;
        private MSlider VolumeSlider;
        private MSlider AttackSlider;
        private MSlider ReleaseSlider;
        private MToggle PushToggle;

        /// <summary>
        /// The clip every synth block's AudioSource plays.
        ///
        /// It is never heard -- OnAudioFilterRead overwrites the stream -- but the
        /// source has to have a clip and be playing or Unity does not run the filter
        /// chain at all. One sample of silence, looped, is the cheapest way to keep
        /// it running, and one clip is shared: a clip per block is a Unity object per
        /// block that nothing destroys when the block goes.
        /// </summary>
        private static AudioClip silence;

        private AudioSource source;
        private MacroOscillator oscillator;
        private DcBlocker blocker;
        private int rate;

        // Written by the game thread, read by the audio thread. Plain fields of
        // primitive type: torn reads would cost one block of slightly wrong
        // timbre, which is not worth a lock on the audio callback.
        private volatile bool gateOpen;
        private volatile int wantModel;
        private volatile int wantPitch;
        private volatile int wantTimbre;
        private volatile int wantColour;
        private volatile float wantVolume;
        private volatile float attackPerSample;
        private volatile float releasePerSample;

        private short[] block;
        private float level;
        private bool playing;

        // The scope's ring buffer. Filled on the audio thread and read on the game
        // thread without a lock: the worst a torn read costs is one frame of a
        // picture, and the picture is redrawn several times a second.
        private readonly float[] scope = new float[ScopeSize];
        private volatile int scopeWrite;

        /// <summary>
        /// Set by the panel to make the block sound while the machine is being
        /// built, so a model can be chosen by ear rather than by name.
        /// </summary>
        private volatile bool previewing;

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
            AttackSlider = AddSlider("Attack", "AttackKey", 0.01f, 0f, 2f);
            ReleaseSlider = AddSlider("Release", "ReleaseKey", 0.05f, 0f, 4f);
            PushToggle = AddToggle("Toggle", "ToggleKey", false);

            rate = AudioSettings.outputSampleRate;
            if (rate <= 0)
            {
                rate = BraidsResources.NativeSampleRate;
            }
            // Builds every table the oscillator will read, on the game thread, so
            // the audio thread never allocates one under itself.
            BraidsResources.Prepare(rate);

            source = GetComponent<AudioSource>();
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
            }
            source.clip = Silence(rate);
            source.loop = true;
            source.playOnAwake = false;

            // The block is a thing in the world, so it is heard from where it is:
            // panned as the camera moves round the machine, and quieter from further
            // off. Unity spatialises the source's output after this component's
            // filter has produced it, so the callback goes on writing one mono
            // signal into every channel and the engine does the placing.
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            // Full volume anywhere on a machine of ordinary size, then falling away.
            source.minDistance = 8f;
            source.maxDistance = 500f;
            // No Doppler. Besiege machines reach speeds that would bend a held note
            // by several semitones, and a synth block is played for its pitch.
            source.dopplerLevel = 0f;

            oscillator = new MacroOscillator(rate);
            blocker = new DcBlocker(rate);
            // Sized for the largest DSP buffer Unity offers, so the audio thread
            // never has to grow it -- an allocation there is a collection under a
            // running note. The check in the callback stays as a backstop.
            block = new short[4096];
            PushSettings();
        }

        private static AudioClip Silence(int rate)
        {
            if (silence == null)
            {
                silence = AudioClip.Create("BraidsSilence", 1, 1, rate, false);
                // Kept out of the scene and out of UnloadUnusedAssets' reach, since
                // the only thing referencing it is this static field.
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

        /// <summary>True while the block is making a sound.</summary>
        public bool IsPlaying { get { return playing; } }

        /// <summary>
        /// Makes the block sound outside a simulation, for auditioning a model while
        /// the machine is being built. Turning it off stops the source rather than
        /// leaving a silent filter chain running on every synth block in the machine.
        /// </summary>
        public void SetPreview(bool on)
        {
            // Never during a simulation: the key is what opens the gate there.
            if (on && IsSimulating)
            {
                return;
            }
            if (previewing == on)
            {
                return;
            }
            previewing = on;
            if (IsSimulating || source == null)
            {
                return;
            }
            if (on)
            {
                level = 0f;
                blocker.Reset();
                PushSettings();
                source.Play();
            }
            else
            {
                source.Stop();
                playing = false;
            }
        }

        public bool IsPreviewing { get { return previewing; } }

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

        public override void OnSimulateStart()
        {
            // The simulation owns the gate. Preview is a build-mode convenience, and
            // leaving it set here is a block that drones through the whole run and
            // ignores its key -- which is what happens to any block auditioned with
            // the panel's LISTEN and then simulated, since nothing else is
            // guaranteed to clear it first.
            previewing = false;
            gateOpen = false;
            playing = false;
            level = 0f;
            oscillator.Init();
            blocker.Reset();
            PushSettings();
            source.Play();
        }

        public override void OnSimulateStop()
        {
            gateOpen = false;
            source.Stop();
        }

        /// <summary>Hands the block's current settings to the audio thread.</summary>
        private void PushSettings()
        {
            wantModel = ModelMenu.Value;
            // Braids counts pitch in 1/128ths of a semitone; the fine control is in
            // cents, which is 1/100th of the same semitone.
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
        /// <paramref name="seconds"/>. Zero seconds still takes a couple of
        /// milliseconds: a gate that switches is a click, which is the one thing a
        /// ramp is here to avoid.
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

        public override void SimulateUpdateAlways()
        {
            PushSettings();

            if (PlayKey.IsPressed)
            {
                gateOpen = PushToggle.IsActive ? !gateOpen : true;
            }
            if (!PushToggle.IsActive && PlayKey.IsReleased)
            {
                gateOpen = false;
            }
        }

        /// <summary>
        /// Keeps the preview following the panel while the machine is being built.
        /// Nothing here runs during a simulation.
        /// </summary>
        public override void BuildingUpdate()
        {
            if (previewing)
            {
                PushSettings();
            }
        }

        /// <summary>
        /// Runs on Unity's audio thread, not the game thread: nothing here may touch
        /// the mapper, the transform, or anything else Unity guards.
        ///
        /// Braids renders int16 at its own rate; the tables were built for Unity's
        /// rate, so a block of samples comes straight out at the right pitch with no
        /// resampling. The gate is ramped rather than switched, because cutting a
        /// running oscillator dead is a click.
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
            float target = open ? wantVolume : 0f;
            if (!open && level <= 0.0001f)
            {
                playing = false;
                level = 0f;
                return;
            }
            playing = true;

            oscillator.Render(null, block, frames);

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

                // The DC blocker stands in for the module's output capacitor, and
                // has to run whether the gate is open or not: it is the offset it
                // removes that would otherwise step the speaker as the gate moves.
                float s = blocker.Process(block[i] * (1f / 32768f)) * level;
                if (s > 1f) { s = 1f; }
                else if (s < -1f) { s = -1f; }

                scope[write] = s;
                write = (write + 1) & (ScopeSize - 1);

                for (int c = 0; c < channels; c++)
                {
                    data[i * channels + c] = s;
                }
            }
            scopeWrite = write;
        }
    }
}
