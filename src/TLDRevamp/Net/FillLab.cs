using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Test tooling (tools/fluids.py): filling a tank the way a player does — a can in hand, the tank's cap opened (its
    /// usable, the game's sync point), the player looking at the cap and holding the left mouse button. The game then
    /// fills by itself (fpscontroller.DrinkFill: straight from the held can into the cap's tank, per frame). Here only
    /// the view and the button are held, like DriveLab holds the pedals.
    [HarmonyPatch]
    public static class FillLab
    {
        private static tankcapscript _cap;
        private static float _safeUntil;
        public static bool Active => _cap != null;

        /// The player steps up to tank ti of `car` (1 m out from its cap), the can (net `can`) is put in front of their
        /// face and picked up — a pick-up from afar is dropped by the game at once (fpscontroller.PickedUpForceAdd:
        /// beyond dropDist, fluids runs 6/7) — the cap is opened, then view and button are held.
        public static string Start(uint can, uint car, int ti)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null) return "{\"error\":\"not in game\"}";
            var cp = Entities.PickupableOf(can);
            if (cp == null || cp.tank == null) return "{\"error\":\"no can " + can + " here\"}";
            var t = Entities.TankOf(car, ti);
            if (t == null || t.TC == null) return "{\"error\":\"no tank " + ti + "\"}";
            _cap = null;
            // the filler: of the tank's caps, the one facing up that only pours when tipped — a radiator's other cap is its
            // drain (run 15: opening it emptied the radiator, 1.5 → 0 L in 3 s, on both machines alike)
            var caps = new System.Collections.Generic.List<string>();
            float best = float.MaxValue;
            foreach (var c in t.TC)
            {
                if (c == null) continue;
                float up = Vector3.Angle(Vector3.up, c.transform.forward);
                caps.Add(c.name + (c.tube ? " tube" : "") + (c.noAngle ? " noAngle" : "") + (c.noPour ? " noPour" : "") + " up " + up.ToString("F0"));
                if (c.tube || c.noAngle) continue;
                if (up < best) { best = up; _cap = c; }
            }
            if (_cap == null) return "{\"error\":\"no filler cap: " + string.Join(", ", caps) + "\"}";
            if (_cap.usable != null && _cap.usable.turnable && _cap.usable.currentTurnState == 0) _cap.usable.UpdTurnState(1, syncinmulti: true);
            // out from the car's physical centre (its root's pivot differs between the owner's car and a copy: run 8)
            var rb = t.GetComponentInParent<Rigidbody>();
            var away = _cap.transform.position - (rb != null ? rb.worldCenterOfMass : t.transform.root.position); away.y = 0f;
            // the teleport's jolt counted as a deadly fall on the laptop (run 9: FallDamage; run 10: still, after it was
            // grounded — 1 m out its capsule touched the copy car's body) — no fall deaths for 2 s (1.6 m out was beyond reach: run 11)
            p.godModeTillGrounded = true;
            if (p.RB != null) p.RB.velocity = Vector3.zero;
            p.Teleport(StandSpot(t.transform.root, away.normalized));
            _safeUntil = Time.time + 2f;
            Aim(p);
            var cam = p.mainCam.transform;
            if (cp.RB != null) { cp.RB.velocity = Vector3.zero; cp.RB.angularVelocity = Vector3.zero; }
            cp.transform.position = cam.position + cam.forward * 0.7f + Vector3.down * 0.2f;
            if (cp.RB != null) cp.RB.position = cp.transform.position;
            p.Pickup(cp, cp.transform.position);
            return "{\"caps\":" + Json.Str(string.Join(", ", caps)) + ",\"stand\":" + Json.Str((p.transform.position - _cap.transform.position).ToString("F2")) + ",\"cap\":" + Json.Str(_cap.name) + ",\"tank\":" + Json.Str(t.name) + ",\"amount\":" + t.F.GetAmount() + ",\"held\":" + Json.Str(p.pickedUp != null ? p.pickedUp.name : "") + "}";
        }

        /// Where a player stands to reach the cap: out from it horizontally (straight away from the car first, then
        /// turning), the nearest spot 0.7–1.4 m out where a player's body touches none of the car's colliders. Caps
        /// under the bonnet are at the front: 1 m straight out put the laptop inside the bumper, the car pushed it off
        /// with the can (run 14).
        private static Vector3 StandSpot(Transform car, Vector3 away)
        {
            var c = _cap.transform.position;
            foreach (float turn in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f })
            {
                var dir = Quaternion.AngleAxis(turn, Vector3.up) * away;
                for (float d = 0.7f; d <= 1.41f; d += 0.1f)
                {
                    var feet = c + dir * d + Vector3.down * 0.6f;
                    bool hitsCar = false;
                    foreach (var col in Physics.OverlapCapsule(feet + Vector3.down * 0.3f, feet + Vector3.up * 1.2f, 0.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        if (col != null && col.transform.root == car) { hitsCar = true; break; }
                    if (!hitsCar) return feet;
                }
            }
            return c + away * 1.0f + Vector3.down * 0.6f;
        }

        private static void Aim(fpscontroller p)
        {
            var d = _cap.transform.position - p.mainCam.transform.position;
            var flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude > 1e-4f) p.BodyRot.rotation = Quaternion.LookRotation(flat);
            p.FyRot = -Mathf.Atan2(d.y, flat.magnitude) * Mathf.Rad2Deg;
            if (!mainscript.IsVR()) p.Th.localEulerAngles = new Vector3(p.FyRot, 0f, 0f);
        }

        // ---------------------------------------------------------------- walking with something in hand (tools/heldpush.py)
        private static Transform _walkTo;
        private static float _walkUntil;

        /// Pick item `net` up at arm's length in front of the player (a pick-up from afar is dropped at once).
        public static string HoldNet(uint net)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            var cp = Entities.PickupableOf(net);
            if (p == null || cp == null) return "{\"error\":\"no item " + net + " here\"}";
            var cam = p.mainCam.transform;
            if (cp.RB != null) { cp.RB.velocity = Vector3.zero; cp.RB.angularVelocity = Vector3.zero; }
            cp.transform.position = cam.position + cam.forward * 0.7f + Vector3.down * 0.2f;
            if (cp.RB != null) cp.RB.position = cp.transform.position;
            p.Pickup(cp, cp.transform.position);
            return "{\"held\":" + Json.Str(p.pickedUp != null ? p.pickedUp.name : "") + "}";
        }

        /// Walk (the forward key held) towards object `net`'s centre for `secs`, facing it — what a player pushing
        /// a held item against a car does.
        public static string WalkAt(uint net, float secs)
        {
            var pk = Entities.PickupableOf(net);
            var root = pk != null ? pk.transform.root : Entities.RootOf(net);
            if (root == null) return "{\"error\":\"no object " + net + " here\"}";
            _walkTo = root; _walkUntil = Time.time + secs;
            return "{\"walking\":" + secs + "}";
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        [HarmonyPostfix]
        private static void Walk(inputscript __instance)
        {
            if (_walkTo == null) return;
            if (Time.time > _walkUntil) { _walkTo = null; return; }
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null) return;
            var rb = _walkTo.GetComponentInChildren<Rigidbody>();
            var d = (rb != null ? rb.worldCenterOfMass : _walkTo.position) - p.transform.position; d.y = 0f;
            if (d.sqrMagnitude > 1e-4f) p.BodyRot.rotation = Quaternion.LookRotation(d);
            p.FyRot = 10f;
            __instance.forward = 1f;
        }

        public static string Stop()
        {
            if (_cap == null) return "{\"error\":\"not filling\"}";
            if (_cap.usable != null && _cap.usable.turnable) _cap.usable.UpdTurnState(0, syncinmulti: true);
            _cap = null;
            return "{\"stopped\":true}";
        }

        public static string Status()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || _cap == null) return "{\"active\":false}";
            return "{\"active\":true,\"selected\":" + Json.Str(p.selectedCap != null ? p.selectedCap.name : "") + ",\"lookDist\":" +
                   (p.lookPoint - _cap.transform.position).magnitude.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"valve\":" + _cap.valve + ",\"held\":" + Json.Str(p.pickedUp != null ? p.pickedUp.name : "") + "}";
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        [HarmonyPostfix]
        private static void Hold(inputscript __instance)
        {
            if (_cap == null) return;
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || p.pickedUp == null || p.mainCam == null) return;
            if (Time.time < _safeUntil) p.godModeTillGrounded = true;
            Aim(p);
            __instance.lmb = true;
        }
    }
}
