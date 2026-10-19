global using Vector3d = TelemetryPrivacyFakes.Vector3d;
global using fpscontroller = TelemetryPrivacyFakes.Player;
global using savedatascript = TelemetryPrivacyFakes.Saves;
global using mainscript = TelemetryPrivacyFakes.Game;
global using DataFromMenuScript = TelemetryPrivacyFakes.MenuData;
global using menuhandler = TelemetryPrivacyFakes.Menu;
global using settingsscript = TelemetryPrivacyFakes.Settings;
global using itemdatabase = TelemetryPrivacyFakes.Database;
global using partslotscript = TelemetryPrivacyFakes.PartSlot;
#pragma warning disable CS0649, CS0414
using System;
using System.Collections.Generic;
using System.Linq;
using TLDRevamp;

namespace BepInEx { static class Paths { internal static string BepInExRootPath; } }
namespace Unity.Profiling
{
    struct ProfilerCategory { internal static ProfilerCategory Internal, Scripts, Physics, Render, Memory, Loading; }
    struct ProfilerRecorder
    {
        internal bool Valid => true;
        internal long LastValue => 1000000;
        internal static ProfilerRecorder StartNew(ProfilerCategory c, string n, int capacity) => default;
        internal void Dispose() { }
    }
}
namespace UnityEngine
{
    static class Time
    {
        internal static double realtimeSinceStartupAsDouble;
        internal static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;
        internal static float timeScale => 1;
        internal static float unscaledDeltaTime => .016f;
    }
    struct Vector3
    {
        internal float x, y, z;
        internal float sqrMagnitude => x * x + y * y + z * z;
        internal float magnitude => (float)Math.Sqrt(sqrMagnitude);
        internal static Vector3 zero => default;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
    }
    class Object { internal static T[] FindObjectsOfType<T>() => Array.Empty<T>(); internal static T[] FindObjectsByType<T>(FindObjectsSortMode mode) => Array.Empty<T>(); }
    enum FindObjectsSortMode { None }
    class Transform { internal Vector3 position; internal string name; }
    class GameObject : Object
    {
        internal string name;
        internal Transform[] Children = Array.Empty<Transform>();
        internal TelemetryPrivacyFakes.PartSlot[] Slots = Array.Empty<TelemetryPrivacyFakes.PartSlot>();
        internal T[] GetComponentsInChildren<T>(bool inactive) => typeof(T) == typeof(Transform) ? Children.Cast<T>().ToArray() : Slots.Cast<T>().ToArray();
    }
    class Rigidbody { internal bool isKinematic; internal Vector3 velocity; internal bool IsSleeping() => false; }
    class Renderer { internal bool isVisible; }
    class MonoBehaviour { }
    class Light { internal bool enabled; internal LightShadows shadows; }
    enum LightShadows { None }
    class ParticleSystem { internal bool isPlaying; }
    static class Screen { internal static int width => 1920; internal static int height => 1080; internal static bool fullScreen => true; }
    static class QualitySettings { internal static int GetQualityLevel() => 2; internal static int vSyncCount => 0; }
    static class SystemInfo
    {
        internal static string processorType => "CPU Model " + TelemetryPrivacyTest.Private;
        internal static string graphicsDeviceName => "GPU Model " + TelemetryPrivacyTest.Private;
        internal static string operatingSystem => "Windows 10 " + TelemetryPrivacyTest.Private;
        internal static string graphicsDeviceVersion => TelemetryPrivacyTest.Private;
        internal static int processorCount => 8;
        internal static int systemMemorySize => 32768;
        internal static int graphicsMemorySize => 8192;
    }
    enum LogType { Warning, Error, Exception, Assert }
    static class Application
    {
        internal static bool isFocused => true;
        internal static int targetFrameRate => 60;
        internal static string unityVersion => "2021.3";
        internal static event Action<string, string, LogType> logMessageReceivedThreaded;
        internal static void Log(string msg, string stack, LogType type) => logMessageReceivedThreaded?.Invoke(msg, stack, type);
        internal static void OpenURL(string url) { }
    }
    static class Mathf { internal static float Min(float a, float b) => Math.Min(a, b); internal static int Clamp(int n, int a, int b) => Math.Clamp(n, a, b); }
}
namespace UnityEngine.Profiling { static class Profiler { internal static long GetTotalAllocatedMemoryLong() => 0; internal static long GetTotalReservedMemoryLong() => 0; } }
namespace UnityEngine.SceneManagement
{
    struct Scene { internal string name => TelemetryPrivacyTest.Private; }
    static class SceneManager { internal static Scene GetActiveScene() => default; }
}
namespace TelemetryPrivacyFakes
{
    class Database
    {
        internal static Database s;
        internal UnityEngine.GameObject[] items, pois;
    }
    class PartSlot { internal string[] tipus; }
    struct Vector3d { internal double x, y, z; }
    class Player
    {
        internal bool Bsitting, loadedmove;
        internal Seat seat;
        internal UnityEngine.Rigidbody RB;
        internal UnityEngine.Transform transform = new UnityEngine.Transform();
    }
    class Seat { internal bool driverSeat0; internal Car Car; internal UnityEngine.Rigidbody RB; }
    class Car { internal UnityEngine.Rigidbody RB; internal UnityEngine.Transform transform = new UnityEngine.Transform(); }
    class SavedItem { internal UnityEngine.Rigidbody RB; }
    class Saves { internal static savedatascript s = new savedatascript(); internal Dictionary<int, SavedItem> items = new Dictionary<int, SavedItem>(); }
    class Game
    {
        internal static mainscript s = new mainscript();
        internal fpscontroller player = new fpscontroller();
        internal List<Car> Cars = new List<Car>();
        internal bool started = true, crsrLocked, pauseMenuOpen;
        internal static Vector3d GlobalFromUnityPos(UnityEngine.Vector3 p) => new Vector3d { x = p.x, y = p.y, z = p.z };
    }
    class MenuData { internal static DataFromMenuScript s = new DataFromMenuScript(); internal bool mainmenu, load; }
    class Menu { internal static menuhandler s = new menuhandler(); internal Map currentMainMap = new Map(); }
    class Map
    {
        internal bool firstRun = true;
        internal List<TerrainGen> terrainGen;
        internal List<RoadGen> roadGens;
        internal List<object> poiGens;
        internal List<ObjectGen> objGens;
    }
    class TerrainGen { internal List<int> coordsNeeded; internal Dictionary<int, object> terrains; }
    class RoadGen { internal List<int> chunksNeeded; internal Dictionary<int, RoadChunk> chunks; }
    class RoadChunk { internal bool Done() => true; }
    class ObjectGen { internal bool disabled; internal List<Category> categories; }
    class Category { internal bool disable; internal List<int> chunksNeeded; internal Dictionary<int, Chunk> generatedChunks; }
    class Chunk { internal bool generated; }
    class Settings { internal static settingsscript s; internal Setting S; }
    class Setting { internal float GetDrawDistance() => 5000; }
}
namespace TLDRevamp
{
    class Plugin
    {
        internal static Plugin Instance = new Plugin();
        internal static string Version => "0.1";
        internal static string BuildHash => "abcdef0";
        internal static string CommitLabel => "abcdef0+changes";
        internal static string GameBuild => "beta";
        internal static Logger Log = new Logger();
        internal Overlay Overlay = new Overlay();
#if REPORT_SHARING
        internal FeedbackReporter Feedback;
#endif
    }
    class Logger { internal void LogWarning(string s) { } internal void LogInfo(string s) { } }
    class Overlay { internal void Toast(string s) { } }
    static class DebugBridge { internal static double TotalPumpMs, MaxPumpMs; internal static string Execute(string command) => TelemetryPrivacyTest.Reply; }
    static class RevampSettings
    {
        internal sealed class Entry { internal string Key; internal Func<int> Get; }
        internal static List<Entry> Entries = new List<Entry> { new Entry { Key = "overlay", Get = () => 1 } };
    }
    static class ScriptProfiler
    {
        internal static bool TimersOn, TimerAlloc, CoarseOn;
        internal static int ColumnCount => 0;
        internal static int TickTop => 12;
        internal static string[] CoarseNames = Array.Empty<string>(), ColumnNames = Array.Empty<string>();
        internal static CoarseClock Coarse = new CoarseClock(0);
        internal static float Column(int c) => 0;
        internal static string TimersInterval(int n) => "{}";
        internal static string LastFrameTop() => null;
    }
    static class PlayerLoopProfiler
    {
        internal static bool Running;
        internal static float LastPresentWaitMs => 0;
        internal static string IntervalJson(int n) => "{}";
        internal static string LastFrameTop() => null;
    }
    static class TelemetryHooks
    {
        internal struct Events { internal int Flags, Terrain, Roads, Scenery, Buildings, Instantiated, Destroyed, Activated, Bodies, BodiesDropped, Moved; }
        internal static int SpawnMethods, SpawnSkipped;
        internal static bool Saving;
        internal static Events Previous => default;
        internal static Events TakeWork() => default;
        internal static void DrainSaves() { }
    }
}
namespace TLDRevamp.Fixes { static class PoiBacklog { internal static int Deferred; internal static int Sample(object gens) => 0; public static int Samples = 1; } }
namespace TLDRevamp.Net
{
    class Transport { internal long BytesIn, BytesOut; }
    class Server {
        internal Transport T = new Transport();
#if REPORT_SHARING
        internal readonly List<(int Target, byte[] Bytes)> Packets = new List<(int, byte[])>();
        internal bool SendReport(int target, NetWriter w) { Packets.Add((target, w.Buf.Take(w.Len).ToArray())); return true; }
        internal void SendToAll(NetWriter w, bool reliable, int except, object near, double range) { }
        internal int ReadyCount() => 1;
#endif
    }
    class Client {
        internal int ServerSeed; internal bool Ready => true; internal Transport T = new Transport();
#if REPORT_SHARING
        internal int MyId = 1;
        internal readonly List<byte[]> Packets = new List<byte[]>();
        internal bool SendReport(NetWriter w) { Packets.Add(w.Buf.Take(w.Len).ToArray()); return true; }
        internal bool SendReportState(NetWriter w) => true;
#endif
    }
    static class Mp
    {
        internal static Server Server = new Server(); internal static Client Client;
        internal static int Seed() => 1;
        internal static string NetStats() => TelemetryPrivacyTest.Reply;
    }
    static class ModObjects { internal static double ClockTime => UnityEngine.Time.realtimeSinceStartupAsDouble; }
    static class DedicatedServer { internal static bool Enabled; }
    static class Session {
        internal static string MyName() => TelemetryPrivacyTest.Secrets[0];
#if REPORT_SHARING
        internal static void Show(int kind, string who, string text) { }
#endif
    }
#if REPORT_SHARING
    static class TelemetryMarks { internal static string Note(string note) => TelemetryPrivacy.Note(note); }
#endif
    static class RemotePlayers { internal static int Count => 1; }
    static class PlayerCombat { internal static string Stats() => TelemetryPrivacyTest.Reply; }
    static class NetMeter
    {
        internal static TrafficCount[] SampleFrame(bool longFrame) => new TrafficCount[256];
        internal static string Name(byte n) => "PlayerState";
        internal static void ResetTelemetry() { }
        internal static string TelemetryTick(double now, double seconds)
        {
#if REPORT_SHARING
            var interval = new TrafficInterval { Local = now, St = SessionLog.St, Seconds = seconds };
            interval.Types[10].Sent = 1; SessionLog.History.Add(interval);
            using var writer = new System.IO.StringWriter(); interval.Write(writer, Name);
            // Numeric network counters emitted by 62528d8, deployed alongside format 3.
            return writer.ToString().TrimEnd('}') + ",\"bandwidth\":{\"identical\":1,\"corpseRoot\":2,\"unknown\":3,\"creatureRange\":4,\"deadAnimation\":5}}";
#else
            return "{}";
#endif
        }
    }
#if !REPORT_SHARING
    static class Reports
    {
        internal static string Note;
        internal static string ScrubStats() => "{}";
        internal static void OnZipped(string path) { }
        internal static Action<string> MarkCompletion(string basename, string note, double moment) { Note = note; return null; }
    }
#endif
    static class Entities
    {
        internal static int MyId => 1;
        internal static bool TelemetryAllOwnedPhysics;
        internal static long TelemetryPhysicsSkipped, StatesIn, StatesSent, StatesStale, Spawned, OwnerChanges, Handoffs;
        internal static int CopyCarCount() => 0;
        internal static string AiStats() => TelemetryPrivacyTest.Reply;
        internal static void ResetTelemetry() { }
        internal static void TelemetryUsable(uint n, int i, int s, bool sent) { }
        internal static void TelemetryTouch(uint n) { }
        internal static void CaptureTelemetry(TelemetryState state)
        {
            string text = TelemetryPrivacyTest.Secrets.Last();
            state.St = SessionLog.St;
            state.Add(new StatePlayer { Id = 1, Name = text });
            state.Add(new StateEntity { Net = 4, Name = text, Car = true, Fuel = 5 });
            state.Add(new StateSeat { Net = 4, Name = text });
            state.Add(new StateSlot { Net = 4, Name = text, Type = text, Content = text });
            state.Add(new StateCarry { Player = 1, Net = 4, Name = text });
            state.Add(new StateUsable { Net = 4, Name = text });
            state.Add(new StateEntity { Net = 5, Name = "Car06Full(Clone)", Car = true });
            state.Add(new StateEntity { Net = 6, Name = TelemetryPrivacyTest.Secrets[0], Prefab = 1 });
            state.Add(new StateCarry { Player = 1, Net = 5, Name = "TÁVCSO(Clone)" });
            state.Add(new StateUsable { Net = 5, Name = "Ignition (2)" });
            state.Add(new StateUsable { Net = 5, Name = "bus door" });
            state.Add(new StateSlot { Net = 5, Name = "WheelSlot", Type = "Wheel", Content = "Car06Full" });
            state.Add(new StateSeat { Net = 5, Name = "DriverSeat" });
            state.Add(new StateStorm { Prefab = 2 }); state.Add(new StateStation { Index = 2 });
        }
    }
}
