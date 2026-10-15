using UnityEngine;

namespace TLDRevamp.Net
{
    /// The game's shared objects as the modding API sees them (MpApi.WorldObjectId / FindWorldObject / ShareWorldObject):
    /// a mod points at a car or an item by its network id, which is the same on every machine.
    public static partial class Entities
    {
        /// The network id of a shared group root on or above go (a car, an item), or 0.
        internal static uint NetIdOf(GameObject go)
        {
            if (go == null) return 0;
            for (var t = go.transform; t != null; t = t.parent)
            {
                var s = t.GetComponent<tosaveitemscript>();
                if (s == null) continue;
                foreach (var e in ByNet.Values) if (e.Root == s) return e.NetId;
            }
            return 0;
        }

        internal static GameObject GameObjectOf(uint net) =>
            ByNet.TryGetValue(net, out var e) && e.Root != null ? e.Root.gameObject : null;

        /// Share the topmost saveable object on or above go, with its group. True if it is shared or on its way.
        internal static bool ShareGameObject(GameObject go)
        {
            if (go == null || !InSession) return false;
            tosaveitemscript top = null;
            for (var t = go.transform; t != null; t = t.parent) { var s = t.GetComponent<tosaveitemscript>(); if (s != null) top = s; }
            if (top == null) return false;
            string res = top.car != null ? ShareCar(top) : ShareItem(top, null);
            return !res.Contains("\"error\"");
        }
    }
}
