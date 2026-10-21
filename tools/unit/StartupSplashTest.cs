using System;
using System.IO;
using TLDRevamp;
using TLDRevamp.Preloader;

static class StartupSplashTest
{
    static int fails;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
    static bool Near(double a, double b) => Math.Abs(a - b) < .000000001;
    static bool Enabled(string cfg) { using (var reader = new StringReader(cfg)) return SplashOptions.Enabled(reader); }
    static int Monitor(int primary, params string[] args) => SplashOptions.MonitorIndex(args, 3, primary);

    static int Main()
    {
        var p = new SplashProgress();
        var s = p.Snapshot();
        Check("preloader starts at zero with real stage", Near(s.Fraction, 0) && s.Status == "Loading BepInEx" && !s.Closing);
        p.Report(StartupSplash.Patching, 0, 230); s = p.Snapshot();
        Check("patch start accounts for initial setup only", Near(s.Fraction, .05) && s.Status == "Patching the game  0 / 230");
        p.Report(StartupSplash.Patching, 115, 230); s = p.Snapshot();
        Check("half the patch classes advances half their weight", Near(s.Fraction, .30) && s.Status == "Patching the game  115 / 230");
        p.Report(StartupSplash.Patching, 114, 230);
        Check("late class counts cannot rewind bar or label", Near(p.Snapshot().Fraction, .30) && p.Snapshot().Status == s.Status);
        p.Report(StartupSplash.Patching, 116, 231);
        Check("a changed denominator cannot falsify class progress", Near(p.Snapshot().Fraction, .30) && p.Snapshot().Status == s.Status);
        p.Report(StartupSplash.Loading, 0, 0);
        Check("late stages cannot rewind patching", Near(p.Snapshot().Fraction, .30) && p.Snapshot().Status == s.Status);
        double last = p.Snapshot().Fraction;
        bool increasing = true, counts = true;
        for (int n = 116; n <= 230; n++)
        {
            p.Report(StartupSplash.Patching, n, 230); s = p.Snapshot();
            increasing &= s.Fraction > last && s.Fraction <= .55;
            counts &= s.Status == "Patching the game  " + n + " / 230";
            last = s.Fraction;
        }
        Check("each completed class advances within the measured weight", increasing && Near(last, .55));
        Check("every class label has the real n / N", counts);
        p.Report(StartupSplash.Starting, 0, 0); s = p.Snapshot();
        Check("remaining Awake follows patches without a jump", Near(s.Fraction, .55) && s.Status == "Starting RevaMP" && !s.Closing);
        p.Report(StartupSplash.Patching, 230, 230);
        Check("delayed patches cannot replace Starting RevaMP", p.Snapshot().Status == s.Status);
        p.Report(StartupSplash.Waiting, 0, 0); s = p.Snapshot();
        Check("Awake completion leaves measured first-frame share", Near(s.Fraction, .60) && s.Status == "Starting the game" && !s.Closing);
        Check("repeated snapshots do not invent timed progress", Near(p.Snapshot().Fraction, .60));
        p.Report(StartupSplash.Starting, 0, 0);
        Check("late Awake stage cannot rewind game startup", p.Snapshot().Status == s.Status && Near(p.Snapshot().Fraction, .60));
        p.Report(StartupSplash.Finished, 0, 0); s = p.Snapshot();
        Check("first Update alone completes and requests fade", Near(s.Fraction, 1) && s.Closing && !s.Failed);
        p.Report(StartupSplash.Loading, 0, 0);
        Check("finished splash cannot reopen", p.Snapshot().Closing && Near(p.Snapshot().Fraction, 1));

        p = new SplashProgress();
        p.Report(StartupSplash.Patching, -1, 10);
        Check("negative count clamps to zero", Near(p.Snapshot().Fraction, .05) && p.Snapshot().Status == "Patching the game  0 / 10");
        p.Report(StartupSplash.Patching, int.MaxValue, 10);
        Check("count cannot exceed N", Near(p.Snapshot().Fraction, .55) && p.Snapshot().Status == "Patching the game  10 / 10");
        p = new SplashProgress(); p.Report(StartupSplash.Patching, 0, 0);
        Check("empty patch set is complete without division by zero", Near(p.Snapshot().Fraction, .55) && p.Snapshot().Status == "Patching the game  0 / 0");
        p = new SplashProgress(); p.Report(StartupSplash.Waiting, 0, 0);
        Check("a skipped report can still advance to a real later stage", Near(p.Snapshot().Fraction, .60));
        p.Report(StartupSplash.Failed, 0, 0); s = p.Snapshot();
        p.Report(StartupSplash.Finished, 0, 0);
        Check("exception closes immediately without claiming completion", s.Closing && s.Failed && Near(s.Fraction, .60) && Near(p.Snapshot().Fraction, .60));
        p = new SplashProgress(); p.Report(-1, 0, 0); p.Report(99, 0, 0);
        Check("unknown stages cannot change progress", Near(p.Snapshot().Fraction, 0) && !p.Snapshot().Closing);

        try
        {
            p = new SplashProgress();
            AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, new Action<int, int, int>(p.Report));
            StartupSplash.Report(StartupSplash.Patching, 7, 10);
            Check("AppDomain bridge carries framework-only stage and counts", p.Snapshot().Status == "Patching the game  7 / 10" && Near(p.Snapshot().Fraction, .40));
            AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, null);
            StartupSplash.Report(StartupSplash.Finished);
            Check("absent preloader or a closed bridge is harmless", !p.Snapshot().Closing);
            AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, new Action<int, int, int>((a, b, c) => throw new IOException("test")));
            StartupSplash.Report(StartupSplash.Patching);
            Check("splash reporting cannot throw into initialization", true);
        }
        finally { AppDomain.CurrentDomain.SetData(StartupSplash.DataKey, null); }

        Check("missing setting defaults on", Enabled("") && Enabled("[General]\nOther = false"));
        Check("only the General key controls splash", Enabled("StartupSplash=false\n[Debug]\nStartupSplash=false\n[General]\nOther=false"));
        Check("BepInEx comments and outer whitespace", !Enabled("  ## Settings file\r\n [General] \r\n # StartupSplash = true\r\n StartupSplash = false \r\n"));
        Check("explicit true and bool casing", Enabled("[General]\nStartupSplash = TRUE") && !Enabled("[General]\nStartupSplash = FaLsE"));
        Check("exact key and section names like BepInEx", Enabled("[general]\nStartupSplash=false\n[General]\nstartupSplash=false\nStartupSplashExtra=false"));
        Check("last duplicate wins like BepInEx", Enabled("[General]\nStartupSplash=false\nStartupSplash=true") && !Enabled("[General]\nStartupSplash=true\n[Other]\nStartupSplash=true\n[General]\nStartupSplash=false"));
        Check("invalid last value defaults on", Enabled("[General]\nStartupSplash=false\nStartupSplash=invalid"));
        Check("malformed values and unsupported inline comments default on", Enabled("[General]\nStartupSplash=") && Enabled("[General]\nStartupSplash=false # comment") && Enabled("[General]\nStartupSplash=0"));
        string dir = Path.Combine(Path.GetTempPath(), "revamp-splash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "tldrevamp.core.cfg");
            Check("missing config defaults on", SplashOptions.EnabledFile(path));
            File.WriteAllText(path, "[General]\nStartupSplash=false", new System.Text.UTF8Encoding(true));
            Check("plain cfg reader handles UTF-8 BOM", !SplashOptions.EnabledFile(path));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check("unreadable config defaults on", SplashOptions.EnabledFile(path));
            Check("invalid config path defaults on", SplashOptions.EnabledFile(null) && SplashOptions.EnabledFile(dir));
        }
        finally { Directory.Delete(dir, true); }

        Check("normal player shows splash", !SplashOptions.Skip(new[] { "game.exe", "-monitor", "2" }) && !SplashOptions.Skip(Array.Empty<string>()));
        Check("batchmode skips", SplashOptions.Skip(new[] { "game.exe", "-batchmode" }));
        Check("nographics skips independently", SplashOptions.Skip(new[] { "game.exe", "-nographics" }));
        Check("headless rules ignore casing", SplashOptions.Skip(new[] { "-BATCHMODE" }) && SplashOptions.Skip(new[] { "-NoGraphics" }));
        Check("skip flags match complete tokens only", !SplashOptions.Skip(new[] { "game-batchmode.exe", "--batchmode", "-nographics=false", "notes -batchmode" }));
        Check("monitor defaults to primary even when not first", Monitor(2, "game.exe") == 2 && Monitor(2) == 2);
        Check("Unity monitor argument is one-based", Monitor(2, "-monitor", "1") == 0 && Monitor(0, "-monitor", "2") == 1 && Monitor(0, "-monitor", "3") == 2);
        Check("monitor flag ignores casing", Monitor(0, "-MONITOR", "2") == 1);
        Check("invalid and out of range monitors fall back to primary", Monitor(1, "-monitor", "0") == 1 && Monitor(1, "-monitor", "-1") == 1 && Monitor(1, "-monitor", "4") == 1 && Monitor(1, "-monitor", "2147483648") == 1 && Monitor(1, "-monitor", "bad") == 1);
        Check("missing monitor value falls back to primary", Monitor(2, "-monitor") == 2 && Monitor(2, "-monitor", "-batchmode") == 2);
        Check("monitor flag matches whole token", Monitor(2, "--monitor", "1") == 2 && Monitor(2, "-monitor=1") == 2);
        Check("no monitors has no selection", SplashOptions.MonitorIndex(new[] { "-monitor", "1" }, 0, 0) == -1);
        Check("invalid primary index falls back to first or last available", Monitor(-1) == 0 && Monitor(8) == 2);
        return fails == 0 ? 0 : 1;
    }
}
