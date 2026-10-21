using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TLDRevamp;
using TLDRevamp.Net;
using UnityEngine;

static class JoinLoadingUiTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static JsonDocument Probe() => JsonDocument.Parse(JoinLoading.Probe());
    static void Tick(double now) { Time.realtimeSinceStartupAsDouble = now; JoinLoading.Tick(); }
    static void Main()
    {
        var c = Mp.Client = new MpClient { ConnectedToHost = true, MyId = 1, HostName = "Host", WorldLoaded = true, SpotPlaced = true };
        var terrain = menuhandler.s.currentMainMap.terrainGen[0];
        terrain.coordsNeeded.AddRange(new[] { 1, 2, 3 }); terrain.hasCollider = true;
        terrain.terrains[1] = new Tile(); terrain.terrains[2] = new Tile { mc = null }; // missing collider
        var road = menuhandler.s.currentMainMap.roadGens[0]; road.chunksNeeded.AddRange(new[] { 1, 2 }); road.Done.Add(1);
        AudioListener.volume = .37f; JoinLoading.Begin(); c.T.BytesIn = 2500; Tick(1);
        using (var p = Probe())
        {
            var r = p.RootElement;
            Check(r.GetProperty("stage").GetInt32() == 3 && !r.GetProperty("geometry").GetBoolean(), "geometry still gates stage 3");
            Check(r.GetProperty("terrainDone").GetInt32() == 1 && r.GetProperty("terrainNeeded").GetInt32() == 3 && r.GetProperty("roadsDone").GetInt32() == 1 && r.GetProperty("roadsNeeded").GetInt32() == 2, "production scan counts missing tiles and colliders separately from roads");
            Check(r.GetProperty("bytesReceived").GetInt32() == 2500 && r.GetProperty("receiveBytesPerSecond").GetDouble() == 2500 && r.GetProperty("route").GetString() == "relay" && r.GetProperty("pingMs").GetInt32() == 34, "actual adapter samples transport and known route/ping");
            foreach (string key in new[] { "visible", "stage", "stages", "muted", "audioVolume", "ready", "reason", "scene", "geometry", "ground", "initialSync", "hostName", "versionCheck", "worldActivity", "placementStatus", "loadingMod", "loadingModBytes", "loadingModTotal", "geometrySampled", "expectedObjects", "manifestReceived", "initialRecordsEnded", "progressPercent", "elapsedMs", "recentObjects", "receivedVehicles", "receivedItemsParts", "receivedOther", "loadingScreen" })
                Check(r.TryGetProperty(key, out _), "full production Probe retains/adds " + key);
            Check(r.GetProperty("loadingScreen").GetString() == "fallback", "missing game canvas selects fallback");
            Check(r.GetProperty("stages").GetArrayLength() == 7 && r.GetProperty("stages")[4].GetProperty("count").GetInt32() == 0 && r.GetProperty("muted").GetBoolean() && r.GetProperty("audioVolume").GetDouble() == 0, "legacy schema and audio semantics");
        }
        terrain.terrains[2] = new Tile(); terrain.terrains[3] = new Tile(); road.Done.Add(2); Tick(1.3);
        Check(JoinLoading.State.Stage == 4 && JoinLoading.GeometryReady, "mesh/collider/chunk completion advances unchanged gate");
        var w = new NetWriter(); w.U8(0); w.VarU32(12); for (uint id = 1; id <= 12; id++) w.U32(id);
        JoinLoading.Manifest(new NetReader { Buf = w.Buf, End = w.Len });
        for (uint id = 1; id <= 12; id++) JoinLoading.Receipt(id, id == 12 ? 999 : (int)id, (JoinObjectKind)(id % 3));
        JoinLoading.Receipt(12345, 1, JoinObjectKind.Vehicle); // not in manifest
        Entities.Applied = 2; c.T.BytesIn = 5000; Tick(1.7);
        using (var p = Probe())
        {
            var r = p.RootElement; var feed = r.GetProperty("recentObjects");
            Check(r.GetProperty("receivedObjects").GetInt32() == 12 && r.GetProperty("expectedObjects").GetInt32() == 12 && r.GetProperty("stages")[4].GetProperty("count").GetInt32() == 2, "received and applied counts are distinct and manifest-scoped");
            Check(feed.GetArrayLength() == 6 && feed[0].GetProperty("net").GetInt32() == 12 && feed[0].GetProperty("name").GetString() == "Shared object" && feed[1].GetProperty("name").GetString() == "Prefab 11", "feed names use safe content boundary and generic unknown fallback");
        }
        Event.current.type = EventType.Repaint; GUI.color = new Color(.2f,.3f,.4f,.2f); GUI.depth = 42; GUI.matrix = new Matrix4x4 { Marker = 42 };
        JoinLoading.Draw();
        Check(GUI.Background.Width == Screen.width && GUI.Background.Height == Screen.height && GUI.Background.Alpha == 1 && GUI.Background.Depth == -10000 && GUI.Background.IdentityMatrix, "backdrop covers full screen opaquely at front depth despite caller tint/matrix");
        Check(GUI.color.a == .2f && GUI.depth == 42 && GUI.matrix.Marker == 42, "draw restores caller GUI state");
        Check(GUI.SliceDraws > 0 && JoinLoadingSkin.Body.font == Resources.Liberation, "production draws game sprite slices with available LiberationSans font");
        var received = GUI.Labels.First(x => x.Text == "Received"); var stages = GUI.Labels.First(x => x.Text == JoinLoadingState.Names[0]);
        Check(received.Rect.x > stages.Rect.x + 300 && GUI.Labels.Any(x => x.Text == "Multiplayer · RevaMP") && !GUI.Labels.Any(x => x.Text == "RevaMP · Joining"), "production wide layout has right feed and subtle credit without debug header");
        Check(GUI.Labels.Any(x => x.Text.Contains("Terrain tiles 3 / 3")) && GUI.Labels.Any(x => x.Text.Contains("scene loaded: yes")) && GUI.Labels.Any(x => x.Text.Contains("MB received")), "stage details and transfer footer are drawn");
        int measures = GUIStyle.Measures; GUI.Clear(); JoinLoading.Draw(); GUI.Clear();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { JoinLoading.Draw(); GUI.Clear(); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated && GUIStyle.Measures == measures, "warmed production Draw allocates nothing and never remeasures text between refreshes");
        Screen.width = 1800; JoinLoading.Draw(); GUI.Clear(); Screen.width = 1900; JoinLoading.Draw(); GUI.Clear(); Screen.width = 1920;
        Check(GUIStyle.Measures == measures, "resizing cannot rebuild text faster than four times per second");

        Screen.width = 360; Screen.height = 240; MpScreen.PendingQuestion = string.Join(" ", Enumerable.Repeat("Required mod download details", 30));
        Time.realtimeSinceStartupAsDouble = 2.1; GUI.Clear(); JoinLoading.Draw();
        received = GUI.Labels.First(x => x.Text == "Received"); stages = GUI.Labels.First(x => x.Text == JoinLoadingState.Names[0]);
        Check(received.Rect.x < stages.Rect.x && received.Rect.y > stages.Rect.y && GUI.Scrolls.Any(x => x.Body > x.View), "short narrow production view stacks and scrolls");
        Check(GUI.Buttons.Any(x => x.Text == "Download mods") && GUI.Buttons.Any(x => x.Text == "Decline") && GUI.Buttons.Any(x => x.Text == "Cancel (Esc)"), "long mod question retains all actions on small screens");
        Check(GUI.Scrolls.Count >= 2 && GUI.Labels.Any(x => x.Text == MpScreen.PendingQuestion && x.Style.wordWrap), "mod question wraps and has separate scroll fallback");
        foreach (var button in GUI.Buttons) Check(button.Rect.x >= 0 && button.Rect.x + button.Rect.width <= Screen.width, "action stays in narrow width");
        MpScreen.PendingQuestion = null; Time.realtimeSinceStartupAsDouble = 62; GUI.Clear(); JoinLoading.Draw();
        Check(GUI.Buttons.Any(x => x.Text == "Continue anyway"), "stuck continue remains available");
        JoinLoading.Probe("cancel");
        Check(!JoinLoading.Visible && !JoinLoading.State.Ready && AudioListener.volume == .37f && Session.Left, "cancel through actual Probe restores audio and leaves");
        NativeLease(c);
        Console.WriteLine("PASS JoinLoading UI: " + checks + " checks");
    }
    static GameObject Child(GameObject parent, string name, params Type[] types)
    {
        var obj = new GameObject(name, types); obj.transform.SetParent(parent.transform, false); return obj;
    }
    static void NativeLease(MpClient c)
    {
        Screen.width = 1920; Screen.height = 1080;
        var canvas = new GameObject("Canvas", typeof(Canvas));
        var menu = Child(canvas, "UIOVERLAY"); menuhandler.s.gameObject = menu;
        var layer = Child(menu, "2"); var root = Child(layer, "TextLoad");
        var image = Child(root, "Image", typeof(UnityEngine.UI.Image));
        var title = Child(root, "Text (TMP)", typeof(TMPro.TextMeshProUGUI));
        var bar = Child(root, "LoadRoadBar", typeof(UnityEngine.UI.Slider));
        var slider = bar.GetComponent<UnityEngine.UI.Slider>(); slider.minValue = 10; slider.maxValue = 110; slider.value = 66;
        var targets = new[] { canvas, menu, layer, root, image, title, bar };
        var original = new[] { true, true, false, false, false, true, false };
        for (int i = 0; i < targets.Length; i++) targets[i].SetActive(original[i]);
        foreach (string close in new[] { "cancel", "continue", "ready", "disconnect", "error" })
        {
            c.Lost = false; Entities.Applied = 0; JoinLoading.Begin();
            using (var p = Probe()) Check(p.RootElement.GetProperty("loadingScreen").GetString() == "native", "complete game screen selected");
            Check(targets.All(x => x.activeInHierarchy) && title.GetComponent<TMPro.TextMeshProUGUI>().text == "Localized loading", "lease enables native ancestry and retains localized title");
            var backdrop = root.transform.Children[0].gameObject;
            Check(backdrop.GetComponent<UnityEngine.UI.Image>().color.a == 1 && !backdrop.GetComponent<UnityEngine.UI.Image>().raycastTarget, "native first-child backdrop is opaque and does not intercept input");
            Check(backdrop.GetComponent<CanvasGroup>().ignoreParentGroups && backdrop.GetComponent<CanvasGroup>().alpha == 1, "opaque native backdrop ignores inherited fades");
            JoinLoadingNative.Update(.6f); JoinLoadingNative.Update(.2f);
            Check(slider.value == 70, "real native Slider maps range and never reverses");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) JoinLoadingNative.Update(.6f);
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "warmed native updates allocate nothing");
            Tick(Time.realtimeSinceStartupAsDouble + .3); Tick(Time.realtimeSinceStartupAsDouble + .3);
            Check(Math.Abs(slider.value - (10 + JoinLoading.State.Stage / 6f * 100)) < .01f, "actual adapter drives native road bar with overall stage progress");
            GUI.Clear(); JoinLoading.Draw();
            Check(GUI.Labels.All(x => x.Text != "Loading") && GUI.Labels.Any(x => x.Text == "Received"), "native localized title is not replaced by fallback title; detail remains");
            GUI.Clear(); allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { JoinLoading.Draw(); GUI.Clear(); }
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "warmed native presentation draws allocate nothing");
            if (close == "continue") { Time.realtimeSinceStartupAsDouble += 61; JoinLoading.Probe("continue"); }
            else if (close == "ready")
            {
                var manifest = new NetWriter(); manifest.U8(0); manifest.VarU32(0);
                JoinLoading.Manifest(new NetReader { Buf = manifest.Buf, End = manifest.Len });
                var end = new NetWriter(); end.U8(1);
                JoinLoading.Manifest(new NetReader { Buf = end.Buf, End = end.Len });
                Tick(Time.realtimeSinceStartupAsDouble + .3);
                Tick(Time.realtimeSinceStartupAsDouble + .6);
                Check(JoinLoading.State.Ready && !JoinLoading.Visible, "actual ready transition closes native lease");
            }
            else if (close == "disconnect") { c.Lost = true; Tick(Time.realtimeSinceStartupAsDouble + .3); }
            else JoinLoading.End(close);
            for (int i = 0; i < targets.Length; i++) Check(targets[i].activeSelf == original[i], close + " restores exact original active state");
            Check(slider.value == 66 && !backdrop.activeSelf, close + " restores slider and removes opaque lease");
            using (var p = Probe()) Check(p.RootElement.GetProperty("loadingScreen").GetString() == "native", "Probe retains last presentation choice after close");
            JoinLoadingNative.Close(); Check(slider.value == 66, "close is idempotent");
        }
        c.Lost = false; JoinLoading.Begin(); image.GetComponent<UnityEngine.UI.Image>().enabled = false;
        JoinLoadingNative.Update(.2f);
        Check(!JoinLoadingNative.Used && !JoinLoadingNative.Active, "native component disappearing switches to fallback");
        for (int i = 0; i < targets.Length; i++) Check(targets[i].activeSelf == original[i], "fallback releases all captured states");
        image.GetComponent<UnityEngine.UI.Image>().enabled = true; JoinLoading.End("cancelled");
        root.name = "OtherBuild"; c.Lost = false; JoinLoading.Begin();
        using (var p = Probe()) Check(p.RootElement.GetProperty("loadingScreen").GetString() == "fallback", "different-build path chooses fallback without mutating native objects");
        JoinLoading.End("cancelled");
    }
}

namespace UnityEngine
{
    public struct Matrix4x4 { public int Marker; public static Matrix4x4 identity => default; }
    public struct Vector3 { public float x,y,z; public static Vector3 zero => default; }
    public class Rigidbody { public bool isKinematic; public Vector3 velocity, angularVelocity; }
    public enum CursorLockMode { None }
    public static class Cursor { public static bool visible; public static CursorLockMode lockState; }
    public static class AudioListener { public static float volume; public static bool pause; }
    public enum KeyCode { Escape }
    public static class Input { public static bool GetKeyDown(KeyCode key) => false; }
    public enum EventType { Repaint, Layout }
    public class Event { public static Event current = new Event(); public EventType type; public bool isMouse; public Vector2 mousePosition; public void Use() { } }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
    public static class Mathf { public static float Min(float a, float b) => Math.Min(a,b); public static float Clamp01(float n) => Math.Clamp(n,0,1); }
    public static class GUI
    {
        public static GUISkin skin = new GUISkin(); public static Color color = Color.white, backgroundColor; public static int depth; public static Matrix4x4 matrix;
        public static readonly List<(string Text, Rect Rect, GUIStyle Style)> Labels = new List<(string,Rect,GUIStyle)>(256);
        public static readonly List<(string Text, Rect Rect)> Buttons = new List<(string,Rect)>(16);
        public static readonly List<(float View, float Body)> Scrolls = new List<(float,float)>(16);
        public static (float Width,float Height,float Alpha,int Depth,bool IdentityMatrix) Background;
        public static void Clear() { Labels.Clear(); Buttons.Clear(); Scrolls.Clear(); }
        public static void Label(Rect r, GUIContent c, GUIStyle style) => Labels.Add((c.text,r,style));
        public static bool Button(Rect r, string text, GUIStyle style) { Buttons.Add((text,r)); return false; }
        public static void DrawTexture(Rect r, Texture2D texture) { if (texture.Height == 64) Background = (r.width,r.height,texture.MinAlpha*color.a,depth,matrix.Marker == 0); }
        public static int SliceDraws;
        public static void DrawTextureWithTexCoords(Rect r, Texture2D texture, Rect uv) { SliceDraws++; }
        public static string TextArea(Rect r, string value, int limit, GUIStyle style) => value;
        public static void BeginGroup(Rect r) { } public static void EndGroup() { }
        public static Vector2 BeginScrollView(Rect r, Vector2 scroll, Rect body) { Scrolls.Add((r.height,body.height)); return scroll; }
        public static void EndScrollView() { }
    }
}
namespace UnityEngine.EventSystems { public class EventSystem { public static EventSystem current; public bool enabled; } }
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
    class HarmonyPrefix : Attribute { }
}
namespace Steamworks { public struct CSteamID { public CSteamID(ulong id) { } } }
struct Vector3d { public double x,y,z; public double magnitude => Math.Sqrt(x*x+y*y+z*z); public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d {x=a.x-b.x,y=a.y-b.y,z=a.z-b.z}; }
class fpscontroller
{
    public void Move() { } public Rigidbody RB = new Rigidbody(); public Transform transform = new Transform(); public bool loadedmove = true, godModeTillGrounded;
}
class mainscript { public static mainscript s = new mainscript(); public fpscontroller player = new fpscontroller(); }
class menuhandler : Component { public static menuhandler s = new menuhandler { gameObject = new GameObject("UIOVERLAY") }; public Map currentMainMap = new Map(); }
class Map { public List<TerrainGen> terrainGen = new List<TerrainGen> { new TerrainGen() }; public List<RoadGen> roadGens = new List<RoadGen> { new RoadGen() }; }
class TerrainGen { public List<int> coordsNeeded = new List<int>(); public Dictionary<int,Tile> terrains = new Dictionary<int,Tile>(); public bool hasCollider; }
class RoadGen { public List<int> chunksNeeded = new List<int>(); public HashSet<int> Done = new HashSet<int>(); public bool ChunkDone(int key) => Done.Contains(key); }
class Tile { public MeshPart mf = new MeshPart(), mc = new MeshPart(); }
class MeshPart { public object sharedMesh = new object(); }
namespace TLDRevamp
{
    static class Json { public static string Str(string s) => JsonSerializer.Serialize(s ?? ""); public static string Num(double n) => n.ToString("F3",CultureInfo.InvariantCulture); }
    static class SessionLog { public static void LoadingStage(int stage,double ms) { } }
    static class TelemetryContent { public static string ItemName(int prefab) => prefab == 999 ? "[removed]" : "Prefab " + prefab; }
    static class TelemetryPrivacy { public const string Removed = "[removed]"; }
    static class Plugin { public static LogSink Log = new LogSink(); }
    class LogSink { public void LogError(string text) { throw new Exception(text); } public void LogWarning(string text) { throw new Exception(text); } }
    static class DebugBridge
    {
        public static bool TpBusy;
        public struct Hit { public Vector3 point; public Collider collider; }
        public class Collider { public Rigidbody attachedRigidbody; }
        static readonly Collider Ground = new Collider();
        public static Hit? StandHit(float x,float z,float y) => new Hit { collider = Ground };
        public static bool TpaWorldReady(double x,double y,double z) => true;
        public static void CancelJoinPlacement() { } public static void PlaceAt(double x,double z) { }
    }
}
namespace TLDRevamp.Net
{
    static class Session { public static ulong LastJoinSteamId; public static bool Left; public static string EndReason; public static string Persona(Steamworks.CSteamID id) => "Host"; public static void Leave() => Left = true; public static void Show(int n,string a,string b) { } }
    static class MpScreen { public static string PendingQuestion; public static void AskProbe(string answer) { } }
    static class Mp { public static MpClient Client; public static Vector3d LocalGlobal() => default; }
    class MpClient { public Transport T = new Transport(); public bool ConnectedToHost, WorldLoaded, SpotPlaced, Lost; public int MyId; public string RejectReason, HostName, LoadingMod; public uint LoadingModBytes, LoadingModTotal; }
    class Transport { public List<int> Connections = new List<int> {1}; public long BytesIn; public Quality Status(int conn) => new Quality {Ping=34}; }
    struct Quality { public int Ping; }
    static class SteamCompat { public static string Route(int conn) => "{\"ok\":true,\"relayed\":true}"; }
    static class Entities { public static bool TpaInboxEmpty = true; public static int Applied; public static int JoinApplied(HashSet<uint> ids) => Applied; }
}
