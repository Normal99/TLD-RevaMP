using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A player changes something on an object another machine owns — opens a door, pours fuel in, sprays paint. The
    /// owner is the one whose state counts (resync), so the change goes to the owner as that item's state record; the
    /// owner applies it and its next resync pass (brought forward) shows it to everyone. The places where a change is
    /// made by a player are the ones the game itself marks for its official MP (usablescript.SyncMulti(true),
    /// tankscript.SyncMulti, partconditionscript.Refresh(multiUpd), colorscript.SColor, …): those run whether or not
    /// the official MP is connected, so they're hooked here. On one's own objects the same hooks bring the resync pass
    /// forward, so a door opened by its owner is seen at once, not up to 3 s later.
    public static partial class Entities
    {
        public const byte Edit = 32;
        public static long EditsSent, EditsApplied, EditsRejected, EditsMarked;
        private static readonly Dictionary<tosaveitemscript, float> EditDirty = new Dictionary<tosaveitemscript, float>();
        private static float _editFlushAt;
        private static bool _applyingState;
        private static readonly List<tosaveitemscript> _editFlushed = new List<tosaveitemscript>();
        private const float EditHold = 1.5f;   // an editor ignores the owner's older state of that item this long

        public static string EditMarks = "";

        /// What an edit carries (playtest 2026-10-09: radiator caps "untouchable by both players"). An edit sent the
        /// item's whole state; a copy's tank, its cap open, changes on its own (the fluid flows here as on the owner's
        /// machine) and the game marks it for sync about six times a second — each of those edits carried the copy's
        /// stale caps along, and the owner's own close of a cap was undone within a second (capsprobe.py: 22 edits in
        /// 3.5 s, the host's cap back open). Now an edit carries the kinds of lists that were changed and the owner
        /// applies only those; a copy's tank change is an edit only right after something poured into it (a player
        /// filling it: tankcapscript.Filling).
        public const int KindUsable = 1, KindTank = 2, KindPart = 4, KindColor = 8, KindFood = 16, KindAmmo = 32, KindPlate = 64, KindBusDoor = 128, KindBlood = 256, KindAll = 0xFFFF;
        public static bool ScopedEdits = true;   // A/B: false = before v0.66.2 (whole item, every copy tank change)
        public static long CopyTankSimSkipped;
        private static readonly Dictionary<tosaveitemscript, int> EditKinds = new Dictionary<tosaveitemscript, int>();
        private static readonly Dictionary<tankscript, float> _tankFilledAt = new Dictionary<tankscript, float>();

        private static int KindBit(string kind)
        {
            switch (kind)
            {
                case "usable": return KindUsable; case "tank": return KindTank; case "part": return KindPart; case "color": return KindColor;
                case "food": return KindFood; case "ammo": return KindAmmo; case "plate": return KindPlate; case "busdoor": return KindBusDoor;
                case "blood": return KindBlood;
                default: return KindAll;
            }
        }

        private static void MarkEdit(tosaveitemscript it, bool fromTank = false, string kind = "")
        {
            if (EditMarks.Length < 400) EditMarks += kind + (it == null ? ":null" : IsProxy(it) ? ":proxy" : ":own") + (_applyingState ? ":applying" : "") + " ";
            if (!InSession || _applyingState || it == null) return;
            var group = EditEntityOf(it);
            if (group != null && group.Proxy)
            {
                EditDirty[it] = Time.realtimeSinceStartup; EditsMarked++;
                EditKinds.TryGetValue(it, out int k); EditKinds[it] = k | (ScopedEdits ? KindBit(kind) : KindAll);
                return;
            }
            if (fromTank) return;   // own tanks change all the time (fuel burn): the regular pass is enough
            foreach (var e in ByNet.Values)
                if (e == group && !e.Proxy) { e.ResyncAcc = Mathf.Max(e.ResyncAcc, ResyncInterval - 0.2f); return; }
        }

        private static tosaveitemscript ItemOf(Component c, uint id, bool hasId)
        {
            if (hasId && savedatascript.s != null && savedatascript.s.items.TryGetValue(id, out var it) && it != null) return it;
            return c != null ? c.GetComponentInParent<tosaveitemscript>() : null;
        }

        /// Per frame: dirty items on display copies go to their owners (at most every 0.15 s: a door being dragged).
        private static void EditTick()
        {
            if (EditDirty.Count == 0 || Time.realtimeSinceStartup < _editFlushAt) return;
            _editFlushAt = Time.realtimeSinceStartup + 0.15f;
            _editFlushed.Clear();
            foreach (var kv in EditDirty)
            {
                var it = kv.Key;
                if (it != null && LiveItemMoving(it)) continue;
                _editFlushed.Add(it);
                if (it == null) continue;
                var group = EditEntityOf(it);
                foreach (var e in ByNet.Values)
                {
                    if (e != group || !e.Proxy || e.Items == null) continue;
                    int idx = e.Items.IndexOf(it);
                    if (idx < 0) continue;
                    int kinds = EditKinds.TryGetValue(it, out int kk) ? kk : KindAll;
                    // the car record only for a wash of its blood (Entities.Blood): the owner takes nothing else of it
                    byte[] rec = StateRecord(ItemSnapshot.Capture(it), CanonMap(e.Items), out _, withCar: kinds != KindAll && (kinds & KindBlood) != 0);
                    if ((kinds & KindTank) != 0) CopyTanksAreNow(it);   // our fill: the owner's record of it may come while we hold it off
                    W.Reset(); W.U8(Edit); W.U32(e.NetId); W.F64(SessionLog.St); W.VarU32((uint)idx); W.VarU32((uint)kinds); W.VarU32((uint)rec.Length); W.Bytes(rec, 0, rec.Length);
                    ToServer(W, true);
                    if (e.EditHoldUntil == null) e.EditHoldUntil = new Dictionary<int, float>();
                    e.EditHoldUntil[idx] = Time.realtimeSinceStartup + EditHold;
                    EditsSent++;
                    SessionLog.Event("edit-sent", e.NetId, item: idx, from: MyId, to: e.OwnerId, count: kinds, epoch: e.Epoch);
                    break;
                }
            }
            foreach (var it in _editFlushed) { EditDirty.Remove(it); EditKinds.Remove(it); }
        }

        /// Server: to the owner — from a player at the object (EditRangeM, the host counts it from the last state or its
        /// record) or seated in it or in the car it came off. Any player could otherwise change anything anywhere in the
        /// world (empty a tank, open every door) by naming its net id. What the edit changes is checked too, and how
        /// often a player edits (Entities.EditCheck).
        public static bool EditRangeCheck = true;
        public static float EditRangeM = 15f;
        public static long EditsOutOfReach;
        private static void ServerEdit(int from, NetReader r)
        {
            uint net = r.U32();
            double stamp = r.F64();
            if (!Finite(stamp) || Math.Abs(SessionLog.St - stamp) > 10) { SessionLog.Event("edit-refused", net, from: from, result: "timestamp"); EditsRejected++; return; }
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) { SessionLog.Event("edit-refused", net, from: from, result: "missing entity or already owner"); EditsRejected++; return; }
            if (EditValidation && from != 0 && !TakeToken(_editBuckets, from, EditRatePerS, EditBurst))
            {
                EditsRejected++;
                Refuse(ref EditRefusedRate, "edit rate", $"edit from player {from}: over {EditRatePerS:F0} a second");
                SessionLog.Event("edit-refused", net, from: from, to: se.OwnerId, result: "edit rate");
                return;
            }
            if (EditRangeCheck && from != 0 && !AtObject(from, se))
            {
                EditsRejected++;
                Refuse(ref EditsOutOfReach, "edit reach", $"edit from player {from}: they aren't at net {net}");
                SessionLog.Event("edit-refused", net, from: from, to: se.OwnerId, result: "edit reach");
                return;
            }
            int idx = (int)r.VarU32(), kinds = (int)r.VarU32(), len = (int)r.VarU32();
            if (r.Bad || len < 0 || len > r.Remaining || idx < 0) { SessionLog.Event("edit-refused", net, item: idx, from: from, result: "malformed record"); EditsRejected++; return; }
            if (EditValidation && from != 0)
            {
                var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len);
                string why = CheckEdit(se, idx, kinds, rec);
                if (why != null)
                {
                    SessionLog.Event("edit-refused", net, item: idx, from: from, to: se.OwnerId, result: why, count: kinds);
                    EditsRejected++;
                    if (why.StartsWith("kinds")) Refuse(ref EditRefusedKinds, "edit kinds", $"edit from player {from} of net {net} item {idx}: {why}");
                    else Refuse(ref EditRefusedValues, "edit values", $"edit from player {from} of net {net} item {idx} (kinds {kinds}): {why}");
                    return;
                }
            }
            if ((kinds & KindUsable) != 0) SetLiveRecordFloor(new UsableKey { Net = net, Epoch = se.Epoch, Item = idx }, stamp);
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        private static bool AtObject(int player, SEnt se)
        {
            se = EditReachEntity(se);
            uint seat = RemotePlayers.SeatNetOf(player);
            if (seat != 0 && (seat == se.NetId || (se.ParentNet != 0 && seat == se.ParentNet))) return true;
            if (Mp.Server == null || !Mp.Server.PeerPos(player, out var p)) return false;
            double dx = p.x - se.Where.x, dy = p.y - se.Where.y, dz = p.z - se.Where.z;
            return dx * dx + dy * dy + dz * dz <= (double)EditRangeM * EditRangeM;
        }

        /// Owner: apply, and bring the resync pass forward so everyone sees it.
        private static void ApplyEdit(NetReader r)
        {
            uint net = r.U32(); double stamp = r.F64(); int idx = (int)r.VarU32(), kinds = (int)r.VarU32(), len = (int)r.VarU32();
            if (r.Bad || len < 0 || len > r.Remaining || idx < 0 || !ByNet.TryGetValue(net, out var e) || e.Proxy || !Resolve(e) || idx >= e.Items.Count || e.Items[idx] == null)
            { SessionLog.Event("edit-refused", net, item: idx, to: MyId, result: "malformed, missing item or no longer owner"); EditsRejected++; return; }
            var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len);
            var d = RecordCodec.Decode(rec);
            if (d == null) { SessionLog.Event("edit-refused", net, item: idx, to: MyId, result: "record decode failed"); EditsRejected++; return; }
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) map[CanonBase + (uint)i] = e.Items[i].idInSave;
            ItemSnapshot.Remap(d, map);
            bool useUsables = LiveRecordUsables(e, idx, stamp);
            if ((kinds & KindUsable) != 0) LiveRecordApplied(e, idx, stamp);
            ApplyState(e.Items[idx], d, kinds, useUsables);
            e.ResyncAcc = Mathf.Max(e.ResyncAcc, ResyncInterval - 0.05f);
            EditsApplied++;
            SessionLog.Event("edit-applied", net, item: idx, to: MyId, count: kinds, epoch: e.Epoch);
        }

        // ------------------------------------------------------------------ the game's "a player changed this" points

        [HarmonyLib.HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class EditUsable
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(usablescript __instance, bool syncinmulti)
            { if (syncinmulti && InSession && !__instance.setidPoi && !MarkLiveUsable(__instance)) MarkEdit(ItemOf(__instance, __instance.tosaveid, __instance.setid), kind: "usable"); }
        }

        [HarmonyLib.HarmonyPatch(typeof(tankscript), nameof(tankscript.SyncMulti))]
        private static class EditTank
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tankscript __instance)
            {
                if (!InSession || __instance.setpoi || _displayPour) return;
                var it = ItemOf(__instance, __instance.tosaveid, __instance.setid);
                // a copy's own simulation (its fluid flowing out of an open cap) is the owner's too: only a fill is an edit
                if (ScopedEdits && it != null && IsProxy(it) && !(_tankFilledAt.TryGetValue(__instance, out float at) && Time.realtimeSinceStartup - at < 2f))
                { CopyTankSimSkipped++; return; }
                MarkEdit(it, fromTank: true, kind: "tank");
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(tankcapscript), nameof(tankcapscript.Filling))]
        private static class TankFilled
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tankcapscript __instance)
            {
                if (!InSession || __instance.Tank == null) return;
                PouredHere(__instance.Tank);
            }
        }

        /// A player here pours into or out of a tank. On a copy every such change goes to the owner, also one too small
        /// for the game's own sync mark (SyncMulti only past syncTankChangeLimit), and a can filled from a copy's barrel
        /// takes the fluid from the owner's barrel too: a copy's tank is put back to the owner's record otherwise
        /// (Entities.CopyFluids).
        private static void PouredHere(tankscript t)
        {
            if (_tankFilledAt.Count > 256) _tankFilledAt.Clear();
            _tankFilledAt[t] = Time.realtimeSinceStartup;
            if (CopyFluidsFollowOwner && !t.setpoi && IsProxy(t)) MarkEdit(ItemOf(t, t.tosaveid, t.setid), fromTank: true, kind: "tank");
        }

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.DrinkFill))]
        private static class PouredFromHand
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(tankscript t1, out float __state) { __state = t1 != null && t1.F != null ? t1.F.GetAmount() : 0f; }
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tankscript t1, float __state)
            { if (InSession && t1 != null && t1.F != null && t1.F.GetAmount() != __state) PouredHere(t1); }   // the source: the cap filled is Filling's
        }

        [HarmonyLib.HarmonyPatch(typeof(tubescript), nameof(tubescript.Fill))]
        private static class PouredThroughTube
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(tankcapscript t2, out float __state) { __state = t2 != null && t2.Tank != null && t2.Tank.F != null ? t2.Tank.F.GetAmount() : 0f; }
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tankcapscript t2, float __state)
            { if (InSession && t2 != null && t2.Tank != null && t2.Tank.F != null && t2.Tank.F.GetAmount() != __state) PouredHere(t2.Tank); }
        }

        [HarmonyLib.HarmonyPatch(typeof(partconditionscript), nameof(partconditionscript.Refresh), new[] { typeof(bool) })]
        private static class EditPartCondition
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(partconditionscript __instance, bool multiUpd)
            { if (multiUpd && InSession) MarkEdit(__instance.tosave != null ? __instance.tosave : ItemOf(__instance, 0, false), kind: "part"); }   // children too: the game's own MP skips them, so a repaired child part never synced there
        }

        [HarmonyLib.HarmonyPatch(typeof(colorscript), nameof(colorscript.SColor))]
        private static class EditColor
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(colorscript __instance)
            {
                if (!InSession) return true;
                MarkEdit(__instance.tosave, kind: "color");
                return false;   // the game's body sends to the official MP unconditionally (syncScript.s may not exist)
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(ediblescript), nameof(ediblescript.UpdMulti))]
        private static class EditFood
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(ediblescript __instance)
            {
                if (!InSession) return;
                if (SpawnFoodNoEdit && __instance == _foodStarting) { FoodStartsSkipped++; return; }
                MarkEdit(__instance.tosave, kind: "food");
            }
        }

        /// A food item's Start shows its bites (UpdParts) and "sends" them when syncScript.IsHostOrSingle() — true on every
        /// machine here (the game's own MP isn't connected). So every food copy that came into a joiner's world was an edit
        /// to its owner: lobby3.py, P2 at the join: 10 edits sent, the host refused 5 (P2 not at them: "they aren't at net
        /// N"); a joiner within 15 m had its copy's bites written over the owner's and the owner's state of it ignored for
        /// EditHold. Nobody changed anything: no edit (A/B: false = before).
        public static bool SpawnFoodNoEdit = true;
        public static long FoodStartsSkipped;
        private static ediblescript _foodStarting;

        [HarmonyLib.HarmonyPatch(typeof(ediblescript), "Start")]
        private static class FoodStart
        {
            [HarmonyLib.HarmonyPrefix] private static void Prefix(ediblescript __instance) { _foodStarting = __instance; }
            [HarmonyLib.HarmonyFinalizer] private static void Finalizer() { _foodStarting = null; }
        }

        [HarmonyLib.HarmonyPatch(typeof(ammoscript), nameof(ammoscript.SendAmmoMulti))]
        private static class EditAmmo { [HarmonyLib.HarmonyPostfix] private static void Postfix(ammoscript __instance) { if (InSession) MarkEdit(__instance.tosave, kind: "ammo"); } }

        [HarmonyLib.HarmonyPatch(typeof(rendszamscript), nameof(rendszamscript.SendMulti))]
        private static class EditPlate
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(rendszamscript __instance) { if (!InSession) return true; MarkEdit(__instance.tosave, kind: "plate"); return false; }
        }

        [HarmonyLib.HarmonyPatch(typeof(busdoorscripts), nameof(busdoorscripts.SendMulti))]
        private static class EditBusDoors
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(busdoorscripts __instance) { if (!InSession) return true; MarkEdit(__instance.tosave, kind: "busdoor"); return false; }
        }
    }
}
