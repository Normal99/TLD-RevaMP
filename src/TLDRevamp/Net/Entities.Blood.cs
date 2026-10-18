using UnityEngine;

namespace TLDRevamp.Net
{
    /// A car's blood (carscript.blood, a washtriggerscript: the windscreen splatter after running something over). It is
    /// part of the car's own record (save_carscript.blood), which only the owner's resync carries, and its loader only
    /// ever dirties (Load: if (blood) GotDirty()). So, playtest 2026-10-18 ("sponge not synced blood clean"):
    ///  - cleaned by the owner: every copy kept the blood for good;
    ///  - cleaned on a copy (a sponge, drotkefescript → washtriggerscript.Wash → GotClean): nothing went to the owner, and
    ///    its next resync put the blood back on the cleaner's screen.
    /// The game kills a driver who crashes with blood already on the car (carscript.DamageStuff: blood.ON &&
    /// isPlayerDriving → Death), so blood that one player had washed off but the car's owner still had was deadly: "he
    /// killed himself by getting in the car" came 6 minutes after the uncleaned wash, on that car.
    /// Now a copy follows the owner's record both ways, and a wash on a copy goes to the owner as an edit (KindBlood:
    /// the car record, of which the owner takes only "clean").
    public static partial class Entities
    {
        public static bool BloodSync = true;   // A/B: false = before v0.69.2 (blood never washes off a copy; a copy's wash stays local)
        public static long BloodCleanedOnCopy, BloodCleanSent, BloodCleanApplied;

        /// The record says clean, this car is bloody: wash it (the game's own way, as a sponge would).
        private static void BloodFollow(tosaveitemscript it, itemDataClass d)
        {
            if (!BloodSync || it == null || it.car == null || d.car == null || d.car.Count == 0 || d.car[0] == null) return;
            var w = it.car.blood;
            if (w == null || !w.ON || d.car[0].blood || w.destroyOnRepair) return;
            w.GotClean();
            BloodCleanedOnCopy++;
        }

        /// Owner: an edit that washed our car's blood off.
        private static void BloodEditApply(tosaveitemscript it, itemDataClass d)
        {
            if (!BloodSync || it == null || it.car == null || it.car.blood == null || d.car == null || d.car.Count == 0 || d.car[0] == null) return;
            var w = it.car.blood;
            if (!w.ON || d.car[0].blood || w.destroyOnRepair) return;
            w.GotClean();
            BloodCleanApplied++;
        }

        [HarmonyLib.HarmonyPatch(typeof(washtriggerscript), nameof(washtriggerscript.GotClean))]
        private static class BloodWashed
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(washtriggerscript __instance)
            {
                if (!BloodSync || !InSession || _applyingState || __instance == null) return;
                var car = __instance.GetComponentInParent<carscript>();
                if (car == null || car.blood != __instance) return;
                var it = car.GetComponent<tosaveitemscript>();
                if (it == null) it = car.GetComponentInParent<tosaveitemscript>();
                if (it == null) return;
                if (IsProxy(it)) BloodCleanSent++;
                MarkEdit(it, kind: "blood");   // a copy: to the owner; our own car: its resync goes out now
            }
        }

        /// Test (bridge `mp blood <net> [dirty|clean]`): the car's blood here, made dirty or washed the game's way.
        public static string BloodTest(uint net, string what)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such car here\"}";
            var w = e.Root.car.blood;
            if (w == null) return "{\"error\":\"this car has no blood\"}";
            if (what == "dirty") w.GotDirty();
            else if (what == "clean") w.Wash(true, 1, 2f, null);   // past repairValue 1: GotClean, as a sponge's last stroke
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"blood\":" + (w.ON ? "true" : "false") + ",\"cleanedOnCopy\":" + BloodCleanedOnCopy +
                   ",\"sent\":" + BloodCleanSent + ",\"applied\":" + BloodCleanApplied + "}";
        }
    }
}
