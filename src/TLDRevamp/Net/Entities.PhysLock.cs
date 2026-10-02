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
                   ",\"sent\":" + PhysLockSent + ",\"applied\":" + PhysLockApplied + ",\"reads\":" + PhysLockReads +
                   ",\"holds\":{" + string.Join(",", rows) + "}}";
        }
    }
}
