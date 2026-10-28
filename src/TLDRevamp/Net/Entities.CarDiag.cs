using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// One car as the physics engine has it right now, for a bug report: what holds a car that "does not move although the
        /// engine runs" shows here (a frozen or jointed body, a wheel lock, a wheel that cannot turn, a collider overlapping the
        /// body). The car the reporter sits in, else the nearest one within 15 m; null when there is none. Main thread.
        internal static string CarDiagnostics()
        {
            var player = mainscript.s != null ? mainscript.s.player : null;
            if (player == null) return null;
            var car = player.Car;
            if (car == null)
            {
                float best = 15f * 15f;
                foreach (var c in Object.FindObjectsOfType<carscript>())
                {
                    if (c == null) continue;
                    float d = (c.transform.position - player.transform.position).sqrMagnitude;
                    if (d < best) { best = d; car = c; }
                }
            }
            return CarDiagnostics(car);
        }

        /// A stuck car held at full throttle, noticed while recording: the snapshot of its first stuck moment, so the report has it
        /// whenever the key is pressed. Replaced at most every 10 s; the newest wins.
        internal static string StuckCarJson;
        private static float _stuckSince = -1f, _stuckTaken = -100f;
        private static void StuckWatch(carscript car)
        {
            if (car.ithrottle > 0.5f && car.speed < 0.5f) { if (_stuckSince < 0f) _stuckSince = Time.realtimeSinceStartup; }
            else { _stuckSince = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (now - _stuckSince < 1.5f || now - _stuckTaken < 10f) return;
            _stuckTaken = now;
            try { StuckCarJson = CarDiagnostics(car); }
            catch (System.Exception e) { SessionLog.Fail("stuck car snapshot", e); }
        }

        internal static string CarDiagnostics(carscript car)
        {
            if (car == null) return null;
            var ic = CultureInfo.InvariantCulture;
            string F(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("0.###", ic);
            string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
            string B(bool v) => v ? "true" : "false";
            var root = car.transform.root;
            var sb = new StringBuilder(4096);
            sb.Append("{\"car\":").Append(Json.Str(root.name)).Append(",\"net\":").Append(TelemetryNet(car)).Append(",\"seated\":").Append(B(car.isPlayerDriving));

            sb.Append(",\"script\":{\"gear\":").Append(car.gear).Append(",\"selectedRatio\":").Append(F(car.selectedgr)).Append(",\"differential\":").Append(F(car.differentialRatio))
              .Append(",\"throttle\":").Append(F(car.throttle)).Append(",\"brake\":").Append(F(car.brake)).Append(",\"handbrake\":").Append(F(car.handbrake)).Append(",\"clutch\":").Append(F(car.clutch))
              .Append(",\"ignition\":").Append(B(car.ignition)).Append(",\"driving\":").Append(B(car.driving)).Append(",\"driving2\":").Append(B(car.driving2)).Append(",\"multiControlling\":").Append(B(car.multiControlling))
              .Append(",\"wheelsLocked\":").Append(B(car.whlocked)).Append(",\"lockBoxesActive\":").Append(CountActive(car.whColliders)).Append(",\"lockBoxes\":").Append(car.whColliders != null ? car.whColliders.Count : 0)
              .Append(",\"speed\":").Append(F(car.speed)).Append(",\"avgWheelRpm\":").Append(F(car.avgRpm)).Append(",\"drivenWheels\":").Append(car.WHDriven != null ? car.WHDriven.Count : 0).Append('}');

            var en = car.Engine;
            sb.Append(",\"engine\":").Append(en == null ? "null" : "{\"running\":" + B(en.running) + ",\"rpm\":" + F(en.rpm) + ",\"output\":" + F(en.actualOutput) + ",\"brakeOutput\":" + F(en.brakeOutput) +
                                                   ",\"inputWheelRpm\":" + F(en.iwrpm) + ",\"gas\":" + F(en.gas) + ",\"clutch\":" + F(en.clutch) + ",\"wheelRpm\":" + F(en.wrpm) + ",\"wantedRpm\":" + F(en.drpm) +
                                                   ",\"rpmDifference\":" + F(en.rpmDifference) + ",\"maxNm\":" + F(en.maxNm) + ",\"modifier\":" + F(en.modifier) + ",\"freeRun\":" + B(en.freeRun) + "}");

            var rb = car.RB;
            sb.Append(",\"body\":");
            if (rb == null) sb.Append("null");
            else sb.Append("{\"kinematic\":").Append(B(rb.isKinematic)).Append(",\"asleep\":").Append(B(rb.IsSleeping())).Append(",\"mass\":").Append(F(rb.mass)).Append(",\"drag\":").Append(F(rb.drag))
                   .Append(",\"angularDrag\":").Append(F(rb.angularDrag)).Append(",\"constraints\":").Append(Json.Str(rb.constraints.ToString())).Append(",\"gravity\":").Append(B(rb.useGravity))
                   .Append(",\"collisions\":").Append(B(rb.detectCollisions)).Append(",\"velocity\":").Append(V(rb.velocity)).Append(",\"angularVelocity\":").Append(V(rb.angularVelocity)).Append('}');
            sb.Append(",\"parent\":").Append(Json.Str(car.transform.parent != null ? car.transform.parent.name : ""));

            var wheels = new List<string>();
            foreach (var w in root.GetComponentsInChildren<WheelCollider>(true))
            {
                w.GetGroundHit(out var hit);
                var wb = w.attachedRigidbody;
                wheels.Add("{\"name\":" + Json.Str(w.name) + ",\"enabled\":" + B(w.enabled) + ",\"active\":" + B(w.gameObject.activeInHierarchy) + ",\"body\":" + Json.Str(wb == null ? "none" : wb == rb ? "car" : wb.name + (wb.isKinematic ? " (kinematic)" : "")) +
                           ",\"mass\":" + F(w.mass) + ",\"sprung\":" + F(w.sprungMass) + ",\"radius\":" + F(w.radius) + ",\"motor\":" + F(w.motorTorque) + ",\"brake\":" + F(w.brakeTorque) + ",\"rpm\":" + F(w.rpm) +
                           ",\"grounded\":" + B(w.isGrounded) + ",\"forwardSlip\":" + F(hit.forwardSlip) + ",\"sidewaysSlip\":" + F(hit.sidewaysSlip) + ",\"load\":" + F(hit.force) + ",\"damping\":" + F(w.wheelDampingRate) + "}");
            }
            sb.Append(",\"wheels\":[").Append(string.Join(",", wheels)).Append(']');

            // joints on the car's own bodies, and joints of other bodies that hold on to them (a trailer, a bus' other half)
            var joints = new List<string>();
            foreach (var j in Object.FindObjectsOfType<Joint>())
            {
                if (j == null) continue;
                bool mine = j.transform.root == root, theirs = j.connectedBody != null && j.connectedBody.transform.root == root;
                if (!mine && !theirs) continue;
                var cb = j.connectedBody;
                joints.Add("{\"type\":" + Json.Str(j.GetType().Name) + ",\"on\":" + Json.Str(j.name) + ",\"onCar\":" + B(mine) + ",\"to\":" + Json.Str(cb == null ? "the world" : cb.name) +
                           ",\"toKinematic\":" + B(cb != null && cb.isKinematic) + ",\"breakForce\":" + F(j.breakForce) + "}");
                if (joints.Count >= 12) break;
            }
            sb.Append(",\"joints\":[").Append(string.Join(",", joints)).Append(']');

            // solid colliders of other objects inside the body's box (the terrain and the car's own colliders left out)
            var inside = new List<string>();
            var bounds = new Bounds(); bool any = false;
            foreach (var c in root.GetComponentsInChildren<Collider>())
            {
                if (c == null || c.isTrigger || !c.enabled || c is WheelCollider) continue;
                if (any) bounds.Encapsulate(c.bounds); else { bounds = c.bounds; any = true; }
            }
            if (any)
                foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || c.GetType().Name == "TerrainCollider" || c.transform.root == root) continue;
                    var crb = c.attachedRigidbody;
                    inside.Add("{\"name\":" + Json.Str(c.name) + ",\"root\":" + Json.Str(c.transform.root.name) + ",\"layer\":" + Json.Str(LayerMask.LayerToName(c.gameObject.layer)) +
                               ",\"body\":" + Json.Str(crb == null ? "static" : crb.isKinematic ? "kinematic" : "dynamic") + "}");
                    if (inside.Count >= 16) break;
                }
            sb.Append(",\"overlapping\":[").Append(string.Join(",", inside)).Append(']');

            sb.Append(",\"mod\":");
            if (ByNet.TryGetValue(TelemetryNet(car), out var e))
                sb.Append("{\"owner\":").Append(e.OwnerId).Append(",\"mine\":").Append(B(e.OwnerId == MyId)).Append(",\"proxy\":").Append(B(e.Proxy)).Append(",\"physicalCopy\":").Append(B(e.Physical))
                  .Append(",\"movedByBody\":").Append(B(e.MovedByBody)).Append(",\"madeKinematic\":").Append(e.MadeKinematic.Count).Append('}');
            else sb.Append("null");
            return sb.Append('}').ToString();
        }
    }
}
