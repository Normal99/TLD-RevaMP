using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// Tests (bridge `call TLDRevamp.LootLab.Odds "39,267,9100" scene|prefabs`): how many of each item the game's loot
    /// spawners (newRandomStuffSpawnScript) are expected to make, from their own chances and the item categories
    /// (itemdatabase.categories, sub-categories included) — no luck involved, so a rare item's rate needs no thousands
    /// of buildings. scene: the spawners in the world loaded now; prefabs: every spawner in memory that isn't in the
    /// world (each building type's once).
    public static class LootLab
    {
        private static readonly FieldInfo CiField = typeof(newRandomStuffSpawnScript).GetField("ci", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static string Odds(string ids, string where)
        {
            var db = itemdatabase.s;
            if (db == null) return "no itemdatabase";
            var want = new List<int>();
            foreach (var t in ids.Split(',')) if (int.TryParse(t.Trim(), out int v)) want.Add(v);
            bool scene = where != "prefabs";
            var expected = new Dictionary<int, double>();
            int spawners = 0;
            var memo = new Dictionary<int, Dictionary<int, double>>();
            foreach (var s in Resources.FindObjectsOfTypeAll<newRandomStuffSpawnScript>())
            {
                if (s == null || s.items == null || s.items.Length == 0) continue;
                if (s.gameObject.scene.IsValid() != scene) continue;
                spawners++;
                int ci = CiField != null ? (int)CiField.GetValue(s) : 0;
                bool cumulative = ci > 0;   // InitSpawn ran on it: chances added up in place
                var w = new double[s.items.Length];
                double total = 0, prev = 0;
                for (int i = 0; i < s.items.Length; i++)
                {
                    double c = s.items[i].chance;
                    w[i] = cumulative ? c - prev : (c == 0 ? 1 : c);
                    prev = c; total += w[i];
                }
                if (total <= 0) continue;
                double spawn = Mathf.Clamp01(s.chanceToSpawn);
                for (int i = 0; i < s.items.Length; i++)
                {
                    var it = s.items[i];
                    double p = spawn * w[i] / total;
                    if (p <= 0 || it.G != null || string.IsNullOrEmpty(it.prefab)) continue;
                    if (it.isitem)
                    {
                        if (Enum.TryParse(it.prefab, out itemdatabase.i iid)) Add(expected, (int)iid, p);
                    }
                    else if (Enum.TryParse(it.prefab, out itemdatabase.k k))
                        foreach (var kv in Category((int)k, memo, 0)) Add(expected, kv.Key, p * kv.Value);
                }
            }
            var parts = new List<string>();
            foreach (var id in want)
            {
                expected.TryGetValue(id, out double e);
                string name = id >= 0 && id < db.items.Length && db.items[id] != null ? db.items[id].name : "?";
                parts.Add($"{id} {name} {e.ToString("F3", CultureInfo.InvariantCulture)}");
            }
            return $"{spawners} spawners ({where}): " + string.Join("; ", parts);
        }

        private static void Add(Dictionary<int, double> d, int id, double p) { d.TryGetValue(id, out double v); d[id] = v + p; }

        /// The chance of each item from category k (itemdatabase.selectId: items and sub-categories, by their chances).
        private static Dictionary<int, double> Category(int k, Dictionary<int, Dictionary<int, double>> memo, int depth)
        {
            if (memo.TryGetValue(k, out var done)) return done;
            var r = new Dictionary<int, double>();
            var cats = itemdatabase.s.categories;
            if (k < 0 || k >= cats.Length || cats[k] == null || depth > 8) return r;
            var c = cats[k];
            double ni = 0, nc = 0;
            if (c.items != null) for (int i = 0; i < c.items.Length; i++) ni += c.itemchances != null && i < c.itemchances.Length ? c.itemchances[i] : 1;
            if (c.categories != null) for (int i = 0; i < c.categories.Length; i++) nc += c.categorychances != null && i < c.categorychances.Length ? c.categorychances[i] : 1;
            double all = ni + nc;
            if (all <= 0) return r;
            if (c.items != null)
                for (int i = 0; i < c.items.Length; i++)
                    Add(r, c.items[i], (c.itemchances != null && i < c.itemchances.Length ? c.itemchances[i] : 1) / all);
            if (c.categories != null)
                for (int i = 0; i < c.categories.Length; i++)
                {
                    double pc = (c.categorychances != null && i < c.categorychances.Length ? c.categorychances[i] : 1) / all;
                    foreach (var kv in Category(c.categories[i], memo, depth + 1)) Add(r, kv.Key, pc * kv.Value);
                }
            memo[k] = r;
            return r;
        }
    }
}
