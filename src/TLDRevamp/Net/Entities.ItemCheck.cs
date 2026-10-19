using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static pickupable _itemCheckHold;

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class ItemCheckInput
        {
            [HarmonyPostfix]
            private static void Postfix(inputscript __instance)
            {
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (InSession && _itemCheckHold != null && pl != null && pl.pickedUp == _itemCheckHold) __instance.lmb = true;
            }
        }

        /// Bridge `mp itemcheck net [hold|release|launch]`: a sustained real pickup, its release, and body diagnostics.
        public static string ItemCheck(uint net, string action)
        {
            if (!InSession || !ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no loaded entity\"}";
            TelemetryTouch(net);
            var pl = mainscript.s.player;
            var limbs = Limbs(e);
            if (action == "hold")
            {
                var p = e.Root.P;
                if (limbs != null && Limp(e) && e.Root.ragdollPos.ps != null)
                    foreach (var limb in e.Root.ragdollPos.ps) if (limb != null) { p = limb; break; }
                if (p == null || pl == null) return "{\"error\":\"no pickupable or player\"}";
                var at = pl.mainCam.transform.position + pl.mainCam.transform.forward * 0.7f;
                var offset = at - p.transform.position;
                if (limbs != null)
                    foreach (var l in limbs) if (l != null && !l.IsChildOf(e.Root.transform)) l.position += offset;
                e.Root.transform.position += offset;
                pl.Pickup(p, at);
                _itemCheckHold = p;
            }
            else if (action == "release")
            {
                _itemCheckHold = null;
                if (pl != null) { if (pl.inHandP != null) pl.RightHandRelease(true); if (pl.pickedUp != null) pl.Drop(); }
            }
            else if (action == "launch")
            {
                if (e.Proxy) return "{\"error\":\"not the owner\"}";
                var rb = e.Root.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic) return "{\"error\":\"no dynamic root body\"}";
                rb.velocity = e.Root.transform.forward * 8f + Vector3.up * 4f; rb.WakeUp();
            }
            var bodies = new HashSet<Rigidbody>(e.Root.GetComponentsInChildren<Rigidbody>(true));
            if (limbs != null)
                foreach (var l in limbs) { var b = l != null ? l.GetComponent<Rigidbody>() : null; if (b != null) bodies.Add(b); }
            int colliding = 0, dynamic = 0;
            foreach (var b in bodies) { if (b.detectCollisions) colliding++; if (!b.isKinematic) dynamic++; }
            return "{\"net\":" + net + ",\"held\":" + (e.Proxy ? e.Held : IsHeld(e.Root)).ToString().ToLowerInvariant() +
                   ",\"stored\":" + (e.Proxy ? e.Stored : IsStored(e.Root)).ToString().ToLowerInvariant() + ",\"bodies\":" + bodies.Count +
                   ",\"colliding\":" + colliding + ",\"dynamic\":" + dynamic + "}";
        }
    }
}
