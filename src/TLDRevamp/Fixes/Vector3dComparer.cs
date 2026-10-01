using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's Vector3d struct overrides Equals(object) but doesn't implement IEquatable&lt;Vector3d&gt;, so
    /// .NET's default comparer boxes both keys on every Dictionary&lt;Vector3d,…&gt; lookup and List.Contains.
    /// World generation (roads, POIs, objects, terrain) does thousands of those per frame.
    ///
    /// We install an allocation-free comparer as EqualityComparer&lt;Vector3d&gt;.Default before the world loads,
    /// so every collection created afterwards picks it up. Equality and hash are bit-for-bit the game's own
    /// (Vector3d.Equals / Vector3d.GetHashCode), so collection behaviour is unchanged. Only the boxing goes away.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.Vector3dComparer.Enabled. When off, calls go through the
    /// game's boxing path. The hash is identical either way, so toggling on live dictionaries is safe.
    public sealed class Vector3dComparer : EqualityComparer<Vector3d>
    {
        public static bool Enabled = true;
        public static bool Installed { get; private set; }

        public static void Install()
        {
            var field = typeof(EqualityComparer<Vector3d>).GetField("defaultComparer", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                Plugin.Log.LogWarning("Vector3dComparer: EqualityComparer<T>.defaultComparer not found; fix not installed");
                return;
            }
            var candidate = new Vector3dComparer();
            string fail = candidate.SelfTest();
            if (fail != null)
            {
                Plugin.Log.LogError("Vector3dComparer self-test FAILED, not installing: " + fail);
                return;
            }
            field.SetValue(null, candidate);
            Installed = EqualityComparer<Vector3d>.Default is Vector3dComparer;
            Plugin.Log.LogInfo("Vector3dComparer installed: " + Installed);
        }

        /// Compares against the game's own Equals/GetHashCode on edge cases and random values.
        /// Returns null on success, else a description of the first mismatch.
        public string SelfTest()
        {
            double[] edge = { 0.0, -0.0, double.NaN, -double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                              double.Epsilon, double.MaxValue, 1000.0, -1000.0, 1e-9, 123456.789 };
            var rnd = new System.Random(1234);
            var vals = new List<Vector3d>();
            foreach (var a in edge) foreach (var b in edge) vals.Add(new Vector3d(a, b, edge[(int)(rnd.NextDouble() * edge.Length)]));
            for (int i = 0; i < 500; i++)
            {
                // Grid-aligned values like chunk positions, plus arbitrary ones
                double g() => rnd.Next(2) == 0 ? System.Math.Round(rnd.NextDouble() * 200 - 100) * 250.0 : rnd.NextDouble() * 1e6 - 5e5;
                vals.Add(new Vector3d(g(), rnd.Next(3) == 0 ? 0.0 : g(), g()));
            }
            bool was = Enabled;
            Enabled = true;
            try
            {
                for (int i = 0; i < vals.Count; i++)
                {
                    var a = vals[i];
                    if (GetHashCode(a) != a.GetHashCode()) return "hash " + a;
                    for (int j = 0; j < vals.Count; j += 7)
                    {
                        var b = vals[j];
                        if (Equals(a, b) != a.Equals((object)b)) return "equals " + a + " vs " + b;
                    }
                    var copy = new Vector3d(a.x, a.y, a.z);
                    if (Equals(a, copy) != a.Equals((object)copy)) return "self " + a;
                }
            }
            finally { Enabled = was; }
            return null;
        }

        public override bool Equals(Vector3d a, Vector3d b)
        {
            if (!Enabled) return a.Equals((object)b); // original path, boxes
            // Same as Vector3d.Equals(object): double.Equals per component (NaN == NaN, 0.0 == -0.0)
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }

        public override int GetHashCode(Vector3d v)
        {
            if (!Enabled) return v.GetHashCode();
            // Same formula as Vector3d.GetHashCode; double.GetHashCode doesn't allocate
            return v.x.GetHashCode() ^ (v.y.GetHashCode() << 2) ^ (v.z.GetHashCode() >> 2);
        }
    }
}
