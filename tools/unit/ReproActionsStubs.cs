global using mainscript = ReproGame;
global using tosaveitemscript = ReproItem;
global using carscript = ReproCar;
global using fpscontroller = ReproPlayer;
global using pickupable = ReproPickup;
global using mountStuff = ReproMount;
global using attachablescript = ReproAttachable;
global using partslotscript = ReproSlot;
global using usablescript = ReproUsable;
global using save_attachable = ReproAttachment;
#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    class Transform
    {
        internal Vector3 position, eulerAngles, localEulerAngles, forward, right;
        internal Quaternion rotation;
        internal void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
        internal Vector3 TransformPoint(Vector3 p) => p;
    }
    struct Vector3
    {
        internal float x, y, z;
        internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        internal float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
    }
    struct Quaternion
    {
        internal static Quaternion identity => default;
        internal static Quaternion Euler(float x, float y, float z) => default;
        internal static Quaternion LookRotation(Vector3 d) => default;
    }
    class GameObject { internal bool activeInHierarchy = true; }
    class Collider { internal Transform transform = new Transform(); internal T GetComponentInParent<T>() where T : class => null; }
    static class Physics { internal static Collider[] OverlapSphere(Vector3 p, float r) => Array.Empty<Collider>(); }
    static class Time { internal static double realtimeSinceStartupAsDouble; internal static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble; internal static float time; internal static int frameCount; }
    static class Application { internal static bool isBatchMode, runInBackground; }
    enum EventType { Repaint }
    class Event { internal static Event current = new Event(); internal EventType type; }
    static class GUI
    {
        internal static GUISkin skin = new GUISkin(); internal static Color color, backgroundColor; internal static int depth;
        internal static void DrawTexture(Rect r, object texture) { }
        internal static void Label(Rect r, GUIContent text, GUIStyle style) { }
        internal static bool Button(Rect r, string text, GUIStyle style) => false;
        internal static string TextArea(Rect r, string text, int limit, GUIStyle style) => text;
    }
    static class Mathf { internal static float Clamp01(float n) => Math.Clamp(n, 0, 1); }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPriority : Attribute { internal HarmonyPriority(int n) { } }
    static class Priority { internal const int First = 800; }
    [AttributeUsage(AttributeTargets.Class)] class HarmonyPatch : Attribute { internal HarmonyPatch() { } internal HarmonyPatch(Type t, string s) { } internal HarmonyPatch(Type t, string s, Type[] a) { } }
    static class AccessTools { internal static System.Reflection.MethodInfo Method(Type t, string n, Type[] a = null) => null; }
}
namespace BepInEx.Bootstrap { static class Chainloader { internal static Dictionary<string, object> PluginInfos = new Dictionary<string, object>(); } }
struct Vector3d
{
    internal double x, y, z;
    internal Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    internal double sqrMagnitude => x * x + y * y + z * z;
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
}
class ReproGame
{
    internal static mainscript s = new mainscript(); internal fpscontroller player = new fpscontroller();
    internal static Vector3d GlobalFromUnityPos(Vector3 p) => new Vector3d(p.x, p.y, p.z);
    internal static Vector3 UnityPosFromGlobal(Vector3d p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
}
class ReproPlayer
{
    internal Transform transform = new Transform(), BodyRot = new Transform(), Th = new Transform();
    internal float FyRot, FxRot, throwForce, minThrowForce = .1f; internal bool Bsitting;
    internal pickupable pickedUp, inHandP; internal List<pickupable> inventory = new List<pickupable>(); internal usablescript lookedUsable;
    internal void Teleport(Vector3 p, Vector3 q) { transform.position = p; BodyRot.eulerAngles = q; }
    internal void Pickup(pickupable p) { pickedUp = p; } internal void Pickup(pickupable p, Vector3 at) { pickedUp = p; }
    internal void Drop(bool destroyed = false) { pickedUp = null; } internal void GetOut(Vector3 p) { Bsitting = false; }
    internal void InvSwitchTo(int i, bool b = false) { } internal void InvSwapTo(int i) { } internal void InvStore(int i) { } internal void InvUnStore(int i) { }
    internal void RightHandGrip() { } internal void RightHandRelease(bool b) { } internal void RayStuff() { }
}
class ReproItem
{
    internal int id = 1; internal Transform transform = new Transform(); internal GameObject gameObject = new GameObject();
    internal pickupable P; internal carscript car; internal attachablescript attachable; internal Radio radio;
    internal List<partslotscript> partslotscripts = new List<partslotscript>(); internal mountStuff[] Mounts = Array.Empty<mountStuff>(); internal bool ThrowMounts;
    internal Collider GetColliderFromIndex(int n) => new Collider();
}
class Radio { internal bool ON, amfm; internal float FR; }
class ReproPickup { internal tosaveitemscript tosave; internal mountStuff physlock; internal void PhysLock(mountStuff m) { physlock = m; } internal void UnPhysicsLock() { physlock = null; } }
class ReproMount { internal tosaveitemscript parentTosave; internal void Store(pickupable p, bool b) { p.PhysLock(this); } }
class ReproAttachable
{
    internal tosaveitemscript tosave; internal object utanfuto; internal bool attached; internal Transform transform = new Transform();
    internal void Detach() { attached = false; } internal object NewPoint(Transform t, Vector3 p, Quaternion q) => null; internal void Attach(object p, Collider c) { attached = true; }
}
class ReproSlot { internal attachablescript Part; internal attachablescript part() => Part; internal void Craft(attachablescript a) { Part = a; } internal bool UnCraft(bool b) => true; }
class ReproUsable
{
    internal float slideValue, xRot, yRot, tunedFloat, lastHoldTime; internal int currentTurnState, tosaveid, setid, poigenid, usableid; internal bool setidPoi; internal Vector3d poiid;
    internal T GetComponentInParent<T>() where T : class => null;
    internal void Refresh(float x, float y, float z, int a) { slideValue = x; xRot = y; yRot = z; currentTurnState = a; }
    internal void SyncMulti(bool b) { }
}
class Engine { internal bool running; }
class ReproCar
{
    internal bool isPlayerDriving, ignition; internal Engine Engine = new Engine(); internal usablescript ignitionUsable;
    internal float ithrottle, ibrake, isteer, iclutch, ihandbrake;
    internal bool iignition, ihorn, ihandbrakeBDown, igrDown, ignDown, ig1Down, ig2Down, ig3Down, ig4Down, ig5Down, ig6Down, iupshiftDown, idownshiftDown, iignitionDown, iLightsBDown;
    internal void ResetInput() { ithrottle = 0; } internal void GetPlayerInput() { }
}
class ReproAttachment { internal Vector3d gpos, lpos, poiid; internal int indexType, attachType, poigenid; internal ReproAttachment(attachablescript a, int n) { } }
namespace TLDRevamp
{
    class Plugin
    {
        internal static Plugin Instance = new Plugin(); internal static bool BridgeEnabled = true; internal Overlay Overlay = new Overlay();
        internal static LogSink Log = new LogSink(); internal sealed class LogSink { internal void LogInfo(string s) { } }
        internal static object KeyRepro; internal static string KeyName(object key) => "Ctrl+Insert";
    }
    static class FeedbackReporter { internal static string Root = "."; }
    static class BugReport
    {
        internal static bool BarText(out string title, out string status, out OverlayKind kind) { title = status = ""; kind = default; return false; }
        internal static int CopyFiles(IEnumerable<string> sources, string destination) => 0;
    }
    class Overlay { internal string Last; internal void Toast(string s, float n) { Last = s; } }
    static class TestSafety { internal static bool BlockAutoSave; }
    static class SessionLog { internal static void Fail(string where, Exception e) { } internal static string Sid = "test", MarksFolder; internal static double St => Time.realtimeSinceStartupAsDouble; }
    static class Json { internal static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s); internal static string Num(double n, string f = null) => n.ToString(f, System.Globalization.CultureInfo.InvariantCulture); }
    static class NetworkConditions
    {
        internal static NetworkSimulation Current; internal static bool Active => Current.Active;
        internal static ReproRow ReproRow(NetworkSimulation v) => new ReproRow { Kind = ReproKind.Network };
        internal static string Apply(NetworkSimulation v) { Current = v; return "{\"ok\":true}"; }
    }
}
namespace TLDRevamp.Net
{
    static class Protocol { internal const int Version = 46; }
    static class Mp { internal static object Client; }
    static class DedicatedServer { internal static bool Enabled; }
    static class TelemetryMarks
    {
        internal static string Local(string s) => "{\"error\":\"one mark per 10 seconds\"}";
    }
    static class Session { internal struct Player { internal bool Me; internal string Name; } internal static Player[] Players() => Array.Empty<Player>(); internal static bool Say(string s) => true; }
    public static partial class Entities
    {
        internal sealed class Ent
        {
            internal uint NetId, ParentNet; internal int OwnerId; internal bool Proxy, Stored, Held, EngRunning;
            internal tosaveitemscript Root; internal List<tosaveitemscript> Items = new List<tosaveitemscript>();
        }
        internal static string WorldSaveFile;
        internal static Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        internal static Ent Make(uint net) { var e = new Ent { NetId = net, OwnerId = MyId, Root = new tosaveitemscript() }; e.Items.Add(e.Root); ByNet[net] = e; return e; }
        internal static bool InSession = true, TestAsPlayer, PlayerActing = true, _hitchApplying, _applyingLive, _applyingState, _applyingPoi, _applyingDetach;
        internal static int MyId = 1, DetachRequests; internal static tosaveitemscript ForceHeld; private static float _keyTestUntil; private static carscript _keyTestCar;
        private static bool Resolve(Ent e) => e.Root != null;
        private static Ent EditEntityOf(tosaveitemscript it)
        {
            foreach (var e in ByNet.Values) if (e.Root == it) return e;
            foreach (var e in ByNet.Values) if (e.Items.Contains(it)) return e;
            return null;
        }
        private static uint TelemetryNet(tosaveitemscript it) => EditEntityOf(it)?.NetId ?? 0;
        private static uint TelemetryNet(carscript car) { foreach (var e in ByNet.Values) if (e.Root.car == car) return e.NetId; return 0; }
        internal static void LocalSeat(out uint net, out int seat) { net = 0; seat = -1; }
        private static string SitSeat(int seat, bool b, uint net) => "{\"ok\":true}";
        private static tosaveitemscript ItemOf(usablescript u, int a, int b) => null;
        private static usablescript[] ItemUsables(tosaveitemscript it) => Array.Empty<usablescript>();
        private static mountStuff[] LockMounts(tosaveitemscript it) { if (it.ThrowMounts) throw new Exception(); return it.Mounts; }
        private static bool PartIsProxy(attachablescript at) => EditEntityOf(at.tosave)?.Proxy == true;
        private static bool RequestDetach(attachablescript at) { DetachRequests++; return true; }
        private static bool Unmount(attachablescript at, bool b) { at.attached = false; return true; }
        private static bool IsStored(tosaveitemscript it) => false;
        private static bool IsHeld(tosaveitemscript it) => false;
        private static string KeyTest(uint net, int seconds) => "{\"refused\":9}";
        private static bool HitchTrailer(Ent e) => true;
        private static void RequestHitch(Ent tr, Ent car, partslotscript slot, bool on) { }
        private struct PoiKey { internal int Gen, U; internal double X, Y, Z; }
        private static usablescript PoiUsableOf(PoiKey k) => null;
    }
    public static partial class RemotePlayers
    {
        private class Remote { internal bool Has; internal Vector3d Target; internal uint SeatNet; internal int SeatIdx; }
        private static Dictionary<int, Remote> All = new Dictionary<int, Remote>();
    }
}
