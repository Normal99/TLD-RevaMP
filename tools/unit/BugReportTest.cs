#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using System.IO;
using TLDRevamp;

// The bug report's pure parts: REPORT.md, the world files, /bug, the key chord, and the recorder's copy of its rows.
static class BugReportTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static BugReportLogic.Facts Sample() => new BugReportLogic.Facts
    {
        Note = "  barrel fell through the floor  ", Version = "0.69.1", Commit = "abc1234", Build = "deadbeef", Game = "1.2.3", Protocol = 55, Time = "2026-10-28 12:00",
        Role = "host", InSession = true, X = 1234.56, Y = 12, Z = -7, Seated = true,
        ReproPresent = true, ReproRows = 4200, ReproSeconds = 420, WorldFile = "TLDsaveData_26_10_27_0158.tlds", WorldStartCopied = true,
    };

    static void ReportText()
    {
        string r = BugReportLogic.Report(Sample());
        Check(r.Contains("barrel fell through the floor") && !r.Contains("  barrel"), "the note is trimmed and shown");
        Check(r.Contains("0.69.1") && r.Contains("abc1234") && r.Contains("wire protocol 55"), "build facts");
        Check(r.Contains("x 1234.6") && r.Contains("seated in a vehicle"), "position and seat");
        Check(r.Contains("4200 rows over 7.0 min") && r.Contains("complete"), "recording summary");
        Check(r.Contains("world/start/") && r.Contains("TLDsaveData_26_10_27_0158.tlds"), "starting world is named");
        Check(r.Contains("bugrepro.py"), "the replay command");
        Check(!r.Contains("/home/") && !r.Contains("C:\\"), "no paths");

        var f = Sample(); f.Note = "  "; f.ReproPresent = false; f.WorldStartCopied = false; f.WorldFile = null; f.Role = "client"; f.ReproFull = false;
        r = BugReportLogic.Report(f);
        Check(r.Contains("(no note)"), "an empty note says so");
        Check(r.Contains("could not be replayed") || r.Contains("cannot be replayed"), "no recording says it cannot be replayed");
        Check(r.Contains("A client has no world save"), "client world text");

        f = Sample(); f.WorldStartCopied = false; f.WorldFile = null; f.Role = "host";
        Check(BugReportLogic.Report(f).Contains("never been saved") || BugReportLogic.Report(f).Contains("had never been saved"), "new world text");

        f = Sample(); f.Role = "solo"; f.InSession = false; f.WorldStartCopied = false; f.WorldFile = null; f.ReproPresent = false;
        r = BugReportLogic.Report(f);
        Check(r.Contains("Not in a multiplayer session, so no starting save") && !r.Contains("never been saved"), "solo says why there is no world");
        f.LatestAutosaveCopied = true; f.AutosaveAgeSeconds = 2468 * 60;
        Check(BugReportLogic.Report(f).Contains("probably not this world"), "an autosave over an hour old is flagged");

        f = Sample(); f.ReproComplete = false; f.ReproDropped = 3; f.ReproFull = true; f.LatestAutosaveCopied = true; f.AutosaveAgeSeconds = 125;
        r = BugReportLogic.Report(f);
        Check(r.Contains("INCOMPLETE (dropped 3") && r.Contains("recorder was full") && r.Contains("latest-autosave") && r.Contains("2.1 min old"), "incomplete, full and autosave");
    }

    static void WorldFiles()
    {
        var w = BugReportLogic.WorldFiles("/saves/TLDsaveData_26_10_27_0158.tlds");
        var list = new List<string>(w);
        Check(list.Contains("/saves/TLDsaveData_26_10_27_0158.tlds") && list.Contains("/saves/TLDsaveData_26_10_27_0158.tlds.leases"), "data and leases");
        Check(list.Contains("/saves/TLDsaveProp_26_10_27_0158.tldsp"), "properties file");
        foreach (var x in w) Check(!x.Contains(".players"), "player records are not copied: " + x);
        var win = new List<string>(BugReportLogic.WorldFiles("C:\\Docs\\TLDsaveData_autosave1.tlds"));
        Check(win.Contains("C:\\Docs\\TLDsaveProp_autosave1.tldsp"), "backslash paths");
        Check(BugReportLogic.WorldFiles(null).Length == 0 && BugReportLogic.WorldFiles("").Length == 0, "no file, no list");
        Check(new List<string>(BugReportLogic.WorldFiles("/x/other.bin")).Count == 2, "an unknown name has no properties file guess");
    }

    static void Command()
    {
        Check(BugReportLogic.IsBugCommand("/bug", out var n) && n == "", "bare /bug");
        Check(BugReportLogic.IsBugCommand("/BUG  car sinks ", out n) && n == "car sinks", "note, any case, trimmed");
        Check(!BugReportLogic.IsBugCommand("/bugs", out _) && !BugReportLogic.IsBugCommand("hello /bug", out _) && !BugReportLogic.IsBugCommand(null, out _) && !BugReportLogic.IsBugCommand("/mark x", out _), "other lines are chat");
    }

    static void Chords()
    {
        const int Insert = 277, F5 = 286, A = 97;
        var held = new HashSet<int>(); var down = new HashSet<int>();
        Func<int, bool> isDown = c => down.Contains(c), isHeld = c => held.Contains(c);
        var ctrlInsert = new[] { BugReportLogic.LeftControl };
        var none = new int[0];
        var others = new List<int[]> { ctrlInsert, none };

        down.Add(Insert); held.Add(BugReportLogic.LeftControl);
        Check(BugReportLogic.ChordDown(Insert, ctrlInsert, isDown, isHeld, others), "left Ctrl+Insert");
        held.Clear(); held.Add(BugReportLogic.RightControl);
        Check(BugReportLogic.ChordDown(Insert, ctrlInsert, isDown, isHeld, others), "right Ctrl+Insert (the reported failure)");
        Check(!BugReportLogic.ChordDown(Insert, none, isDown, isHeld, others), "plain Insert does not also fire while Ctrl is held");
        held.Clear();
        Check(!BugReportLogic.ChordDown(Insert, ctrlInsert, isDown, isHeld, others), "no Ctrl, no chord");
        Check(BugReportLogic.ChordDown(Insert, none, isDown, isHeld, others), "plain Insert without Ctrl");
        held.Add(BugReportLogic.LeftShift);
        Check(BugReportLogic.ChordDown(Insert, none, isDown, isHeld, others), "an unrelated modifier does not block plain Insert");
        down.Clear();
        Check(!BugReportLogic.ChordDown(Insert, none, isDown, isHeld, others), "the key must have gone down this frame");
        down.Add(F5); held.Clear(); held.Add(BugReportLogic.LeftAlt);
        Check(BugReportLogic.ChordDown(F5, new[] { BugReportLogic.RightAlt }, isDown, isHeld, null), "Alt twins");
        Check(!BugReportLogic.ChordDown(0, none, isDown, isHeld, null), "unbound");
        Check(!BugReportLogic.ChordDown(A, none, isDown, isHeld, null), "another key");
    }

    static void ReproCopy()
    {
        var data = new ReproData(10);
        for (int i = 0; i < 4; i++) Check(data.Add(new ReproRow { Kind = ReproKind.Pose, T = i * 0.1, X = i }), "row " + i);
        data.Skip();
        var copy = data.Copy();
        Check(copy.Count == 4 && copy.Skipped == 1 && copy.Dropped == 0, "the copy has the rows and counts");
        Check(Math.Abs(copy.Seconds - 0.3) < 1e-9, "seconds is the last row's time");
        Check(data.Add(new ReproRow { Kind = ReproKind.Pose, T = 0.5 }) && data.Count == 5 && copy.Count == 4, "the recorder keeps going; the copy does not change");
        var sw = new StringWriter(); copy.Write(sw, 55);
        string json = sw.ToString();
        Check(json.StartsWith("{\"format\":1,\"protocol\":55") && json.Contains("\"complete\":true") && json.Contains("\"skipped\":1"), "the copy writes a complete format-1 file");
        Check(new ReproData(1).Copy().Count == 0, "an empty recording copies");
    }

    static void CarState()
    {
        var s = new ReproCarState.State { EngineOn = true, Driving = true, HandbrakeOn = true, GroundedDriven = 2, DrivenWheels = 4, Gear = 3, ClutchPercent = 87, HandbrakePercent = 100, MotorTorque = true, BrakeTorque = true, Rpm = 3200.5, Speed = 0.25 };
        double f = ReproCarState.Pack(s);
        Check(f == Math.Truncate(f) && f > 0 && f < 34359738368.0, "packed flags are an exact integer");
        var u = ReproCarState.Unpack(f, s.Rpm, s.Speed);
        Check(u.EngineOn && !u.Kinematic && !u.Sleeping && u.Driving && u.HandbrakeOn && u.MotorTorque && u.BrakeTorque, "flags round trip");
        Check(u.GroundedDriven == 2 && u.DrivenWheels == 4 && u.Gear == 3 && u.ClutchPercent == 87 && u.HandbrakePercent == 100, "counts, gear and percents round trip");
        foreach (int gear in new[] { -1, 0, 1, 6 }) Check(ReproCarState.Unpack(ReproCarState.Pack(new ReproCarState.State { Gear = gear }), 0, 0).Gear == gear, "gear " + gear);
        var k = ReproCarState.Unpack(ReproCarState.Pack(new ReproCarState.State { Kinematic = true, Sleeping = true }), 0, 0);
        Check(k.Kinematic && k.Sleeping && !k.EngineOn && k.ClutchPercent == 0, "a frozen asleep body");
        var lk = ReproCarState.Unpack(ReproCarState.Pack(new ReproCarState.State { WheelsLocked = true, Frozen = true, EngineOn = true }), 0, 0);
        Check(lk.WheelsLocked && lk.Frozen && lk.EngineOn && !lk.Kinematic, "wheel lock and body constraints round trip");
        Check(!ReproCarState.Unpack(ReproCarState.Pack(new ReproCarState.State { EngineOn = true }), 0, 0).WheelsLocked, "no lock by default");
        Check(ReproCarState.Percent(0.874f) == 87 && ReproCarState.Percent(-1) == 0 && ReproCarState.Percent(9) == 100, "percent clamps");
        var row = new ReproRow { Kind = ReproKind.Drive, Net = 1 }.WithCar(s);
        Check(row.F == f && row.G == 3200.5 && row.H == 0.25 && row.WellFormed, "the row carries the car in f, g, h and stays well formed");
        var sw = new StringWriter(); row.Write(sw);
        Check(sw.ToString().EndsWith("," + ((long)f).ToString() + ",3200.5,0.25]"), "written as plain numbers: " + sw);
    }

    static int Main()
    {
        CarState(); ReportText(); WorldFiles(); Command(); Chords(); ReproCopy();
        Console.WriteLine("ALL PASS (" + checks + " checks)");
        return 0;
    }
}
