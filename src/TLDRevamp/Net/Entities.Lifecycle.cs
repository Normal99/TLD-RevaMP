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
        public static int StoreOutTrace;   // test: log the next N shared roots streamed out (where, why — the call stack)

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
                if (ddata != null && ddata.itemData != null && savedatascript.IndexOfID(ddata.itemData.items, __instance.idInSave, out _))
                {
                    if (AnomalyWatch)
                        foreach (var ea in ByNet.Values) if (ea.Root == __instance) { AnomalyStoreOut(ea, __instance); break; }
                    if (StoreOutTrace > 0)
                        foreach (var e0 in ByNet.Values)
                        {
                            if (e0.Root != __instance) continue;
                            if (!e0.Proxy) break;   // copies only (the owner's own streaming is the game's)
                            StoreOutTrace--;
                            var gp = mainscript.GlobalFromUnityPos(__instance.transform.position);
                            double best = double.MaxValue;
                            if (menuhandler.s != null && menuhandler.s.currentMainMap != null)
                                foreach (var ga in menuhandler.s.currentMainMap.genArounds) best = System.Math.Min(best, (mainscript.GlobalFromUnityPos(ga.upos) - gp).magnitude);
                            Plugin.Log.LogInfo($"[storeout] {__instance.name} net {e0.NetId} proxy {e0.Proxy} at {gp.x:F1},{gp.y:F1},{gp.z:F1} nearest player {best:F0} m (remove beyond " +
                                               $"{(itemPlaceRemoveScript.s != null ? itemPlaceRemoveScript.s.itemRemoveDist : -1)}) frame {Time.frameCount}\n" + System.Environment.StackTrace);
                            break;
                        }
                    return;
                }
                bool byPlayer = _playerDeleted.Remove(__instance) && PlayerDeleteSync;
                NoteMemberDestroyed(__instance);   // a part of an object we own: the others lose it too (Entities.MemberGone)
                if (byPlayer) RequestMemberGone(__instance);   // a part of someone else's: its owner deletes it
                foreach (var e in ByNet.Values)
                {
                    if (e.Root != __instance) continue;   // children/parts ride their group; only roots are entities
                    if (byPlayer && (e.Proxy || e.OwnerId != MyId))
                    {
                        // the local player deleted another player's object: a deletion, as the owner's would be
                        W.Reset(); W.U8(RemoveItem); W.U32(e.NetId);
                        ToServer(W, true);
                        ByNet.Remove(e.NetId);
                        DestroyMembers(e, __instance);   // as the others destroy theirs (DestroyLocalCopy)
                        PlayerDeletesSent++;
                        return;
                    }
                    // placed again in the same frame (Fixes.StreamRace): a new object holds the id — the entity follows it
                    if (savedatascript.s.items.TryGetValue(__instance.idInSave, out var now) && now != null && now != __instance)
                    {
                        e.Root = null; e.RootId = __instance.idInSave; Replaced++;
                        return;
                    }
                    // only the owner deletes a shared object. A copy this machine's game destroyed on its own (streaming
                    // races, a part far from its car taking the whole car with it) is this machine's loss alone: before
                    // v0.65.4 it went out as a deletion and the owner's car vanished from the world (bug 10, 2026-10-03:
                    // the friend's game dropped the host's car, net 2). The copy is fetched again from the server.
                    if (e.Proxy || e.OwnerId != MyId)
                    {
                        ByNet.Remove(e.NetId);
                        CopiesLost++;
                        // the members it let go (a crate's loot) would stay as frozen copies nobody moves, next to the
                        // fresh ones the refetch or the owner's re-share brings
                        if (ReshareSurvivors)
                        {
                            SuppressDestroyEcho = true;
                            try
                            {
                                for (int i = 0; i < e.Items.Count; i++)
                                    // children too: the crate's mountStuff lets its loot go in its own OnDestroy, maybe after this one
                                    if (e.Items[i] != null && e.Items[i] != __instance && !CameOff(e, i))
                                    { UnityEngine.Object.Destroy(e.Items[i].gameObject); OrphansCleared++; }
                            }
                            finally { SuppressDestroyEcho = false; }
                        }
                        float now2 = Time.realtimeSinceStartup;
                        bool again = _refetchedAt.TryGetValue(e.NetId, out float last) && now2 - last < 30f;
                        Plugin.Log.LogWarning($"copy lost here: {__instance.name} (net {e.NetId}, owner {e.OwnerId}) — {(again ? "lost again within 30 s, not fetched" : "fetched again")}");
                        if (again) return;
                        _refetchedAt[e.NetId] = now2;
                        W.Reset(); W.U8(Refetch); W.U32(e.NetId);
                        ToServer(W, true);
                        return;
                    }
                    if (DestroyTraces++ < 3)
                        Plugin.Log.LogInfo($"shared item destroyed: {__instance.name} (net {e.NetId})\n" + UnityEngine.StackTraceUtility.ExtractStackTrace());
                    W.Reset(); W.U8(RemoveItem); W.U32(e.NetId);
                    ToServer(W, true);
                    ByNet.Remove(e.NetId);
                    // what the object held lives on: a crate's loot (mountStuff.OnDestroy unstores it), parts lying in a
                    // box. The receivers destroy the whole group with the object (DestroyLocalCopy) — the survivors go out
                    // again as objects of their own next frame, once the game has let them go (playtest 2026-10-09:
                    // "crates are not synced" — the loot was on the breaker's machine only)
                    if (ReshareSurvivors)
                        for (int i = 0; i < e.Items.Count; i++)
                            if (e.Items[i] != null && e.Items[i] != __instance) _reshare.Add(e.Items[i]);
                    return;
                }
            }
        }

        /// The dev menu's delete (dev mode: X on what the player looks at, cheatedStuff.Delete — every item has one) destroys the
        /// object on this machine whoever simulates it. Only the owner's deletion went out: a copy deleted here counted as lost
        /// to streaming (fetched again; a part that came off a car isn't fetched, so it was gone here only), a part of another
        /// player's car deleted here stayed on everyone else's car (playtest 2026-10-18: "deleting previously owned wheel ...
        /// on other client"). Now the player's delete goes out as a deletion: an object as RemoveItem (which the server takes
        /// from anyone), a member of another player's object as MemberGone, which the server passes to its owner.
        public static bool PlayerDeleteSync = true;   // A/B: false = before v0.69.2
        public static long PlayerDeletesSent, MemberDeletesSent, MemberDeletesForwarded, MemberDeletesApplied;
        private static readonly HashSet<tosaveitemscript> _playerDeleted = new HashSet<tosaveitemscript>();

        [HarmonyLib.HarmonyPatch(typeof(cheatedStuff), nameof(cheatedStuff.Delete))]
        private static class PlayerDelete
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(cheatedStuff __instance)
            {
                if (!InSession || __instance == null) return;
                var ts = __instance.GetComponent<tosaveitemscript>();
                if (ts == null) return;
                if (_playerDeleted.Count > 64) _playerDeleted.RemoveWhere(x => x == null);   // destroyed without an OnDestroy here
                _playerDeleted.Add(ts);   // Destroy is deferred: its OnDestroy (ItemDestroyed) comes at the end of the frame
            }
        }

        /// The local player deleted a member of another player's object: its owner deletes it (ApplyMemberGone).
        private static void RequestMemberGone(tosaveitemscript it)
        {
            foreach (var e in ByNet.Values)
            {
                if (e.Root == it || !e.Proxy || e.Items == null) continue;
                int idx = e.Items.IndexOf(it);
                if (idx < 0 || (e.Split != null && e.Split.Contains(idx))) continue;
                W.Reset(); W.U8(MemberGone); W.U32(e.NetId); W.VarU32((uint)idx);
                ToServer(W, true);
                MemberDeletesSent++;
                return;
            }
        }

        /// A member that came off the object (its own shared object since: PartDetached), or rides one that did (the tyre on a
        /// wheel taken off). The game deletes an object with what is in its hierarchy; these are elsewhere, objects of their own.
        /// Before v0.69.2 deleting a car deleted, on every other machine, each part that had ever come off it — a wheel bolted
        /// onto another car since, a tyre lying in the grass — while the deleter kept them.
        public static bool KeepPartsOnRemove = true;   // A/B: false = before v0.69.2
        public static long PartsKeptOnRemove, PartsOrphaned, PartsOrphanedNoRecord;
        private static bool CameOff(Ent e, int i)
        {
            if (!KeepPartsOnRemove || e.Split == null || e.Items == null) return false;
            if (e.Split.Contains(i)) return true;
            var it = e.Items[i];
            if (it == null) return false;
            foreach (int j in e.Split)
                if (j != i && j < e.Items.Count && e.Items[j] != null && it.transform.IsChildOf(e.Items[j].transform)) return true;
            return false;
        }

        /// Everything of the group but the root (going with its own Destroy) and what came off it.
        private static void DestroyMembers(Ent e, tosaveitemscript root)
        {
            bool was = SuppressDestroyEcho;
            SuppressDestroyEcho = true;
            try
            {
                for (int i = 0; i < e.Items.Count; i++)
                {
                    var it = e.Items[i];
                    if (it == null || it == root) continue;
                    if (CameOff(e, i)) { PartsKeptOnRemove++; continue; }
                    UnityEngine.Object.Destroy(it.gameObject);
                }
            }
            finally { SuppressDestroyEcho = was; }
        }

        /// Server: the object is gone; the parts that came off it are not — objects of their own in everyone's world, still
        /// sending states. Kept, and given a record (the host's copy, as it is now) so that a player who gets them later can
        /// make them: before, a part only ever travelled with its car (SendObject). Not loaded on the host: kept without one.
        private static void OrphanPart(SEnt car, SEnt part)
        {
            PartsOrphaned++;
            if (!ByNet.TryGetValue(part.NetId, out var pe) || !Resolve(pe) || pe.Root == null) { PartsOrphanedNoRecord++; return; }
            part.Record = CaptureRecord(pe.Root);
            part.PartIndex = -1;
            part.Where = part.HasState ? part.Pos : mainscript.GlobalFromUnityPos(pe.Root.transform.position);
            foreach (var kv in Known) if (kv.Value.Contains(car.NetId)) kv.Value.Add(part.NetId);   // they have it already
        }

        public static bool ReshareSurvivors = true;   // A/B: false = before v0.66.2 (crates.py)
        public static long Reshared, OrphansCleared;
        private static readonly List<tosaveitemscript> _reshare = new List<tosaveitemscript>();

        /// Per frame: survivors of a deleted group (above), the owner's gib of a break (BreakOut) — each still here and
        /// loose is shared as a new object.
        private static void ReshareTick()
        {
            if (_reshare.Count == 0) return;
            var list = new List<tosaveitemscript>(_reshare);
            _reshare.Clear();
            foreach (var it in list)
            {
                if (it == null || it.gameObject == null) continue;
                bool known = false;
                foreach (var e in ByNet.Values) if (e.Root == it || (e.Items != null && e.Items.Contains(it))) { known = true; break; }
                if (known || AttachedToItem(it)) continue;
                if (it.transform.parent != null && it.transform.parent.GetComponentInParent<tosaveitemscript>() != null) continue;
                ShareItem(it, null);
                Reshared++;
            }
        }

        internal static void QueueReshare(tosaveitemscript it) { if (it != null) _reshare.Add(it); }

        public const byte Refetch = 58;
        public static long Replaced, CopiesLost, Refetched;
        private static readonly Dictionary<uint, float> _refetchedAt = new Dictionary<uint, float>();

        /// A player lost its copy of a shared object: it gets the object again as it is now (as on first sight).
        internal static void ServerRefetch(int from, uint net)
        {
            if (!Server.TryGetValue(net, out var se) || se.PartIndex >= 0) return;
            Refetched++;
            SendObject(from, se);
        }

        /// The server relays the removal to everyone (the destroyer already cleaned itself), drops the entity and
        /// its detached-part entities, and destroys its own live copy — its OnDestroy cleans the host's save.
        internal static void ServerRemoveItem(int from, uint net)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            SessionLog.Event("remove-relayed", net, from: from, epoch: se.Epoch);
            // rights: any ready peer may remove — refusing the sync would recreate exactly the desync this
            // message exists to fix (the item is already gone on the destroyer's machine). Griefing concerns are
            // a server-policy question for later; the core syncs reality.
            Server.Remove(net);
            if (se.Parts != null)
                foreach (var pn in new List<uint>(se.Parts))
                {
                    if (!Server.TryGetValue(pn, out var part)) continue;
                    if (KeepPartsOnRemove) OrphanPart(se, part);
                    else Server.Remove(pn);
                }
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
            DestroyMembers(e, e.Root);   // not what came off it (KeepPartsOnRemove)
            UnityEngine.Object.Destroy(e.Root.gameObject);   // its OnDestroy removes it from this machine's save
            ByNet.Remove(net);
        }

        private static void ApplyRemoveItem(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            SessionLog.Event("remove-applied", net, to: MyId, epoch: e.Epoch);
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
