using System;

namespace TLDRevamp.WorldGen
{
    /// The terrain shapes a new game can be generated in (issue #32).
    ///
    /// Crossroads and MainRoadOnly are the game's own road generator driven by the two flags it already has
    /// (roadGenScript.hasMainRoad / hasSideRoad); MainRoadOnly also needs the fix in MainRoadOnlyRoads, because the
    /// game's own version of it breaks the road at every chunk. OldTerrain is the landscape, road and roadside buildings
    /// of the public game v2023.05.02d, ported from AnonymousTLD's TerrainChoice (GPL-3.0, WorldGen/Old2023). Every mode
    /// is a pure function of the seed, so every player with the same seed and mode gets the same world.
    ///
    ///   Crossroads    - the game as shipped: a main road down the middle (chunk x = 0) and side roads crossing it.
    ///   MainRoadOnly  - only the main road: no side roads, so the world is a single ribbon of road.
    ///   OldTerrain    - the 2023 world: its hills, its one road and its roadside buildings, drawn with today's models.
    public enum TerrainMode : byte
    {
        Crossroads = 0,
        MainRoadOnly = 1,
        OldTerrain = 2,
    }

    /// Everything about the terrain mode that is pure logic: the enum, the map-name tag, the text and the rules
    /// that decide which mode a world, a save or a host asks for. No Unity types here, so it can be unit tested
    /// outside the game (tools/unit/TerrainModeTest.cs).
    public static class TerrainModes
    {
        /// The tag appended to a map name to remember the mode, e.g. "Dust Belt [Crossroads]".
        /// A suffix the game itself never writes, so the game's map list and the save screen still show the plain name.
        public const string TagOpen = "[";
        public const string TagSeparator = " [";
        public const string TagSuffix = "]";

        /// The mode a world gets when nothing says otherwise: the game as shipped.
        public const TerrainMode Default = TerrainMode.Crossroads;

        public static readonly TerrainMode[] All = { TerrainMode.Crossroads, TerrainMode.MainRoadOnly, TerrainMode.OldTerrain };

        public static string Name(TerrainMode m)
        {
            switch (m)
            {
                case TerrainMode.MainRoadOnly: return "MainRoadOnly";
                case TerrainMode.OldTerrain: return "OldTerrain";
                default: return "Crossroads";
            }
        }

        /// The short name used in the map tag, without the leading space: "[Crossroads]".
        public static string Tag(TerrainMode m) => TagOpen + Name(m) + TagSuffix;

        /// The tag as it is appended to a map name: "Dust Belt [Crossroads]".
        public static string TagSuffixOf(TerrainMode m) => TagSeparator + Name(m) + TagSuffix;

        /// The mode's own text for the settings screen and the diagnostics.
        public static string Describe(TerrainMode m)
        {
            switch (m)
            {
                case TerrainMode.MainRoadOnly:
                    return "Main road only: one road down the middle of the world, no side roads.";
                case TerrainMode.OldTerrain:
                    return "Old terrain (2023): the 2023 landscape, its one road and its roadside buildings.";
                default:
                    return "Crossroads: the game's own world - a main road with side roads crossing it.";
            }
        }

        /// The modes a player may pick, in the order the new-game screen lists them.
        public static TerrainMode[] Selectable() => new[] { TerrainMode.Crossroads, TerrainMode.MainRoadOnly, TerrainMode.OldTerrain };

        /// Is this mode implemented and offered?
        public static bool IsSelectable(TerrainMode m) =>
            m == TerrainMode.Crossroads || m == TerrainMode.MainRoadOnly || m == TerrainMode.OldTerrain;

        /// The mode's name on the new-game screen.
        public static string Label(TerrainMode m)
        {
            switch (m)
            {
                case TerrainMode.MainRoadOnly: return "Main road only";
                case TerrainMode.OldTerrain: return "Old terrain (2023)";
                default: return "Crossroads";
            }
        }

        // ------------------------------------------------------------------ map name tag

        /// Does this map name carry a terrain tag? Only a tag that names a mode we know counts; a map that happens
        /// to be called "Dust [Belt]" has no tag.
        public static bool HasTag(string mapName)
        {
            return TrySplitAnyTag(mapName, out _, out _);
        }

        /// The mode a tagged map name asks for, or Default when the name carries no tag or a tag we do not offer.
        ///
        /// Note that Crossroads is the default, so "no tag" and "the Crossroads tag" both answer Crossroads. A
        /// caller that has to tell them apart uses TrySplitAnyTag, which says whether there was a tag at all.
        /// Both the current tag and the legacy "[Terrain: <mode>]" tag are read, so a save written by either
        /// build is found and the game still looks its map up by the plain name.
        public static TerrainMode FromMapName(string mapName)
        {
            if (!TrySplitAnyTag(mapName, out string plain, out TerrainMode mode)) return Default;
            return IsSelectable(mode) ? mode : Default;
        }

        /// The map name without its terrain tag: what the game and the save screen show.
        public static string StripTag(string mapName)
        {
            if (!TrySplitAnyTag(mapName, out string plain, out _)) return mapName ?? "";
            return plain;
        }

        /// The map name with a terrain tag: what is written into the save and sent to the host.
        public static string WithTag(string mapName, TerrainMode mode)
        {
            string plain = StripTag(mapName);
            string tag = TagSuffixOf(mode);
            return plain.Length == 0 ? Tag(mode) : plain + tag;
        }

        /// Split a map name into its plain part and the mode its tag asks for.
        ///
        /// The tag is found at the end of the name, whatever the name in front of it is: "Dust Belt [MainRoadOnly]"
        /// and "[MainRoadOnly]" are both a tag. The word between the brackets has to be a mode we know, so a map
        /// that happens to be called "Dust [Belt]" keeps its brackets and stays the same map.
        ///
        /// The mode that comes back is the one the tag names, even when we do not offer it: the tag is still a tag,
        /// and the name still has to be cleaned of it. Callers that want a mode to act on use FromMapName, which
        /// falls back to the default for a mode we do not offer.
        public static bool TrySplitTag(string mapName, out string plain, out TerrainMode mode)
        {
            plain = mapName ?? "";
            mode = Default;
            if (string.IsNullOrEmpty(mapName)) return false;
            int end = mapName.LastIndexOf(TagSuffix, StringComparison.Ordinal);
            if (end < 0) return false;
            int open = mapName.LastIndexOf(TagOpen, end, StringComparison.Ordinal);
            if (open < 0) return false;
            string word = mapName.Substring(open + 1, end - open - 1);
            if (!IsTagWord(word)) return false;
            TerrainMode tagged;
            if (!TryParse(word, out tagged)) return false;      // a tag we do not know: ignore the whole suffix
            plain = mapName.Substring(0, open).TrimEnd();
            mode = tagged;                                    // the caller decides whether to honour it
            return true;
        }

        /// The other tag format that has been written into save names: "[Terrain: <mode>]".
        ///
        /// A save's map name is persisted, so a build that writes one format and a build that reads only the
        /// other would not find the save at all: the player silently starts a new world. The reader therefore
        /// accepts both formats, so a save written by either build still loads. Only the mode name inside the
        /// brackets is the same in both, so the tag is recognised by its "Terrain: " prefix.
        public const string LegacyTagPrefix = "Terrain: ";

        /// The mode a legacy "[Terrain: <mode>]" tag names, if the name ends in one.
        public static bool TrySplitLegacyTag(string mapName, out string plain, out TerrainMode mode)
        {
            plain = mapName ?? "";
            mode = Default;
            if (string.IsNullOrEmpty(mapName)) return false;
            int end = mapName.LastIndexOf(TagSuffix, StringComparison.Ordinal);
            if (end < 0) return false;
            int open = mapName.LastIndexOf(TagOpen, end, StringComparison.Ordinal);
            if (open < 0) return false;
            string word = mapName.Substring(open + 1, end - open - 1);
            if (!word.StartsWith(LegacyTagPrefix, StringComparison.Ordinal)) return false;
            string name = word.Substring(LegacyTagPrefix.Length);
            if (!IsTagWord(name)) return false;
            TerrainMode tagged;
            if (!TryParse(name, out tagged)) return false;
            plain = mapName.Substring(0, open).TrimEnd();
            mode = tagged;
            return true;
        }

        /// The mode a map name asks for, in either the current or the legacy format. The current format wins when
        /// a name somehow carries both.
        public static bool TrySplitAnyTag(string mapName, out string plain, out TerrainMode mode)
        {
            if (TrySplitTag(mapName, out plain, out mode)) return true;
            return TrySplitLegacyTag(mapName, out plain, out mode);
        }

        private static bool IsTagWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            foreach (char c in word)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        // ------------------------------------------------------------------ names

        /// "MainRoadOnly" -> TerrainMode.MainRoadOnly. Case-insensitive; unknown names fail.
        public static bool TryParse(string name, out TerrainMode mode)
        {
            mode = Default;
            if (string.IsNullOrEmpty(name)) return false;
            string t = name.Trim();
            foreach (var m in All)
                if (string.Equals(Name(m), t, StringComparison.OrdinalIgnoreCase)) { mode = m; return true; }
            return false;
        }

        /// The name as it is written into a save or onto the wire.
        public static string Code(TerrainMode m) => Name(m);

        /// The mode a code names, or Default when the code is empty or unknown.
        public static TerrainMode FromCode(string code)
        {
            TerrainMode m;
            return TryParse(code, out m) ? m : Default;
        }
    }
}
