using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The physics-lock setting (Settings → Misc: which containers hold what is put in them - glovebox, baskets, racks,
    /// trunks; mountStuff.CanStore, and with any lock on, cars loaded into a trunk or passenger room). Each machine used
    /// its own, so an item in a basket was held or loose depending on which player owned it at the moment, and changed
    /// when it was handed over. The game's own MP: the host's setting rules - the host puts it in the lobby
    /// (syncScript.UpdPhysicsLockServer) and a client's CanStore reads it from there (syncScript.GetPhysicsLock).
    /// The same here: the host sends its setting when it changes and every PhysLockResendS (a player who just joined);
    /// on a client the game's checks read the host's value instead of the local one. The client's own setting is
    /// never changed - it is its setting again when the session ends.
    public static partial class Entities
    {
        public const byte PhysLockSync = 55;
        public static float PhysLockResendS = 5f;
        public static int HostPhysicsLock = -1;   // a client: the host's setting (-1: none received)
        public static long PhysLockSent, PhysLockApplied, PhysLockReads;
        private static int _physLockLast = -1;
        private static float _physLockAcc;

        private static void PhysLockTick(float dt)
        {
            if (!IsHost || settingsscript.s == null || settingsscript.s.S == null) return;
            int v = settingsscript.s.S.IPhysicsLock;
            _physLockAcc += dt;
            if (v == _physLockLast && _physLockAcc < PhysLockResendS) return;
            _physLockLast = v; _physLockAcc = 0f;
            W.Reset(); W.U8(PhysLockSync); W.U8((byte)Mathf.Clamp(v, 0, 255));
            ServerSendAll(W, true, 0);
            PhysLockSent++;
        }

        private static void ApplyPhysLock(NetReader r)
        {
            int v = r.U8();
            if (r.Bad || IsHost) return;
            HostPhysicsLock = v;
            PhysLockApplied++;
        }

        /// What the game's checks read in place of settingsscript.s.S.IPhysicsLock.
        public static int PhysicsLockSetting(settingsscript.setting s)
        {
            if (HostPhysicsLock >= 0 && InSession && !IsHost) { PhysLockReads++; return HostPhysicsLock; }
            return s.IPhysicsLock;
        }

        [HarmonyPatch]
        private static class PhysLockFromHost
        {
            private static readonly FieldInfo Field = AccessTools.Field(typeof(settingsscript.setting), nameof(settingsscript.setting.IPhysicsLock));
            private static readonly MethodInfo Replacement = AccessTools.Method(typeof(Entities), nameof(PhysicsLockSetting));
            public static int Patched;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(mountStuff), nameof(mountStuff.CanStore));
                yield return AccessTools.Method(typeof(mountStuff), "Update");
            }

            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var ins in instructions)
                {
                    if (ins.LoadsField(Field)) { ins.opcode = OpCodes.Call; ins.operand = Replacement; Patched++; }   // stack: setting -> int
                    yield return ins;
                }
            }
        }

        /// A container's own scan (mountStuff.Update, once a second: what lies in it for two scans is stored — parented,
        /// its body removed) ran on the COPIES of other players' cars too, taking anything: an item lying around here got
        /// locked onto someone else's car on this machine only (playtest 2026-10-09: "items appear on the other car which
        /// don't appear for them … if something from the environment physlocks on there by accident"). The car's owner
        /// decides what its containers hold. A copy's container stores only copies of the car owner's items (what the
        /// owner's own container holds: parented, they ride exactly with the car — cargoride.py); an own container
        /// doesn't take another player's copy (storing removes its body: it isn't ours — touching it with our car claims
        /// it first, Entities.Cargo).
        public static bool CopyContainersInert = true;   // A/B: false = before v0.66.2
        public static long ContainerStoresRefused;
        public static int StoreTrace;   // test: log the next N stores with their call stack
        private static bool _containerScan;
        private static mountStuff _scanning;     // the container whose scan is running (Update runs every frame: no lookups there)

        [HarmonyPatch(typeof(mountStuff), "Update")]
        private static class ContainerScan
        {
            [HarmonyPrefix]
            private static void Prefix(mountStuff __instance)
            {
                if (!CopyContainersInert || !InSession) return;
                _scanning = __instance;
                _containerScan = true;
            }
            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception) { _containerScan = false; return __exception; }
        }

        /// A container's first frames (mountStuff.StartFreeze: three QuickFreeze scans after it starts) store what lies
        /// in it at once. A copy starts where its record put it — the pose it had when shared (a joiner gets every car at
        /// its original record pose), or one taken before its spawner moved it — and the interpolator moves it to the
        /// owner's place a moment later: what lay at the old pose went along, stored, on this machine only (cratepull.py:
        /// a crate's copy took a plank off a shelf 12 m from where the crate stood; it fell when the crate broke). A copy's
        /// contents come from its owner's record (physLocks, loaded with it); its first-frames scans are skipped.
        public static bool CopyStartFreezeOff = true;   // A/B: false = before v0.66.3 (cratepull.py --old)
        public static long CopyStartFreezesSkipped;

        /// Our copy stored in a container here (parented: it rides that object) while its owner has it somewhere else: a
        /// copy's container stored it on this machine only (its own scan takes copies of the car owner's things — the
        /// owner's container didn't). Shown at its owner's pose it was dragged across the world still parented in the
        /// car, and the game's streaming removes a whole car when one item in it is beyond its range: crate loot lying
        /// at home, stored into the copy of the laptop's car on the host, made the host's copy of that car go and come
        /// back every second 35 m from the player (playround.py, the anomaly watch, 2026-10-09; the 2026-10-09 playtest: "items
        /// physlocked onto the other player's car on one machine only", "cars disappearing and reappearing").
        /// Off its owner's pose for a second by more than CopyLockReleaseM: out of that container here, the game's way.
        public static bool CopyLockRelease = true;   // A/B: false = before v0.66.7
        public static float CopyLockReleaseM = 5f, CopyLockReleaseS = 1f;
        public static long CopyLocksReleased;
        private static void CheckCopyLock(Ent e, Vector3 shownUnity, double now)
        {
            var p = e.Root.P;
            if (!CopyLockRelease || p == null || p.physlock == null || p.physlock.transform.root == e.Root.transform
                || (shownUnity - e.Root.transform.position).sqrMagnitude <= CopyLockReleaseM * CopyLockReleaseM) { e.LockOffSince = 0; return; }
            if (e.LockOffSince <= 0) { e.LockOffSince = now; return; }
            if (now - e.LockOffSince < CopyLockReleaseS) return;
            e.LockOffSince = 0;
            string where = p.physlock.transform.root.name;
            float off = Vector3.Distance(shownUnity, e.Root.transform.position);
            p.UnPhysicsLock();
            ApplyProxyBodies(e);
            CopyLocksReleased++;
            if (CopyLocksReleased <= 20) Plugin.Log.LogWarning($"copy taken out of a container here: {e.Root.name} net {e.NetId} in {where} — its owner has it {off:F0} m away");
        }

        /// Test hook (bridge `mp copystore <item> <car>`): our copy of the item stored into a container of our copy of the
        /// car, this machine only — what a copy's own scan did by mistake.
        public static string TestCopyStore(uint item, uint car)
        {
            if (!ByNet.TryGetValue(item, out var ie) || !ie.Proxy || !Resolve(ie) || ie.Root == null || ie.Root.P == null) return "{\"error\":\"no copy of the item here\"}";
            if (!ByNet.TryGetValue(car, out var ce) || !Resolve(ce) || ce.Root == null) return "{\"error\":\"no car here\"}";
            mountStuff best = null;
            foreach (var m in ce.Root.transform.root.GetComponentsInChildren<mountStuff>(true)) if (m != null) { best = m; if (m.sType.ToString().ToLower().Contains("trunk") || m.sType.ToString().ToLower().Contains("car")) break; }
            if (best == null) return "{\"error\":\"no container on the car\"}";
            ie.Root.transform.position = best.transform.position + Vector3.up * 0.2f;
            best.Store(ie.Root.P, true);
            return "{\"stored\":" + Json.Str(ie.Root.name) + ",\"in\":" + Json.Str(best.name + " (" + best.sType + ")") + ",\"physlock\":" + (ie.Root.P.physlock != null ? "true" : "false") + ",\"ipReady\":" + (ie.Ip != null && ie.Ip.Ready ? "true" : "false") +
                   ",\"physical\":" + (ie.Physical ? "true" : "false") + ",\"byBody\":" + (ie.MovedByBody ? "true" : "false") + ",\"carriedBy\":" + ie.CarriedBy + "}";
        }

        [HarmonyPatch(typeof(mountStuff), nameof(mountStuff.QuickFreeze))]
        private static class CopyStartFreeze
        {
            [HarmonyPrefix]
            private static bool Prefix(mountStuff __instance)
            {
                if (!InSession || !IsProxy(__instance)) return true;
                if (StoreTrace > 0)
                {
                    var g = mainscript.GlobalFromUnityPos(__instance.transform.root.position);
                    Plugin.Log.LogInfo("[storetrace] copy start freeze: " + __instance.transform.root.name + " at " + g.x.ToString("F1") + "," + g.y.ToString("F1") + "," + g.z.ToString("F1") + " frame " + Time.frameCount);
                }
                if (!CopyStartFreezeOff) return true;
                CopyStartFreezesSkipped++;
                return false;
            }
        }

        [HarmonyPatch(typeof(mountStuff), nameof(mountStuff.Store))]
        private static class ContainerStore
        {
            [HarmonyPrefix]
            private static bool Prefix(mountStuff __instance, pickupable _p)
            {
                if (StoreTrace > 0 && _p != null)
                {
                    StoreTrace--;
                    Plugin.Log.LogInfo("[storetrace] " + __instance.transform.root.name + " stores " + _p.name + " scan " + _containerScan + "\n" + System.Environment.StackTrace);
                }
                if (!_containerScan || _p == null || _scanning == null) return true;
                var ce = IsProxy(_scanning) ? EntOfRoot(_scanning.transform.root) : null;
                int _scanCarOwner = ce != null ? ce.OwnerId : -1;
                var pe = IsProxy(_p) ? EntOfRoot(_p.transform.root) : null;
                bool ok = _scanCarOwner < 0 ? pe == null                                  // our container: our own things only
                                            : pe != null && pe.OwnerId == _scanCarOwner;  // a copy's: the car owner's things only
                if (ok) return true;
                ContainerStoresRefused++;
                return false;
            }
        }

        /// Diagnostics (bridge `mp physlock`): the local setting, the host's, what the game's checks use here, and for the
        /// nearest containers of each kind whether they would hold an item now (the game's own CanStore).
        public static string PhysLockStatus(double gx, double gz, float radius)
        {
            int local = settingsscript.s != null && settingsscript.s.S != null ? settingsscript.s.S.IPhysicsLock : -1;
            int used = settingsscript.s != null && settingsscript.s.S != null ? PhysicsLockSetting(settingsscript.s.S) : -1;
            var kinds = new SortedDictionary<string, string>();
            foreach (var m in Object.FindObjectsOfType<mountStuff>())
            {
                if (m == null) continue;
                var g = mainscript.GlobalFromUnityPos(m.transform.position);
                if ((g.x - gx) * (g.x - gx) + (g.z - gz) * (g.z - gz) > radius * radius) continue;
                string k = m.sType.ToString();
                if (kinds.ContainsKey(k)) continue;
                bool holds = false;
                try { holds = m.CanStore(); } catch { }
                kinds[k] = holds ? "true" : "false";
            }
            var rows = new List<string>();
            foreach (var kv in kinds) rows.Add(Json.Str(kv.Key) + ":" + kv.Value);
            return "{\"local\":" + local + ",\"host\":" + HostPhysicsLock + ",\"used\":" + used + ",\"patched\":" + PhysLockFromHost.Patched +
                   ",\"sent\":" + PhysLockSent + ",\"applied\":" + PhysLockApplied + ",\"reads\":" + PhysLockReads + ",\"storesRefused\":" + ContainerStoresRefused + ",\"copyLocksReleased\":" + CopyLocksReleased + ",\"startFreezesSkipped\":" + CopyStartFreezesSkipped +
                   ",\"holds\":{" + string.Join(",", rows) + "}}";
        }
    }
}
