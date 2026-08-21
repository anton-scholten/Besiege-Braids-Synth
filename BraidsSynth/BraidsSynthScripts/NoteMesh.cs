using System.Collections.Generic;
using UnityEngine;

namespace BraidsSynth
{
    /// <summary>
    /// The pair of beamed semiquavers standing inside the block's cage.
    ///
    /// Built here rather than shipped, for the same reason the oscillator's tables
    /// are: it is a handful of extruded outlines -- two tilted ellipses for the
    /// heads, two uprights for the stems, two slanted parallelograms for the beams
    /// -- and a formula is smaller than a mesh and easier to adjust than one.
    ///
    /// Everything is laid out in a flat XY plane and extruded along Z, then centred
    /// and normalised so the tall side measures 1. <see cref="Size"/> is what fits
    /// it inside the cage, and is the only number worth touching.
    /// </summary>
    public static class NoteMesh
    {
        /// <summary>How much of the cage's inside the note fills.</summary>
        private const float Size = 0.66f;

        /// <summary>Where the cage's middle is, along the block's stacking axis.</summary>
        private const float Middle = 0.5f;

        private const string ChildName = "BraidsNote";

        // The layout, in the note's own units. Both heads sit on stems that rise to
        // a beam slanting up to the right, which is what makes it a *pair* rather
        // than two notes that happen to be side by side.
        private const float HeadRadiusX = 0.175f;
        private const float HeadRadiusY = 0.135f;
        private const float HeadTilt = -22f;
        private const float StemWidth = 0.055f;
        private const float BeamDepth = 0.115f;
        private const float BeamGap = 0.19f;
        private const float HalfThickness = 0.09f;
        private const int HeadSegments = 28;

        private static Mesh shared;

        /// <summary>
        /// Hangs the note inside the cage. Safe to call more than once on a block --
        /// the second call finds the first one's child and leaves it alone.
        /// </summary>
        public static GameObject Attach(Transform block)
        {
            if (block == null || block.Find(ChildName) != null)
            {
                return null;
            }
            if (shared == null)
            {
                shared = Build();
            }

            GameObject note = new GameObject(ChildName);
            note.transform.SetParent(block, false);
            note.transform.localPosition = new Vector3(0f, 0f, Middle);
            // Upright, and facing -y rather than +y. A note is a flat plate: one
            // side of it reads as a note and the other reads mirrored, and there is
            // no facing that satisfies both. This is the side the block is looked at
            // from. Turned rather than mirrored through a negative scale, which
            // would reverse the winding and light the note inside out.
            note.transform.localRotation = Quaternion.LookRotation(Vector3.down,
                                                                   Vector3.forward);
            note.transform.localScale = new Vector3(Size, Size, Size);

            MeshFilter filter = note.AddComponent<MeshFilter>();
            filter.sharedMesh = shared;
            note.AddComponent<MeshRenderer>();
            return note;
        }

        // ---- the outline -------------------------------------------------------

        private static Mesh Build()
        {
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            // Right head sits higher than the left, so the beam has something to
            // slant across.
            Head(verts, tris, -0.34f, -0.42f);
            Head(verts, tris, 0.26f, -0.18f);

            float leftStem = -0.34f + HeadRadiusX - StemWidth * 0.5f;
            float rightStem = 0.26f + HeadRadiusX - StemWidth * 0.5f;
            float leftTop = 0.46f;
            float rightTop = 0.70f;

            Bar(verts, tris, leftStem - StemWidth * 0.5f, -0.42f,
                             leftStem + StemWidth * 0.5f, leftTop);
            Bar(verts, tris, rightStem - StemWidth * 0.5f, -0.18f,
                             rightStem + StemWidth * 0.5f, rightTop);

            // The beams overhang the stems a little, as engraved ones do.
            float slope = (rightTop - leftTop) / (rightStem - leftStem);
            float x0 = leftStem - StemWidth * 0.5f;
            float x1 = rightStem + StemWidth * 0.5f;
            for (int i = 0; i < 2; i++)
            {
                float drop = i * BeamGap;
                float y0 = leftTop + slope * (x0 - leftStem) - drop;
                float y1 = leftTop + slope * (x1 - leftStem) - drop;
                Beam(verts, tris, x0, y0, x1, y1, BeamDepth);
            }

            Centre(verts);

            Mesh mesh = new Mesh();
            mesh.name = "BraidsNote";
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            // Every face has its own vertices, so this leaves the note faceted
            // rather than smearing the shading round its edges.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A note head: an ellipse, tilted the way an engraver draws one.</summary>
        private static void Head(List<Vector3> verts, List<int> tris, float cx, float cy)
        {
            Vector2[] outline = new Vector2[HeadSegments];
            float tilt = HeadTilt * Mathf.Deg2Rad;
            float cos = Mathf.Cos(tilt);
            float sin = Mathf.Sin(tilt);
            for (int i = 0; i < HeadSegments; i++)
            {
                float a = 2f * Mathf.PI * i / HeadSegments;
                float x = HeadRadiusX * Mathf.Cos(a);
                float y = HeadRadiusY * Mathf.Sin(a);
                outline[i] = new Vector2(cx + x * cos - y * sin,
                                         cy + x * sin + y * cos);
            }
            Extrude(verts, tris, outline);
        }

        /// <summary>An upright bar: a stem.</summary>
        private static void Bar(List<Vector3> verts, List<int> tris,
                                float x0, float y0, float x1, float y1)
        {
            Extrude(verts, tris, new Vector2[]
            {
                new Vector2(x0, y0), new Vector2(x1, y0),
                new Vector2(x1, y1), new Vector2(x0, y1)
            });
        }

        /// <summary>
        /// A beam: a parallelogram with upright sides, which is how a slanted beam
        /// is cut -- its ends stay vertical however far it leans.
        /// </summary>
        private static void Beam(List<Vector3> verts, List<int> tris,
                                 float x0, float y0, float x1, float y1, float depth)
        {
            Extrude(verts, tris, new Vector2[]
            {
                new Vector2(x0, y0 - depth), new Vector2(x1, y1 - depth),
                new Vector2(x1, y1), new Vector2(x0, y0)
            });
        }

        /// <summary>
        /// Turns a convex outline, wound anticlockwise, into a solid: a cap at each
        /// end and a band of quads round the side. Every triangle gets its own
        /// vertices so the normals come out flat.
        /// </summary>
        private static void Extrude(List<Vector3> verts, List<int> tris, Vector2[] outline)
        {
            int n = outline.Length;
            for (int i = 1; i < n - 1; i++)
            {
                // Front, facing -z; back, facing +z.
                Triangle(verts, tris,
                         At(outline[0], -HalfThickness),
                         At(outline[i + 1], -HalfThickness),
                         At(outline[i], -HalfThickness));
                Triangle(verts, tris,
                         At(outline[0], HalfThickness),
                         At(outline[i], HalfThickness),
                         At(outline[i + 1], HalfThickness));
            }
            for (int i = 0; i < n; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % n];
                Triangle(verts, tris, At(a, -HalfThickness), At(b, -HalfThickness),
                                      At(b, HalfThickness));
                Triangle(verts, tris, At(a, -HalfThickness), At(b, HalfThickness),
                                      At(a, HalfThickness));
            }
        }

        private static Vector3 At(Vector2 p, float z)
        {
            return new Vector3(p.x, p.y, z);
        }

        private static void Triangle(List<Vector3> verts, List<int> tris,
                                     Vector3 a, Vector3 b, Vector3 c)
        {
            int at = verts.Count;
            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            tris.Add(at);
            tris.Add(at + 1);
            tris.Add(at + 2);
        }

        /// <summary>
        /// Centres the note on its own bounds and scales the tall side to 1, so what
        /// hangs it inside the cage is one number rather than the whole layout.
        /// </summary>
        private static void Centre(List<Vector3> verts)
        {
            if (verts.Count == 0)
            {
                return;
            }
            Vector3 low = verts[0];
            Vector3 high = verts[0];
            for (int i = 1; i < verts.Count; i++)
            {
                low = Vector3.Min(low, verts[i]);
                high = Vector3.Max(high, verts[i]);
            }
            Vector3 middle = (low + high) * 0.5f;
            Vector3 span = high - low;
            float largest = Mathf.Max(span.x, span.y);
            float scale = largest > 0.0001f ? 1f / largest : 1f;
            for (int i = 0; i < verts.Count; i++)
            {
                verts[i] = (verts[i] - middle) * scale;
            }
        }
    }
}
