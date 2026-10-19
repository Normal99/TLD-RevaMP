using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// A removed receiver mod leaves a trailer record naming a slot beyond the stock car's list. The game's
    /// save_attachable.Load fallback welds it to the car without a slot. Keep the loaded trailer loose instead.
    [HarmonyPatch(typeof(save_attachable), nameof(save_attachable.Load),
        new[] { typeof(attachablescript), typeof(int), typeof(int), typeof(uint), typeof(int), typeof(Vector3d), typeof(int), typeof(Vector3d), typeof(Vector3d) })]
    public static class MissingTrailerSlot
    {
        public static bool Enabled = true;
        public static long Detached;

        [HarmonyPrefix]
        internal static bool Prefix(attachablescript _script, int _indexType, int _attachType, uint _parentid, int _index)
        {
            if (!Enabled || _script == null || _script.utanfuto == null || _indexType != 2 || _attachType != 1 || _index < 0 ||
                itemPlaceRemoveScript.s == null || !itemPlaceRemoveScript.s.GetOne(_parentid, out var parent) ||
                parent == null || parent.car == null || parent.partslotscripts == null || _index < parent.partslotscripts.Count) return true;
            if (_script.attached) _script.Detach();
            _script.Crafted(false);
            if (_script.tosave != null && _script.tosave.P != null)
            {
                _script.tosave.P.crafted = false;
                _script.tosave.P.Crafted();
            }
            _script.tempDontMoveAtAttach = false;
            Detached++;
            return false;
        }
    }
}
