using System;

namespace TLDRevamp.WorldGen
{
    /// The rule that decides which terrain a world is generated with. No game state in it, so it can be unit tested
    /// outside the game (tools/unit/TerrainSelectTest.cs).
    ///
    /// The order matters:
    ///
    ///   the host's mode     - when we play in somebody else's world, the host's shape rules. The world is the
    ///                         host's: it was generated on its machine, and everybody drives the same roads.
    ///   the save's tag      - a save keeps the terrain it was made with, so loading an old save gives the same
    ///                         world it gave before.
    ///   the player's choice - a new game: the mode the player picked in the new-game screen.
    ///
    /// A save tag that names a mode we do not offer (the disabled old terrain) is ignored, and the player's own
    /// choice is used instead: a save must never put the game into a state it cannot generate.
    public static class TerrainSelect
    {
        /// Where a mode came from, for the log and the diagnostics.
        public const string SourceHost = "host";
        public const string SourceSave = "save";
        public const string SourceNewGame = "new game";

        /// The mode a world gets, given the host's mode (or -1 when there is none), the save's map name and the
        /// player's own choice.
        public static TerrainMode Choose(int hostMode, string saveMapName, TerrainMode own, out string source)
        {
            if (hostMode >= 0 && hostMode < TerrainModes.All.Length)
            {
                var host = (TerrainMode)hostMode;
                if (TerrainModes.IsSelectable(host))
                {
                    source = SourceHost;
                    return host;
                }
            }
            // Both the current tag and the legacy "[Terrain: <mode>]" tag are read, so a save written by either
            // build is found: the map name is persisted, and a reader that only knew one format would silently
            // start a new world instead of the save.
            if (!string.IsNullOrEmpty(saveMapName) && TerrainModes.TrySplitAnyTag(saveMapName, out _, out TerrainMode tagged)
                && TerrainModes.IsSelectable(tagged))
            {
                // The tag names a mode we offer: the save keeps the terrain it was made with.
                source = SourceSave;
                return tagged;
            }
            source = SourceNewGame;
            return TerrainModes.IsSelectable(own) ? own : TerrainModes.Default;
        }

        /// The same rule, for the case where there is no host.
        public static TerrainMode Choose(string saveMapName, TerrainMode own, out string source) =>
            Choose(-1, saveMapName, own, out source);
    }
}
