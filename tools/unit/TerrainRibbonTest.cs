#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;
using TLDRevamp.Fixes;
using UnityEngine;

/// The Unity-shaped twin of the pure ribbon geometry (src/TLDRevamp/Fixes/TerrainRibbon.cs).
///
/// The renderer draws its ribbons through TerrainHorizonGeometry, which has no Unity types, so
/// the headless test can run it. This file is the Unity twin of the same geometry; it needs Unity's
/// Vector3, so it gets the same stubs the other unit tests use. The point of the test is that the
/// twin must agree with the pure one: same refusals, same winding, no NaN in the buffer.
static class TerrainRibbonTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    static bool Refused(Vector3 a, Vector3 c, float halfWidth, float lift)
    {
        var v = new List<Vector3>(); var u = new List<Vector2>(); var t = new List<int>();
        bool accepted = TerrainRibbon.Add(v, u, t, a, c, halfWidth, lift);
        if (accepted) return false;
        // a refused strip must not have written anything at all
        if (v.Count != 0 || u.Count != 0 || t.Count != 0) throw new Exception("a refused strip wrote into the buffer");
        return true;
    }

    static bool FacesUp(List<Vector3> v, List<int> t)
    {
        for (int i = 0; i + 2 < t.Count; i += 3)
        {
            var e1 = v[t[i + 1]] - v[t[i]];
            var e2 = v[t[i + 2]] - v[t[i]];
            if (!(Vector3.Cross(e1, e2).y > 0f)) return false;
        }
        return true;
    }

    static int Main()
    {
        // ---- the non-finite input (defect D1)
        // A NaN half-width is NOT caught by a range test: every comparison with NaN is false, so
        // `halfWidth <= 0` is false and `halfWidth > 100` is false. The check has to be explicit.
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), float.NaN, 0), "a NaN half-width is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), float.PositiveInfinity, 0), "an infinite half-width is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), float.NegativeInfinity, 0), "a negative infinite half-width is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), 6, float.NaN), "a NaN lift is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), 6, float.PositiveInfinity), "an infinite lift is refused");
        // an overflowing segment: the length becomes an infinity, which a `length > 0.01` test
        // would happily accept
        Check(Refused(new Vector3(0, 0, 0), new Vector3(float.MaxValue, 0, float.MaxValue), 6, 0), "an overflowing segment is refused");
        Check(Refused(new Vector3(float.NaN, 0, 0), new Vector3(0, 0, 30), 6, 0), "a NaN endpoint is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(float.PositiveInfinity, 0, 30), 6, 0), "an infinite endpoint is refused");
        Check(Refused(new Vector3(0, float.NaN, 0), new Vector3(0, 0, 30), 6, 0), "a NaN height is refused");
        // the degenerate and absurd ones
        Check(Refused(new Vector3(1, 0, 1), new Vector3(1, 0, 1), 6, 0), "a zero-length strip is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), 0, 0), "a zero half-width is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), -1, 0), "a negative half-width is refused");
        Check(Refused(new Vector3(0, 0, 0), new Vector3(0, 0, 30), 1000, 0), "an unbounded half-width is refused");

        // ---- the winding: the road must face UP
        var v = new List<Vector3>(); var u = new List<Vector2>(); var t = new List<int>();
        Check(TerrainRibbon.Add(v, u, t, new Vector3(0, 10, 0), new Vector3(0, 10, 40), 7, 0.08f), "a finite strip is accepted");
        Check(v.Count == 4 && u.Count == 4 && t.Count == 6, "one strip is four vertices, four uvs and two triangles");
        Check(FacesUp(v, t), "the ribbon's triangles face up");
        Check(TerrainRibbon.FacesUp(new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(10, 0, 10)), "a counter-clockwise triangle faces up");
        Check(!TerrainRibbon.FacesUp(new Vector3(0, 0, 0), new Vector3(10, 0, 10), new Vector3(0, 0, 10)), "a clockwise triangle faces down");
        Check(!TerrainRibbon.FacesUp(new Vector3(float.NaN, 0, 0), new Vector3(0, 0, 10), new Vector3(10, 0, 10)), "a non-finite triangle has no facing");

        // every direction of travel, and a climbing road
        var dirs = new (float dx, float dz, string name)[] { (0, 1, "+Z"), (0, -1, "-Z"), (1, 0, "+X"), (-1, 0, "-X") };
        foreach (var (dx, dz, name) in dirs)
        {
            var v2 = new List<Vector3>(); var u2 = new List<Vector2>(); var t2 = new List<int>();
            Check(TerrainRibbon.Add(v2, u2, t2, new Vector3(0, 0, 0), new Vector3(dx * 40, 0, dz * 40), 7, 0), "a strip along " + name + " is accepted");
            Check(FacesUp(v2, t2), "a strip along " + name + " faces up");
        }
        var climb = new List<Vector3>(); var climbU = new List<Vector2>(); var climbT = new List<int>();
        Check(TerrainRibbon.Add(climb, climbU, climbT, new Vector3(0, 0, 0), new Vector3(0, 30, 40), 7, 0), "a climbing strip is accepted");
        Check(FacesUp(climb, climbT), "a climbing strip faces up");

        // ---- the twin must agree with the pure geometry the renderer uses
        var pv = new TerrainHorizonGeometry.Batch(1, 100);
        Check(pv.AddStrip(new TerrainHorizonGeometry.Point(0, 10, 0), new TerrainHorizonGeometry.Point(0, 10, 40), 7, 0.08),
              "the pure geometry accepts the same strip");
        Check(pv.VertexCount == v.Count && pv.TriangleCount * 3 == t.Count, "the twin and the pure geometry make the same triangles");
        for (int i = 0; i < 4; i++)
        {
            Check(Math.Abs(pv.Vx[i] - v[i].x) < 1e-4 && Math.Abs(pv.Vy[i] - v[i].y) < 1e-4 && Math.Abs(pv.Vz[i] - v[i].z) < 1e-4,
                  "the twin and the pure geometry place vertex " + i + " the same way");
        }
        for (int i = 0; i < 6; i++) Check(pv.T[i] == t[i], "the twin and the pure geometry agree on triangle index " + i);

        Console.WriteLine("Terrain ribbon: " + checks + " checks passed");
        return 0;
    }
}
