using System;

namespace BraidsSynth
{
    /// <summary>
    /// The three shapes of Braids' `digital_oscillator.cc` that stand on their own:
    /// the triple ring modulator, the saw swarm and the comb filter. Emilie Gillet,
    /// MIT, like the rest of the port.
    ///
    /// The rest of that file -- FM, the physical models, the noise models, the
    /// wavetables -- is a project of its own and is not here. These three are, being
    /// the ones the macro-oscillator reaches for while it is otherwise stacking up
    /// analog oscillators, and none of them needs anything Braids ships as data.
    ///
    /// Fixed point throughout, as everywhere else in the port.
    /// </summary>
    public class DigitalOscillator
    {
        public const int ShapeTripleRingMod = 0;
        public const int ShapeSawSwarm = 1;
        public const int ShapeComb = 2;

        /// <summary>Samples in the comb's delay line. Braids' `kCombDelayLength`.</summary>
        private const int CombDelayLength = 8192;

        /// <summary>
        /// `digital_oscillator.cc`'s own note ceiling: note 140, an octave above
        /// where `analog_oscillator.cc` puts it. Only the delay uses it, and only
        /// after taking that octave back off -- so the two files in fact stop at the
        /// same note, by different arithmetic. Getting this wrong shortens the comb's
        /// delay early, which is audible as the comb mistuning at high TIMBRE.
        /// </summary>
        private const int HighestNote = 140 * 128;

        private readonly int sampleRate;

        private uint phase;
        private readonly uint[] swarmPhase = new uint[6];
        private int swarmLow;
        private int swarmBand;
        private uint modulatorPhase;
        private uint modulatorPhase2;

        // Held rather than made per block: this is the audio thread, and an
        // allocation per block is a collection every few seconds.
        private readonly uint[] swarmIncrements = new uint[7];

        private short[] combDelay;
        private int combPointer;
        private int combPitch;

        private bool strike;
        private uint rngState = 1u;

        public DigitalOscillator(int sampleRate)
        {
            this.sampleRate = sampleRate;
            Init();
        }

        public void Init()
        {
            phase = 0;
            for (int i = 0; i < swarmPhase.Length; i++)
            {
                swarmPhase[i] = 0;
            }
            swarmLow = 0;
            swarmBand = 0;
            modulatorPhase = 0;
            modulatorPhase2 = 0;
            combPointer = 0;
            combPitch = 0;
            if (combDelay != null)
            {
                Array.Clear(combDelay, 0, combDelay.Length);
            }
            strike = true;
        }

        /// <summary>Scatters the swarm's phases at the next note, as Braids does.</summary>
        public void Strike()
        {
            strike = true;
        }

        // ---- pitch ------------------------------------------------------------

        /// <summary>
        /// The same fold-and-interpolate as the analog oscillator's, over the same
        /// table. Duplicated rather than shared because Braids duplicates it, and
        /// because the two classes are otherwise independent.
        /// </summary>
        private uint ComputePhaseIncrement(int midiPitch)
        {
            // Clamped to the table's own start, not to HighestNote. Braids uses one
            // limit here and the other in ComputeDelay, in the same file.
            int p = midiPitch;
            if (p >= BraidsResources.PitchTableStart)
            {
                p = BraidsResources.PitchTableStart - 1;
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
            return increment >> numShifts;
        }

        /// <summary>
        /// The delay a pitch asks for, in 16.16 samples. The reciprocal of the
        /// increment table, and read the same way -- but shifted the other way, so
        /// the octave folding that divides an increment multiplies a delay.
        /// </summary>
        private uint ComputeDelay(int midiPitch)
        {
            int p = midiPitch;
            if (p >= HighestNote - BraidsResources.Octave)
            {
                p = HighestNote - BraidsResources.Octave;
            }
            int refPitch = p - BraidsResources.PitchTableStart;
            int numShifts = 0;
            while (refPitch < 0)
            {
                refPitch += BraidsResources.Octave;
                numShifts++;
            }
            uint[] lut = BraidsResources.OscillatorDelays(sampleRate);
            uint a = lut[refPitch >> 4];
            uint b = lut[(refPitch >> 4) + 1];
            uint delay = a + (uint)((int)(b - a) * (refPitch & 0xf) >> 4);
            return delay >> (12 - numShifts);
        }

        /// <summary>stmlib's linear congruential generator, seed and all.</summary>
        private uint NextRandom()
        {
            rngState = rngState * 1664525u + 1013904223u;
            return rngState;
        }

        // ---- shapes -----------------------------------------------------------

        /// <summary>
        /// Three sines multiplied together, the two modulators tuned by TIMBRE and
        /// COLOR against the carrier. Detuned by a fraction of a semitone it beats;
        /// detuned by an interval it goes metallic.
        /// </summary>
        public void RenderTripleRingMod(byte[] syncIn, short[] buffer, int size,
                                        int pitch, int timbre, int colour)
        {
            // Braids offsets the carrier by half a period so the three waves do not
            // all start at the same place, and puts it back afterwards.
            uint p = phase + (1u << 30);
            uint increment = ComputePhaseIncrement(pitch);
            uint modIncrement = ComputePhaseIncrement(pitch + ((timbre - 16384) >> 2));
            uint modIncrement2 = ComputePhaseIncrement(pitch + ((colour - 16384) >> 2));

            uint mod = modulatorPhase;
            uint mod2 = modulatorPhase2;

            for (int i = 0; i < size; i++)
            {
                p += increment;
                if (syncIn != null && syncIn[i] != 0)
                {
                    p = 0;
                    mod = 0;
                    mod2 = 0;
                }
                mod += modIncrement;
                mod2 += modIncrement2;

                int result = BraidsResources.Interpolate824(BraidsResources.Sine, p);
                result = result * BraidsResources.Interpolate824(BraidsResources.Sine, mod) >> 16;
                result = result * BraidsResources.Interpolate824(BraidsResources.Sine, mod2) >> 16;
                buffer[i] = BraidsResources.Interpolate88(
                    BraidsResources.ModerateOverdrive, result + 32768);
            }

            phase = p - (1u << 30);
            modulatorPhase = mod;
            modulatorPhase2 = mod2;
        }

        /// <summary>
        /// Seven detuned saws summed, saturated, and put through a high-pass. The
        /// classic supersaw: TIMBRE spreads the detuning, COLOR opens the filter.
        ///
        /// The saws are bare phase ramps with no BLEP at all -- seven of them at
        /// slightly different rates alias into a wash rather than into tones, and
        /// Braids leans on that.
        /// </summary>
        public void RenderSawSwarm(byte[] syncIn, short[] buffer, int size,
                                   int pitch, int timbre, int colour)
        {
            int detune = timbre + 1024;
            detune = (detune * detune) >> 9;

            uint[] increments = swarmIncrements;
            for (int i = 0; i < 7; i++)
            {
                int sawDetune = detune * (i - 3);
                int integral = sawDetune >> 16;
                int fractional = sawDetune & 0xffff;
                int a = (int)ComputePhaseIncrement(pitch + integral);
                int b = (int)ComputePhaseIncrement(pitch + integral + 1);
                increments[i] = (uint)(a + (((b - a) * fractional) >> 16));
            }

            if (strike)
            {
                for (int i = 0; i < swarmPhase.Length; i++)
                {
                    swarmPhase[i] = NextRandom();
                }
                strike = false;
            }

            int cutoff = pitch;
            cutoff += colour < 10922
                ? ((colour - 10922) * 24) >> 5
                : ((colour - 10922) * 12) >> 5;
            if (cutoff < 0) { cutoff = 0; }
            else if (cutoff > 32767) { cutoff = 32767; }

            int f = BraidsResources.Interpolate824(BraidsResources.SvfCutoff(sampleRate),
                                                   (uint)cutoff << 17);
            int damp = BraidsResources.SvfDampOpen;
            int band = swarmBand;
            int low = swarmLow;

            for (int i = 0; i < size; i++)
            {
                if (syncIn != null && syncIn[i] != 0)
                {
                    for (int j = 0; j < swarmPhase.Length; j++)
                    {
                        swarmPhase[j] = 0;
                    }
                }

                phase += increments[0];
                for (int j = 0; j < 6; j++)
                {
                    swarmPhase[j] += increments[j + 1];
                }

                int sample = -28672;
                sample += (int)(phase >> 19);
                for (int j = 0; j < 6; j++)
                {
                    sample += (int)(swarmPhase[j] >> 19);
                }
                sample = BraidsResources.Interpolate88(
                    BraidsResources.ModerateOverdrive, sample + 32768);

                // A state-variable filter, of which only the high-pass is taken.
                int notch = sample - (band * damp >> 15);
                low += f * band >> 15;
                low = BraidsResources.Clip(low);
                int high = notch - low;
                band += f * high >> 15;

                buffer[i] = (short)BraidsResources.Clip(high);
            }

            swarmBand = band;
            swarmLow = low;
        }

        /// <summary>
        /// A resonant comb filter over whatever is already in the buffer. TIMBRE
        /// tunes the delay against the note, COLOR is the feedback -- at the top it
        /// rings on its own, which is what makes SAW COMB a string rather than a
        /// filtered saw.
        /// </summary>
        public void RenderComb(short[] buffer, int size, int pitch, int timbre, int colour)
        {
            if (combDelay == null)
            {
                combDelay = new short[CombDelayLength];
            }

            // The delay time is filtered, not stepped: moving a delay line's read
            // point in one go is a click every time the knob moves.
            int wanted = pitch + ((timbre - 16384) >> 1);
            combPitch = (15 * combPitch + wanted) >> 4;

            uint delay = ComputeDelay(combPitch);
            if (delay > (uint)CombDelayLength << 16)
            {
                delay = (uint)CombDelayLength << 16;
            }
            int delayIntegral = (int)(delay >> 16);
            int delayFractional = (int)(delay & 0xffff);

            // Warped so the ends of the knob's travel, where the interesting part
            // is, are not squeezed into the last few degrees of it.
            int resonance = (colour << 1) - 32768;
            resonance = BraidsResources.Interpolate88(
                BraidsResources.ModerateOverdrive, resonance + 32768);

            int pointer = combPointer % CombDelayLength;
            for (int i = 0; i < size; i++)
            {
                int input = buffer[i];
                int offset = pointer + 2 * CombDelayLength - delayIntegral;
                int a = combDelay[offset % CombDelayLength];
                int b = combDelay[(offset - 1) % CombDelayLength];
                int delayed = a + (((b - a) * (delayFractional >> 1)) >> 15);

                int feedback = (delayed * resonance >> 15) + (input >> 1);
                combDelay[pointer] = (short)BraidsResources.Clip(feedback);

                buffer[i] = (short)BraidsResources.Clip((input + (delayed << 1)) >> 1);
                pointer = (pointer + 1) % CombDelayLength;
            }
            combPointer = pointer;
        }
    }
}
