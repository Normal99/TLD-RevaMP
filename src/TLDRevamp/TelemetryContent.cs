using UnityEngine;

namespace TLDRevamp
{
    internal static class TelemetryContent
    {
        private static GameObject[] _items, _pois;
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
                foreach (var child in prefab.GetComponentsInChildren<Transform>(true)) TelemetryPrivacy.ContentName(child.name);
                foreach (var slot in prefab.GetComponentsInChildren<partslotscript>(true))
                    if (slot.tipus != null) foreach (string type in slot.tipus) TelemetryPrivacy.ContentName(type);
            }
            previous = prefabs;
        }
    }
}
