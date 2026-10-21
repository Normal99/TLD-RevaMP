using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using TLDRevamp.Net;
using UnityEngine;

namespace TLDRevamp
{
    internal static class ObjectPanels
    {
        private sealed class View
        {
            internal ObjectPanelState State, Local;
            internal uint Round;
            internal double Received, Expected;
            internal readonly GUIContent Text = new GUIContent();
            internal int Lines;
            internal OverlayKind Kind;
            internal int Row;
        }
        private static readonly Dictionary<int, View> Partners = new Dictionary<int, View>();
        private static readonly List<int> Remove = new List<int>();
        private static readonly GUIContent Mine = new GUIContent(), FeedText = new GUIContent();
        private static ObjectPanelState _mine, _feed;
        private static readonly ObjectPanelState[] Locals = new ObjectPanelState[8];
        private static uint _net, _requested, _token, _round;
        private static double _next, _sampled, _feedSampled;
        private static bool _compare, _events;
        private static bool _allFields, _dirty = true;
        private static int _revision = -1;
        private static readonly GUIContent Card = new GUIContent(), Focus = new GUIContent();
        private static float _cardWidth;
        private static int _styleRevision = -1;
        internal static float CardHeight, CompareHeight;
        private static object _session;
        private static GUIStyle _style;
        private static Vector2 _compareScroll, _focusScroll;
        private static float _height, _focusHeight;
        private static string _rawCard = "";
        private static long _samples, _ticks, _maxTicks, _alloc;
        internal static bool Compare
        {
            get => _compare;
            set
            {
                if (_compare == value) return;
                if (value) DebugView.Enable(DebugViewMode.Focus);
                if (_compare) ObjectPanelWire.Request(_token, _round, 0);
                _compare = value; _token++; _round = 0; _net = _requested = 0; _next = 0; Partners.Clear(); Array.Clear(Locals, 0, Locals.Length); _mine = null; _compareScroll = Vector2.zero;
                DebugView.PinnedNet = 0;
                if (!_events) ObjectPanelEvents.FocusTpa(0);
            }
        }
        internal static bool Events { get => _events; set { if (value) DebugView.Enable(DebugViewMode.Focus); _events = value; _next = 0; if (!value) { _feed = null; FeedText.text = null; if (!_compare) ObjectPanelEvents.FocusTpa(0); } } }
        internal static ObjectPanelState Capture(uint net, bool remote = false, bool eventsOnly = false)
        {
            long at = Stopwatch.GetTimestamp(), alloc = SessionLog.AllocCounter ? GC.GetAllocatedBytesForCurrentThread() : 0;
            var s = eventsOnly ? new ObjectPanelState { Net = net } : Entities.PanelCapture(net);
            if (remote && !Reports.Share) s.Flags |= ObjectPanelState.Bits.EventsNotShared;
            else ObjectPanelEvents.Capture(SessionLog.PanelEventsRing, s, Time.realtimeSinceStartupAsDouble);
            long ticks = Stopwatch.GetTimestamp() - at;
            _samples++; _ticks += ticks; _maxTicks = Math.Max(_maxTicks, ticks);
            if (SessionLog.AllocCounter) _alloc += GC.GetAllocatedBytesForCurrentThread() - alloc;
            return s;
        }
        internal static void Tick()
        {
            if (!DebugView.Active) return;
            if (_revision != DebugView.Revision) { _revision = DebugView.Revision; _next = 0; }
            object session = (object)Mp.Server ?? Mp.Client;
            if (!ReferenceEquals(session, _session))
            {
                _session = session; _token++; _round = 0; Partners.Clear(); Array.Clear(Locals, 0, Locals.Length); ObjectPanelEvents.FocusTpa(0); _net = 0; _mine = _feed = null; _next = 0;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _next) return;
            _next = now + .5;
            uint target = _compare && _requested != 0 ? _requested : Entities.PanelFocus(true);
            if (target != _net)
            {
                _token++; _round = 0; Partners.Clear(); Array.Clear(Locals, 0, Locals.Length); _compareScroll = Vector2.zero;
                if (_compare && target == 0 && _net != 0) ObjectPanelWire.Request(_token, _round, 0);
                _net = target;
            }
            ObjectPanelEvents.FocusTpa(_net);
            Mine.text = Entities.OverlayDescription(_net, now);
            if (_compare)
            {
                ObjectPanelEvents.FocusTpa(_net);
                _mine = _net != 0 ? Capture(_net) : null; _sampled = now; _round++; Locals[_round % 8] = _mine;
                if (_net != 0) ObjectPanelWire.Request(_token, _round, _net);
                Remove.Clear();
                foreach (var pair in Partners) if (now - pair.Value.Expected > 4) Remove.Add(pair.Key);
                foreach (int player in Remove) Partners.Remove(player);
                foreach (var pair in Partners)
                {
                    var v = pair.Value;
                    UpdateView(pair.Key, v, now);
                }
            }
            if (_net != 0)
            {
                uint net = _net;
                ObjectPanelEvents.FocusTpa(net);
                _feed = _compare && _mine != null && _mine.Net == net ? _mine : Capture(net, eventsOnly: true); _feedSampled = now;
                var text = new StringBuilder();
                AppendEvents(text, "You", _feed, 0);
                if (_compare) foreach (var pair in Partners) if (pair.Value.State != null && pair.Value.State.Net == net)
                    AppendEvents(text, "Player " + pair.Key, pair.Value.State, now - pair.Value.Received);
                FeedText.text = text.ToString();
            }
            else { _feed = null; FeedText.text = null; }
            RebuildCard();
        }
        private static void UpdateView(int player, View v, double now)
        {
            v.Text.text = "Player " + player + (v.State == null ? "  waiting" : "  reply " + ObjectPanelState.N(now - v.Received) + " s ago" + (now - v.Received > 2 ? "  STALE" : "")) + "\n" +
                (v.State != null ? _allFields ? v.State.Text(v.Local) : v.State.Differences(v.Local) : "No reply yet");
            int severity = v.State != null && v.Local != null && v.State.Has(ObjectPanelState.Bits.Found) && v.Local.Has(ObjectPanelState.Bits.Found) ? ObjectPanelState.Severity(v.State.Distance(v.Local), v.State.Angle(v.Local)) : 0;
            v.Kind = severity == 2 ? OverlayKind.Problem : severity == 1 || v.State == null || now - v.Received > 2 ? OverlayKind.Warning : OverlayKind.Ok;
            v.Lines = Lines(v.Text.text);
        }
        private static int Lines(string text) { int n = 1; foreach (char c in text) if (c == '\n') n++; return n; }
        private static void AppendEvents(StringBuilder b, string source, ObjectPanelState s, double age)
        {
            if (s.Has(ObjectPanelState.Bits.EventsNotShared)) { b.Append(source).Append(": events not shared\n"); return; }
            if (s.EventCount == 0) return;
            b.Append(source).Append(":\n");
            for (int i = 0; i < s.EventCount && i < (_events ? 6 : 3); i++)
            {
                var e = s.Events[i];
                b.Append("  -").Append(ObjectPanelState.N(e.Age + age)).Append(" s  ").Append(ObjectPanelEvents.Kinds[e.Kind]).Append("  ").Append(ObjectPanelEvents.Reasons[e.Reason])
                    .Append('\n');
            }
        }
        internal static void Expect(uint token, uint round, uint net, int player)
        {
            if (!DebugView.Active || !_compare || token != _token || net != _net || round != _round || player == Entities.MyId) return;
            if (!Partners.TryGetValue(player, out var v)) Partners[player] = v = new View();
            v.Expected = Time.realtimeSinceStartupAsDouble;
        }
        internal static void Receive(uint token, uint round, int source, ObjectPanelState s)
        {
            if (!DebugView.Active || !_compare || token != _token || s.Net != _net || source == Entities.MyId || unchecked(_round - round) >= 8) return;
            if (!Partners.TryGetValue(source, out var v)) Partners[source] = v = new View();
            if (v.State != null && (round == v.Round || unchecked(round - v.Round) > int.MaxValue)) return;
            v.State = s; v.Local = Locals[round % 8]; v.Round = round; v.Received = Time.realtimeSinceStartupAsDouble; v.Expected = v.Received;
            UpdateView(source, v, v.Received);
            RebuildCard();
        }
        private static void RebuildCard()
        {
            var b = new StringBuilder();
            int row = 0;
            if (_compare && _net != 0) foreach (var pair in Partners)
            {
                if (b.Length > 0) { b.Append("\n\n"); row += 2; }
                pair.Value.Row = row; b.Append(pair.Value.Text.text); row += pair.Value.Lines - 1;
            }
            if (!string.IsNullOrEmpty(FeedText.text)) b.Append("\n\nRecent events\n").Append(FeedText.text.TrimEnd());
            string text = b.ToString();
            if (_rawCard == text && Focus.text == Mine.text) return;
            _rawCard = text; _dirty = true;
        }
        internal static void ReserveLayout(float availableWidth = 0)
        {
            CardHeight = CompareHeight = 0;
            if (!DebugView.Active) return;
            OverlayStyle.Ensure(); _style = OverlayStyle.Body;
            float width = availableWidth > 0 ? availableWidth : DebugLayout.LeftWidth(Screen.width, Screen.height, false);
            if (_dirty || _cardWidth != width || _styleRevision != OverlayStyle.Revision)
            {
                _styleRevision = OverlayStyle.Revision; _cardWidth = width; _dirty = false;
                Focus.text = OverlayStyle.Truncate(Mine.text, width - 2 * OverlayStyle.Padding - 20 * OverlayStyle.Scale);
                _height = Lines(_rawCard) * OverlayStyle.Line;
                Card.text = OverlayStyle.Truncate(_rawCard, width - 2 * OverlayStyle.Padding - 34 * OverlayStyle.Scale);
                _focusHeight = Lines(Focus.text ?? "") * OverlayStyle.Line;
            }
            CardHeight = OverlayStyle.Header + OverlayStyle.Padding + _focusHeight;
            CompareHeight = (_compare || _events) && !string.IsNullOrEmpty(Card.text) ? OverlayStyle.Header + OverlayStyle.Padding + _height : 0;
        }
        internal static void Draw()
        {
            if (!DebugView.Active || Event.current.type == EventType.Layout) return;
            bool paint = Event.current.type == EventType.Repaint;
            Color color = GUI.color; int depth = GUI.depth; GUI.depth = 10;
            try
            {
                if (DebugLayout.Card.H > 0)
                {
                    var tool = DebugView.Mode == DebugViewMode.Focus ? OverlayTool.Focus : DebugView.Mode == DebugViewMode.Problems ? OverlayTool.Problems : OverlayTool.Everything;
                    var focus = OverlayStyle.Panel(DebugLayout.Card, tool);
                    _focusScroll = GUI.BeginScrollView(focus, _focusScroll, new Rect(0, 0, focus.width - 20 * OverlayStyle.Scale, _focusHeight));
                    if (paint) OverlayStyle.Label(new Rect(0, 0, focus.width - 20 * OverlayStyle.Scale, _focusHeight), Focus);
                    GUI.EndScrollView();
                }
                if (DebugLayout.Compare.H <= 0) return;
                var rect = OverlayStyle.Panel(DebugLayout.Compare, _compare ? OverlayTool.Compare : OverlayTool.Events);
                _compareScroll = GUI.BeginScrollView(rect, _compareScroll, new Rect(0, 0, Mathf.Max(0, rect.width - 20 * OverlayStyle.Scale), _height));
                if (paint)
                {
                    OverlayStyle.Label(new Rect(14 * OverlayStyle.Scale, 0, Mathf.Max(0, rect.width - 34 * OverlayStyle.Scale), _height), Card);
                    if (_compare) foreach (var pair in Partners)
                        OverlayStyle.StatusDot(new Rect(0, pair.Value.Row * OverlayStyle.Line + 6 * OverlayStyle.Scale, 6 * OverlayStyle.Scale, 6 * OverlayStyle.Scale), pair.Value.Kind);
                }
                GUI.EndScrollView();
            }
            finally { GUI.color = color; GUI.depth = depth; }
        }
        internal static string Command(bool comparison, string[] args)
        {
            string op = args.Length > 1 ? args[1] : "state";
            if (comparison && (op == "all" || op == "differences"))
            {
                _allFields = op == "all";
                double now = Time.realtimeSinceStartupAsDouble;
                foreach (var pair in Partners) UpdateView(pair.Key, pair.Value, now);
                RebuildCard(); return Json();
            }
            if (op == "on" || op == "show")
            {
                if (comparison)
                {
                    uint net = args.Length > 2 && uint.TryParse(args[2], out uint n) ? n : 0;
                    Compare = false; Compare = true; _requested = net; DebugView.PinnedNet = net;
                }
                else Events = true;
                Tick();
            }
            else if (op == "off" || op == "hide") { if (comparison) Compare = false; else Events = false; }
            return Json();
        }
        internal static string Json()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            var b = new StringBuilder("{\"debugView\":").Append(TLDRevamp.Json.Str(DebugView.Mode.ToString().ToLowerInvariant())).Append(",\"allFields\":").Append(_allFields ? "true" : "false")
                .Append(",\"compare\":").Append(_compare ? "true" : "false").Append(",\"events\":").Append(_events ? "true" : "false").Append(",\"net\":").Append(_net)
                .Append(",\"local\":").Append(_compare && _mine != null ? _mine.Json(null, now - _sampled) : "null").Append(",\"partners\":[");
            bool comma = false;
            if (_compare) foreach (var pair in Partners)
            {
                if (comma) b.Append(','); comma = true; var v = pair.Value;
                b.Append("{\"player\":").Append(pair.Key).Append(",\"ageS\":").Append(v.State == null ? "null" : TLDRevamp.Json.Num(now - v.Received))
                    .Append(",\"stale\":").Append(v.State == null || now - v.Received > 2 ? "true" : "false").Append(",\"state\":").Append(v.State != null ? v.State.Json(v.Local, now - v.Received) : "null").Append('}');
            }
            return b.Append("],\"feed\":").Append(DebugView.Active && _feed != null ? _feed.Json(null, now - _feedSampled) : "null")
                .Append(",\"cost\":{\"samples\":").Append(_samples).Append(",\"captureMs\":").Append(TLDRevamp.Json.Num(_ticks * 1000.0 / Stopwatch.Frequency))
                .Append(",\"captureMaxMs\":").Append(TLDRevamp.Json.Num(_maxTicks * 1000.0 / Stopwatch.Frequency)).Append(",\"captureAllocBytes\":").Append(SessionLog.AllocCounter ? _alloc.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null").Append("}}").ToString();
        }
    }
}
