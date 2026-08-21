# Changelog

## Unreleased

First cut. A Synth Block that renders Braids' macro-oscillator live.

**Working**

- **Braids' sixteen analog-family models**, ported from `macro_oscillator.cc`:
  CSaw, Morph, Saw square, Sine triangle, Buzz, Square sub, Saw sub, Square sync,
  Saw sync, the four Triples, Triple ring mod, Saw swarm and Saw comb. All
  sixteen render sample for sample identically to Braids' own C++ —
  `./tools/compare-reference.sh` builds the original and checks it.
- **All nine of `analog_oscillator.cc`'s waveforms**, offered directly as well:
  saw, variable saw, square, triangle, sine, the two wavefolders and BUZZ.
- Braids' control scheme: a model, a pitch, and TIMBRE and COLOR, plus a fine
  tuning and an attack and release on the gate.
- **A UI Factory panel** — the models by name, what the two controls do in the one
  you picked, a live trace of the block's output, and a LISTEN button that sounds
  the block while the machine is still being built. A soft dependency: without UI
  Factory the block uses Besiege's own mapper and everything still works.
- **Every table computed at startup, none shipped.** Braids generates twenty-odd
  arrays offline through `resources.py`; each one turns out to have a closed form,
  and each closed form reproduces the shipped array exactly (within the 2 counts
  of dither on `wav_sine` and the comb tables). Everything that depends on a
  sample rate is built for Unity's, so nothing is resampled.
- A DC blocker standing in for the module's output capacitor, without which the
  models that carry an offset step the speaker as the gate moves.
- The block is heard from where it is: the sound is placed in 3D and falls away
  with distance, with Doppler off so a fast machine does not detune a held note.
- NOTE snaps to whole semitones on the panel, since a note a quarter-semitone
  sharp cannot be played in a tune -- FINE is what the in-between is for.

- **The panel is where the block is set up.** ATTACK, RELEASE and a new RANGE join
  the dials, and everything but the key and the toggle is hidden from Besiege's
  block mapper — which has no room to say what TIMBRE means in the model you
  picked, and that is the whole difficulty of a macro-oscillator. They are still
  mapper settings and still saved with the machine; only their display is off. This
  makes UI Factory a hard dependency rather than a soft one.
- **RANGE**, in metres: the radius the block is at full volume within, with the
  1/distance falloff scaled to match, so one dial covers both how loud a block is
  and how far away it can still be heard.

**Fixed along the way**

- The block was heard at the same volume from everywhere, dead centre, however the
  camera moved -- in a simulation and under LISTEN alike. The oscillator writes its
  samples from `OnAudioFilterRead`, and a filter added that way runs *after* the
  AudioSource's 3D stage: the panner and the distance rolloff had already been
  applied to the one sample of silence the source plays, and the filter then
  overwrote the result. Feeding the samples in earlier, through a streaming clip,
  does get them panned -- but a streamed clip is read well ahead of being heard, so
  the note no longer starts when the key does. The block places itself instead: the
  source is 2D, and the filter applies a distance and pan gain worked out each frame
  from where the block stands relative to the listener, slid across the block so a
  turning camera is not heard as a staircase.
- After a simulation, LISTEN made no sound and could not be made to. The block was
  relying on three things that turn out not to hold. Besiege runs a simulation on a
  *clone* of the machine, so `OnSimulateStart` and `OnSimulateStop` never reach the
  block the panel edits -- the code that cleared the preview flag "when the run
  started" was running on a different object. That block's own `IsSimulating` is
  false even mid-run, because `BasicInfo::UpdateSimState` answers false for anything
  flagged `isBuildBlock`. And `BuildingUpdate`, where the preview did its per-frame
  work, is declared on `ModBlockBehaviour` and called from nowhere in the game. So
  the run hid the build machine, Unity stopped the deactivated object's AudioSource,
  and the block came back with its preview flag still set and its source stopped:
  the panel read the flag and drew LISTENING, nothing sounded, and pressing the
  button turned the preview off -- the one thing that could not help. There is now
  one rule, in Unity's own `Update`: the source plays while a run is on or the panel
  is auditioning, and is stopped otherwise. It is re-checked every frame instead of
  being switched from callbacks that may never arrive, and it reads the global
  `StatMaster.levelSimulating`, which is the only simulation signal that reaches
  this object.
- The panel kept drawing the last waveform it saw after a simulation ended. Nothing
  cleared the flag the scope reads once the source stopped and the audio callback
  went quiet.
- Settings changed on the panel were heard by LISTEN and ignored by a simulation.
  A mapper setting is stored twice -- a live value and the value a block is loaded
  from -- and assigning `MapperType.Value` writes only the first, while a
  simulation is built from the second. The panel now commits through
  `BlockMapper.OnEditField`, as Besiege's own widgets do.
- A block auditioned with LISTEN kept sounding through the whole simulation and
  ignored its key, because nothing cleared the preview flag when the run started.
- The panel's window was a guessed height and its last two rows hung below the
  frame. It is now sized from what was laid out.

- The oscillator did not interpolate the phase increment across a block, which
  every shape in `analog_oscillator.cc` but BUZZ does. At Braids' 24-sample block
  it is a detail; at Unity's block size it is the difference between a pitch that
  glides and one that arrives in jumps. Found by rendering against the original.
- `digital_oscillator.cc` has two different note ceilings and uses them in two
  different places. Reading the delay against the wrong one mistuned Saw comb at
  high TIMBRE.

**Not yet**

- The rest of `digital_oscillator.cc`: FM, feedback FM, the physical models, the
  noise models, the wavetables, the vowel synthesis. That file is 80 KB and its
  own project. The three shapes that need nothing shipped -- ring mod, swarm and
  comb -- are already here.
- Braids' quantizer, its AD envelope and its META mode.
- A block model of its own. The mesh and texture are Sound Blocks' for now.

**If you built a machine against an earlier working copy**, the Shape setting is
now a Model setting over a longer list in a different order, so a saved synth
block will come back playing something else. Nothing has been released, so this
should affect nobody.
