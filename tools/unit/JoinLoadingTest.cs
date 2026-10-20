using System;
using System.Collections.Generic;
using System.Linq;
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
        Console.WriteLine("PASS JoinLoading: " + checks + " checks"); return 0;
    }
}
