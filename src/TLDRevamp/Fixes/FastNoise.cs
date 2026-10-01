using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// NoiseS3D (the game's simplex noise) is the hottest function of world generation: every terrain height is a sum of
    /// fractal layers of 3D simplex noise (x, seed, z), road shapes use 2D noise, biomes more 3D noise. The game's version
    /// is the reference implementation written slowly: `perm` is a property with a null check read 12 times per 3D call,
    /// 4 `% 12` divisions, gradients in a jagged int[][] passed to separate dot() calls, fastfloor() calls.
    ///
    /// Same function, same floating-point operations in the same order (bit-identical results, checked by `noisecheck`):
    /// the permutation array read once, `perm[i] % 12` from a precomputed table built from the same array, gradients from
    /// a flat array, the tiny helpers inlined — including fastfloor's quirk (whole negative numbers floor one lower).
    /// The game's callers (terrain noise layers, road shapes) call this directly (transpiler), so there is no wrapper cost.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FastNoise.Enabled
    [HarmonyPatch]
    public static class FastNoise
    {
        public static bool Enabled = false;
        public static int Patched;

        private static readonly AccessTools.FieldRef<int[]> PermRef = AccessTools.StaticFieldRefAccess<int[]>(AccessTools.Field(typeof(NoiseS3D), "perm_"));
        private static readonly MethodInfo Orig2 = AccessTools.Method(typeof(NoiseS3D), nameof(NoiseS3D.Noise), new[] { typeof(double), typeof(double) });
        private static readonly MethodInfo Orig3 = AccessTools.Method(typeof(NoiseS3D), nameof(NoiseS3D.Noise), new[] { typeof(double), typeof(double), typeof(double) });

        // grad3 of NoiseS3D, flattened, as doubles ((double)g[i] in the original)
        private static readonly double[] G = { 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0, 1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1, 0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1 };

        private sealed class Tables { public int[] Perm; public int[] Mod12; }
        private static Tables _t;

        private static Tables Get()
        {
            var perm = PermRef();
            var t = _t;
            if (t != null && ReferenceEquals(t.Perm, perm)) return t;
            if (perm == null) return null; // not set up yet: the original sets it up
            var mod = new int[perm.Length];
            for (int i = 0; i < perm.Length; i++) mod[i] = perm[i] % 12;
            t = new Tables { Perm = perm, Mod12 = mod };
            _t = t; // reference assignment is atomic; workers may build their own copy once, results are identical
            return t;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(terrainGenSettings.noiseSetting), nameof(terrainGenSettings.noiseSetting.NoOctaveNoise));
            yield return AccessTools.Method(typeof(roadGenScript), nameof(roadGenScript.PointOnRoad));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.Calls(Orig3)) { ins.operand = AccessTools.Method(typeof(FastNoise), nameof(Noise3)); Patched++; }
                else if (ins.Calls(Orig2)) { ins.operand = AccessTools.Method(typeof(FastNoise), nameof(Noise2)); Patched++; }
                yield return ins;
            }
        }

        // The original computes these per call; the same expressions give the same doubles
        private static readonly double F2r = 0.5 * (System.Math.Sqrt(3.0) - 1.0);
        private static readonly double G2r = (3.0 - System.Math.Sqrt(3.0)) / 6.0;

        public static double Noise2(double x, double y)
        {
            var t = Enabled ? Get() : null;
            if (t == null) return NoiseS3D.Noise(x, y);
            var perm = t.Perm; var mod = t.Mod12;
            double num = F2r;
            double num2 = (x + y) * num;
            double fx = x + num2, fy = y + num2;
            int num3 = !(fx > 0.0) ? (int)fx - 1 : (int)fx;
            int num4 = !(fy > 0.0) ? (int)fy - 1 : (int)fy;
            double num5 = G2r;
            double num6 = (double)(num3 + num4) * num5;
            double num7 = (double)num3 - num6;
            double num8 = (double)num4 - num6;
            double num9 = x - num7;
            double num10 = y - num8;
            int num11, num12;
            if (num9 > num10) { num11 = 1; num12 = 0; } else { num11 = 0; num12 = 1; }
            double num13 = num9 - (double)num11 + num5;
            double num14 = num10 - (double)num12 + num5;
            double num15 = num9 - 1.0 + 2.0 * num5;
            double num16 = num10 - 1.0 + 2.0 * num5;
            int num17 = num3 & 0xFF;
            int num18 = num4 & 0xFF;
            int g19 = mod[num17 + perm[num18]] * 3;
            int g20 = mod[num17 + num11 + perm[num18 + num12]] * 3;
            int g21 = mod[num17 + 1 + perm[num18 + 1]] * 3;
            var G_ = G;
            double num22 = 0.5 - num9 * num9 - num10 * num10;
            double num23;
            if (num22 < 0.0) num23 = 0.0;
            else { num22 *= num22; num23 = num22 * num22 * (G_[g19] * num9 + G_[g19 + 1] * num10); }
            double num24 = 0.5 - num13 * num13 - num14 * num14;
            double num25;
            if (num24 < 0.0) num25 = 0.0;
            else { num24 *= num24; num25 = num24 * num24 * (G_[g20] * num13 + G_[g20 + 1] * num14); }
            double num26 = 0.5 - num15 * num15 - num16 * num16;
            double num27;
            if (num26 < 0.0) num27 = 0.0;
            else { num26 *= num26; num27 = num26 * num26 * (G_[g21] * num15 + G_[g21 + 1] * num16); }
            return 70.0 * (num23 + num25 + num27);
        }

        public static double Noise3(double x, double y, double z)
        {
            var t = Enabled ? Get() : null;
            if (t == null) return NoiseS3D.Noise(x, y, z);
            var perm = t.Perm; var mod = t.Mod12;
            double num = 1.0 / 3.0;
            double num2 = (x + y + z) * num;
            double fx = x + num2, fy = y + num2, fz = z + num2;
            int num3 = !(fx > 0.0) ? (int)fx - 1 : (int)fx;
            int num4 = !(fy > 0.0) ? (int)fy - 1 : (int)fy;
            int num5 = !(fz > 0.0) ? (int)fz - 1 : (int)fz;
            double num6 = 1.0 / 6.0;
            double num7 = (double)(num3 + num4 + num5) * num6;
            double num8 = (double)num3 - num7;
            double num9 = (double)num4 - num7;
            double num10 = (double)num5 - num7;
            double num11 = x - num8;
            double num12 = y - num9;
            double num13 = z - num10;
            int num14, num15, num16, num17, num18, num19;
            if (num11 >= num12)
            {
                if (num12 >= num13) { num14 = 1; num15 = 0; num16 = 0; num17 = 1; num18 = 1; num19 = 0; }
                else if (num11 >= num13) { num14 = 1; num15 = 0; num16 = 0; num17 = 1; num18 = 0; num19 = 1; }
                else { num14 = 0; num15 = 0; num16 = 1; num17 = 1; num18 = 0; num19 = 1; }
            }
            else if (num12 < num13) { num14 = 0; num15 = 0; num16 = 1; num17 = 0; num18 = 1; num19 = 1; }
            else if (num11 < num13) { num14 = 0; num15 = 1; num16 = 0; num17 = 0; num18 = 1; num19 = 1; }
            else { num14 = 0; num15 = 1; num16 = 0; num17 = 1; num18 = 1; num19 = 0; }
            double num20 = num11 - (double)num14 + num6;
            double num21 = num12 - (double)num15 + num6;
            double num22 = num13 - (double)num16 + num6;
            double num23 = num11 - (double)num17 + 2.0 * num6;
            double num24 = num12 - (double)num18 + 2.0 * num6;
            double num25 = num13 - (double)num19 + 2.0 * num6;
            double num26 = num11 - 1.0 + 3.0 * num6;
            double num27 = num12 - 1.0 + 3.0 * num6;
            double num28 = num13 - 1.0 + 3.0 * num6;
            int num29 = num3 & 0xFF;
            int num30 = num4 & 0xFF;
            int num31 = num5 & 0xFF;
            int g32 = mod[num29 + perm[num30 + perm[num31]]] * 3;
            int g33 = mod[num29 + num14 + perm[num30 + num15 + perm[num31 + num16]]] * 3;
            int g34 = mod[num29 + num17 + perm[num30 + num18 + perm[num31 + num19]]] * 3;
            int g35 = mod[num29 + 1 + perm[num30 + 1 + perm[num31 + 1]]] * 3;
            var G_ = G;
            double num36 = 0.6 - num11 * num11 - num12 * num12 - num13 * num13;
            double num37;
            if (num36 < 0.0) num37 = 0.0;
            else { num36 *= num36; num37 = num36 * num36 * (G_[g32] * num11 + G_[g32 + 1] * num12 + G_[g32 + 2] * num13); }
            double num38 = 0.6 - num20 * num20 - num21 * num21 - num22 * num22;
            double num39;
            if (num38 < 0.0) num39 = 0.0;
            else { num38 *= num38; num39 = num38 * num38 * (G_[g33] * num20 + G_[g33 + 1] * num21 + G_[g33 + 2] * num22); }
            double num40 = 0.6 - num23 * num23 - num24 * num24 - num25 * num25;
            double num41;
            if (num40 < 0.0) num41 = 0.0;
            else { num40 *= num40; num41 = num40 * num40 * (G_[g34] * num23 + G_[g34 + 1] * num24 + G_[g34 + 2] * num25); }
            double num42 = 0.6 - num26 * num26 - num27 * num27 - num28 * num28;
            double num43;
            if (num42 < 0.0) num43 = 0.0;
            else { num42 *= num42; num43 = num42 * num42 * (G_[g35] * num26 + G_[g35 + 1] * num27 + G_[g35 + 2] * num28); }
            return 32.0 * (num37 + num39 + num41 + num43);
        }

        /// Bit-exact comparison against the game's NoiseS3D on n random points (2D and 3D), plus timing.
        public static string Check(int n)
        {
            if (Get() == null) NoiseS3D.Noise(0.1, 0.2); // let the game set up its permutation
            bool was = Enabled; Enabled = true;
            var rnd = new System.Random(7);
            long mism = 0; string first = "";
            for (int i = 0; i < n; i++)
            {
                double s = i % 4 == 0 ? 1e-3 : i % 4 == 1 ? 1 : i % 4 == 2 ? 1000 : 1e6;
                double x = (rnd.NextDouble() - 0.5) * s, y = (rnd.NextDouble() - 0.5) * s, z = (rnd.NextDouble() - 0.5) * s;
                if (i % 97 == 0) { x = System.Math.Round(x); z = System.Math.Round(z); } // whole numbers: fastfloor quirk
                double a2 = NoiseS3D.Noise(x, y), b2 = Noise2(x, y), a3 = NoiseS3D.Noise(x, y, z), b3 = Noise3(x, y, z);
                if (System.BitConverter.DoubleToInt64Bits(a2) != System.BitConverter.DoubleToInt64Bits(b2) ||
                    System.BitConverter.DoubleToInt64Bits(a3) != System.BitConverter.DoubleToInt64Bits(b3))
                { mism++; if (first.Length == 0) first = $"({x},{y},{z}) 2D {a2:R}/{b2:R} 3D {a3:R}/{b3:R}"; }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew(); double sink = 0;
            for (int i = 0; i < 200000; i++) sink += NoiseS3D.Noise(i * 0.37, 12345.0, i * 0.11);
            double tOrig = sw.Elapsed.TotalMilliseconds; sw.Restart();
            for (int i = 0; i < 200000; i++) sink += Noise3(i * 0.37, 12345.0, i * 0.11);
            double tFast = sw.Elapsed.TotalMilliseconds;
            Enabled = was;
            return "{\"checked\":" + n + ",\"mismatches\":" + mism + ",\"first\":" + Json.Str(first) + ",\"orig3dNs\":" + (tOrig * 1e6 / 200000).ToString("F0") +
                   ",\"fast3dNs\":" + (tFast * 1e6 / 200000).ToString("F0") + ",\"sink\":" + (sink != 0 ? 1 : 0) + ",\"patched\":" + Patched + "}";
        }
    }
}
