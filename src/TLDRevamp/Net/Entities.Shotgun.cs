using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The front passenger's controls. In the game the driver's seat has driverSeat0+driverSeat1 and the front
    /// passenger seat driverSeat1 only ("shotgun": carWhatDoing.shotgun, Car.driving2). carscript.GetPlayerInput reads
    /// steer, horn, lights, handbrake, gear and ignition buttons for driving2 as well — a passenger can grab the wheel,
    /// throttle/brake/clutch stay with the driver. In a session the passenger's car is a display copy, so those inputs
    /// would only move the copy. Here they go to the car's owner, who merges them into its own input frame
    /// (steer added and clamped, buttons OR-ed) — the same mix the game makes when both players are local.
    public static partial class Entities
    {
        public const byte ShotgunIn = 39;
        public static long ShotgunSent, ShotgunApplied;
        public static float TestSteer;   // test hook (bridge `set`): the passenger's steering input
        public static bool TestLights;   // test hook: one press of the light switch

        // bits of the message (held = state this frame, edges = pressed since the last send)
        private const ushort BHorn = 1, BIgnition = 2;
        private const ushort ELights = 1, EHandbrake = 2, EIgnition = 4, EGearR = 8, EGearN = 16, EG1 = 32, EG2 = 64, EG3 = 128,
                             EG4 = 256, EG5 = 512, EG6 = 1024, EUp = 2048, EDown = 4096;

        // ---- the passenger's side
        private static carscript _shotCar;
        private static float _shotSteer, _shotHandbrake, _shotSendAcc;
        private static ushort _shotHeld, _shotEdges;
        private static bool _shotWasActive;

        /// A proxy car whose shotgun seat the local player occupies: capture the controls, and keep them off the copy
        /// (it only shows the owner's car — a light toggled here AND there would toggle twice).
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.GetPlayerInput))]
        private static class ShotgunCapture
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                if (!InSession) return;
                if (__instance.isPlayerDriving2 && !__instance.isPlayerDriving && IsProxy(__instance))
                {
                    var c = __instance;
                    _shotCar = c;
                    _shotSteer = TestSteer != 0f ? TestSteer : c.isteer; _shotHandbrake = c.ihandbrake;
                    if (TestLights) { c.iLightsBDown = true; TestLights = false; }   // bridge test hooks: what a player's hands would do
                    _shotHeld = (ushort)((c.ihorn ? BHorn : 0) | (c.iignition ? BIgnition : 0));
                    _shotEdges |= (ushort)((c.iLightsBDown ? ELights : 0) | (c.ihandbrakeBDown ? EHandbrake : 0) | (c.iignitionDown ? EIgnition : 0)
                        | (c.igrDown ? EGearR : 0) | (c.ignDown ? EGearN : 0) | (c.ig1Down ? EG1 : 0) | (c.ig2Down ? EG2 : 0) | (c.ig3Down ? EG3 : 0)
                        | (c.ig4Down ? EG4 : 0) | (c.ig5Down ? EG5 : 0) | (c.ig6Down ? EG6 : 0) | (c.iupshiftDown ? EUp : 0) | (c.idownshiftDown ? EDown : 0));
                    c.ihorn = false; c.iignition = false; c.iLightsBDown = false; c.ihandbrakeBDown = false; c.iignitionDown = false;
                    c.igrDown = c.ignDown = c.ig1Down = c.ig2Down = c.ig3Down = c.ig4Down = c.ig5Down = c.ig6Down = c.iupshiftDown = c.idownshiftDown = false;
                    return;
                }
                // the owner's own frame (its player drives): merge what its passenger does
                MergeRemote(__instance, afterLocal: true);
            }
        }

        /// Per frame from Tick: send the captured controls (20 Hz while active, one release frame at the end).
        private static void ShotgunTick(float dt)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            bool active = _shotCar != null && pl != null && pl.Bsitting && pl.seat != null && pl.seat.Car == _shotCar && !pl.seat.driverSeat0 && pl.seat.driverSeat1;
            if (!active && !_shotWasActive) { _shotCar = null; return; }
            _shotSendAcc += dt;
            if (active && _shotSendAcc < 0.05f) return;
            _shotSendAcc = 0f;
            Ent e = null;
            if (_shotCar != null)
                foreach (var x in ByNet.Values) if (x.Proxy && x.Root != null && x.Root.car == _shotCar) { e = x; break; }
            if (e != null)
            {
                W.Reset(); W.U8(ShotgunIn); W.U32(e.NetId);
                W.U8((byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(active ? _shotSteer : 0f, -1f, 1f) * 127f));
                W.U8((byte)Mathf.RoundToInt(Mathf.Clamp01(active ? _shotHandbrake : 0f) * 255f));
                W.U16(active ? _shotHeld : (ushort)0); W.U16(_shotEdges);
                ToServer(W, true);
                ShotgunSent++;
            }
            _shotEdges = 0;
            _shotWasActive = active;
            if (!active) _shotCar = null;
        }

        /// Server: to the car's owner.
        private static void ServerShotgun(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        // ---- the owner's side
        private sealed class ShotIn { public float Steer, Handbrake, At; public ushort Held, Edges, FrameEdges; }
        private static readonly Dictionary<carscript, ShotIn> RemoteShot = new Dictionary<carscript, ShotIn>();

        private static void ApplyShotgun(NetReader r)
        {
            uint net = r.U32(); float steer = (sbyte)r.U8() / 127f, hb = r.U8() / 255f; ushort held = r.U16(), edges = r.U16();
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || e.Root == null || e.Root.car == null) return;
            if (!RemoteShot.TryGetValue(e.Root.car, out var s)) RemoteShot[e.Root.car] = s = new ShotIn();
            s.Steer = steer; s.Handbrake = hb; s.Held = held; s.Edges |= edges; s.At = Time.realtimeSinceStartup;
            ShotgunApplied++;
        }

        private static bool Fresh(carscript c, out ShotIn s) =>
            RemoteShot.TryGetValue(c, out s) && Time.realtimeSinceStartup - s.At < 0.3f;

        /// Every frame the car starts from ResetInput: the remote passenger's controls go in first (the owner's own
        /// GetPlayerInput, if its player sits in the car, then overwrites and MergeRemote adds them back).
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.ResetInput))]
        private static class ShotgunInject
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                if (!InSession || RemoteShot.Count == 0) return;
                if (RemoteShot.TryGetValue(__instance, out var s)) { s.FrameEdges = s.Edges; s.Edges = 0; }
                MergeRemote(__instance, afterLocal: false);
            }
        }

        private static void MergeRemote(carscript c, bool afterLocal)
        {
            if (!Fresh(c, out var s)) { if (s != null) s.FrameEdges = 0; return; }
            ushort ed = s.FrameEdges, held = s.Held;
            c.isteer = afterLocal ? Mathf.Clamp(c.isteer + s.Steer, -1f, 1f) : s.Steer;
            c.ihandbrake = Mathf.Max(c.ihandbrake, s.Handbrake);
            c.ihorn |= (held & BHorn) != 0; c.iignition |= (held & BIgnition) != 0;
            c.iLightsBDown |= (ed & ELights) != 0; c.ihandbrakeBDown |= (ed & EHandbrake) != 0; c.iignitionDown |= (ed & EIgnition) != 0;
            c.igrDown |= (ed & EGearR) != 0; c.ignDown |= (ed & EGearN) != 0; c.ig1Down |= (ed & EG1) != 0; c.ig2Down |= (ed & EG2) != 0;
            c.ig3Down |= (ed & EG3) != 0; c.ig4Down |= (ed & EG4) != 0; c.ig5Down |= (ed & EG5) != 0; c.ig6Down |= (ed & EG6) != 0;
            c.iupshiftDown |= (ed & EUp) != 0; c.idownshiftDown |= (ed & EDown) != 0;
        }

        /// Nobody sits in the owner's car (driving2 off there): carscript skips the shotgun parts of its frame — the
        /// steer, the light switch, the handbrake lever. Do those for the remote passenger, as the game would for a
        /// local one. (Gears need a driver in the game too: `if (!driving ...) return`.)
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.ControlStuff))]
        private static class ShotgunNoLocalPlayer
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(carscript __instance)
            {
                if (!InSession || RemoteShot.Count == 0 || __instance.driving2 || __instance.multiControlling) return;
                if (!Fresh(__instance, out var s)) return;
                __instance.steer = __instance.isteer * __instance.steerAngle;
                if (__instance.iLightsBDown) __instance.InteractSwitchLights();
                if (__instance.ihandbrakeBDown && !settingsscript.s.S.CBenableHandBrake) __instance.InteractHandbrake();
            }
        }

        internal static void ShotgunReset() { RemoteShot.Clear(); _shotCar = null; _shotEdges = 0; _shotWasActive = false; }
    }
}
