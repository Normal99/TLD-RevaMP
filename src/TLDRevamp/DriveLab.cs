using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    /// Test driver for reproducible driving benchmarks: seats the player in a car's driver seat and feeds the game's own
    /// input values (inputscript.i.throttle/steer/brake), so the car is driven exactly as if keys were pressed.
    ///
    /// Autopilot: follows the road (prefers the main road). Each frame it takes the nearest road point to a spot
    /// Lookahead metres ahead of the car and steers towards it; throttle/brake hold TargetSpeed (m/s).
    /// Needs automatic shifting (settingsscript.s.S.BAutoShift): holding throttle then also starts the engine.
    ///
    /// Bridge: `drivein` (nearest car), `autodrive on <m/s>` / `autodrive off` / `autodrive` (stats), `driveout`.
    [HarmonyPatch]
    public static class DriveLab
    {
        public static bool Active;
        public static float TargetSpeed = 20f;   // m/s
        public static float Lookahead = 18f;     // m
        public static float SteerFullDeg = 25f;  // heading error that gives full steering
        public static float LaneOffset = 1.8f;   // m to the right of the road centre line (right lane)
        public static float AvoidLook = 30f;     // m ahead checked for obstacles. Longer (2.5 s of travel) + braking was
                                                 // tried: it braked for roadside objects on curves and fell into reverse.
        public static float AvoidSide = 3.5f;    // m sideways to pass an obstacle

        // Stats since `autodrive on`
        public static float Distance, MaxCrossTrack, StuckSeconds, Seconds;
        public static int OffRoadFrames, Frames, AvoidEvents;
        public static string LastObstacle = "";
        private static float _avoidOffset, _avoidUntil;
        // stuck recovery: stopped against something (a rock the sweep let through, a parked car) — back off with the
        // wheel turned the other way, then pass on the other side (seed 20260924: stuck for good at a big rock 0.6 km
        // and at a parked car 1.7 km from spawn — tools/shiftdrive.py never reached an origin shift)
        private static float _stoppedFor, _reverseUntil, _lastSteer;
        public static long Recoveries;
        public static bool Recover = true;   // off: never back off (shiftdrive takeovers: a back-off turned the car round, run 11)
        private static Collider _avoiding;

        private static List<roadGenScript.roadClass> _roads;
        private static int _roadsFrame = -999;
        private static int _handbrakeToggleFrame = -999;

        [HarmonyPatch(typeof(inputscript), "Update")]
        [HarmonyPostfix]
        private static void Postfix(inputscript __instance)
        {
            if (!Active) return;
            var p = mainscript.s != null ? mainscript.s.player : null;
            var car = p != null ? p.Car : null;
            if (car == null || car.RB == null || !car.isPlayerDriving) return;
            Drive(__instance, car);
        }

        /// Keep the driver looking straight ahead during scripted drives (the view decides what gets rendered and so
        /// the frame times; a turned head made a pass INVALID, 2026-09-25). Only while `autodrive` is active.
        public static bool LookAhead = true;

        [HarmonyPatch(typeof(fpscontroller), "Update")]
        [HarmonyPostfix]
        private static void HoldView(fpscontroller __instance)
        {
            if (!Active || !LookAhead || __instance != mainscript.s.player || __instance.seat == null) return;
            __instance.FxRot = 0f;
            __instance.FyRot = __instance.seat.defYrot;
        }

        private static void Drive(inputscript input, carscript car)
        {
            var t = car.RB.transform;
            Vector3 fwd = t.forward; fwd.y = 0f; fwd.Normalize();
            float speed = Vector3.Dot(car.RB.velocity, fwd);

            // Road target
            float steer = 0f;
            var gpos = mainscript.GlobalFromUnityPos(t.position);
            if (Time.frameCount - _roadsFrame >= 15 || _roads == null)
            {
                _roads = mainSetRoads(gpos);
                _roadsFrame = Time.frameCount;
            }
            if (_roads.Count > 0)
            {
                roadGenScript.NearestPointOnRoadBone(gpos, _roads, out var here);
                var h = here; h.y = gpos.y;
                float cross = (float)(h - gpos).magnitude;
                if (cross > MaxCrossTrack) MaxCrossTrack = cross;
                if (cross > 8f) OffRoadFrames++;

                var ahead = gpos + fwd * Lookahead;
                // at an intersection keep the road that continues our heading (the main road through it), not the
                // nearest bone of a crossing road
                var onward = _roads.FindAll(r => { var d = r.end - r.start; d.y = 0; return Vector3.Dot(((Vector3)d).normalized, fwd) > 0.25f; });
                var pool = onward.Count > 0 ? onward : _roads;
                var road = roadGenScript.NearestPointOnRoadBone(ahead, pool, out var target);
                if (road != null)
                {
                    var dir = road.end - road.start; dir.y = 0;
                    var right = Vector3.Cross(Vector3.up, ((Vector3)dir).normalized);
                    if (Vector3.Dot((Vector3)dir, fwd) < 0f) right = -right; // driving against the bone direction
                    Avoid(car, t, (Vector3)(target - gpos), right);
                    float off = LaneOffset + (Time.time < _avoidUntil ? _avoidOffset : 0f);
                    target = target + right * off;
                }
                Vector3 to = (Vector3)(target - gpos); to.y = 0f;
                if (to.sqrMagnitude > 0.01f)
                    steer = Mathf.Clamp(Vector3.SignedAngle(fwd, to, Vector3.up) / SteerFullDeg, -1f, 1f);
            }

            // Speed hold; a real obstacle ahead caps the speed (creep past it, don't ram it)
            float want = _obstacleDist < 25f ? Mathf.Min(TargetSpeed, 6f) : TargetSpeed;
            float err = want - speed;
            float throttle = Mathf.Clamp01(0.35f + err * 0.15f);
            float brake = err < -3f ? Mathf.Clamp01(-err * 0.1f) : 0f;
            if (brake > 0f) throttle = 0f;

            float dtr = Time.deltaTime;
            if (Time.time < _reverseUntil)
            {
                // the automatic gearbox: brake held at a standstill selects reverse and backs up (carscript.AutoShift)
                steer = -Mathf.Sign(_lastSteer == 0f ? 1f : _lastSteer);
                throttle = 0f; brake = 1f;
            }
            else
            {
                if (Mathf.Abs(speed) < 1f && Seconds > 8f && TargetSpeed > 1f) _stoppedFor += dtr; else _stoppedFor = 0f;
                if (_stoppedFor > 3f && Recover)
                {
                    _stoppedFor = 0f; _reverseUntil = Time.time + 2.5f; Recoveries++;
                    _avoidOffset = -_avoidOffset; if (_avoidOffset == 0f) _avoidOffset = AvoidSide;
                    _avoidUntil = Time.time + 2.5f + 4f;   // then pass on the other side
                }
                _lastSteer = steer;
            }

            input.steer = steer;
            input.throttle = throttle;
            input.brake = brake;
            input.handbrake = 0f;
            // Hold Tab (equiprecenter), as a player would to face forward: the game resets the head lean and eases the
            // view to the seat's straight-ahead angles (fpscontroller, sitting branch). Nothing is held in the hands.
            if (LookAhead) input.recenter = true;
            // Spawned cars start with the parking brake on; release it the way a player does (one toggle key press)
            if (car.BhandBrake && Time.frameCount - _handbrakeToggleFrame > 30)
            {
                input.handbrakeBDown = true;
                _handbrakeToggleFrame = Time.frameCount;
            }
            input.clutch = 0f;

            float dt = Time.deltaTime;
            Frames++;
            Seconds += dt;
            Distance += Mathf.Abs(speed) * dt;
            if (Mathf.Abs(speed) < 1f && Seconds > 8f) StuckSeconds += dt;
        }

        /// Obstacle check along the path: a sphere swept from the car towards the road target. Anything solid that
        /// isn't the road, terrain or the car itself (e.g. rocks the world generator put on the road) → pass it on
        /// the side away from it for a moment.
        private static float _obstacleDist = float.PositiveInfinity;   // nearest real obstacle ahead (m)
        private static void Avoid(carscript car, Transform t, Vector3 toTarget, Vector3 right)
        {
            if (Time.frameCount % 3 != 0) return;   // between scans the last distance holds (no flicker in the speed cap)
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return;
            var origin = t.position + Vector3.up * 0.8f;
            RaycastHit best = default;
            bool found = false;
            foreach (var h in Physics.SphereCastAll(origin, 1.3f, toTarget.normalized, AvoidLook, ~0, QueryTriggerInteraction.Ignore))
            {
                var root = h.collider.transform.root;
                if (root == t.root || root == mainscript.s.player.transform.root) continue;
                if (h.collider is WheelCollider || h.collider.GetType().Name == "TerrainCollider") continue;
                if (root.name == "RoadParent" || root.name == "TerrainParent" || h.collider.name == "RoadCollider") continue;
                if (h.collider.name == "Plane" || root.name == "Map") continue;   // the ground: not an obstacle
                if (h.distance == 0f) continue; // overlapping at start (the car's own surroundings)
                if (!found || h.distance < best.distance) { best = h; found = true; }
            }
            _obstacleDist = found ? best.distance : float.PositiveInfinity;
            if (!found) return;
            if (_avoiding != best.collider)
            {
                _avoiding = best.collider;
                AvoidEvents++;
                LastObstacle = best.collider.transform.parent != null ? best.collider.transform.parent.name + "/" + best.collider.name : best.collider.name;
                // pass on the side away from the obstacle (relative to the lane we're aiming for)
                float side = Vector3.Dot(best.collider.bounds.center - t.position, right) - LaneOffset;
                _avoidOffset = side > 0f ? -AvoidSide : AvoidSide;
            }
            _avoidUntil = Time.time + 1.5f;
        }

        private static List<roadGenScript.roadClass> mainSetRoads(Vector3d gpos)
        {
            var all = mapSettingScript.RoadsNear(gpos);
            var main = all.FindAll(r => r.generated && r.roadType == roadGenScript.roadTypeEmu.mainRoad);
            return main.Count > 0 ? main : all.FindAll(r => r.generated);
        }

        public static string Start(float speed)
        {
            TargetSpeed = speed;
            Distance = MaxCrossTrack = StuckSeconds = Seconds = 0f;
            OffRoadFrames = Frames = AvoidEvents = 0;
            LastObstacle = "";
            _avoiding = null; _avoidUntil = 0f; _stoppedFor = 0f; _reverseUntil = 0f;
            _roads = null;
            Active = true;
            return Stats();
        }

        public static string Stop()
        {
            Active = false;
            return Stats();
        }

        /// Seat the player in the driver seat of the car nearest to `near` (Unity coordinates; default: the player).
        public static string GetIn(Vector3? near = null)
        {
            var p = mainscript.s.player;
            Vector3 at = near ?? p.transform.position;
            seatscript best = null;
            float bestD = float.MaxValue;
            foreach (var s in Object.FindObjectsOfType<seatscript>())
            {
                if (!s.driverSeat0 || s.Car == null || !s.FreeCanSit()) continue;
                float d = (s.transform.position - at).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) return "{\"error\":\"no free driver seat\"}";
            // fpscontroller.GetIn pushes the car down at the player's current position and moves the camera from
            // there. From far away (the player 46 m off) that launched the car ~750 m up; the game only ever seats a
            // player standing next to the seat. So: step next to the seat first (callers wait a moment), then sit.
            float gap = (p.transform.position - best.transform.position).magnitude;
            if (gap > 3f)
            {
                var side = best.transform.position - best.transform.right * 1.5f + Vector3.up * 0.3f;
                p.Teleport(side);
                return "{\"stepped\":true,\"car\":" + Json.Str(best.Car.transform.root.name) + ",\"gap\":" + gap.ToString("F1") + "}";
            }
            p.GetIn(best);
            return "{\"car\":" + Json.Str(best.Car.transform.root.name) + ",\"dist\":" + Mathf.Sqrt(bestD).ToString("F1") + "}";
        }

        /// Test-driver recovery: shift the driven car `dist` metres along its nose and kill its velocities — out of
        /// poles, ditches and whatever else the driver wedged itself against.
        public static string Nudge(float dist)
        {
            var p = mainscript.s.player;
            var car = p != null ? p.Car : null;
            if (car == null || car.RB == null) return "{\"error\":\"not in a car\"}";
            var fwd = car.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return "{\"error\":\"no forward\"}";
            car.RB.position += fwd.normalized * dist;
            car.RB.velocity = Vector3.zero; car.RB.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            return "{\"nudged\":" + dist.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// Test setup: fill the driven car's tank with what its engine actually consumes. The game's random spawner
        /// can roll a diesel engine with petrol in the tank — the engine then can never start (canRun = 0 for a
        /// mismatched type), which is correct game behaviour; the test just fuels it like a player would.
        public static string FuelUp()
        {
            var p = mainscript.s.player;
            var car = p != null ? p.Car : null;
            if (car == null || car.Engine == null || car.Engine.Fuel == null) return "{\"error\":\"no car/engine\"}";
            var need = car.Engine.FuelConsumption != null ? car.Engine.FuelConsumption.fluids : null;
            if (need == null || need.Count == 0) return "{\"error\":\"engine consumes nothing\"}";
            var tank = car.Engine.Fuel.F;
            float want = tank.maxC > 1f ? tank.maxC : 40f;
            var type = need[0].type;
            tank.fluids.Clear();
            tank.fluids.Add(new mainscript.fluid(type, want));
            return "{\"fuel\":" + Json.Str(type.ToString()) + ",\"amount\":" + want.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// Test helper: hold the driven car still (wheel brake torque + kill velocity) — for seat-sync tests the
        /// car must not roll while everyone sits in it.
        public static string Brake()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            var car = p != null ? p.Car : null;
            if (car == null || car.RB == null) return "{\"error\":\"not in a car\"}";
            foreach (var w in car.RB.GetComponentsInChildren<WheelCollider>(true)) w.brakeTorque = 5000f;
            car.RB.velocity = Vector3.zero; car.RB.angularVelocity = Vector3.zero;
            return "{\"braked\":true}";
        }

        public static string GetOut()
        {
            Active = false;
            var p = mainscript.s.player;
            if (p.seat == null) return "{\"error\":\"not seated\"}";
            p.GetOut(p.seat.transform.position + p.seat.transform.up * 1.5f - p.seat.transform.right * 2f, true);
            return "{\"out\":true}";
        }

        /// Turn the car nearest to `near` to face `yaw` degrees (freshly spawned, at rest), so the route direction is fixed.
        public static string FaceCar(Vector3 near, float yaw)
        {
            carscript best = null;
            float bestD = float.MaxValue;
            foreach (var c in Object.FindObjectsOfType<carscript>())
            {
                if (c.RB == null) continue;
                float d = (c.RB.position - near).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return "{\"error\":\"no car\"}";
            best.RB.velocity = Vector3.zero;
            best.RB.angularVelocity = Vector3.zero;
            best.RB.transform.SetPositionAndRotation(best.RB.position + Vector3.up * 0.5f, Quaternion.Euler(0f, yaw, 0f));
            return "{\"car\":" + Json.Str(best.transform.root.name) + ",\"yaw\":" + yaw.ToString("F1") + "}";
        }

        /// Nearest main-road point to the player (Unity coordinates) and the road's heading there (degrees, bone direction).
        public static string RoadPoint()
        {
            var gpos = mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position);
            var roads = mainSetRoads(gpos);
            var road = roadGenScript.NearestPointOnRoadBone(gpos, roads, out var pt);
            if (road == null) return "{\"error\":\"no road near\"}";
            // heading HERE (the bones around the nearest point), not the whole road's start→end — on the beta's curvy roads
            // that pointed test cars well off the road's direction
            var dir = road.end - road.start;
            var bones = new List<Vector3d>();
            foreach (var el in road.roadElements) if (el.bonePos != null) bones.AddRange(el.bonePos);
            if (bones.Count >= 2)
            {
                int k = 0; double best = double.MaxValue;
                for (int i = 0; i < bones.Count; i++) { double d = (bones[i].x - pt.x) * (bones[i].x - pt.x) + (bones[i].z - pt.z) * (bones[i].z - pt.z); if (d < best) { best = d; k = i; } }
                int a = Mathf.Max(0, k - 2), b = Mathf.Min(bones.Count - 1, k + 2);
                if (a != b) dir = bones[b] - bones[a];
            }
            float yaw = Mathf.Atan2((float)dir.x, (float)dir.z) * Mathf.Rad2Deg;
            var u = mainscript.UnityPosFromGlobal(pt);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"x\":" + u.x.ToString("F2", ci) + ",\"y\":" + u.y.ToString("F2", ci) + ",\"z\":" + u.z.ToString("F2", ci) +
                   ",\"gx\":" + pt.x.ToString("F2", ci) + ",\"gz\":" + pt.z.ToString("F2", ci) +
                   ",\"yaw\":" + yaw.ToString("F1", ci) + ",\"main\":" + (road.roadType == roadGenScript.roadTypeEmu.mainRoad ? "true" : "false") + "}";
        }

        /// Colliders within `radius` of the player, grouped by root object (obstacle identification).
        public static string Near(float radius)
        {
            var p = mainscript.s.player;
            var at = p.transform.position;
            var groups = new Dictionary<Transform, (int n, float d, Vector3 pos, bool rb, bool trig)>();
            foreach (var c in Physics.OverlapSphere(at, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                var root = c.transform.root;
                if (root == p.transform.root) continue;
                // World containers (ObjGenParent, RoadParent...) hold every generated object: list the object itself
                if (root.name.EndsWith("Parent")) root = c.transform.parent != null && c.transform.parent != c.transform.root ? c.transform.parent : c.transform;
                float d = (c.ClosestPoint(at) - at).magnitude;
                groups.TryGetValue(root, out var g);
                groups[root] = (g.n + 1, g.n == 0 ? d : Mathf.Min(g.d, d), root.position, g.rb || c.attachedRigidbody != null, false);
            }
            var sb = new System.Text.StringBuilder("{\"near\":[");
            bool first = true;
            foreach (var kv in groups)
            {
                if (!first) sb.Append(',');
                first = false;
                var rp = kv.Value.pos - at;
                sb.Append("{\"root\":").Append(Json.Str(kv.Key.name)).Append(",\"cols\":").Append(kv.Value.n)
                  .Append(",\"dist\":").Append(kv.Value.d.ToString("F1")).Append(",\"rb\":").Append(kv.Value.rb ? "true" : "false")
                  .Append(",\"rel\":[").Append(rp.x.ToString("F1")).Append(',').Append(rp.y.ToString("F1")).Append(',').Append(rp.z.ToString("F1")).Append(']')
                  .Append(",\"fromRoadCentre\":").Append(FromRoadCentre(kv.Value.pos).ToString("F1")).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private static float FromRoadCentre(Vector3 upos)
        {
            var g = mainscript.GlobalFromUnityPos(upos);
            var roads = mainSetRoads(g);
            if (roads.Count == 0) return -1f;
            roadGenScript.NearestPointOnRoadBone(g, roads, out var pt);
            pt.y = g.y;
            return (float)(pt - g).magnitude;
        }

        /// Test (bridge `carsnear [m]`): cars within m of the player — distance, speed, engine, gear, handbrake, throttle,
        /// whether a player drives it. For what an EMPTY car does (bailout.py: does it creep on at idle?).
        public static string CarsNear(float range)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null) return "{\"error\":\"not in game\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture; var rows = new List<string>();
            foreach (var c in Object.FindObjectsOfType<carscript>())
            {
                float d = Vector3.Distance(c.transform.position, p.transform.position);
                if (d > range) continue;
                rows.Add("{\"name\":" + Json.Str(c.name) + ",\"d\":" + d.ToString("F1", ic) + ",\"kmh\":" + (c.RB != null ? c.RB.velocity.magnitude * 3.6f : 0f).ToString("F1", ic) +
                         ",\"engine\":" + (c.Engine != null && c.Engine.running ? "true" : "false") + ",\"gear\":" + c.gear +
                         ",\"handbrake\":" + c.handbrake.ToString("F2", ic) + ",\"throttle\":" + c.throttle.ToString("F2", ic) +
                         ",\"driven\":" + (c.isPlayerDriving ? "true" : "false") + "}");
            }
            return "{\"cars\":[" + string.Join(",", rows) + "]}";
        }

        public static string Stats()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            var car = p != null ? p.Car : null;
            float v = car != null && car.RB != null ? car.RB.velocity.magnitude : 0f;
            return "{\"active\":" + (Active ? "true" : "false") + ",\"driving\":" + (car != null && car.isPlayerDriving ? "true" : "false") +
                   ",\"speed\":" + v.ToString("F1") + ",\"target\":" + TargetSpeed.ToString("F1") +
                   ",\"lookYaw\":" + (p != null && p.seat != null ? p.FxRot.ToString("F1") : "null") +
                   ",\"engine\":" + (car != null && car.Engine != null && car.Engine.running ? "true" : "false") +
                   ",\"gear\":" + (car != null ? car.gear : 0) +
                   ",\"distance\":" + Distance.ToString("F0") + ",\"seconds\":" + Seconds.ToString("F1") +
                   ",\"maxCrossTrack\":" + MaxCrossTrack.ToString("F1") + ",\"offRoadFrames\":" + OffRoadFrames +
                   ",\"stuckSeconds\":" + StuckSeconds.ToString("F1") + ",\"roads\":" + (_roads != null ? _roads.Count : 0) +
                   ",\"avoidEvents\":" + AvoidEvents + ",\"recoveries\":" + Recoveries + ",\"lastObstacle\":" + Json.Str(LastObstacle) +
                   ",\"pos\":" + (car != null ? "[" + car.RB.position.x.ToString("F1") + "," + car.RB.position.y.ToString("F1") + "," + car.RB.position.z.ToString("F1") + "]" : "null") +
                   ",\"wheels\":" + Wheels(car) + "}";
        }

        private static string Wheels(carscript car)
        {
            if (car == null || car.allWH == null) return "[]";
            var sb = new System.Text.StringBuilder("[");
            foreach (var w in car.allWH)
            {
                if (sb.Length > 1) sb.Append(',');
                if (w == null) { sb.Append("null"); continue; }
                bool g = w.GetGroundHit(out var hit);
                sb.Append("{\"n\":").Append(Json.Str(w.name)).Append(",\"on\":").Append(w.enabled && w.gameObject.activeInHierarchy ? "true" : "false")
                  .Append(",\"motor\":").Append(w.motorTorque.ToString("F0")).Append(",\"brake\":").Append(w.brakeTorque.ToString("F0"))
                  .Append(",\"rpm\":").Append(w.rpm.ToString("F0")).Append(",\"ground\":").Append(g ? Json.Str(hit.collider.name) : "null")
                  .Append(",\"fslip\":").Append(g ? hit.forwardSlip.ToString("F2") : "0").Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
