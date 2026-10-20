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
        private static object _session;
        private static GUIStyle _style;
        private static Vector2 _compareScroll, _feedScroll;
        private static float _height, _feedHeight;
        private static long _samples, _ticks, _maxTicks, _alloc;
        internal static bool Compare
        {
            get => _compare;
            set
            {
                if (_compare == value) return;
                if (_compare) ObjectPanelWire.Request(_token, _round, 0);
                _compare = value; _token++; _round = 0; _net = _requested = 0; _next = 0; Partners.Clear(); Array.Clear(Locals, 0, Locals.Length); _mine = null; _compareScroll = Vector2.zero;
                if (!_events) ObjectPanelEvents.FocusTpa(0);
            }
        }
        internal static bool Events { get => _events; set { _events = value; _next = 0; if (!value) { _feed = null; FeedText.text = null; if (!_compare) ObjectPanelEvents.FocusTpa(0); } } }
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
            if (!_compare && !_events) return;
            object session = (object)Mp.Server ?? Mp.Client;
            if (!ReferenceEquals(session, _session))
            {
                _session = session; _token++; _round = 0; Partners.Clear(); Array.Clear(Locals, 0, Locals.Length); ObjectPanelEvents.FocusTpa(0); _net = 0; _mine = _feed = null; _next = 0;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _next) return;
            _next = now + .5;
            if (_compare)
            {
                if (_net == 0) _net = _requested != 0 ? _requested : Entities.PanelFocus(false);
                ObjectPanelEvents.FocusTpa(_net);
                _mine = Capture(_net); _sampled = now; _round++; Locals[_round % 8] = _mine;
                if (_net != 0) ObjectPanelWire.Request(_token, _round, _net);
                Remove.Clear();
                foreach (var pair in Partners) if (now - pair.Value.Expected > 4) Remove.Add(pair.Key);
                foreach (int player in Remove) Partners.Remove(player);
                Mine.text = "You (player " + Entities.MyId + ")  net " + _net + "\n" + (_net == 0 ? "Aim at a shared object or sit in a shared car" : _mine.Text(null));
                _height = Lines(Mine.text) * 16 + 16;
                foreach (var pair in Partners)
                {
                    var v = pair.Value;
                    UpdateView(pair.Key, v, now);
                }
            }
            if (_events)
            {
                uint net = _compare && _net != 0 ? _net : Entities.PanelFocus(true);
                ObjectPanelEvents.FocusTpa(net);
                _feed = _compare && _mine != null && _mine.Net == net ? _mine : Capture(net, eventsOnly: true); _feedSampled = now;
                var text = new StringBuilder("Events  net ").Append(net).Append("  (last 20 per view, up to 35 s; wheel scroll)\n");
                AppendEvents(text, "You", _feed, 0);
                if (_compare) foreach (var pair in Partners) if (pair.Value.State != null && pair.Value.State.Net == net)
                    AppendEvents(text, "Player " + pair.Key, pair.Value.State, now - pair.Value.Received);
                FeedText.text = text.ToString();
                _feedHeight = Lines(FeedText.text) * 16 + 12;
            }
        }
        private static void UpdateView(int player, View v, double now)
        {
            v.Text.text = "Player " + player + (v.State == null ? "  waiting" : "  age " + ObjectPanelState.N(now - v.Received) + " s" + (now - v.Received > 2 ? "  STALE" : "")) + "\n" + (v.State != null ? v.State.Text(v.Local) : "No reply yet");
            int severity = v.State != null && v.Local != null && v.State.Has(ObjectPanelState.Bits.Found) && v.Local.Has(ObjectPanelState.Bits.Found) ? ObjectPanelState.Severity(v.State.Distance(v.Local), v.State.Angle(v.Local)) : 0;
            v.Color = severity == 2 ? new Color(1f, .45f, .35f) : severity == 1 ? new Color(1f, .8f, .3f) : Color.white;
            v.Lines = Lines(v.Text.text); _height = Mathf.Max(_height, v.Lines * 16 + 16);
        }
        private static int Lines(string text) { int n = 1; foreach (char c in text) if (c == '\n') n++; return n; }
        private static void AppendEvents(StringBuilder b, string source, ObjectPanelState s, double age)
        {
            b.Append(source).Append(s.Has(ObjectPanelState.Bits.EventsNotShared) ? ": events not shared\n" : s.EventCount == 0 ? ": no recent events\n" : ":\n");
            for (int i = 0; i < s.EventCount; i++)
            {
                var e = s.Events[i];
                b.Append("  -").Append(ObjectPanelState.N(e.Age + age)).Append(" s  ").Append(ObjectPanelEvents.Kinds[e.Kind]).Append("  ").Append(ObjectPanelEvents.Reasons[e.Reason])
                    .Append("  net ").Append(e.Net).Append(" other ").Append(e.Other).Append(" epoch ").Append(e.Epoch).Append(" p ").Append(e.From).Append(" -> ").Append(e.To)
                    .Append(" value ").Append(ObjectPanelState.N(e.Value)).Append('\n');
            }
        }
        internal static void Expect(uint token, uint round, uint net, int player)
        {
            if (!_compare || token != _token || net != _net || round != _round || player == Entities.MyId) return;
            if (!Partners.TryGetValue(player, out var v)) Partners[player] = v = new View();
            v.Expected = Time.realtimeSinceStartupAsDouble;
        }
        internal static void Receive(uint token, uint round, int source, ObjectPanelState s)
        {
            if (!_compare || token != _token || s.Net != _net || source == Entities.MyId || unchecked(_round - round) >= 8) return;
            if (!Partners.TryGetValue(source, out var v)) Partners[source] = v = new View();
            if (v.State != null && (round == v.Round || unchecked(round - v.Round) > int.MaxValue)) return;
            v.State = s; v.Local = Locals[round % 8]; v.Round = round; v.Received = Time.realtimeSinceStartupAsDouble; v.Expected = v.Received;
            UpdateView(source, v, v.Received);
        }
        internal static void Draw()
        {
            if (!_compare && !_events || Event.current.type == EventType.Layout) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
                _style.normal.textColor = Color.white;
            }
            bool paint = Event.current.type == EventType.Repaint;
            Color color = GUI.color; int depth = GUI.depth; GUI.depth = 10;
            try
            {
                if (_compare)
                {
                    var rect = new Rect(12, 185, Mathf.Max(200, Screen.width - 24), Mathf.Max(100, Screen.height * .46f));
                    if (paint) Back(rect);
                    _compareScroll = GUI.BeginScrollView(rect, _compareScroll, new Rect(0, 0, (Partners.Count + 1) * 360, _height));
                    if (paint)
                    {
                        GUI.color = Color.white; GUI.Label(new Rect(6, 4, 354, _height), Mine, _style);
                        int i = 1;
                        foreach (var pair in Partners) { GUI.color = pair.Value.Color; GUI.Label(new Rect(i++ * 360 + 6, 4, 354, _height), pair.Value.Text, _style); }
                    }
                    GUI.EndScrollView();
                }
                if (_events)
                {
                    float height = Mathf.Min(180, Screen.height * .25f);
                    var rect = new Rect(12, Screen.height - height - 12, Mathf.Max(200, Screen.width - 24), height);
                    if (paint) Back(rect);
                    _feedScroll = GUI.BeginScrollView(rect, _feedScroll, new Rect(0, 0, 1100, _feedHeight));
                    if (paint) { GUI.color = Color.white; GUI.Label(new Rect(6, 4, 1094, _feedHeight), FeedText, _style); }
                    GUI.EndScrollView();
                }
            }
            finally { GUI.color = color; GUI.depth = depth; }
        }
        private static void Back(Rect r) { GUI.color = new Color(0, 0, 0, .78f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
        internal static string Command(bool comparison, string[] args)
        {
            string op = args.Length > 1 ? args[1] : "state";
            if (op == "on" || op == "show")
            {
                if (comparison)
                {
                    uint net = args.Length > 2 && uint.TryParse(args[2], out uint n) ? n : 0;
                    Compare = false; Compare = true; _requested = net;
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
            var b = new StringBuilder("{\"compare\":").Append(_compare ? "true" : "false").Append(",\"events\":").Append(_events ? "true" : "false").Append(",\"net\":").Append(_net)
                .Append(",\"local\":").Append(_compare && _mine != null ? _mine.Json(null, now - _sampled) : "null").Append(",\"partners\":[");
            bool comma = false;
            if (_compare) foreach (var pair in Partners)
            {
                if (comma) b.Append(','); comma = true; var v = pair.Value;
                b.Append("{\"player\":").Append(pair.Key).Append(",\"ageS\":").Append(v.State == null ? "null" : TLDRevamp.Json.Num(now - v.Received))
                    .Append(",\"stale\":").Append(v.State == null || now - v.Received > 2 ? "true" : "false").Append(",\"state\":").Append(v.State != null ? v.State.Json(v.Local, now - v.Received) : "null").Append('}');
            }
            return b.Append("],\"feed\":").Append(_events && _feed != null ? _feed.Json(null, now - _feedSampled) : "null")
                .Append(",\"cost\":{\"samples\":").Append(_samples).Append(",\"captureMs\":").Append(TLDRevamp.Json.Num(_ticks * 1000.0 / Stopwatch.Frequency))
                .Append(",\"captureMaxMs\":").Append(TLDRevamp.Json.Num(_maxTicks * 1000.0 / Stopwatch.Frequency)).Append(",\"captureAllocBytes\":").Append(SessionLog.AllocCounter ? _alloc.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null").Append("}}").ToString();
        }
    }
}
