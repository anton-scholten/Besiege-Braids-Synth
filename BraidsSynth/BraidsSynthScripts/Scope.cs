using UnityEngine;

namespace BraidsSynth
{
    /// <summary>
    /// Draws the block's own output into a texture, so the panel shows the wave the
    /// model is actually making.
    ///
    /// A macro-oscillator is twenty-three names and two controls that mean something
    /// different under each of them. A picture of the wave is the shortest way to
    /// find out what a control is doing, and it is the one thing the module itself
    /// cannot show you.
    ///
    /// The trace is drawn as a filled span per column, from the lowest to the
    /// highest sample that falls in it. A line through one sample per column would
    /// alias into a shape that is not the wave -- there are far more samples across
    /// the buffer than there are pixels across the panel.
    /// </summary>
    public class Scope
    {
        private readonly Texture2D texture;
        private readonly Color32[] pixels;
        private readonly int width;
        private readonly int height;

        private readonly Color32 background;
        private readonly Color32 grid;
        private readonly Color32 trace;

        public Scope(int width, int height)
        {
            this.width = width;
            this.height = height;
            texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            pixels = new Color32[width * height];

            background = new Color32(10, 11, 15, 220);
            // Bright enough that a scope with nothing to draw reads as a quiet
            // instrument rather than as a panel that failed to draw.
            grid = new Color32(84, 88, 102, 255);
            Color t = UIF.Trace;
            trace = new Color32((byte)(t.r * 255f), (byte)(t.g * 255f), (byte)(t.b * 255f), 255);
        }

        public Texture2D Texture { get { return texture; } }

        /// <summary>Frees the texture. A Texture2D is not collected on its own.</summary>
        public void Dispose()
        {
            if (texture != null)
            {
                Object.Destroy(texture);
            }
        }

        /// <summary>
        /// Redraws from <paramref name="samples"/>, which are -1..1 and oldest
        /// first. <paramref name="count"/> may be less than the array's length.
        /// </summary>
        public void Draw(float[] samples, int count)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = background;
            }

            int middle = height / 2;
            for (int x = 0; x < width; x++)
            {
                pixels[middle * width + x] = grid;
            }

            if (samples == null || count <= 0)
            {
                Commit();
                return;
            }

            // Anchored on a rising zero crossing, so a steady note stands still
            // rather than sliding across the panel every frame. Only the first half
            // of the buffer is searched, so there is always a whole screen to draw.
            int start = 0;
            for (int i = 1; i < count / 2; i++)
            {
                if (samples[i - 1] <= 0f && samples[i] > 0f)
                {
                    start = i;
                    break;
                }
            }

            int span = count - start;
            float half = (height - 2) * 0.5f;

            for (int x = 0; x < width; x++)
            {
                int from = start + span * x / width;
                int to = start + span * (x + 1) / width;
                if (to <= from)
                {
                    to = from + 1;
                }
                if (to > count)
                {
                    to = count;
                }

                float low = 1f;
                float high = -1f;
                for (int i = from; i < to; i++)
                {
                    float s = samples[i];
                    if (s < low) { low = s; }
                    if (s > high) { high = s; }
                }
                if (low > high)
                {
                    continue;
                }

                int bottom = middle + Mathf.RoundToInt(Mathf.Clamp(low, -1f, 1f) * half);
                int top = middle + Mathf.RoundToInt(Mathf.Clamp(high, -1f, 1f) * half);
                if (bottom < 0) { bottom = 0; }
                if (top > height - 1) { top = height - 1; }

                for (int y = bottom; y <= top; y++)
                {
                    pixels[y * width + x] = trace;
                }
            }

            Commit();
        }

        private void Commit()
        {
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }
    }
}
