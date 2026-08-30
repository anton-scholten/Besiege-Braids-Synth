# Changelog

## Unreleased

First cut. A Synth Block that renders Braids' macro-oscillator live.

**Fixed**

- **Besiege's master volume slider did not reach the synth blocks.** The
  per-category sliders do -- BLOCKS and SFX are exposed parameters on an
  `AudioMixer`, and a block's `AudioSource` is routed through a mixer group -- but
  the master slider sets `AudioListener.volume`, and Unity does not apply that to
  audio coming out of a mixer. The block applies it itself now, read on the game
  thread in `Place` and folded into the envelope's target so a slider move slews in
  rather than stepping, and only where the game does not: a source with no mixer
  group is still scaled by the listener, and applying it there too would work the
  slider twice. The same hole is in any mod that gives a block an `AudioSource` --
  fixed alongside this in Orchestra and Sound Blocks.

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
- **Every value on the panel can be typed**, not only dragged: click the number and
  write over it. Units are read back the way they are written, so `20 ms`, `1.50 s`,
  `50%` and `8 m` all go back in as themselves, and NOTE takes either a name — `C4`,
  `F#2`, `Bb3` — or a MIDI number. Besiege's keyboard is held off while a field has
  focus, or the letters would drive the camera and fire the block keys. A click
  selects what is in the box and a second click places the caret, as Besiege's own
  value boxes do.
- **ATTACK, RELEASE and RANGE reach further than their sliders do.** The travel
  stays where it is worth dragging — 2 s, 4 s, 100 m — and a longer swell or a
  wider carry can be typed instead, up to 600 s and 100000 m. The limit belongs to the
  setting rather than the panel, so what is stored is always inside the bounds the
  setting declares; the handle simply rests against its stop.
- **The note lights up while the block sounds**, in a colour off a slider on
  Besiege's own mapper — the one setting worth reaching without opening the block
  up, and the only one left there beside the key and the toggle. It answers a key,
  an automation variable and the panel's LISTEN alike, since it watches the gate
  rather than what opened it. The block wears its own two-by-two texture to do it:
  the note takes its colour from a single texel, so one texel per corner lights the
  note without touching the cage, and lighting one synth block does not light every
  other one on the machine.
- **RANGE**, in metres: the radius the block is at full volume within, with the
  1/distance falloff scaled to match, so one dial covers both how loud a block is
  and how far away it can still be heard.

- **The gate takes a variable as well as a key.** PLAY reads the emulated state
  beside the keyboard, so Besiege's variables drive the block the way they drive
  any other. Variables are an `MKey` feature and nothing else — sliders, toggles and
  menus have no part in them — so hiding the rest of the settings from the mapper
  costs nothing here.

- **No skin picker.** The block's mesh and texture are generated to be looked at,
  and one texel of that texture is the note's colour, so a skin would replace both
  and take the lit note with it.
- **The model is a dropdown**, not twenty-three buttons. The list was most of the
  panel's height; a dropdown is one row that opens the whole list when asked, and
  scrolls it — which brings the panel itself inside the room under the mapper and
  takes its scroll bar away. An arrow either side steps to the next model without
  opening it. The control is Special Effects' own, hand-built rather than taken
  from UI Factory's drop-down prefab, whose list spills past the panel and paints
  what should have been scrolled away over the world.
- **The panel docks under Besiege's mapper**, measured against it every frame and
  taking its width from it, so the two read as one window with a seam. It scrolls,
  and LISTEN sits under the trace, which is the thing it makes move.
- **A block of its own to look at.** The model is Special Effects' text block cage
  with its lettering stripped out, painted a flat grey, and standing inside it is a
  pair of beamed semiquavers in black, filling the block. Mesh and texture are both
  generated, by
  `./tools/make-block-mesh.py`: the texture is a 714-byte square of grey with one
  black corner, which is where the note's vertices look, so the note needs no
  material of its own and stays black whatever colour the machine is painted. The
  note is part of the mesh rather than added at load, which is the only way it can
  reach the toolbar icon — Besiege draws that from the mesh. The cage is emitted
  two-sided as well: it is an outer skin with no inner walls, so without that its
  far pillars are culled away and the block reads hollow from the inside. The note is built at load rather than shipped — two tilted ellipses,
  two stems and two slanted beams, extruded — so the mod still carries no geometry
  it did not compute, and the shape is a handful of constants to adjust rather than
  a mesh to re-export.

**Fixed along the way**

- The panel was invisible the first time a block was opened. `BuildWindow` leaves
  what it builds switched off, and the first open takes its width from the mapper
  and rebuilds — after the `Show` that would have switched it on had already run.

- The block disappeared from the toolbar entirely. A comment in `SynthBlock.xml`
  had a dash written as two hyphens, which XML does not allow inside a comment, so
  the file stopped parsing and Besiege dropped the block without a word about it.
  `./tools/build.sh` now parses every XML the mod ships and refuses the build
  rather than letting it look like it worked.

- RELEASE was not heard under LISTEN, though it was in a run. The source was kept
  up for exactly as long as the gate was open, so turning LISTEN off stopped it in
  the same frame and the audio callback -- the thing that plays the release out --
  stopped with it. A run keeps its source up for the whole run, which is why the
  identical ramp was heard there. The source now outlives the gate: it runs on
  until the voice reports that it has reached silence, under LISTEN and in a run
  alike, and the panel's dials and the block's placement keep following it through
  the tail.
- LISTEN did not give the oscillator the standing start a run does. `OnSimulateStart`
  calls `Init` and starting a preview did not, so a model auditioned after another
  one carried whatever state it was left in -- audible on the models that have any,
  which is the swarm and the comb.

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
- Nothing outstanding on the block's look.

**If you built a machine against an earlier working copy**, the Shape setting is
now a Model setting over a longer list in a different order, so a saved synth
block will come back playing something else. Nothing has been released, so this
should affect nobody.
