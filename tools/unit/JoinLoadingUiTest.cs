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
            Check(r.GetProperty("loadingScreen").GetString() == "revamp", "RevaMP always owns the loading presentation");
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
        Check(GUI.SliceDraws > 0 && JoinLoadingSkin.Body.font == Resources.Liberation && JoinLoadingSkin.Title.font == Resources.Bold, "production draws game sprite slices and separate regular/bold LiberationSans assets");
        Check(JoinLoadingSkin.Title.fontSize == 52 && JoinLoadingSkin.Step.fontSize == 22 && JoinLoadingSkin.Clock.fontSize == 36, "menu typography uses specified 1080p sizes");
        Check(GUI.Labels.Count(x => x.Text == "Joining") == 5 && GUI.Labels.Count(x => x.Text == "00:01") == 1 && GUI.Labels.Count(x => x.Text == "Cancel (Esc)") == 1, "title uses four outline offsets; plaque labels and clock have none");
        Check(!GUI.Labels.Any(x => x.Text.StartsWith("Sample data")), "real join has no sample-data prefix");
        using (var p = Probe()) Check(GUI.Labels.Any(x => x.Text == p.RootElement.GetProperty("reason").GetString() && x.Style == JoinLoadingSkin.Line && !x.Style.wordWrap), "distinct waiting reason stays on one footer line");
        var received = GUI.Labels.First(x => x.Text == "Received"); var stages = GUI.Labels.First(x => x.Text == JoinLoadingState.Names[0]);
        Check(received.Rect.x > stages.Rect.x + 300 && GUI.Labels.Any(x => x.Text == "Multiplayer by RevaMP") && !GUI.Labels.Any(x => x.Text == "RevaMP · Joining"), "production wide layout has right feed and subtle credit without debug header");
        Check(GUI.Labels.Any(x => x.Text.Contains("Terrain 3 / 3")) && GUI.Labels.Any(x => x.Text.StartsWith("Scene loaded · ")) && GUI.Labels.Any(x => x.Text.Contains(" received · ")), "stage details and transfer footer are drawn");
        int measures = GUIStyle.Measures, searches = Resources.Searches; GUI.Clear(); JoinLoading.Draw(); GUI.Clear();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { JoinLoading.Draw(); GUI.Clear(); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated && GUIStyle.Measures == measures && Resources.Searches == searches, "warmed production Draw allocates nothing, performs no asset lookups and never remeasures between refreshes");
        Screen.width = 1800; JoinLoading.Draw(); GUI.Clear(); Screen.width = 1900; JoinLoading.Draw(); GUI.Clear(); Screen.width = 1920;
        Check(GUIStyle.Measures == measures, "resizing cannot rebuild text faster than four times per second");

        MpScreen.PendingQuestion = string.Join(" ", Enumerable.Repeat("Required mod download details", 100));
        Time.realtimeSinceStartupAsDouble = 2; GUI.Clear(); JoinLoading.Draw();
        Check(GUI.Scrolls.Count == 1 && GUI.Scrolls[0].Body > GUI.Scrolls[0].View &&
            GUI.Labels.Any(x => x.Text == "Joining" && !x.Clipped) && GUI.Labels.Any(x => x.Text == "Cancel (Esc)" && !x.Clipped),
            "oversized card scrolls the combined body while header and footer stay visible");
        Screen.width = 360; Screen.height = 240; MpScreen.PendingQuestion = string.Join(" ", Enumerable.Repeat("Required mod download details", 30));
        Time.realtimeSinceStartupAsDouble = 2.3; GUI.Clear(); JoinLoading.Draw();
        stages = GUI.Labels.First(x => x.Text == JoinLoadingState.Names[0]);
        Check(GUI.Scrolls.Any(x => x.Body > x.View) && !GUI.Labels.Any(x => x.Text == "Received"), "short narrow question replaces received and stacked body scrolls");
        Check(GUI.Buttons.Any(x => x.Text == "Download mods") && GUI.Buttons.Any(x => x.Text == "Decline") && GUI.Buttons.Any(x => x.Text == "Cancel (Esc)"), "long mod question retains all actions on small screens");
        Check(GUI.Scrolls.Count == 1 && GUI.Labels.Any(x => x.Text == MpScreen.PendingQuestion && x.Style.wordWrap), "long question shares the card's single scroll fallback");
        foreach (var button in GUI.Buttons) Check(button.Rect.x >= 0 && button.Rect.x + button.Rect.width <= Screen.width, "action stays in narrow width");
        MpScreen.PendingQuestion = null; Time.realtimeSinceStartupAsDouble = 62; GUI.Clear(); JoinLoading.Draw();
        Check(GUI.Buttons.Any(x => x.Text == "Continue anyway"), "stuck continue remains available");
        // Ordinary received block stacks below steps when there is no question.
        received = GUI.Labels.First(x => x.Text == "Received"); stages = GUI.Labels.First(x => x.Text == JoinLoadingState.Names[0]);
        Check(received.Rect.x < stages.Rect.x && received.Rect.y > stages.Rect.y, "narrow layout stacks received under steps");
        JoinLoading.Probe("cancel");
        Check(!JoinLoading.Visible && !JoinLoading.State.Ready && AudioListener.volume == .37f && Session.Left, "cancel through actual Probe restores audio and leaves");
        Preview();
        FallbackAssets();
        Console.WriteLine("PASS JoinLoading UI: " + checks + " checks");
    }
    static void Preview()
    {
        Screen.width = 1920; Screen.height = 1080; GUI.Clear();
        var before = JoinLoading.Probe(); float volume = AudioListener.volume; bool paused = AudioListener.pause;
        bool held = mainscript.s.player.RB.isKinematic;
        var events = UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem { enabled = true };
        Check(JoinLoading.PreviewFromBridge(new[] { "loading", "preview", "3" }).Contains("error") && !JoinLoading.Showing, "preview unavailable without test bridge");
        JoinLoading.Probe("preview"); Check(!JoinLoading.Showing, "normal loading probe never starts preview");
        Plugin.BridgeEnabled = true;
        foreach (var bad in new[] { new[] { "loading", "preview", "-1" }, new[] { "loading", "preview", "7" }, new[] { "loading", "preview", "3", "other" }, new[] { "loading", "preview" } })
            Check(JoinLoading.PreviewFromBridge(bad).Contains("error") && !JoinLoading.Showing, "invalid preview arguments are inert");
        for (int stage = 0; stage <= 6; stage++) foreach (string mode in new[] { "", "question", "stuck" })
        {
            var args = mode == "" ? new[] { "loading", "preview", stage.ToString() } : new[] { "loading", "preview", stage.ToString(), mode };
            Check(JoinLoading.PreviewFromBridge(args).Contains("true") && JoinLoading.Showing && !JoinLoading.Visible, "bridge opens presentation without a join");
            GUI.Clear(); JoinLoading.Tick(); JoinLoading.FixedTick(); JoinLoading.Draw();
            Check(GUI.Labels.Any(x => x.Text.Contains("SAMPLE PREVIEW")) && GUI.Labels.Any(x => x.Text.Contains("Host: Sample host")) && GUI.Labels.Any(x => x.Text == "00:07"), "sample clearly labelled with host and fixed clock");
            Check((mode == "question") == GUI.Buttons.Any(x => x.Text == "Download mods") && (mode == "stuck") == GUI.Buttons.Any(x => x.Text == "Continue anyway"), "preview exposes requested question/stuck actions");
            if (mode != "question") Check(GUI.Labels.Any(x => x.Text == "128 / 342") && JoinLoadingPreview.Names.All(n => GUI.Labels.Any(x => x.Text == n)), "preview received block includes fixed totals and six names");
            Check(GUI.Scrolls.Count == 0 && JoinLoadingState.Names.All(n => GUI.Labels.Any(x => x.Text == n && !x.Clipped)), "all seven steps fit 1080p without any scroll view");
            Check(GUI.Labels.All(x => !x.Clipped), $"1080p stage {stage} {mode} text stays inside its clip bounds: " + string.Join(", ", GUI.Labels.Where(x => x.Clipped).Select(x => x.Text)));
            Check(!GUI.Labels.Any(x => x.Text.StartsWith("Sample data")) && GUI.Labels.Count(x => x.Text == JoinLoadingPreview.Details[stage]) == 1, "repeated current-step detail is absent from the footer");
            if (mode != "question") Check(JoinLoadingPreview.Names.All(n => GUI.Labels.Any(x => x.Text == n && !x.Clipped)), "all six latest names fit inside the paper");
            if (stage == 4 && mode == "") LayoutPolish();
            if (stage >= 3) Check(GUI.Labels.Any(x => x.Text.Contains("Terrain 46 / 64")), "sample terrain and road counts present");
            Check(JoinLoading.Probe() == before && AudioListener.volume == volume && AudioListener.pause == paused && events.enabled && held == mainscript.s.player.RB.isKinematic, "preview never changes live probe, audio, input or placement");
        }
        int measures = GUIStyle.Measures; GUI.Clear(); JoinLoading.Draw(); GUI.Clear();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { JoinLoading.Draw(); GUI.Clear(); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated && GUIStyle.Measures == measures, "preview also allocates nothing between refreshes");
        JoinLoading.PreviewFromBridge(new[] { "loading", "preview", "off" });
        Check(!JoinLoading.Showing && JoinLoading.Probe() == before, "preview off restores presentation only");
        JoinLoading.Begin();
        Check(JoinLoading.PreviewFromBridge(new[] { "loading", "preview", "3" }).Contains("error") && !JoinLoadingPreview.Active, "preview cannot replace a real join");
        JoinLoading.End("cancelled");
    }
    static void LayoutPolish()
    {
        var rows = JoinLoadingState.Names.Select(n => GUI.Labels.Single(x => x.Text == n)).ToArray();
        var detail = GUI.Labels.Single(x => x.Text == JoinLoadingPreview.Details[4]);
        Check(rows[5].Rect.y - rows[4].Rect.y == rows[4].Rect.height + detail.Rect.height + 8 &&
            rows[6].Rect.y - rows[5].Rect.y == rows[5].Rect.height + 8, "drawn row pitch includes detail only when visible");
        var transfer = GUI.Labels.Single(x => x.Text == "2.41 MB received · 312 KB/s");
        var percent = GUI.Labels.Single(x => x.Text == "67 %");
        var bar = GUI.Textures.Where(x => x.Tint.r == 0x40/255f && x.Tint.g == 0x40/255f && x.Tint.b == 0x40/255f && x.Tint.a == 1).OrderByDescending(x => x.Rect.width).First();
        Check(transfer.Rect.y == percent.Rect.y && percent.Rect.x + percent.Rect.width == bar.Rect.x + bar.Rect.width + 1 &&
            transfer.ScreenRect.y > bar.ScreenRect.y + bar.Rect.height, "percent and transfer share the line under the bar, percent aligns right");
        var latest = GUI.Labels.Single(x => x.Text == "Jerrycan");
        Check(latest.ScreenRect.y + latest.Rect.height < transfer.ScreenRect.y && !latest.Clipped, "last latest name clears the footer");
        var count = GUI.Labels.Single(x => x.Text == "132");
        Check(count.Style.alignment == TextAnchor.UpperRight && count.Rect.x + count.Rect.width == latest.Rect.x + latest.Rect.width, "right-column counts align to its inner edge");
        var rule = GUI.Textures.Single(x => x.Tint.r == 0x40/255f && x.Tint.a == .4f && x.Rect.width < x.Rect.height);
        Check(rule.Rect.y == 0 && rule.Rect.height == Math.Max(rows[6].Rect.y + rows[6].Rect.height + 8, latest.Rect.y + latest.Rect.height), "column rule spans the full measured body");
        foreach (var text in new[] { "Joining" })
        {
            var labels = GUI.Labels.Where(x => x.Text == text).ToArray(); var face = labels.Last();
            Check(labels.Take(4).All(x => Math.Abs(x.Rect.x - face.Rect.x) + Math.Abs(x.Rect.y - face.Rect.y) == 1 &&
                x.Tint.r == 0x0E/255f && x.Tint.g == 0x11/255f && x.Tint.b == 0x0E/255f), "one-pixel four-direction dark outlines");
        }
        var currentDots = GUI.Textures.Where(x => x.Rect.width == 4 && x.Rect.height == 4).ToArray();
        var pendingDots = GUI.Textures.Where(x => x.Rect.width == 6 && x.Rect.height == 6).ToArray();
        Check(currentDots.Length == 3 && currentDots.All(x => x.Rect.y + 2 == rows[4].Rect.y + JoinLoadingSkin.Step.lineHeight / 2) &&
            pendingDots.Length == 2 && pendingDots[0].Rect.y + 3 == rows[5].Rect.y + JoinLoadingSkin.Step.lineHeight / 2, "dots centre on the step-name line");
        Check(!GUI.Textures.Any(x => x.Tint.r == 0 && x.Tint.g == 0 && x.Tint.b == 0 && x.Tint.a == .35f), "paper has no rectangular drop shadow");
    }
    static void FallbackAssets()
    {
        // Recreate the cached skin as if this build had neither menu sprites nor Liberation fonts.
        Resources.Sprites = Array.Empty<Sprite>(); Resources.Fonts = Array.Empty<Font>();
        var fields = typeof(JoinLoadingSkin).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            if (field.FieldType == typeof(GUIStyle) || field.FieldType == typeof(Sprite) || field.FieldType == typeof(Font)) field.SetValue(null, null);
            if (field.Name == "_height") field.SetValue(null, -1);
        }
        JoinLoading.PreviewFromBridge(new[] { "loading", "preview", "4", "question" });
        int slices = GUI.SliceDraws; GUI.Clear(); JoinLoading.Draw();
        Check(GUI.SliceDraws == slices && JoinLoadingSkin.Body.font.name == "Arial", "missing menu assets use flat bordered rectangles and legacy Arial");
        Check(GUI.Textures.Count(x => x.Texture == Texture2D.whiteTexture && x.Tint.r == 0xC8/255f && x.Tint.g == 1f && x.Tint.b == 0xC8/255f) >= 4, "paper and plaques use the pale green palette tint");
        Check(GUI.Buttons.Count == 3 && GUI.Labels.Any(x => x.Text == "Joining"), "fallback retains title and all question actions");
        foreach (var label in GUI.Labels) Check(label.Rect.x == Math.Round(label.Rect.x) && label.Rect.y == Math.Round(label.Rect.y) && label.Rect.width == Math.Round(label.Rect.width) && label.Rect.height == Math.Round(label.Rect.height), "text rectangles pixel snapped");
        JoinLoading.PreviewFromBridge(new[] { "loading", "preview", "off" });
        string bridge = System.IO.File.ReadAllText("src/TLDRevamp/DebugBridge.cs");
        string plugin = System.IO.File.ReadAllText("src/TLDRevamp/Plugin.cs");
        Check(bridge.Contains("ma[1] == \"preview\") return Net.JoinLoading.PreviewFromBridge(ma)") && plugin.Contains("if (Net.JoinLoading.Showing) { Net.JoinLoading.Draw(); return; }"), "bridge routes preview and runner suppresses other overlays while showing");
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
    public enum EventType { Repaint, Layout, MouseDown, MouseUp }
    public class Event { public static Event current = new Event(); public EventType type; public EventType rawType; public bool isMouse; public Vector2 mousePosition; public void Use() { } }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
    public static class Mathf { public static float Min(float a, float b) => Math.Min(a,b); public static float Clamp01(float n) => Math.Clamp(n,0,1); }
    public static class GUI
    {
        public static GUISkin skin = new GUISkin(); public static Color color = Color.white, backgroundColor; public static int depth; public static Matrix4x4 matrix;
        public static readonly List<(string Text, Rect Rect, GUIStyle Style, Color Tint, Rect ScreenRect, bool Clipped)> Labels = new List<(string,Rect,GUIStyle,Color,Rect,bool)>(256);
        public static readonly List<(string Text, Rect Rect)> Buttons = new List<(string,Rect)>(16);
        public static readonly List<(Rect Rect, Texture2D Texture, Color Tint, Rect ScreenRect)> Textures = new List<(Rect,Texture2D,Color,Rect)>(256);
        public static readonly List<(float View, float Body)> Scrolls = new List<(float,float)>(16);
        public static (float Width,float Height,float Alpha,int Depth,bool IdentityMatrix) Background;
        static Vector2 offset; static Rect clip = new Rect(0, 0, Screen.width, Screen.height);
        static readonly (Vector2 Offset, Rect Clip)[] contexts = new (Vector2,Rect)[8]; static int context;
        public static void Clear() { Labels.Clear(); Buttons.Clear(); Scrolls.Clear(); Textures.Clear(); context = 0; offset = default; clip = new Rect(0, 0, Screen.width, Screen.height); }
        static Rect Absolute(Rect r) => new Rect(r.x + offset.x, r.y + offset.y, r.width, r.height);
        public static void Label(Rect r, GUIContent c, GUIStyle style)
        {
            var a = Absolute(r);
            Labels.Add((c.text,r,style,color,a,a.x < clip.x || a.y < clip.y || a.x + a.width > clip.x + clip.width || a.y + a.height > clip.y + clip.height));
        }
        public static bool Button(Rect r, string text, GUIStyle style) { Buttons.Add((text,r)); return false; } public static bool Button(Rect r, GUIContent text, GUIStyle style) => Button(r, text.text, style);
        public static void DrawTexture(Rect r, Texture2D texture) { Textures.Add((r,texture,color,Absolute(r))); if (texture.Height == 256) Background = (r.width,r.height,texture.MinAlpha*color.a,depth,matrix.Marker == 0); }
        public static int SliceDraws;
        public static void DrawTextureWithTexCoords(Rect r, Texture2D texture, Rect uv) { SliceDraws++; }
        public static string TextArea(Rect r, string value, int limit, GUIStyle style) => value;
        public static void BeginGroup(Rect r)
        {
            contexts[context++] = (offset,clip); var a = Absolute(r);
            float left = Math.Max(clip.x,a.x), top = Math.Max(clip.y,a.y);
            clip = new Rect(left,top,Math.Max(0,Math.Min(clip.x+clip.width,a.x+a.width)-left),Math.Max(0,Math.Min(clip.y+clip.height,a.y+a.height)-top));
            offset = new Vector2(a.x,a.y);
        }
        public static void EndGroup() { var c = contexts[--context]; offset = c.Offset; clip = c.Clip; }
        public static Vector2 BeginScrollView(Rect r, Vector2 scroll, Rect body) { Scrolls.Add((r.height,body.height)); BeginGroup(r); offset = new Vector2(offset.x-scroll.x,offset.y-scroll.y); return scroll; }
        public static void EndScrollView() => EndGroup();
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
class menuhandler { public static menuhandler s = new menuhandler(); public Map currentMainMap = new Map(); }
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
    static class Plugin { public static bool BridgeEnabled; public static LogSink Log = new LogSink(); }
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
