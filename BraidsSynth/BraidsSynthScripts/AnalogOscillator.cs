using System;

namespace BraidsSynth
{
    /// <summary>
    /// A port of Braids' `analog_oscillator.cc` (Emilie Gillet, MIT). Kept in the
    /// original 32-bit fixed point: the phase is a uint accumulator, samples are
    /// int16, and the band-limited step (BLEP) corrections are the same integer
    /// arithmetic. Floating point would round differently and lose the character.
    ///
    /// Shapes are `const int` rather than an enum on purpose -- Besiege's in-game
    /// compiler segfaults on any enum declaration.
    ///
    /// All nine of Braids' analog shapes are here. The three that read shipped
    /// tables -- the two wavefolders and BUZZ -- get them from
    /// <see cref="BraidsResources"/>, which rebuilds them from the formulas in
    /// Braids' `resources.py` instead of embedding the arrays.
    /// </summary>
    public class AnalogOscillator
    {
        public const int ShapeSaw = 0;
        public const int ShapeVariableSaw = 1;
        public const int ShapeCSaw = 2;
        public const int ShapeSquare = 3;
        public const int ShapeTriangle = 4;
        public const int ShapeSine = 5;
        public const int ShapeTriangleFold = 6;
        public const int ShapeSineFold = 7;
        public const int ShapeBuzz = 8;
        public const int ShapeCount = 9;

        private const int HighestNote = 128 * 128;

        private uint phase;
        private uint phaseIncrement;
        private bool high;

        // Braids does not step the pitch at a block boundary; it slides the phase
        // increment across the block from last block's value to this one's. Every
        // shape but BUZZ does it, so a pitch that is being moved glides rather than
        // arriving in jumps -- and at Unity's block size, which is far longer than
        // Braids' 24 samples, leaving it out is plainly audible.
        //
        // The wavefolders sweep their parameter the same way, for the same reason:
        // a fold moving in steps sounds like it is being switched.
        //
        // Init deliberately leaves the increment alone, as Braids does: it is where
        // the pitch is sliding *from*, and clearing it on a shape change would make
        // every change of model chirp up from nothing over a block. The first block
        // after construction does sweep up from zero, which is Braids' behaviour at
        // power-up and is under a closed gate here.
        private uint previousPhaseIncrement;
        private short previousParameter;

        private short parameter;
        private short auxParameter;
        private short discontinuityDepth;
        private short pitch;
        private int nextSample;

        private int shape;
        private int previousShape;
        private int sampleRate;

        public AnalogOscillator(int sampleRate)
        {
            this.sampleRate = sampleRate;
            Init();
            previousShape = -1;
        }

        public void Init()
        {
            phase = 0;
            phaseIncrement = 1;
            high = false;
            parameter = 0;
            previousParameter = 0;
            auxParameter = 0;
            discontinuityDepth = -16383;
            pitch = 60 << 7;
            nextSample = 0;
        }

        public void SetShape(int value) { shape = value; }
        public void SetPitch(short value) { pitch = value; }
        public void SetParameter(short value) { parameter = value; }
        public void SetAuxParameter(short value) { auxParameter = value; }
        public uint PhaseIncrement { get { return phaseIncrement; } }
        public void Reset() { phase = 0u - phaseIncrement; }

        /// <summary>
        /// Braids' own pitch-to-increment: fold the note down into the table's
        /// octave counting the shifts, interpolate, then shift back.
        /// </summary>
        private uint ComputePhaseIncrement(short midiPitch)
        {
            int p = midiPitch;
            if (p >= HighestNote)
            {
                p = HighestNote - 1;
            }
            int refPitch = p - BraidsResources.PitchTableStart;
            int numShifts = 0;
            while (refPitch < 0)
            {
                refPitch += BraidsResources.Octave;
                numShifts++;
            }
            int[] lut = BraidsResources.OscillatorIncrements(sampleRate);
            uint a = (uint)lut[refPitch >> 4];
            uint b = (uint)lut[(refPitch >> 4) + 1];
            uint increment = a + (uint)((int)(b - a) * (refPitch & 0xf) >> 4);
            increment >>= numShifts;
            return increment;
        }

        public void Render(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            if (shape != previousShape)
            {
                Init();
                previousShape = shape;
            }

            phaseIncrement = ComputePhaseIncrement(pitch);

            if (pitch > HighestNote)
            {
                pitch = HighestNote;
            }
            else if (pitch < 0)
            {
                pitch = 0;
            }

            if (shape == ShapeSine)
            {
                RenderSine(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeTriangleFold)
            {
                RenderTriangleFold(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeSineFold)
            {
                RenderSineFold(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeBuzz)
            {
                RenderBuzz(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeTriangle)
            {
                RenderTriangle(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeSquare)
            {
                RenderSquare(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeVariableSaw)
            {
                RenderVariableSaw(syncIn, buffer, syncOut, size);
            }
            else if (shape == ShapeCSaw)
            {
                RenderCSaw(syncIn, buffer, syncOut, size);
            }
            else
            {
                RenderSaw(syncIn, buffer, syncOut, size);
            }
        }

        // ---- band-limited step corrections -----------------------------------

        private static int ThisBlepSample(uint t)
        {
            if (t > 65535) { t = 65535; }
            return (int)(t * t >> 18);
        }

        private static int NextBlepSample(uint t)
        {
            if (t > 65535) { t = 65535; }
            t = 65535 - t;
            return -(int)(t * t >> 18);
        }

        /// <summary>Writes the fractional reset time the sync slave reads back.</summary>
        private void EmitSync(byte[] syncOut, int i, uint increment)
        {
            if (syncOut == null)
            {
                return;
            }
            syncOut[i] = phase < increment
                ? (byte)(phase / (increment >> 7) + 1)
                : (byte)0;
        }

        private static bool Synced(byte[] syncIn, int i)
        {
            return syncIn != null && syncIn[i] != 0;
        }

        // ---- shapes ----------------------------------------------------------

        private void RenderSaw(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int next = nextSample;
            for (int i = 0; i < size; i++)
            {
                bool syncReset = false;
                bool selfReset = false;
                bool transitionDuringReset = false;
                uint resetTime = 0;

                increment += incrementStep;
                int thisSample = next;
                next = 0;

                if (Synced(syncIn, i))
                {
                    resetTime = (uint)(syncIn[i] - 1) << 9;
                    uint phaseAtReset = phase + (65535 - resetTime) * (increment >> 16);
                    syncReset = true;
                    if (phaseAtReset < phase)
                    {
                        transitionDuringReset = true;
                    }
                    int discontinuity = (int)(phaseAtReset >> 17);
                    thisSample -= discontinuity * ThisBlepSample(resetTime) >> 15;
                    next -= discontinuity * NextBlepSample(resetTime) >> 15;
                }

                phase += increment;
                if (phase < increment)
                {
                    selfReset = true;
                }
                EmitSync(syncOut, i, increment);

                if ((transitionDuringReset || !syncReset) && selfReset)
                {
                    uint t = phase / (increment >> 16);
                    thisSample -= ThisBlepSample(t);
                    next -= NextBlepSample(t);
                }

                if (syncReset)
                {
                    phase = resetTime * (increment >> 16);
                    high = false;
                }

                next += (int)(phase >> 17);
                buffer[i] = (short)((thisSample - 16384) << 1);
            }
            nextSample = next;
            previousPhaseIncrement = increment;
        }

        private void RenderSquare(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            if (parameter > 32000)
            {
                parameter = 32000;
            }
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int next = nextSample;
            for (int i = 0; i < size; i++)
            {
                bool syncReset = false;
                bool selfReset = false;
                bool transitionDuringReset = false;
                uint resetTime = 0;

                increment += incrementStep;
                uint pw = (uint)(32768 - parameter) << 16;
                int thisSample = next;
                next = 0;

                if (Synced(syncIn, i))
                {
                    resetTime = (uint)(syncIn[i] - 1) << 9;
                    uint phaseAtReset = phase + (65535 - resetTime) * (increment >> 16);
                    syncReset = true;
                    if (phaseAtReset < phase || (!high && phaseAtReset >= pw))
                    {
                        transitionDuringReset = true;
                    }
                    if (phaseAtReset >= pw)
                    {
                        thisSample -= ThisBlepSample(resetTime);
                        next -= NextBlepSample(resetTime);
                    }
                }

                phase += increment;
                if (phase < increment)
                {
                    selfReset = true;
                }
                EmitSync(syncOut, i, increment);

                while (transitionDuringReset || !syncReset)
                {
                    if (!high)
                    {
                        if (phase < pw) { break; }
                        uint t = (phase - pw) / (increment >> 16);
                        thisSample += ThisBlepSample(t);
                        next += NextBlepSample(t);
                        high = true;
                    }
                    if (high)
                    {
                        if (!selfReset) { break; }
                        selfReset = false;
                        uint t = phase / (increment >> 16);
                        thisSample -= ThisBlepSample(t);
                        next -= NextBlepSample(t);
                        high = false;
                    }
                }

                if (syncReset)
                {
                    phase = resetTime * (increment >> 16);
                    high = false;
                }

                next += phase < pw ? 0 : 32767;
                buffer[i] = (short)((thisSample - 16384) << 1);
            }
            nextSample = next;
            previousPhaseIncrement = increment;
        }

        private void RenderVariableSaw(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int next = nextSample;
            if (parameter < 1024)
            {
                parameter = 1024;
            }
            for (int i = 0; i < size; i++)
            {
                bool syncReset = false;
                bool selfReset = false;
                bool transitionDuringReset = false;
                uint resetTime = 0;

                increment += incrementStep;
                uint pw = (uint)parameter << 16;
                int thisSample = next;
                next = 0;

                if (Synced(syncIn, i))
                {
                    resetTime = (uint)(syncIn[i] - 1) << 9;
                    uint phaseAtReset = phase + (65535 - resetTime) * (increment >> 16);
                    syncReset = true;
                    if (phaseAtReset < phase || (!high && phaseAtReset >= pw))
                    {
                        transitionDuringReset = true;
                    }
                    int before = (int)(phaseAtReset >> 18) + (int)((phaseAtReset - pw) >> 18);
                    int after = 0 + (int)((0 - pw) >> 18);
                    int discontinuity = after - before;
                    thisSample += discontinuity * ThisBlepSample(resetTime) >> 15;
                    next += discontinuity * NextBlepSample(resetTime) >> 15;
                }

                phase += increment;
                if (phase < increment)
                {
                    selfReset = true;
                }
                EmitSync(syncOut, i, increment);

                while (transitionDuringReset || !syncReset)
                {
                    if (!high)
                    {
                        if (phase < pw) { break; }
                        uint t = (phase - pw) / (increment >> 16);
                        thisSample -= ThisBlepSample(t) >> 1;
                        next -= NextBlepSample(t) >> 1;
                        high = true;
                    }
                    if (high)
                    {
                        if (!selfReset) { break; }
                        selfReset = false;
                        uint t = phase / (increment >> 16);
                        thisSample -= ThisBlepSample(t) >> 1;
                        next -= NextBlepSample(t) >> 1;
                        high = false;
                    }
                }

                if (syncReset)
                {
                    phase = resetTime * (increment >> 16);
                    high = false;
                }

                next += (int)(phase >> 18);
                next += (int)((phase - pw) >> 18);
                buffer[i] = (short)((thisSample - 16384) << 1);
            }
            nextSample = next;
            previousPhaseIncrement = increment;
        }

        private void RenderCSaw(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int next = nextSample;
            for (int i = 0; i < size; i++)
            {
                bool syncReset = false;
                bool selfReset = false;
                bool transitionDuringReset = false;
                uint resetTime = 0;

                increment += incrementStep;
                uint pw = (uint)parameter * 49152;
                if (pw < 8 * increment)
                {
                    pw = 8 * increment;
                }

                int thisSample = next;
                next = 0;

                if (Synced(syncIn, i))
                {
                    resetTime = (uint)(syncIn[i] - 1) << 9;
                    uint phaseAtReset = phase + (65535 - resetTime) * (increment >> 16);
                    syncReset = true;
                    transitionDuringReset = false;
                    if (phaseAtReset < phase || (!high && phaseAtReset >= pw))
                    {
                        transitionDuringReset = true;
                    }
                    if (phase >= pw)
                    {
                        discontinuityDepth = (short)(-2048 + (auxParameter >> 2));
                        int before = (int)(phaseAtReset >> 18);
                        int after = discontinuityDepth;
                        int discontinuity = after - before;
                        thisSample += discontinuity * ThisBlepSample(resetTime) >> 15;
                        next += discontinuity * NextBlepSample(resetTime) >> 15;
                    }
                }

                phase += increment;
                if (phase < increment)
                {
                    selfReset = true;
                }
                EmitSync(syncOut, i, increment);

                while (transitionDuringReset || !syncReset)
                {
                    if (!high)
                    {
                        if (phase < pw) { break; }
                        uint t = (phase - pw) / (increment >> 16);
                        int before = discontinuityDepth;
                        int after = (int)(phase >> 18);
                        int discontinuity = after - before;
                        thisSample += discontinuity * ThisBlepSample(t) >> 15;
                        next += discontinuity * NextBlepSample(t) >> 15;
                        high = true;
                    }
                    if (high)
                    {
                        if (!selfReset) { break; }
                        selfReset = false;
                        discontinuityDepth = (short)(-2048 + (auxParameter >> 2));
                        uint t = phase / (increment >> 16);
                        int before = 16383;
                        int after = discontinuityDepth;
                        int discontinuity = after - before;
                        thisSample += discontinuity * ThisBlepSample(t) >> 15;
                        next += discontinuity * NextBlepSample(t) >> 15;
                        high = false;
                    }
                }

                if (syncReset)
                {
                    phase = resetTime * (increment >> 16);
                    high = false;
                }

                next += phase < pw ? discontinuityDepth : (int)(phase >> 18);
                buffer[i] = (short)((thisSample - 8192) << 1);
            }
            nextSample = next;
            previousPhaseIncrement = increment;
        }

        /// <summary>Two-times oversampled, as in Braids: no BLEP needed for a triangle.</summary>
        private void RenderTriangle(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            uint p = phase;
            for (int i = 0; i < size; i++)
            {
                increment += incrementStep;
                if (Synced(syncIn, i))
                {
                    p = 0;
                }

                p += increment >> 1;
                short tri = Fold(p);
                int sample = tri >> 1;

                p += increment >> 1;
                tri = Fold(p);
                sample += tri >> 1;

                buffer[i] = (short)sample;
            }
            phase = p;
            previousPhaseIncrement = increment;
        }

        private static short Fold(uint p)
        {
            ushort phase16 = (ushort)(p >> 16);
            int t = (phase16 << 1) ^ ((phase16 & 0x8000) != 0 ? 0xffff : 0x0000);
            return unchecked((short)(unchecked((short)t) + 32768));
        }

        private void RenderSine(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            uint p = phase;
            for (int i = 0; i < size; i++)
            {
                increment += incrementStep;
                p += increment;
                if (Synced(syncIn, i))
                {
                    p = 0;
                }
                buffer[i] = BraidsResources.Interpolate824(BraidsResources.Sine, p);
            }
            previousPhaseIncrement = increment;
            phase = p;
        }

        // ---- the shapes that read tables -------------------------------------

        /// <summary>
        /// How far into the folding curve the parameter drives the signal. At zero
        /// the wave barely reaches the first fold; at full it is pushed through
        /// several. Braids' range, unchanged.
        /// </summary>
        private static int FoldGain(int parameterValue)
        {
            return 2048 + (parameterValue * 30720 >> 15);
        }

        /// <summary>
        /// A triangle driven into `ws_tri_fold`. Two-times oversampled, like the
        /// plain triangle: a wavefolder makes far more harmonics than it is given,
        /// and there is no BLEP that would help with them.
        ///
        /// The parameter and the pitch are both swept across the block rather than
        /// stepped at its edge -- see <see cref="previousPhaseIncrement"/>.
        /// </summary>
        private void RenderTriangleFold(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint p = phase;
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int parameterStart = previousParameter;
            int parameterDelta = parameter - previousParameter;
            int parameterStep = 32767 / size;
            int xfade = 0;

            for (int i = 0; i < size; i++)
            {
                xfade += parameterStep;
                int value = parameterStart + (parameterDelta * xfade >> 15);
                increment += incrementStep;

                int gain = FoldGain(value);
                if (Synced(syncIn, i))
                {
                    p = 0;
                }

                p += increment >> 1;
                int sample = Folded(Fold(p), gain) >> 1;

                p += increment >> 1;
                sample += Folded(Fold(p), gain) >> 1;

                buffer[i] = (short)sample;
            }

            previousParameter = parameter;
            previousPhaseIncrement = increment;
            phase = p;
        }

        /// <summary>A sine driven into `ws_sine_fold`. Otherwise the triangle fold.</summary>
        private void RenderSineFold(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            uint p = phase;
            uint increment = previousPhaseIncrement;
            uint incrementStep = IncrementStep(size);
            int parameterStart = previousParameter;
            int parameterDelta = parameter - previousParameter;
            int parameterStep = 32767 / size;
            int xfade = 0;

            for (int i = 0; i < size; i++)
            {
                xfade += parameterStep;
                int value = parameterStart + (parameterDelta * xfade >> 15);
                increment += incrementStep;

                int gain = FoldGain(value);
                if (Synced(syncIn, i))
                {
                    p = 0;
                }

                p += increment >> 1;
                int sample = SineFolded(p, gain) >> 1;

                p += increment >> 1;
                sample += SineFolded(p, gain) >> 1;

                buffer[i] = (short)sample;
            }

            previousParameter = parameter;
            previousPhaseIncrement = increment;
            phase = p;
        }

        /// <summary>
        /// Drives an already-folded triangle sample through the waveshaper. The
        /// int16 truncation on the way in is Braids' and is part of the sound:
        /// the gain is applied to a value that has already wrapped.
        /// </summary>
        private static short Folded(short triangle, int gain)
        {
            short driven = unchecked((short)(triangle * gain >> 15));
            return BraidsResources.Interpolate88(BraidsResources.TriFold, driven + 32768);
        }

        private static short SineFolded(uint p, int gain)
        {
            short sine = BraidsResources.Interpolate824(BraidsResources.Sine, p);
            short driven = unchecked((short)(sine * gain >> 15));
            return BraidsResources.Interpolate88(BraidsResources.SineFold, driven + 32768);
        }

        /// <summary>
        /// How much the phase increment moves per sample to reach this block's pitch
        /// by the end of it. Unsigned throughout, so a falling pitch wraps round --
        /// which is Braids' `~((previous - target) / size)`, and adding a wrapped
        /// step is the same as subtracting. Every shape but BUZZ starts its loop
        /// with a step of this.
        /// </summary>
        private uint IncrementStep(int size)
        {
            return previousPhaseIncrement < phaseIncrement
                ? (phaseIncrement - previousPhaseIncrement) / (uint)size
                : ~((previousPhaseIncrement - phaseIncrement) / (uint)size);
        }

        /// <summary>
        /// BUZZ: a pulse train, read out of whichever pair of band-limited comb
        /// tables covers the pitch and blended between them. TIMBRE shifts which
        /// pair, so it runs from a thin buzz to a dark one.
        /// </summary>
        private void RenderBuzz(byte[] syncIn, short[] buffer, byte[] syncOut, int size)
        {
            int shiftedPitch = pitch + ((32767 - parameter) >> 1);
            int crossfade = (shiftedPitch << 6) & 0xffff;

            int index = shiftedPitch >> 10;
            if (index >= BraidsResources.CombZones)
            {
                index = BraidsResources.CombZones - 1;
            }
            short[][] tables = BraidsResources.BandlimitedComb(sampleRate);
            short[] first = tables[index];

            index += 1;
            if (index >= BraidsResources.CombZones)
            {
                index = BraidsResources.CombZones - 1;
            }
            short[] second = tables[index];

            for (int i = 0; i < size; i++)
            {
                phase += phaseIncrement;
                if (Synced(syncIn, i))
                {
                    phase = 0;
                }
                buffer[i] = BraidsResources.Crossfade(first, second, phase, crossfade);
            }
        }
    }
}
