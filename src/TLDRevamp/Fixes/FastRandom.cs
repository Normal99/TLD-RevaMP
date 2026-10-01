using System;
using System.Collections.Generic;

namespace TLDRevamp.Fixes
{
    /// The first NextDouble() of `new System.Random(seed)`, without allocating: a copy of the reference-source
    /// System.Random seeding (Knuth subtractive generator) that Mono's mscorlib uses. The object generator constructs a
    /// Random per grid cell just to test one number; in-game a construction + draw costs ~2.4 µs.
    /// Exactness is checked against the real System.Random in-game (`randcheck`), not assumed.
    public static class FastRandom
    {
        private const int MBIG = int.MaxValue;
        private const int MSEED = 161803398;

        [ThreadStatic] private static int[] _seedArray;

        // Only the steps that feed the first sample (a[1] - a[22]): found by backward liveness over the 4×55 mixing
        // steps, executed in the original order. 34 of 220 mixing steps; exact by construction (same operations on
        // the same values). Seeding still runs its whole 54-step chain (each step depends on the previous).
        private static readonly int[] SeedIdx = new int[55];
        private static readonly int[] OpI, OpJ;

        static FastRandom()
        {
            for (int i = 1; i < 55; i++) SeedIdx[i] = 21 * i % 55;
            var ops = new List<int[]>();
            for (int k = 1; k < 5; k++)
                for (int i = 1; i < 56; i++) ops.Add(new[] { i, 1 + (i + 30) % 55 });
            var live = new HashSet<int> { 1, 22 };
            var need = new List<int[]>();
            for (int n = ops.Count - 1; n >= 0; n--)
                if (live.Contains(ops[n][0])) { need.Add(ops[n]); live.Add(ops[n][1]); }
            need.Reverse();
            OpI = new int[need.Count]; OpJ = new int[need.Count];
            for (int n = 0; n < need.Count; n++) { OpI[n] = need[n][0]; OpJ[n] = need[n][1]; }
        }

        public static double FirstDouble(int seed)
        {
            var a = _seedArray ?? (_seedArray = new int[56]);
            int subtraction = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
            int mj = MSEED - subtraction;
            a[55] = mj;
            int mk = 1;
            var seedIdx = SeedIdx;
            for (int i = 1; i < 55; i++)
            {
                int ii = seedIdx[i];
                a[ii] = mk;
                mk = mj - mk;
                if (mk < 0) mk += MBIG;
                mj = a[ii];
            }
            var opI = OpI; var opJ = OpJ;
            for (int n = 0; n < opI.Length; n++)
            {
                int i = opI[n];
                int v = a[i] - a[opJ[n]];
                if (v < 0) v += MBIG;
                a[i] = v;
            }
            int ret = a[1] - a[22];
            if (ret == MBIG) ret--;
            if (ret < 0) ret += MBIG;
            return ret * (1.0 / MBIG);
        }

        /// The full seeding (all 220 mixing steps), kept for comparison.
        public static double FirstDoubleFull(int seed)
        {
            var a = _seedArray ?? (_seedArray = new int[56]);
            int subtraction = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
            int mj = MSEED - subtraction;
            a[55] = mj;
            int mk = 1;
            for (int i = 1; i < 55; i++)
            {
                int ii = 21 * i % 55;
                a[ii] = mk;
                mk = mj - mk;
                if (mk < 0) mk += MBIG;
                mj = a[ii];
            }
            for (int k = 1; k < 5; k++)
            {
                for (int i = 1; i < 56; i++)
                {
                    a[i] -= a[1 + (i + 30) % 55];
                    if (a[i] < 0) a[i] += MBIG;
                }
            }
            // InternalSample with inext = 0, inextp = 21 → indices 1 and 22
            int ret = a[1] - a[22];
            if (ret == MBIG) ret--;
            if (ret < 0) ret += MBIG;
            return ret * (1.0 / MBIG);
        }

        /// Compare with the real System.Random for n seeds spread over the whole int range (incl. edge cases).
        public static string Check(int n)
        {
            long mismatches = 0;
            string first = "";
            var rnd = new System.Random(12345);
            int[] edge = { 0, 1, -1, int.MaxValue, int.MinValue, int.MinValue + 1, MSEED, -MSEED };
            for (int i = 0; i < n + edge.Length; i++)
            {
                int seed = i < edge.Length ? edge[i] : rnd.Next(int.MinValue, int.MaxValue);
                double real = new System.Random(seed).NextDouble();
                double fast = FirstDouble(seed);
                if (real != fast)
                {
                    mismatches++;
                    if (first.Length == 0) first = seed + ": " + real.ToString("R") + " vs " + fast.ToString("R");
                }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double sink = 0;
            for (int i = 0; i < 20000; i++) sink += FirstDouble(i * 7919);
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / 20000;
            sw.Restart();
            for (int i = 0; i < 20000; i++) sink += new System.Random(i * 7919).NextDouble();
            double realUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 20000;
            return "{\"checked\":" + (n + edge.Length) + ",\"mismatches\":" + mismatches + ",\"first\":" + Json.Str(first) +
                   ",\"fastUs\":" + us.ToString("F3") + ",\"realUs\":" + realUs.ToString("F3") + ",\"sink\":" + (sink > 0 ? 1 : 0) + "}";
        }
    }
}
