using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    [HarmonyPatch]
    internal static class MarkPanel
    {
        internal static bool Opened { get; private set; }
        private static string _note = "", _status = "", _error;
        private static MarkFiles.Entry _editing;
        private static Vector2 _scroll;
        private static bool _focus, _cursorVisible;
        private static CursorLockMode _cursorLock;
        private static int _openedFrame;
        private static GUIStyle _text;
        private static MarkFiles.Entry[] _snapshot, _marks = Array.Empty<MarkFiles.Entry>();
        private static float[] _heights = Array.Empty<float>();
        private static string _sid;
        private static float _width, _contentHeight;
        static MarkPanel() => SessionLog.MarkSaved += note => Plugin.Instance.Overlay.Toast("Mark saved: " + note);
        internal static void Open()
        {
            if (mainscript.s == null || mainscript.s.player == null) return;
            if (!Opened) { _cursorVisible = Cursor.visible; _cursorLock = Cursor.lockState; }
            Net.Chat.Close(); Opened = true; _openedFrame = Time.frameCount; _focus = true; _editing = null; _note = ""; _status = "";
        }
        private static void Close()
        {
            Opened = false; Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible;
        }
        internal static void Tick()
        {
            if (_error != MarkFiles.Error && MarkFiles.Error != null) { _error = MarkFiles.Error; Plugin.Instance.Overlay.Toast(_error); }
            if (!Opened) return;
            if (mainscript.s == null || mainscript.s.player == null || DataFromMenuScript.s == null || DataFromMenuScript.s.mainmenu) { Close(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private static void Save()
        {
            if (_editing != null)
            {
                Net.Reports.RenameMark(_editing.Sid, _editing.St, _editing.Origin, _note);
                _status = "Rename requested; the list updates when saved."; _editing = null; _note = ""; return;
            }
            string result = Net.TelemetryMarks.Local(_note);
            if (result.Contains("\"error\"")) { _status = result; return; }
            _status = "Saving mark (includes the next 5 seconds)..."; _note = "";
        }
        internal static void Draw()
        {
            if (!Opened) return;
            if (_text == null) _text = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            float width = Math.Min(700, Screen.width - 24), height = Math.Min(640, Screen.height - 24);
            var rect = new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height);
            GUI.Box(rect, "Session marks"); GUI.BeginGroup(rect);
            if (GUI.Button(new Rect(width - 70, 4, 62, 24), "Close")) { Close(); GUI.EndGroup(); return; }
            GUI.Label(new Rect(16, 32, width - 32, 24), _editing == null ? "New note (optional, 500 characters)" : "Edit note", _text);
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && Time.frameCount > _openedFrame)
            {
                if (ev.keyCode == KeyCode.Escape) { ev.Use(); Close(); GUI.EndGroup(); return; }
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) { ev.Use(); Save(); }
            }
            GUI.SetNextControlName("mark-note"); _note = GUI.TextArea(new Rect(16, 60, width - 32, 104), _note, 500);
            if (_focus) { GUI.FocusControl("mark-note"); _focus = false; }
            if (GUI.Button(new Rect(16, 172, 120, 28), _editing == null ? "Save (Enter)" : "Rename (Enter)")) Save();
            if (GUI.Button(new Rect(148, 172, 120, 28), "New note")) { _editing = null; _note = ""; _focus = true; }
            if (GUI.Button(new Rect(280, 172, 180, 28), "Generate comparison")) { MarkFiles.Compare(FeedbackReporter.Root, SessionLog.Sid); _status = "Generating comparison.html in this session's marks folder."; }
            GUI.Label(new Rect(16, 205, width - 32, 44), _status, _text);
            var snapshot = MarkFiles.Snapshot;
            if (_snapshot != snapshot || _sid != SessionLog.Sid || _width != width)
            {
                _snapshot = snapshot; _sid = SessionLog.Sid; _width = width;
                _marks = snapshot.Where(e => e.Sid == _sid).GroupBy(e => e.Key).Select(g => g.OrderBy(e => e.Received).First()).OrderBy(e => e.St).ToArray();
                _heights = _marks.Select(e => Math.Max(64, _text.CalcHeight(new GUIContent(e.Note), width - 150) + 30)).ToArray();
                _contentHeight = _heights.Sum();
            }
            _scroll = GUI.BeginScrollView(new Rect(16, 252, width - 32, height - 268), _scroll, new Rect(0, 0, width - 52, _contentHeight));
            float y = 0;
            for (int i = 0; i < _marks.Length; i++)
            {
                var mark = _marks[i]; float row = _heights[i];
                if (y + row < _scroll.y || y > _scroll.y + height - 268) { y += row; continue; }
                string who = mark.Origin == Net.Entities.MyId || mark.Role == "solo" ? "me" : "player-" + mark.Origin;
                GUI.Label(new Rect(0, y, width - 150, 24), mark.St.ToString("F3") + " s | " + who + (mark.Received ? " (copy from player-" + mark.Source + ")" : "") + " | " + mark.Role, _text);
                GUI.Label(new Rect(0, y + 24, width - 150, row - 24), mark.Note, _text);
                if (GUI.Button(new Rect(width - 138, y + 4, 76, 26), "Edit")) { _editing = mark; _note = mark.Note; _focus = true; }
                y += row;
            }
            GUI.EndScrollView(); GUI.EndGroup();
        }
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected)), HarmonyPostfix]
        private static void InputSelected(ref bool __result) { if (Opened) __result = true; }
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.PressedEscape)), HarmonyPrefix]
        private static bool Escape() { if (!Opened) return true; Close(); return false; }
    }
}
