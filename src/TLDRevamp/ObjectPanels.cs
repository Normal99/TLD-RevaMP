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
            internal Color Color;
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
        private static readonly GUIContent Card = new GUIContent();
        private static float _cardWidth;
        private static object _session;
        private static GUIStyle _style;
        private static Vector2 _compareScroll;
        private static float _height;
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
            v.Color = severity == 2 ? new Color(1f, .45f, .35f) : severity == 1 ? new Color(1f, .8f, .3f) : Color.white;
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
            var b = new StringBuilder(Mine.text);
            if (_compare && _net != 0) foreach (var pair in Partners) b.Append("\n\n").Append(pair.Value.Text.text);
            if (!string.IsNullOrEmpty(FeedText.text)) b.Append("\n\nRecent events\n").Append(FeedText.text.TrimEnd());
            string text = b.ToString();
            if (Card.text == text) return;
            Card.text = text; _height = Lines(text) * 16 + 16; _dirty = true;
        }
        internal static void ReserveLayout()
        {
            if (!DebugView.Active) { DebugLayout.Card = default; return; }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false, wordWrap = true, padding = new RectOffset(0, 0, 0, 0) };
                _style.normal.textColor = Color.white;
            }
            float width = Mathf.Min(380, Screen.width - 24), top = DebugLayout.CardTop;
            var performance = DebugLayout.Performance;
            if (Screen.width - width - 12 < performance.X + performance.W + 8) top = Mathf.Max(top, performance.Y + performance.H + 8);
            if (_dirty || _cardWidth != width)
            {
                _height = _style.CalcHeight(Card, width - 28) + 16; _cardWidth = width; _dirty = false;
            }
            DebugLayout.Card = new DebugBox(Screen.width - width - 12, top, width, Mathf.Max(0, Mathf.Min(_height, Screen.height - top - DebugLayout.Footer.H - 12)));
        }
        internal static void Draw()
        {
            if (!DebugView.Active || Event.current.type == EventType.Layout) return;
            bool paint = Event.current.type == EventType.Repaint;
            Color color = GUI.color; int depth = GUI.depth; GUI.depth = 10;
            try
            {
                ReserveLayout(); var box = DebugLayout.Card;
                if (box.H <= 0) return;
                var rect = new Rect(box.X, box.Y, box.W, box.H);
                if (paint) Back(rect);
                _compareScroll = GUI.BeginScrollView(rect, _compareScroll, new Rect(0, 0, box.W - 20, _height));
                if (paint) { GUI.color = Color.white; GUI.Label(new Rect(8, 6, box.W - 28, _height), Card, _style); }
                GUI.EndScrollView();
            }
            finally { GUI.color = color; GUI.depth = depth; }
        }
        private static void Back(Rect r) { GUI.color = new Color(0, 0, 0, .78f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
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
