using UnityEngine;

namespace TLDRevamp.Net
{
    /// Players standing on a shared object — a car's roof or bed, the back of the bus — ride in its frame (protocol 23):
    /// the stander sends the object and where on it they stand, every machine puts the body there in its own view of it
    /// (RemotePlayers.Tick). Their world position came from their own, late view of the car and was late again on the
    /// others: 3 m behind the roof at 30 km/h (standride.py, 2026-10-08). Seated players have their seat instead.
    public static partial class Entities
    {
        public static bool StandFrames = true;   // A/B: false = world positions only (before v0.65.46)
        public static long FootingSent;

        /// Our player: the shared object under its feet (the game's ground sensor, fpscontroller.grounded), if any.
        public static void LocalFooting(out Protocol.Footing f)
        {
            f = default;
            if (!StandFrames) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.Bsitting) { FootWhy = "seated"; return; }
            // what is under the feet: the game's ground sensor, else a short ray down (the sensor is a trigger that keeps
            // whichever collider it touched last)
            Transform under = pl.grounded != null && pl.grounded.sensed && pl.grounded.T != null ? pl.grounded.T : null;
            string via = "sensor";
            if (under == null || ProxyOf(under.root) == null && OwnedRoot(under.root) == null)
            {
                var from = pl.transform.position + Vector3.up * 0.5f;
                var hits = Physics.RaycastAll(from, Vector3.down, 2.0f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; Transform bt = null;
                foreach (var h in hits)
                {
                    var hr = h.collider.transform.root;
                    if (hr == pl.transform.root) continue;   // our own body
                    if (pl.pickedUp != null && hr == pl.pickedUp.transform.root || pl.inHandP != null && hr == pl.inHandP.transform.root) continue;   // what we hold
                    if (h.distance < best) { best = h.distance; bt = h.collider.transform; }
                }
                if (bt != null) { under = bt; via = "ray"; }
            }
            if (under == null) { FootWhy = "nothing under"; return; }
            var root = under.root;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.PartIndex >= 0 || e.Root.transform != root) continue;
                // only what moves: a vehicle, or a loose object in motion. Stepping over a tyre on the ground flipped the
                // footing on and off, each time a fresh interpolator on the other machines
                if (e.Root.car == null)
                {
                    var rb = e.Root.GetComponent<Rigidbody>();
                    var v = rb == null ? Vector3.zero : e.Proxy || rb.isKinematic ? e.LastVel : rb.velocity;
                    if (v.sqrMagnitude < 0.5f * 0.5f) { FootWhy = "on " + e.NetId + ": not moving"; return; }
                }
                f.Net = e.NetId;
                f.Local = root.InverseTransformPoint(pl.transform.position);
                f.Yaw = Mp.LocalYaw() - root.eulerAngles.y;
                FootingSent++; FootWhy = "on " + e.NetId + " (" + via + ")";
                return;
            }
            // a body of a vehicle outside its hierarchy (the back of a bus: its own body on a joint, as for its seats,
            // LocalSeat): the position in the vehicle's frame
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.PartIndex >= 0 || e.Root.car == null || e.Root.partslotscripts == null) continue;
                bool mine = false;
                foreach (var sl in e.Root.partslotscripts) if (sl != null && sl.transform.root == root) { mine = true; break; }
                if (!mine) continue;
                var vt = e.Root.transform;
                f.Net = e.NetId;
                f.Local = vt.InverseTransformPoint(pl.transform.position);
                f.Yaw = Mp.LocalYaw() - vt.eulerAngles.y;
                FootingSent++; FootWhy = "on " + e.NetId + " (" + via + ", its body " + root.name + ")";
                return;
            }
            FootWhy = "under " + under.name + " / root " + root.name + " (" + via + "): not shared";
        }
        public static string FootWhy = "";

        private static Ent OwnedRoot(Transform root)
        {
            foreach (var e in ByNet.Values) if (e.Root != null && e.PartIndex < 0 && e.Root.transform == root) return e;
            return null;
        }

        /// Test (`mp footprobe`): what our player stands on — the game's ground sensor, the ray down, the feet's IK targets
        /// against the surface (a stander's legs went through the roof on the other screen).
        public static string FootProbe()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            System.Func<float, string> F = x => x.ToString("F3", ci);
            var gs = pl.grounded;
            string sensor = gs == null ? "null" : "{\"sensed\":" + (gs.sensed ? "true" : "false") + ",\"T\":" + Json.Str(gs.T != null ? gs.T.name : "") +
                            ",\"layer\":" + gs.gameObject.layer + ",\"trigger\":" + (gs.selfCol != null && gs.selfCol.isTrigger ? "true" : "false") +
                            ",\"rb\":" + Json.Str(gs.GetComponentInParent<Rigidbody>() != null ? (gs.GetComponentInParent<Rigidbody>().isKinematic ? "kinematic" : "dynamic") : "none") + "}";
            var from = pl.transform.position + Vector3.up * 0.5f;
            string ray = "null"; float surf = float.NaN;
            if (Physics.Raycast(from, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                surf = hit.point.y;
                var rrb = hit.collider.attachedRigidbody;
                ray = "{\"hit\":" + Json.Str(hit.collider.name) + ",\"root\":" + Json.Str(hit.collider.transform.root.name) + ",\"layer\":" + hit.collider.gameObject.layer +
                      ",\"rb\":" + Json.Str(rrb == null ? "none" : rrb.isKinematic ? "kinematic" : "dynamic") + ",\"belowPlayer\":" + F(pl.transform.position.y - hit.point.y) + "}";
            }
            var a = pl.anim;
            string feet = a == null || a.LLTarget == null ? "null" : "{\"left\":" + F(a.LLTarget.position.y - (float.IsNaN(surf) ? 0 : surf)) + ",\"right\":" + F(a.RLTarget.position.y - (float.IsNaN(surf) ? 0 : surf)) + "}";
            return "{\"sensor\":" + sensor + ",\"ray\":" + ray + ",\"feetAboveSurface\":" + feet + ",\"why\":" + Json.Str(FootWhy) + "}";
        }

        /// Test (`mp outsidebodies <net>`): a vehicle's bodies outside its hierarchy on this machine (Bus01's BusBack) —
        /// how they are moved: body flags, the joint, the parent, every script on them.
        public static string OutsideBodies(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no such vehicle here\"}";
            var root = e.Root.transform.root; var rows = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<Transform>();
            if (e.Root.partslotscripts != null)
                foreach (var sl in e.Root.partslotscripts)
                {
                    if (sl == null) continue;
                    var br = sl.transform.root;
                    if (br == root || !seen.Add(br)) continue;
                    var rb = br.GetComponent<Rigidbody>(); var j = br.GetComponent<Joint>();
                    var scripts = new System.Collections.Generic.List<string>();
                    foreach (var mb in br.GetComponents<MonoBehaviour>()) if (mb != null) scripts.Add(mb.GetType().Name);
                    rows.Add("{\"name\":" + Json.Str(br.name) + ",\"rb\":" + (rb == null ? "null" : "{\"kin\":" + (rb.isKinematic ? "true" : "false") + ",\"interp\":" + Json.Str(rb.interpolation.ToString()) +
                             ",\"constraints\":" + Json.Str(rb.constraints.ToString()) + ",\"sleeping\":" + (rb.IsSleeping() ? "true" : "false") + ",\"madeKinByUs\":" + (e.MadeKinematic.Contains(rb) ? "true" : "false") + "}") +
                             ",\"joint\":" + (j == null ? "null" : "{\"type\":" + Json.Str(j.GetType().Name) + ",\"to\":" + Json.Str(j.connectedBody != null ? j.connectedBody.name : "") +
                             ",\"toKin\":" + (j.connectedBody != null && j.connectedBody.isKinematic ? "true" : "false") + "}") +
                             ",\"parent\":" + Json.Str(br.parent != null ? br.parent.name : "") + ",\"scripts\":[" + string.Join(",", scripts.ConvertAll(Json.Str)) + "]}");
                }
            // every wheel mesh on the vehicle and on those bodies: its spin angle now, and whether the copy's wheel sync
            // drives it (ProxyWheelOwner) - sampled twice while driving, a wheel that doesn't turn is a frozen wheel
            var wrows = new System.Collections.Generic.List<string>(); var whosts = new System.Collections.Generic.List<Transform> { root }; whosts.AddRange(seen);
            foreach (var hr in whosts)
                foreach (var wg in hr.GetComponentsInChildren<wheelgraphicsscript>(true))
                {
                    var vis = wg.T != null ? wg.T : wg.transform; var lq = Quaternion.Inverse(root.rotation) * vis.rotation;
                    var ic2 = System.Globalization.CultureInfo.InvariantCulture;
                    wrows.Add("[" + Json.Str(hr.name + "/" + wg.name) + "," + (ProxyWheelOwner.ContainsKey(wg) ? "true" : "false") + ",[" +
                              lq.x.ToString("F4", ic2) + "," + lq.y.ToString("F4", ic2) + "," + lq.z.ToString("F4", ic2) + "," + lq.w.ToString("F4", ic2) + "]]");
                }
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"wheels(name,synced,rotInVehicle)\":[" + string.Join(",", wrows) + "]" + ",\"ragdollCopy\":" + (RagdollCopies.Contains(e) ? "true" : "false") + ",\"limbsByBody\":" + (LimbsByBody(e) ? "true" : "false") +
                   ",\"limbs\":" + (Limbs(e) is Transform[] ls ? ls.Length : 0) + ",\"bodies\":[" + string.Join(",", rows) + "]}";
        }

        /// The shared object `net`'s transform here (null when it isn't loaded).
        public static Transform FootRootOf(uint net) => ByNet.TryGetValue(net, out var e) && e.Root != null ? e.Root.transform : null;
    }
}
