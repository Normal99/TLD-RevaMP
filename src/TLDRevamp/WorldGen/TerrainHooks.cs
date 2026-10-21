using System;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.WorldGen
{
    /// The hooks that put the chosen terrain mode into the game's world generation, keep it in the save and take
    /// the host's mode when joining (issue #32).
    ///
    /// The order of a world start in the game:
    ///
    ///   menuhandler.LoadMap(mapClass)   - picks the map prefab, instantiates it, and calls
    ///                                      mapSettingScript.FStart(seed, newGame) at the end.
    ///   mapSettingScript.FStart          - sets the seed, then RestartMap(), which starts every generator
    ///                                      (terrainGen, roadGen, poiGen, objGen) in turn.
    ///
    /// The road generator's flags have to be right before roadGenScript.FStart, so the mode is applied in a
    /// prefix of mapSettingScript.FStart: the map instance exists by then, and the generators have not run yet.
    ///
    /// The mode a world gets, in order of precedence:
    ///   1. the host's mode, when we are joining somebody's world (the host owns the world we play in),
    ///   2. the mode in the save's map tag, when we are loading a save,
    ///   3. the mode the player picked for a new game.
    [HarmonyPatch]
    public static class TerrainHooks
    {
        /// The mode the host told us, while we play in the host's world. -1 = none yet.
        private static int _hostMode = -1;

        /// The mode the running world was started with, remembered so the save can be written with the same tag.
        private static TerrainMode _runningMode = TerrainModes.Default;

        /// The map the game is running (or about to run). The game's own menu holds it.
        public static mapSettingScript CurrentMap =>
            menuhandler.s != null ? menuhandler.s.currentMainMap : null;

        /// Set the mode before the world starts (the new-game selector, the settings screen).
        public static void Select(TerrainMode mode)
        {
            if (!TerrainModes.IsSelectable(mode)) return;   // OldTerrain: disabled
            TerrainChoice.Selected = mode;
        }

        /// The host's mode, from the Welcome message. Applied before the world loads.
        internal static void TakeHost(TerrainMode mode)
        {
            if (!TerrainModes.IsSelectable(mode)) return;
            _hostMode = (int)mode;
            TerrainChoice.HostTaken++;
            Plugin.Log.LogInfo("terrain: the host's mode — " + TerrainModes.Name(mode));
        }

        /// Leaving the host's world: our own mode again.
        internal static void ClearHost()
        {
            _hostMode = -1;
        }

        /// The mode the next world should be generated with, and where it comes from.
        internal static TerrainMode ModeForThisWorld(out string source)
        {
            // The host's mode, when we are playing in somebody else's world.
            if (_hostMode >= 0 && TerrainModes.IsSelectable((TerrainMode)_hostMode))
            {
                source = TerrainSelect.SourceHost;
                return (TerrainMode)_hostMode;
            }
            // A save carries its mode in the map name tag.
            string saveMapName = null;
            if (DataFromMenuScript.s != null && DataFromMenuScript.s.load && savedatascript.s != null && savedatascript.s.data != null)
            {
                var props = savedatascript.s.data.properties;
                if (props != null)
                {
                    // saveproperties.map is a struct (gamemodesettingsscript.mapClass), not a reference
                    saveMapName = props.map.map.mapName;
                }
            }
            return TerrainSelect.Choose(-1, saveMapName, TerrainChoice.Selected, out source);
        }

        // ------------------------------------------------------------------ world start

        /// The map starts: apply the mode to the map's road generators before they run.
        [HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.FStart))]
        [HarmonyPrefix]
        private static void BeforeMapStart(mapSettingScript __instance)
        {
            try
            {
                TerrainMode mode = ModeForThisWorld(out string source);
                if (!TerrainModes.IsSelectable(mode)) mode = TerrainModes.Default;
                bool changed = TerrainChoice.ApplyTo(__instance, mode);
                _runningMode = mode;
                TerrainChoice.Active = mode;
                TerrainChoice.ActiveSource = source;
                if (changed) TerrainChoice.Applied++;
                Plugin.Log.LogInfo($"terrain: {TerrainModes.Name(mode)} ({source})" +
                    (changed ? "" : " (already set)"));
            }
            catch (Exception e)
            {
                TerrainChoice.Errors++;
                Plugin.Log.LogError("terrain: could not apply the mode: " + e);
            }
        }

        // ------------------------------------------------------------------ save and load

        /// The save is written: put the mode into the map name, so the save keeps its terrain.
        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.RefreshProperties))]
        [HarmonyPostfix]
        private static void AfterRefreshProperties(saveproperties _properties)
        {
            try
            {
                if (_properties == null) return;
                TerrainMode mode = _runningMode;
                string name = _properties.map.map.mapName;   // mapSaveClass is a struct
                if (string.IsNullOrEmpty(name)) return;
                string tagged = TerrainChoice.TaggedMapName(name, mode);
                if (tagged == name) return;
                _properties.map.map.mapName = tagged;
                TerrainChoice.Tagged++;
            }
            catch (Exception e)
            {
                TerrainChoice.Errors++;
                Plugin.Log.LogError("terrain: could not tag the save: " + e);
            }
        }

        /// A save is loaded: its map name carries the mode, and the game must not be confused by the tag.
        /// The tag is stripped from the name the player sees and from the map's own record, so the game keeps
        /// showing the plain map name. The mode itself is read by ModeForThisWorld.
        // The overload that takes the name: the game has UpdMapName(string) and UpdMapName(), and a patch that names
        // neither fails at load with "Ambiguous match" (the whole patch class is then dead).
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.UpdMapName), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void AfterUpdMapName(menuhandler __instance, string _mapName)
        {
            try
            {
                if (string.IsNullOrEmpty(_mapName)) return;
                if (!TerrainModes.HasTag(_mapName)) return;
                string plain = TerrainModes.StripTag(_mapName);
                if (plain == _mapName) return;
                if (__instance != null)
                {
                    __instance.selectedMapName = plain;
                    // UpdMapName(string) already wrote the label from the tagged name: write it again from the plain one.
                    __instance.UpdMapName();
                }
                // The map instance keeps the name the save had (with the tag): clean it there too, so the world
                // never carries our tag into the game's own records.
                var map = __instance != null ? __instance.currentMainMap : null;
                if (map != null && map.mapClass.map.mapName == _mapName) map.mapClass.map.mapName = plain;
                TerrainChoice.SaveTaken++;
            }
            catch (Exception e)
            {
                TerrainChoice.Errors++;
                Plugin.Log.LogError("terrain: could not read the save's map tag: " + e);
            }
        }

        /// The map name shown in the menu must not carry our tag: strip it wherever the game asks for it.
        ///
        /// The game looks the map up by name (MapIndexFromName) and falls back to the first map when the name is
        /// not in its list. A tagged name is not in that list, so the tag has to be gone before the lookup. The
        /// lookup is done again here with the plain name, so the right map is loaded even if the game's own
        /// lookup did not find it.
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.MapIndexFromName))]
        [HarmonyPostfix]
        private static void AfterMapIndexFromName(menuhandler __instance, menuhandler.mapSaveClass _map, ref int __result)
        {
            try
            {
                if (_map.mapName == null || !TerrainModes.HasTag(_map.mapName)) return;
                if (__result >= 0) return;   // the game found it anyway
                string plain = TerrainModes.StripTag(_map.mapName);
                int found = __instance.MapIndexFromName(new menuhandler.mapSaveClass
                {
                    tip = _map.tip,
                    defmapID = _map.defmapID,
                    mapName = plain,
                });
                if (found >= 0) __result = found;
            }
            catch (Exception e)
            {
                TerrainChoice.Errors++;
                Plugin.Log.LogWarning("terrain: the map lookup with the tag removed failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ the world reload

        /// The world is torn down (a new world, a new session): forget the running mode, so the next world is
        /// chosen from scratch.
        ///
        /// The host's mode is NOT forgotten here: the client takes the host's mode from Welcome and then calls
        /// menuhandler.PressedStart, which calls ResetStuff. Clearing the host's mode here would throw the host's
        /// choice away exactly when it is about to be used. The host's mode is cleared when the client leaves the
        /// host's world instead (TerrainHooks.ClearHost, called from Mp.Stop).
        [HarmonyPatch(typeof(DataFromMenuScript), nameof(DataFromMenuScript.ResetStuff))]
        [HarmonyPostfix]
        private static void AfterResetStuff()
        {
            _runningMode = TerrainModes.Default;
            TerrainChoice.Active = TerrainModes.Default;
            TerrainChoice.ActiveSource = "default";
        }
    }
}
