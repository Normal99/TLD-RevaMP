using UnityEngine;

namespace TLDRevamp
{
    internal static class TelemetryContent
    {
        private static GameObject[] _items, _pois;
        internal static string ItemName(int prefab, string fallback = "")
        {
            var items = itemdatabase.s?.items;
            var go = items != null && prefab >= 0 && prefab < items.Length ? items[prefab] : null;
            return TelemetryPrivacy.ObjectName(go != null ? go.name : fallback);
        }
        internal static void Refresh()
        {
            var db = itemdatabase.s;
            if (db == null) return;
            Read(db.items, ref _items);
            Read(db.pois, ref _pois);
        }
        private static void Read(GameObject[] prefabs, ref GameObject[] previous)
        {
            if (prefabs == null || ReferenceEquals(prefabs, previous)) return;
            foreach (var prefab in prefabs) if (prefab != null)
            {
                TelemetryPrivacy.ContentName(prefab.name);
                foreach (var child in prefab.GetComponentsInChildren<Transform>(true)) TelemetryPrivacy.ContentName(child.name);
                foreach (var slot in prefab.GetComponentsInChildren<partslotscript>(true))
                    if (slot.tipus != null) foreach (string type in slot.tipus) TelemetryPrivacy.ContentName(type);
            }
            previous = prefabs;
        }
    }
}
