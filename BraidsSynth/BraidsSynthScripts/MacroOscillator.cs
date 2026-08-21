using System;

namespace BraidsSynth
{
    /// <summary>
    /// A port of Braids' `macro_oscillator.cc` (Emilie Gillet, MIT): the layer that
    /// turns one MODEL setting into a stack of oscillators and a way of mixing them.
    ///
    /// This is where Braids' character actually comes from. The analog oscillator
    /// underneath has six waveforms; the models here get sixteen sounds out of it by
    /// detuning three copies against each other, syncing one to another, folding,
    /// filtering and saturating. TIMBRE and COLOR mean something different in every
    /// one, which is the whole idea of the instrument.
    ///
    /// Models are `const int`, not an enum: Besiege's in-game compiler segfaults on
    /// any enum declaration.
    ///
    /// Ported are the models built from the analog oscillator, plus the three from
    /// `digital_oscillator.cc` that need nothing shipped -- ring mod, swarm and comb.
    /// The rest of that file (FM, the physical models, the noise models, the
    /// wavetables) is not here.
    /// </summary>
    public class MacroOscillator
    {
        // Braids' own model order, which is the order they appear on its display.
        public const int ModelCSaw = 0;
        public const int ModelMorph = 1;
        public const int ModelSawSquare = 2;
        public const int ModelSineTriangle = 3;
        public const int ModelBuzz = 4;
        public const int ModelSquareSub = 5;
        public const int ModelSawSub = 6;
        public const int ModelSquareSync = 7;
        public const int ModelSawSync = 8;
        public const int ModelTripleSaw = 9;
        public const int ModelTripleSquare = 10;
        public const int ModelTripleTriangle = 11;
        public const int ModelTripleSine = 12;
        public const int ModelTripleRingMod = 13;
        public const int ModelSawSwarm = 14;
        public const int ModelSawComb = 15;

        /// <summary>How many of the entries are Braids models rather than raw waveforms.</summary>
        public const int BraidsModelCount = 16;

        // The analog oscillator on its own, which Braids does not offer as a model
        // but which is the obvious thing to want from a block that makes a note.
        public const int ModelRawSaw = 16;
        public const int ModelRawVariableSaw = 17;
        public const int ModelRawSquare = 18;
        public const int ModelRawTriangle = 19;
        public const int ModelRawSine = 20;
        public const int ModelRawTriangleFold = 21;
        public const int ModelRawSineFold = 22;

        public const int ModelCount = 23;

        /// <summary>One semitone, in the 1/128ths of a semitone Braids counts pitch in.</summary>
        private const int Semi = 128;

        /// <summary>
        /// What the two detuning controls on the TRIPLE models sweep through: mostly
        /// whole semitones out to two octaves either way, with the useful intervals
        /// given a flat spot on the knob and a few cents of beating either side of
        /// unison in the middle.
        /// </summary>
        private static readonly int[] Intervals = new int[]
        {
            -24 * Semi, -24 * Semi, -24 * Semi + 4,
            -23 * Semi, -22 * Semi, -21 * Semi, -20 * Semi, -19 * Semi, -18 * Semi,
            -17 * Semi - 4, -17 * Semi,
            -16 * Semi, -15 * Semi, -14 * Semi, -13 * Semi,
            -12 * Semi - 4, -12 * Semi,
            -11 * Semi, -10 * Semi, -9 * Semi, -8 * Semi,
            -7 * Semi - 4, -7 * Semi,
            -6 * Semi, -5 * Semi, -4 * Semi, -3 * Semi, -2 * Semi, -1 * Semi,
            -24, -8, -4, 0, 4, 8, 24,
            1 * Semi, 2 * Semi, 3 * Semi, 4 * Semi, 5 * Semi, 6 * Semi,
            7 * Semi, 7 * Semi + 4,
            8 * Semi, 9 * Semi, 10 * Semi, 11 * Semi,
            12 * Semi, 12 * Semi + 4,
            13 * Semi, 14 * Semi, 15 * Semi, 16 * Semi,
            17 * Semi, 17 * Semi + 4,
            18 * Semi, 19 * Semi, 20 * Semi, 21 * Semi, 22 * Semi, 23 * Semi,
            24 * Semi - 4, 24 * Semi, 24 * Semi
        };

        private readonly AnalogOscillator[] analog;
        private readonly DigitalOscillator digital;
        private readonly int sampleRate;

        private short[] temp;
        private byte[] syncBuffer;

        private int model;
        private short pitch;
        private short timbre;
        private short colour;
        /// <summary>
        /// Last block's COLOR. Only COLOR is remembered: it is the mix control on
        /// every model that has one, and a mix stepped at a block edge clicks.
        /// TIMBRE goes into an oscillator's own parameter, which the oscillator
        /// sweeps for itself where it matters.
        /// </summary>
        private short previousColour;
        private int lpState;

        public MacroOscillator(int sampleRate)
        {
            this.sampleRate = sampleRate;
            BraidsResources.Prepare(sampleRate);
            analog = new AnalogOscillator[3];
            for (int i = 0; i < analog.Length; i++)
            {
                analog[i] = new AnalogOscillator(sampleRate);
            }
            digital = new DigitalOscillator(sampleRate);
            Init();
        }

        public void Init()
        {
            for (int i = 0; i < analog.Length; i++)
            {
                analog[i].Init();
            }
            digital.Init();
            lpState = 0;
            previousColour = 0;
        }

        public void SetModel(int value)
        {
            if (value != model)
            {
                // Braids re-strikes the digital oscillator on any model change, which
                // is what scatters the swarm's phases and clears the comb.
                digital.Strike();
            }
            model = value;
        }

        public void SetPitch(short value) { pitch = value; }
        public void SetTimbre(short value) { timbre = value; }
        public void SetColour(short value) { colour = value; }

        /// <summary>Braids' Strike: tells the models with state to start again.</summary>
        public void Strike()
        {
            digital.Strike();
        }

        /// <summary>
        /// Renders one block. <paramref name="size"/> may be anything -- Braids uses
        /// 24 samples, Unity hands over whatever its DSP buffer is -- so the scratch
        /// buffers grow to fit rather than being fixed at Braids' block size.
        /// </summary>
        public void Render(byte[] syncIn, short[] buffer, int size)
        {
            if (temp == null || temp.Length < size)
            {
                temp = new short[size];
                syncBuffer = new byte[size];
            }

            if (model >= BraidsModelCount)
            {
                RenderRaw(syncIn, buffer, size);
                return;
            }

            switch (model)
            {
                case ModelCSaw: RenderCSaw(syncIn, buffer, size); break;
                case ModelMorph: RenderMorph(syncIn, buffer, size); break;
                case ModelSawSquare: RenderSawSquare(syncIn, buffer, size); break;
                case ModelSineTriangle: RenderSineTriangle(syncIn, buffer, size); break;
                case ModelBuzz: RenderBuzz(syncIn, buffer, size); break;
                case ModelSquareSub:
                case ModelSawSub: RenderSub(syncIn, buffer, size); break;
                case ModelSquareSync:
                case ModelSawSync: RenderDualSync(syncIn, buffer, size); break;
                case ModelTripleSaw:
                case ModelTripleSquare:
                case ModelTripleTriangle:
                case ModelTripleSine: RenderTriple(syncIn, buffer, size); break;
                case ModelTripleRingMod:
                    digital.RenderTripleRingMod(syncIn, buffer, size, pitch, timbre, colour);
                    break;
                case ModelSawSwarm:
                    digital.RenderSawSwarm(syncIn, buffer, size, pitch, timbre, colour);
                    break;
                default: RenderSawComb(syncIn, buffer, size); break;
            }

            previousColour = colour;
        }

        // ---- parameter sweeps -------------------------------------------------

        /// <summary>
        /// How far a parameter moves per sample to reach this block's value by the
        /// end of it, in the 15-bit fraction Braids' interpolation macros use.
        /// Stepping a mix balance at a block edge is a click; sweeping it is not.
        /// </summary>
        private static int SweepStep(int size)
        {
            return 32767 / size;
        }

        private static int Swept(int start, int delta, int xfade)
        {
            return start + (delta * xfade >> 15);
        }

        // ---- models -----------------------------------------------------------

        /// <summary>
        /// CSAW: the analog CSaw with its level put back. TIMBRE is where in the
        /// cycle the wave drops out, COLOR how deep the notch goes -- and the deeper
        /// it goes the quieter the wave gets, so the gain follows COLOR back up.
        /// </summary>
        private void RenderCSaw(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetPitch(pitch);
            analog[0].SetShape(AnalogOscillator.ShapeCSaw);
            analog[0].SetParameter(timbre);
            analog[0].SetAuxParameter(colour);
            analog[0].Render(syncIn, buffer, null, size);

            int shift = (32767 - colour) >> 4;
            for (int i = 0; i < size; i++)
            {
                int s = buffer[i] + shift;
                buffer[i] = unchecked((short)((s * 13) >> 3));
            }
        }

        /// <summary>
        /// MORPH: triangle to saw to square to sine as TIMBRE sweeps, through a
        /// low-pass that tracks the note, with COLOR opening the filter and driving
        /// the result into a fuzz. The fuzz is pulled back at high pitches, where it
        /// would be all aliasing.
        /// </summary>
        private void RenderMorph(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetPitch(pitch);
            analog[1].SetPitch(pitch);

            int balance;
            if (timbre <= 10922)
            {
                analog[0].SetParameter(0);
                analog[1].SetParameter(0);
                analog[0].SetShape(AnalogOscillator.ShapeTriangle);
                analog[1].SetShape(AnalogOscillator.ShapeSaw);
                balance = timbre * 6;
            }
            else if (timbre <= 21845)
            {
                analog[0].SetParameter(0);
                analog[1].SetParameter(0);
                analog[0].SetShape(AnalogOscillator.ShapeSquare);
                analog[1].SetShape(AnalogOscillator.ShapeSaw);
                balance = 65535 - (timbre - 10923) * 6;
            }
            else
            {
                analog[0].SetParameter((short)((timbre - 21846) * 3));
                analog[1].SetParameter(0);
                analog[0].SetShape(AnalogOscillator.ShapeSquare);
                analog[1].SetShape(AnalogOscillator.ShapeSine);
                balance = 0;
            }

            analog[0].Render(syncIn, buffer, null, size);
            analog[1].Render(syncIn, temp, null, size);

            int cutoff = pitch - (colour >> 1) + 128 * 128;
            if (cutoff < 0) { cutoff = 0; }
            else if (cutoff > 32767) { cutoff = 32767; }
            int f = BraidsResources.Interpolate824(
                BraidsResources.SvfCutoff(sampleRate), (uint)cutoff << 17);

            int fuzz = colour << 1;
            if (pitch > (80 << 7))
            {
                fuzz -= (pitch - (80 << 7)) << 4;
                if (fuzz < 0) { fuzz = 0; }
            }

            int lp = lpState;
            for (int i = 0; i < size; i++)
            {
                short sample = BraidsResources.Mix(buffer[i], temp[i], balance);
                lp += (sample - lp) * f >> 15;
                lp = BraidsResources.Clip(lp);
                short fuzzed = BraidsResources.Interpolate88(
                    BraidsResources.ViolentOverdrive, lp + 32768);
                buffer[i] = BraidsResources.Mix(sample, fuzzed, fuzz);
            }
            lpState = lp;
        }

        /// <summary>
        /// SAW SQUARE: a variable saw and a square sharing a pulse width, mixed by
        /// COLOR. TIMBRE moves the corner in the saw and the width of the square at
        /// the same time, so the two stay related.
        /// </summary>
        private void RenderSawSquare(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetParameter(timbre);
            analog[1].SetParameter(timbre);
            analog[0].SetPitch(pitch);
            analog[1].SetPitch(pitch);
            analog[0].SetShape(AnalogOscillator.ShapeVariableSaw);
            analog[1].SetShape(AnalogOscillator.ShapeSquare);

            analog[0].Render(syncIn, buffer, null, size);
            analog[1].Render(syncIn, temp, null, size);

            int start = previousColour;
            int delta = colour - previousColour;
            int step = SweepStep(size);
            int xfade = 0;
            for (int i = 0; i < size; i++)
            {
                xfade += step;
                int balance = Swept(start, delta, xfade) << 1;
                // The square is the louder of the two by more than it should be.
                int attenuated = temp[i] * 148 >> 8;
                buffer[i] = BraidsResources.Mix(buffer[i], attenuated, balance);
            }
        }

        /// <summary>
        /// SINE TRIANGLE: two wavefolders in parallel, TIMBRE driving both into the
        /// fold and COLOR choosing between them. The drive is rolled off with pitch
        /// -- a folder makes harmonics far above its own note, and high up there is
        /// nowhere for them to go.
        /// </summary>
        private void RenderSineTriangle(byte[] syncIn, short[] buffer, int size)
        {
            int sineGain = 32767 - 6 * (pitch - (92 << 7));
            int triGain = 32767 - 7 * (pitch - (80 << 7));
            if (sineGain < 0) { sineGain = 0; }
            else if (sineGain > 32767) { sineGain = 32767; }
            if (triGain < 0) { triGain = 0; }
            else if (triGain > 32767) { triGain = 32767; }

            analog[0].SetParameter((short)(timbre * sineGain >> 15));
            analog[1].SetParameter((short)(timbre * triGain >> 15));
            analog[0].SetPitch(pitch);
            analog[1].SetPitch(pitch);
            analog[0].SetShape(AnalogOscillator.ShapeSineFold);
            analog[1].SetShape(AnalogOscillator.ShapeTriangleFold);

            analog[0].Render(syncIn, buffer, null, size);
            analog[1].Render(syncIn, temp, null, size);

            int start = previousColour;
            int delta = colour - previousColour;
            int step = SweepStep(size);
            int xfade = 0;
            for (int i = 0; i < size; i++)
            {
                xfade += step;
                buffer[i] = BraidsResources.Mix(buffer[i], temp[i],
                                                Swept(start, delta, xfade) << 1);
            }
        }

        /// <summary>
        /// BUZZ: two pulse trains a fraction apart, COLOR setting how far. Together
        /// they beat, which is what stops it sounding like a single buzzer.
        /// </summary>
        private void RenderBuzz(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetParameter(timbre);
            analog[0].SetShape(AnalogOscillator.ShapeBuzz);
            analog[0].SetPitch(pitch);

            analog[1].SetParameter(timbre);
            analog[1].SetShape(AnalogOscillator.ShapeBuzz);
            analog[1].SetPitch((short)(pitch + (colour >> 8)));

            analog[0].Render(syncIn, buffer, null, size);
            analog[1].Render(syncIn, temp, null, size);

            for (int i = 0; i < size; i++)
            {
                buffer[i] = (short)((buffer[i] >> 1) + (temp[i] >> 1));
            }
        }

        /// <summary>
        /// SQUARE SUB and SAW SUB: the wave with a square under it, one or two
        /// octaves down. COLOR is the sub's level, and picks the octave on the way
        /// past the middle -- so the knob goes sub, none, sub, an octave lower.
        /// </summary>
        private void RenderSub(byte[] syncIn, short[] buffer, int size)
        {
            int baseShape = model == ModelSquareSub
                ? AnalogOscillator.ShapeSquare
                : AnalogOscillator.ShapeVariableSaw;

            analog[0].SetParameter(timbre);
            analog[0].SetShape(baseShape);
            analog[0].SetPitch(pitch);

            analog[1].SetParameter(0);
            analog[1].SetShape(AnalogOscillator.ShapeSquare);
            int octave = colour < 16384 ? (24 << 7) : (12 << 7);
            analog[1].SetPitch((short)(pitch - octave));

            analog[0].Render(syncIn, buffer, null, size);
            analog[1].Render(syncIn, temp, null, size);

            int start = previousColour;
            int delta = colour - previousColour;
            int step = SweepStep(size);
            int xfade = 0;
            for (int i = 0; i < size; i++)
            {
                xfade += step;
                int value = Swept(start, delta, xfade);
                int subGain = (value < 16384 ? (16383 - value) : (value - 16384)) << 1;
                buffer[i] = BraidsResources.Mix(buffer[i], temp[i], subGain);
            }
        }

        /// <summary>
        /// SQUARE SYNC and SAW SYNC: a second oscillator restarted by the first
        /// every cycle, so it keeps the first one's pitch and TIMBRE only changes
        /// its formant. COLOR mixes the slave in against the master.
        /// </summary>
        private void RenderDualSync(byte[] syncIn, short[] buffer, int size)
        {
            int baseShape = model == ModelSquareSync
                ? AnalogOscillator.ShapeSquare
                : AnalogOscillator.ShapeSaw;

            analog[0].SetParameter(0);
            analog[0].SetShape(baseShape);
            analog[0].SetPitch(pitch);

            analog[1].SetParameter(0);
            analog[1].SetShape(baseShape);
            analog[1].SetPitch((short)(pitch + (timbre >> 2)));

            // The master writes where in each sample it wrapped; the slave restarts
            // there, which is what keeps the sync hard and free of steps.
            analog[0].Render(syncIn, buffer, syncBuffer, size);
            analog[1].Render(syncBuffer, temp, null, size);

            int start = previousColour;
            int delta = colour - previousColour;
            int step = SweepStep(size);
            int xfade = 0;
            for (int i = 0; i < size; i++)
            {
                xfade += step;
                int balance = Swept(start, delta, xfade) << 1;
                buffer[i] = (short)((BraidsResources.Mix(buffer[i], temp[i], balance) >> 2) * 3);
            }
        }

        /// <summary>
        /// The four TRIPLE models: three of the same waveform, the second and third
        /// detuned by TIMBRE and COLOR through <see cref="Intervals"/>. A few cents
        /// gives a chorus, an interval gives a chord.
        /// </summary>
        private void RenderTriple(byte[] syncIn, short[] buffer, int size)
        {
            int baseShape;
            if (model == ModelTripleSaw) { baseShape = AnalogOscillator.ShapeSaw; }
            else if (model == ModelTripleSquare) { baseShape = AnalogOscillator.ShapeSquare; }
            else if (model == ModelTripleTriangle) { baseShape = AnalogOscillator.ShapeTriangle; }
            else { baseShape = AnalogOscillator.ShapeSine; }

            analog[0].SetPitch(pitch);
            for (int i = 0; i < 3; i++)
            {
                analog[i].SetParameter(0);
                analog[i].SetShape(baseShape);
            }

            // Interpolated between two entries of the interval table rather than
            // stepped from one to the next, so the detuning slides.
            for (int i = 0; i < 2; i++)
            {
                int value = i == 0 ? timbre : colour;
                int a = Intervals[value >> 9];
                int b = Intervals[((value >> 8) + 1) >> 1];
                int xfade = (value << 8) & 0xffff;
                analog[i + 1].SetPitch((short)(pitch + a + ((b - a) * xfade >> 16)));
            }

            Array.Clear(buffer, 0, size);
            for (int i = 0; i < 3; i++)
            {
                analog[i].Render(syncIn, temp, null, size);
                for (int j = 0; j < size; j++)
                {
                    buffer[j] = (short)(buffer[j] + (temp[j] * 21 >> 6));
                }
            }
        }

        /// <summary>
        /// SAW COMB: a plain saw through a resonant comb filter. TIMBRE tunes the
        /// comb against the note, COLOR is the feedback; wound up it rings like a
        /// plucked string over the saw.
        /// </summary>
        private void RenderSawComb(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetParameter(0);
            analog[0].SetPitch(pitch);
            analog[0].SetShape(AnalogOscillator.ShapeSaw);
            analog[0].Render(syncIn, buffer, null, size);

            digital.RenderComb(buffer, size, pitch, timbre, colour);
        }

        /// <summary>
        /// The analog oscillator with nothing on top: one waveform, TIMBRE and COLOR
        /// straight through to its parameter and aux parameter. Not one of Braids'
        /// models -- it is what the models are made of.
        /// </summary>
        private void RenderRaw(byte[] syncIn, short[] buffer, int size)
        {
            analog[0].SetShape(RawShape(model));
            analog[0].SetPitch(pitch);
            analog[0].SetParameter(timbre);
            analog[0].SetAuxParameter(colour);
            analog[0].Render(syncIn, buffer, null, size);
        }

        private static int RawShape(int which)
        {
            switch (which)
            {
                case ModelRawVariableSaw: return AnalogOscillator.ShapeVariableSaw;
                case ModelRawSquare: return AnalogOscillator.ShapeSquare;
                case ModelRawTriangle: return AnalogOscillator.ShapeTriangle;
                case ModelRawSine: return AnalogOscillator.ShapeSine;
                case ModelRawTriangleFold: return AnalogOscillator.ShapeTriangleFold;
                case ModelRawSineFold: return AnalogOscillator.ShapeSineFold;
                default: return AnalogOscillator.ShapeSaw;
            }
        }
    }
}
