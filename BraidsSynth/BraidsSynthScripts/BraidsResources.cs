using System;

namespace BraidsSynth
{
    /// <summary>
    /// The lookup tables Braids reads, computed at startup rather than shipped.
    ///
    /// Braids keeps these in `resources.cc` as const arrays generated offline by
    /// `resources.py`. Every one of them turns out to have a closed form -- the
    /// waveshapers are tanh and arctan curves, the comb wavetables are Dirichlet
    /// kernels, the filter and pitch tables are arithmetic -- so they are rebuilt
    /// here instead. Nothing has to be embedded, and the tables that depend on a
    /// sample rate are built for whatever rate Unity is actually running at.
    ///
    /// Each formula was checked against the corresponding array in Braids'
    /// `resources.cc` at 96 kHz and reproduces it exactly, apart from the two
    /// wavetables `resources.py` runs a second-order dither over -- `wav_sine` and
    /// the comb tables -- which come out within 2 of 32766.
    /// </summary>
    public static class BraidsResources
    {
        /// <summary>Entries in a waveform table, plus the guard point Interpolate824 reads.</summary>
        public const int WaveSize = 256;

        /// <summary>Entries in a waveshaper table, plus its guard point.</summary>
        public const int ShaperSize = 256;

        /// <summary>One octave of pitch, in 1/128ths of a semitone.</summary>
        public const int Octave = 12 * 128;

        /// <summary>Where the pitch tables start, in the same units. MIDI note 128.</summary>
        public const int PitchTableStart = 128 * 128;

        /// <summary>Braids' own rate. Only used as the default.</summary>
        public const int NativeSampleRate = 96000;

        /// <summary>Band-limited comb wavetables, one per eight semitones of pitch.</summary>
        public const int CombZones = 15;

        // ---- rate-independent tables ------------------------------------------

        /// <summary>
        /// wav_sine: one period, 257 entries so the guard point is real.
        ///
        /// Braids' table is a quarter period ahead of a plain sine and inverted --
        /// `resources.py` builds it as a sine and then reads it through a quadrature
        /// index -- which comes to -cos. Harmless for a free-running oscillator, but
        /// the ring modulator multiplies two of these together, so it is kept.
        /// </summary>
        public static readonly short[] Sine = BuildSine();

        /// <summary>ws_moderate_overdrive: tanh(2x). The gentler of the two saturators.</summary>
        public static readonly short[] ModerateOverdrive = BuildShaper(2);

        /// <summary>ws_violent_overdrive: tanh(8x). MORPH's fuzz.</summary>
        public static readonly short[] ViolentOverdrive = BuildShaper(8);

        /// <summary>ws_tri_fold: sin(pi(3x + 8x^3)), the triangle wavefolder's curve.</summary>
        public static readonly short[] TriFold = BuildTriFold();

        /// <summary>ws_sine_fold: a windowed 4-cycle sine fading into an arctan.</summary>
        public static readonly short[] SineFold = BuildSineFold();

        // ---- tables built for the output rate ---------------------------------

        private static int cachedRate;
        private static int[] increments;
        private static uint[] delays;
        private static ushort[] svfCutoff;
        private static short[][] combs;

        /// <summary>
        /// Rebuilds every rate-dependent table for <paramref name="sampleRate"/>, if
        /// it is not already built for it. Called once as a block wakes up; the audio
        /// thread only ever reads what this leaves behind.
        /// </summary>
        public static void Prepare(int sampleRate)
        {
            if (cachedRate == sampleRate && increments != null)
            {
                return;
            }
            increments = BuildIncrements(sampleRate);
            delays = BuildDelays(sampleRate);
            svfCutoff = BuildSvfCutoff(sampleRate);
            combs = BuildCombs(sampleRate);
            cachedRate = sampleRate;
        }

        /// <summary>
        /// lut_oscillator_increments: phase increment per sample, over the octave
        /// above MIDI note 128, in steps of 1/8 semitone. 97 entries, the last being
        /// the guard point ComputePhaseIncrement interpolates towards.
        ///
        /// Braids samples at 96 kHz and resamples on the way out. Building the table
        /// at Unity's own rate instead means the oscillator runs natively at that
        /// rate, so there is no resampling stage at all -- the BLEP anti-aliasing
        /// works from the increment either way.
        /// </summary>
        public static int[] OscillatorIncrements(int sampleRate)
        {
            Prepare(sampleRate);
            return increments;
        }

        /// <summary>
        /// lut_oscillator_delays: the same octave as a delay length in 4096ths of a
        /// sample, which is what the comb filter's delay line is addressed in.
        /// </summary>
        public static uint[] OscillatorDelays(int sampleRate)
        {
            Prepare(sampleRate);
            return delays;
        }

        /// <summary>lut_svf_cutoff: 2*sin(pi*f) per semitone, for the state-variable filter.</summary>
        public static ushort[] SvfCutoff(int sampleRate)
        {
            Prepare(sampleRate);
            return svfCutoff;
        }

        /// <summary>
        /// lut_svf_damp[0]: the damping factor at zero resonance, which is the only
        /// entry anything ported here asks for. Always the table's clamp of 2.0.
        /// </summary>
        public const int SvfDampOpen = 65534;

        /// <summary>
        /// wav_bandlimited_comb_0..14: a pulse train band-limited to whatever fits
        /// under Nyquist for the zone's pitch. BUZZ crossfades between two of them.
        /// </summary>
        public static short[][] BandlimitedComb(int sampleRate)
        {
            Prepare(sampleRate);
            return combs;
        }

        // ---- stmlib's interpolation, verbatim ---------------------------------

        /// <summary>Interpolate824: 8 bits of index, 16 of fraction, signed table.</summary>
        public static short Interpolate824(short[] table, uint phase)
        {
            int a = table[phase >> 24];
            int b = table[(phase >> 24) + 1];
            return (short)(a + ((b - a) * (int)((phase >> 8) & 0xffff) >> 16));
        }

        /// <summary>The same over an unsigned table, which is how the filter table is read.</summary>
        public static int Interpolate824(ushort[] table, uint phase)
        {
            uint a = table[phase >> 24];
            uint b = table[(phase >> 24) + 1];
            return (int)(a + ((b - a) * ((phase >> 8) & 0xffff) >> 16));
        }

        /// <summary>Interpolate88: 8 bits of index, 8 of fraction. How a waveshaper is read.</summary>
        public static short Interpolate88(short[] table, int index)
        {
            ushort i = (ushort)index;
            int a = table[i >> 8];
            int b = table[(i >> 8) + 1];
            return (short)(a + ((b - a) * (i & 0xff) >> 8));
        }

        /// <summary>Reads the same phase out of two wavetables and blends them.</summary>
        public static short Crossfade(short[] a, short[] b, uint phase, int balance)
        {
            int x = Interpolate824(a, phase);
            int y = Interpolate824(b, phase);
            return (short)(x + ((y - x) * (balance & 0xffff) >> 16));
        }

        /// <summary>stmlib Mix: 0 is all of <paramref name="a"/>, 65535 all of b.</summary>
        public static short Mix(int a, int b, int balance)
        {
            int t = balance & 0xffff;
            return (short)((a * (65535 - t) + b * t) >> 16);
        }

        /// <summary>stmlib's CLIP, which stops one short of full scale as the original does.</summary>
        public static int Clip(int x)
        {
            if (x < -32767) { return -32767; }
            if (x > 32767) { return 32767; }
            return x;
        }

        // ---- building ---------------------------------------------------------

        /// <summary>
        /// `resources.py`'s `scale`: optionally centre the curve, then stretch it so
        /// its largest excursion reaches full scale. The published form works through
        /// a shift into 0..1 and back out again, which cancels down to this.
        /// </summary>
        private static short[] Scale(double[] v, bool centre)
        {
            int n = v.Length;
            if (centre)
            {
                double sum = 0.0;
                for (int i = 0; i < n; i++) { sum += v[i]; }
                double mean = sum / n;
                for (int i = 0; i < n; i++) { v[i] -= mean; }
            }
            double peak = 0.0;
            for (int i = 0; i < n; i++)
            {
                double a = Math.Abs(v[i]);
                if (a > peak) { peak = a; }
            }
            if (peak <= 0.0) { peak = 1.0; }

            short[] table = new short[n];
            for (int i = 0; i < n; i++)
            {
                table[i] = (short)Math.Round(v[i] / peak * 32766.0);
            }
            return table;
        }

        private static short[] BuildSine()
        {
            double[] v = new double[WaveSize + 1];
            for (int i = 0; i <= WaveSize; i++)
            {
                v[i] = -Math.Cos(2.0 * Math.PI * i / WaveSize);
            }
            return Scale(v, true);
        }

        /// <summary>
        /// The shaper axis: -1 to 1 across the table, with the last entry repeating
        /// the one before it. That repeat is Braids' -- the guard point exists to be
        /// interpolated towards and tanh(1) is where the curve ends.
        /// </summary>
        private static double[] ShaperAxis()
        {
            double[] x = new double[ShaperSize + 1];
            for (int i = 0; i <= ShaperSize; i++)
            {
                x[i] = i / 128.0 - 1.0;
            }
            x[ShaperSize] = x[ShaperSize - 1];
            return x;
        }

        private static short[] BuildShaper(double drive)
        {
            double[] x = ShaperAxis();
            double[] v = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                v[i] = Math.Tanh(drive * x[i]);
            }
            return Scale(v, true);
        }

        private static short[] BuildTriFold()
        {
            double[] x = ShaperAxis();
            double[] v = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double t = 2.0 * x[i];
                v[i] = Math.Sin(Math.PI * (3.0 * x[i] + t * t * t));
            }
            return Scale(v, true);
        }

        /// <summary>
        /// Four cycles of sine under a Gaussian window, handing over to an arctan as
        /// the window closes -- so the fold has folds in the middle and a soft limit
        /// at the ends rather than wrapping round. Not centred: this one is already
        /// odd, and centring it would tilt it.
        /// </summary>
        private static short[] BuildSineFold()
        {
            double[] x = ShaperAxis();
            double[] v = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double window = Math.Pow(Math.Exp(-x[i] * x[i] * 4.0), 1.5);
                v[i] = Math.Sin(8.0 * Math.PI * x[i]) * window
                     + Math.Atan(3.0 * x[i]) * (1.0 - window);
            }
            return Scale(v, false);
        }

        /// <summary>The note the pitch tables' <paramref name="step"/>th entry stands for.</summary>
        private static double TableHz(int step)
        {
            double note = (PitchTableStart + step * 16) / 128.0;
            return 440.0 * Math.Pow(2.0, (note - 69.0) / 12.0);
        }

        private static int[] BuildIncrements(int sampleRate)
        {
            int steps = Octave / 16;                  // 96, plus the guard point
            int[] table = new int[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double inc = 4294967296.0 * TableHz(i) / sampleRate;
                if (inc > 4294967295.0)
                {
                    inc = 4294967295.0;
                }
                table[i] = unchecked((int)(uint)inc);
            }
            return table;
        }

        private static uint[] BuildDelays(int sampleRate)
        {
            int steps = Octave / 16;
            uint[] table = new uint[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double d = sampleRate / TableHz(i) * 65536.0 * 4096.0;
                if (d > 4294967295.0)
                {
                    d = 4294967295.0;
                }
                table[i] = (uint)d;
            }
            return table;
        }

        private static ushort[] BuildSvfCutoff(int sampleRate)
        {
            ushort[] table = new ushort[257];
            for (int i = 0; i <= 256; i++)
            {
                double hz = 440.0 * Math.Pow(2.0, (i - 69) / 12.0);
                double f = hz / sampleRate;
                if (f > 0.125)
                {
                    f = 0.125;
                }
                table[i] = (ushort)(2.0 * Math.Sin(Math.PI * f) * 32767.0);
            }
            return table;
        }

        /// <summary>
        /// One period of a pulse train per zone, each carrying every harmonic that
        /// still fits under Nyquist at the top of its zone -- a Dirichlet kernel with
        /// an odd number of terms. The zones are eight semitones apart, starting at
        /// MIDI note 18, and the last is capped just under Nyquist.
        /// </summary>
        private static short[][] BuildCombs(int sampleRate)
        {
            short[][] tables = new short[CombZones][];
            for (int zone = 0; zone < CombZones; zone++)
            {
                double f0 = 440.0 * Math.Pow(2.0, (18 + 8 * zone - 69) / 12.0);
                double nyquist = sampleRate / 2.0;
                f0 = zone == CombZones - 1 ? nyquist - 1.0 : Math.Min(f0, nyquist);

                double m = 2.0 * Math.Floor(sampleRate / f0 / 2.0) + 1.0;

                // The kernel over one period, indexed from -half to half.
                double[] pulse = new double[WaveSize];
                for (int j = 0; j < WaveSize; j++)
                {
                    double t = (j - WaveSize / 2) / (double)WaveSize;
                    pulse[j] = j == WaveSize / 2
                        ? 1.0
                        : Math.Sin(Math.PI * t * m) / (m * Math.Sin(Math.PI * t) + 1e-9);
                }

                // Read out through the same quarter-period rotation wav_sine uses,
                // with the guard point wrapping back to the start.
                double[] v = new double[WaveSize + 1];
                for (int i = 0; i <= WaveSize; i++)
                {
                    v[i] = pulse[(i + WaveSize / 4) % WaveSize];
                }
                tables[zone] = Scale(v, true);
            }
            return tables;
        }
    }
}
