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
        private static GUIContent[] _metadata = Array.Empty<GUIContent>(), _notes = Array.Empty<GUIContent>();
        private static readonly GUIContent Status = new GUIContent(), Prompt = new GUIContent();
        private static int _styleRevision = -1;
        private static MarkFiles.Entry[] _snapshot, _marks = Array.Empty<MarkFiles.Entry>();
        private static float[] _heights = Array.Empty<float>();
        private static string _sid;
        private static bool _bug;
        private static float _width, _contentHeight;
        static MarkPanel() => SessionLog.MarkSaved += note => Plugin.Instance.Overlay.Toast("Mark saved: " + note);
        /// bug: the same note box, but Enter takes a bug report (the report zip, the recorded actions and the world save) instead of a mark.
        internal static void Open(bool bug = false)
        {
            if (mainscript.s == null || mainscript.s.player == null) return;
            if (!Opened) { _cursorVisible = Cursor.visible; _cursorLock = Cursor.lockState; }
            Net.Chat.Close(); Opened = true; _openedFrame = Time.frameCount; _focus = true; _editing = null; _note = ""; _status = "";
            if (_bug != bug) { _bug = bug; _snapshot = null; }
        }
        private static void Close()
        {
            Opened = false; _bug = false; Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible;
        }
        internal static void Tick()
        {
            if (_error != MarkFiles.Error && MarkFiles.Error != null) { _error = MarkFiles.Error; Plugin.Instance.Overlay.Toast(_error); }
            if (!Opened) return;
            if (mainscript.s == null || mainscript.s.player == null || DataFromMenuScript.s == null || DataFromMenuScript.s.mainmenu) { Close(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private static void Save() => Submit(_editing, _note);
        internal static string Submit(MarkFiles.Entry editing, string note)
        {
            if (editing != null && !CanEdit(editing)) return "{\"error\":\"mark belongs to another player\"}";
            if (_bug && editing == null)
            {
                bool started = BugReport.Request(note);
                _note = ""; if (started) Close(); else _status = "A bug report is already being saved.";
                return started ? "{\"ok\":true}" : "{\"error\":\"a bug report is already being saved\"}";
            }
            _editing = editing; _note = note;
            if (_editing != null)
            {
                Net.Reports.RenameMark(_editing.Sid, _editing.St, _editing.Origin, _note);
                _status = "Rename requested; the list updates when saved."; _editing = null; _note = ""; return "{\"ok\":true}";
            }
            string result = Net.TelemetryMarks.Local(_note);
            if (result.Contains("\"error\"")) { _status = result; return result; }
            _status = "Saving mark (includes the next 5 seconds)..."; _note = ""; return result;
        }
        private static bool CanEdit(MarkFiles.Entry mark) => mark.Origin == (Net.Mp.Client != null ? Net.Mp.Client.MyId : 0);
        internal static string Bridge(string arg, bool raw = false)
        {
            if (!raw && arg.StartsWith("save ", StringComparison.Ordinal)) return Submit(null, arg.Substring(5));
            var parts = arg.Split(new[] { ' ' }, 5);
            if (parts.Length != 5 || parts[0] != "rename" || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double st) ||
                !int.TryParse(parts[3], out int origin)) return "{\"error\":\"use save <note> or rename <sid> <st> <origin> <note>\"}";
            string key = MarkFiles.Identity(parts[1], st, origin);
            var mark = MarkFiles.Snapshot.FirstOrDefault(e => e.Key == key);
            if (mark == null || mark.Sid != SessionLog.Sid) return "{\"error\":\"unknown session mark\"}";
            if (!raw) return Submit(mark, parts[4]);
            Net.Reports.RenameMark(mark.Sid, mark.St, mark.Origin, parts[4]); return "{\"ok\":true}";
        }
        private static float _promptHeight, _inputY, _buttonsY, _statusY, _statusHeight, _listY, _rowLabel;
        private static string _lastStatus;
        internal static float Measure()
        {
            if (!Opened) return 0;
            _text = OverlayStyle.Wrap;
            float s = OverlayStyle.Scale, width = Mathf.Min(Screen.width - 24 * s, 700 * s) - 2 * OverlayStyle.Padding;
            var snapshot = MarkFiles.Snapshot;
            bool changed = _width != width || _styleRevision != OverlayStyle.Revision;
            if (_snapshot != snapshot || _sid != SessionLog.Sid || changed)
            {
                _snapshot = snapshot; _sid = SessionLog.Sid; _width = width; _styleRevision = OverlayStyle.Revision;
                _marks = snapshot.Where(e => !_bug && e.Sid == _sid).GroupBy(e => e.Key).Select(g => g.OrderBy(e => e.Received).First()).OrderBy(e => e.St).ToArray();
                _heights = new float[_marks.Length]; _metadata = new GUIContent[_marks.Length]; _notes = new GUIContent[_marks.Length]; _contentHeight = 0;
                _rowLabel = OverlayStyle.Small.lineHeight;
                for (int i = 0; i < _marks.Length; i++)
                {
                    var mark = _marks[i]; string who = mark.Origin == Net.Entities.MyId || mark.Role == "solo" ? "You" : "Player " + mark.Origin;
                    _metadata[i] = new GUIContent(OverlayStyle.Truncate(mark.St.ToString("F1") + " s · " + who + (mark.Received ? " · Copy from player " + mark.Source : "") + " · " + mark.Role, width - 110 * s, OverlayStyle.Small));
                    _notes[i] = new GUIContent(mark.Note);
                    _heights[i] = Math.Max(OverlayStyle.ButtonHeight, _rowLabel + OverlayStyle.Height(_notes[i], width - 110 * s, _text)) + 8 * s;
                    _contentHeight += _heights[i];
                }
            }
            if (changed || _lastStatus != _status)
            {
                _lastStatus = _status; Status.text = _status;
                _statusHeight = string.IsNullOrEmpty(_status) ? 0 : OverlayStyle.Height(Status, width, _text);
            }
            // Leave room for the full-height Close button in the header.
            _promptHeight = OverlayStyle.Line;
            _inputY = _promptHeight + 8 * s;
            _buttonsY = _inputY + 80 * s + 8 * s;
            _statusY = _buttonsY + OverlayStyle.ButtonHeight + 8 * s;
            _listY = _statusY + _statusHeight + (_statusHeight > 0 && _marks.Length > 0 ? 8 * s : 0);
            float content = _marks.Length > 0 ? _listY + _contentHeight : _statusHeight > 0 ? _statusY + _statusHeight : _buttonsY + OverlayStyle.ButtonHeight;
            return Math.Min(640 * s, OverlayStyle.PanelHeight(6 * s + content));
        }
        internal static void Draw()
        {
            if (!Opened || DebugLayout.Mark.H <= 0) return;
            OverlayStyle.Ensure();
            var frame = OverlayStyle.Rect(DebugLayout.Mark); var rect = OverlayStyle.Panel(frame, OverlayTool.Mark);
            float s = OverlayStyle.Scale; rect.y += 6 * s; rect.height = Math.Max(0, rect.height - 6 * s);
            float width = rect.width;
            if (OverlayStyle.HeaderButton(frame, "Close")) { Close(); return; }
            GUI.BeginGroup(rect);
            Prompt.text = _editing != null ? "Edit note" : _bug ? "What went wrong? (optional, 500 characters) Enter reports the bug" : "Note (optional, 500 characters)";
            OverlayStyle.Label(new Rect(0, 0, width, _promptHeight), Prompt, OverlayKind.Muted);
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && Time.frameCount > _openedFrame)
            {
                if (ev.keyCode == KeyCode.Escape) { ev.Use(); Close(); GUI.EndGroup(); return; }
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) { ev.Use(); Save(); }
            }
            GUI.SetNextControlName("mark-note"); _note = OverlayStyle.Edit(new Rect(0, _inputY, width, 80 * s), _note, 500);
            if (_focus) { GUI.FocusControl("mark-note"); _focus = false; }
            if (OverlayStyle.Click(new Rect(0, _buttonsY, (_bug ? 170 : 124) * s, OverlayStyle.ButtonHeight), _editing != null ? "Rename (Enter)" : _bug ? "Report bug (Enter)" : "Save (Enter)")) Save();
            if (OverlayStyle.Click(new Rect((_bug ? 178 : 132) * s, _buttonsY, 110 * s, OverlayStyle.ButtonHeight), "New note")) { _editing = null; _note = ""; _focus = true; }
            if (OverlayStyle.Click(new Rect((_bug ? 296 : 250) * s, _buttonsY, 180 * s, OverlayStyle.ButtonHeight), "Generate comparison")) { MarkFiles.Compare(FeedbackReporter.Root, SessionLog.Sid); _status = "Generating comparison.html in this session's marks folder."; }
            OverlayStyle.Label(new Rect(0, _statusY, width, _statusHeight), Status, OverlayKind.Muted, _text);
            if (_marks.Length > 0)
            {
                float listHeight = Math.Max(0, rect.height - _listY);
                _scroll = GUI.BeginScrollView(new Rect(0, _listY, width, listHeight), _scroll, new Rect(0, 0, width - 20 * s, _contentHeight));
                float y = 0;
                for (int i = 0; i < _marks.Length; i++)
                {
                    var mark = _marks[i]; float row = _heights[i];
                    if (y + row < _scroll.y || y > _scroll.y + listHeight) { y += row; continue; }
                    OverlayStyle.Label(new Rect(0, y, width - 110 * s, _rowLabel), _metadata[i], OverlayKind.Muted, OverlayStyle.Small);
                    OverlayStyle.Label(new Rect(0, y + _rowLabel, width - 110 * s, row - _rowLabel), _notes[i], OverlayKind.Text, _text);
                    if (CanEdit(mark) && OverlayStyle.Click(new Rect(width - 98 * s, y, 76 * s, OverlayStyle.ButtonHeight), "Edit")) { _editing = mark; _note = mark.Note; _focus = true; }
                    y += row;
                }
                GUI.EndScrollView();
            }
            GUI.EndGroup();
        }
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected)), HarmonyPostfix]
        private static void InputSelected(ref bool __result) { if (Opened) __result = true; }
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.PressedEscape)), HarmonyPrefix]
        private static bool Escape() { if (!Opened) return true; Close(); return false; }
    }
}
