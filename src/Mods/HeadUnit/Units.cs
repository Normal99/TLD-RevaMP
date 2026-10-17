using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HeadUnit
{
    /// The head unit models as game items. Each is the game's own radio (radio01) cloned, with a screen added and its
    /// own item id: the game saves, loads and spawns it like any item, and TLD Revamp shares it with the car it sits in
    /// (every player has this mod, which the join checks). The game's radio inside keeps working.
    ///
    /// Which cars take which model: a model carries its own tag, which no slot has, and the game's fit check
    /// (partslotscript.CanCraft) is extended: a head unit fits an empty slot that takes the game's radio when the car
    /// the slot is on is one of the model's. Checked on the live car, so cars already in the world and in saves take
    /// it too (playtest 2026-10-14: tagging the car prefabs' slots left the cars in the world unable to take one).
    /// The car's own radios still fit as before.
    ///
    /// Loot: the models join the game's radio loot (category 29, radio01 and radio02 at 100 each) at 160 together, so
    /// a head unit is found a little less often than a radio (tools/lootodds.py, 2026-10-15: at 12 together a head
    /// unit was 17 times rarer than a radio; asked for: a little rarer than a radio, not by an insane amount).
    [HarmonyPatch]
    public static class Units
    {
        public sealed class Model
        {
            public string Key, Name;
            public int Id;                 // item id (set at registration)
            public string[] Cars;          // itemdatabase.i names of the cars it fits
            public int LootWeight;         // against 100 for each of the game's radios
            public Color Body, Screen;
            public string Tag => "headunit." + Key;
        }

        /// The models. Ids are counted from FirstId: far above the game's own (about 400 today), so a game update that
        /// adds items doesn't take them. Never renumber: saves store the id.
        public const int FirstId = 9100;
        public static readonly Model[] Models =
        {
            new Model { Key = "dh1", Name = "Voltline DH-1", Cars = new[] { "car01", "car02", "car02k", "car03", "car03k" }, LootWeight = 80,
                        Body = new Color(0.12f, 0.12f, 0.13f), Screen = new Color(0.25f, 0.6f, 1f) },
            new Model { Key = "dh2", Name = "Voltline DH-2 Wide", Cars = new[] { "car04", "car05", "car06", "car06p", "car07" }, LootWeight = 53,
                        Body = new Color(0.18f, 0.18f, 0.2f), Screen = new Color(0.3f, 1f, 0.6f) },
            new Model { Key = "rx", Name = "Kestrel RX Pro", Cars = new[] { "car08", "car08c", "car09", "car09P", "car09T", "car10", "car10k", "car11", "bus01", "bus02", "bus03" }, LootWeight = 27,
                        Body = new Color(0.08f, 0.08f, 0.08f), Screen = new Color(1f, 0.55f, 0.2f) },
        };

        public static Model ById(int id) { foreach (var m in Models) if (m.Id == id) return m; return null; }

        private static GameObject _holder;   // inactive: our prefabs live under it, so they never run
        public static bool Registered;
        public static string RadioTipus = "";
        private static string[] _radioTipus = new string[0];

        [HarmonyPatch(typeof(itemdatabase), "Awake")]
        [HarmonyPostfix]
        private static void AfterDatabase(itemdatabase __instance)
        {
            try { Register(__instance); }
            catch (Exception e) { Plugin.Log.LogError("registering the head units failed: " + e); }
        }

        private static void Register(itemdatabase db)
        {
            var radio = db.items != null && db.items.Length > (int)itemdatabase.i.radio01 ? db.items[(int)itemdatabase.i.radio01] : null;
            var radioAtt = radio != null ? radio.GetComponent<attachablescript>() : null;
            if (radioAtt == null) { Plugin.Log.LogError("the game's radio01 item has no part script: head units not registered"); return; }
            RadioTipus = string.Join(",", radioAtt.tipus ?? new string[0]);
            _radioTipus = radioAtt.tipus ?? new string[0];

            int last = FirstId + Models.Length - 1;
            if (db.items.Length <= last) Array.Resize(ref db.items, last + 1);
            if (_holder == null) { _holder = new GameObject("HeadUnitPrefabs"); _holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_holder); }

            for (int i = 0; i < Models.Length; i++)
            {
                var m = Models[i];
                m.Id = FirstId + i;
                var prefab = UnityEngine.Object.Instantiate(radio, _holder.transform);
                prefab.name = "radio_headunit_" + m.Key;   // the dev menu's spawn list shows and searches this name
                var save = prefab.GetComponent<tosaveitemscript>();
                if (save != null)
                {
                    save.id = m.Id;
                    save.searchtags = new List<string>(save.searchtags ?? new List<string>()) { "radios", "headunits", "head units", "screens", m.Name.ToLowerInvariant() };
                }
                var att = prefab.GetComponent<attachablescript>();
                att.tipus = new[] { m.Tag };          // fits only slots that carry this model's tag
                att.maintipus = new string[0];
                // stuck anywhere with the game's glue key (Glue.cs), never into a slot that way; parented to what it's
                // stuck on, as a part in a slot (the game removes its body; TLD Revamp then sends no positions for it)
                att.onlyToSlot = false;
                att.ignoreSlots = true;
                att.parentAtAttach = true;
                if (prefab.GetComponent<UnitBehaviour>() == null) prefab.AddComponent<UnitBehaviour>();
                Look.Dress(prefab, m);
                db.items[m.Id] = prefab;

                Plugin.Log.LogInfo($"{m.Name}: item {m.Id}, fits {string.Join(", ", m.Cars)}");
            }

            // loot: the radio category, the game's radios weighted 100 each
            var cat = db.categories != null && db.categories.Length > 29 ? db.categories[29] : null;
            if (cat != null && cat.items != null && Array.IndexOf(cat.items, Models[0].Id) < 0)
            {
                var items = new List<int>(cat.items);
                var chances = new List<int>();
                for (int k = 0; k < cat.items.Length; k++) chances.Add(cat.itemchances != null && k < cat.itemchances.Length ? cat.itemchances[k] : 100);
                foreach (var m in Models) { items.Add(m.Id); chances.Add(m.LootWeight); }
                cat.items = items.ToArray(); cat.itemchances = chances.ToArray();
                Plugin.Log.LogInfo($"head units added to the radio loot ({string.Join(", ", chances)})");
            }
            Registered = true;
        }

        [HarmonyPatch(typeof(partslotscript), nameof(partslotscript.CanCraft))]
        [HarmonyPostfix]
        private static void Fits(partslotscript __instance, attachablescript PP, ref bool __result)
        {
            if (__result || PP == null || PP.tipus == null || PP.tipus.Length != 1) return;
            Model m = null;
            foreach (var x in Models) if (PP.tipus[0] == x.Tag) { m = x; break; }
            if (m == null || __instance.tipus == null || !TakesRadio(__instance.tipus)) return;
            if (!__instance.attachTarget && __instance.hasPart()) return;
            var car = __instance.GetComponentInParent<carscript>();
            var save = car != null ? car.GetComponent<tosaveitemscript>() : null;
            if (save == null) return;
            string name = ((itemdatabase.i)save.id).ToString();
            if (name.EndsWith("full", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            foreach (var c in m.Cars) if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) { __result = true; return; }
        }

        internal static bool TakesRadio(string[] slotTipus)
        {
            foreach (var t in _radioTipus) if (Array.IndexOf(slotTipus, t) >= 0) return true;
            return false;
        }
    }
}
