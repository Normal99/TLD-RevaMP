using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Why does a stored car arrive with parts loose beside it? (farworld runs 10 and 12, phase B: a player arriving at
    /// a site whose records came from the host — 5–16 parts of two cars lay unmounted next to them, the same records
    /// mounted fine on the host.) Logs every attachment the game fails to restore from a record (save_attachable.Load:
    /// the parent not found, a slot index out of range) and every part that leaves its slot (attachablescript.Detach)
    /// with its caller — first Max of each.
    public static class AttachLog
    {
        public static bool Enabled = true;
        public static int Max = 40;
        public static long LoadFailed, Detached;

        [HarmonyPatch(typeof(save_attachable), nameof(save_attachable.Load),
            new[] { typeof(attachablescript), typeof(int), typeof(int), typeof(uint), typeof(int), typeof(Vector3d), typeof(int), typeof(Vector3d), typeof(Vector3d) })]
        private static class LoadPatch
        {
            [HarmonyPostfix]
            private static void Postfix(attachablescript _script, int _indexType, int _attachType, uint _parentid, int _index)
            {
                if (!Enabled || _script == null || _script.attached || _attachType == 0) return;
                LoadFailed++;
                if (LoadFailed > Max) return;
                string parent = "?";
                if (_attachType == 1)
                {
                    bool loaded = savedatascript.s.items.TryGetValue(_parentid, out var it) && it != null;
                    bool stored = savedatascript.IndexOfID(savedatascript.s.data.itemData.items, _parentid, out _);
                    parent = $"parent {_parentid} loaded {loaded} stored {stored}" + (loaded ? $" {it.name} slots {it.partslotscripts.Count}" : "");
                }
                Plugin.Log.LogWarning($"attach load failed #{LoadFailed}: {_script.name} type {_attachType} index type {_indexType} index {_index} {parent} " +
                                      $"at global {mainscript.GlobalFromUnityPos(_script.transform.position).ToString()}");
            }
        }

        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class DetachPatch
        {
            [HarmonyPrefix]
            private static void Prefix(attachablescript __instance)
            {
                if (!Enabled || __instance == null || !__instance.attached || __instance.slot == null) return;
                Detached++;
                if (Detached > Max) return;
                var st = new System.Diagnostics.StackTrace(2, false);
                var calls = new System.Text.StringBuilder();
                for (int i = 0; i < st.FrameCount && i < 6; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m != null) calls.Append(m.DeclaringType?.Name).Append('.').Append(m.Name).Append(" < ");
                }
                Plugin.Log.LogWarning($"part off #{Detached}: {__instance.name} from {__instance.slot.name} at global " +
                                      $"{mainscript.GlobalFromUnityPos(__instance.transform.position).ToString()} t {Time.time:F1} via {calls}");
            }
        }

        public static string Stats => "{\"loadFailed\":" + LoadFailed + ",\"detached\":" + Detached + "}";
    }
}
