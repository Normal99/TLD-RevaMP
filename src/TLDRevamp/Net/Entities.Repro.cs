using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static ReproRow ReproBinding(uint net)
        {
            var r = new ReproRow { Kind = ReproKind.Bind, Net = net, A = -1 };
            if (ByNet.TryGetValue(net, out var e) && e.Root != null)
            {
                var g = mainscript.GlobalFromUnityPos(e.Root.transform.position);
                r.X = g.x; r.Y = g.y; r.Z = g.z; r.A = e.Root.id;
            }
            return r;
        }
        private static ReproRow ReproItem(ReproKind kind, tosaveitemscript it)
        {
            var e = it != null ? EditEntityOf(it) : null;
            return new ReproRow { Kind = kind, Net = e != null ? e.NetId : 0, Item = e != null ? e.Items.IndexOf(it) : -1 };
        }
        internal static void ReproAttach(uint net, uint other, int item, int slot)
        {
            if (!PlayerActing || !ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.attachable == null || _hitchApplying) return;
            var at = e.Root.attachable; if (at.utanfuto != null) return;
            var rec = new save_attachable(at, 0);
            var g = mainscript.GlobalFromUnityPos(at.transform.position); var q = at.transform.eulerAngles;
            if (!e.Proxy && e.OwnerId == MyId) ReproRecorder.Add(new ReproRow { Kind = ReproKind.EntityPose, Net = net, X = g.x, Y = g.y, Z = g.z, A = q.x, B = q.y, C = q.z });
            ReproRecorder.Add(new ReproRow { Kind = ReproKind.Attach, Net = net, Other = other, Item = item, Slot = slot,
                X = rec.gpos.x, Y = rec.gpos.y, Z = rec.gpos.z, A = rec.indexType, B = rec.attachType,
                C = rec.lpos.x, D = rec.lpos.y, E = rec.lpos.z, F = rec.poigenid, G = rec.poiid.x, H = rec.poiid.z });
        }
        private static bool ReproLocal(fpscontroller p) => ReproRecorder.Recording && !ReproPlayback.Active && mainscript.s != null && mainscript.s.player == p;
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Pickup), new[] { typeof(pickupable) })]
        private static class ReproPickup
        {
            private static void Postfix(fpscontroller __instance, pickupable p)
            { if (ReproLocal(__instance) && __instance.pickedUp == p && p != null && _reproInventoryDepth == 0) ReproRecorder.Add(ReproItem(ReproKind.Pickup, p.tosave)); }
        }
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Drop), new[] { typeof(bool) })]
        private static class ReproDrop
        {
            private static bool Prefix(fpscontroller __instance, bool isDestroyed)
            {
                if (ReproPlayback.Active && !isDestroyed && __instance.pickedUp != null && ForceHeld == __instance.pickedUp.tosave) return false;
                if (!ReproLocal(__instance) || isDestroyed || __instance.pickedUp == null || _reproInventoryDepth > 0) return true;
                var r = ReproItem(__instance.throwForce > __instance.minThrowForce ? ReproKind.Throw : ReproKind.Drop, __instance.pickedUp.tosave);
                r.A = Mathf.Clamp01(__instance.throwForce); var dir = __instance.Th.forward; r.X = dir.x; r.Y = dir.y; r.Z = dir.z; ReproRecorder.Add(r); return true;
            }
        }
        private static int _reproInventoryDepth;
        [HarmonyPatch]
        private static class ReproInventory
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.InvSwitchTo), new[] { typeof(int), typeof(bool) });
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.InvSwapTo));
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.InvStore));
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.InvUnStore));
            }
            private static void Prefix(fpscontroller __instance, int i, MethodBase __originalMethod, out bool __state)
            {
                __state = ReproLocal(__instance); if (!__state) return;
                if (_reproInventoryDepth++ != 0) return;
                var k = __originalMethod.Name == "InvSwitchTo" ? ReproKind.InventorySwitch : __originalMethod.Name == "InvSwapTo" ? ReproKind.InventorySwap :
                    __originalMethod.Name == "InvStore" ? ReproKind.InventoryStore : ReproKind.InventoryUnstore;
                var p = __instance.inventory != null && i >= 0 && i < __instance.inventory.Count ? __instance.inventory[i] : null;
                var r = ReproItem(k, p != null ? p.tosave : null); r.Slot = i; ReproRecorder.Add(r);
            }
            private static Exception Finalizer(Exception __exception, bool __state) { if (__state) _reproInventoryDepth--; return __exception; }
        }
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RightHandGrip))]
        private static class ReproGrip
        {
            private static void Prefix(fpscontroller __instance)
            { if (ReproLocal(__instance) && _reproInventoryDepth == 0 && __instance.pickedUp != null) ReproRecorder.Add(ReproItem(ReproKind.RightGrip, __instance.pickedUp.tosave)); }
        }
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RightHandRelease))]
        private static class ReproRelease
        {
            private static void Prefix(fpscontroller __instance)
            { if (ReproLocal(__instance) && _reproInventoryDepth == 0 && __instance.inHandP != null) ReproRecorder.Add(ReproItem(ReproKind.RightRelease, __instance.inHandP.tosave)); }
        }
        [HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class ReproUsable
        {
            private static void Postfix(usablescript __instance, bool syncinmulti)
            {
                if (!ReproRecorder.Recording || ReproPlayback.Active || !syncinmulti || !PlayerActing || _applyingLive || _applyingState || _applyingPoi) return;
                var u = __instance;
                var it = ItemOf(u, u.tosaveid, u.setid); var r = ReproItem(ReproKind.Usable, it);
                if (u.setidPoi)
                {
                    r.Kind = ReproKind.PoiUsable; r.Net = 0; r.Item = u.poigenid; r.Slot = u.usableid;
                    r.E = u.poiid.x; r.F = u.poiid.y; r.G = u.poiid.z;
                }
                else r.Slot = it != null ? Array.IndexOf(ItemUsables(it), u) : -1;
                QueueReproUsable(u, r);
            }
        }
        private struct ReproUsableEntry { internal usablescript U; internal ReproRow Row; internal bool Dirty; }
        private static readonly ReproUsableEntry[] ReproUsables = new ReproUsableEntry[32];
        private static int _reproUsed;
        internal static void ReproClear() { Array.Clear(ReproUsables, 0, ReproUsables.Length); _reproUsed = 0; _reproKey = 0; }
        internal static void ReproReset() { ReproClear(); _reproInventoryDepth = 0; _reproDriveNext = 0; }
        private static void QueueReproUsable(usablescript u, ReproRow row)
        {
            int at = 0; while (at < _reproUsed && ReproUsables[at].U != u) at++;
            if (at == ReproUsables.Length)
            {
                ReproFlush(); at = 0;
                if (!ReproRecorder.Recording) return;
            }
            if (at == _reproUsed) _reproUsed++;
            ReproUsables[at] = new ReproUsableEntry { U = u, Row = row, Dirty = true };
        }
        internal static void ReproFlush()
        {
            for (int i = 0; i < _reproUsed && ReproRecorder.Recording; i++)
            {
                ref var entry = ref ReproUsables[i]; if (!entry.Dirty || entry.U == null) continue;
                entry.Dirty = false; var r = entry.Row; var u = entry.U;
                r.X = u.slideValue; r.Y = u.xRot; r.Z = u.yRot; r.A = u.currentTurnState; r.B = u.tunedFloat; ReproRecorder.Add(r);
            }
        }
        private static uint _reproKey;
        internal static void ReproFinish()
        {
            ReproFlush();
            if (_reproKey != 0) { ReproRecorder.Add(new ReproRow { Kind = ReproKind.Ignition, Net = _reproKey }); _reproKey = 0; }
        }
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class ReproKey
        {
            private static void Postfix(fpscontroller __instance)
            {
                if (!ReproLocal(__instance)) return;
                uint held = 0; var u = __instance.lookedUsable;
                if (u != null && u.lastHoldTime == Time.time)
                {
                    var it = u.GetComponentInParent<tosaveitemscript>();
                    if (it != null && it.car != null && it.car.ignitionUsable == u) held = TelemetryNet(it);
                }
                if (held == _reproKey) return;
                if (_reproKey != 0) ReproRecorder.Add(new ReproRow { Kind = ReproKind.Ignition, Net = _reproKey });
                _reproKey = held;
                if (held != 0) ReproRecorder.Add(new ReproRow { Kind = ReproKind.Ignition, Net = held, A = 1 });
            }
        }
        private static int _reproDetachFrame = -1, _reproDetachItem;
        private static uint _reproDetachNet;
        private static ReproRow? ReproDetaching(attachablescript at)
        {
            if (!ReproRecorder.Recording || ReproPlayback.Active || !PlayerActing || _applyingDetach || _applyingState || _hitchApplying || at == null || at.utanfuto != null) return null;
            return ReproItem(ReproKind.Detach, at.tosave);
        }
        private static void ReproDetached(ReproRow? row)
        {
            if (!row.HasValue) return;
            var r = row.Value;
            if (_reproDetachFrame == Time.frameCount && _reproDetachNet == r.Net && _reproDetachItem == r.Item) return;
            _reproDetachFrame = Time.frameCount; _reproDetachNet = r.Net; _reproDetachItem = r.Item; ReproRecorder.Add(r);
        }
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class ReproDetach
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(attachablescript __instance, out ReproRow? __state)
            { __state = ReproDetaching(__instance); }
            private static void Postfix(attachablescript __instance, ReproRow? __state, bool __runOriginal)
            { if (__runOriginal && !__instance.attached) ReproDetached(__state); }
        }
        [HarmonyPatch(typeof(partslotscript), nameof(partslotscript.UnCraft), new[] { typeof(bool) })]
        private static class ReproUncraft
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(partslotscript __instance, out ReproRow? __state)
            { __state = ReproRecorder.Recording && !ReproPlayback.Active ? ReproDetaching(__instance.part()) : null; }
            private static void Postfix(ReproRow? __state, bool __result)
            { if (__result) ReproDetached(__state); }
        }
        [HarmonyPatch(typeof(pickupable), nameof(pickupable.PhysLock))]
        private static class ReproLock
        {
            private static void Postfix(pickupable __instance, mountStuff _physlock)
            {
                if (!ReproRecorder.Recording || ReproPlayback.Active || _physlock == null) return;
                try
                {
                    var e = EditEntityOf(__instance.tosave); if (e == null || e.Proxy || e.OwnerId != MyId) return;
                    if (_physlock.parentTosave == null) { ReproRecorder.Skip(); return; }
                    var r = ReproItem(ReproKind.PhysLock, __instance.tosave); var c = EditEntityOf(_physlock.parentTosave);
                    r.Other = c != null ? c.NetId : 0; r.Slot = Array.IndexOf(LockMounts(_physlock.parentTosave), _physlock);
                    r.A = c != null ? c.Items.IndexOf(_physlock.parentTosave) : -1; ReproRecorder.Add(r);
                }
                catch { ReproRecorder.Skip(); }
            }
        }
        [HarmonyPatch(typeof(pickupable), nameof(pickupable.UnPhysicsLock))]
        private static class ReproUnlock
        {
            private static void Prefix(pickupable __instance)
            {
                if (!ReproRecorder.Recording || ReproPlayback.Active || __instance.physlock == null) return;
                var e = EditEntityOf(__instance.tosave); if (e != null && !e.Proxy && e.OwnerId == MyId) ReproRecorder.Add(ReproItem(ReproKind.PhysUnlock, __instance.tosave));
            }
        }
    }
}
