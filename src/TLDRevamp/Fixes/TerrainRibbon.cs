using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Pure road-ribbon geometry, kept next to the renderer of issue #37.
    ///
    /// The renderer itself draws its ribbons through TerrainHorizonGeometry.Batch, which is the
    /// same geometry without the Unity types (so the headless test can run it). This class is the
    /// Unity-shaped twin of that code, for a caller that wants a plain buffer; it must agree with
    /// the pure one exactly, and the test checks that both wind the same way and reject the same
    /// input.
    internal static class TerrainRibbon
    {
        /// Appends one finite strip. Returns false without touching a buffer for degenerate,
        /// non-finite, or absurd input.
        internal static bool Add(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles, Vector3 a, Vector3 c, float halfWidth, float lift)
        {
            // Every number that reaches the buffer must be finite: a NaN half-width or a segment
            // that overflows to infinity would write NaN vertices into the mesh, and the mesh would
            // then be culled or drawn as garbage. The checks are explicit, not a range test: a
            // comparison with NaN is false, so `halfWidth <= 0` does NOT catch a NaN half-width.
            if (!Finite(a) || !Finite(c)) return false;
            if (!Finite(halfWidth) || halfWidth <= 0 || halfWidth > 100) return false;
            if (!Finite(lift)) return false;
            Vector3 forward = new Vector3(c.x - a.x, 0, c.z - a.z);
            if (!Finite(forward)) return false;                  // an overflowing segment
            float length = forward.magnitude;
            // A length that overflows to infinity must be refused: `length > 0.01` is TRUE for an
            // infinity, so the range test alone would let it through, and the strip would then be
            // built with a zero-width side vector.
            if (!Finite(length)) return false;
            if (!(length > 0.01f)) return false;                  // also catches a NaN length
            Vector3 side = Vector3.Cross(Vector3.up, forward / length) * halfWidth;
            if (!Finite(side)) return false;
            Vector3 liftV = Vector3.up * lift;
            var a0 = a - side + liftV; var a1 = a + side + liftV;
            var c0 = c - side + liftV; var c1 = c + side + liftV;
            if (!Finite(a0) || !Finite(a1) || !Finite(c0) || !Finite(c1)) return false;
            int n = vertices.Count;
            vertices.Add(a0); vertices.Add(a1); vertices.Add(c0); vertices.Add(c1);
            uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(0, 1)); uvs.Add(new Vector2(1, 1));
            // The winding that faces UP. The road materials are single-sided (Cull Back), so a
            // ribbon wound the other way is culled by every camera above it and the road is never
            // drawn. The game's own terrain meshes wind this way.
            //
            // The decision comes from the PURE geometry (TerrainHorizonGeometry), which is the
            // code the renderer itself draws with, so the two can never drift apart.
            TerrainHorizonGeometry.UpwardWinding(
                new TerrainHorizonGeometry.Point(a0.x, a0.y, a0.z),
                new TerrainHorizonGeometry.Point(a1.x, a1.y, a1.z),
                new TerrainHorizonGeometry.Point(c0.x, c0.y, c0.z),
                new TerrainHorizonGeometry.Point(c1.x, c1.y, c1.z),
                out int i0, out int i1, out int i2, out int j0, out int j1, out int j2);
            triangles.Add(n + i0); triangles.Add(n + i1); triangles.Add(n + i2);
            triangles.Add(n + j0); triangles.Add(n + j1); triangles.Add(n + j2);
            return true;
        }

        /// Whether a triangle of the given three corners faces up.
        internal static bool FacesUp(Vector3 a, Vector3 b, Vector3 c)
        {
            if (!Finite(a) || !Finite(b) || !Finite(c)) return false;
            var e1 = b - a; var e2 = c - a;
            var n = Vector3.Cross(e1, e2);
            return n.y > 0f;
        }

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
