using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CarTrailer
{
    /// Saves made before the empty trailer (version 0.1.1 and older) carry an extra set of wheels for every load: the
    /// trailer loaded with its own wheels on and the saved ones went on top. When a car trailer's wheel slot gets a
    /// second wheel, the first stays and the others are removed, with whatever is on them, a moment later (the tyres
    /// and rims on a wheel load after it). Only in single player and on a host: a player who joined gets the host's.
    [HarmonyPatch(typeof(partslotscript), nameof(partslotscript.Craft))]
    internal static class ExtraWheels
    {
        private static void Postfix(partslotscript __instance)
        {
            try
            {
                if (__instance.parts == null || __instance.parts.Count < 2 || !__instance.name.Contains("Wheel")) return;
                var owner = __instance.transform.parent != null ? __instance.transform.parent.GetComponentInParent<tosaveitemscript>() : null;
                if (owner == null || owner.id != Plugin.ItemId || Plugin.LegacyForTest || Joined()) return;
                if (__instance.isActiveAndEnabled) __instance.StartCoroutine(Later(__instance));
                else Trim(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError("checking a car trailer's wheels failed: " + e); }
        }

        private static IEnumerator Later(partslotscript slot)
        {
            yield return new WaitForSeconds(2f);
            if (slot != null) Trim(slot);
        }

        private static void Trim(partslotscript slot)
        {
            slot.hasPart();   // drops parts already gone
            int removed = 0;
            while (slot.parts.Count > 1)
            {
                var p = slot.parts[slot.parts.Count - 1];
                slot.parts.RemoveAt(slot.parts.Count - 1);
                if (p == null) continue;
                foreach (var s in p.GetComponentsInChildren<tosaveitemscript>(true)) s.JustDestroy();
                removed++;
            }
            if (removed > 0) Plugin.Log.LogInfo($"car trailer: removed {removed} extra wheel(s) from {slot.name} (a save from before the empty trailer)");
        }

        // TLD Revamp's multiplayer, read without depending on it (this mod works without its API): in a session, not the host
        private static PropertyInfo _inSession, _isHost;
        private static bool _looked;
        private static bool Joined()
        {
            if (!_looked)
            {
                _looked = true;
                var api = Type.GetType("TLDRevamp.Multiplayer.MpApi, TLDRevamp.Multiplayer");
                _inSession = api?.GetProperty("InSession", BindingFlags.Public | BindingFlags.Static);
                _isHost = api?.GetProperty("IsHost", BindingFlags.Public | BindingFlags.Static);
            }
            return _inSession != null && _isHost != null && (bool)_inSession.GetValue(null) && !(bool)_isHost.GetValue(null);
        }
    }
}
