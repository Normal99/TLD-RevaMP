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
        private const float EditHold = 1.5f;   // an editor ignores the owner's older state of that item this long

        public static string EditMarks = "";
        private static void MarkEdit(tosaveitemscript it, bool fromTank = false, string kind = "")
        {
            if (EditMarks.Length < 400) EditMarks += kind + (it == null ? ":null" : IsProxy(it) ? ":proxy" : ":own") + (_applyingState ? ":applying" : "") + " ";
            if (!InSession || _applyingState || it == null) return;
            if (IsProxy(it)) { EditDirty[it] = Time.realtimeSinceStartup; EditsMarked++; return; }
            if (fromTank) return;   // own tanks change all the time (fuel burn): the regular pass is enough
            foreach (var e in ByNet.Values)
                if (!e.Proxy && e.Items != null && e.Items.Contains(it)) { e.ResyncAcc = Mathf.Max(e.ResyncAcc, ResyncInterval - 0.2f); return; }
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
            foreach (var kv in EditDirty)
            {
                var it = kv.Key;
                if (it == null) continue;
                foreach (var e in ByNet.Values)
                {
                    if (!e.Proxy || e.Items == null) continue;
                    int idx = e.Items.IndexOf(it);
                    if (idx < 0) continue;
                    byte[] rec = StateRecord(ItemSnapshot.Capture(it), CanonMap(e.Items), out _, withCar: false);
                    W.Reset(); W.U8(Edit); W.U32(e.NetId); W.VarU32((uint)idx); W.VarU32((uint)rec.Length); W.Bytes(rec, 0, rec.Length);
                    ToServer(W, true);
                    if (e.EditHoldUntil == null) e.EditHoldUntil = new Dictionary<int, float>();
                    e.EditHoldUntil[idx] = Time.realtimeSinceStartup + EditHold;
                    EditsSent++;
                    break;
                }
            }
            EditDirty.Clear();
        }

        /// Server: to the owner.
        private static void ServerEdit(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) { EditsRejected++; return; }
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Owner: apply, and bring the resync pass forward so everyone sees it.
        private static void ApplyEdit(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32(), len = (int)r.VarU32();
            if (r.Bad || len < 0 || len > r.Remaining || idx < 0 || !ByNet.TryGetValue(net, out var e) || e.Proxy || !Resolve(e) || idx >= e.Items.Count || e.Items[idx] == null) { EditsRejected++; return; }
            var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len);
            var d = RecordCodec.Decode(rec);
            if (d == null) { EditsRejected++; return; }
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) map[CanonBase + (uint)i] = e.Items[i].idInSave;
            ItemSnapshot.Remap(d, map);
            ApplyState(e.Items[idx], d);
            e.ResyncAcc = Mathf.Max(e.ResyncAcc, ResyncInterval - 0.05f);
            EditsApplied++;
        }

        // ------------------------------------------------------------------ the game's "a player changed this" points

        [HarmonyLib.HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class EditUsable
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(usablescript __instance, bool syncinmulti)
            { if (syncinmulti && InSession && !__instance.setidPoi) MarkEdit(ItemOf(__instance, __instance.tosaveid, __instance.setid), kind: "usable"); }
        }

        [HarmonyLib.HarmonyPatch(typeof(tankscript), nameof(tankscript.SyncMulti))]
        private static class EditTank
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(tankscript __instance)
            { if (InSession && !__instance.setpoi && !_displayPour) MarkEdit(ItemOf(__instance, __instance.tosaveid, __instance.setid), fromTank: true, kind: "tank"); }
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
                MarkEdit(__instance.tosave);
                return false;   // the game's body sends to the official MP unconditionally (syncScript.s may not exist)
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(ediblescript), nameof(ediblescript.UpdMulti))]
        private static class EditFood { [HarmonyLib.HarmonyPostfix] private static void Postfix(ediblescript __instance) { if (InSession) MarkEdit(__instance.tosave); } }

        [HarmonyLib.HarmonyPatch(typeof(ammoscript), nameof(ammoscript.SendAmmoMulti))]
        private static class EditAmmo { [HarmonyLib.HarmonyPostfix] private static void Postfix(ammoscript __instance) { if (InSession) MarkEdit(__instance.tosave); } }

        [HarmonyLib.HarmonyPatch(typeof(rendszamscript), nameof(rendszamscript.SendMulti))]
        private static class EditPlate
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(rendszamscript __instance) { if (!InSession) return true; MarkEdit(__instance.tosave); return false; }
        }

        [HarmonyLib.HarmonyPatch(typeof(busdoorscripts), nameof(busdoorscripts.SendMulti))]
        private static class EditBusDoors
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(busdoorscripts __instance) { if (!InSession) return true; MarkEdit(__instance.tosave); return false; }
        }
    }
}
