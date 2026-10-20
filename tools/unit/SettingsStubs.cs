using System;
namespace TLDRevamp.Fixes
{
    static class MirrorThrottle { internal static bool Enabled; internal static int EveryN; internal static bool NoShadows; }
    static class TinyRendererCull { internal static bool Enabled; internal static float ThresholdPx; }
    static class GrassCameraThrottle { internal static bool Enabled; internal static int EveryN; }
    static class RenderOptions { internal static float ProbeInterval; internal static int ShadowCascades; internal static bool SmallItemShadows; }
    static class BinocularLensCamera { internal static bool Enabled; }
    static class RoadPlaceCache { internal static bool Enabled; }
    static class ObjGenFast { internal static bool Enabled; }
    static class SceneryTileSync { internal static bool Enabled; }
    static class TerrainSync { internal static bool Enabled; }
    static class RoadConnectMemo { internal static bool Enabled; }
    static class DotChunkMemo { internal static bool Enabled; }
    static class AsyncTerrain { internal static bool Enabled; }
    static class ParallelRoadGen { internal static bool Enabled; }
    static class ChunksNeededInPlace { internal static bool Enabled; }
    static class ObjPlaceInPlace { internal static bool Enabled; }
    static class ItemSpawnSpread { internal static bool Enabled; }
    static class FarItemLookup { internal static bool Spread; }
    static class PartConditionIdle { internal static bool Enabled; }
    static class SleepingWheelSkip { internal static bool Enabled; }
    static class ImpactSoundLimiter { internal static int Mode; }
    static class RebaseParticles { internal static bool Enabled; }
    static class AlignNearestRoad { internal static bool Enabled; }
    static class AiRaycastNoAlloc { internal static bool Enabled; }
    static class OutfitDistanceNoAlloc { internal static bool Enabled; }
    static class IdleTextureStreaming { internal static bool Enabled; }
    static class IdlePourPause { internal static bool Enabled; }
    static class IdleCarParticles { internal static bool Enabled; }
    static class TrailerBrakes { internal static bool Enabled; }
    static class DrivingAssists { internal static bool Roll; internal static bool Sway; internal static bool Traction; }
}
namespace BepInEx { static class Paths { internal static string ConfigPath; } }
namespace TLDRevamp
{
    static class Json { internal static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s); }
    class Plugin { internal static Plugin Instance = new Plugin(); internal static Logger Log = new Logger(); internal Overlay Overlay = new Overlay(); }
    class Logger { internal void LogWarning(string s) { throw new Exception(s); } }
    class Overlay { internal bool Visible; }
    static class WorldOverlays { internal static bool Ownership, Ghost, GhostAll, Links; }
    static class ObjectPanels { internal static bool Compare, Events; }
    static class Sandstorms { internal static int Frequency; internal static void SetFrequency(int n) { Frequency = n; } }
}
namespace TLDRevamp.Net
{
    static class JoinLoading { internal static bool Enabled = true; }
    static class Voice { internal enum Mode { Push, Open, Off } internal static Mode Setting; internal static float Volume; }
    static class Tpa { internal static int[] Choices = { -1, 0, 60, 300, 900 }; internal static bool Enabled; internal static int CooldownS; }
    static class NetMeter { internal static bool Show; }
    static class ItemTraffic { internal static bool Show; }
    static class Reports { internal static bool Share, AskPlayers; }
}
