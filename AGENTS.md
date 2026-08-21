# Working notes

A Besiege block that synthesises audio, by porting Mutable Instruments' Braids.

## Layout

```
BraidsSynth/            the folder Besiege loads, and what goes to the Workshop
  Mod.xml               manifest; <ID> is written by the game on first load, keep it
  SynthBlock.xml        the block
  BraidsSynthScripts/   sources; the built BraidsSynth.dll sits beside them
  Resources/            mesh, texture, icon
tools/                  build, test and install, on Besiege's own compiler
```

The mod is **Besiege-Braids-Synth** in the mods menu; the folder, the assembly and
the namespace are all `BraidsSynth`. The one name that must never change is
`<ID>` in `Mod.xml` -- saved machines are keyed on it.

The module type is `SynthModule`, not `BraidsSynth`, because that is the
namespace's own name and a type sharing its namespace's name is the kind of
self-reference this compiler handles badly.

`.git` must stay *outside* the folder Besiege copies when publishing — its
read-only objects jam the Workshop uploader. Hence the subfolder.

## Build and test

```
./tools/build.sh              build the mod's assembly
./tools/verify-build.sh       compile only, leaving the shipped one alone
./tools/install.sh            symlink into Besiege_Data/Mods
./tools/run-tests.sh          render every model and check it is a signal
./tools/compare-reference.sh  render every model against Braids' own C++
```

There is no .NET toolchain: the build drives Besiege's own `mcs.dll` through
`libmono.so`. `install.sh` symlinks by default, so a rebuild is picked up by the
next start.

**That compiler is C# 4, and old.** No interpolated strings, no `?.`, no
`nameof`, no expression-bodied members. Most importantly: **any `enum`
declaration segfaults it**. Braids is enum-heavy, so every shape constant is a
`const int` instead. A SIGSEGV from this compiler means "there is an error
somewhere in these files", not necessarily "there is an enum"; bisect by
commenting out the newest block.

`build.sh` also parses every XML the mod ships, because **an XML comment may not
contain two hyphens in a row** and Besiege says nothing when one does: the block
is just missing from the toolbar, which looks like a mod that failed to load, a
module that threw, or a mesh it could not find. A dash written in prose inside a
comment is all it takes. `tools/tests/XmlCheck.cs` is what stops that reaching the
game.

`build.sh` also runs the blacklist check. The mod loader refuses assemblies that
reference forbidden namespaces, and it scans field types, method locals and IL
operands — but never custom attributes, which is why `[XmlRoot]` is fine.

## The port

Braids is MIT, Émilie Gillet. The notice ships in
`BraidsSynth/BraidsSynthScripts/BRAIDS-LICENSE.txt` and must stay there.

```
BraidsResources.cs    every lookup table, computed at startup
AnalogOscillator.cs   analog_oscillator.cc: nine waveforms, BLEP and all
DigitalOscillator.cs  the three shapes of digital_oscillator.cc that stand alone
MacroOscillator.cs    macro_oscillator.cc: the sixteen models
DcBlocker.cs          the output capacitor the module has and Unity does not
```

**Keep the fixed point.** Phase is a `uint` accumulator, samples are `short`, and
the BLEP corrections are integer arithmetic. Rewriting any of it in float rounds
differently and the character goes. C#'s `int`/`uint` semantics match C's
`int32_t`/`uint32_t` closely enough to translate line for line; watch the places
Braids relies on `int16_t` truncation, e.g. the triangle's `+= 32768` wrap, which
needs an explicit `unchecked((short)…)` here.

### Every table has a closed form

This was the thing that looked hardest and was not. `resources.py` generates
`resources.cc` offline, and it was assumed the shapes reading those arrays could
not be ported without shipping them. In fact every generator in that script is a
formula — the waveshapers are `tanh` and `arctan` curves, the comb wavetables are
Dirichlet kernels, the filter and pitch tables are arithmetic — and each one
reproduces the shipped array **exactly**. The two exceptions are `wav_sine` and
the fifteen comb tables, over which `resources.py` runs a second-order dither;
those land within 2 of 32766, which is inaudible.

So nothing is embedded, and everything that depends on a sample rate is built for
Unity's rather than Braids' 96 kHz — which removes the resampling stage entirely.

### Render against the original, always

`./tools/compare-reference.sh` fetches Braids, builds it with gcc, and renders
every model through both it and this port. It found two real bugs that no amount
of reading found:

1. **Every shape but BUZZ interpolates the phase increment across the block**, and
   the first version of this port dropped it from all of them. At Braids' 24
   samples it is a detail. At Unity's block size it is a pitch that jumps.
2. **`digital_oscillator.cc` has two note ceilings** — `kHighestNote` is
   `140 * 128` there and `128 * 128` in `analog_oscillator.cc` — and it uses
   `kPitchTableStart` for the increment while using `kHighestNote - kOctave` for
   the delay. Those come to the same number by different routes, and using the
   wrong one mistuned SAW COMB.

Two patches make the comparison possible, and neither changes what Braids
computes: `AnalogOscillator::previous_shape_` and the comb's delay line are never
initialised, so whether the first block clobbers the pitch, and what the comb
reads before it has filled, both depend on uninitialised memory. Give them
defined values and the two agree on every sample.

### DC is not a bug

Several models are meant to sit far off zero. A square at full TIMBRE is a 1%
pulse; MORPH with both controls up is a saturated constant, verified bit-exact
against the original. On the module the output stage is capacitor-coupled and
none of it escapes. `DcBlocker` is that capacitor. Do not "fix" the oscillator.

## Audio

The AudioSource is **3D** (`spatialBlend = 1`). Unity spatialises a source's
output *after* the `OnAudioFilterRead` components on it have run, so the callback
writes one mono signal into every channel and the engine does the panning and the
distance attenuation. `dopplerLevel` is 0 on purpose: Besiege machines reach
speeds that would bend a held note by several semitones.

`OnAudioFilterRead` runs on Unity's **audio thread**. Nothing in it may touch the
mapper, the transform, or any other Unity object. The behaviour therefore hands
settings over through plain `volatile` fields, and accepts that a torn read costs
at most one block of slightly wrong timbre. Same for the scope's ring buffer, in
the other direction: a torn read there costs one frame of a picture.

Every table is built on the game thread, in `SafeAwake`, so that the audio thread
never finds itself allocating one.

The AudioSource needs a clip and needs to be playing, or Unity never runs the
filter chain at all — hence the one-sample silent clip.

The gate is ramped rather than switched. Cutting a running oscillator dead is a
click, and Braids has no envelope of its own in this path.

## The block

A block that does not appear in the toolbar, or appears and will not attach, is
usually `SynthBlock.xml` missing something rather than anything in the code.
`<BasePoint>` is what a block is placed *on*; without it there is nothing to
attach. `<Colliders>` is what the cursor and cannonballs find. `<AddingPoints>`
are the faces other blocks attach *to*. The mesh is Sound Blocks' cube, so its
`<Mesh>` offset and three quarter-turns are copied from that mod's own block XML
and only make sense together with that mesh.

## The panel

UI Factory is a **soft** dependency and has to stay one. Every mention of
`Besiege.UI` in this mod is in `UIF.cs`, because a type that cannot be resolved
fails as the method mentioning it is compiled — so the try/catch has to be in a
*caller*, not inside the method that names the type. `UIF.Available` is that one
guarded call site, and the rest of the panel is only ever reached through it.

`build.sh` still needs `Besiege.UI.dll` on the reference path to compile.

`BlockMapper.onMapperOpen` / `onMapperClose` are plain static `Action`s, so
whatever subscribes has to outlive scene loads — hence the `DontDestroyOnLoad`
host in `Mod.cs` — and has to unsubscribe itself.

### A mapper setting is stored twice, and `Value` only writes one of them

`MSlider` and `MMenu` each keep a `_value` and a `_loadValue`. Reading the IL:

```
set_Value    → _value = x; InvokeValueChanged()
SetValue(x)  → _value = x                       (no event)
ApplyValue() → _loadValue = _value; InvokeValueChanged()
```

**A simulation is built from the load value.** So a panel that assigns
`slider.Value = x` and stops there is heard by anything reading the live value —
the block's own `PushSettings`, and therefore the build-mode preview — and
completely ignored once the machine runs. It looks like the audio code being
wrong and is not.

Besiege's own widgets go through **`BlockMapper.OnEditField(holder, type)`**,
which is static despite reading like an instance method. It serialises the
changed type, deserialises it onto every block in the selection, calls
`ApplyValue`, reloads the holder, and files an `UndoActionField`. That is the
path to copy. It is not free — an undo entry per call — so a drag writes `Value`
every frame for the sound and commits once when the button comes up.

`BlockMapper.Close()` is static too. `Refresh()` is not.

Besiege's own buttons are colliders answering `OnMouseOver`, which is raycast
from the cameras and knows nothing about uGUI. A canvas over one hides it without
stopping it; `ClickShield` zeroes every camera's `eventMask` while the pointer is
inside the panel. Gather the cameras every frame, and release from `OnDisable`.

The sibling repo `Besiege-Git-view` carries much longer notes on UI Factory in
`docs/MODDING-NOTES.md` — what the nineteen prefabs are called, why a UI Factory
graphic cannot be tinted, and what Besiege's palette is. Read them before adding
to the panel.

**Variables reach a block through its keys and nowhere else.** `MKey` carries the
whole feature — `Emulating`, `EmulationPressed`, `EmulationHeld(includePressed)`,
`EmulationReleased` — while `MSlider`, `MToggle`, `MMenu` and `MapperType` have
nothing variable-related on them at all. So read a key as
`IsPressed || EmulationPressed()` and `IsHeld || EmulationHeld(true)`, the way
`Modding.Modules.Official.ShootingModuleBehaviour` does, and a variable drives the
block for free. The edge methods sit on a snapshot `MKey` advances once per fixed
step, so polling them often is safe but leaving one uncalled lets it go stale —
read both every frame rather than only the one the current mode needs.

**Typing into the panel needs Besiege's keyboard held off.** Its own key handler,
the camera orbit and both selection tools stand down for `StatMaster.inMenu`, and
`StatMaster.SetInMenu(bool)` is public — so raise it while a field has focus and
drop it after, or the letters being typed drive the camera and fire block keys.
Besiege *counts* it, so it has to be raised and dropped exactly once: drop it when
the panel closes too, or the game is left believing a menu is still open.
(`StatMaster.textFieldSelected` looks like the flag for this and is not — only
`ScaleOnMouseOver` and `KeySelectorExtender` read it.) UI Factory has no input
prefab, so a typable value is its `Text` with an `InputField` built round it, the
Text a *child* of the field — and from then on the field drives the Text, so write
values through `InputField.text`, never to the label.

Watch the name: **Besiege has its own `Slider` in the global namespace**, and it
is the one an unqualified `Slider` binds to. Write `UnityEngine.UI.Slider` in
full. Same for `Scrollbar`, `LOD` and `Particle`, and fully qualify every
`Modding` type — the bundled mod.io SDK's global `ModIO` shadows `Modding.ModIO`.

## What Besiege does not tell a block

Four things about the block lifecycle, all found by disassembling
`Assembly-CSharp`, all of which cost a wrong fix first.

**A simulation runs on a clone.** `Machine::StartPhysics` builds a
`simulationClone` and `DestroySimMachine` tears it down. `OnSimulateStart`,
`OnSimulateStop` and `SimulateUpdateAlways` arrive on the clone's blocks — never
on the block the block mapper and the panel are editing. Anything a *building*
block has to know about a run has to come from somewhere else.

**`IsSimulating` is false on a building block, even mid-run.** It reads
`handler.isSimulating`, which only `BasicInfo::UpdateSimState` writes, and that
answers `false` outright for anything flagged `isBuildBlock`. It is also computed
on `Awake`, `OnEnable` and a parent-machine change, and at no other time — so it
is a cached per-block value, not a live global. `StatMaster.levelSimulating` is
the global flag, public and static.

**`BuildingUpdate` is never called.** It is declared on `ModBlockBehaviour` and
there is not one call to it anywhere in `Assembly-CSharp`. Code put there does
not run. Use Unity's own `Update` — `ModBlockBehaviour` and
`BlockModuleBehaviour<T>` declare no `Update`, `OnEnable`, `OnDisable` or
`OnDestroy`, so all four are free to implement and hide nothing.

**The build machine is hidden for the run**, which deactivates the object and
stops any AudioSource on it without telling the component. State a block was
holding across that — a preview flag, a "still playing" flag — comes back stale.

The moral for the synth block: it does not try to be told, it reconciles. The
AudioSource is brought into line with the wanted state every frame from `Update`,
rather than being started and stopped from whichever callback happened to fire.

## Where the sound is placed

`OnAudioFilterRead` runs *after* the AudioSource's 3D stage, so a filter that
writes the whole buffer throws away everything the panner and the rolloff did --
which is why the block used to be heard at one volume, dead centre, from
anywhere. The obvious repair is to feed the samples in earlier, as a streaming
`AudioClip` with a PCM reader callback, and that does get them panned. It also
costs the stream's read-ahead: the callback runs well before those samples are
heard, so the gate opens into audio that was generated with it shut, and the note
arrives late. **Do not go back to it.**

So the source is 2D and the block places itself: `Place` works out a distance and
pan gain each frame on the game thread, and the filter slides onto it across the
block. Keep the placement on the game thread -- a transform must not be read from
the audio thread -- and keep re-finding the `AudioListener`, because Besiege swaps
cameras between building and running and the held one goes stale rather than null.

## Next

`digital_oscillator.cc` is what is left: FM, the physical models, the noise
models, the wavetables, the vowel synthesis. 80 KB, and the wavetable models need
`data/waves.bin`, which is data rather than a formula and would have to ship.
Braids' quantizer, its AD envelope and META mode are all small and unported.

The block is Special Effects' text block cage (same author) with its lettering
dropped — `tools/` has no step for that; it was a one-off split of the OBJ's two
connected pieces, and the provenance is in the header of
`Resources/SynthBlock/SynthBlock.obj`. The note standing inside it is built at load
by `NoteMesh.cs`, on the same principle as the tables: a formula rather than an
asset. `NoteMesh.Size` is what fits it to the cage; the rest of the layout is the
constants above it.
