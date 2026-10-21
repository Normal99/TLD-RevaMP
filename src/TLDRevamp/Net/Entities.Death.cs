using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// A/B: share the corpse that the game creates in fpscontroller.Death2. False leaves it local as before; the
        /// limb transport itself is already implemented in Entities.Ragdoll.
        public static bool DeathRagdollShare = true;
        public static long DeathCorpsesShared, DeathCorpseShareMissed;
        /// How many corpses were un-shared again (the corpse of a death that ended: the game's own RagdolReset
        /// replaces the corpse object but never destroys the old one, so without this every death leaked one shared
        /// entity and one orphan body per machine).
        public static long DeathCorpsesReleased;
        /// How many times the game's own Death was called again for a death that already happened: the corpse was
        /// already shared, so it is the same corpse (not a second one).
        public static long DeathCorpseDuplicates;

        /// The corpse of the last death this machine made, with the exact ids a test needs: the game's own save id
        /// (the id the host and the joiner agree on) and the network id (the id the share got).
        public static uint DeathCorpseSaveId, DeathCorpseNetId;
        /// The save ids of the things the death spawned and shared (the test's own items), in order. The save id
        /// is the id every machine agrees on; the network id comes later (the host answers the share), so it is
        /// read when the diagnostics are made, not when the thing is spawned.
        public static readonly System.Collections.Generic.List<uint> DeathItemSaveIds = new System.Collections.Generic.List<uint>();
        /// The network ids of the same things, in the same order (0 while the share is still pending).
        public static readonly System.Collections.Generic.List<uint> DeathItemNetIds = new System.Collections.Generic.List<uint>();

        /// The corpse object of the death that is currently open on this machine (the one this machine shared). It is
        /// released when the death ends, so the next death does not leave the previous body behind.
        private static GameObject _deathCorpse;
        private static uint _deathCorpseNet;

        /// The network id of a shared object by its save id (0 while the share is still pending).
        internal static uint NetIdOfSaveId(uint saveId)
        {
            if (saveId == 0) return 0;
            foreach (var e in ByNet.Values) if (e.RootId == saveId) return e.NetId;
            return 0;
        }

        /// The diagnostics of the last death this machine made (bridge `mp deathinfo`): the exact ids of the corpse
        /// and of the things this death spawned, and the counters that say what happened. A test asserts these
        /// exact numbers, not "an item nearby".
        public static string DeathInfo()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"corpseSaveId\":").Append(DeathCorpseSaveId).Append(",\"corpseNetId\":").Append(DeathCorpseNetId);
            sb.Append(",\"corpsesShared\":").Append(DeathCorpsesShared).Append(",\"corpsesMissed\":").Append(DeathCorpseShareMissed);
            sb.Append(",\"corpsesReleased\":").Append(DeathCorpsesReleased);
            sb.Append(",\"corpsesDuplicated\":").Append(DeathCorpseDuplicates);
            sb.Append(",\"itemIds\":[");
            for (int i = 0; i < DeathItemSaveIds.Count; i++)
            {
                if (i > 0) sb.Append(',');
                // the network id is resolved now: the host answers the share a moment after the spawn
                uint net = NetIdOfSaveId(DeathItemSaveIds[i]);
                if (net == 0 && i < DeathItemNetIds.Count) net = DeathItemNetIds[i];
                sb.Append('[').Append(DeathItemSaveIds[i]).Append(',').Append(net).Append(']');
            }
            sb.Append("],\"death\":").Append(SessionRules.DeathStats());
            sb.Append('}');
            return sb.ToString();
        }

        /// A new session: the death's ids belong to the session that made them. The corpse of the last death is
        /// released first: it is a shared entity, and the session's teardown has already removed the other shared
        /// objects, so the corpse would otherwise be left behind.
        internal static void DeathReset()
        {
            ReleaseDeathCorpse();
            DeathCorpseSaveId = 0; DeathCorpseNetId = 0;
            DeathItemSaveIds.Clear(); DeathItemNetIds.Clear();
            _deathCorpseNet = 0;
        }

        /// Release the corpse of the death that just ended. The game's own Revive calls RagdolReset, which replaces
        /// the corpse object with a fresh one and never destroys the old one: without this the world gained one
        /// orphan body and one shared entity per death, and the death-screen camera of the next death was inside the
        /// previous corpse's collider.
        private static void ReleaseDeathCorpse()
        {
            if (_deathCorpse == null) { _deathCorpseNet = 0; return; }
            var go = _deathCorpse;
            _deathCorpse = null;
            _deathCorpseNet = 0;
            // the shared entity goes first: the others' copies of the corpse go with it (the RemoveItem broadcast).
            uint net = 0;
            foreach (var e in ByNet.Values)
                if (e.Root != null && (e.Root.transform == go.transform || e.Root.transform.IsChildOf(go.transform))) { net = e.NetId; break; }
            if (net != 0)
            {
                try { DeleteShared(net); }
                catch (System.Exception e) { Plugin.Log.LogWarning("death corpse: release failed: " + e.Message); }
                // the entity may be dormant here (the body streamed out): the removal still has to go out, or the
                // others keep the corpse for good.
                if (ByNet.ContainsKey(net))
                {
                    try
                    {
                        W.Reset(); W.U8(RemoveItem); W.U32(net);
                        ToServer(W, true);
                        ByNet.Remove(net);
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning("death corpse: removal failed: " + e.Message); }
                }
            }
            // the local body: the game's own object, destroyed so it stops lying there and stops being counted.
            try { UnityEngine.Object.Destroy(go); } catch { }
            DeathCorpsesReleased++;
        }

        /// A/B: share the corpse that the game creates in fpscontroller.Death2. False leaves it local as before; the
        /// limb transport itself is already implemented in Entities.Ragdoll.
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]
        private static class ShareRealDeathRagdoll
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance)
            {
                if (!DeathRagdollShare || !InSession || __instance == null || mainscript.s == null || __instance != mainscript.s.player) return;
                var rag = __instance.spawnedRagdol;
                if (rag == null || !__instance.died || rag.GetComponent<tosaveitemscript>() == null)
                {
                    if (__instance.died) DeathCorpseShareMissed++;
                    return;
                }
                var ts = rag.GetComponent<tosaveitemscript>();
                long before = SpawnShared;
                ShareSpawn(rag);
                if (SpawnShared > before)
                {
                    DeathCorpsesShared++;
                    DeathCorpseSaveId = ts != null ? ts.idInSave : 0;
                    DeathCorpseNetId = NetIdOf(rag);   // 0 while the host has not answered the share yet
                    _deathCorpse = rag;
                    _deathCorpseNet = DeathCorpseNetId;
                }
                else
                {
                    // The corpse was already shared. That is the normal case for a DUPLICATE death: the game's own
                    // SpawnRagdol re-uses the corpse object (only RagdolReset makes a new one), so the second Death
                    // call shares the same corpse and the share is refused as "already shared". The corpse's ids are
                    // kept (they are the same corpse's), and the duplicate is counted as such.
                    bool known = false;
                    foreach (var e in ByNet.Values) if (e.Root != null && (e.Root.transform == rag.transform || e.Root.transform.IsChildOf(rag.transform))) { known = true; break; }
                    if (!known) DeathCorpseShareMissed++;
                    else
                    {
                        DeathCorpseSaveId = ts != null ? ts.idInSave : 0;
                        DeathCorpseNetId = NetIdOf(rag);
                        _deathCorpse = rag;
                        _deathCorpseNet = DeathCorpseNetId;
                        DeathCorpseDuplicates++;   // the same corpse, shared once: a duplicate death, not a second corpse
                    }
                }
            }
        }

        /// The respawn ended the death: the corpse of that death goes away (it was the corpse of a moment, not a
        /// permanent object), so the next death starts with a clean world and the one-record budget holds.
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Revive))]
        private static class ReleaseCorpseOnRevive
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance)
            {
                if (!InSession || __instance == null || mainscript.s == null || __instance != mainscript.s.player) return;
                ReleaseDeathCorpse();
            }
        }

        /// The game's own Death, called again for a death that already happened (the duplicate path). The game's
        /// Death is not idempotent, so the mod must not treat the second call as a new death: no second anchor, no
        /// second corpse, no second record. The test calls this while the death screen is open.
        public static string DeathAgain()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (!InSession) return "{\"error\":\"join a multiplayer world first\"}";
            if (!pl.died && (mainscript.s == null || !mainscript.s.died)) return "{\"error\":\"not dead\"}";
            string beforeStats = SessionRules.DeathStats();   // the stats are the observable, not a copy of it
            long corpsesBefore = DeathCorpsesShared;
            try { pl.Death(); }
            catch (System.Exception e) { return "{\"error\":\"" + e.Message + "\"}"; }
            return "{\"died\":true,\"corpsesBefore\":" + corpsesBefore + ",\"corpsesAfter\":" + DeathCorpsesShared +
                   ",\"statsBefore\":" + beforeStats + ",\"statsAfter\":" + SessionRules.DeathStats() + "}";
        }

        /// Put one thing in the player's hands and the rest in the inventory slots, the way the game does. The
        /// game's own Pickup only ever takes the first thing into the hands (pl.pickedUp is single), so a test
        /// that spawned two things and called Pickup twice really carried one and left the other lying on the
        /// ground: the "the carried things drop at the anchor" assertion then passed for the wrong reason.
        private static void Carry(fpscontroller pl, pickupable pk)
        {
            if (pl.pickedUp == null) { pl.Pickup(pk); return; }
            if (pl.inventory == null) return;
            for (int i = 0; i < pl.inventory.Count; i++)
            {
                if (pl.inventory[i] != null) continue;
                if (!pk.canInventory) return;   // it cannot be carried: it stays where it is (and the test says so)
                pl.inventory[i] = pk;
                pk.transform.parent = pl.transform;
                try { pk.SyncInventory(); } catch { }
                return;
            }
        }

        /// Deterministic two-machine test: optional item prefab ids are spawned at the player and picked
        /// up through the game's own Pickup before the player's body is put into a known fling. Then the game's public
        /// Death runs, and the normal hooks capture the anchor and share the corpse.
        public static string DeathFling(float vx, float vy, float vz, int[] prefabs = null)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (!InSession) return "{\"error\":\"join a multiplayer world first\"}";
            if (pl.died || (mainscript.s != null && mainscript.s.died)) return "{\"error\":\"already dead\"}";
            try
            {
                if (kaposztaleves.s != null) { kaposztaleves.s.settings.god = false; kaposztaleves.s.settings.fly = false; }
            }
            catch { }
            pl.godModeTillGrounded = false;
            var v = new Vector3(vx, vy, vz);
            if (!Finite(v)) return "{\"error\":\"non-finite velocity\"}";
            var before = mainscript.GlobalFromUnityPos(pl.transform.position);
            var spawned = new System.Collections.Generic.List<int>();
            // this death's own things: the ids a test asserts on, not "any item nearby"
            DeathItemSaveIds.Clear(); DeathItemNetIds.Clear();
            if (prefabs != null)
                foreach (int id in prefabs)
                {
                    if (id < 0 || itemdatabase.s == null || id >= itemdatabase.s.items.Length || itemdatabase.s.items[id] == null) continue;
                    var go = UnityEngine.Object.Instantiate(itemdatabase.s.items[id], pl.transform.position + pl.transform.forward * 0.8f + Vector3.up, pl.transform.rotation);
                    var ts = go.GetComponent<tosaveitemscript>();
                    if (ts != null) { ts.FStart(); ShareSpawn(go); DeathItemSaveIds.Add(ts.idInSave); DeathItemNetIds.Add(NetIdOf(go)); }
                    var pk = go.GetComponent<pickupable>();
                    if (pk != null) Carry(pl, pk);
                    spawned.Add(id);
                }
            // A seated player has no rigidbody of their own (the game's GetIn destroys it and parents the player to
            // the seat), so a fling would silently do nothing. The game's own Respawn gets the player out of the
            // seat; here the death itself is what the test asks for, so the fling is only applied when there is a
            // body to fling. The report says what really happened.
            bool flung = false;
            if (pl.RB != null) { pl.RB.isKinematic = false; pl.RB.velocity = v; pl.RB.angularVelocity = Vector3.zero; pl.RB.WakeUp(); flung = true; }
            pl.Death();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var ids = new System.Text.StringBuilder();
            // [save id, network id] pairs, as `mp deathinfo` writes them (the old "save:net" was not JSON: every reply
            // with carried things failed to parse)
            for (int i = 0; i < DeathItemSaveIds.Count; i++) { if (i > 0) ids.Append(','); ids.Append('[').Append(DeathItemSaveIds[i]).Append(',').Append(DeathItemNetIds[i]).Append(']'); }
            return "{\"died\":" + (pl.died ? "true" : "false") + ",\"flung\":" + (flung ? "true" : "false") + ",\"seated\":" + (pl.Bsitting ? "true" : "false") +
                   ",\"velocity\":[" + v.x.ToString("F3", ic) + "," + v.y.ToString("F3", ic) + "," + v.z.ToString("F3", ic) +
                   "],\"before\":[" + before.x.ToString("F3", ic) + "," + before.y.ToString("F3", ic) + "," + before.z.ToString("F3", ic) + "],\"spawned\":[" + string.Join(",", spawned) +
                   "],\"itemIds\":[" + ids + "],\"corpseSaveId\":" + DeathCorpseSaveId + ",\"corpseNetId\":" + DeathCorpseNetId +
                   "," + SessionRules.DeathStats().TrimStart('{');
        }
    }
}
