using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Rabbits, back. The beta still has the rabbit (itemdatabase item 103 "Nyul": nyulscript, aiscript, sensoryscript,
    /// movecreaturescript, a ragdoll; the big ones turn evil and kill, a car can run one over) and its spawner on the
    /// player (aispawn on the Player prefab: rabbits around the player, out of view, up to its limit) - but nothing
    /// starts the spawner (aispawn.START has no caller), so no rabbit ever comes up; only the dev menu made one.
    ///
    /// The game's own spawner is started once the player is in a world, with its own settings (chance, wait, count,
    /// distance, field of view). In a session (Net.Entities.Rabbits) every machine runs its own spawner around its own
    /// player - it has the ground there; the host does not generate around the others - and a new rabbit is shared at
    /// once. The limit counts every rabbit around the player, the others' too (players together do not get twice as
    /// many), and a rabbit never comes up near another player or where they look.
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.Rabbits.Enabled (off: the beta as it was - the spawner never runs)
    public static class Rabbits
    {
        public static bool Enabled = true;
        public static long Starts, Stops, Spawned, Limited, NearOthers;
        public static float CountM = 0f;   // others' rabbits this near the player count for its limit; 0: the spawner's maxDist + 50 m

        private static aispawn _sp;
        private static float _checkAt;

        /// The local player's spawner (on the Player prefab).
        internal static aispawn Spawner()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) { _sp = null; return null; }
            if (_sp == null || _sp.transform.root != pl.transform.root) _sp = pl.GetComponentInChildren<aispawn>(true);
            return _sp;
        }

        /// Once a second: the spawner runs while the switch is on and the player is in a world (a dedicated server has no
        /// player: no spawner).
        internal static void Tick()
        {
            if (Time.unscaledTime < _checkAt) return;
            _checkAt = Time.unscaledTime + 1f;
            var sp = Spawner();
            if (sp == null || sp.AIPrefab == null || !sp.isActiveAndEnabled) return;
            bool want = Enabled && !mainscript.s.died;
            if (want && !sp.started) { sp.START(); Starts++; Plugin.Log.LogInfo("rabbits: the player's spawner started (max " + sp.maxAINum + ", chance " + sp.chance + ", every " + sp.minTime + "-" + sp.maxTime + " s, " + sp.minDist + "-" + sp.maxDist + " m)"); }
            else if (!want && sp.started) { sp.STOP(); sp.StopAllCoroutines(); Stops++; }
        }

        /// Rabbits around the local player: this machine's own (the spawner's list) and, in a session, every rabbit within
        /// CountM - a copy of another player's too.
        internal static int Around(aispawn sp)
        {
            int n = 0;
            float r = CountM > 0f ? CountM : sp.maxDist + 50f;
            var at = sp.bodyRot != null ? sp.bodyRot.position : sp.transform.position;
            var seen = new HashSet<nyulscript>();
            foreach (var go in sp.AIs) { var ny = go != null ? go.GetComponentInChildren<nyulscript>() : null; if (ny != null && seen.Add(ny)) n++; }
            if (!Net.Entities.InSession) return n;
            foreach (var ny in Object.FindObjectsOfType<nyulscript>())
                if (ny != null && !seen.Contains(ny) && (ny.transform.position - at).sqrMagnitude < r * r) { seen.Add(ny); n++; }
            return n;
        }

        /// In a session the limit counts the others' rabbits near the player too.
        [HarmonyPatch(typeof(aispawn), nameof(aispawn.TrySpawn))]
        private static class Limit
        {
            [HarmonyPrefix]
            private static bool Prefix(aispawn __instance)
            {
                if (!Net.Entities.InSession || __instance != Spawner()) return true;
                if (Around(__instance) < __instance.maxAINum) return true;
                Limited++;
                return false;
            }
        }

        /// Never near another player or in their view: the game keeps a spawn out of the local player's field of view
        /// (halfFov) - the same for every other player (their body faces where they look), and never closer than the
        /// spawner's minDist to them.
        [HarmonyPatch(typeof(aispawn), nameof(aispawn.CanSpawn))]
        private static class NotNearOthers
        {
            [HarmonyPostfix]
            private static void Postfix(aispawn __instance, ref Vector3 __result)
            {
                if (__result == Vector3.zero || !Net.Entities.InSession) return;
                foreach (var t in Net.RemotePlayers.AiTargets())
                {
                    var d = __result - t.body.position;
                    var flat = Vector3.ProjectOnPlane(d, Vector3.up);
                    if (flat.magnitude < __instance.minDist || Vector3.Angle(Vector3.ProjectOnPlane(t.body.forward, Vector3.up), flat) <= __instance.halfFov)
                    {
                        NearOthers++;
                        __result = Vector3.zero;
                        return;
                    }
                }
            }
        }

        /// A new rabbit: shared at once in a session (aispawn instantiates the prefab itself, not through mainscript.Spawn).
        [HarmonyPatch(typeof(aispawn), nameof(aispawn.Spawn))]
        private static class Share
        {
            [HarmonyPostfix]
            private static void Postfix(aispawn __instance)
            {
                Spawned++;
                if (__instance.AIs.Count > 0) Net.Entities.RabbitSpawned(__instance.AIs[__instance.AIs.Count - 1]);
            }
        }

        /// Test (bridge `rabbits spawn`): one rabbit from the player's spawner now, as its routine would (its own place
        /// rules; the chance and the limit skipped).
        public static string TestSpawn()
        {
            var sp = Spawner();
            if (sp == null) return "{\"error\":\"no spawner\"}";
            int before = sp.AIs.Count;
            for (int k = 0; k < 20 && sp.AIs.Count == before; k++)
            {
                var p = sp.CanSpawn(sp.RandomPlace());
                if (p != Vector3.zero) sp.Spawn(p);
            }
            if (sp.AIs.Count == before) return "{\"error\":\"no place found\"}";
            var go = sp.AIs[sp.AIs.Count - 1];
            var g = mainscript.GlobalFromUnityPos(go.transform.position);
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"spawned\":" + Json.Str(go.name) + ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]}";
        }

        public static string Stats()
        {
            var sp = Spawner();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string s = sp == null ? "null" :
                "{\"started\":" + (sp.started ? "true" : "false") + ",\"prefab\":" + Json.Str(sp.AIPrefab != null ? sp.AIPrefab.name : "") + ",\"max\":" + sp.maxAINum +
                ",\"chance\":" + sp.chance.ToString("F2", ic) + ",\"time\":[" + sp.minTime.ToString("F0", ic) + "," + sp.maxTime.ToString("F0", ic) + "]" +
                ",\"count\":[" + sp.minCount.ToString("F0", ic) + "," + sp.maxCount.ToString("F0", ic) + "],\"dist\":[" + sp.minDist.ToString("F0", ic) + "," + sp.maxDist.ToString("F0", ic) + "]" +
                ",\"halfFov\":" + sp.halfFov.ToString("F0", ic) + ",\"radius\":" + sp.radius.ToString("F2", ic) + ",\"listed\":" + sp.AIs.Count + ",\"around\":" + Around(sp) + "}";
            return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"spawner\":" + s + ",\"starts\":" + Starts + ",\"stops\":" + Stops + ",\"spawned\":" + Spawned +
                   ",\"limited\":" + Limited + ",\"nearOthers\":" + NearOthers + "}";
        }
    }
}
