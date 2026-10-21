using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Globalization;
using TLDRevamp;
using TLDRevamp.Net;

static class JoinLoadingTest
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main()
    {
        // Every readiness combination, then removal of a previously satisfied geometry condition.
        for (int bits = 0; bits < 64; bits++)
        {
            float audio = .371f; var s = new JoinLoadingState(); var events = new List<int>();
            s.Begin(true, 0, audio, v => audio = v, (stage, ms) => events.Add(stage));
            bool B(int n) => (bits & (1 << n)) != 0;
            s.Observe(1, B(0), B(1), B(2), B(3), B(4), B(5), 5, 5);
            Check(s.Ready == (bits == 63), "all six facts required: " + bits);
            Check(audio == (bits == 63 ? .371f : 0), "audio follows readiness");
            Check(events.SequenceEqual(Enumerable.Range(0, events.Count)), "out-of-order facts never skip stages");
        }
        float volume = .247f; var done = new List<(int stage, double ms)>(); var ordered = new JoinLoadingState();
        ordered.Begin(true, 10, volume, v => volume = v, (i, ms) => done.Add((i, ms)));
        for (int i = 0; i < 6; i++)
        {
            ordered.Observe(11 + i, true, i >= 1, i >= 2, i >= 3, i >= 4, i >= 5, i >= 4 ? 100 : 0, 100);
            Check(ordered.Stage == i + 1, "stage order");
        }
        Check(done.Take(6).All(x => x.ms == 1000) && done.Last() == (6, 6000d), "per-stage durations and total");
        Check(ordered.Ready && !ordered.Visible && volume == .247f, "normal completion restores exact volume");
        var regressed = new JoinLoadingState();
        regressed.Begin(true, 0, 1, _ => { }, null);
        regressed.Observe(1, true, true, true, true, true, false, 100, 100);
        regressed.Observe(2, true, true, true, false, true, true, 100, 100);
        Check(!regressed.Ready && regressed.Visible, "geometry regression blocks ready");
        regressed.Observe(3, true, true, true, true, true, true, 100, 100);
        Check(regressed.Ready, "regressed fact recovers");
        var stuck = new JoinLoadingState(); volume = .63f;
        stuck.Begin(true, 0, volume, v => volume = v, null);
        Check(!stuck.Stuck(59.999) && stuck.Stuck(60), "timeout boundary");
        stuck.Continue(59); Check(stuck.Visible && volume == 0, "cannot bypass before stuck");
        stuck.Observe(60, true, true, true, true, false, false, 1, 100);
        Check(!stuck.Stuck(119.999) && stuck.Stuck(120), "object progress resets timeout");
        stuck.Progress(120); Check(!stuck.Stuck(179.99), "download/backlog progress resets timeout");
        stuck.Continue(180); Check(!stuck.Visible && !stuck.Ready && volume == .63f, "explicit bypass restores audio without declaring ready");
        foreach (string path in new[] { "cancelled", "disconnect", "kicked", "error", "menu", "quit", "disabled", "destroyed", "stopped" })
        {
            int writes = 0; volume = .1729f; var s = new JoinLoadingState();
            s.Begin(true, 5, volume, v => { writes++; volume = v; }, null);
            s.Close(path); s.Close(path);
            Check(!s.Visible && !s.Muted && !s.Ready && volume == .1729f && writes == 2, "idempotent restoration: " + path);
        }
        int calls = 0; var off = new JoinLoadingState();
        off.Begin(false, 0, .3f, _ => calls++, (_, __) => calls++);
        off.Observe(100, true, true, true, true, true, true, 1, 1); off.Close("quit"); off.Continue(100);
        Check(!off.Visible && !off.Muted && !off.Ready && calls == 0, "setting off is inert");

        var geometry = new JoinGeometryCounts();
        Check(!geometry.Ready && geometry.Pending == 0, "empty generation request is not ready");
        geometry.Road(true); Check(!geometry.Ready, "roads alone cannot satisfy terrain readiness");
        geometry.Terrain(true); geometry.Terrain(false); geometry.Road(false);
        Check(geometry.TerrainDone == 1 && geometry.TerrainNeeded == 2 && geometry.RoadsDone == 1 && geometry.RoadsNeeded == 2 && geometry.Pending == 2 && !geometry.Ready, "independent requested/finished geometry counts");
        geometry = default; geometry.Terrain(true); geometry.Road(true);
        Check(geometry.Ready, "all requested meshes/colliders/chunks finished");

        var metrics = new JoinLoadingMetrics();
        for (uint i = 1; i <= 120; i++) metrics.Record(i, (int)i, (JoinObjectKind)(i % 3));
        Check(metrics.Received == 120 && metrics.FeedCount == 6 && metrics.Vehicles == 40 && metrics.ItemsParts == 40 && metrics.Other == 40, "bounded ring retains all measured kind counters");
        for (int i = 0; i < 6; i++) Check(metrics.Recent(i).Net == 120 - i, "feed is newest first after many wraps");
        Check(!metrics.Record(1, 1, JoinObjectKind.Other) && metrics.Received == 120 && metrics.Recent(0).Net == 120, "duplicate record never inflates counters or reorders feed");
        metrics.PublishFeed(row => "prefab \"" + row.Prefab + "\"\n");
        metrics.Geometry = geometry; metrics.BytesReceived = 1234567; metrics.Rate = 123.5; metrics.ElapsedMs = 2400;
        // Exercise the production additive serializer in a non-English locale, including escaped names.
        var culture = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var json = new StringBuilder("{\"visible\":true,\"stage\":4,\"stages\":[],\"muted\":true,\"audioVolume\":0,\"ready\":false,\"reason\":\"waiting\",\"scene\":true,\"geometry\":true,\"ground\":false,\"initialSync\":false");
        metrics.AppendProbe(json); json.Append('}');
        using (var doc = JsonDocument.Parse(json.ToString()))
        {
            var root = doc.RootElement;
            foreach (string key in new[] { "visible", "stage", "stages", "muted", "audioVolume", "ready", "reason", "scene", "geometry", "ground", "initialSync" }) Check(root.TryGetProperty(key, out _), "legacy probe field retained: " + key);
            Check(root.GetProperty("bytesReceived").GetInt64() == 1234567 && root.GetProperty("receiveBytesPerSecond").GetDouble() == 123.5 && root.GetProperty("elapsedMs").GetDouble() == 2400, "measured transfer facts serialized");
            Check(root.GetProperty("terrainDone").GetInt32() == 1 && root.GetProperty("terrainNeeded").GetInt32() == 1 && root.GetProperty("roadsDone").GetInt32() == 1 && root.GetProperty("roadsNeeded").GetInt32() == 1, "geometry totals serialized separately");
            Check(root.GetProperty("receivedObjects").GetInt32() == 120 && root.GetProperty("receivedVehicles").GetInt32() == 40 && root.GetProperty("receivedItemsParts").GetInt32() == 40 && root.GetProperty("receivedOther").GetInt32() == 40, "kind counts serialized");
            var feed = root.GetProperty("recentObjects");
            Check(feed.GetArrayLength() == 6 && feed[0].GetProperty("name").GetString() == "prefab \"120\"\n" && feed[0].GetProperty("net").GetInt32() == 120, "bounded named feed JSON");
        }
        CultureInfo.CurrentCulture = culture;
        // A receipt cannot mismatch the previous published names while waiting for the next 4 Hz snapshot.
        metrics.Record(121, 121, JoinObjectKind.ItemPart);
        json.Clear().Append('{'); json.Append("\"test\":true"); metrics.AppendProbe(json); json.Append('}');
        using (var doc = JsonDocument.Parse(json.ToString())) Check(doc.RootElement.GetProperty("recentObjects")[0].GetProperty("net").GetInt32() == 120, "feed snapshot changes only on publication");
        metrics.Clear(); Check(metrics.Received == 0 && metrics.FeedCount == 0 && metrics.ShownCount == 0 && metrics.Vehicles == 0 && metrics.Names.All(x => x == null), "new join clears feed and counters");
        metrics.Record(9999, -1, JoinObjectKind.Other, false);
        Check(metrics.Received == 1 && metrics.FeedCount == 0, "records known before the manifest count without inventing a recent arrival");
        metrics.Clear();
        Check(JoinPresentation.TwoColumns(1048, 1), "menu card uses two columns at 1080p");
        Check(JoinPresentation.TwoColumns(900, 1) && !JoinPresentation.TwoColumns(899, 1), "inner-width threshold is inclusive");
        Check(JoinPresentation.TwoColumns(1440, 1.6f) && !JoinPresentation.TwoColumns(1439, 1.6f), "column threshold follows overlay scale");
        Check(JoinPresentation.RowHeight(26, 23, 1) == 57 && JoinPresentation.RowHeight(26, 0, 1) == 34 && JoinPresentation.RowHeight(60, 46, 1) == 114, "row pitch is the name plus visible detail plus eight pixels");
        Check(JoinPresentation.FooterStatus("Waiting for records", "Receiving shared objects", "Waiting for records") == "" &&
            JoinPresentation.FooterStatus("Connecting to the host", "Connecting to the host", "direct") == "" &&
            JoinPresentation.FooterStatus("", "Ready", "") == "", "footer suppresses repeated detail, repeated step name and empty status");
        Check(JoinPresentation.FooterStatus("Waiting for ground", "Receiving shared objects", "128 / 342") == "Waiting for ground", "footer retains a distinct waiting reason");
        foreach (var screen in new[] { (1920f,1080f), (1280f,720f), (800f,600f), (360f,240f), (240f,360f), (3440f,1440f) })
        {
            float s = Math.Max(.8f, screen.Item2 / 1080f);
            var paper = JoinPresentation.Paper(screen.Item1, screen.Item2, s, 780 * s);
            Check(paper.X >= Math.Round(32*s) && paper.Y >= Math.Round(32*s) && paper.X + paper.W <= screen.Item1 - Math.Round(32*s) && paper.Y + paper.H <= screen.Item2 - Math.Round(32*s), "centred card respects minimum margin");
            Check(Math.Abs(paper.X*2 + paper.W - screen.Item1) <= 1 && Math.Abs(paper.Y*2 + paper.H - screen.Item2) <= 1, "card centred and pixel-snapped");
            for (int count = 1; count <= 2; count++)
            {
                float width = paper.W - 72*s;
                float lastEnd = -1, lastY = -1;
                for (int i = 0; i < count; i++)
                {
                    var button = JoinPresentation.Button(width, 80*s, s, i, count);
                    Check(button.X >= 0 && (button.Y > lastY || button.X >= lastEnd) && button.X + button.W <= Math.Round(width) && button.H == Math.Round(56*s), "plaque buttons fit, align right, and never overlap");
                    Check(button.X == Math.Round(button.X) && button.Y == Math.Round(button.Y), "button positions are pixel snapped");
                    lastEnd = button.X + button.W;
                    lastY = button.Y;
                }
            }
        }
        var card = JoinPresentation.Paper(1920,1080,1,780);
        Check(card.X == 400 && card.Y == 150 && card.W == 1120 && card.H == 780, "1080p paper grows to its measured content height");
        Check(JoinPresentation.Paper(1920,1080,1,1200).H == 1016, "oversized paper stops at the screen margins");
        var fullButton = JoinPresentation.Button(1048, 0, 1, 0, 1);
        Check(fullButton.X == 828 && fullButton.W == 220 && fullButton.H == 56, "specified 1080p plaque dimensions");
        Check(JoinPresentation.Button(414, 0, 1, 1, 2).Y == 68 && JoinPresentation.ButtonsHeight(414,1,2) == 124, "question buttons stack into full-sized plaques in a narrow right column");
        Check(JoinPresentation.Forward(.75f, .5f) == .75f && JoinPresentation.Forward(.75f, 2) == 1 && JoinPresentation.Forward(0, -1) == 0, "progress clamped and never reverses");
        foreach (float scale in new[] { .65f, 1, 2 }) foreach (float width in new[] { 8f, 160f })
        {
            float area = 0;
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
            {
                JoinPresentation.Slice(col, row, new JoinRect(10, 20, width, width / 2), new JoinRect(32, 64, 80, 60),
                    5, 6, 7, 8, scale, 256, 512, out var dst, out var uv);
                Check(dst.X >= 10 - .001f && dst.Y >= 20 - .001f && dst.W >= 0 && dst.H >= 0 && dst.X + dst.W <= 10 + width + .001f && dst.Y + dst.H <= 20 + width / 2 + .001f, "nine-slice destination remains in panel, including undersized panels");
                Check(uv.X >= 32f / 256 && uv.Y >= 64f / 512 && uv.W >= 0 && uv.H >= 0 && uv.X + uv.W <= 112f / 256 + .001f && uv.Y + uv.H <= 124f / 512 + .001f, "nine-slice samples atlas rect only");
                area += dst.W * dst.H;
                if (row == 0) Check(Math.Abs(dst.Y + dst.H - (20 + width / 2)) < .001f && uv.Y == 64f / 512, "bottom source border is placed at IMGUI bottom");
            }
            Check(Math.Abs(area - width * width / 2) < .01f, "nine slices cover destination without gaps or overlap");
        }
        JoinPresentation.Slice(1, 1, new JoinRect(0, 0, 100, 100), new JoinRect(8, 16, 80, 60), 0, 0, 0, 0, 1, 128, 128, out var full, out var fullUv);
        Check(full.W == 100 && full.H == 100 && fullUv.X == 8f / 128 && fullUv.W == 80f / 128, "borderless poster stretches its full texture rect");

        // The manifest reserves deduplication storage before the streamed records arrive.
        metrics.Reserve(1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (uint i = 1; i <= 1000; i++) { metrics.Record(i, 1, JoinObjectKind.ItemPart); metrics.Record(i, 1, JoinObjectKind.ItemPart); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated && metrics.FeedCount == 6, "warmed ring and counters allocate nothing on receipt");
        Console.WriteLine("PASS JoinLoading: " + checks + " checks"); return 0;
    }
}

namespace TLDRevamp
{
    // The metrics serializer uses the same JSON boundary as the bridge; no Unity dependency in this fixture.
    internal static class Json
    {
        internal static string Num(double n) => n.ToString("F3", CultureInfo.InvariantCulture);
        internal static string Str(string value) => JsonSerializer.Serialize(value ?? "");
    }
}
