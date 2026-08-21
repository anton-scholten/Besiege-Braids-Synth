using System.Collections.Generic;

namespace BraidsSynth
{
    /// <summary>
    /// What each model is called and what its two controls do in it.
    ///
    /// TIMBRE and COLOR mean something different in every model -- that is the whole
    /// idea of a macro-oscillator, and it is also the thing that makes one hard to
    /// use blind. Braids has a four-letter display and a manual; this block has room
    /// for a sentence, so it uses it.
    ///
    /// The order here is <see cref="MacroOscillator"/>'s, which is Braids' own.
    /// </summary>
    public static class BraidsModels
    {
        private static readonly string[] names = new string[]
        {
            "CSaw", "Morph", "Saw square", "Sine triangle", "Buzz",
            "Square sub", "Saw sub", "Square sync", "Saw sync",
            "Triple saw", "Triple square", "Triple triangle", "Triple sine",
            "Triple ring mod", "Saw swarm", "Saw comb",
            "Saw", "Variable saw", "Square", "Triangle", "Sine",
            "Triangle fold", "Sine fold"
        };

        private static readonly string[] timbres = new string[]
        {
            "where the wave drops out",
            "triangle to saw to square to sine",
            "shape of both waves",
            "how far both fold",
            "brightness",
            "pulse width",
            "shape of the saw",
            "pitch of the synced oscillator",
            "pitch of the synced oscillator",
            "detune of the second saw",
            "detune of the second square",
            "detune of the second triangle",
            "detune of the second sine",
            "first modulator's interval",
            "how far the seven saws spread",
            "tuning of the comb",
            "nothing",
            "where the slope turns",
            "pulse width",
            "nothing",
            "nothing",
            "how far the wave folds",
            "how far the wave folds"
        };

        private static readonly string[] colours = new string[]
        {
            "how deep the notch goes",
            "filter, then fuzz",
            "saw against square",
            "sine fold against triangle fold",
            "detune between the two",
            "sub level, and its octave",
            "sub level, and its octave",
            "synced against original",
            "synced against original",
            "detune of the third saw",
            "detune of the third square",
            "detune of the third triangle",
            "detune of the third sine",
            "second modulator's interval",
            "high-pass",
            "how much the comb rings",
            "nothing",
            "nothing",
            "nothing",
            "nothing",
            "nothing",
            "nothing",
            "nothing"
        };

        /// <summary>Where the raw waveforms start; everything before is a Braids model.</summary>
        public static int WaveformsFrom
        {
            get { return MacroOscillator.BraidsModelCount; }
        }

        public static int Count
        {
            get { return names.Length; }
        }

        public static string Name(int model)
        {
            return InRange(model) ? names[model] : "Model " + model;
        }

        /// <summary>What TIMBRE does in this model, as a phrase to follow "TIMBRE:".</summary>
        public static string Timbre(int model)
        {
            return InRange(model) ? timbres[model] : "";
        }

        /// <summary>The same for COLOR.</summary>
        public static string Colour(int model)
        {
            return InRange(model) ? colours[model] : "";
        }

        /// <summary>False where the control does nothing, so the panel can dim it.</summary>
        public static bool UsesTimbre(int model)
        {
            return Timbre(model) != "nothing";
        }

        public static bool UsesColour(int model)
        {
            return Colour(model) != "nothing";
        }

        /// <summary>The menu the block's mapper shows, in the same order.</summary>
        public static List<string> MenuItems()
        {
            List<string> items = new List<string>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                items.Add(names[i]);
            }
            return items;
        }

        private static bool InRange(int model)
        {
            return model >= 0 && model < names.Length;
        }

        /// <summary>
        /// A MIDI note as a name: 60 is C4, which is what a keyboard would call it.
        /// </summary>
        public static string NoteName(int midiNote)
        {
            string[] step = new string[]
            {
                "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
            };
            int index = midiNote % 12;
            if (index < 0)
            {
                index += 12;
            }
            int octave = (midiNote - index) / 12 - 1;
            return step[index] + octave;
        }
    }
}
