using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Sandstorms, back. The beta still has the storm (itemdatabase.storms[0], sandstormscript: drifts, grows near the
    /// player, darkens sky and fog, blows loose bodies about) and the spawner's code (sandStormSpawnScript), but no
    /// spawner in the world (stormprobe: mainscript.sandStormSpawn null, none loaded) - so no storm ever comes up, and the
    /// save loader skips the storms of a save (SetDataFromSave returns when there is no spawner).
    ///
    /// The game's spawner is put back (its own component: the save keeps its schedule and the storms, as the game wrote
    /// it). What changed since it was taken out: the world has biomes (GetBiomeInt: 0 sand, 1 snow, 2 the rest), and the
    /// storm is a sand biome's storm (prefab biome 0, lifeLossOnOtherBiome) - but the game's Move only fades it by
    /// distance (destroyDist 25 km). So:
    ///   - a storm comes up only on sand: SpawnDistM from a player, at a point of the storm's biome (a few tries; none
    ///     around anyone - no storm this time). It heads for that player (within AimDeg): a dust wall coming at them,
    ///     not one drifting off where nobody sees it (a random heading from 1.5 km passes the player one time in four).
    ///   - off its biome it fades (lifeLossOnOtherBiome: 10 s) and is gone; far from every player too (destroyDist).
    /// In a session the storms are the host's (Net.Entities.Storms): its spawner alone runs, around any player, and a
    /// storm fades by the player nearest to it.
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.Sandstorms.Enabled (off: the beta as it was - no spawner made)
    public static class Sandstorms
    {
        public static bool Enabled = true;
        public static float MinS = 15f * 60f, MaxS = 45f * 60f, SpawnDistM = 1500f, AimDeg = 25f, BiomeCheckS = 1f;
        public static long Spawners, Spawns, SpawnsNoBiome, FadedOffBiome;
        internal static readonly Dictionary<sandstormscript, float> FadeDist = new Dictionary<sandstormscript, float>();   // diagnostics: what Move measured
        private static readonly Dictionary<sandstormscript, float> _biomeAt = new Dictionary<sandstormscript, float>();
        private static readonly Dictionary<sandstormscript, bool> _offBiome = new Dictionary<sandstormscript, bool>();

        private static readonly AccessTools.FieldRef<sandstormscript, float> Dist = AccessTools.FieldRefAccess<sandstormscript, float>("currentDistance");

        /// The world's spawner: made once a world has its main script (before a save's data is set: it fills it in).
        internal static void Ensure()
        {
            if (!Enabled || mainscript.s == null || mainscript.s.sandStormSpawn != null || itemdatabase.s == null ||
                itemdatabase.s.storms == null || itemdatabase.s.storms.Length == 0 || itemdatabase.s.storms[0] == null) return;
            var sp = new GameObject("SandStormSpawn").AddComponent<sandStormSpawnScript>();
            sp.minTime = MinS; sp.maxTime = MaxS; sp.radius = SpawnDistM; sp.prefabid = 0;
            mainscript.s.sandStormSpawn = sp;
            Spawners++;
        }

        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SetDataFromSave))]
        private static class OnLoad
        {
            [HarmonyPrefix] private static void Prefix() => Ensure();

            /// A save written with no spawner (every beta save so far) holds no schedule (0, 0): the first storm would
            /// be due at once. It gets a fresh wait, as a new world does (sandStormSpawnScript.Start → UpdTime).
            [HarmonyPostfix]
            private static void Postfix()
            {
                var sp = mainscript.s != null ? mainscript.s.sandStormSpawn : null;
                if (sp != null && sp.nextTime <= 0f) { sp.lastTime = Time.time; sp.nextTime = Random.Range(sp.minTime, sp.maxTime); }
            }
        }

        internal static void Tick() { if (Enabled && mainscript.s != null && mainscript.s.sandStormSpawn == null && mainscript.s.player != null) Ensure(); }

        /// One spawner in a session: the host's.
        [HarmonyPatch(typeof(sandStormSpawnScript), "Update")]
        private static class SpawnerHostOnly
        {
            [HarmonyPrefix]
            private static bool Prefix() =>
                mainscript.s != null && mainscript.s.player != null && (!Net.Entities.InSession || Net.Entities.IsHost);
        }

        [HarmonyPatch(typeof(sandStormSpawnScript), nameof(sandStormSpawnScript.Spawn))]
        private static class SpawnOnSand
        {
            [HarmonyPrefix]
            private static bool Prefix(sandStormSpawnScript __instance)
            {
                if (!Enabled) return true;
                var prefab = itemdatabase.s.storms[__instance.prefabid].GetComponent<sandstormscript>();
                var around = new List<Vector3> { mainscript.s.player.transform.position };
                if (Net.Entities.InSession && Net.Entities.IsHost)
                    foreach (var g in Net.RemotePlayers.GlobalPositions()) around.Add(mainscript.UnityPosFromGlobal(g));
                for (int i = around.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); var t = around[i]; around[i] = around[j]; around[j] = t; }
                foreach (var c in around)
                    for (int k = 0; k < 8; k++)
                    {
                        float r = Random.Range(0f, Mathf.PI * 2f);
                        var p = c + new Vector3(Mathf.Cos(r), 0f, Mathf.Sin(r)) * __instance.radius;
                        if (BiomeAt(p) != prefab.biome) continue;
                        SpawnToward(__instance.prefabid, p, c);
                        return false;
                    }
                SpawnsNoBiome++;
                return false;
            }
        }

        /// A storm at p heading for `at` (within AimDeg); size and speed rolled as the storm's own FStart rolls them.
        internal static sandstormscript SpawnToward(int prefabid, Vector3 p, Vector3 at)
        {
            var go = Object.Instantiate(itemdatabase.s.storms[prefabid], p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var st = go.GetComponent<sandstormscript>();
            var d = at - p; d.y = 0f;
            float a = Mathf.Atan2(d.z, d.x) + Random.Range(-AimDeg, AimDeg) * Mathf.Deg2Rad;
            st.started = true;   // Start: FStart only when not started
            st.size = Random.Range(0.5f, 1.5f); st.speed = Random.Range(3f, 12f);
            st.xdir = Mathf.Cos(a); st.ydir = Mathf.Sin(a);
            Spawns++;
            return st;
        }

        internal static int BiomeAt(Vector3 upos) => terrainGenSettings.GetFirstBiomeInt(mainscript.GlobalFromUnityPos(upos));

        /// The game's Move (drift; life up near the player, down far from them) with the fade by biome, and in a session
        /// by the player nearest the storm. A client's storm only drifts: its strength and its end are the host's.
        [HarmonyPatch(typeof(sandstormscript), "Move")]
        private static class Move
        {
            [HarmonyPrefix]
            private static bool Prefix(sandstormscript __instance)
            {
                if (!Enabled && !Net.Entities.InSession) return true;
                var st = __instance; var t = st.transform; float dt = Time.deltaTime;
                t.position = new Vector3(t.position.x + st.xdir * st.speed * dt, (float)mainscript.s.visszarakva.y, t.position.z + st.ydir * st.speed * dt);
                if (Net.Entities.InSession && !Net.Entities.IsHost) return false;
                float d = Dist(st);   // to the local player, last frame (Update measures after Move, as in the game)
                if (Net.Entities.InSession)
                {
                    var g = mainscript.GlobalFromUnityPos(t.position);
                    foreach (var p in Net.RemotePlayers.GlobalPositions())
                    {
                        double dx = p.x - g.x, dz = p.z - g.z;
                        d = Mathf.Min(d, (float)System.Math.Sqrt(dx * dx + dz * dz));
                    }
                }
                FadeDist[st] = d;
                bool off = false;
                if (Enabled)
                {
                    if (!_biomeAt.TryGetValue(st, out float at) || Time.time - at >= BiomeCheckS)
                    {
                        _biomeAt[st] = Time.time;
                        _offBiome[st] = BiomeAt(t.position) != st.biome;
                    }
                    off = _offBiome[st];
                }
                if (d >= st.destroyDist || off)
                {
                    st.life = Mathf.Clamp(st.life - dt * st.lifeLossOnOtherBiome, 0f, 1f);
                    if (st.life <= 0f)
                    {
                        if (off) FadedOffBiome++;
                        Forget(st);
                        Object.Destroy(st.gameObject);
                    }
                }
                else st.life = Mathf.Clamp(st.life + dt * st.lifeGain, 0f, 1f);
                return false;
            }
        }

        private static void Forget(sandstormscript st) { FadeDist.Remove(st); _biomeAt.Remove(st); _offBiome.Remove(st); }

        internal static void Prune()
        {
            var gone = new List<sandstormscript>();
            foreach (var k in _biomeAt.Keys) if (k == null) gone.Add(k);
            foreach (var k in FadeDist.Keys) if (k == null) gone.Add(k);
            foreach (var k in gone) Forget(k);
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"spawners\":" + Spawners + ",\"spawns\":" + Spawns + ",\"noSand\":" + SpawnsNoBiome +
            ",\"fadedOffBiome\":" + FadedOffBiome + "}";
    }
}
