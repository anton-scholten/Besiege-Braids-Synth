namespace BraidsSynth
{
    /// <summary>
    /// The high-pass the real Braids has in hardware and the port would otherwise
    /// be missing.
    ///
    /// Several of Braids' models carry a large DC offset on purpose. A square at
    /// full TIMBRE is a 1% pulse, so it sits near the positive rail for the rest of
    /// the cycle; MORPH's filter and fuzz shift the whole wave; CSAW's notch is an
    /// offset by construction. On the module none of that reaches the outside world,
    /// because the output stage is capacitor-coupled.
    ///
    /// Unity has no such stage. Left in, the offset costs most of the headroom, and
    /// opening or closing the gate steps the speaker cone rather than starting a
    /// note. So the same one-pole high-pass goes here, at the end of the chain and
    /// after nothing else, which is where the hardware has it.
    ///
    /// Deliberately not in the fixed point: this is not part of Braids' arithmetic,
    /// it is the analogue stage after it, and at 12 Hz the coefficient has no useful
    /// fixed-point form anyway.
    /// </summary>
    public class DcBlocker
    {
        /// <summary>
        /// Corner frequency. Low enough to be well under anything the oscillator is
        /// asked to play -- MIDI note 24 is 32 Hz -- and high enough to settle in a
        /// few tens of milliseconds rather than ringing on under a note.
        /// </summary>
        private const float CornerHz = 12f;

        private readonly float pole;
        private float lastIn;
        private float lastOut;

        public DcBlocker(int sampleRate)
        {
            if (sampleRate <= 0)
            {
                sampleRate = 48000;
            }
            pole = 1f - 6.2831853f * CornerHz / sampleRate;
            if (pole < 0f) { pole = 0f; }
            if (pole > 0.9999f) { pole = 0.9999f; }
        }

        public void Reset()
        {
            lastIn = 0f;
            lastOut = 0f;
        }

        public float Process(float sample)
        {
            float output = sample - lastIn + pole * lastOut;
            lastIn = sample;
            lastOut = output;
            return output;
        }
    }
}
