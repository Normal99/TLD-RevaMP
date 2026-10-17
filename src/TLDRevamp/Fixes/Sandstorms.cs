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
    ///
    /// How often (0.68.4, playtest: "I see them at every other building and they almost always arrive there"): every
    /// storm used to head for a player and lived until it left the sand or was 25 km from everyone, one every 15-45 min.
    /// A storm aimed within 25 deg from 1.5 km always reaches the player (it passes at most 630 m off; a storm's reach is
    /// its radius), and a parked player sees it arrive a few minutes later; the ones that passed drifted on for an hour
    /// over a big desert. Now (Settings > Revamp > World > Sandstorms, the host's in a session):
    ///   - the wait between storms is the setting's (Normal 30-75 min; Rare 60-150; Often 15-45; Off: none come up);
    ///   - only AimChance of them come at the player (within AimDeg); the others head at least MissDeg past them: seen
    ///     going by on the horizon, not arriving;
    ///   - a storm dies down after LifeMinS-LifeMaxS (over DieDownS), wherever it is.
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.Sandstorms.Enabled (off: the beta as it was - no spawner made);
    /// MinS/MaxS (the live spawner follows), AimChance (1 = every storm aimed, as before 0.68.4), AgeOut (false: no
    /// dying down, as before).
    public static class Sandstorms
    {
        public static bool Enabled = true;
        public static float MinS = 30f * 60f, MaxS = 75f * 60f, SpawnDistM = 1500f, AimDeg = 25f, BiomeCheckS = 1f;
        public static float AimChance = 0.4f, MissDeg = 60f;
        public static bool AgeOut = true;
        public static float LifeMinS = 15f * 60f, LifeMaxS = 30f * 60f, DieDownS = 60f;
        public static long Spawners, Spawns, SpawnsNoBiome, FadedOffBiome, Aimed, Passing, SpawnsOff, AgedOut;

        /// Settings > Revamp > World > Sandstorms: 0 off, 1 rare, 2 normal, 3 often (wait min, max in minutes; aim chance)
        public static int Frequency = 2;
        private static readonly float[,] Presets = { { 0f, 0f, 0f }, { 60f, 150f, 0.25f }, { 30f, 75f, 0.4f }, { 15f, 45f, 0.7f } };
        public static void SetFrequency(int v)
        {
            Frequency = Mathf.Clamp(v, 0, 3);
            if (Frequency > 0) { MinS = Presets[Frequency, 0] * 60f; MaxS = Presets[Frequency, 1] * 60f; AimChance = Presets[Frequency, 2]; }
        }
        internal static readonly Dictionary<sandstormscript, float> FadeDist = new Dictionary<sandstormscript, float>();   // diagnostics: what Move measured
        private static readonly Dictionary<sandstormscript, float> _biomeAt = new Dictionary<sandstormscript, float>();
        private static readonly Dictionary<sandstormscript, bool> _offBiome = new Dictionary<sandstormscript, bool>();
        private static readonly Dictionary<sandstormscript, float> _bornAt = new Dictionary<sandstormscript, float>(), _lifeS = new Dictionary<sandstormscript, float>();

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

        internal static void Tick()
        {
            if (!Enabled || mainscript.s == null) return;
            var sp = mainscript.s.sandStormSpawn;
            if (sp == null) { if (mainscript.s.player != null) Ensure(); return; }
            if (Time.frameCount % 600 == 0) Prune();
            // the wait changed (the setting, or the bridge): the next storm by the new one, from now
            if (sp.minTime != MinS || sp.maxTime != MaxS) { sp.minTime = MinS; sp.maxTime = MaxS; sp.lastTime = Time.time; sp.nextTime = Random.Range(MinS, MaxS); }
        }

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
                if (Frequency == 0) { SpawnsOff++; return false; }
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

        /// A storm at p: AimChance of the time heading for `at` (within AimDeg), else at least MissDeg past it (going by,
        /// not arriving); size and speed rolled as the storm's own FStart rolls them.
        internal static sandstormscript SpawnToward(int prefabid, Vector3 p, Vector3 at)
        {
            var go = Object.Instantiate(itemdatabase.s.storms[prefabid], p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var st = go.GetComponent<sandstormscript>();
            var d = at - p; d.y = 0f;
            bool aim = Random.value < AimChance;
            float off = aim ? Random.Range(-AimDeg, AimDeg) : (Random.value < 0.5f ? -1f : 1f) * Random.Range(MissDeg, 180f);
            if (aim) Aimed++; else Passing++;
            float a = Mathf.Atan2(d.z, d.x) + off * Mathf.Deg2Rad;
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
                bool old = false;
                if (Enabled && AgeOut)
                {
                    // its time is up: it dies down (a loaded save's storms start a new span)
                    if (!_bornAt.TryGetValue(st, out float born)) { _bornAt[st] = Time.time; _lifeS[st] = Random.Range(LifeMinS, LifeMaxS); }
                    else old = Time.time - born >= _lifeS[st];
                }
                if (d >= st.destroyDist || off || old)
                {
                    float loss = d >= st.destroyDist || off ? st.lifeLossOnOtherBiome : 0f;
                    if (old) loss = Mathf.Max(loss, 1f / Mathf.Max(DieDownS, 0.1f));
                    st.life = Mathf.Clamp(st.life - dt * loss, 0f, 1f);
                    if (st.life <= 0f)
                    {
                        if (off) FadedOffBiome++; else if (old) AgedOut++;
                        Forget(st);
                        Object.Destroy(st.gameObject);
                    }
                }
                else st.life = Mathf.Clamp(st.life + dt * st.lifeGain, 0f, 1f);
                return false;
            }
        }

        private static void Forget(sandstormscript st) { FadeDist.Remove(st); _biomeAt.Remove(st); _offBiome.Remove(st); _bornAt.Remove(st); _lifeS.Remove(st); }

        internal static void Prune()
        {
            var gone = new List<sandstormscript>();
            foreach (var k in _biomeAt.Keys) if (k == null) gone.Add(k);
            foreach (var k in FadeDist.Keys) if (k == null) gone.Add(k);
            foreach (var k in _bornAt.Keys) if (k == null) gone.Add(k);
            foreach (var k in gone) Forget(k);
        }

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"spawners\":" + Spawners + ",\"spawns\":" + Spawns + ",\"noSand\":" + SpawnsNoBiome +
            ",\"fadedOffBiome\":" + FadedOffBiome + ",\"frequency\":" + Frequency + ",\"aimed\":" + Aimed + ",\"passing\":" + Passing +
            ",\"off\":" + SpawnsOff + ",\"agedOut\":" + AgedOut + ",\"ages\":[" + Ages() + "]}";

        /// seconds since each storm came up (as Move counts it) and its span, for the tests
        private static string Ages()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture; var rows = new List<string>();
            foreach (var kv in _bornAt) if (kv.Key != null) rows.Add("[" + (Time.time - kv.Value).ToString("F0", ic) + "," + _lifeS[kv.Key].ToString("F0", ic) + "]");
            return string.Join(",", rows);
        }
    }
}
