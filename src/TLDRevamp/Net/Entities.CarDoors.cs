using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Test: a car's doors (hood, trunk, doors — door_rot / door_move driven by inputconnect from a usable). A player
    /// opens one through its usable (a click turns it, a drag slides it); the usable sync carries it to the others.
    public static partial class Entities
    {
        private sealed class DoorRef { public inputconnect Ic; public usablescript U; public Transform T; public string Name; }

        private static List<DoorRef> DoorsOf(Ent e)
        {
            var list = new List<DoorRef>();
            if (e == null || e.Root == null) return list;
            // car doors, hood, trunk: a slide usable that swings its TDoor between two poses (usablescript.Slide)
            // (most cars: a rotatable usable on the hinge, dragged by the player — usablescript.Rot turns its RT1)
            foreach (var u in e.Root.GetComponentsInChildren<usablescript>(true))
            {
                if (u.slideAble && u.TDoor != null) list.Add(new DoorRef { U = u, T = u.TDoor, Name = u.TDoor.name });
                else if (u.rotateAble && u.RT1 != null && (u.maxX - u.minX > 20f || u.maxY - u.minY > 20f))
                    list.Add(new DoorRef { U = u, T = u.RT1, Name = u.RT1.name });
            }
            foreach (var ic in e.Root.GetComponentsInChildren<inputconnect>(true))
            {
                if (ic.Sdoor_rot == null && ic.Sdoor_move == null) continue;
                usablescript u = ic.turnOverride;
                if (u == null && ic.IUsableV0To1 != null) foreach (var x in ic.IUsableV0To1) if (x != null) { u = x; break; }
                if (u == null && ic.IUsableV1To1 != null) foreach (var x in ic.IUsableV1To1) if (x != null) { u = x; break; }
                if (u != null) list.Add(new DoorRef { Ic = ic, U = u, T = ic.Sdoor_rot != null ? ic.Sdoor_rot.transform : ic.Sdoor_move.transform, Name = ic.name });
            }
            return list;
        }

        /// `mp cardoors <net>`: each door — its name, the usable's state (turn state, slide value) and the door's.
        public static string CarDoors(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no entity\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"doors\":[");
            int n = 0;
            foreach (var d in DoorsOf(e))
            {
                // what a player sees: the door's pose relative to the car (degrees off its closed pose here)
                var lr = d.T.localRotation; var lp = d.T.localPosition;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"name\":" + Json.Str(d.Name) + ",\"x\":" + d.U.xRot.ToString("F1", ci) + ",\"y\":" + d.U.yRot.ToString("F1", ci) + ",\"turn\":" + (d.U.turnable ? d.U.currentTurnState : -1) +
                          ",\"slide\":" + d.U.slideValue.ToString("F2", ci) +
                          ",\"rot\":[" + lr.eulerAngles.x.ToString("F1", ci) + "," + lr.eulerAngles.y.ToString("F1", ci) + "," + lr.eulerAngles.z.ToString("F1", ci) + "]" +
                          ",\"pos\":[" + lp.x.ToString("F3", ci) + "," + lp.y.ToString("F3", ci) + "," + lp.z.ToString("F3", ci) + "]}");
            }
            sb.Append("],\"usables\":[");
            n = 0;
            foreach (var u in e.Root.GetComponentsInChildren<usablescript>(true))
            {
                if (n++ > 0) sb.Append(',');
                sb.Append(Json.Str(u.name + (u.slideAble ? " slide" : "") + (u.TDoor != null ? " tdoor" : "") + (u.rotateAble ? " rot" : "") + (u.turnable ? " turn" : "") + (u.RT1 != null ? " rt1" : "")));
            }
            sb.Append("],\"parts\":" + e.Root.GetComponentsInChildren<partconditionscript>(true).Length);
            return sb.Append("}").ToString();
        }

        /// `mp opendoor <net> <i>`: the player's action on door i's usable — a click (Turn) if it turns, else a drag to
        /// the other end (slide 0↔1, then the game's sync call, as Slide does).
        public static string OpenDoor(uint net, int i)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no entity\"}";
            var ds = DoorsOf(e);
            if (i < 0 || i >= ds.Count) return "{\"error\":\"no door " + i + " of " + ds.Count + "\"}";
            var u = ds[i].U;
            if (u.rotateAble && u.RT1 != null && !u.slideAble)
            {
                // a drag to the other end of the hinge (Rot with vr: the raw angle, as a VR hand moves it; it syncs)
                float dx = u.maxX - u.minX > 20f ? (u.xRot - u.minX < u.maxX - u.xRot ? u.maxX - u.xRot : u.minX - u.xRot) : 0f;
                float dy = u.maxY - u.minY > 20f ? (u.yRot - u.minY < u.maxY - u.yRot ? u.maxY - u.yRot : u.minY - u.yRot) : 0f;
                u.Rot(dx, dy, true, true);
            }
            else if (u.turnable) u.Turn();
            else { u.Refresh(u.slideValue > 0.5f ? 0f : 1f, u.xRot, u.yRot, u.currentTurnState); u.SyncMulti(true); }   // Refresh poses TDoor (as Slide's end does)
            return CarDoors(net);
        }

        /// Test: cargo. `mp putincar <car> <item> <k>`: an item this machine owns onto the car's k-th rearmost seat
        /// (0.5 m above it), moving with the car. `mp relpos <car> <item…>`: each item where this machine shows it, in
        /// the car's frame (metres) — on a copy that is what the player sees of the cargo.
        public static string PutInCar(uint car, uint item, int k)
        {
            if (!ByNet.TryGetValue(car, out var c) || c.Root == null || !ByNet.TryGetValue(item, out var e) || e.Root == null) return "{\"error\":\"no entity\"}";
            if (e.Proxy) return "{\"error\":\"not the owner\"}";
            var ct = c.Root.transform;
            var seats = new List<seatscript>(c.Root.GetComponentsInChildren<seatscript>(true));
            seats.Sort((a, b) => ct.InverseTransformPoint(a.transform.position).z.CompareTo(ct.InverseTransformPoint(b.transform.position).z));
            if (k < 0 || k >= seats.Count) return "{\"error\":\"no seat " + k + " of " + seats.Count + "\"}";
            var at = seats[k].transform.position + ct.up * 0.5f;
            var t = e.Root.transform; t.position = at; t.rotation = ct.rotation;
            var rb = e.Root.GetComponent<Rigidbody>(); var crb = c.Root.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.position = at; rb.velocity = crb != null ? crb.velocity : Vector3.zero; rb.angularVelocity = Vector3.zero; rb.WakeUp(); }
            Physics.SyncTransforms();
            return RelPos(car, new[] { item });
        }

        public static string RelPos(uint car, uint[] items)
        {
            if (!ByNet.TryGetValue(car, out var c) || c.Root == null) return "{\"error\":\"no car\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ct = c.Root.transform;
            var sb = new System.Text.StringBuilder("{\"proxy\":" + (c.Proxy ? "true" : "false") + ",\"items\":{");
            int n = 0;
            foreach (var id in items)
            {
                if (!ByNet.TryGetValue(id, out var e) || e.Root == null) continue;
                var l = ct.InverseTransformPoint(e.Root.transform.position);
                if (n++ > 0) sb.Append(',');
                sb.Append("\"" + id + "\":[" + l.x.ToString("F3", ci) + "," + l.y.ToString("F3", ci) + "," + l.z.ToString("F3", ci) + "]");
            }
            // as drawn: the same, read in LateUpdate (after every Update — the bridge runs before Mp.Tick places copies)
            sb.Append("},\"late\":{");
            n = 0;
            if (_lateCar == car && _lateItems != null && items.Length == _lateItems.Length)
                for (int i = 0; i < _lateItems.Length; i++)
                {
                    if (!_lateHave[i]) continue;
                    if (n++ > 0) sb.Append(',');
                    var l = _lateRel[i];
                    sb.Append("\"" + _lateItems[i] + "\":[" + l.x.ToString("F3", ci) + "," + l.y.ToString("F3", ci) + "," + l.z.ToString("F3", ci) + "]");
                }
            _lateCar = car; if (_lateItems == null || _lateItems.Length != items.Length) { _lateItems = items; _lateRel = new Vector3[items.Length]; _lateHave = new bool[items.Length]; } else _lateItems = items;
            return sb.Append("}}").ToString();
        }

        /// Test: our player stands on top of shared car `car` (its colliders' top, at the middle), `dy` above it, moving
        /// with the car.
        public static string StandOn(uint car, float dy, bool rear = false)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (!ByNet.TryGetValue(car, out var c) || c.Root == null) return "{\"error\":\"no car\"}";
            if (rear)
            {
                // inside a body of the vehicle outside its hierarchy (the back of a bus): in the aisle by its first seat,
                // dy above the floor found by a ray down from seat height. (Before: the middle of all its colliders' bounds
                // at their lowest point + dy — the bottom of the wheels: the player stood on the road under the bus, and
                // every "standing in the back" run was invalid; the user, watching: "Laptop isn't even inside of the bus ever".)
                Transform body = null;
                if (c.Root.partslotscripts != null)
                    foreach (var sl in c.Root.partslotscripts) if (sl != null && sl.transform.root != c.Root.transform.root) { body = sl.transform.root; break; }
                if (body == null) return "{\"error\":\"no body outside the vehicle\"}";
                bool any2 = false; var b2 = new Bounds();
                foreach (var col in body.GetComponentsInChildren<Collider>()) { if (col.isTrigger || !col.enabled) continue; if (!any2) { b2 = col.bounds; any2 = true; } else b2.Encapsulate(col.bounds); }
                if (!any2) return "{\"error\":\"no colliders on " + body.name + "\"}";
                var seats = body.GetComponentsInChildren<seatscript>(true);
                if (seats.Length == 0) return "{\"error\":\"no seats on " + body.name + "\"}";
                var st = seats[seats.Length / 2].transform;
                // the aisle: the bus's centre line (its root's x = 0; the middle of the colliders' world box drifted half
                // a metre into a seat row depending on the bus's heading)
                // along it: the middle of the section's length (a seat's z hit the rear bench, which spans the width);
                // the floor is the nearest hit on the section that isn't a seat
                var rt = c.Root.transform; var ls = rt.InverseTransformPoint(st.position); var lm = rt.InverseTransformPoint(b2.center);
                var from = rt.TransformPoint(new Vector3(0f, ls.y, lm.z)) + Vector3.up * 0.5f;
                RaycastHit? floor = null;
                foreach (var h in Physics.RaycastAll(from, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
                    if (h.collider.transform.root == body && h.collider.GetComponentInParent<seatscript>() == null && !h.collider.name.StartsWith("seat")
                        && (floor == null || h.distance < floor.Value.distance)) floor = h;
                if (floor == null) return "{\"error\":\"no floor of " + body.name + " under the aisle\"}";
                // the player's pivot isn't at its feet: the capsule's bottom goes dy above the floor (pivot at the floor
                // put the body half into it — pushed up into the roof and out of the bus)
                float feet = 0f;
                var cb = pl.CBody;
                if (cb != null) feet = pl.transform.position.y - (cb.transform.TransformPoint(cb.center).y - cb.height * 0.5f * cb.transform.lossyScale.y);
                var at = floor.Value.point + Vector3.up * (feet + dy);
                pl.transform.position = at;
                var prb2 = pl.GetComponent<Rigidbody>(); if (prb2 != null) { prb2.position = at; if (!prb2.isKinematic) prb2.velocity = Vector3.zero; }
                return "{\"stood\":true,\"body\":" + Json.Str(body.name) + ",\"floor\":" + Json.Str(floor.Value.collider.name) + ",\"seat\":" + Json.Str(st.name) + ",\"feet\":" + feet.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                       ",\"local\":" + V3f(c.Root.transform.InverseTransformPoint(at)) + "}";
            }
            var cols = c.Root.GetComponentsInChildren<Collider>();
            bool any = false; var b = new Bounds();
            foreach (var col in cols) { if (col.isTrigger || !col.enabled) continue; if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds); }
            if (!any) return "{\"error\":\"no colliders\"}";
            var ct = c.Root.transform;
            var top = new Vector3(ct.position.x, b.max.y + dy, ct.position.z);
            pl.transform.position = top;
            var crb = c.Root.GetComponent<Rigidbody>(); var prb = pl.GetComponent<Rigidbody>();
            if (crb != null && prb != null && !prb.isKinematic) prb.velocity = crb.velocity;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"stood\":true,\"top\":" + b.max.y.ToString("F2", ci) + ",\"local\":" + V3f(ct.InverseTransformPoint(top)) + "}";
        }

        /// Test (`mp tow <net> <m/s> [accel]` / `mp tow off`): drives a vehicle this machine simulates with nobody at the
        /// wheel — its body's forward speed ramped to `speed` at `accel` m/s² in the physics step, its joints (the back of
        /// a bus) pulling the rest as in the game. The same drive with a player standing in it on the owner (the game's
        /// physics: the baseline) and on a copy (busrear --stand --tow).
        private static uint _towNet; private static float _towSpeed, _towAccel;
        public static string Tow(string[] a)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (a.Length < 2 || a[1] == "off") { _towNet = 0; return "{\"tow\":false}"; }
            uint net = uint.Parse(a[1]);
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.car == null || e.Proxy) return "{\"error\":\"not a vehicle simulated here\"}";
            _towNet = net; _towSpeed = a.Length > 2 ? float.Parse(a[2], ic) : 3f; _towAccel = a.Length > 3 ? float.Parse(a[3], ic) : 2f;
            return "{\"tow\":" + net + ",\"speed\":" + _towSpeed.ToString(ic) + ",\"accel\":" + _towAccel.ToString(ic) + "}";
        }
        private static void TowTick(float dt)
        {
            if (_towNet == 0) return;
            if (!ByNet.TryGetValue(_towNet, out var e) || e.Root == null || e.Root.car == null || e.Proxy) { _towNet = 0; return; }
            var rb = e.Root.car.RB; if (rb == null || rb.isKinematic) return;
            var f = rb.transform.forward; f.y = 0f; f.Normalize();
            float v = Vector3.Dot(rb.velocity, f);
            float dv = Mathf.Clamp(_towSpeed - v, -_towAccel * dt, _towAccel * dt);
            rb.AddForce(f * dv, ForceMode.VelocityChange);
        }

        /// Test: where the players are in car `car`'s frame — ours and every remote body shown here; now and as drawn
        /// (LateUpdate, the frame before).
        public static string RelPlayers(uint car)
        {
            if (!ByNet.TryGetValue(car, out var c) || c.Root == null) return "{\"error\":\"no car\"}";
            var ct = c.Root.transform;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var sb = new System.Text.StringBuilder("{\"me\":" + (pl != null ? V3f(ct.InverseTransformPoint(pl.transform.position)) : "null") + ",\"remotes\":{");
            int n = 0;
            foreach (var kv in RemotePlayers.ShownBodies()) { if (n++ > 0) sb.Append(','); sb.Append("\"" + kv.Key + "\":" + V3f(ct.InverseTransformPoint(kv.Value.position))); }
            sb.Append("},\"late\":{");
            n = 0;
            if (_latePlCar == car) foreach (var kv in _latePl) { if (n++ > 0) sb.Append(','); sb.Append("\"" + kv.Key + "\":" + V3f(kv.Value)); }
            _latePlCar = car;
            return sb.Append("}}").ToString();
        }
        private static string V3f(Vector3 v)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "[" + v.x.ToString("F3", ci) + "," + v.y.ToString("F3", ci) + "," + v.z.ToString("F3", ci) + "]";
        }
        private static uint _latePlCar; private static readonly Dictionary<int, Vector3> _latePl = new Dictionary<int, Vector3>();

        private static uint _lateCar; private static uint[] _lateItems; private static Vector3[] _lateRel; private static bool[] _lateHave;
        /// Plugin LateUpdate: the car-frame positions `mp relpos` asked for, as they will be drawn this frame.
        public static void LateSample()
        {
            if (_latePlCar != 0 && ByNet.TryGetValue(_latePlCar, out var pc) && pc.Root != null)
            {
                var pt = pc.Root.transform; _latePl.Clear();
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl != null) _latePl[-1] = pt.InverseTransformPoint(pl.transform.position);   // -1: ours
                foreach (var kv in RemotePlayers.ShownBodies()) _latePl[kv.Key] = pt.InverseTransformPoint(kv.Value.position);
            }
            if (_lateCar == 0 || _lateItems == null) return;
            if (!ByNet.TryGetValue(_lateCar, out var c) || c.Root == null) return;
            var ct = c.Root.transform;
            for (int i = 0; i < _lateItems.Length; i++)
            {
                _lateHave[i] = ByNet.TryGetValue(_lateItems[i], out var e) && e.Root != null;
                if (_lateHave[i]) _lateRel[i] = ct.InverseTransformPoint(e.Root.transform.position);
            }
        }

        /// Test: food. `mp bite`: one bite of what the player holds, the game's own way (fpscontroller's eat on a click:
        /// survival.Eat(edible.Eat(…))). `mp food <net>`: its bites here (index, hp, parts shown, gone).
        public static string Bite()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.pickedUp == null || pl.pickedUp.edible == null) return "{\"error\":\"holding no food\"}";
            var ed = pl.pickedUp.edible;
            pl.survival.Eat(ed.Eat(out float sh, out float hp, out float wa, true), sh, hp, wa);
            return "{\"index\":" + ed.index + ",\"hp\":" + ed.hp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        public static string Food(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"gone\":true}";
            var ed = e.Root.GetComponentInChildren<ediblescript>(true);
            if (ed == null) return "{\"error\":\"not food\"}";
            int shown = 0, all = 0;
            if (ed.parts != null) foreach (var p in ed.parts) { all++; if (p != null && p.activeSelf) shown++; }
            return "{\"gone\":false,\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"name\":" + Json.Str(e.Root.name) + ",\"index\":" + ed.index +
                   ",\"hp\":" + ed.hp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"parts\":[" + shown + "," + all + "],\"continous\":" +
                   (ed.continous ? "true" : "false") + ",\"more\":" + (ed.moreParts ? "true" : "false") + ",\"destroyOnEnd\":" + (ed.destroyOnEnd ? "true" : "false") + "}";
        }

        /// Test: a rabbit (aiscript). `mp rabbit <net>`: fleeing?, its stimulus balance, global position and facing.
        public static string Rabbit(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no entity\"}";
            var a = e.Root.GetComponentInChildren<aiscript>(true);
            if (a == null) return "{\"error\":\"no aiscript\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var g = mainscript.GlobalFromUnityPos(a.transform.position); var f = a.transform.forward;
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"fleeing\":" + (a.fleeing ? "true" : "false") + ",\"ff\":" + a.flightFight.ToString("F2", ci) +
                   ",\"stimuli\":" + (a.senses != null ? a.senses.stimuls.Count : -1) +
                   ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "],\"fwd\":[" + f.x.ToString("F2", ci) + "," + f.z.ToString("F2", ci) + "]}";
        }

        /// Test: how a copy moves on this screen, frame by frame (`mp copymotion <net> [reset]`): shown speed min / p10 /
        /// p50 / p90 / max (m/s) since the reset, and frames it all but stopped (< 30 % of the median).
        public static string CopyMotion(uint net, bool reset)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no entity\"}";
            if (reset) { e.ShownN = 0; return "{\"reset\":true}"; }
            int n = Math.Min(e.ShownN, e.ShownSpeed.Length);
            if (n < 5) return "{\"n\":" + n + "}";
            var a = new float[n]; Array.Copy(e.ShownSpeed, a, n); Array.Sort(a);
            float med = a[n / 2]; int held = 0; foreach (var v in a) if (v < 0.3f * med) held++;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            System.Func<float, string> f = v => v.ToString("F2", ci);
            return "{\"n\":" + n + ",\"min\":" + f(a[0]) + ",\"p10\":" + f(a[n / 10]) + ",\"p50\":" + f(med) + ",\"p90\":" + f(a[n * 9 / 10]) + ",\"max\":" + f(a[n - 1]) + ",\"held\":" + held + "}";
        }
    }
}
