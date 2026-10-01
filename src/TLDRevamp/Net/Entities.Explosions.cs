using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Explosions on every machine. The game's explosion (explosionscript.Explode: a fuel tank's TryExplode, an
    /// explosive's Explode) runs on the machine where it was set off and nowhere else: its blast and sound, the push on
    /// bodies, chain explosions of tanks and explosives, breakables, crash damage to cars (DamageStuff), deafening and
    /// killing the player. A player shooting someone else's fuel can blew up THEIR copy only; the copy's deletion then
    /// removed the owner's can silently — no blast there.
    /// Now an explosion is an event everyone replays with the game's own code at the same place, force and reach, and
    /// each machine's blast acts on what that machine simulates: its own objects and its own player (copies are
    /// kinematic, their car damage is already the owner's — Entities.Proxy). A tank or explosive on a COPY doesn't go off
    /// inside a blast — the owner's machine replays the same blast and sets its own off (that's broadcast in turn); set
    /// off directly (a shot at someone's can), the request goes to the owner, whose explosion everyone then sees.
    public static partial class Entities
    {
        public const byte ExplodeReq = 46, ExplosionFx = 47;
        public static long ExplosionsSent, ExplosionsApplied, ExplodeReqsSent, ExplodeReqsRun, CopyBlastsSkipped;

        private static explosionscript _replaying;   // another machine's explosion replayed here: not sent back out
                                                    // (a tank of OURS it sets off is a new explosion, and is sent)
        private static int _blastDepth;     // inside an explosionscript.Explode (any)

        private struct PendingBlast { public explosionscript X; public byte Type; public Vector3d At; public Quaternion Rot; public float F, Add; }
        private static readonly List<PendingBlast> Pending = new List<PendingBlast>();

        [HarmonyPatch(typeof(explosionscript), nameof(explosionscript.Explode), new[] { typeof(float), typeof(float) })]
        private static class BlastHook
        {
            [HarmonyPrefix]
            private static void Prefix(explosionscript __instance, float pF, float distAdd)
            {
                _blastDepth++;
                if (!InSession || mainscript.s == null || (_replaying != null && __instance == _replaying)) return;
                int type = BlastType(__instance);
                if (type < 0) return;
                Pending.Add(new PendingBlast { X = __instance, Type = (byte)type, At = mainscript.GlobalFromUnityPos(__instance.transform.position),
                                               Rot = __instance.transform.rotation, F = pF, Add = distAdd });
            }

            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception) { _blastDepth--; return __exception; }
        }

        /// A fuel tank on a copy: inside a blast the owner sets its own off; set off directly, ask the owner.
        [HarmonyPatch(typeof(tankscript), nameof(tankscript.TryExplode))]
        private static class CopyTank
        {
            [HarmonyPrefix]
            private static bool Prefix(tankscript __instance, float forceAdd, bool add)
            {
                if (!InSession || !IsProxy(__instance)) return true;
                if (_blastDepth > 0) { CopyBlastsSkipped++; return false; }
                if (__instance.neverExplode || __instance.alreadyExploded || __instance.F == null || !(__instance.F.isExplosive() || __instance.alwaysExplode)
                    || forceAdd < __instance.explosionForceNeededToExplode) return false;   // wouldn't go off anyway
                if (TankRef(__instance, out uint net, out int ti))
                {
                    W.Reset(); W.U8(ExplodeReq); W.U32(net); W.U8(1); W.VarU32((uint)ti); W.F32(forceAdd); W.Bool(add);
                    ToServer(W, true); ExplodeReqsSent++;
                }
                return false;
            }
        }

        /// The same for explosives (dynamite and the like).
        [HarmonyPatch(typeof(explosivescript), nameof(explosivescript.TryExplode))]
        private static class CopyExplosive
        {
            [HarmonyPrefix]
            private static bool Prefix(explosivescript __instance, float force)
            {
                if (!InSession || !IsProxy(__instance)) return true;
                if (_blastDepth > 0) { CopyBlastsSkipped++; return false; }
                if (!(force > __instance.forceNeeded)) return false;
                if (ItemRef(__instance.GetComponentInParent<tosaveitemscript>(), out uint net, out int idx))
                {
                    W.Reset(); W.U8(ExplodeReq); W.U32(net); W.U8(2); W.VarU32((uint)idx); W.F32(force); W.Bool(false);
                    ToServer(W, true); ExplodeReqsSent++;
                }
                return false;
            }
        }

        /// Per frame: this machine's explosions go out (after the frame they started in: an explosive colours its
        /// blast after Explode returns).
        private static void BlastTick()
        {
            if (Pending.Count == 0) return;
            foreach (var b in Pending)
            {
                var c = b.X != null && b.X.particle != null ? b.X.particle.main.startColor.color : Color.white;
                W.Reset(); W.U8(ExplosionFx); W.U8(b.Type);
                W.F64(b.At.x); W.F64(b.At.y); W.F64(b.At.z);
                W.F32(b.Rot.x); W.F32(b.Rot.y); W.F32(b.Rot.z); W.F32(b.Rot.w);
                W.F32(b.F); W.F32(b.Add);
                W.F32(c.r); W.F32(c.g); W.F32(c.b); W.F32(c.a);
                ToServer(W, true);
                ExplosionsSent++;
            }
            Pending.Clear();
        }

        // ---------------------------------------------------------------- server
        private static void ServerExplodeReq(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        private static void ServerExplosionFx(int from, NetReader r)
        {
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, true, from);
        }

        // ---------------------------------------------------------------- owner / everyone
        /// Owner: someone set off our tank or explosive — the game's own call here (and so the blast everyone sees).
        private static void ApplyExplodeReq(NetReader r)
        {
            uint net = r.U32(); byte kind = r.U8(); int i = (int)r.VarU32(); float force = r.F32(); bool add = r.Bool();
            if (r.Bad) return;
            if (kind == 1)
            {
                var t = TankOf(net, i);
                if (t == null || IsProxy(t)) return;
                ExplodeReqsRun++;
                t.TryExplode(force, add);
            }
            else if (kind == 2)
            {
                if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || i >= e.Items.Count || e.Items[i] == null || e.Proxy) return;
                var x = e.Items[i].GetComponentInChildren<explosivescript>();
                if (x == null) return;
                ExplodeReqsRun++;
                x.TryExplode(force);
            }
        }

        /// Another machine's explosion, replayed here with the game's own code.
        private static void ApplyExplosionFx(NetReader r)
        {
            int type = r.U8();
            var at = new Vector3d(r.F64(), r.F64(), r.F64());
            var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            float f = r.F32(), add = r.F32();
            var c = new Color(r.F32(), r.F32(), r.F32(), r.F32());
            if (r.Bad || mainscript.s == null) return;
            var prefab = mainscript.s.GetExplosionType(type);
            if (prefab == null) return;
            var go = Object.Instantiate(prefab, mainscript.UnityPosFromGlobal(at), rot);
            var x = go.GetComponent<explosionscript>();
            if (x == null) return;
            _replaying = x;
            try
            {
                x.Explode(f, add);
                if (x.particle != null) { var m = x.particle.main; m.startColor = c; }
                ExplosionsApplied++;
            }
            finally { _replaying = null; }
        }

        private static int BlastType(explosionscript x)
        {
            var ms = mainscript.s;
            string n = x.name;
            if (ms.ExplosionRed != null && n == ms.ExplosionRed.name + "(Clone)") return 0;
            if (ms.ExplosionBlue != null && n == ms.ExplosionBlue.name + "(Clone)") return 2;
            if (ms.ExplosionPaint != null && n == ms.ExplosionPaint.name + "(Clone)") return 3;
            return -1;
        }

        private static bool ItemRef(tosaveitemscript it, out uint net, out int idx)
        {
            net = 0; idx = -1;
            if (it == null) return false;
            foreach (var e in ByNet.Values)
            {
                if (e.Items == null) continue;
                int i = e.Items.IndexOf(it);
                if (i >= 0) { net = e.NetId; idx = i; return true; }
            }
            return false;
        }

        public static string ExplosionStats() => "{\"sent\":" + ExplosionsSent + ",\"applied\":" + ExplosionsApplied + ",\"reqSent\":" + ExplodeReqsSent +
                                                 ",\"reqRun\":" + ExplodeReqsRun + ",\"copySkipped\":" + CopyBlastsSkipped + "}";
    }
}
