using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Car-to-car crashes between players. Each car is simulated by its owner; the other machines show it as a display
    /// copy — kinematic, moved to the owner's pose every frame. A kinematic body has infinite mass: a car driving into a
    /// copy hit a wall, and a copy driving into a car pushed it like a train (or, teleported into it, popped it out at
    /// the depenetration speed). Relaying the reaction impulse on top (the old contact relay) made both machines
    /// send each other ever larger shoves: a 40 km/h rear-end threw a car 356 m (crashtest, 2026-09-30).
    ///
    /// Now, like the game's own crash between two local cars: a copy that comes near a car simulated HERE becomes a real
    /// body with its real mass, steered along the owner's path (velocity servo in FixedUpdate). The moment it touches
    /// that car the steering lets go — both bodies react as the game's physics says, each machine's own car included —
    /// for as long as the owner's reaction takes to arrive (its network delay); then the steering takes over again,
    /// blending in, and the copy follows what the owner's car really did. Far from local cars, a copy is kinematic as
    /// before (cheap, exact). No impulses cross the network: every machine sees both cars, so every machine's own car
    /// gets its crash from its own physics (and the game's crash damage from its own change of speed).
    public static partial class Entities
    {
        public static bool PhysicalCopies = true;      // A/B: false = kinematic copies everywhere (the old behaviour)
        public static bool ContactRelayOn;              // A/B: the old impulse relay (double counts; off)
        public static float NearEnterM = 12f, NearExitM = 18f;
        public static float ServoGain = 10f, SpinGain = 10f, ServoRampS = 0.4f;
        public static float FreeMinS = 0.4f, FreeMaxS = 1.5f, CrashS = 0.3f;
        public static float ServoMaxAccel = 30f;       // m/s² of correction beyond the path's own motion (~3 g)
        public static float GhostMaxS = 2.0f;          // longest a copy passes through the car it hit while it returns
        public static bool GhostOnReturn;              // off (v0.57.63): the user saw the T-bone rammer phase into the hit car
                                                       // and both get slingshotted when collisions came back while overlapping
        public static long Ghosted, Unghosted;
        public static long PhysicalEnters, PhysicalExits, FreeWindows;
        public static string LastFree = "";

        private static readonly List<carscript> _localCars = new List<carscript>();
        private static readonly Dictionary<carscript, Ent> PhysicalCars = new Dictionary<carscript, Ent>();

        /// A physical copy's wheels get the owner's torques: carscript.Update sets them every frame from this machine's
        /// (empty) inputs through Throttle → SetWheel; overridden right after.
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.Throttle))]
        private static class CopyWheelTorques
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                if (PhysicalCars.Count == 0 || !PhysicalCars.TryGetValue(__instance, out var e) || !(e.Physical || e.CrashTaking) || e.WBrake == null || (!e.HasTorques && !e.CrashTaking)) return;
                var ws = WheelsOf(e);
                for (int i = 0; i < ws.Length && i < e.WBrake.Length; i++)
                {
                    if (ws[i].W != null) { ws[i].W.brakeTorque = e.WBrake[i]; ws[i].W.motorTorque = e.WMotor[i]; }
                    if (ws[i].W2 != null) { ws[i].W2.brakeTorque = e.WBrake[i]; ws[i].W2.motorTorque = e.WMotor[i]; }
                }
            }
        }
        private static float _localCarsAt = -10f;

        /// Runner.FixedUpdate: switch copies physical/kinematic by distance to cars simulated here, steer the physical ones.
        public static void FixedTick()
        {
            if (!InSession) return;
            TraceTick();
            float dt = Time.fixedDeltaTime;
            CrashHoldTick();
            if (IsHost) ServerCrashTick();
            if (Time.time - _localCarsAt > 0.5f)
            {
                _localCarsAt = Time.time;
                _localCars.Clear();
                // players' cars simulated here (our shared cars; the one we sit in) — not a scene-wide search: that grows
                // with the world and every player count (a parked world car hit by a copy stays the game's own business)
                foreach (var o in ByNet.Values)
                    if (!o.Proxy && o.Root != null && o.PartIndex < 0 && o.Root.car != null && o.Root.car.RB != null && !o.Root.car.RB.isKinematic) _localCars.Add(o.Root.car);
                var mine = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Car : null;
                if (mine != null && mine.RB != null && !mine.RB.isKinematic && !IsProxy(mine) && !_localCars.Contains(mine)) _localCars.Add(mine);
            }
            ColliderLodTick();
            TowTick(dt);
            CargoNearTick(dt);   // before the copies move this step: claimed before they touch
            WatchTick();
            if (CopiesMoveByBody) BodyMotionTick(dt);
            else foreach (var e in ByNet.Values) if (e.MovedByBody) StopBodyMotion(e);   // switched off: back to transform writes
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Root == null || e.Root.car == null || e.Ip == null || !e.Ip.Ready) { if (e.Physical && (e.Root == null || !e.Proxy)) e.Physical = false; continue; }
                var rb = e.Root.GetComponent<Rigidbody>();
                if (rb == null) continue;
                if (e.Physical && rb.isKinematic) e.Physical = false;   // re-bound after streaming: the copy's bodies were reset
                bool want = false;
                if (PhysicalCopies)
                {
                    float r = e.Physical ? NearExitM : NearEnterM;
                    var p = rb.position;
                    foreach (var c in _localCars)
                        if (c != null && c.RB != null && !c.RB.isKinematic && c.transform.root != e.Root.transform.root && (c.RB.position - p).sqrMagnitude < r * r) { want = true; break; }
                    if (e.Physical && (Time.time < e.FreeUntil + ServoRampS || e.GhostOn)) want = true;   // never leave mid-crash
                }
                if (want && !e.Physical) EnterPhysical(e, rb);
                else if (!want && e.Physical) ExitPhysical(e, rb);
                if (e.Physical) Servo(e, rb, dt);
            }
        }

        /// Copies move as one kinematic BODY in the physics step (MovePosition/MoveRotation + interpolation), the way the
        /// game moves a real car — not by setting the transform every frame. A transform write makes the physics engine
        /// re-sync every collider under it before the next query: 16 car01 copies = 3,619 colliders, and the player
        /// controller's raycasts paid for it — the laptop went from 16 to 80 ms a frame (copycost, v0.57.70; colliders
        /// off: 21 ms). Moved as a body, the colliders ride along like on the owner's car.
        public static bool CopiesMoveByBody = true;   // A/B: false = transform writes every frame (before v0.57.71)
        public static bool CopiesInterpolate = true;  // A/B (fpsdrill): rigidbody interpolation on body-moved copies
        public static long CopySweeps, CopyRebaseSeats, CopyTeleports;

        /// After every floating-origin shift: each body-moved copy placed (transform and body) where it is shown, in the new
        /// origin's coordinates, interpolation history dropped (BodyMotionTick turns it back on). Copies carry no visszarako,
        /// so the shift left them — and the next MovePosition, already in new coordinates, swept the kinematic copy across
        /// the whole shift in one step (every shift since copies move by body, v0.57.71): 201 km at 10,052,770 m/s after a test teleport
        /// (tpprobe trace, v0.57.97) — it dragged the player 200 km (fall-damage death) and flung the siphon hoses near it;
        /// a normal 2–4.5 km shift is the same sweep at ~100–225 km/s through whatever stands in its path.
        [HarmonyLib.HarmonyPatch(typeof(mainscript), nameof(mainscript.VisszaRakas))]
        private static class SeatCopiesAfterRebase
        {
            [HarmonyLib.HarmonyPrefix] private static void Pre(mainscript __instance, out Vector3d __state) => __state = __instance.visszarakva;
            [HarmonyLib.HarmonyPostfix]
            private static void Post(mainscript __instance, Vector3d __state)
            {
                if (__instance.visszarakva.x == __state.x && __instance.visszarakva.y == __state.y && __instance.visszarakva.z == __state.z) return;
                var d = (Vector3)(__instance.visszarakva - __state);
                foreach (var e in ByNet.Values)
                {
                    if (!e.Proxy || e.Root == null || e.Root.transform.parent != null) continue;
                    var rb = e.Root.GetComponent<Rigidbody>();
                    if (rb == null) continue;
                    var vr = e.Root.GetComponent<visszarako>();
                    bool shifted = vr != null && !vr.dont;
                    if (e.MovedByBody && rb.isKinematic)
                    {
                        // copies carry no visszarako: the shift moves neither their transform nor their body
                        var up = mainscript.UnityPosFromGlobal(e.ShownPos);
                        rb.interpolation = RigidbodyInterpolation.None;
                        e.Root.transform.position = up; rb.position = up;
                    }
                    else if (!shifted) { e.Root.transform.position += d; rb.position += d; }   // a simulated copy: keep its motion
                    else rb.position = e.Root.transform.position;
                    CopyRebaseSeats++;
                }
            }
        }

        private static void BodyMotionTick(float dt)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Physical || e.Root == null || e.Ip == null || !e.Ip.Ready) { if (e.MovedByBody && (!e.Proxy || e.Root == null)) e.MovedByBody = false; continue; }
                if (e.PartIndex >= 0 || e.Items.Count <= 1) { if (RidesParent(e)) continue; }
                var rb = e.Root.GetComponent<Rigidbody>();
                if (rb == null || !rb.isKinematic) continue;
                // held in a car (no collisions while held): placed each frame in the car's shown pose (Entities.Tick). Moved
                // from the physics step it was a step or two behind the car: 0.5 m at 50 km/h (heldincar.py)
                if (e.CarriedBy != 0) { if (rb.interpolation != RigidbodyInterpolation.None) rb.interpolation = RigidbodyInterpolation.None; continue; }
                EvalShown(e, now, dt, out var pos, out var rot);
                e.ShownPos = pos;
                if (e.Ip.Extrapolating) e.ExtrapFrames++;
                var up = mainscript.UnityPosFromGlobal(pos);
                if (!e.MovedByBody)
                {
                    e.MovedByBody = true;
                    e.OrigInterp = rb.interpolation;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.position = up; rb.rotation = rot;   // start where it is shown: no sweep from an old pose
                }
                var wantI = CopiesInterpolate ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
                if (rb.interpolation != wantI) rb.interpolation = wantI;
                // a teleport (the curve restarted) or anything a kinematic body cannot cover in a step without sweeping
                // through the world (> 50 m = 2,500 m/s): put it there, don't drive it there
                bool snap = e.Ip.TakeSnap();
                if (snap || (up - rb.position).sqrMagnitude > 2500f)
                {
                    if (!snap && CopySweeps++ < 20)
                        Plugin.Log.LogWarning($"copy sweep stopped: {e.Root.name} {(up - rb.position).magnitude:F0} m in one step (body {rb.position}, target {up}, parent {(e.Root.transform.parent != null ? e.Root.transform.parent.name : "none")})");
                    CopyTeleports++;
                    rb.interpolation = RigidbodyInterpolation.None;
                    e.Root.transform.SetPositionAndRotation(up, rot); rb.position = up; rb.rotation = rot;
                    MoveLimbs(e, rb, up, rot, true);
                    continue;
                }
                e.StepSpeed = (up - rb.position).magnitude / Mathf.Max(dt, 1e-4f); e.StepExtrap = e.Ip.Extrapolating;
                e.StepTurn = Quaternion.Angle(rb.rotation, rot) / Mathf.Max(dt, 1e-4f);
                rb.MovePosition(up); rb.MoveRotation(rot);
                MoveLimbs(e, rb, up, rot, false);
            }
        }

        /// Collider LOD for copies: a car copy far from this machine's player and from every car simulated here keeps only its
        /// own body's colliders; its parts' colliders (a car01 has ~180 of its ~226 on parts) come back within reach. At
        /// 14 car copies near, the parts' colliders cost the laptop ~30 ms a frame (fpsdrill v0.57.73: body/part colliders
        /// off → the frame's first physics query 37 → 6.8 ms) — and a slow machine then fell into the physics catch-up
        /// spiral (the game lets a frame run up to 5 s of physics steps) and froze. Close up nothing changes: crashes,
        /// shoving and shots at parts need them.
        public static bool CopyColliderLod = true;
        public static float PartCollidersOnM = 40f, PartCollidersOffM = 50f;
        public static long PartCollidersOff, PartCollidersOn;
        private static float _lodAt;
        private static void ColliderLodTick()
        {
            if (Time.time - _lodAt < 0.25f) return;
            _lodAt = Time.time;
            var pl = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.PartIndex >= 0 || e.Root.car == null) continue;
                bool want;
                if (!CopyColliderLod || !e.Proxy || e.Physical || e.CrashTaking) want = true;
                else
                {
                    var p = e.Root.transform.position;
                    float r = e.PartsColOff ? PartCollidersOnM : PartCollidersOffM, r2 = r * r;
                    want = (p - pl).sqrMagnitude < r2;
                    if (!want) foreach (var c in _localCars) if (c != null && (c.transform.position - p).sqrMagnitude < r2) { want = true; break; }
                }
                if (want == !e.PartsColOff) continue;
                SetPartColliders(e, want);
            }
        }

        private static void SetPartColliders(Ent e, bool on)
        {
            if (!on && e.PartCols == null)
            {
                // the parts' colliders: under the group's other items (bolted parts, things in it), not the car body's own
                e.PartCols = new List<Collider>();
                for (int i = 0; i < e.Items.Count; i++)
                {
                    var it = e.Items[i];
                    if (it == null || it == e.Root) continue;
                    foreach (var c in it.GetComponentsInChildren<Collider>(true)) if (c.enabled && !(c is WheelCollider)) e.PartCols.Add(c);
                }
            }
            if (e.PartCols == null) { e.PartsColOff = false; return; }
            foreach (var c in e.PartCols) if (c != null) c.enabled = on;
            if (on) { e.PartCols = null; PartCollidersOn++; } else PartCollidersOff++;
            e.PartsColOff = !on;
        }

        /// A vehicle's limbs (Bus01's back section, BusBack: its own body on a ConfigurableJoint to the front, 33 of its
        /// seats) are synced like a ragdoll's (save_ragdollpos; RagdollTick: the owner's poses relative to the root).
        /// RagdollTick set them each frame by transform — the right place, no velocity: a player standing in the back of
        /// another player's bus wasn't carried by its floor, slid to the rear wall and fell out of the bus (busrear
        /// --stand; the user, watching: "He fell off"; "players should be able to stand in the bus as they can in vanilla").
        /// On a copy moved as a body they move as bodies too, in the same physics step as the root, to the same synced
        /// pose: the floor has the bus's velocity, as on the owner's machine.
        public static bool VehicleLimbsByBody = true;   // A/B: false = before v0.65.53
        public static long LimbBodyMoves;
        internal static bool LimbsByBody(Ent e) => VehicleLimbsByBody && e.MovedByBody && e.Root != null && e.Root.car != null;

        private static void MoveLimbs(Ent e, Rigidbody rb, Vector3 up, Quaternion rot, bool snap)
        {
            if (!LimbsByBody(e) || e.Rag == null || !e.Rag.Has || !RagdollCopies.Contains(e)) return;
            var ls = Limbs(e); var g = e.Rag;
            if (ls == null || ls.Length != g.TPos.Length) return;
            for (int i = 0; i < ls.Length; i++)
            {
                var l = ls[i]; var lrb = l != null ? l.GetComponent<Rigidbody>() : null;
                if (lrb == null || lrb == rb || !lrb.isKinematic) continue;
                var p = up + rot * g.Pos[i]; var q = rot * g.Rot[i];
                if (snap) { lrb.interpolation = RigidbodyInterpolation.None; l.SetPositionAndRotation(p, q); lrb.position = p; lrb.rotation = q; }
                else
                {
                    if (lrb.interpolation != rb.interpolation) lrb.interpolation = rb.interpolation;
                    lrb.MovePosition(p); lrb.MoveRotation(q);
                }
                LimbBodyMoves++;
            }
        }

        internal static void StopBodyMotion(Ent e)
        {
            if (!e.MovedByBody) return;
            e.MovedByBody = false;
            var rb = e.Root != null ? e.Root.GetComponent<Rigidbody>() : null;
            if (rb != null) rb.interpolation = e.OrigInterp;
        }

        private static void EnterPhysical(Ent e, Rigidbody rb)
        {
            if (e.PartsColOff) SetPartColliders(e, true);
            e.Physical = true;
            e.PhysInterp = e.MovedByBody ? e.OrigInterp : rb.interpolation;
            e.MovedByBody = false;
            e.JointBodies.Clear();
            // parts hung on joints (doors, lids: attachablescript without parentAtAttach) — a kinematic part holds the
            // car body where the part was put last frame; they move as on the owner's machine while the copy is physical
            foreach (var j in e.Root.GetComponents<Joint>())
                if (j.connectedBody != null && j.connectedBody.isKinematic && e.MadeKinematic.Contains(j.connectedBody)) e.JointBodies.Add(j.connectedBody);
            foreach (var b in e.MadeKinematic)
                if (b != null && b != rb && b.isKinematic)
                    foreach (var j in b.GetComponents<Joint>())
                        if (j.connectedBody == rb) { e.JointBodies.Add(b); break; }
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.velocity = e.LastVel; rb.angularVelocity = Vector3.zero;
            foreach (var b in e.JointBodies) { b.isKinematic = false; b.velocity = e.LastVel; }
            e.HasServoPrev = false; e.FreeUntil = -10f; e.FreeFrom = -10f;
            PhysicalCars[e.Root.car] = e;
            if (e.Root.GetComponent<CopyContact>() == null) e.Root.gameObject.AddComponent<CopyContact>().E = e;
            else e.Root.GetComponent<CopyContact>().E = e;
            PhysicalEnters++;
        }

        private static void ExitPhysical(Ent e, Rigidbody rb)
        {
            if (e.GhostOn) Ghost(e, false);
            e.Physical = false;
            if (e.Root.car != null) PhysicalCars.Remove(e.Root.car);
            // the display takes over where the body is: the difference to the owner's path blends out (no snap)
            e.Ip.Absorb(mainscript.GlobalFromUnityPos(rb.position), rb.rotation);
            rb.interpolation = e.PhysInterp;
            rb.isKinematic = true;
            foreach (var b in e.JointBodies) if (b != null) b.isKinematic = true;
            e.JointBodies.Clear();
            PhysicalExits++;
        }

        /// Undo physical mode without the blend (ownership flips: SetProxy handles the bodies).
        internal static void DropPhysical(Ent e)
        {
            if (e.GhostOn) Ghost(e, false);
            if (!e.Physical) return;
            e.Physical = false;
            if (e.Root != null && e.Root.car != null) PhysicalCars.Remove(e.Root.car);
            var rb = e.Root != null ? e.Root.GetComponent<Rigidbody>() : null;
            if (rb != null) rb.interpolation = e.PhysInterp;
            e.JointBodies.Clear();
        }

        private static void Servo(Ent e, Rigidbody rb, float dt)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            EvalShown(e, now, dt, out var pos, out var rot);
            e.ShownPos = pos;
            if (e.Ip.TakeSnap())   // the owner's object teleported: so does the simulated copy (a servo would fling it)
            {
                CopyTeleports++;
                rb.position = mainscript.UnityPosFromGlobal(pos); rb.rotation = rot;
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                e.HasServoPrev = false;
                return;
            }
            Vector3 vT = e.HasServoPrev ? (Vector3)(pos - e.ServoPrevPos) / dt : e.LastVel;
            Vector3 wT = Vector3.zero;
            if (e.HasServoPrev)
            {
                (rot * Quaternion.Inverse(e.ServoPrevRot)).ToAngleAxis(out float a, out var ax);
                if (a > 180f) a -= 360f;
                if (!float.IsNaN(ax.x)) wT = ax * (a * Mathf.Deg2Rad / dt);
            }
            e.ServoPrevPos = pos; e.ServoPrevRot = rot; e.HasServoPrev = true;
            float t = Time.time;
            float w = t < e.FreeUntil ? 0f : t < e.FreeUntil + ServoRampS ? (t - e.FreeUntil) / ServoRampS : 1f;
            if (w <= 0f) return;   // crash in progress: the game's physics has it
            // the two machines' crashes differ (each simulated its own); steering the copy back to its owner's result must
            // not drive it through the car it hit here — at 100 km/h that threw the rammer to 30 m/s and spun the copy at
            // 400°/s (crashtest v0.57.60). The copy passes through that car until the two are apart.
            if (GhostOnReturn && e.FreeHit != null && !e.GhostOn && t - e.FreeUntil < 0.1f) Ghost(e, true);
            if (e.GhostOn && (t - e.FreeUntil > GhostMaxS || Apart(e))) Ghost(e, false);
            var target = mainscript.UnityPosFromGlobal(pos);
            var vServo = vT + (target - rb.position) * ServoGain;
            (rot * Quaternion.Inverse(rb.rotation)).ToAngleAxis(out float ea, out var eax);
            if (ea > 180f) ea -= 360f;
            var wServo = wT + (float.IsNaN(eax.x) ? Vector3.zero : eax * (ea * Mathf.Deg2Rad * SpinGain));
            var dv = Vector3.ClampMagnitude(vServo - rb.velocity, ServoMaxAccel * dt + (vT - rb.velocity).magnitude);   // follow the path's own motion; the correction on top is bounded
            rb.velocity = Vector3.Lerp(rb.velocity, rb.velocity + dv, w);
            rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, wServo, w);
        }

        private static void Ghost(Ent e, bool on)
        {
            if (e.Root == null || e.FreeHit == null) { e.GhostOn = false; return; }
            var mine = e.Root.GetComponentsInChildren<Collider>();
            var theirs = e.FreeHit.GetComponentsInChildren<Collider>();
            foreach (var a in mine)
                if (a != null && !a.isTrigger)
                    foreach (var b in theirs)
                        if (b != null && !b.isTrigger) Physics.IgnoreCollision(a, b, on);
            e.GhostOn = on;
            if (on) Ghosted++; else { Unghosted++; e.FreeHit = null; }
        }

        /// The copy's and the hit car's bounds no longer overlap (0.3 m apart).
        private static bool Apart(Ent e)
        {
            if (e.FreeHit == null) return true;
            Bounds A = default, B = default; bool ha = false, hb = false;
            foreach (var c in e.Root.GetComponentsInChildren<Collider>()) if (!c.isTrigger) { if (ha) A.Encapsulate(c.bounds); else { A = c.bounds; ha = true; } }
            foreach (var c in e.FreeHit.GetComponentsInChildren<Collider>()) if (!c.isTrigger) { if (hb) B.Encapsulate(c.bounds); else { B = c.bounds; hb = true; } }
            if (!ha || !hb) return true;
            A.Expand(0.6f);
            return !A.Intersects(B);
        }

        /// How long until the copy's data shows the owner's reaction to this crash: the owner meets the crash only when
        /// OUR car's copy reaches theirs (one display delay after us: one-way trip + interpolation delay), and its reaction
        /// comes back the same way — two display delays — plus the crash itself (~0.3 s of contact and rebound).
        /// 0.2 s was too short (crashtest LAN, v0.57.59): the copy was steered back into the rammer while the owner's
        /// reaction was still in flight — the cars kept 55 % of their momentum (single player ~100 %).
        private static float FreeSeconds(Ent e)
        {
            float oneWay = Mp.PingMs() / 2000f;
            return Mathf.Clamp(2f * (e.Ip.DelayMs / 1000f + oneWay) + CrashS, FreeMinS, FreeMaxS);
        }

        /// On a copy's root while it's physical: contact with a body simulated here starts the free window.
        internal sealed class CopyContact : MonoBehaviour
        {
            [System.NonSerialized] public Ent E;
            private void OnCollisionEnter(Collision c) => Touch(c);
            private void OnCollisionStay(Collision c) => Touch(c);
            private void Touch(Collision c)
            {
                var e = E;
                if (e == null || !e.Physical || c.rigidbody == null || c.rigidbody.isKinematic) return;
                if (IsProxy(c.rigidbody)) return;                       // another copy: its owner handles it
                if (c.rigidbody.transform.root == transform.root) return;
                float t = Time.time;
                if (t < e.FreeUntil) return;                            // already free
                if (t - e.FreeFrom < FreeMaxS + ServoRampS) return;    // one window per crash; sustained shoving is steered
                if (c.relativeVelocity.magnitude < 0.5f) return;
                if (TryTakeCrash(e, c.rigidbody, c.relativeVelocity.magnitude)) return;   // approach B: this machine simulates the crash
                e.FreeFrom = t; e.FreeUntil = t + FreeSeconds(e); e.FreeHit = c.rigidbody.transform.root;
                FreeWindows++;
                LastFree = "net " + e.NetId + " free " + (e.FreeUntil - t).ToString("F2") + " s, hit " + c.rigidbody.transform.root.name + " at " + c.relativeVelocity.magnitude.ToString("F1") + " m/s";
            }
        }

        public static string PhysicalStatus()
        {
            int phys = 0; foreach (var e in ByNet.Values) if (e.Physical) phys++;
            return "{\"enabled\":" + (PhysicalCopies ? "true" : "false") + ",\"contactRelay\":" + (ContactRelayOn ? "true" : "false") + ",\"physicalNow\":" + phys +
                   ",\"distFreezeSkipped\":" + DistFreezeSkipped + ",\"copySweeps\":" + CopySweeps + ",\"copyTeleports\":" + CopyTeleports + ",\"ipSnaps\":" + PoseInterpolator.Snaps + ",\"ipIdleRestarts\":" + PoseInterpolator.IdleRestarts + ",\"ipBigCorrSnaps\":" + PoseInterpolator.BigCorrSnaps + ",\"ipVelClamped\":" + PoseInterpolator.VelClamped + ",\"copyRebaseSeats\":" + CopyRebaseSeats + ",\"partCollidersOff\":" + PartCollidersOff + ",\"partCollidersOn\":" + PartCollidersOn + ",\"ghosted\":" + Ghosted + ",\"unghosted\":" + Unghosted + ",\"enters\":" + PhysicalEnters + ",\"exits\":" + PhysicalExits + ",\"freeWindows\":" + FreeWindows + ",\"lastFree\":" + Json.Str(LastFree) + "}";
        }
    }
}
