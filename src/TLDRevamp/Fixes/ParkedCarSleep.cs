using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// carscript.Update does, every frame for every car:
    ///     RB.velocity = Vector3.ClampMagnitude(RB.velocity, mainscript.s.maxCarVelocity);
    /// Below the speed cap ClampMagnitude returns the same vector, but assigning Rigidbody.velocity always wakes the body.
    /// Measured: parked cars put to sleep were all awake again 0.05 s later. So no car ever sleeps and the physics engine
    /// keeps simulating every parked car (car park: ~7 ms/frame physics).
    ///
    /// The assignment is routed through Assign(): it's skipped only when it would not change the velocity AND the car is
    /// parked (nobody driving, ignition off). The only behavioural difference is that resting parked cars may now fall
    /// asleep like any other resting object. Contacts wake them as usual.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.ParkedCarSleep.Enabled
    [HarmonyPatch(typeof(carscript), "Update")]
    public static class ParkedCarSleep
    {
        public static bool Enabled = false; // decided after experiment + A/B + playtest
        public static long Assigned, Skipped;
        public static bool Patched;

        private static readonly MethodInfo SetVelocity = AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.velocity));
        private static readonly MethodInfo Clamp = AccessTools.Method(typeof(Vector3), nameof(Vector3.ClampMagnitude));
        private static readonly MethodInfo Helper = AccessTools.Method(typeof(ParkedCarSleep), nameof(Assign));

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 1 < list.Count; i++)
            {
                // ... call Vector3.ClampMagnitude ; callvirt Rigidbody.set_velocity
                if (list[i].Calls(Clamp) && list[i + 1].Calls(SetVelocity))
                {
                    // Same stack (rb, v): swap the setter for Assign(rb, v) in place, keeping the instruction's labels
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = Helper;
                    Patched = true;
                    break;
                }
            }
            if (!Patched) Plugin.Log.LogWarning("ParkedCarSleep: velocity clamp not found in carscript.Update; not patched");
            return list;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Rigidbody, carscript> Cars =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Rigidbody, carscript>();

        public static void Assign(Rigidbody rb, Vector3 v)
        {
            // carscript.RB belongs to the car; find the car once per body
            var car = Enabled ? Cars.GetValue(rb, r => r.GetComponent<carscript>() ?? r.GetComponentInChildren<carscript>() ?? r.GetComponentInParent<carscript>()) : null;
            if (Enabled && car != null && !car.driving && !car.driving2 && !car.isPlayerDriving && !car.ignition && rb.velocity == v)
            {
                Skipped++;
                return; // same value: the only effect of assigning would be waking the body
            }
            Assigned++;
            rb.velocity = v;
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + (Patched ? "true" : "false") +
            ",\"assigned\":" + Assigned + ",\"skipped\":" + Skipped + "}";
    }
}
