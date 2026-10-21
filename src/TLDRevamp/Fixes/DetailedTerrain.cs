using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Issue #37: the detailed, road-aware terrain around the player reaches further.
    ///
    /// The game's detailed terrain (the near `terrainGenScript`, 100 m tiles with colliders) only reaches about 300 m,
    /// whatever the draw-distance slider says (it lerps 300 -> 320 m). Beyond that the player sees the coarse distant
    /// terrain, which is made by a different noise: roads that the game places further out float over it or run into
    /// it, and the detailed tiles visibly pop in a few hundred metres ahead.
    ///
    /// The game's ring is also off-centre: FStart builds its sample grid from -(n+1) to (n-1) tiles, so when the
    /// distance is a whole number of tiles (300 m, the slider's lowest setting) the detailed terrain stops a whole tile
    /// earlier towards +x/+z than towards -x/-z (about 250 m against 350 m).
    ///
    /// This adds a second, centred ring of the game's own tiles around the LOCAL player only, from where the game's
    /// ring ends out to DistanceM, placed after every player's own ring so nobody's ground under their feet waits for
    /// it. The tiles are the game's: same generator, same mesh, same collider and material, and the game removes them
    /// the same way when they fall out of range. Around it:
    ///  - the near cameras draw as far as the ring reaches (the game pins them at 500 m),
    ///  - roads are placed as far as the ring reaches, so the road sits in the terrain that is now drawn there,
    ///  - the distant terrain's push-down (its shader sinks it near the player so it stays under the detailed
    ///    terrain) starts and ends further out by the same amount.
    ///
    /// DistanceM = 0 is the game's own terrain, byte for byte: no patch changes anything then.
    [HarmonyPatch]
    public static class DetailedTerrain
    {
        /// How far the detailed terrain reaches around the local player, in metres. 0 = the game's own (~300 m).
        public static float DistanceM = 750f;
        /// The settings menu's choices, in order.
        internal static readonly float[] Choices = { 0f, 500f, 750f, 1000f };
        /// The near cameras draw this much past the ring's edge, so the last tiles are not cut at their middle.
        internal const float CameraMarginM = 60f;

        public static long PlaceChecks, RingCoords, RoadPlaceRaised, CameraRaised, PushDownMoved, Errors;
        public static string LastError = "";

        private sealed class Ring
        {
            public float GameDistance, Target, TileSize;
            public readonly List<Vector3> Extra = new List<Vector3>();
        }

        private static readonly ConditionalWeakTable<terrainGenScript, Ring> Rings = new ConditionalWeakTable<terrainGenScript, Ring>();

        private sealed class PushDown { public float Near, Far; }
        private static readonly Dictionary<Material, PushDown> PushDowns = new Dictionary<Material, PushDown>();

        private static readonly AccessTools.FieldRef<terrainGenScript, List<Vector3d>> CoordsNeeded =
            AccessTools.FieldRefAccess<terrainGenScript, List<Vector3d>>("coordsNeeded");

        internal static bool On => DistanceM > 0f;

        /// The game's detailed generator: not the distant one.
        internal static bool IsDetailed(terrainGenScript gen) => gen != null && !gen.isDistantTerrain;

        /// How far the detailed terrain of this generator reaches now: the game's own distance or the ring's.
        internal static float EffectiveDistance(terrainGenScript gen)
        {
            if (gen == null) return 0f;
            return On && IsDetailed(gen) ? Math.Max(gen.drawDistance, DistanceM) : gen.drawDistance;
        }

        // ------------------------------------------------------------------ the ring

        /// The ring's tile offsets as world vectors, nearest first (DetailedTerrainRing has the maths).
        internal static void ExtraCoords(float tileSize, float target, List<Vector3> gameSamples, List<Vector3> extra)
        {
            extra.Clear();
            var game = new List<DetailedTerrainRing.Tile>();
            if (gameSamples != null && tileSize > 0f)
                foreach (var s in gameSamples) game.Add(new DetailedTerrainRing.Tile(Mathf.RoundToInt(s.x / tileSize), Mathf.RoundToInt(s.z / tileSize)));
            foreach (var t in DetailedTerrainRing.Extra(tileSize, target, game))
                extra.Add(new Vector3(t.X, 0f, t.Z) * tileSize);
        }

        private static Ring RingOf(terrainGenScript gen)
        {
            var ring = Rings.GetValue(gen, _ => new Ring());
            if (ring.GameDistance != gen.drawDistance || ring.Target != DistanceM || ring.TileSize != gen.tileSize)
            {
                ring.GameDistance = gen.drawDistance;
                ring.Target = DistanceM;
                ring.TileSize = gen.tileSize;
                if (DistanceM > gen.drawDistance) ExtraCoords(gen.tileSize, DistanceM, gen.sampleCoords, ring.Extra);
                else ring.Extra.Clear();
            }
            return ring;
        }

        /// The game's CheckPlace, plus the ring around the local player after every player's own ring.
        [HarmonyPatch(typeof(terrainGenScript), "CheckPlace")]
        [HarmonyPrefix]
        private static bool CheckPlace(terrainGenScript __instance)
        {
            var gen = __instance;
            // The game's own path when off, for the distant generators, and for the game's threaded mode (its
            // CheckPlace then runs on a worker, where the player's transform cannot be read).
            if (!On || !IsDetailed(gen) || gen.mapSetting == null || gen.mapSetting.useAsync2 || gen.sampleCoords == null) return true;
            try
            {
                var ring = RingOf(gen);
                if (ring.Extra.Count == 0) return true;
                var needed = CoordsNeeded(gen);
                var arounds = gen.mapSetting.genArounds;
                needed.Clear();
                int local = -1;
                var player = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform : null;
                for (int i = 0; i < arounds.Count; i++)
                {
                    var ga = arounds[i];
                    var currentp = mainscript.Snap(mainscript.GlobalFromUnityPos(ga.upos), gen.tileSize);
                    currentp.y = 0.0;
                    foreach (var s in gen.sampleCoords) needed.Add(currentp + s);
                    if (local < 0 && player != null && ga.t != null && (ga.t == player || ga.t.IsChildOf(player))) local = i;
                }
                if (local < 0 && arounds.Count > 0) local = 0;   // the game adds the local player (its BodyRot) first
                if (local >= 0)
                {
                    var centre = mainscript.Snap(mainscript.GlobalFromUnityPos(arounds[local].upos), gen.tileSize);
                    centre.y = 0.0;
                    foreach (var s in ring.Extra) needed.Add(centre + s);
                    RingCoords += ring.Extra.Count;
                }
                PlaceChecks++;
                return false;
            }
            catch (Exception e)
            {
                Fail("CheckPlace", e);
                return true;
            }
        }

        // ------------------------------------------------------------------ roads, cameras, distant push-down

        /// Roads are placed as far as the detailed terrain reaches, so they lie in it instead of ending short of it.
        // The game has UpdChunksNeeded() and UpdChunksNeeded(Vector3d): name the one without arguments, the one that
        // sets roadPlaceDist (an unnamed overload fails with "Ambiguous match" and the whole class is dead).
        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdChunksNeeded), new Type[0])]
        [HarmonyPostfix]
        private static void AfterUpdChunksNeeded(roadGenScript __instance)
        {
            if (!On || __instance == null) return;
            if (__instance.roadPlaceDist < DistanceM) { __instance.roadPlaceDist = DistanceM; RoadPlaceRaised++; }
        }

        /// The game pins both near cameras at 500 m whenever the settings are applied.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SettingsRefreshed))]
        [HarmonyPostfix]
        private static void AfterSettingsRefreshed() => ApplyCameras();

        internal static void ApplyCameras()
        {
            try
            {
                var p = mainscript.s != null ? mainscript.s.player : null;
                if (p == null) return;
                float far = 500f;   // the game's own (mainscript.SettingsRefreshed)
                if (On) far = Math.Max(far, DistanceM + CameraMarginM);
                bool raised = false;
                if (p.Cam != null && p.Cam.farClipPlane != far) { p.Cam.farClipPlane = far; raised = true; }
                if (p.mainCam != null && p.mainCam.farClipPlane != far) { p.mainCam.farClipPlane = far; raised = true; }
                if (raised) CameraRaised++;
            }
            catch (Exception e) { Fail("cameras", e); }
        }

        /// The distant terrain sinks under the detailed terrain near the player (its material's _NearDist/_FarDist,
        /// set by FStart). The sinking has to follow the detailed terrain's new edge, or the coarse hills would show
        /// through where the ring now is.
        [HarmonyPatch(typeof(terrainGenScript), nameof(terrainGenScript.FStart))]
        [HarmonyPostfix]
        private static void AfterFStart(terrainGenScript __instance)
        {
            var gen = __instance;
            if (gen == null) return;
            try
            {
                if (gen.isDistantTerrain && gen.material != null)
                {
                    // FStart just wrote the game's values: they are the base the shift is added to.
                    PushDowns[gen.material] = new PushDown { Near = gen.material.GetFloat("_NearDist"), Far = gen.material.GetFloat("_FarDist") };
                    ApplyPushDown(gen.material);
                }
            }
            catch (Exception e) { Fail("FStart", e); }
        }

        /// How much further out the detailed terrain now ends than the game's own.
        internal static float Shift()
        {
            if (!On) return 0f;
            float game = GameDetailedDistance();
            return game > 0f && DistanceM > game ? DistanceM - game : 0f;
        }

        /// The game's own detailed distance, from the detailed generator (whichever order the generators started in).
        private static float GameDetailedDistance()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.terrainGen == null) return 0f;
            foreach (var g in map.terrainGen)
            {
                if (!IsDetailed(g)) continue;
                if (g.drawDistance > 0f) return g.drawDistance;
                float lerp = settingsscript.s != null ? settingsscript.s.S.GetDrawDistanceLerp() : 0f;
                return Mathf.Round(Mathf.Lerp(g.minDrawDist.drawDistance, g.maxDrawDist.drawDistance, lerp));
            }
            return 0f;
        }

        private static void ApplyPushDown(Material m)
        {
            if (m == null || !PushDowns.TryGetValue(m, out var b)) return;
            float shift = Shift();
            m.SetFloat("_NearDist", b.Near + shift);
            m.SetFloat("_FarDist", b.Far + shift);
            if (shift > 0f) PushDownMoved++;
        }

        // ------------------------------------------------------------------ the setting

        /// The settings menu changed the distance: the ring, the cameras, the roads and the push-down follow at once.
        /// No tile is rebuilt: the next CheckPlace asks for the new ring and CheckRemove drops what fell out of it.
        internal static void Apply()
        {
            ApplyCameras();
            var dead = new List<Material>();
            foreach (var m in PushDowns.Keys) if (m == null) dead.Add(m); else ApplyPushDown(m);
            foreach (var m in dead) PushDowns.Remove(m);
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.roadGens != null)
                foreach (var rg in map.roadGens)
                    if (rg != null) { try { rg.UpdChunksNeeded(); } catch (Exception e) { Fail("roads", e); } }
        }

        private static void Fail(string where, Exception e)
        {
            Errors++;
            LastError = where + ": " + e.GetType().Name + ": " + e.Message;
            if (Errors <= 5) Plugin.Log.LogWarning("detailed terrain " + LastError);
        }

        internal static string Stats()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int extra = 0; float game = 0f, tile = 0f;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.terrainGen != null)
                foreach (var g in map.terrainGen)
                    if (IsDetailed(g) && Rings.TryGetValue(g, out var r)) { extra = r.Extra.Count; game = r.GameDistance; tile = r.TileSize; }
            return "{\"distanceM\":" + DistanceM.ToString(ci) + ",\"gameDistanceM\":" + game.ToString(ci) + ",\"tileSize\":" + tile.ToString(ci) +
                   ",\"ringTiles\":" + extra + ",\"shiftM\":" + Shift().ToString(ci) + ",\"placeChecks\":" + PlaceChecks +
                   ",\"ringCoords\":" + RingCoords + ",\"roadPlaceRaised\":" + RoadPlaceRaised + ",\"cameraRaised\":" + CameraRaised +
                   ",\"pushDownMoved\":" + PushDownMoved + ",\"errors\":" + Errors + ",\"lastError\":" + Json.Str(LastError) + "}";
        }
    }
}
