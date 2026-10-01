using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// What made a car lose parts? The game tears parts off in carscript.DamageStuff(v) — v is the car's speed change in
    /// one frame, not a collision — so a body pushed out of something it was placed inside (a depenetration jolt) wrecks a
    /// car as surely as a wall (fluids run 16: 13 parts off the host's parked car, no collision reported). Logged when v
    /// is in the range where parts can come off: the car (net, owned/copy), v, its last collisions (other object, impact
    /// speed, how long ago) and every foreign collider overlapping the car now (net, owner, copy/own, kinematic, held by
    /// this machine's player, penetration depth). Bridge: `get TLDRevamp.Diagnostics.CrashCause.Last`.
    [HarmonyPatch]
    public static class CrashCause
    {
        public static bool Enabled = true;
        public static int Max = 20;
        public static long Logged;
        public static string Last = "";

        private struct Hit { public float T, V; public string Other; }
        private static readonly Dictionary<carscript, Queue<Hit>> Recent = new Dictionary<carscript, Queue<Hit>>();

        [HarmonyPatch(typeof(carscript), "OnCollisionEnter")]
        [HarmonyPostfix]
        private static void Collided(carscript __instance, Collision c)
        {
            if (!Enabled || c == null || c.collider == null) return;
            if (!Recent.TryGetValue(__instance, out var q)) Recent[__instance] = q = new Queue<Hit>();
            q.Enqueue(new Hit { T = Time.time, V = c.relativeVelocity.magnitude, Other = Describe(c.collider) });
            while (q.Count > 6) q.Dequeue();
        }

        [HarmonyPatch(typeof(carscript), nameof(carscript.DamageStuff))]
        [HarmonyPrefix]
        private static void Damage(carscript __instance, float v)
        {
            if (!Enabled || __instance == null || Logged >= Max) return;
            var ms = mainscript.s;
            if (ms == null) return;
            // as DamageStuff: v × crashMultiplier × crashFallOffMultiplier against a threshold between crashSpeedMin/MaxFallOff,
            // both lowered down to 0.2× for a car in bad condition
            float k = __instance.condition != null ? Mathf.Lerp(1f, 0.2f, Mathf.InverseLerp(0f, 4f, __instance.condition.state)) : 1f;
            float min = ms.crashSpeedMinFallOff * k, max = ms.crashSpeedMaxFallOff * k;
            v *= __instance.crashMultiplier;
            if (!(v * __instance.crashFallOffMultiplier > min)) return;
            Logged++;
            var root = __instance.transform.root;
            var sb = new System.Text.StringBuilder();
            sb.Append("car ").Append(root.name).Append(Net.Entities.IsProxy(__instance) ? " (copy)" : " (own)")
              .Append(" Δv ").Append(v.ToString("F1")).Append(" × ").Append(__instance.crashFallOffMultiplier.ToString("F2"))
              .Append(" vs falloff ").Append(min.ToString("F1")).Append("–").Append(max.ToString("F1"))
              .Append(" at global ").Append(mainscript.GlobalFromUnityPos(root.position).ToString());
            sb.Append(" | last hits:");
            if (Recent.TryGetValue(__instance, out var q))
                foreach (var h in q) sb.Append(' ').Append(h.Other).Append(" @").Append(h.V.ToString("F1")).Append("m/s ").Append((Time.time - h.T).ToString("F2")).Append("s ago;");
            sb.Append(" | inside it now:");
            var cols = new List<Collider>();
            foreach (var col in root.GetComponentsInChildren<Collider>()) if (col != null && col.enabled && !col.isTrigger) cols.Add(col);
            var seen = new HashSet<Collider>();
            foreach (var mine in cols)
            {
                var b = mine.bounds;
                foreach (var other in Physics.OverlapBox(b.center, b.extents, Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (other == null || other.transform.root == root || other.GetType().Name == "TerrainCollider" || !seen.Add(other)) continue;
                    if (!Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation, other, other.transform.position, other.transform.rotation, out _, out float depth)) continue;
                    sb.Append(' ').Append(Describe(other)).Append(" in ").Append(mine.name).Append(" by ").Append(depth.ToString("F2")).Append("m;");
                }
            }
            Last = sb.ToString();
            Plugin.Log.LogWarning("crash cause #" + Logged + ": " + Last);
        }

        private static string Describe(Collider c)
        {
            var root = c.transform.root;
            var rb = c.attachedRigidbody;
            var p = mainscript.s != null ? mainscript.s.player : null;
            string held = p != null && p.pickedUp != null && p.pickedUp.transform.root == root ? " HELD-HERE" : "";
            if (p != null && root == p.transform.root) held = " LOCAL-PLAYER";
            return root.name + "/" + c.name + " [" + Net.Entities.DescribeRoot(root) + (rb == null ? " static" : rb.isKinematic ? " kin" : " dyn") + held + "]";
        }
    }
}
