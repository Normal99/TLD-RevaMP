using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static pickupable _itemCheckHold;
        private static uint _itemCheckNet;
        private static float _itemCheckDropGap, _itemCheckDropLimit;

        private static void ItemCheckHoldInput(inputscript input)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (!InSession || input == null || _itemCheckHold == null || pl == null || pl.pickedUp != _itemCheckHold) return;
            input.pickup = true; input.pickupUp = input.pickupDown = false;
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class ItemCheckInput
        {
            [HarmonyPostfix]
            private static void Postfix(inputscript __instance)
            {
                ItemCheckHoldInput(__instance);
            }
        }

        [HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class ItemCheckPlayerInput
        {
            private static void Prefix(fpscontroller __instance)
            {
                if (mainscript.s != null && mainscript.s.player == __instance) ItemCheckHoldInput(inputscript.i);
            }
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Drop), new[] { typeof(bool) })]
        private static class ItemCheckDrop
        {
            private static void Prefix(fpscontroller __instance)
            {
                if (!InSession || _itemCheckHold == null || __instance.pickedUp != _itemCheckHold) return;
                _itemCheckDropGap = (__instance.TpickedUpParent.position - _itemCheckHold.transform.position).magnitude;
                _itemCheckDropLimit = __instance.dropDist * (inputscript.i.recenter ? 2f : 1f);
                SessionLog.Event("itemcheck-drop", _itemCheckNet, value: _itemCheckDropGap, dx: _itemCheckDropLimit,
                    count: inputscript.i.pickupUp ? 1 : 0);
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
                // PickedUpForceAdd clamps its target around this hand anchor, not the rendering camera.
                // A seated/outside camera can put the fixture beyond the game's dropDist on the next frame.
                var at = pl.TpickedUpPos.position;
                var offset = at - p.transform.position;
                // Detached ragdoll branches can still contain other sampled limbs. Move each branch once;
                // translating both a parent and its children stretches the joints and makes the game drop it.
                var roots = new HashSet<Transform> { e.Root.transform };
                if (limbs != null) foreach (var l in limbs) if (l != null) roots.Add(l);
                foreach (var t in roots)
                {
                    bool nested = false;
                    for (var parent = t.parent; parent != null; parent = parent.parent)
                        if (roots.Contains(parent)) { nested = true; break; }
                    if (!nested) t.position += offset;
                }
                Physics.SyncTransforms();
                pl.Pickup(p, at);
                _itemCheckHold = p;
                _itemCheckNet = net;
                _itemCheckDropGap = _itemCheckDropLimit = 0f;
                ItemCheckHoldInput(inputscript.i);
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
                   ",\"colliding\":" + colliding + ",\"dynamic\":" + dynamic +
                   ",\"dropGap\":" + _itemCheckDropGap.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"dropLimit\":" + _itemCheckDropLimit.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
    }
}
