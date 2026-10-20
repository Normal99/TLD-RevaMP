#pragma warning disable CS0649, CS8981
global using attachablescript = TestAttach;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TLDRevamp;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public partial class Ent
        {
            public byte[] SettledState;
            public bool Physical;
            public int TelemetrySnapFrame = -1;
            public double TelemetryReceived = 1;
            public Vector3d LastSentPos;
            public Quaternion LastSentRot = Quaternion.identity;
        }
        partial class SEnt { public bool Hitch; }
        static bool BoltedGroupRides = true;
        static uint _telemetryFooting, _telemetryLook;
        static float RestSpeed = .05f;
        static readonly Dictionary<uint, int> _hitchTransfers = new Dictionary<uint, int>(), _hitchCommands = new Dictionary<uint, int>();
        static uint TelemetryNet(uint net) => net;
        static bool RidesParent(Ent e) => e.Root?.attachable != null && e.Root.attachable.attached && e.Root.attachable.parentAtAttach && e.Root.transform.parent != null;
        static bool RidesOwnersContainer(Ent e) => !e.PoseLocked && !e.Held && e.Root?.P?.physlock?.parentTosave != null &&
            ByNet.Values.Any(c => c.Root == e.Root.P.physlock.parentTosave && c.OwnerId == e.OwnerId);
        static bool IsStored(TestItem it) => it.P != null && it.P.inInventory;
        static bool IsSpraying(TestItem it) => IsHeld(it) && it.GetComponent<TestSpray>()?.valve > .1f;

        static void OverlayCheck(bool ok, string why) => IdleTrafficTest.Check(ok, "overlay: " + why);
        static bool OverlayNear(Vector3d a, Vector3d b) => (a - b).magnitude < .001;
        static JsonElement Snapshot() => JsonDocument.Parse(WorldOverlays.Command("links", "state")).RootElement;
        static void Repaint(double advance = .3)
        {
            Time.realtimeSinceStartupAsDouble += advance; Time.frameCount++;
            WorldOverlays.Draw();
        }
        static Ent OverlayEnt(uint net, int owner = 1)
        {
            var e = new Ent { NetId = net, OwnerId = owner };
            e.Items.Add(e.Root); ByNet[net] = e; return e;
        }
        internal static void TestWorldOverlays()
        {
            ByNet.Clear(); Server.Clear(); TestMain.Origin = default;
            TestMain.s = new TestMain { player = new TestPlayer { Cam = new Camera() } };
            Event.current = null;
            WorldOverlays.Draw();
            OverlayCheck(GUIStyle.Measures == 0, "off returns before touching Unity event, initialization or scans");
            Event.current = new Event();
            var car = OverlayEnt(40, 2); car.Root.car = new TestCar();
            var cargo = OverlayEnt(41, 2); cargo.Proxy = true;
            var mount = new TestMount { parentTosave = car.Root };
            car.Root.Add(new[] { mount }); mount.transform.parent = car.Root.transform;
            cargo.Root.P = new TestPickup();
            // Receive the actual wire frame sample, then drive without another cargo state.
            ReadCarriedBy = 40; ReadLocalPos = new Vector3(1, 2, 3); ReadLocalRot = Quaternion.Euler(0, 15, 0); ReadLockedState = true;
            var sample = new PoseInterpolator.Sample { T = 1, Rot = Quaternion.identity };
            TakeCarried(cargo, ref sample, default, Quaternion.identity, false, false);
            cargo.LockLocalPos = ReadLocalPos; cargo.LockLocalRot = ReadLocalRot;
            ApplyPoseLock(cargo, true, 40, 0, 0);
            cargo.Ip.Add(sample, 1.1);
            WorldOverlays.Command("ownership", "on"); WorldOverlays.Command("ghost", "on"); WorldOverlays.Command("ghostall", "on");
            for (int i = 0; i < 3; i++)
            {
                car.Root.transform.position = new Vector3(100 * i, 0, 0); car.Root.transform.rotation = Quaternion.Euler(5, 45 * i, 3);
                EvalShown(cargo, 1.2 + i, .02f, out var shown, out var rotation);
                cargo.Root.transform.SetPositionAndRotation((Vector3)shown, rotation);
                OverlayCheck(OverlaySample(cargo, out var reference, out var q) && OverlayNear(reference, shown) && Quaternion.Angle(q, rotation) < .01f,
                    "locked reference follows translating and rotating car without keepalive");
                Repaint();
                var row = Snapshot().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("net").GetUInt32() == 41);
                OverlayCheck(row.GetProperty("errorM").GetDouble() < .001 && row.GetProperty("text").GetString().Contains("in frame"), "real Fill reports no growing cargo error");
                OverlayCheck(GL.Vertices.Any(v => (v - new Vector3(cargo.Root.transform.position.x, cargo.Root.transform.position.y, 0)).magnitude < .001f), "ghost line reads the current framed pose during Draw");
            }
            TestMain.Origin = new Vector3d(1000, 0, 0);
            OverlaySample(cargo, out var shifted, out _);
            OverlayCheck(OverlayNear(shifted, TestMain.GlobalFromUnityPos(cargo.Root.transform.position)), "framed reference honors floating origin");
            TestMain.Origin = default;
            cargo.ReceivedStateEpoch = 0;
            OverlayCheck(!OverlaySample(cargo, out _, out _), "old epoch never supplies a ghost"); cargo.ReceivedStateEpoch = 1;
            cargo.PoseLocked = false; cargo.Root.P.physlock = null;
            cargo.Ip = new PoseInterpolator();
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 1, Pos = ReadLocalPos, Rot = ReadLocalRot }, 1.1);
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 1.05, Pos = ReadLocalPos, Rot = ReadLocalRot }, 1.15);
            cargo.Ip.Evaluate(1.19, .02f, out _, out _);
            OverlayCheck(OverlaySample(cargo, out var carried, out _) && OverlayNear(carried, car.Root.transform.TransformPoint(ReadLocalPos)), "nonlocked carried reference uses local interpolator sample");
            OverlayCheck(OverlayMode(cargo) == "in frame / interpolating", "carried mode retains actual interpolation");
            cargo.CarriedBy = 999;
            OverlayCheck(OverlaySample(cargo, out var fallback, out _) && OverlayNear(fallback, cargo.CarriedGlobal), "missing carrier uses retained wire fallback");
            cargo.CarriedBy = 0; cargo.Physical = true;
            OverlayCheck(OverlayMode(cargo) == "physical", "physical mode overrides stale interpolator mode"); cargo.Physical = false;
            cargo.Root.P.physlock = mount;
            OverlayCheck(OverlayMode(cargo) == "rides parent", "container rider mode overrides stale interpolator mode"); cargo.Root.P.physlock = null;
            cargo.Ip = new PoseInterpolator();
            OverlayCheck(OverlayMode(cargo) == "waiting for sample", "unevaluated empty copy cannot claim interpolation");
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 1, Rot = Quaternion.identity }, 1.1);
            cargo.Ip.Evaluate(1.2, .02f, out _, out _);
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 2, Pos = new Vector3d(400, 0, 0), Rot = Quaternion.identity }, 2.1);
            cargo.Ip.Evaluate(2.2, .02f, out _, out _);
            OverlayCheck(cargo.Ip.TelemetryMode == 3 && OverlayMode(cargo) == "snap", "actual interpolator snap mode is labelled snap");
            cargo.TelemetrySnapFrame = Time.frameCount;
            OverlayCheck(OverlayMode(cargo) == "snap", "mover snap frame takes precedence");
            OverlaySendChecks(); OverlayLinkChecks(); OverlayCadenceCheck();
            WorldOverlays.Command("ownership", "off"); WorldOverlays.Command("ghost", "off"); WorldOverlays.Command("links", "off");
            ByNet.Clear(); Server.Clear(); TestMain.s = null;
        }
        static void OverlaySendChecks()
        {
            var part = new Ent { SentAtRest = false };
            part.Root.attachable = new TestAttach { attached = true, parentAtAttach = true };
            part.Root.transform.parent = new Transform();
            OverlayCheck(OverlaySendState(part) == "attached (no states)" && OverlayMode(part) == "rides parent", "bolted copy with fresh interpolator and owner with moving baseline ride parent");
            part.Root.attachable.parentAtAttach = false; part.Root.attachable.isPart = true;
            part.Root.attachable.slot = new TestSlot(); part.Root.attachable.point = new Transform(); part.Root.attachable.point.Add(new TestCar());
            OverlayCheck(OverlaySendState(part) == "attached (no states)" && OverlayMode(part) == "rides parent", "joint-mounted AttachedCarPart is not labelled moving/interpolating");
            part.HitchWaiting = true;
            OverlayCheck(OverlaySendState(part) == "waiting for hitch", "hitch wait precedes other send states");
            foreach (bool ground in new[] { true, false })
            {
                var compact = new Ent(); compact.QuietTail.Active = true;
                if (ground) compact.Root.nyul = new TestRabbit { AI = new TestOldAi { alive = true } };
                else compact.Root.Add(new TestFlyer());
                compact.Root.transform.rotation = Quaternion.Euler(20, 40, 10);
                compact.LastSentRot = Quaternion.Euler(0, compact.Root.transform.rotation.eulerAngles.y, 0);
                OverlayCheck(OverlaySendState(compact) == "quiet tail", "tilted compact ground/flyer compares sent yaw only");
                compact.Root.transform.rotation = Quaternion.Euler(20, 60, 10);
                OverlayCheck(OverlaySendState(compact) == "moving", "compact yaw change still counts");
                compact.Root.transform.rotation = Quaternion.Euler(20, 40, 10); compact.Root.P = new TestPickup { inRightHand = true };
                OverlayCheck(OverlaySendState(compact) == "moving", "held item uses full rotation like sender");
            }
        }
        static void OverlayLinkChecks()
        {
            ByNet.Clear(); Server.Clear(); RemotePlayers.All.Clear();
            TestMain.s.player.transform.position = default; TestMain.s.player.SeatNet = 1;
            for (uint n = 1; n <= 6; n++)
            {
                var e = OverlayEnt(n, 2); e.Root.car = new TestCar();
                for (int i = 0; i < 35; i++)
                {
                    var it = new TestItem(); var slot = new TestSlot();
                    it.attachable = new TestAttach { attached = true, tosave = it, owner = e.Root, slot = slot, transform = it.transform };
                    slot.parts.Add(it.attachable); e.Root.partslotscripts.Add(slot); e.Items.Add(it);
                }
            }
            RemotePlayers.All[3] = new RemotePlayers.Remote { Id = 3, SeatNet = 6, Go = new GameObject() };
            WorldOverlays.Command("links", "on"); Repaint();
            var edges = Snapshot().GetProperty("edges").EnumerateArray().ToArray();
            OverlayCheck(edges.Length == 2 && edges.All(e => e.GetProperty("kind").GetString() == "passenger"), "210 healthy internal parts cannot starve local/remote riders");
            OverlayCheck(edges.Any(e => e.GetProperty("netA").GetUInt32() == 0 && e.GetProperty("netB").GetUInt32() == 6 && e.GetProperty("rider").GetInt32() == 3 && e.GetProperty("bad").GetBoolean()), "bridge identifies one-sided remote rider by session number and vehicle net");
            var first = ByNet[1]; var broken = first.Items[1]; broken.attachable.slot.parts.Clear();
            Repaint(); edges = Snapshot().GetProperty("edges").EnumerateArray().ToArray();
            OverlayCheck(edges.Any(e => e.GetProperty("kind").GetString() == "part" && e.GetProperty("netA").GetUInt32() == 1 && e.GetProperty("netB").GetUInt32() == 1 && e.GetProperty("bad").GetBoolean()), "one-sided same-entity part still drawn red");
            broken.attachable.slot.parts.Add(broken.attachable); broken.attachable.attached = false;
            Repaint();
            OverlayCheck(Snapshot().GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "part" && e.GetProperty("bad").GetBoolean()), "reverse-only slot reference still drawn red");
            broken.attachable.attached = true;
            var detached = OverlayEnt(20, 4); detached.Root = broken; detached.Items.Clear(); detached.Items.Add(broken);
            first.Split = new HashSet<int> { 1 };
            Repaint();
            OverlayCheck(Snapshot().GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("netA").GetUInt32() == 20 && e.GetProperty("netB").GetUInt32() == 1 && e.GetProperty("ownerA").GetInt32() == 4 && e.GetProperty("bad").GetBoolean()), "own root wins over former group in refresh map");
            ByNet.Remove(20);
            Repaint();
            OverlayCheck(Snapshot().GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("netA").GetUInt32() == 0 && e.GetProperty("netB").GetUInt32() == 1), "split member is unshared; refresh map does not retain removed entity");
            var trailer = OverlayEnt(30, 5); trailer.Root.car = new TestCar();
            trailer.Root.attachable = new TestAttach { attached = true, owner = first.Root, tosave = trailer.Root, point = first.Root.transform, utanfuto = new object() };
            Repaint();
            OverlayCheck(Snapshot().GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "hitch" && e.GetProperty("netA").GetUInt32() == 30 && e.GetProperty("netB").GetUInt32() == 1 && e.GetProperty("bad").GetBoolean()), "bridge identifies specific red hitch");
            int measures = GUIStyle.Measures;
            Repaint(.01); Repaint(.01);
            OverlayCheck(GUIStyle.Measures == measures, "ordinary Repaints never remeasure visible row or edge labels");
            WorldOverlays.Command("ownership", "off"); WorldOverlays.Command("ghost", "off");
            Repaint(); measures = GUIStyle.Measures; Repaint();
            OverlayCheck(GUIStyle.Measures == measures, "unchanged link text reuses sizes across refreshes");
            TLDRevamp.SessionLog.AllocCounter = false;
            OverlayCheck(Snapshot().GetProperty("bytesPerRepaint").ValueKind == JsonValueKind.Null, "unsupported allocation counter reports null");
            TLDRevamp.SessionLog.AllocCounter = true; Repaint(.01);
            OverlayCheck(Snapshot().GetProperty("bytesPerRepaint").ValueKind == JsonValueKind.Number, "available allocation counter reports numeric measurement");
            TLDRevamp.SessionLog.AllocCounter = false;
            var before = Snapshot().GetProperty("linkRefreshes").GetInt64();
            WorldOverlays.Command("links", "off"); Repaint();
            OverlayCheck(Snapshot().GetProperty("linkRefreshes").GetInt64() == before, "link discovery does not run with Links off");
            WorldOverlays.Command("links", "on"); WorldOverlays.Command("links", "reset");
            // Exercise the complete production refresh on a larger population; Unity traversal/projection is doubled.
            for (uint n = 100; n < 1800; n++) { var e = OverlayEnt(n); e.Root.transform.position = new Vector3(500, 0, 0); for (int i = 0; i < 3; i++) e.Items.Add(new TestItem()); }
            for (int i = 0; i < 20; i++) Repaint();
            WorldOverlays.Command("links", "reset");
            for (int i = 0; i < 100; i++) Repaint();
            var cost = Snapshot();
            OverlayCheck(cost.GetProperty("linkRefreshes").GetInt64() == 100 && cost.GetProperty("linkMeanMs").GetDouble() > 0, "bridge measures link refresh separately");
            Console.WriteLine("Overlay links: " + cost.GetProperty("linkMeanMs").GetDouble().ToString("F4", CultureInfo.InvariantCulture) + " ms/refresh (" + ByNet.Count + " entities, " + ByNet.Values.Sum(e => e.Items.Count) + " items, .NET 9, Unity/game doubles)");
            WorldOverlays.Command("links", "reset");
            OverlayCheck(Snapshot().GetProperty("linkRefreshes").GetInt64() == 0 && Snapshot().GetProperty("linkMaxMs").GetDouble() == 0, "reset clears link timings");
        }
        static void OverlayCadenceCheck()
        {
            var tail = new BandwidthRules.Tail(); double sent = 0;
            BandwidthRules.StateQuiet(ref tail, 0, true, true, false, false, false, true, false, false, false, 0);
            for (int tick = 1; tick < 100; tick++)
            {
                double now = tick / 20.0;
                var quiet = BandwidthRules.StateQuiet(ref tail, now, false, false, false, false, false, true, false, false, false, now - sent);
                OverlayCheck(!WorldOverlayLogic.Stale(now - sent + .15, true), "real quiet-tail keepalive cadence plus jitter never flickers stale");
                if (quiet == BandwidthRules.Quiet.None) sent = now;
            }
            OverlayCheck(WorldOverlayLogic.Stale(1.51, true), "missing moving stream still becomes stale after margin");
        }
    }
    public static partial class RemotePlayers
    {
        internal class Remote { public GameObject Go; public int Id; public uint SeatNet; public Footing Foot; public Transform SeatedOn; }
        internal struct Footing { public uint Net; }
        internal static readonly Dictionary<int, Remote> All = new Dictionary<int, Remote>();
    }
}

namespace TLDRevamp.Fixes
{
    static class SlotOwnerSave { internal static TestItem Owner(TestAttach at) => at.owner; }
}
namespace TLDRevamp
{
    static class SessionLog { internal static bool AllocCounter; }
    static class Json
    {
        internal static string Str(string s) => JsonSerializer.Serialize(s);
        internal static string Num(double n) => double.IsFinite(n) ? n.ToString(CultureInfo.InvariantCulture) : "null";
    }
}

public partial class TestComponent
{
    public void GetComponentsInChildren<T>(bool inactive, List<T> into) where T : class => into.AddRange(GetComponentsInChildren<T>(inactive));
}
public partial class TestCar : TestComponent { public bool whlocked; public Transform transform = new Transform(); }
public partial class TestPickup : TestComponent { public Camera Cam, mainCam; public uint SeatNet; public bool inInventory; }
public partial class TestMount
{
    public readonly List<TestPickup> stored = new List<TestPickup>();
    public readonly List<TestCar> storedcars = new List<TestCar>();
}
public class TestSlot { public Transform transform = new Transform(); public readonly List<TestAttach> parts = new List<TestAttach>(); }
public partial class TestAttach
{
    public TestItem owner, tosave; public bool parentAtAttach; public object utanfuto; public Transform transform = new Transform();
}
public class TestSpray { public float valve; }
public partial class TestItem
{
    public readonly List<TestSlot> partslotscripts = new List<TestSlot>();
}

namespace UnityEngine
{
    public static partial class Time { public static int frameCount = 10; public static double realtimeSinceStartupAsDouble = 10; }
    public partial class GameObject { public bool activeInHierarchy = true; public Transform transform = new Transform(false); }
    public partial class Transform
    {
        public Transform() { gameObject = new GameObject(); }
        public Transform(bool empty) { gameObject = null; }
    }
    public partial struct Vector3
    {
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
    }
    public partial struct Quaternion
    {
        public Vector3 eulerAngles
        {
            get
            {
                var m = System.Numerics.Matrix4x4.CreateFromQuaternion(N(this));
                return new Vector3(MathF.Asin(Math.Clamp(-m.M32, -1, 1)), MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22)) * (180 / MathF.PI);
            }
        }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
    }
    public struct Bounds { public Vector3 center, extents; public Bounds(Vector3 c, Vector3 size) { center = c; extents = size * .5f; } }
    public class Collider { public Bounds bounds; }
    public class Renderer { public Bounds bounds; }
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public bool Contains(Vector2 p) => p.x >= x && p.x < x + width && p.y >= y && p.y < y + height;
    }
    public class Camera
    {
        public Transform transform = new Transform(); public bool isActiveAndEnabled = true; public float nearClipPlane = .1f;
        public Rect pixelRect = new Rect(0, 0, 1920, 1080);
        public Vector3 WorldToScreenPoint(Vector3 p) => new Vector3(p.x + 500, p.y + 500, p.z + 100);
    }
    public enum EventType { Repaint }
    public class Event { public static Event current; public EventType type; }
    public static class Screen { public static int width = 1920, height = 1080; }
    public class GUIContent { public string text; public GUIContent(string s = null) { text = s; } }
    public class RectOffset { public RectOffset(int a, int b, int c, int d) { } }
    public class GUIStyle
    {
        public static int Measures;
        public int fontSize; public bool richText, wordWrap; public RectOffset padding;
        public GUIStyle normal => this; public Color textColor;
        public GUIStyle() { } public GUIStyle(GUIStyle other) { }
        public Vector2 CalcSize(GUIContent text) { Measures++; return new Vector2(text.text.Length * 6, 20); }
    }
    public static class GUI
    {
        public class Skin { public GUIStyle label = new GUIStyle(); }
        public static Skin skin = new Skin(); public static Color color; public static int depth;
        public static void DrawTexture(Rect r, Texture2D t) { }
        public static void Label(Rect r, GUIContent t, GUIStyle s) { }
    }
    public class Texture2D { public static Texture2D whiteTexture = new Texture2D(); }
    public class Shader { public static Shader Find(string s) => new Shader(); }
    public enum HideFlags { HideAndDontSave }
    public class Material
    {
        public HideFlags hideFlags;
        public Material(Shader s) { } public void SetInt(string s, int v) { } public bool SetPass(int pass) => true;
    }
    public static class GL
    {
        public const int LINES = 1;
        public static readonly List<Vector3> Vertices = new List<Vector3>();
        public static void PushMatrix() { } public static void PopMatrix() { } public static void LoadPixelMatrix(float a, float b, float c, float d) { }
        public static void Begin(int mode) { Vertices.Clear(); } public static void End() { } public static void Color(Color c) { }
        public static void Vertex3(float x, float y, float z) { Vertices.Add(new Vector3(x - 500, Screen.height - y - 500, 0)); }
    }
}
namespace UnityEngine.Rendering
{
    public enum BlendMode { SrcAlpha, OneMinusSrcAlpha }
    public enum CullMode { Off }
    public enum CompareFunction { Always }
}
