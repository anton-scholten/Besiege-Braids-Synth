using System;
using BraidsSynth;

/// <summary>
/// Renders every model and checks the result is a signal rather than silence,
/// a constant, or a runaway. Run by tools/run-tests.sh, through Besiege's own
/// compiler and runtime, so it exercises exactly the arithmetic the game will.
///
/// This cannot say a model sounds right. It does say that a model renders at
/// all, stays inside int16, keeps its DC offset small and moves -- which is
/// what a fixed-point port gets wrong.
/// </summary>
public class OscillatorCheck
{
    private const int SampleRate = 48000;
    private const int Block = 512;
    private const int Blocks = 24;

    private static int failures;

    private static readonly string[] Names = new string[]
    {
        "CSAW", "MORPH", "SAW SQUARE", "SINE TRIANGLE", "BUZZ",
        "SQUARE SUB", "SAW SUB", "SQUARE SYNC", "SAW SYNC",
        "TRIPLE SAW", "TRIPLE SQUARE", "TRIPLE TRIANGLE", "TRIPLE SINE",
        "TRIPLE RING MOD", "SAW SWARM", "SAW COMB",
        "raw saw", "raw variable saw", "raw square", "raw triangle",
        "raw sine", "raw triangle fold", "raw sine fold"
    };

    public static int Main(string[] args)
    {
        BraidsResources.Prepare(SampleRate);
        CheckTables();

        for (int model = 0; model < MacroOscillator.ModelCount; model++)
        {
            Check(model, 60, 16384, 16384);
        }

        // The corners of the two controls, where the fixed point is most likely
        // to come apart, over the pitch range the block's slider offers.
        int[] pitches = new int[] { 24, 48, 72, 96 };
        int[] values = new int[] { 0, 1, 16384, 32767 };
        for (int model = 0; model < MacroOscillator.ModelCount; model++)
        {
            for (int p = 0; p < pitches.Length; p++)
            {
                for (int t = 0; t < values.Length; t++)
                {
                    for (int c = 0; c < values.Length; c++)
                    {
                        Check(model, pitches[p], values[t], values[c]);
                    }
                }
            }
        }

        CheckSweep();
        CheckDcBlocker();

        if (failures == 0)
        {
            Console.WriteLine("All " + MacroOscillator.ModelCount + " models render cleanly.");
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Fail(string what)
    {
        Console.WriteLine("FAIL: " + what);
        failures++;
    }

    /// <summary>The tables have known lengths and known ends; a wrong one is silent.</summary>
    private static void CheckTables()
    {
        Length("wav_sine", BraidsResources.Sine, 257);
        Length("ws_tri_fold", BraidsResources.TriFold, 257);
        Length("ws_sine_fold", BraidsResources.SineFold, 257);
        Length("ws_moderate_overdrive", BraidsResources.ModerateOverdrive, 257);
        Length("ws_violent_overdrive", BraidsResources.ViolentOverdrive, 257);
        if (BraidsResources.OscillatorIncrements(SampleRate).Length != 97)
        {
            Fail("the increment table is not 97 entries");
        }
        if (BraidsResources.OscillatorDelays(SampleRate).Length != 97)
        {
            Fail("the delay table is not 97 entries");
        }
        if (BraidsResources.BandlimitedComb(SampleRate).Length != BraidsResources.CombZones)
        {
            Fail("there are not " + BraidsResources.CombZones + " comb tables");
        }

        // Each waveshaper must span its whole output range, or the model reading
        // it comes out quiet in a way nothing else would explain.
        Span("ws_tri_fold", BraidsResources.TriFold);
        Span("ws_sine_fold", BraidsResources.SineFold);
        Span("ws_violent_overdrive", BraidsResources.ViolentOverdrive);
    }

    private static void Length(string name, short[] table, int expected)
    {
        if (table == null || table.Length != expected)
        {
            Fail(name + " is " + (table == null ? "missing" : table.Length.ToString())
                 + ", expected " + expected);
        }
    }

    /// <summary>
    /// A waveshaper's largest excursion should reach full scale. Only the largest:
    /// `resources.py` centres a curve before scaling it, so a curve that is not
    /// symmetric reaches the rail at one end and stops short at the other.
    /// </summary>
    private static void Span(string name, short[] table)
    {
        int low = 32767;
        int high = -32768;
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] < low) { low = table[i]; }
            if (table[i] > high) { high = table[i]; }
        }
        if (Math.Max(-low, high) < 32700)
        {
            Fail(name + " only spans " + low + ".." + high);
        }
    }

    /// <summary>
    /// MORPH with both controls at the top renders a saturated constant, not a
    /// wave: TIMBRE puts it on a 1% pulse, COLOR closes the low-pass down onto the
    /// fundamental so only the pulse's own average survives, and the fuzz then
    /// pins that average to the rail. Verified bit-for-bit against Braids' C++ by
    /// tools/compare-reference.sh, so it is the instrument, not the port -- and on
    /// the module the output capacitor makes it silence, as the DC blocker does
    /// here. Excluded rather than worked around.
    /// </summary>
    private static bool IsSaturatedByDesign(int model, int timbre, int colour)
    {
        return model == MacroOscillator.ModelMorph && timbre == 32767 && colour == 32767;
    }

    private static void Check(int model, int note, int timbre, int colour)
    {
        if (IsSaturatedByDesign(model, timbre, colour))
        {
            return;
        }

        MacroOscillator osc = new MacroOscillator(SampleRate);
        osc.SetModel(model);
        osc.SetPitch((short)(note * 128));
        osc.SetTimbre((short)timbre);
        osc.SetColour((short)colour);

        short[] buffer = new short[Block];
        long sum = 0;
        long energy = 0;
        int low = 32767;
        int high = -32768;
        int changes = 0;

        for (int b = 0; b < Blocks; b++)
        {
            osc.Render(null, buffer, Block);
            // The first blocks are the oscillator settling: a filter charging, a
            // comb line filling. Only the steady state is judged.
            if (b < Blocks / 2)
            {
                continue;
            }
            for (int i = 0; i < Block; i++)
            {
                int s = buffer[i];
                sum += s;
                energy += (long)s * s;
                if (s < low) { low = s; }
                if (s > high) { high = s; }
                if (i > 0 && buffer[i] != buffer[i - 1]) { changes++; }
            }
        }

        string what = Names[model] + " (note " + note + ", timbre " + timbre
                    + ", color " + colour + ")";
        int samples = Block * (Blocks / 2);

        if (changes == 0)
        {
            Fail(what + " renders a constant " + buffer[0]);
            return;
        }

        // Measured about the mean rather than about zero. Several models are meant
        // to carry a large offset -- a 1% pulse spends 99% of its cycle at the rail
        // -- and it is the block's DC blocker, not the oscillator, that takes it
        // off. What has to be here is the part that moves.
        double mean = (double)sum / samples;
        double variance = (double)energy / samples - mean * mean;
        double rms = variance <= 0.0 ? 0.0 : Math.Sqrt(variance);
        if (rms < 24.0)
        {
            Fail(what + " is silent (rms " + rms.ToString("F1") + " about " +
                 mean.ToString("F0") + ")");
        }

        if (high - low < 64)
        {
            Fail(what + " only spans " + low + ".." + high);
        }
    }

    /// <summary>
    /// Every model through the block's DC blocker, which is what stands in for the
    /// module's output capacitor. Whatever offset a model carries must be gone by
    /// the time it reaches Unity, or the gate steps the speaker instead of starting
    /// a note.
    /// </summary>
    private static void CheckDcBlocker()
    {
        for (int model = 0; model < MacroOscillator.ModelCount; model++)
        {
            MacroOscillator osc = new MacroOscillator(SampleRate);
            osc.SetModel(model);
            osc.SetPitch(60 * 128);
            // Full TIMBRE is where the offsets are: it is the narrowest pulse
            // width, which is the most lopsided square.
            osc.SetTimbre(32767);
            osc.SetColour(16384);

            DcBlocker blocker = new DcBlocker(SampleRate);
            short[] buffer = new short[Block];
            double sum = 0.0;
            double energy = 0.0;
            int counted = 0;

            for (int b = 0; b < Blocks; b++)
            {
                osc.Render(null, buffer, Block);
                for (int i = 0; i < Block; i++)
                {
                    float s = blocker.Process(buffer[i]);
                    if (b < Blocks / 2) { continue; }
                    sum += s;
                    energy += (double)s * s;
                    counted++;
                }
            }

            double mean = sum / counted;
            double rms = Math.Sqrt(energy / counted);
            if (Math.Abs(mean) > 600.0)
            {
                Fail(Names[model] + " still sits at " + mean.ToString("F0") +
                     " after the DC blocker");
            }
            if (rms < 24.0)
            {
                Fail(Names[model] + " is silent after the DC blocker");
            }
        }
    }

    /// <summary>
    /// Sweeping the controls while rendering, which is what the block does every
    /// frame. Catches a model whose state runs away when it is not left alone.
    /// </summary>
    private static void CheckSweep()
    {
        for (int model = 0; model < MacroOscillator.ModelCount; model++)
        {
            MacroOscillator osc = new MacroOscillator(SampleRate);
            osc.SetModel(model);
            short[] buffer = new short[Block];
            long energy = 0;

            for (int b = 0; b < 64; b++)
            {
                osc.SetPitch((short)((36 + (b % 48)) * 128));
                osc.SetTimbre((short)(b * 512 % 32768));
                osc.SetColour((short)(32767 - b * 512 % 32768));
                osc.Render(null, buffer, Block);
                if (b < 32) { continue; }
                for (int i = 0; i < Block; i++)
                {
                    energy += (long)buffer[i] * buffer[i];
                }
            }

            double rms = Math.Sqrt((double)energy / (Block * 32));
            if (rms < 24.0)
            {
                Fail(Names[model] + " goes silent when its controls are swept (rms "
                     + rms.ToString("F1") + ")");
            }
        }
    }
}
