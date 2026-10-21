using UnityEngine;

namespace TLDRevamp.Fixes
{
    public static partial class TerrainHorizon
    {
        /// How far the player's *global* position may have moved and still count as an origin
        /// shift. The shift keeps the global position where it was, so only a small allowance
        /// (the player may have walked a little in the same frame) is given.
        internal const float OriginShiftGlobalTolM = 5f;

        /// How far the player's *Unity* position must have jumped to count as an origin shift.
        /// The game shifts the world in 2500 m steps, so a jump of this size is the shift and not
        /// an ordinary walk.
        internal const float OriginShiftUnityJumpM = 500f;

        private static readonly TerrainHorizonGeometry.OriginShiftObserver OriginObserver =
            new TerrainHorizonGeometry.OriginShiftObserver(OriginShiftGlobalTolM, OriginShiftUnityJumpM);

        internal static bool IsOriginShift(Vector3d at, Vector3 unityAt) =>
            OriginObserver.Observe(at.x, at.z, unityAt.x, unityAt.y, unityAt.z);

        internal static void ResetOriginObservation() => OriginObserver.Reset();
    }
}
