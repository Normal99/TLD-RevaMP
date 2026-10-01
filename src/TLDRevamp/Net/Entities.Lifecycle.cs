using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Entity lifecycle: deletion sync and spawn-time sharing.
    ///
    /// Deletion: destroying a shared item removes it for everyone. The destroyer broadcasts RemoveItem; receivers
    /// destroy their copies with the echo suppressed — every machine's own tosaveitemscript.OnDestroy removes the
    /// item from ITS save, so records stay clean on all sides. Detached parts are entities of their own and go out
    /// as their own RemoveItem.
    ///
    /// Spawn sharing: a session spawn of a root item (mainscript.Spawn — spawnpile, the game's own creation paths)
    /// is shared at once, so the other players see it without anyone having to sit in it first. Streaming placements
    /// of existing save records don't come through this path, and the lease capture dedups against it.
    public static partial class Entities
    {
        /// Set while we destroy copies on purpose (a received RemoveItem): the OnDestroy hooks must not echo.
        public static bool SuppressDestroyEcho;
        public static long DestroyTraces;

        [HarmonyLib.HarmonyPatch(typeof(tosaveitemscript), "OnDestroy")]
        private static class ItemDestroyed
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tosaveitemscript __instance)
            {
                if (SuppressDestroyEcho || Mp.SceneTearingDown || !InSession || ByNet.Count == 0) return;
                // streaming-out also destroys the GameObject (the record moves to the far store) — only a REAL
                // deletion (no far-store record) is broadcast; otherwise every shared car driving out of range
                // would be deleted from the whole world
                var ddata = savedatascript.s != null ? savedatascript.s.data : null;
                if (ddata != null && ddata.itemData != null && savedatascript.IndexOfID(ddata.itemData.items, __instance.idInSave, out _)) return;
                foreach (var e in ByNet.Values)
                {
                    if (e.Root != __instance) continue;   // children/parts ride their group; only roots are entities
                    if (DestroyTraces++ < 3)
                        Plugin.Log.LogInfo($"shared item destroyed: {__instance.name} (net {e.NetId})\n" + UnityEngine.StackTraceUtility.ExtractStackTrace());
                    W.Reset(); W.U8(RemoveItem); W.U32(e.NetId);
                    ToServer(W, true);
                    ByNet.Remove(e.NetId);
                    return;
                }
            }
        }

        /// The server relays the removal to everyone (the destroyer already cleaned itself), drops the entity and
        /// its detached-part entities, and destroys its own live copy — its OnDestroy cleans the host's save.
        internal static void ServerRemoveItem(int from, uint net)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            // rights: any ready peer may remove — refusing the sync would recreate exactly the desync this
            // message exists to fix (the item is already gone on the destroyer's machine). Griefing concerns are
            // a server-policy question for later; the core syncs reality.
            Server.Remove(net);
            if (se.Parts != null)
                foreach (var pn in new List<uint>(se.Parts)) Server.Remove(pn);
            W.Reset(); W.U8(RemoveItem); W.U32(net);
            ServerSendAll(W, true, from);   // everyone but the destroyer (already clean) — incl. the host's own copy (Deliver)
        }

        private static void DestroyLocalCopy(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            if (e.Root == null)
            {
                // dormant: no live copy here — drop the far-store record too, or the item respawns on the next visit
                RemoveRecord(e.RootId);
                ByNet.Remove(net);
                return;
            }
            foreach (var it in e.Items) if (it != null) UnityEngine.Object.Destroy(it.gameObject);
            UnityEngine.Object.Destroy(e.Root.gameObject);   // its OnDestroy removes it from this machine's save
            ByNet.Remove(net);
        }

        private static void ApplyRemoveItem(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            SuppressDestroyEcho = true;
            try { DestroyLocalCopy(net); }
            finally { SuppressDestroyEcho = false; }
        }

        private static void RemoveRecord(uint id)
        {
            try { savedatascript.s.items.Remove(id); } catch { }
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data != null && data.itemData != null && savedatascript.IndexOfID(data.itemData.items, id, out int k))
                data.itemData.items.RemoveAt(k);
        }

        /// The local player picks up the nearest pickupable (the game's own Pickup — the claim hook fires for proxies).
        public static string PickupNearest(float radius)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            pickupable best = null; float bd = radius * radius;
            foreach (var pk in UnityEngine.Object.FindObjectsOfType<pickupable>())
            {
                if (pk.pickedUp || !pk.gameObject.activeInHierarchy) continue;
                var d = (pk.transform.position - pl.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = pk; }
            }
            if (best == null) return "{\"error\":\"nothing to pick up\"}";
            if (pl.transform.root.name.Contains("Player")) { }   // the pickup target may be anywhere near
            pl.Teleport(best.transform.position + Vector3.up * 0.5f);
            best.Pickup();
            LastPicked = best;
            return "{\"picked\":" + Json.Str(best.name) + "}";
        }

        /// Test/bridge hook: the E-press path on a car — the game's pickup interaction on its pickupable component
        /// (the same call a player's enter/flip press makes). With the car-claim guard this must NOT flip ownership.
        public static string PickupCar(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var pk = e.Root.GetComponent<pickupable>();
            if (pk == null) pk = e.Root.GetComponentInChildren<pickupable>();
            if (pk == null) return "{\"error\":\"the car has no pickupable\"}";
            pk.Pickup();
            return "{\"picked\":" + Json.Str(e.Root.name) + "}";
        }

        /// A session spawn of a root item is shared at once — the others see it without anyone sitting in it first.
        /// Every mainscript.Spawn overload: the dev menu's item spawner uses the colour/condition/random-type ones
        /// (kaposztaleves.Spawn → mainscript.Spawn(g, color, worn, rtype, paint)), which the first two hooks missed —
        /// dev-menu spawns (a gun, a car) existed on one machine only. Postfix on the outer call: the item is finished
        /// (colour, condition, random type applied) when it's captured.
        [HarmonyLib.HarmonyPatch]
        private static class SpawnShare
        {
            [HarmonyLib.HarmonyTargetMethods]
            private static IEnumerable<System.Reflection.MethodBase> Targets()
            {
                foreach (var m in typeof(mainscript).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (m.Name == nameof(mainscript.Spawn) && m.ReturnType == typeof(GameObject)) yield return m;
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(GameObject __result) => ShareSpawn(__result);
        }

        /// Test/bridge hook: remove a shared entity the way the game's own deletion does (JustDestroy →
        /// OnDestroy → the RemoveItem broadcast).
        public static string DeleteShared(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            RemoveRecord(e.Root.idInSave);   // a real deletion: the far-store record goes too, so OnDestroy broadcasts
            e.Root.JustDestroy();
            return "{\"deleted\":" + net + "}";
        }

        public static long SpawnShared;
        internal static pickupable LastPicked;

        private static void ShareSpawn(GameObject go)
        {
            if (!InSession || go == null || Mp.SceneTearingDown) return;
            var it = go.GetComponent<tosaveitemscript>();
            if (it == null) it = go.GetComponentInChildren<tosaveitemscript>();
            if (it == null) return;
            foreach (var e in ByNet.Values) if (e.Root == it) return;   // already shared (spawn auto-share, lease capture)
            if (AttachedToItem(it)) return;                             // rides its parent item
            if (it.transform.parent != null && it.transform.parent.GetComponentInParent<tosaveitemscript>() != null) return;
            ShareItem(it, null);
            SpawnShared++;
        }
    }
}
