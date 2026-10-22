using System.Collections.Generic;
using UnityEngine;

namespace CarTrailer
{
    /// Test hooks for TLD Revamp's debug bridge (`call CarTrailer.Probe.Wheels`): what the car trailers in the world
    /// carry, as text (tools/cartrailersave.py).
    public static class Probe
    {
        /// Each car trailer: its wheel slots and how many parts sit in each, and its save id.
        public static string Wheels()
        {
            var rows = new List<string>();
            foreach (var save in Object.FindObjectsOfType<tosaveitemscript>())
            {
                if (save.id != Plugin.ItemId) continue;
                int slots = 0, parts = 0, most = 0;
                foreach (var ps in save.GetComponentsInChildren<partslotscript>(true))
                {
                    if (!ps.name.Contains("Wheel")) continue;
                    slots++; int n = 0;
                    foreach (var p in ps.parts) if (p != null) n++;
                    parts += n; most = Mathf.Max(most, n);
                }
                int tyres = 0;
                foreach (var a in save.GetComponentsInChildren<attachablescript>(true)) if (a.GetComponent<tosaveitemscript>() != null && a.gameObject != save.gameObject) tyres++;
                rows.Add($"{save.name} at {save.transform.position}: wheel slots {slots}, parts in them {parts}, most in one {most}, attached items {tyres}");
            }
            return rows.Count == 0 ? "none" : string.Join(" | ", rows);
        }

        /// Each car trailer's items on it, by the slot (or object) each hangs on (a spawned trailer's built-in parts
        /// and loaded ones have different names).
        public static string Parts()
        {
            var rows = new List<string>();
            foreach (var save in Object.FindObjectsOfType<tosaveitemscript>())
            {
                if (save.id != Plugin.ItemId) continue;
                var names = new List<string>();
                foreach (var a in save.GetComponentsInChildren<attachablescript>(true))
                {
                    if (a.gameObject == save.gameObject || a.GetComponent<tosaveitemscript>() == null) continue;
                    names.Add(a.slot != null ? a.slot.name : a.transform.parent != null ? a.transform.parent.name : "-");
                }
                names.Sort();
                rows.Add(save.name + ": " + string.Join(", ", names));
            }
            return rows.Count == 0 ? "none" : string.Join(" | ", rows);
        }

        /// The nearest trailer's physics: mass, centre of mass, inertia, the hitch joint, each wheel's suspension and grip.
        public static string Physics()
        {
            var pl = mainscript.s.player.transform.position;
            utanfutoscript best = null; float bd = float.MaxValue;
            foreach (var t in Object.FindObjectsOfType<utanfutoscript>())
            {
                float d = (t.transform.position - pl).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            if (best == null) return "no trailer";
            var rb = best.GetComponent<Rigidbody>(); var c = System.Globalization.CultureInfo.InvariantCulture;
            var s = new System.Text.StringBuilder();
            s.AppendFormat(c, "{0} id {1} mass {2:F0} com {3} inertia {4} rot {5} drag {6:F2} angdrag {7:F2} solver {8}/{9} interp {10} cols {11}",
                best.name, best.GetComponent<tosaveitemscript>()?.id ?? -1, rb.mass, rb.centerOfMass.ToString("F2"), rb.inertiaTensor.ToString("F0"), rb.inertiaTensorRotation.eulerAngles.ToString("F0"),
                rb.drag, rb.angularDrag, rb.solverIterations, rb.solverVelocityIterations, rb.interpolation, best.GetComponentsInChildren<Collider>().Length);
            var grip = best.GetComponent<attachgripscript>();
            if (grip != null && grip.J != null)
            {
                var j = grip.J; s.AppendFormat(c, "; joint {0} anchor {1} connected {2} massScale {3:F2}/{4:F2} grip at {5}", j.GetType().Name, j.anchor.ToString("F2"),
                    j.connectedBody != null ? j.connectedBody.name + " m " + j.connectedBody.mass.ToString("F1", c) : "none", j.massScale, j.connectedMassScale, best.transform.InverseTransformPoint(grip.GripPos.position).ToString("F2"));
                if (j is ConfigurableJoint cj) s.AppendFormat(c, " motion {0}{1}{2} ang {3}{4}{5} proj {6}", cj.xMotion, cj.yMotion, cj.zMotion, cj.angularXMotion, cj.angularYMotion, cj.angularZMotion, cj.projectionMode);
                if (j is CharacterJoint ch) s.AppendFormat(c, " swing1 {0:F0} swing2 {1:F0} twist {2:F0}..{3:F0}", ch.swing1Limit.limit, ch.swing2Limit.limit, ch.lowTwistLimit.limit, ch.highTwistLimit.limit);
                if (j is HingeJoint hj) s.AppendFormat(c, " axis {0} limits {1}", hj.axis, hj.useLimits);
                if (grip.Grip != null) { var g = grip.Grip.GetComponent<Rigidbody>(); s.AppendFormat(c, " ball rb {0} parent {1}", g != null ? g.mass.ToString("F1", c) + (g.isKinematic ? " kinematic" : "") : "none", grip.Grip.transform.parent != null ? grip.Grip.transform.parent.name : "-"); }
            }
            foreach (var w in best.wheels)
            {
                if (w == null) continue;
                var sp = w.suspensionSpring; var fw = w.forwardFriction; var sw = w.sidewaysFriction;
                w.GetGroundHit(out var hit);
                s.AppendFormat(c, "; wheel {0} at {1} r {2:F2} m {3:F0} susp {4:F2} spring {5:F0} damper {6:F0} target {7:F2} fwd {8:F2}/{9:F2} side ex {10:F2} {11:F2} as {12:F2} {13:F2} stiff {14:F2} load {15:F0} grounded {16} fapd {17:F2}",
                    w.name, best.transform.InverseTransformPoint(w.transform.position).ToString("F2"), w.radius, w.mass, w.suspensionDistance, sp.spring, sp.damper, sp.targetPosition,
                    fw.stiffness, fw.extremumValue, sw.extremumSlip, sw.extremumValue, sw.asymptoteSlip, sw.asymptoteValue, sw.stiffness, hit.force, w.isGrounded, w.forceAppPointDistance);
            }
            return s.ToString();
        }

        static utanfutoscript Nearest()
        {
            var pl = mainscript.s.player.transform.position;
            utanfutoscript best = null; float bd = float.MaxValue;
            foreach (var t in Object.FindObjectsOfType<utanfutoscript>())
            {
                float d = (t.transform.position - pl).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            return best;
        }

        /// Tries values on the nearest trailer (tools/cartrailersway.py): "mass=500,spring=40000,damper=3500,ball=20,comz=0.5".
        public static string Tune(string spec)
        {
            var t = Nearest(); if (t == null) return "no trailer";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var kv in spec.Split(','))
            {
                var p = kv.Split('='); if (p.Length != 2) continue;
                float v = float.Parse(p[1], c);
                switch (p[0])
                {
                    case "mass": var ms = t.GetComponent<massScript>(); if (ms != null) ms.SetMass(v + ms.OwnMass() - ms.Mass()); break;   // the total
                    case "spring": foreach (var w in t.wheels) { var s = w.suspensionSpring; s.spring = v; w.suspensionSpring = s; } break;
                    case "damper": foreach (var w in t.wheels) { var s = w.suspensionSpring; s.damper = v; w.suspensionSpring = s; } break;
                    case "side": foreach (var w in t.wheels) { var f = w.sidewaysFriction; f.stiffness = v; w.sidewaysFriction = f; } break;
                    case "ball":
                        var grip = t.GetComponent<attachgripscript>();
                        if (grip != null && grip.J != null) grip.J.connectedMassScale = v;
                        var att = grip != null && grip.Grip != null ? grip.Grip.GetComponent<attachablescript>() : null;
                        if (att != null && att.J != null) att.J.massScale = v;
                        break;
                    case "comz":
                        if (t.CenterOfMassAttached != null) { var lp = t.transform.InverseTransformPoint(t.CenterOfMassAttached.position); t.CenterOfMassAttached.position = t.transform.TransformPoint(new Vector3(lp.x, lp.y, v)); }
                        t.RefreshCenterOffMass(t.attached, t.rear);
                        break;
                    case "angdrag": t.GetComponent<Rigidbody>().angularDrag = v; break;
                    case "contacts": if (t.GetComponent<ContactLog>() == null) t.gameObject.AddComponent<ContactLog>(); ContactLog.Log.Clear(); break;
                    case "target": foreach (var w in t.wheels) { var s = w.suspensionSpring; s.targetPosition = v; w.suspensionSpring = s; } break;
                    case "travel": foreach (var w in t.wheels) w.suspensionDistance = v; break;
                    case "slick":
                        var pm = new PhysicMaterial("CarTrailerUnderbody") { dynamicFriction = v, staticFriction = v, frictionCombine = PhysicMaterialCombine.Minimum };
                        foreach (var col in t.GetComponentsInChildren<Collider>()) if (!col.isTrigger && !(col is WheelCollider) && t.transform.InverseTransformPoint(col.bounds.center).y < 0.75f) col.sharedMaterial = pm;
                        break;
                    case "fapd": foreach (var w in t.wheels) w.forceAppPointDistance = v; break;
                    case "sway": var sd = t.GetComponent<SwayDamper>() ?? t.gameObject.AddComponent<SwayDamper>(); sd.Strength = v; break;
                }
            }
            return Physics();
        }

        /// What the trailers' bodies touched since the last call (Tune "contacts=1" first).
        public static string Contacts() { var s = string.Join(" | ", ContactLog.Log); ContactLog.Log.Clear(); return s.Length == 0 ? "none" : s; }

        /// A sideways push on the nearest trailer: its turn rate changed by this many degrees a second.
        public static string Kick(string degPerS)
        {
            var t = Nearest(); if (t == null) return "no trailer";
            var rb = t.GetComponent<Rigidbody>();
            rb.angularVelocity += Vector3.up * float.Parse(degPerS, System.Globalization.CultureInfo.InvariantCulture) * Mathf.Deg2Rad;
            return "kicked " + t.name;
        }

        /// The trailer nearest the player's car, how it rides: its angle to the car (yaw, + = to the right), roll and
        /// pitch, its up/down speed, turn and roll rates, hitched or not, upright or not (tools/cartrailerride.py).
        public static string Ride()
        {
            var car = mainscript.s.player.Car;
            if (car == null) return "not in a car";
            utanfutoscript best = null; float bd = float.MaxValue;
            foreach (var t in Object.FindObjectsOfType<utanfutoscript>())
            {
                float d = (t.transform.position - car.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            if (best == null) return "no trailer";
            var tr = best.transform; var rb = best.GetComponent<Rigidbody>();
            float yaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(car.transform.forward, Vector3.up), Vector3.ProjectOnPlane(tr.forward, Vector3.up), Vector3.up);
            var e = tr.eulerAngles; float roll = Mathf.DeltaAngle(0f, e.z), pitch = Mathf.DeltaAngle(0f, e.x);
            var local = rb != null ? tr.InverseTransformDirection(rb.angularVelocity) : Vector3.zero;
            var grip = best.GetComponent<attachgripscript>();
            bool hitched = grip != null && grip.J != null && grip.J.connectedBody != null;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "yaw {0:F2} roll {1:F2} pitch {2:F2} vy {3:F3} yawrate {4:F3} rollrate {5:F3} speed {6:F2} hitched {7} upright {8:F3} id {9} dist {10:F1} caryawrate {11:F3} carroll {12:F2} grounded {13}",
                yaw, roll, pitch, rb != null ? rb.velocity.y - car.RB.velocity.y : 0f, local.y, local.z, rb != null ? rb.velocity.magnitude : 0f, hitched,
                Vector3.Dot(tr.up, Vector3.up), best.GetComponent<tosaveitemscript>()?.id ?? -1, Mathf.Sqrt(bd),
                Vector3.Dot(car.RB.angularVelocity, Vector3.up), Mathf.DeltaAngle(0f, car.transform.eulerAngles.z), best.wheels.FindAll(w => w != null && w.isGrounded).Count);
        }

        /// Loading as before the empty trailer (the full one loads, extra wheels stay), for making an old save.
        public static string Legacy(string on) { Plugin.LegacyForTest = on == "1" || on == "true"; return "legacy " + Plugin.LegacyForTest; }

        /// The save ids of the game's trailer prefabs (a full variant saves its base id).
        public static string Ids()
        {
            var db = itemdatabase.s;
            string Id(int i) { var s = db.items[i] != null ? db.items[i].GetComponent<tosaveitemscript>() : null; return s != null ? s.id.ToString() : "none"; }
            return $"utanfuto {Id((int)itemdatabase.i.utanfuto)}, utanfutofull {Id((int)itemdatabase.i.utanfutofull)}, cartrailer {Id(Plugin.ItemId)}, full {Id(Plugin.FullItemId)} ({(db.items[Plugin.FullItemId] != null ? db.items[Plugin.FullItemId].name : "none")}); holder children {Plugin.HolderChildren}";
        }
    }
}
