using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HeadUnit
{
    /// A head unit can be stuck anywhere, as the game lets players stick items (its glue key, G by default): on the
    /// dashboard, a pillar, the ceiling. Stuck, it is just the screen: the radio body around it is hidden and lets
    /// everything through (its colliders become triggers: the game's own look ray still finds it, so the pickup key
    /// takes it off again, as with any stuck item). In a radio slot, held or lying around it is the whole radio, as
    /// before.
    ///
    /// Stuck, it faces the player who stuck it, upright, with the back of the screen on the spot looked at. The game
    /// saves the stick and TLD Revamp replays it on every machine (the game's attach record); each machine hides the
    /// body itself when it sees the unit stuck outside a slot.
    [HarmonyPatch]
    public static class Glue
    {
        /// The unit is stuck somewhere other than a slot.
        public static bool Stuck(UnitBehaviour u)
        {
            var at = u != null ? u.Attachable : null;
            return at != null && at.attached && at.slot == null && at.point != null;
        }

        /// Before the game sticks a unit where the player looks: turned to face the player, upright.
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(RaycastHit) })]
        [HarmonyPrefix]
        private static void BeforeStick(attachablescript __instance, RaycastHit _hit)
        {
            var u = __instance.GetComponent<UnitBehaviour>();
            var cam = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Cam : null;
            if (u == null || u.Touch == null || cam == null) return;
            // the screen's face shows towards -forward of its root: that side to the player, its up the world's up
            var toPlayer = cam.transform.position - _hit.point;
            var up = Vector3.ProjectOnPlane(Vector3.up, toPlayer);
            if (toPlayer.sqrMagnitude < 1e-4f || up.sqrMagnitude < 1e-4f) return;
            var want = Quaternion.LookRotation(-toPlayer.normalized, up.normalized);   // the screen root's world rotation
            var t = u.transform;
            t.rotation = want * Quaternion.Inverse(u.Touch.Root.rotation) * t.rotation;
        }

        /// After any attach (a stick, a replayed one from another machine, a loaded save): stuck outside a slot, the
        /// back of the screen goes on the attach point and the body is hidden.
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(Transform) })]
        [HarmonyPostfix]
        private static void AfterAttach(attachablescript __instance)
        {
            var u = __instance.GetComponent<UnitBehaviour>();
            if (u == null) return;
            if (Stuck(u)) { Seat(u); Body(u, false); }
            else Body(u, true);
        }

        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        [HarmonyPostfix]
        private static void AfterDetach(attachablescript __instance)
        {
            var u = __instance.GetComponent<UnitBehaviour>();
            if (u != null) Body(u, true);
        }

        /// The back of the screen's bezel on the attach point (the game puts the body's nearest side there, which left
        /// the screen standing 15 cm off the dashboard behind the hidden radio).
        private static void Seat(UnitBehaviour u)
        {
            var bezel = u.transform.Find(Look.BezelName);
            var at = u.Attachable;
            if (bezel == null || at.point == null) return;
            // the bezel is a unit cube scaled to the face: its back is half its depth behind its centre (into the radio)
            var back = bezel.position + bezel.forward * (bezel.lossyScale.z * 0.5f);
            u.transform.position += at.point.position - back;
        }

        /// The radio body shown and solid (true), or hidden and passable (false). The screen, its bezel and buttons stay.
        public static void Body(UnitBehaviour u, bool on)
        {
            if (u.BodyShown == on) return;
            u.BodyShown = on;
            var keep = new HashSet<Transform>();
            var screen = u.transform.Find(Look.ScreenName); var bezel = u.transform.Find(Look.BezelName);
            if (screen != null) keep.Add(screen);
            if (bezel != null) keep.Add(bezel);
            if (u.Touch != null && u.Touch.Root != null) keep.Add(u.Touch.Root);
            foreach (var r in u.GetComponentsInChildren<Renderer>(true))
            {
                if (Under(r.transform, keep)) continue;
                if (!on) { if (r.enabled) { r.enabled = false; u.HiddenRenderers.Add(r); } }
            }
            if (on) { foreach (var r in u.HiddenRenderers) if (r != null) r.enabled = true; u.HiddenRenderers.Clear(); }
            foreach (var c in u.GetComponentsInChildren<Collider>(true))
            {
                if (Under(c.transform, keep)) continue;
                if (!on) { if (!c.isTrigger) { c.isTrigger = true; u.TriggerColliders.Add(c); } }
            }
            if (on) { foreach (var c in u.TriggerColliders) if (c != null) c.isTrigger = false; u.TriggerColliders.Clear(); }
        }

        private static bool Under(Transform t, HashSet<Transform> keep)
        {
            for (var x = t; x != null; x = x.parent) if (keep.Contains(x)) return true;
            return false;
        }
    }
}
