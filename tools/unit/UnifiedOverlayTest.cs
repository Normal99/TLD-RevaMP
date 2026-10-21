global using mainscript = OverlayGame;
using System;
using System.Linq;
using TLDRevamp;
using UnityEngine;

static class UnifiedOverlayTest
{
    private static int _checks;
    private static void Check(bool ok, string why) { _checks++; if (!ok) throw new Exception(why); }
    static void Main()
    {
        Event.current.type = EventType.Repaint; OverlayStyle.Ensure();
        Check(OverlayStyle.Button.normal.background != OverlayStyle.Button.hover.background && OverlayStyle.Button.hover.background != OverlayStyle.Button.active.background, "button states have distinct fills");
        OverlayStyle.Click(new Rect(0, 0, 180, OverlayStyle.ButtonHeight), "Save");
        Check(GUI.ButtonBackground.r == 1 && OverlayStyle.ButtonHeight == 28, "button textures are untinted and height is shared");

        var bar = new CompactOverlay(); bar.Measure("RevaMP · Recording repro", "Recording actions");
        Check(bar.Width < 400 && bar.Height < 40, "recording is a text-sized single-line bar");
        GUI.Labels.Clear(); bar.Draw(new DebugBox(100, 100, bar.Width, bar.Height), OverlayKind.Warning);
        Check(GUI.Labels.Count == 2 && GUI.Labels[0].Rect.y == GUI.Labels[1].Rect.y && GUI.Labels[0].Rect.x + GUI.Labels[0].Rect.width < GUI.Labels[1].Rect.x, "accent title and status share a row with a gap");
        var pill = new CompactOverlay(); pill.Measure("RevaMP · 9 more labels hidden");
        Check(pill.Width < 240 && pill.Height < 40, "hidden-label notice is a small pill");
        var network = new CompactOverlay(); network.Measure("RevaMP · Network conditions", "Simulated network", "Host: all client links · relays impaired twice");
        Check(network.Height > bar.Height && network.Height < 65 && network.Width < 500, "network detail adds exactly one text row");
        int measures = GUIStyle.Measures;
        for (int i = 0; i < 100; i++) bar.Measure("RevaMP · Recording repro", "Recording actions");
        Check(GUIStyle.Measures == measures, "unchanged bars reuse all measurements");

        var widths = new float[] { 25, 36, 47, 29, 33 }; var left = new float[5]; var right = new float[5];
        float name = OverlayTableLogic.Columns(480, 10, widths, left, right);
        Check(name == 260 && right[4] == 480 && left[0] == 270 && right[0] == 295, "packet column coordinates derive from measured numeric widths");
        for (int i = 1; i < 5; i++) Check(left[i] == right[i - 1] + 10, "packet columns have fixed nonoverlapping gaps");
        name = OverlayTableLogic.Columns(80, 10, widths, left, right);
        Check(name == 0 && Math.Abs(right[4] - 80) < .001 && left.All(x => x >= 0), "narrow tables stay inside available width");

        var table = new PacketTable(); table.Rebuild(500, true);
        Check(table.Height == OverlayStyle.PanelHeight(3 * OverlayStyle.Small.lineHeight), "packet panel fits summary, header and row without a trailing blank line");
        GUI.Labels.Clear(); table.Draw(new DebugBox(12, 12, 500, table.Height));
        var headings = GUI.Labels.Where(x => x.Style == OverlayStyle.Numeric).ToArray();
        Check(headings.Length == 10 && headings.All(x => x.Style.alignment == TextAnchor.UpperRight), "numeric packet cells use right-aligned styles");
        for (int i = 0; i < 5; i++) Check(headings[i].Rect.x == headings[i + 5].Rect.x && headings[i].Rect.width == headings[i + 5].Rect.width, "packet header and data share column positions");
        table.Rebuild(500, false);
        Check(table.Height == OverlayStyle.PanelHeight(OverlayStyle.Small.lineHeight), "no-session packets fit one line");

        var join = JoinDialogLayout.Measure(28, 10, 8, 36, 20, 28, 7, 20, 0, 0, 28);
        Check(join.ButtonsY == join.DetailY + 28 && join.Height == 390, "join Cancel follows the measured status with one gap");
        var question = JoinDialogLayout.Measure(28, 10, 8, 36, 20, 28, 7, 20, 56, 0, 28);
        Check(question.Height - join.Height == 100 && question.QuestionButtonsY + 36 == question.ButtonsY, "mod question adds its measured text, actions and gaps");
        var stuck = JoinDialogLayout.Measure(28, 10, 8, 36, 20, 28, 7, 20, 0, 40, 28);
        Check(stuck.Height - join.Height == 48, "stuck hint adds only measured text and a gap");

        TLDRevamp.Net.Banner.Show(10, "Host restarting in 5 minutes");
        float bannerHeight = TLDRevamp.Net.Banner.Measure();
        Check(bannerHeight == OverlayStyle.PanelHeight(OverlayStyle.Title.lineHeight), "host banner fits its one text line");
        DebugLayout.Arrange(1920, 1080, new DebugLayout.Request { Banner = bannerHeight });
        GUI.Labels.Clear(); TLDRevamp.Net.Banner.Draw();
        Check(GUI.Labels[0].Text == "RevaMP · Host message", "host notice has the broadcast header");
        TLDRevamp.Net.Banner.Show(10, "First line\\nSecond line");
        Check(TLDRevamp.Net.Banner.Measure() == bannerHeight + OverlayStyle.Title.lineHeight, "multiline host notice grows by one measured line");

        MarkPanel.Open(); float emptyMark = MarkPanel.Measure();
        Check(emptyMark < 220, "empty mark panel has no empty list or status region");
        MarkFiles.Snapshot = new[] { Entry(0) }; float oneMark = MarkPanel.Measure();
        Check(oneMark > emptyMark && oneMark < emptyMark + 80, "mark panel grows by the measured row and gap");
        MarkFiles.Snapshot = Enumerable.Range(0, 30).Select(Entry).ToArray();
        Check(MarkPanel.Measure() == 640, "mark panel caps at its existing maximum");
        DebugLayout.Arrange(1920, 1080, new DebugLayout.Request { Mark = MarkPanel.Measure() });
        MarkPanel.Draw(); Check(GUI.ScrollBody > GUI.ScrollViewport && GUI.ScrollViewport > 0, "marks beyond cap scroll");
        MarkFiles.Snapshot = Array.Empty<MarkFiles.Entry>(); MarkPanel.Measure();
        measures = GUIStyle.Measures;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { bar.Measure("RevaMP · Recording repro", "Recording actions"); MarkPanel.Measure(); }
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes && GUIStyle.Measures == measures, "warmed size requests allocate nothing and reuse text measurements");
        Event.current.type = EventType.Layout; GUI.Labels.Clear();
        bar.Draw(new DebugBox(0, 0, bar.Width, bar.Height)); table.Draw(new DebugBox(0, 0, 500, table.Height));
        Check(GUI.Labels.Count == 0, "overlay drawing stays Repaint-only");
        Console.WriteLine("PASS " + _checks + " unified overlay checks");
    }
    private static MarkFiles.Entry Entry(int i) => new MarkFiles.Entry { Sid = SessionLog.Sid, Key = "mark" + i, Note = "Test note", Role = "solo", St = i };
}

namespace UnityEngine
{
    public enum EventType { Repaint, Layout, KeyDown }
    public enum KeyCode { Escape, Return, KeypadEnter }
    public class Event { public static Event current = new Event(); public EventType type; public KeyCode keyCode; public void Use() { } }
    public static class Time { public static float realtimeSinceStartup; public static int frameCount; }
    public enum CursorLockMode { None }
    public static class Cursor { public static bool visible; public static CursorLockMode lockState; }
    public static class Mathf { public static float Min(float a, float b) => Math.Min(a, b); public static float Max(float a, float b) => Math.Max(a, b); public static float Clamp01(float n) => Math.Clamp(n, 0, 1); }
    public static class GUI
    {
        public static GUISkin skin = new GUISkin(); public static Color color = Color.white, backgroundColor; public static Color ButtonBackground;
        public static readonly System.Collections.Generic.List<(string Text, Rect Rect, GUIStyle Style)> Labels = new System.Collections.Generic.List<(string, Rect, GUIStyle)>();
        public static void Label(Rect r, GUIContent text, GUIStyle style) => Labels.Add((text.text, r, style));
        public static void DrawTexture(Rect r, Texture2D texture) { }
        public static bool Button(Rect r, string text, GUIStyle style) { ButtonBackground = backgroundColor; return false; }
        public static string TextArea(Rect r, string text, int limit, GUIStyle style) => text;
        public static void BeginGroup(Rect r) { } public static void EndGroup() { }
        public static float ScrollBody, ScrollViewport;
        public static Vector2 BeginScrollView(Rect r, Vector2 scroll, Rect body) { ScrollBody = body.height; ScrollViewport = r.height; return scroll; }
        public static void EndScrollView() { } public static void SetNextControlName(string name) { } public static void FocusControl(string name) { }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)] public class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type t, string name) { } }
    public class HarmonyPrefix : Attribute { } public class HarmonyPostfix : Attribute { }
}
class OverlayGame { public static OverlayGame s = new OverlayGame(); public object player = new object(); public void SomeInputSelected() { } public void PressedEscape() { } }
class DataFromMenuScript { public static DataFromMenuScript s; public bool mainmenu; } // test stub, not game code
namespace TLDRevamp
{
    static class MarkFiles
    {
        internal static Entry[] Snapshot = Array.Empty<Entry>(); internal static string Error;
        internal class Entry { internal string Sid, Key, Note, Role; internal int Origin, Source; internal bool Received; internal double St; }
        internal static string Identity(string sid, double st, int origin) => sid;
        internal static void Compare(string root, string sid) { }
    }
    static class SessionLog { internal static string Sid = "session"; internal static event Action<string> MarkSaved { add { } remove { } } }
    static class FeedbackReporter { internal static string Root; }
    class Plugin { internal static Plugin Instance = new Plugin(); internal Overlay Overlay = new Overlay(); }
    class Overlay { internal void Toast(string text) { } }
}
namespace TLDRevamp.Net
{
    static class Chat { internal static void Close() { } }
    static class Reports { internal static void RenameMark(string sid, double st, int origin, string note) { } }
    static class TelemetryMarks { internal static string Local(string note) => "{}"; }
    static class Mp { internal static Client Client; }
    class Client { internal int MyId; }
    static class Entities { internal static int MyId; }
    static class NetMeter
    {
        internal static string[][] PanelRows(int rows) => new[] { new[] { "Up 8.9 KB/s · Down 5.5 KB/s · 1.25 ms/s" },
            new[] { "Type", "Up/s", "KB/s", "Down/s", "KB/s", "ms/s" }, new[] { "ObjectCompareAnswer", "2.0", "0.00", "1999.0", "3.39", "0.05" } };
    }
}
