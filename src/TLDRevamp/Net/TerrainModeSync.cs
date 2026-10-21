using System;
using TLDRevamp.WorldGen;

namespace TLDRevamp.Net
{
    /// The host's terrain mode, sent to every joiner in Welcome (issue #32).
    ///
    /// The world is generated on every machine from the seed alone, so a joiner that generated a different shape
    /// than the host would be driving on roads the host has never heard of. The mode therefore rides along with
    /// the rest of the host's world description (seed, start car, map type, game mode) and the joiner takes it
    /// before its world loads.
    ///
    /// Only the mode is sent, not the whole map: the mode is what decides the shape, and it is a single byte.
    /// An old host (which does not send it) means "the game as shipped" - Crossroads - so the joiner keeps the
    /// world it would have had anyway.
    public static class TerrainModeSync
    {
        /// The byte for a mode. A mode we do not offer is never sent: the host sends the game's own terrain
        /// instead, so the joiner is never asked for something it cannot do.
        public static byte HostModeFor(TerrainMode mode)
        {
            if (!TerrainModes.IsSelectable(mode)) mode = TerrainModes.Default;
            return (byte)mode;
        }

        /// Read the host's mode from Welcome. Returns false when the message carried none (an old host).
        public static bool Read(NetReader r, out TerrainMode mode)
        {
            mode = TerrainModes.Default;
            if (r == null || r.Bad) return false;
            if (r.Remaining < 1) return false;   // an old host: no mode on the wire
            byte b = r.U8();
            if (r.Bad) return false;
            TerrainMode parsed;
            if (!TerrainModes.TryParse(EnumName(b), out parsed)) return false;
            if (!TerrainModes.IsSelectable(parsed)) return false;   // never take a mode we do not offer
            mode = parsed;
            return true;
        }

        /// The mode names are stable strings on the wire, so an unknown byte cannot silently mean another mode.
        private static string EnumName(byte b)
        {
            switch (b)
            {
                case (byte)TerrainMode.Crossroads: return TerrainModes.Name(TerrainMode.Crossroads);
                case (byte)TerrainMode.MainRoadOnly: return TerrainModes.Name(TerrainMode.MainRoadOnly);
                case (byte)TerrainMode.OldTerrain: return TerrainModes.Name(TerrainMode.OldTerrain);
                default: return "";
            }
        }
    }
}
