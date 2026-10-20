using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Walking a vehicle by its handlebars (pushgripable: the push key on a grip). The game joins the player to the
    /// vehicle's body (a ConfigurableJoint on the player) and steers it with the player's steering keys (carscript reads
    /// pushgripable.steerValue). On a vehicle another machine owns that body is a kinematic copy: the player was held
    /// by something that does not move, and the steering went nowhere.
    /// Now taking the grip takes the vehicle, as picking up an item does (provisional at once, the server confirms):
    /// this machine moves it, the others see it walked. Not from its driver: a vehicle someone rides is let go at once
    /// (the server refuses that claim too); and if the vehicle goes to another machine while held, it is let go.
    public static partial class Entities
    {
        public static long GripClaims, GripRefused, GripLost;
        private static uint _gripNet;

        private static void PushGripTick()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var g = pl != null && pl.pushgripping ? pl.pushgrip : null;
            if (g == null) { _gripNet = 0; return; }
            var root = g.transform.root;
            Ent e = null;
            foreach (var x in ByNet.Values) if (x.Root != null && x.PartIndex < 0 && x.Root.transform.root == root) { e = x; break; }
            if (e == null) return;
            if (!e.Proxy) { _gripNet = e.NetId; return; }
            if (_gripNet == e.NetId) { pl.StopPushGripping(); GripLost++; _gripNet = 0; return; }   // gone to another machine: not ours to hold
            if (e.Driven) { pl.StopPushGripping(); GripRefused++; return; }                        // someone rides it
            SetProxy(e, false);
            e.OwnerId = MyId;
            W.Reset(); WriteClaim(W, e.NetId, ClaimReason.PushGrip); ToServer(W, true);
            ClaimsSent++; GripClaims++;
            _gripNet = e.NetId;
        }

        // ---- test hooks (bridge `mp grip net [secs] [steer]`, `mp grip stop`): take the vehicle's grip the way the
        // game does (fpscontroller's push-key branch on a pushgripable) and walk with the forward key held
        private static readonly AccessTools.FieldRef<fpscontroller, ConfigurableJoint> GripJoint = AccessTools.FieldRefAccess<fpscontroller, ConfigurableJoint>("pushGripJoint");
        private static readonly AccessTools.FieldRef<fpscontroller, bool> GripNow = AccessTools.FieldRefAccess<fpscontroller, bool>("pushgripnow");
        private static float _gripWalkUntil, _gripSteer;

        public static string GripTest(string[] a)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (a[1] == "stop") { pl.StopPushGripping(); _gripWalkUntil = 0f; return "{\"stopped\":true}"; }
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            uint net = uint.Parse(a[1]);
            float secs = a.Length > 2 ? float.Parse(a[2], ic) : 0f;
            _gripSteer = a.Length > 3 ? float.Parse(a[3], ic) : 0f;
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such vehicle\"}";
            var grips = e.Root.car.pushgrip;
            if (grips == null || grips.Length == 0) return "{\"error\":\"no grip\"}";
            pushgripable best = null; float bd = float.MaxValue;
            foreach (var gp in grips) if (gp != null) { float d = (gp.transform.position - pl.transform.position).sqrMagnitude; if (d < bd) { bd = d; best = gp; } }
            if (best == null || (best.inuse && pl.pushgrip != best)) return "{\"error\":\"grip in use\"}";
            if (pl.pushgrip != best)
            {
                // where a player taking it stands: at the grip, beside the vehicle (griptest run 1: 1.9 m away, past the
                // game's pushGripDistance - let go the next frame)
                var side = best.transform.position - e.Root.transform.position; side.y = 0f;
                var at = best.transform.position + (side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.right) * 0.5f;
                at.y = pl.transform.position.y;
                pl.Teleport(at);
                bd = (best.transform.position - at).sqrMagnitude;
                var f = e.Root.transform.forward; f.y = 0f;   // a player taking the grip faces along the vehicle
                if (f.sqrMagnitude > 1e-4f) pl.BodyRot.rotation = Quaternion.LookRotation(f);
                pl.pushgripping = true; GripNow(pl) = true;
                pl.pushgrip = best; best.GetRot(); best.inuse = true;
                var j = pl.gameObject.AddComponent<ConfigurableJoint>();
                j.xMotion = ConfigurableJointMotion.Locked; j.yMotion = ConfigurableJointMotion.Free; j.zMotion = ConfigurableJointMotion.Locked;
                j.angularXMotion = ConfigurableJointMotion.Free; j.angularYMotion = ConfigurableJointMotion.Free; j.angularZMotion = ConfigurableJointMotion.Free;
                j.connectedBody = best.transform.root.GetComponent<Rigidbody>();
                GripJoint(pl) = j;
            }
            _gripWalkUntil = Time.time + secs;
            return "{\"gripping\":" + Json.Str(best.name) + ",\"dist\":" + Mathf.Sqrt(bd).ToString("F2", ic) + ",\"maxDist\":" + Mathf.Sqrt(pl.pushGripDistance).ToString("F2", ic) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + "}";
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class GripWalk
        {
            [HarmonyPostfix]
            private static void Postfix(inputscript __instance)
            {
                if (_gripWalkUntil <= 0f) return;
                if (Time.time > _gripWalkUntil) { _gripWalkUntil = 0f; return; }
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl == null || !(pl.pushgripping || (_drawbarWalk && pl.pickedUp != null))) return;
                __instance.forward = 1f; __instance.steer = _gripSteer;
            }
        }

        private static bool _drawbarWalk;

        /// Bridge `mount drawbar <net> [secs] [steer]`: the local player picks up trailer `net`'s drawbar
        /// (attachgripscript.Grip, as the pickup key does: fpscontroller.Pickup) and walks forward with it for secs;
        /// `mount drawbar stop` lets go (fpscontroller.Drop); `mount drawbar state` what the player holds. For trailercreep.py's walk.
        public static string DrawbarTest(string[] a)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (a[1] == "state") return "{\"pickedUp\":" + Json.Str(pl.pickedUp != null ? pl.pickedUp.name : "") + ",\"walking\":" + (_drawbarWalk && Time.time < _gripWalkUntil ? "true" : "false") + "}";
            if (a[1] == "stop") { _drawbarWalk = false; _gripWalkUntil = 0f; if (pl.pickedUp != null) pl.Drop(); return "{\"dropped\":true}"; }
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!ByNet.TryGetValue(uint.Parse(a[1]), out var e) || e.Root == null) return "{\"error\":\"no such item\"}";
            var g = e.Root.GetComponentInChildren<attachgripscript>(true);
            var grip = g != null && g.Grip != null ? g.Grip.GetComponent<pickupable>() : null;
            if (grip == null) return "{\"error\":\"no drawbar\"}";
            if (pl.pickedUp != grip.GetComponent<pickupable>())
            {
                // standing a step in front of the drawbar's end, facing it
                var away = grip.transform.position - e.Root.transform.position; away.y = 0f;
                var stand = grip.transform.position + (away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward) * 0.3f;
                stand.y = grip.transform.position.y + 0.1f;
                pl.Teleport(stand);
                // facing away from the trailer, to pull it along
                if (away.sqrMagnitude > 1e-4f) pl.BodyRot.rotation = Quaternion.LookRotation(away.normalized);
                pl.Pickup(grip, grip.transform.position);
            }
            _gripSteer = a.Length > 3 ? float.Parse(a[3], ic) : 0f;
            _drawbarWalk = true;
            _gripWalkUntil = Time.time + (a.Length > 2 ? float.Parse(a[2], ic) : 0f);
            return "{\"holding\":" + (pl.pickedUp == grip ? "true" : "false") + ",\"grip\":" + Json.Str(grip.name) + "}";
        }

        public static string GripStatus()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return "{\"gripping\":" + (pl != null && pl.pushgripping ? "true" : "false") + ",\"grip\":" + Json.Str(pl != null && pl.pushgrip != null ? pl.pushgrip.name : "") +
                   ",\"yaw\":" + (pl != null && pl.pushgrip != null && pl.pushgrip.car != null ? pl.pushgrip.car.transform.eulerAngles.y : 0f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"steer\":" + (pl != null && pl.pushgrip != null && pl.pushgrip.car != null ? pl.pushgrip.car.steer : 0f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"net\":" + _gripNet + ",\"claims\":" + GripClaims + ",\"refused\":" + GripRefused + ",\"lost\":" + GripLost + "}";
        }

        /// Diagnostics (bridge `mp grips`): the item prefabs with grips: [index, name, grips].
        public static string GripPrefabs()
        {
            var rows = new List<string>();
            var db = itemdatabase.s != null ? itemdatabase.s.items : null;
            if (db == null) return "{\"error\":\"no itemdatabase\"}";
            for (int i = 0; i < db.Length; i++)
            {
                var c = db[i] != null ? db[i].GetComponent<carscript>() : null;
                if (c != null && c.pushgrip != null && c.pushgrip.Length > 0) rows.Add("[" + i + "," + Json.Str(db[i].name) + "," + c.pushgrip.Length + "]");
            }
            return "{\"grips\":[" + string.Join(",", rows) + "]}";
        }
    }
}
