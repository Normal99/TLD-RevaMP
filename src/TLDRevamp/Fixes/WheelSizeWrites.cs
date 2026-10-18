using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// wheelgraphicsscript.WheelSize runs every frame for every wheel and writes the WheelCollider's radius, mass and both
    /// friction curves (and the second collider's), although they only change when a wheel or hub is swapped. Each write
    /// is an engine call into the physics wheel; PhysX may wake the car's body on it.
    ///
    /// Exact copy of WheelSize where a write is skipped only when the collider already holds exactly that value (read back
    /// first): the colliders end every frame with the same values as with the original. The original's mass rule
    /// ("mass from the wheel's data; if the collider then reads 0, 20") is kept literally when the data says 0.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.WheelSizeWrites.Enabled
    [HarmonyPatch(typeof(wheelgraphicsscript), nameof(wheelgraphicsscript.WheelSize))]
    public static class WheelSizeWrites
    {
        /// On by default: car park (tools/carpark.py --ab, 16 cars, 2+2 windows twice): 87.35 → 88.50 and 86.55 → 87.35
        /// fps (+1%); every write was redundant (0 writes / 723k skipped); the parked cars stay awake all the same.
        public static bool Enabled = true;
        public static long Calls, Writes, Skipped;

        private static readonly AccessTools.FieldRef<wheelgraphicsscript, WheelFrictionCurve> W1 = AccessTools.FieldRefAccess<wheelgraphicsscript, WheelFrictionCurve>("w1");
        private static readonly AccessTools.FieldRef<wheelgraphicsscript, WheelFrictionCurve> W2f = AccessTools.FieldRefAccess<wheelgraphicsscript, WheelFrictionCurve>("w2");
        private static readonly AccessTools.FieldRef<wheelgraphicsscript, float> LastRadius = AccessTools.FieldRefAccess<wheelgraphicsscript, float>("lastwradius");

        [HarmonyPrefix]
        private static bool Prefix(wheelgraphicsscript __instance)
        {
            if (!Enabled) return true;
            Calls++;
            var g = __instance;
            var W = g.W;
            SetRadius(W, g.GetRadius());
            float s1, s2;
            if (g.slot != null && g.slot.hasPart() && g.slot.part().tosave.P.wheel != null)
            {
                var data = g.slot.part().tosave.P.wheel.data;
                float m = data.GetFloat("mass");
                if (m == 0f) { W.mass = m; if (W.mass == 0f) W.mass = 20f; Writes++; }
                else SetMass(W, m);
                s1 = data.GetFloat("s1");
                s2 = g.sideStiffnessModifier != 0f ? data.GetFloat("s2") * g.sideStiffnessModifier : data.GetFloat("s2");
            }
            else
            {
                var k = mainscript.s.kerekagyProperties[(int)(g.car != null ? g.car.kerekagyType : g.secondaryKerekagyType)];
                SetMass(W, k.mass);
                s1 = k.slip1;
                s2 = k.slip2;
            }
            var f1 = W.forwardFriction;
            var f2 = W.sidewaysFriction;
            if (f1.stiffness != s1) { f1.stiffness = s1; W.forwardFriction = f1; Writes++; } else Skipped++;
            if (f2.stiffness != s2) { f2.stiffness = s2; W.sidewaysFriction = f2; Writes++; } else Skipped++;
            W1(g) = f1;
            W2f(g) = f2;
            if (g.W2 != null)
            {
                // as the original: the second collider gets what the first one holds now (read back)
                SetRadius(g.W2, W.radius);
                var c1 = W.forwardFriction; var c2 = W.sidewaysFriction;
                if (!Same(g.W2.forwardFriction, c1)) { g.W2.forwardFriction = c1; Writes++; } else Skipped++;
                if (!Same(g.W2.sidewaysFriction, c2)) { g.W2.sidewaysFriction = c2; Writes++; } else Skipped++;
            }
            if (g.car != null && LastRadius(g) != W.radius) g.car.UpdWheelLockSize();
            LastRadius(g) = W.radius;
            return false;
        }

        private static void SetRadius(WheelCollider w, float r) { if (w.radius != r) { w.radius = r; Writes++; } else Skipped++; }
        private static void SetMass(WheelCollider w, float m) { if (w.mass != m) { w.mass = m; Writes++; } else Skipped++; }

        private static bool Same(WheelFrictionCurve a, WheelFrictionCurve b) =>
            a.extremumSlip == b.extremumSlip && a.extremumValue == b.extremumValue && a.asymptoteSlip == b.asymptoteSlip &&
            a.asymptoteValue == b.asymptoteValue && a.stiffness == b.stiffness;

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"calls\":" + Calls + ",\"writes\":" + Writes + ",\"skipped\":" + Skipped + "}";
    }
}
