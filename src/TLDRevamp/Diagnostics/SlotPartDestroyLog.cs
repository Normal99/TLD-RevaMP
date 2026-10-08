using HarmonyLib;

namespace TLDRevamp.Diagnostics
{
    /// A part destroyed while it sits in a part slot, with the caller (busdrive.py: a bus copy's two rear wheels — slots on
    /// BusBack — went missing on the joining player's machine some time after the copy was built, with no Detach and no
    /// failed load: the slot drops a destroyed part from its list on its own). First Max, bridge `slotdestroys`.
    [HarmonyPatch(typeof(tosaveitemscript), "OnDestroy")]
    public static class SlotPartDestroyLog
    {
        public static int Max = 20;
        public static long Count;
        public static string Log = "";

        [HarmonyPrefix]
        private static void Prefix(tosaveitemscript __instance)
        {
            if (__instance == null || Net.Mp.SceneTearingDown || !Net.Entities.InSession) return;
            var at = __instance.attachable;
            if (at == null || !at.attached || at.slot == null) return;
            var e = Net.Entities.SharedGroupOf(__instance);   // only parts of shared objects: a join clears dozens of local ones
            if (e == null) return;
            Count++;
            if (Count > Max) return;
            bool stored = false;
            try { var d = savedatascript.s.data.itemData; stored = d != null && savedatascript.IndexOfID(d.items, __instance.idInSave, out _); } catch { }
            var line = $"slotted part destroyed #{Count}: {__instance.name} ({__instance.idInSave}) in {at.slot.name} on {at.slot.transform.root.name}" +
                       $" (slot owner {(at.slot.tosaveitem != null ? at.slot.tosaveitem.name + " " + at.slot.tosaveitem.idInSave : "-")}), far-store record {stored}," +
                       $" frame {UnityEngine.Time.frameCount}, entity {e.NetId} proxy {e.Proxy} owner {e.OwnerId}, group root alive {(e.Root != null)}," +
                       $" item index {e.Items.IndexOf(__instance)}";
            if (Log.Length < 12000) Log += line + "\n";
            Plugin.Log.LogWarning(line);
        }
    }
}
