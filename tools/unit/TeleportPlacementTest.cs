#pragma warning disable CS0649, CS8981
global using tosaveitemscript = TeleportFakes.Item;
global using mainscript = TeleportFakes.Main;
using System;
using System.Collections.Generic;
using System.Reflection;
using TLDRevamp.Net;
using UnityEngine;

static class TeleportPlacementTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static RaycastHit Hit(Collider c, float y, float normal = 1) => new RaycastHit { collider = c, point = new Vector3(0, y, 0), normal = new Vector3(0, normal, 0) };
    static void Main()
    {
        var terrain = new TerrainCollider(); var floor = new Collider();
        Physics.Hits = p => new[] { Hit(terrain, 10, .4f), Hit(floor, 5) };
        Check(TpaPlacement.StandHit(0, 0, null) == null, "steep terrain still excludes buried static floors");
        var item = new tosaveitemscript(); var body = new Rigidbody { transform = item.transform };
        var top = new Collider { attachedRigidbody = body, transform = item.transform };
        Physics.Hits = p => new[] { Hit(top, 3), Hit(terrain, 0) };
        Check(TpaPlacement.StandHit(0, 0, 4, true).Value.point.y == 0, "loose rigidbody tops are not vehicle floors");
        item.car = new object();
        Check(TpaPlacement.StandHit(0, 0, 4, true).Value.point.y == 3, "parked vehicle roof accepted");
        Check(TpaPlacement.StandHit(0, 0, null).Value.point.y == 0, "vehicle roof cannot end static ground wait");
        body.velocity = new Vector3(1, 0, 0);
        Check(TpaPlacement.StandHit(0, 0, 4, true).Value.point.y == 0, "moving vehicle roof rejected");
        body.velocity = default; item.car = null; item.attachable = new attachablescript { utanfuto = new object() };
        Check(TpaPlacement.StandHit(0, 0, 4, true).Value.point.y == 3, "parked trailer bed accepted");
        item.ProxyReady = false;
        Check(TpaPlacement.StandHit(0, 0, 4, true).Value.point.y == 0, "just placed proxy vehicle cannot be a floor");
        Physics.Capsule = (p, buf) => { buf[0] = top; return 1; };
        Check(!TpaPlacement.Room(default, null), "room rejects a vehicle body");
        Check(TpaPlacement.StandHit(0, 0, 4, true) == null, "blocked candidate cannot land");
        Physics.Capsule = (p, buf) => 0;
        Check(TpaPlacement.StandHit(0, 0, 4, true, false).HasValue, "strict selection can inspect candidate room separately");

        // batch13 settled gaps: the old body-minus-1.1 probe cut through the supporting floor.
        Physics.Hits = p => new[] { Hit(terrain, 0) };
        Physics.Capsule = (p, buf) => { if (p.y < .3f) { buf[0] = terrain; return 1; } return 0; };
        foreach (float gap in new[] { .888f, .928f })
        {
            var settled = new Vector3(0, gap, 0);
            Check(!TpaPlacement.Room(settled - Vector3.up * 1.1f, null), "old settled probe intersects support");
            Check(TpaPlacement.Landing(settled, null, out bool clear).HasValue && clear, "seated-car/indoor arrival checks clearance above measured floor");
            Check(Physics.LastRayTrigger == QueryTriggerInteraction.Ignore && Physics.LastCapsuleTrigger == QueryTriggerInteraction.Ignore,
                "landing ignores car interior triggers in both support and clearance queries");
        }
        var own = new Collider();
        Physics.Capsule = (p, buf) => { buf[0] = own; return 1; };
        Check(TpaPlacement.Landing(new Vector3(0, .888f, 0), own.transform.root, out bool selfClear).HasValue && selfClear,
            "landing ignores player's own colliders");
        foreach (var obstruction in new[] { floor, top })
        {
            Physics.Capsule = (p, buf) => { buf[0] = obstruction; return 1; };
            Check(TpaPlacement.Landing(new Vector3(0, .928f, 0), own.transform.root, out bool clear).HasValue && !clear,
                "landing still reports real wall/furniture/vehicle body beside a valid floor");
        }
        Physics.Hits = p => Array.Empty<RaycastHit>();
        Check(!TpaPlacement.Landing(new Vector3(0, .928f, 0), null, out bool noFloor).HasValue && !noFloor, "no support does not certify landing clearance");
        Physics.Capsule = (p, buf) => 0;

        var foot = new fpscontroller(); mainscript.s = new mainscript { player = foot };
        Time.realtimeSinceStartup = 0; Tpa.Go(0, 0, 0, 0);
        foot.transform.position = new Vector3(2, .114f, 0);
        Time.realtimeSinceStartup = .6f; Tpa.Tick();
        Time.realtimeSinceStartup = 5.7f; Tpa.Tick();
        Check(TLDRevamp.SessionLog.LastArrival == "within landing area; objects applied" && TLDRevamp.SessionLog.Failures == 0,
            "actual on-foot arrival tick accepts batch13 indoor ring instead of logging arrival failed");
        Tpa.ResetTelemetry(); TLDRevamp.SessionLog.Arrivals = 0;

        Physics.Capsule = (p, buf) => 0;
        Physics.Hits = p => new[] { Hit(terrain, -2.1f) };
        foot.transform.position = new Vector3(0, -1, 0);
        Time.realtimeSinceStartup = 0; TLDRevamp.DebugBridge.Placements = 0; Tpa.Go(0, 0, 0, 0);
        for (int i = 1; i <= 60; i++) { Time.realtimeSinceStartup = i * .5f; Tpa.Tick(); }
        Check(TLDRevamp.DebugBridge.Placements == 1, "actual TPA tick never reapplies valid seated-target fallback for 30 seconds");
        Tpa.ResetTelemetry();
        Physics.Hits = p => new[] { Hit(terrain, -7.1f) };
        foot.transform.position = new Vector3(0, -6, 0);
        Time.realtimeSinceStartup = 0; TLDRevamp.DebugBridge.Placements = 0; Tpa.Go(0, 0, 0, 0);
        Time.realtimeSinceStartup = 10; Tpa.Tick();
        Check(TLDRevamp.DebugBridge.Placements == 1, "unchanged low fallback is observed without another teleport");
        Physics.Hits = p => new[] { Hit(terrain, -1) };
        Time.realtimeSinceStartup = 11; Tpa.Tick();
        Check(TLDRevamp.DebugBridge.Placements == 2, "new higher support triggers actual TPA placement after strict window");
        foot.transform.position = new Vector3(0, .1f, 0);
        Time.realtimeSinceStartup = 12; Tpa.Tick();
        Check(TLDRevamp.DebugBridge.Placements == 2, "improved landing is not applied repeatedly");
        Physics.Hits = p => new[] { Hit(terrain, p.x == 0 && p.z == 0 ? -1.6f : -.4f) };
        Check(!TpaPlacement.HigherFloor(default, new Vector3(0, -.5f, 0)), "retry probes the floor placement would choose, not a higher unselected ring");
        Tpa.ResetTelemetry(); TLDRevamp.SessionLog.Arrivals = TLDRevamp.SessionLog.Failures = 0;

        // Exercise the real Go/CarTo/CarFloor/Release and DistUnFreezeRB prefix, including a repeated TPA.
        var car = new tosaveitemscript { car = new object() };
        var rb = new Rigidbody { transform = car.transform };
        car.RB = rb; car.transform.Bodies.Add(rb);
        car.transform.Colliders.Add(new Collider { transform = car.transform, attachedRigidbody = rb });
        var pl = new fpscontroller { Bsitting = true, seat = new seatscript { tosave = car } };
        mainscript.s = new mainscript { player = pl };
        Physics.Hits = p => Array.Empty<RaycastHit>();
        Time.realtimeSinceStartup = 0; Tpa.Go(0, 10, 0, 0);
        Check(rb.isKinematic && Tpa.Holding(rb), "moved car is held");
        Check(TpaPlacement.StandHit(0, 0, 30, true) == null, "empty destination does not invent a floor");
        Physics.Hits = p => new[] { Hit(car.transform.Colliders[0], 14) };
        Check(TpaPlacement.StandHit(0, 0, 30, true) == null, "moving asker group is excluded as a landing floor");
        Physics.Hits = p => Array.Empty<RaycastHit>();
        Time.realtimeSinceStartup = 31; Tpa.Tick();
        Check(Tpa.Holding(rb) && rb.isKinematic && Tpa.CarsGaveUp == 1, "no floor at deadline remains physically held with failure event");
        TLDRevamp.Fixes.UnfreezeOnGround.Enabled = false;
        bool unfreeze = (bool)typeof(TLDRevamp.Fixes.UnfreezeOnGround).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { car });
        Check(!unfreeze, "game streamer cannot drop a held owned car even when general ground fix is disabled");
        Time.realtimeSinceStartup = 37; Tpa.Tick();
        Check(!Tpa.RefreshingObjects && TLDRevamp.SessionLog.Arrivals == 1, "held failure completes arrival telemetry instead of staying pending forever");
        Tpa.Go(0, 10, 0, 0);
        Check(rb.isKinematic && Tpa.Holding(rb), "re-TPA re-holds after restoring original body mode");
        Physics.Hits = p => new[] { new RaycastHit { collider = terrain, point = new Vector3(p.x, 0, p.z), normal = Vector3.up } };
        Physics.Box = (p, e) => Math.Abs(p.x) < 4 && Math.Abs(p.z) < 4 ? new[] { floor } : Array.Empty<Collider>();
        Time.realtimeSinceStartup = 68; Tpa.Tick();
        Check(!Tpa.Holding(rb) && !rb.isKinematic, "re-TPA car restores dynamic mode after clear ring landing");
        Check(Math.Abs(car.transform.position.x) >= 4 || Math.Abs(car.transform.position.z) >= 4, "car searches beyond blocked pivot and small rings");
        Check(Math.Abs(car.transform.position.y - 1.4f) < .001, "car lowered to its floor before release");
        Console.WriteLine($"Teleport placement: {checks} checks passed");
    }
}

namespace UnityEngine
{
    public class Component
    {
        public Transform transform;
        public string name = "fixture";
        public T GetComponent<T>() where T : class => transform.GetComponent<T>();
        public T GetComponentInParent<T>() where T : class => GetComponent<T>();
        public T[] GetComponentsInChildren<T>() => transform.GetComponentsInChildren<T>();
        public int GetInstanceID() => GetHashCode();
    }
    public class Transform : Component
    {
        public Transform() { transform = this; }
        public object Item;
        public Transform root => this;
        public bool IsChildOf(Transform t) => t == this;
        public Vector3 position, lossyScale = Vector3.one;
        public Quaternion rotation;
        public Vector3 right => Vector3.right;
        public Vector3 up => Vector3.up;
        public Vector3 forward => Vector3.forward;
        public List<Rigidbody> Bodies = new List<Rigidbody>();
        public List<Collider> Colliders = new List<Collider>();
        public new T GetComponent<T>() where T : class => Item as T;
        public new T[] GetComponentsInChildren<T>() => typeof(T) == typeof(Rigidbody) ? (T[])(object)Bodies.ToArray() : (T[])(object)Colliders.ToArray();
        public T GetComponentInChildren<T>() where T : class => Item as T;
        public Vector3 TransformPoint(Vector3 p) => position + p;
        public Vector3 InverseTransformPoint(Vector3 p) => p - position;
    }
    public class Rigidbody : Component { public bool isKinematic; public Vector3 velocity, angularVelocity, position; }
    public class Collider : Component
    {
        public Collider() { transform = new Transform(); }
        public bool isTrigger, enabled = true;
        public Rigidbody attachedRigidbody;
        public Bounds bounds => new Bounds(transform.position, new Vector3(2, 2, 2));
    }
    public class TerrainCollider : Collider { }
    public struct RaycastHit { public Collider collider; public Vector3 point, normal; }
    public struct Bounds
    {
        public Vector3 center, size;
        public Bounds(Vector3 c, Vector3 s) { center = c; size = s; }
        public Vector3 extents => size * .5f;
        public Vector3 min => center - extents;
        public Vector3 max => center + extents;
        public void Encapsulate(Bounds b) { Encapsulate(b.min); Encapsulate(b.max); }
        public void Encapsulate(Vector3 p)
        {
            var lo = min; var hi = max;
            lo = new Vector3(MathF.Min(lo.x,p.x),MathF.Min(lo.y,p.y),MathF.Min(lo.z,p.z));
            hi = new Vector3(MathF.Max(hi.x,p.x),MathF.Max(hi.y,p.y),MathF.Max(hi.z,p.z));
            center = (lo + hi) * .5f; size = hi - lo;
        }
    }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float a,float b,float c) { x=a;y=b;z=c; }
        public static Vector3 zero => default;
        public static Vector3 one => new Vector3(1,1,1);
        public static Vector3 right => new Vector3(1,0,0);
        public static Vector3 up => new Vector3(0,1,0);
        public static Vector3 forward => new Vector3(0,0,1);
        public static Vector3 down => new Vector3(0,-1,0);
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 Scale(Vector3 a,Vector3 b) => new Vector3(a.x*b.x,a.y*b.y,a.z*b.z);
        public static Vector3 operator +(Vector3 a,Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b) => new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float b) => new Vector3(a.x*b,a.y*b,a.z*b);
    }
    public struct Quaternion { }
    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }
    public static class Physics
    {
        public const int DefaultRaycastLayers = 0;
        public static Func<Vector3,RaycastHit[]> Hits = p => Array.Empty<RaycastHit>();
        public static Func<Vector3,Collider[],int> Capsule = (p,b) => 0;
        public static Func<Vector3,Vector3,Collider[]> Box = (p,e) => Array.Empty<Collider>();
        public static QueryTriggerInteraction LastRayTrigger, LastCapsuleTrigger;
        public static RaycastHit[] RaycastAll(Vector3 p, Vector3 dir, float dist, int layers, QueryTriggerInteraction trigger) { LastRayTrigger = trigger; return Hits(p); }
        public static int RaycastNonAlloc(Vector3 p,Vector3 dir,RaycastHit[] buf,float dist,int layers,QueryTriggerInteraction trigger) { var h=Hits(p); Array.Copy(h,buf,Math.Min(h.Length,buf.Length)); return Math.Min(h.Length,buf.Length); }
        public static int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float radius,Collider[] buf,int layers,QueryTriggerInteraction trigger) { LastCapsuleTrigger = trigger; return Capsule(a,buf); }
        public static Collider[] OverlapBox(Vector3 p,Vector3 ext,Quaternion q,int layers,QueryTriggerInteraction trigger) => Box(p,ext);
    }
    public static class Time { public static float realtimeSinceStartup; public static double realtimeSinceStartupAsDouble => realtimeSinceStartup; }
    public static class Mathf
    {
        public const float PI = MathF.PI, Deg2Rad = PI/180;
        public static float Abs(float v) => MathF.Abs(v);
        public static float Sin(float v) => MathF.Sin(v);
        public static float Cos(float v) => MathF.Cos(v);
        public static int CeilToInt(float v) => (int)MathF.Ceiling(v);
    }
}
namespace TeleportFakes
{
public class Item : Component
{
    public Item() { transform = new Transform { Item = this }; }
    public object car; public attachablescript attachable; public Rigidbody RB; public bool buried, ProxyReady = true;
    public void DistUnFreezeRB() { }
}
}
public class attachablescript { public object utanfuto; }
public class seatscript : Component { public tosaveitemscript tosave; }
public class pickupable : Component { }
public class fpscontroller : Component
{
    public fpscontroller() { transform = new Transform(); }
    public bool Bsitting; public seatscript seat; public pickupable pickedUp, inHandP;
    public void GetOut(Vector3 p,bool force) { Bsitting = false; }
}
namespace TeleportFakes
{
public class Main
{
    public static mainscript s; public fpscontroller player;
    public static Vector3 UnityPosFromGlobal(Vector3d p) => new Vector3((float)p.x,(float)p.y,(float)p.z);
    public static Vector3d GlobalFromUnityPos(Vector3 p) => new Vector3d(p.x,p.y,p.z);
}
}
public struct Vector3d
{
    public double x,y,z; public Vector3d(double a,double b,double c) { x=a;y=b;z=c; }
    public double magnitude => Math.Sqrt(x*x+y*y+z*z);
    public static Vector3d operator -(Vector3d a,Vector3d b) => new Vector3d(a.x-b.x,a.y-b.y,a.z-b.z);
}
namespace HarmonyLib
{
    public class HarmonyPatch : Attribute { public HarmonyPatch(Type t,string method) { } }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int p) { } }
    public static class Priority { public const int Low = 0; }
}
namespace TLDRevamp
{
    public static class DebugBridge
    {
        public static bool TpBusy => false;
        public static int Placements;
        public static void PlaceTpa(double x,double z,double y,bool retry = false) { Placements++; }
        public static void PlaceAt(double x,double z) { }
        public static void TpSettle(fpscontroller p) { }
        public static bool TpaWorldReady(double x,double y,double z) => false;
        public static RaycastHit? StandHit(float x,float z,float? below) => TpaPlacement.StandHit(x,z,below);
    }
    public static class SessionLog { public static int Arrivals, Failures; public static string LastArrival; public static void Event(string kind,int from=-1,int to=-1,float value=0,int count=0,float dx=0,float dy=0,float dz=0,string result="ok") { if (kind == "tpa-arrived") { Arrivals++; LastArrival = result; } if (kind == "tpa-failure") Failures++; } }
    // Panel history is covered by TeleportTest; placement runs with panels off.
    internal static class ObjectPanelEvents { internal static void TpaObject(uint net, bool sent, int player, double now) { } }
    public static class RevampSettings { public static void Save() { } }
    public static class Json { public static string Str(string s) => s; }
    public static class Plugin { public static Logger Log = new Logger(); public class Logger { public void LogWarning(string s) { } } }
}
namespace TLDRevamp.Net
{
    public static class Entities
    {
        public const int MyId=1;
        public static bool Finite(double d) => double.IsFinite(d);
        public static bool RootNetOf(tosaveitemscript it,out uint net) { net=1;return true; }
        public static bool TpaFloorReady(tosaveitemscript it) => it == null || it.ProxyReady;
        public static void DrainTpaInbox() { }
        public static bool TpaInboxEmpty => true;
        public static void BeginTpaRefresh() { }
        public static void ServerTpaTick() { }
        public static void ServerTpaBegin(int player,Vector3d pos) { }
    }
    public static class ItemSnapshot { public static List<tosaveitemscript> Group(tosaveitemscript car) => new List<tosaveitemscript> { car }; }
    public static class Mp
    {
        public static MpServer Server;
        public static ulong MySteamId() => 0;
        public static Vector3d LocalGlobal() => default;
        public static float LocalYaw() => 0;
    }
    public sealed partial class MpServer
    {
        internal sealed class Peer { internal int Id,Conn; internal bool Ready,HasPos; internal string Name; internal ulong SteamId; internal Vector3d Pos; internal float Yaw; internal uint SeatNet; }
        readonly Dictionary<int,Peer> _peers = new Dictionary<int,Peer>();
        readonly NetWriter _w = new NetWriter();
        readonly Transport T = new Transport();
    }
    public class Transport { public bool Send(int conn,NetWriter w,int flags) => true; }
    public static class SteamTransport { public const int SendReliable=1; }
    public static class Session { public static void Show(int k,string name,string text) { } public static string MyName() => "fixture"; }
    public static class Protocol { public const byte Chat=1; }
    public static class Plausibility { public static void Grace(int p) { } }
    public static class DedicatedServer { public static bool Enabled; }
}
