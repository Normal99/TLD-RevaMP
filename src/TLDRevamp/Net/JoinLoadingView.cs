using System;
using UnityEngine;
using static TLDRevamp.Net.JoinLoadingSkin;

namespace TLDRevamp.Net
{
    public static partial class JoinLoading
    {
        private static readonly GUIContent Title = new GUIContent("Joining"), Host = new GUIContent(), ClockText = new GUIContent(),
            ReceivedTitle = new GUIContent("Received"), Counters = new GUIContent(), Objects = new GUIContent("objects"),
            Applied = new GUIContent(), Latest = new GUIContent("LATEST"), Footer = new GUIContent(), DetailText = new GUIContent(),
            PercentText = new GUIContent(), QuestionText = new GUIContent(), CreditText = new GUIContent("Multiplayer by RevaMP"),
            PreviewLabel = new GUIContent("SAMPLE PREVIEW · no join in progress"), Check = new GUIContent("✓"),
            StuckText = new GUIContent("No progress for 60 s. Wait, cancel or continue."),
            CancelText = new GUIContent("Cancel (Esc)"), ContinueText = new GUIContent("Continue anyway"),
            DownloadText = new GUIContent("Download mods"), DeclineText = new GUIContent("Decline");
        private static readonly GUIContent[] Stages = Contents(7), StageDetails = Contents(7), Timers = Contents(7), Feed = Contents(6),
            Kinds = { new GUIContent("Vehicles"), new GUIContent("Items and parts"), new GUIContent("Other") }, KindCounts = Contents(3);
        private static readonly float[] StageY = new float[7], StageNameHeight = new float[7], StageDetailHeight = new float[7];
        private static Vector2 _dialogScroll, _outerScroll;
        private static bool _twoColumns, _viewQuestion, _viewStuck, _viewReady, _viewPreview;
        private static int _viewStage, _feedRows;
        private static float _width, _bodyWidth, _columnWidth, _stagesHeight, _receivedHeight, _questionHeight, _counterWidth,
            _headerHeight, _footerHeight, _hostHeight, _detailHeight, _transferHeight, _bodyHeight, _buttonY;
        private static GUIContent[] Contents(int count)
        {
            var rows = new GUIContent[count];
            for (int i = 0; i < count; i++) rows[i] = new GUIContent();
            return rows;
        }
        private static void RebuildText(float width, double now, bool scrollbar = false, bool wholeScroll = false)
        {
            float s = Scale;
            float viewportWidth = width;
            if (wholeScroll) width = Math.Max(1, width - 20 * s);
            _nextText = now + .25; _width = width;
            _twoColumns = JoinPresentation.TwoColumns(width, s);
            _bodyWidth = Math.Max(1, width - (scrollbar && !wholeScroll ? 20 * s : 0));
            _columnWidth = _twoColumns ? (_bodyWidth - 40 * s) * .58f : _bodyWidth;
            float rightWidth = _twoColumns ? _bodyWidth - _columnWidth - 40 * s : _bodyWidth;
            _viewPreview = JoinLoadingPreview.Active;
            _viewStage = _viewPreview ? JoinLoadingPreview.Stage : State.Stage;
            _viewReady = _viewPreview ? _viewStage == 6 : State.Ready;
            _viewQuestion = _viewPreview ? JoinLoadingPreview.Question : MpScreen.PendingQuestion != null;
            _viewStuck = _viewPreview ? JoinLoadingPreview.Stuck : State.Stuck(now);
            string host = _viewPreview ? JoinLoadingPreview.Host : _host;
            Host.text = $"Host: {host} · {(_viewPreview ? "direct" : _route)} · {(_viewPreview ? "34 ms" : _ping >= 0 ? $"{_ping:N0} ms" : "ping unknown")}";
            bool smallHeader = width < 450 * s;
            Host.text = OverlayStyle.Truncate(Host.text, Math.Max(1, width - (smallHeader ? 0 : 178 * s)), JoinLoadingSkin.Host);
            _hostHeight = 28 * s;
            _headerHeight = (smallHeader ? 178 : 112) * s;
            double elapsed = _viewPreview ? 7 : Math.Max(0, now - State.Started);
            ClockText.text = $"{(int)elapsed / 60:00}:{(int)elapsed % 60:00}";
            var g = Metrics.Geometry;
            StageDetails[0].text = $"{_route} · " + (_ping >= 0 ? $"{_ping:N0} ms" : "ping unknown");
            StageDetails[1].text = _version + (_mod != null ? $" · {(_viewQuestion ? "Awaiting approval" : "Downloading")} {_mod}: {_modBytes:N0} / {_modTotal:N0} B" : "");
            StageDetails[2].text = $"{_world} · {JoinPresentation.Bytes(Metrics.BytesReceived)} from host";
            StageDetails[3].text = _pending < 0 ? "Waiting for the scene and join position" : $"Terrain {g.TerrainDone:N0} / {g.TerrainNeeded:N0} tiles · Roads {g.RoadsDone:N0} / {g.RoadsNeeded:N0} chunks";
            StageDetails[4].text = !_manifest ? "Waiting for the initial object manifest" : $"Applied {State.Count:N0} / {Expected.Count:N0} · received {Metrics.Received:N0} / {Expected.Count:N0} · " + (State.Objects ? "Initial sync complete" : _ended ? "Host finished; waiting for application" : "Waiting for records and end marker");
            StageDetails[5].text = _placement; StageDetails[6].text = "";
            _stagesHeight = 0;
            for (int i = 0; i < 7; i++)
            {
                if (_viewPreview) StageDetails[i].text = JoinLoadingPreview.Details[i];
                Stages[i].text = JoinLoadingState.Names[i];
                StageY[i] = _stagesHeight;
                StageNameHeight[i] = Height(Stages[i], _columnWidth - 106 * s, StepBold);
                // Retain every fact when a mod or host name needs wrapping; measure only at the text cadence.
                StageDetailHeight[i] = i > _viewStage || StageDetails[i].text.Length == 0 ? 0 : Height(StageDetails[i], _columnWidth - 30 * s, Small);
                _stagesHeight += JoinPresentation.RowHeight(StageNameHeight[i], StageDetailHeight[i], s);
                double ms = _viewPreview ? (i < _viewStage ? 800 : 1800) : i < State.Stage ? State.Ms[i] : (now - State.StageAt) * 1000;
                Timers[i].text = i <= _viewStage ? $"{ms / 1000:F1} s" : "";
            }
            bool manifest = _viewPreview || _manifest;
            int received = _viewPreview ? JoinLoadingPreview.Received : Metrics.Received;
            int applied = _viewPreview ? JoinLoadingPreview.Applied : State.Count;
            int expected = _viewPreview ? JoinLoadingPreview.Expected : Expected.Count;
            Counters.text = manifest ? $"{applied:N0} / {expected:N0}" : "0"; Objects.text = manifest ? "objects" : "objects · waiting for the list";
            _counterWidth = JoinLoadingSkin.Counter.CalcSize(Counters).x;
            Applied.text = $"{received:N0} received · {applied:N0} applied";
            KindCounts[0].text = (_viewPreview ? 8 : Metrics.Vehicles).ToString("N0");
            KindCounts[1].text = (_viewPreview ? 132 : Metrics.ItemsParts).ToString("N0");
            KindCounts[2].text = (_viewPreview ? 14 : Metrics.Other).ToString("N0");
            _feedRows = _viewPreview ? 6 : Math.Max(1, Metrics.ShownCount);
            for (int i = 0; i < 6; i++) Feed[i].text = OverlayStyle.Truncate(_viewPreview ? JoinLoadingPreview.Names[i] : i < Metrics.ShownCount ? Metrics.Names[i] : i == 0 ? "Waiting for shared objects" : "", rightWidth, Body);
            _receivedHeight = (236 + _feedRows * 24) * s;
            QuestionText.text = _viewQuestion ? (_viewPreview ? JoinLoadingPreview.QuestionText : MpScreen.PendingQuestion) : "";
            _questionHeight = _viewQuestion ? Height(QuestionText, rightWidth, Body) : 0;
            float rightHeight = _viewQuestion ? _questionHeight + 12 * s + JoinPresentation.ButtonsHeight(rightWidth, s, 2) : _receivedHeight;
            _bodyHeight = _twoColumns ? Math.Max(_stagesHeight, rightHeight) : _stagesHeight + 24 * s + rightHeight;
            DetailText.text = JoinPresentation.FooterStatus(_viewPreview ? JoinLoadingPreview.Details[_viewStage] : Reason(),
                Stages[_viewStage].text, StageDetails[_viewStage].text);
            Footer.text = _viewPreview ? "2.41 MB received · 312 KB/s" : $"{JoinPresentation.Bytes(Metrics.BytesReceived)} received · {JoinPresentation.Bytes((long)Metrics.Rate)}/s";
            _detailHeight = DetailText.text.Length == 0 ? 0 : 28 * s; _transferHeight = 24 * s;
            DetailText.text = OverlayStyle.Truncate(DetailText.text, width, Line);
            Footer.text = OverlayStyle.Truncate(Footer.text, Math.Max(1, width - 80 * s), Line);
            _buttonY = 44 * s + _transferHeight + _detailHeight + 12 * s + (_viewStuck ? 26 * s : 0);
            _footerHeight = _buttonY + JoinPresentation.ButtonsHeight(width, s, _viewStuck ? 2 : 1) + 34 * s;
            float progress = _viewPreview ? _viewStage / 6f : Progress();
            PercentText.text = $"{progress * 100:F0} %";
            // Re-measure with a gutter only if the complete measured card cannot fit.
            if (!scrollbar && _headerHeight + _bodyHeight + _footerHeight + 72 * s > Screen.height - 2 * JoinPresentation.Snap(32 * s))
                RebuildText(width, now, true);
            else if (scrollbar && !wholeScroll && _headerHeight + _footerHeight + 90 * s > Screen.height - 2 * JoinPresentation.Snap(32 * s) - 72 * s)
                RebuildText(viewportWidth, now, true, true);
        }
        internal static void Draw()
        {
            if (!Showing) return;
            Ensure();
            int depth = GUI.depth; var colour = GUI.color; var matrix = GUI.matrix;
            GUI.depth = -10000; GUI.color = Color.white; GUI.matrix = Matrix4x4.identity;
            try { DrawScreen(); }
            finally { GUI.depth = depth; GUI.color = colour; GUI.matrix = matrix; }
            if (!_viewPreview && Event.current.isMouse) Event.current.Use();
        }
        private static void DrawScreen()
        {
            float s = Scale;
            var paper = JoinPresentation.Paper(Screen.width, Screen.height, s, Screen.height);
            float innerWidth = Math.Max(1, paper.W - 72 * s);
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _nextText) RebuildText(innerWidth, now);
            var frame = Rect(JoinPresentation.Paper(Screen.width, Screen.height, s, _headerHeight + _bodyHeight + _footerHeight + 72 * s));
            float innerHeight = Math.Max(1, frame.height - 72 * s);
            if (Event.current.type == EventType.Repaint) { Background(); Panel(frame); }
            var inner = Rect(frame.x + 36 * s, frame.y + 36 * s, innerWidth, innerHeight);
            // Very short screens scroll the full stack so header/footer actions remain reachable.
            bool outer = innerHeight < _headerHeight + _footerHeight + 90 * s;
            float bodyView = outer ? _bodyHeight : Math.Min(_bodyHeight, innerHeight - _headerHeight - _footerHeight);
            float natural = _headerHeight + bodyView + _footerHeight;
            if (outer) _outerScroll = GUI.BeginScrollView(inner, _outerScroll, Rect(0, 0, _bodyWidth, natural));
            else GUI.BeginGroup(inner);
            Outlined(Rect(1, 1, Math.Max(1, _width - (_width < 450 * s ? 0 : 178 * s) - 2), 64 * s), Title, JoinLoadingSkin.Title, Accent);
            Label(Rect(0, 64 * s, _width, _hostHeight), Host, Ink, JoinLoadingSkin.Host);
            var clock = Rect(Math.Max(0, _width - 150 * s), _width < 450 * s ? 94 * s : 0, Math.Min(_width, 150 * s), 58 * s);
            if (Event.current.type == EventType.Repaint) Bordered(clock, Dark, Charcoal, s);
            Label(clock, ClockText, PaleGreen, Clock);
            if (Event.current.type == EventType.Repaint) Fill(Rect(0, _headerHeight - 12 * s, _width, s), Border);
            float rightWidth = _twoColumns ? _bodyWidth - _columnWidth - 40 * s : _bodyWidth;
            bool bodyScroll = !outer && bodyView < _bodyHeight;
            var body = Rect(0, _headerHeight, outer ? _bodyWidth : _width, bodyView);
            if (bodyScroll) _dialogScroll = GUI.BeginScrollView(body, _dialogScroll, Rect(0, 0, _bodyWidth, _bodyHeight));
            else GUI.BeginGroup(body);
            DrawStages(now);
            float rightX = _twoColumns ? _columnWidth + 40 * s : 0, rightY = _twoColumns ? 0 : _stagesHeight + 24 * s;
            if (_twoColumns && Event.current.type == EventType.Repaint) Fill(Rect(_columnWidth + 20 * s, 0, s, _bodyHeight), Border);
            if (_viewQuestion) DrawQuestion(rightX, rightY, rightWidth); else DrawReceived(rightX, rightY, rightWidth);
            if (bodyScroll) GUI.EndScrollView(); else GUI.EndGroup();
            DrawFooter(_headerHeight + bodyView);
            if (outer) GUI.EndScrollView(); else GUI.EndGroup();
            if (_viewPreview) Label(Rect(frame.x, frame.y - 28 * s, frame.width, 24 * s), PreviewLabel, PaleGreen, Small);
        }
        private static void DrawStages(double now)
        {
            float s = Scale;
            for (int i = 0; i < 7; i++)
            {
                bool done = i < _viewStage || _viewReady, current = i == _viewStage && !_viewReady;
                float y = StageY[i];
                if (done) Label(Rect(0, y, 26 * s, Step.lineHeight), Check, Done, Mark);
                else if (Event.current.type == EventType.Repaint)
                {
                    if (current)
                    {
                        for (int dot = 0; dot < 3; dot++) Fill(Rect(dot * 8 * s, y + (Step.lineHeight - 4 * s) / 2, 4 * s, 4 * s), dot == (int)(now * 4) % 3 ? Accent : Muted);
                    }
                    else Fill(Rect(0, y + (Step.lineHeight - 6 * s) / 2, 6 * s, 6 * s), Muted);
                }
                Label(Rect(30 * s, y, _columnWidth - 106 * s, StageNameHeight[i]), Stages[i], current ? Current : done ? Ink : Muted, current ? StepBold : Step);
                Label(Rect(_columnWidth - 70 * s, y + 3 * s, 70 * s, 26 * s), Timers[i], current ? Current : Detail, Numeric);
                if ((done || current) && StageDetailHeight[i] > 0)
                    Label(Rect(30 * s, y + StageNameHeight[i], _columnWidth - 30 * s, StageDetailHeight[i]), StageDetails[i], Detail, Small);
            }
        }
        private static void DrawReceived(float x, float y, float width)
        {
            float s = Scale;
            Label(Rect(x, y, width, 32 * s), ReceivedTitle, Ink, Heading);
            bool manifest = _viewPreview || _manifest;
            Label(Rect(x, y + 36 * s, width, 50 * s), Counters, Ink, Counter);
            if (_counterWidth + 80 * s <= width) Label(Rect(x + _counterWidth + 12 * s, y + 56 * s, width - _counterWidth - 12 * s, 24 * s), Objects, Detail, Small);
            Label(Rect(x, y + 90 * s, width, 24 * s), Applied, Detail, Small);
            for (int i = 0; i < 3; i++)
            {
                Label(Rect(x, y + (122 + 28 * i) * s, width - 60 * s, 26 * s), Kinds[i], Ink, Body);
                Label(Rect(x + width - 60 * s, y + (122 + 28 * i) * s, 60 * s, 26 * s), KindCounts[i], Detail, Numeric);
            }
            Label(Rect(x, y + 208 * s, width, 22 * s), Latest, Muted, JoinLoadingSkin.Latest);
            for (int i = 0; i < _feedRows; i++) Label(Rect(x, y + (234 + 24 * i) * s, width, 26 * s), Feed[i], Color.Lerp(Ink, Muted, i / 5f), Body);
        }
        private static void DrawQuestion(float x, float y, float width)
        {
            float s = Scale;
            Label(Rect(x, y, width, _questionHeight), QuestionText, Ink, Body);
            var yes = JoinPresentation.Button(width, y + _questionHeight + 12 * s, s, 0, 2); yes.X += x;
            var no = JoinPresentation.Button(width, y + _questionHeight + 12 * s, s, 1, 2); no.X += x;
            if (Click(Rect(yes), DownloadText, 3, true) && !_viewPreview) MpScreen.AskProbe("yes");
            if (Click(Rect(no), DeclineText, 4) && !_viewPreview) MpScreen.AskProbe("no");
        }
        private static void DrawFooter(float y)
        {
            float s = Scale;
            if (Event.current.type == EventType.Repaint) JoinLoadingSkin.Progress(Rect(0, y + 16 * s, _width, 18 * s), _viewPreview ? _viewStage / 6f : Progress());
            Label(Rect(0, y + 44 * s, Math.Max(1, _width - 80 * s), _transferHeight), Footer, Detail, Line);
            Label(Rect(_width - 80 * s, y + 44 * s, 80 * s, _transferHeight), PercentText, Ink, JoinLoadingSkin.Percent);
            if (_detailHeight > 0) Label(Rect(0, y + 44 * s + _transferHeight, _width, _detailHeight), DetailText, Detail, Line);
            float buttonY = y + _buttonY;
            if (_viewStuck) Label(Rect(0, buttonY - 26 * s, _width, 24 * s), StuckText, Alert, Line);
            int count = _viewStuck ? 2 : 1;
            if (Click(Rect(JoinPresentation.Button(_width, buttonY, s, 0, count)), CancelText, 1) && !_viewPreview) Cancel();
            if (_viewStuck && Click(Rect(JoinPresentation.Button(_width, buttonY, s, 1, 2)), ContinueText, 2, true) && !_viewPreview) Probe("continue");
            Label(Rect(0, y + _footerHeight - 22 * s, _width, 22 * s), CreditText, Muted, Credit);
        }
    }
}
