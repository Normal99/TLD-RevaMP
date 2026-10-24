using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// wheelgraphicsscript.WheelSize runs every frame for every wheel and writes the WheelCollider's radius, mass and both
    /// friction curves (and the second collider's), although they only change when a wheel or hub is swapped. Each write
    /// is an engine call into the physics wheel; PhysX may wake the car's body on it.
    ///
    /// The game's own WheelSize with its collider writes swapped at runtime (GamePatch) for ones that skip a write only
    /// when the collider already holds exactly that value (read back first): the colliders end every frame with the
    /// same values as with the game's code. Its mass rule ("mass from the wheel's data; if the collider then reads 0,
    /// 20") stays the game's own: a 0 is written (the collider holds 20), read back, and 20 written again.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.WheelSizeWrites.Enabled
    [HarmonyPatch(typeof(wheelgraphicsscript), nameof(wheelgraphicsscript.WheelSize))]
    public static class WheelSizeWrites
    {
        /// On by default: car park (tools/carpark.py --ab, 16 cars, 2+2 windows twice): 87.35 → 88.50 and 86.55 → 87.35
        /// fps (+1%); every write was redundant (0 writes / 723k skipped); the parked cars stay awake all the same.
        public static bool Enabled = true;
        public static long Calls, Writes, Skipped;
        public static int Patched;

        private static MethodInfo Our(string name) => AccessTools.Method(typeof(WheelSizeWrites), name);
        private static MethodInfo Setter(string name) => AccessTools.PropertySetter(typeof(WheelCollider), name);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var p = new GamePatch(nameof(WheelSizeWrites), instructions);
            p.Calls(Setter(nameof(WheelCollider.radius)), Our(nameof(Radius)), 2);
            p.Calls(Setter(nameof(WheelCollider.mass)), Our(nameof(Mass)), 4);
            p.Calls(Setter(nameof(WheelCollider.forwardFriction)), Our(nameof(Forward)), 4);
            p.Calls(Setter(nameof(WheelCollider.sidewaysFriction)), Our(nameof(Sideways)), 4);
            return p.Result(ref Patched);
        }

        [HarmonyPrefix]
        private static void Count() { if (Enabled) Calls++; }

        public static void Radius(WheelCollider w, float r)
        {
            if (Enabled && w.radius == r) { Skipped++; return; }
            w.radius = r; Writes++;
        }

        public static void Mass(WheelCollider w, float m)
        {
            if (Enabled && w.mass == m) { Skipped++; return; }
            w.mass = m; Writes++;
        }

        public static void Forward(WheelCollider w, WheelFrictionCurve c)
        {
            if (Enabled && Same(w.forwardFriction, c)) { Skipped++; return; }
            w.forwardFriction = c; Writes++;
        }

        public static void Sideways(WheelCollider w, WheelFrictionCurve c)
        {
            if (Enabled && Same(w.sidewaysFriction, c)) { Skipped++; return; }
            w.sidewaysFriction = c; Writes++;
        }

        private static bool Same(WheelFrictionCurve a, WheelFrictionCurve b) =>
            a.extremumSlip == b.extremumSlip && a.extremumValue == b.extremumValue && a.asymptoteSlip == b.asymptoteSlip &&
            a.asymptoteValue == b.asymptoteValue && a.stiffness == b.stiffness;

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"calls\":" + Calls + ",\"writes\":" + Writes + ",\"skipped\":" + Skipped + "}";
    }
}
