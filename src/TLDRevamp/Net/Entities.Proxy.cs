using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Display copies: what the owner does, a copy only shows — no crash damage, no parts falling off on their own,
    /// wheels rebuilt from the owner's suspension/steer/rpm.
    public static partial class Entities
    {
        /// Items shown as display copies on this machine: the game's physical consequences (crash damage, parts falling
        /// off) belong to the owner only.
        internal static readonly HashSet<GameObject> ProxyItems = new HashSet<GameObject>();
        public static long SuppressedDamage, SuppressedFallOff;

        internal static bool IsProxy(Component c) => c != null && ProxyItems.Count > 0 && (ProxyItems.Contains(c.gameObject) || ProxyItems.Contains(c.transform.root.gameObject));

        /// A car that just became ours: no crash detection while its wheels spin up from the copy's standstill.
        internal static readonly Dictionary<carscript, float> DamageGraceUntil = new Dictionary<carscript, float>();
        public static long HandoverDamageSuppressed;

        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.DamageStuff))]
        private static class NoProxyDamage
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(carscript __instance, float v)
            {
                if (IsProxy(__instance)) { SuppressedDamage++; return false; }
                var e = OwnEntity(__instance.GetComponent<tosaveitemscript>());
                if (e != null && Time.time < e.HitchUntil && v > 90f)
                    SessionLog.Event("speed-anomaly", e.NetId, epoch: e.Epoch, value: v, result: "hitch crash handover");
                if (DamageGraceUntil.Count > 0 && DamageGraceUntil.TryGetValue(__instance, out float until))
                {
                    if (Time.time < until) { SuppressedDamage++; HandoverDamageSuppressed++; return false; }
                    DamageGraceUntil.Remove(__instance);
                }
                return true;
            }
        }

        /// The game's item streaming (itemPlaceRemoveScript.RemoveStuff, every second) unfreezes every item within
        /// itemUnFreezeDist (100 m) of a player: RB.isKinematic = false — our display copies included. Every other
        /// player's car near you became a free body with its wheels' colliders gone, dragged along by our interpolation
        /// and pushed out of the ground each physics step: 14 such copies cost the laptop ~36 ms a frame (the desktop,
        /// whose copies happened to be just past 100 m, ~1 ms; copydiag v0.57.76). A copy's body mode is ours.
        public static long DistFreezeSkipped;

        [HarmonyLib.HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.DistUnFreezeRB))]
        private static class NoDistUnfreezeOnCopies
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(tosaveitemscript __instance)
            {
                var e = OwnEntity(__instance);
                if (!IsProxy(__instance) && (e == null || !e.HitchWaiting)) return true;
                DistFreezeSkipped++;
                return false;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.DistFreezeRB))]
        private static class NoDistFreezeOnCopies
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(tosaveitemscript __instance)
            {
                var e = OwnEntity(__instance);
                if (!IsProxy(__instance) && (e == null || !e.HitchWaiting)) return true;
                DistFreezeSkipped++;
                return false;
            }
        }

        internal static bool _applyingDetach;
        /// Test only (A/B of report #5): replay a part-off with the old bare Detach instead of the game's un-bolt.
        public static bool ReplayBareDetach;

        /// True while the local player's own controller runs (fpscontroller.Update of mainscript.s.player): the
        /// wrench, the E-press dismount, picking up an attached item. Everything else that detaches is the game's own
        /// doing on this machine.
        private static bool _playerActing; private static int _playerActingFrame = -1;
        internal static bool PlayerActing => TestAsPlayer || (_playerActing && _playerActingFrame == Time.frameCount);
        public static bool TestAsPlayer;   // test (bridge): a bridge command counts as the local player's own action

        /// A part this player dismounted on another player's car: in single player the dismount puts it in the player's
        /// hand (fpscontroller: UnCraft, then Pickup). Here the owner takes it off one network delay later; when it comes
        /// off on this machine the player gets it in hand the same way — if the hands are still free and it is in reach.
        /// Before v0.65.5 nothing came to hand: the part dropped off a moment later at the owner's word, and pressing
        /// again sent more requests (the friend's rear seat came off 7 times in 30 s, 2026-10-03).
        private static tosaveitemscript _handFor; private static float _handAt;
        public static float HandOverS = 3f, HandOverM = 4f;
        public static long HandedOver;
        internal static void HandOver(tosaveitemscript it)
        {
            if (it == null || _handFor != it) return;
            _handFor = null;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || it.P == null || pl.pickedUp != null || Time.realtimeSinceStartup - _handAt > HandOverS) return;
            if ((it.transform.position - pl.transform.position).magnitude > HandOverM) return;
            // into the hand: the game drops a held item farther than dropDist (1.25 m) from the hold point at once — in
            // single player you dismount what you look at up close; here the part arrives where the OWNER's game put it,
            // a network delay later (buglist2m run 3: handed over, dropped the same frame)
            var cam = pl.Cam != null ? pl.Cam.transform : pl.transform;
            var hold = cam.position + cam.forward * 1f;
            var rb = it.GetComponent<Rigidbody>();
            it.transform.position = hold;
            if (rb != null) { rb.position = hold; if (!rb.isKinematic) rb.velocity = Vector3.zero; }
            pl.Pickup(it.P, hold);   // the game's own pickup (claims it: PickupClaim)
            HandedOver++;
        }
        public static long AutoDetachesIgnored;

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class PlayerActScope
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance)
            {
                _playerActing = mainscript.s != null && __instance == mainscript.s.player;
                _playerActingFrame = Time.frameCount;
            }
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() => _playerActing = false;
        }

        /// Autopilot traffic: another player's car shown here as a display copy moves by pose updates, so its rigidbody
        /// reads 0 — the owner's velocity instead. Null: not a display copy here (its physics body is the truth).
        public static bool ProxyByOwnRoot = true;   // A/B: false = keyed by transform.root (v0.68.1)
        private static readonly Dictionary<Transform, Ent> _proxyByRoot = new Dictionary<Transform, Ent>();
        private static int _proxyByRootFrame = -1;
        internal static Vector3? ProxyVelocity(Transform root)
        {
            if (!InSession || root == null) return null;
            if (_proxyByRootFrame != Time.frameCount)
            {
                _proxyByRootFrame = Time.frameCount;
                _proxyByRoot.Clear();
                // keyed by the copy's own root object: a copy stored in a container is parented into that car, and keyed by
                // its transform.root it took the car's place — the jerry can was its own "container", always inside it,
                // so a can set down 3 m from the car stayed on the back seat on every other screen (pallet.py C)
                foreach (var e in ByNet.Values)
                    if (e.Proxy && e.Root != null) _proxyByRoot[ProxyByOwnRoot ? e.Root.transform : e.Root.transform.root] = e;
            }
            return _proxyByRoot.TryGetValue(root, out var pe) ? pe.LastVel : (Vector3?)null;
        }

        public static long DetachReqsSent;

        public static long PartsReported, PartsApplied, PartOffRejected, ClaimsRefused, ClaimsSent;
        private static uint _claimedFor;   // one claim per sit-down
        public static string PartRejects = "";

        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.FallOFf))]
        private static class NoProxyFallOff
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(attachablescript __instance) { if (!IsProxy(__instance)) return true; SuppressedFallOff++; return false; }
        }

        // ---- wheels: the game places each wheel mesh from its WheelCollider pose (wheelgraphicsscript.Graphics). On a
        // display copy no wheel physics runs, so the owner sends what the pose is made of — suspension travel along the
        // collider's up axis, steer angle, rpm — and the copy rebuilds the same pose (spin integrated from rpm: a wheel
        // turns ~180° between two 20 Hz states at speed, sampling its angle would look wrong).
        private static wheelgraphicsscript[] WheelsOf(Ent e)
        {
            if (e.Wheels == null || e.Wheels.Length == 0) e.Wheels = e.Root.GetComponentsInChildren<wheelgraphicsscript>(true);
            return e.Wheels;
        }

        private static void WriteWheels(NetWriter w, Ent e)
        {
            var ws = e.PartIndex >= 0 ? new wheelgraphicsscript[0] : WheelsOf(e);
            // torques only matter to a copy that may turn physical (near a car simulated on its machine) — sent only while
            // another player's car is within TorqueRadiusM here: 16 players side by side would otherwise add ~77 KB/s of
            // host upload for data no one uses. Flag in the count byte's top bit.
            bool torques = ws.Length > 0 && NearOtherPlayersCar(e);
            w.U8((byte)(Math.Min(ws.Length, 16) | (torques ? 0x80 : 0)));
            for (int i = 0; i < ws.Length && i < 16; i++)
            {
                var W = ws[i].W;
                float travel = 0f, steer = 0f, rpm = 0f;
                if (W != null)
                {
                    // what the owner SEES: the wheel mesh, where the game's Graphics() put it (GetWorldPose, paired wheels
                    // averaged). The collider's own pose can be stale - a spawned Car10Full sent −647 suspensions (≈130 m,
                    // likely from before an origin shift) while its mesh sat at 0.10: clamped, every copy wheel hung 0.2 m
                    // low (leasepair drift6 3b; the "one-off" 0.2 m of v0.64.5). The copy puts the mesh back the same way.
                    Vector3 pos;
                    if (ws[i].T != null) pos = ws[i].T.position;
                    else
                    {
                        W.GetWorldPose(out pos, out _);
                        if (ws[i].W2 != null) { ws[i].W2.GetWorldPose(out var p2, out _); pos = (pos + p2) * 0.5f; }
                    }
                    // where the wheel sits in its suspension: 0 = the collider's center (fully compressed), 1 = center −
                    // suspensionDistance (hanging). Center and distance come from the prefab (the game never changes them),
                    // so both machines turn the fraction back into the same place. Before v0.64.5 this was the distance from
                    // the collider's origin, packed into ±25.4 cm — vans and pickups have center.y −0.25 and hang to 0.45:
                    // their copies' wheels sat up to 0.2 m too high (leasepair 3b, wheelprobe.py).
                    travel = Vector3.Dot(W.transform.TransformPoint(W.center) - pos, W.transform.up) / Mathf.Max(W.suspensionDistance, 0.01f);
                    steer = W.steerAngle; rpm = Mathf.Abs(W.rpm) <= 2f ? 0f : W.rpm;
                }
                // 4 bytes: travel in 0.5 % of the suspension (−0.135…1.135), steer 0.5° steps (±63°), rpm i16
                w.U8((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt((travel - 0.5f) * 200f), -127, 127));
                w.U8((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(steer * 2f), -127, 127));
                w.U16((ushort)(short)Mathf.Clamp(Mathf.RoundToInt(rpm), -32767, 32767));
            }
            // the torques the owner's car puts on each wheel: a physical copy brakes and drives like the car — a seated
            // player's automatic gearbox holds the handbrake at low rpm (carscript.AutoHandBrake); a copy rolling free where
            // the car is braked flew off in a crash the car shrugged off (crashtest 100 km/h). 2 bytes per wheel:
            // brake 0–16320 Nm in 64 Nm steps, motor ±4064 Nm in 32 Nm steps.
            if (torques)
                for (int i = 0; i < ws.Length && i < 16; i++)
                {
                    var W = ws[i].W;
                    w.U8((byte)Mathf.Clamp(Mathf.RoundToInt((W != null ? W.brakeTorque : 0f) / 64f), 0, 255));
                    w.U8((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt((W != null ? W.motorTorque : 0f) / 32f), -127, 127));
                }
        }

        public static float TorqueRadiusM = 40f;
        /// Owner side: is another player's car (a copy here) near this car?
        private static bool NearOtherPlayersCar(Ent e)
        {
            if (e.Root == null) return false;
            var p = e.Root.transform.position; float r2 = TorqueRadiusM * TorqueRadiusM;
            foreach (var o in ByNet.Values)
                if (o.Proxy && o.Root != null && o.PartIndex < 0 && o.Root.car != null && (o.Root.transform.position - p).sqrMagnitude < r2) return true;
            return false;
        }

        private static void ReadWheels(NetReader r, Ent e)
        {
            int nf = r.U8(); int n = nf & 0x7F; bool torques = (nf & 0x80) != 0;
            if (e.WTravel == null || e.WTravel.Length != n) { e.WTravel = new float[n]; e.WSteer = new float[n]; e.WRpm = new float[n]; e.WSpin = new float[n]; e.WBrake = new float[n]; e.WMotor = new float[n]; }
            for (int i = 0; i < n; i++)
            {
                e.WTravel[i] = (sbyte)r.U8() / 200f + 0.5f; e.WSteer[i] = (sbyte)r.U8() / 2f; e.WRpm[i] = (short)r.U16();
            }
            e.HasTorques = torques;
            if (torques) for (int i = 0; i < n; i++) { e.WBrake[i] = r.U8() * 64f; e.WMotor[i] = (sbyte)r.U8() * 32f; }
        }

        // ---- engine: enginescript.Update simulates (burns fuel from the tank — gated only by the official MP's host check —
        // heats the engine, rolls start chances). On a display copy that would drain fuel on the watching machine; so
        // the simulation is skipped there, and the owner's running/starter/rpm drive the game's own sound curves.
        internal static readonly Dictionary<enginescript, Ent> ProxyEngineOwner = new Dictionary<enginescript, Ent>();

        private static void WriteEngine(NetWriter w, Ent e)
        {
            var eng = e.PartIndex < 0 && e.Root != null && e.Root.car != null ? e.Root.car.Engine : null;
            if (eng == null) { w.U8(0); return; }
            w.U8((byte)(1 | (eng.running ? 2 : 0) | (eng.start ? 4 : 0)));
            w.U16((ushort)Mathf.Clamp(Mathf.RoundToInt(eng.rpm), 0, 65535));
        }

        private static void ReadEngine(NetReader r, Ent e)
        {
            byte f = r.U8();
            if ((f & 1) == 0) return;
            e.EngRunning = (f & 2) != 0; e.EngStart = (f & 4) != 0; e.EngRpm = r.U16();
            if (!r.Bad) BindProxyEngine(e);
        }

        [HarmonyLib.HarmonyPatch(typeof(enginescript), "Update")]
        private static class ProxyEngine
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(enginescript __instance)
            {
                if (ProxyEngineOwner.Count == 0 || !ProxyEngineOwner.TryGetValue(__instance, out var e)) return true;
                if (!e.Proxy || e.Root == null || e.Root.car == null || e.Root.car.Engine != __instance) return true;
                var g = __instance;
                g.rpm = Mathf.Lerp(g.rpm, e.EngRpm, 1f - Mathf.Exp(-10f * Time.deltaTime));
                g.running = e.EngRunning;
                g.start = e.EngStart;
                if (g.SAlap == null || g.SMotor == null) return false;
                if (g.running)
                {
                    // the game's own sound curves (enginescript.Update)
                    g.SAlap.volume = Mathf.InverseLerp(g.SMaxAlapRpm, g.SMinAlapRpm, g.rpm) * g.SMaxAlapSound;
                    g.SMotor.volume = Mathf.InverseLerp(g.SMinMotorRpm, g.SMaxMotorRpm, g.rpm) * g.SMaxMotorSound;
                    g.SAlap.pitch = Mathf.LerpUnclamped(g.minAlapPitch, g.maxAlapPitch, Mathf.InverseLerp(0f, g.SMaxAlapRpm, g.rpm) + Mathf.InverseLerp(g.SMaxAlapRpm, g.SMaxAlapRpm * 2f, g.rpm));
                    g.SMotor.pitch = Mathf.LerpUnclamped(g.minMotorPitch, g.maxMotorPitch, Mathf.InverseLerp(g.SMinMotorRpm, g.maxRpm, g.rpm) + Mathf.InverseLerp(g.maxRpm, g.maxRpm * 2f, g.rpm));
                    if (!g.SAlap.isPlaying) g.SAlap.Play();
                    if (!g.SMotor.isPlaying) g.SMotor.Play();
                }
                else { g.SAlap.Stop(); g.SMotor.Stop(); }
                return false;
            }
        }

        internal static readonly Dictionary<wheelgraphicsscript, Ent> ProxyWheelOwner = new Dictionary<wheelgraphicsscript, Ent>();
        internal static readonly Dictionary<wheelgraphicsscript, int> ProxyWheelIndex = new Dictionary<wheelgraphicsscript, int>();

        [HarmonyLib.HarmonyPatch(typeof(wheelgraphicsscript), nameof(wheelgraphicsscript.Graphics))]
        private static class ProxyWheelGraphics
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(wheelgraphicsscript __instance)
            {
                if (ProxyWheelOwner.Count == 0 || !ProxyWheelOwner.TryGetValue(__instance, out var e)) return true;
                int i = ProxyWheelIndex[__instance];
                var W = __instance.W;
                if (W == null || __instance.T == null || e.WTravel == null || i >= e.WTravel.Length) return false;
                e.WSpin[i] = Mathf.Repeat(e.WSpin[i] + e.WRpm[i] * 6f * Time.deltaTime, 360f);  // rpm → degrees per second
                var V = W.transform.TransformPoint(W.center) - W.transform.up * (e.WTravel[i] * W.suspensionDistance);
                var Q = W.transform.rotation * Quaternion.Euler(0f, e.WSteer[i], 0f) * Quaternion.Euler(e.WSpin[i], 0f, 0f);
                var T = __instance.T;
                T.position = V;
                if (__instance.keepUpright)
                {
                    var forward = Q * Vector3.forward;
                    T.rotation = Quaternion.LookRotation(__instance.keepUprightParent.TransformDirection(__instance.keepUprightPAxis)) * Quaternion.FromToRotation(Vector3.forward, __instance.keepUprightTAxis);
                    T.Rotate(__instance.keepUprightTAxis, 0f - Vector3.SignedAngle(forward, T.forward, T.TransformDirection(__instance.keepUprightTAxis)), Space.Self);
                    T.Rotate(__instance.transform.root.up, e.WSteer[i], Space.World);
                }
                else T.rotation = Q;
                return false;
            }
        }
    }
}
