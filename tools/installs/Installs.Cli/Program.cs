using System.Text.Json;
using TLDInstalls;

// tld-installs: copies of The Long Drive side by side, from the command line (the TLD Copies window does the same).
const string Usage = """
tld-installs: copies of The Long Drive next to Steam's, each with its own saves (Steam's own install is never changed)

  tld-installs list [--json]                  the copies and what is in each
  tld-installs make NAME --clean              the game exactly as Steam has it: no BepInEx, no mods; its own save folder
  tld-installs make NAME --vanilla            no gameplay mods (Windows: BepInEx + save isolation only)
  tld-installs make NAME --release [VERSION]  BepInEx + a TLD RevaMP release (default: the one the installer is pinned to)
  tld-installs make NAME --dev                BepInEx + a dev build of this checkout (tools/devrelease.py --dry-run)
  tld-installs make NAME --source [REF]       newest dev, built here (default origin/dev; Git + .NET SDK required)
  tld-installs build NAME                    fetch and rebuild a source copy now
  tld-installs update NAME --ref REF         switch a source copy to another branch or commit
  tld-installs make NAME --test               BepInEx + this checkout's test build, debug bridge on (what the test tools run)
  tld-installs update NAME [--release VERSION]   game files up to Steam's, then the mod again (a release copy keeps its
                                              version unless one is given; dev/test copies are rebuilt)
  tld-installs sync NAME                      only the game files, up to Steam's (after a game update)
  tld-installs run NAME [--force] [--options LINE] [-- ARGS]
                                              start it (Steam must be running; --force allows a duplicate or a test run);
                                              --options replaces the copy's launch options for this launch only
  tld-installs options NAME [LINE | -- WORDS… | --clear]
                                              show or set the copy's launch options, written like Steam's:
                                              NAME=VALUE variables, a wrapper around %command% (Linux), game arguments,
                                              e.g. 'PROTON_LOG=1 gamemoderun %command% -screen-fullscreen 0'
  tld-installs stop NAME                      close its game at once
  tld-installs run2 NAME [--monitor N]        Windows: start it a second time on another Steam account, in a Sandboxie-Plus
                                              box (TLD_SANDBOX_BOX, default TLD2), mirrored into the copy's account2
                                              folder first; --monitor (0 = the game's own) is remembered
  tld-installs stop2 NAME                     close the second account's game at once
  tld-installs check NAME [--timeout S]       start it, wait for the main menu, print the build it reports and whether the
                                              debug bridge answers, then close it
  tld-installs remove NAME --yes              delete the copy, its saves included
  tld-installs report NAME TEXT [--files LIST] choose local telemetry files, then open a public issue with build info
                                              and a private zip reference only; LIST: numbers, default, or none
                                              (bug is an alias; TLD_BUG_PROJECT defaults to Normal99/1)
  tld-installs releases                       the TLD RevaMP releases a copy can be made from
  tld-installs where                          the copies folder, Steam's game, Proton and the checkout found

Options: --root DIR (the copies folder; default TLD_INSTALLS, else the window's choice, else ~/tld-installs),
         --repo DIR (the checkout dev/test copies are built from; default: the one this program is in).
""";

var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0) return null;
    string? v = i + 1 < argv.Count && !argv[i + 1].StartsWith("--") ? argv[i + 1] : "";
    argv.RemoveRange(i, v == "" ? 1 : 2);
    return v;
}
bool Flag(string name) => argv.Remove(name);

List<string> extra = new();
var dd = argv.IndexOf("--");
if (dd >= 0) { extra = argv.Skip(dd + 1).ToList(); argv = argv.Take(dd).ToList(); }
if (Opt("--repo") is { Length: > 0 } repo) Env.RepoDir = Path.GetFullPath(repo);
var store = new Store(Opt("--root") is { Length: > 0 } root ? root : null);
void Log(string s) => Console.WriteLine(s);

try
{
    if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") { Console.Write(Usage); return 0; }
    if (Env.Windows && !Launcher.AnyGame()) Saves.ForThisMachine(store).Recover(Log);
    var cmd = argv[0];
    string Name() => argv.Count > 1 ? argv[1] : throw new UserError("which copy? (tld-installs list)");
    var b = new Builder(store, Log);
    switch (cmd)
    {
        case "list":
        {
            var copies = store.List();
            if (Flag("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(copies.Select(c =>
                {
                    var identity = c.InstalledIdentity();
                    return new { c.Name, Kind = c.KindLabel, c.LaunchOptions, c.RevampVersion, c.RevampSource, c.Commit, InstalledCommit = identity.Commit, InstalledDirty = identity.Dirty, identity.LabelDiffers, c.SourceRef, c.UpdateBeforeLaunch, c.NewerCommits, c.GameBuildId, c.GameBranch, c.Bridge, Dir = c.Dir, c.Compat, Running = Launcher.IsRunning(c), c.SizeBytes, c.OwnBytes, c.LastLaunched };
                }), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (copies.Count == 0) { Console.WriteLine($"no copies in {store.Root} yet (tld-installs make NAME --clean|--vanilla|--release|--dev|--test)"); return 0; }
            var steamBuild = Steam.FindGame() is { } g ? Steam.BuildInfo(g) : default;
            foreach (var c in copies)
            {
                var behind = steamBuild.BuildId != null && (c.GameBuildId != steamBuild.BuildId || c.GameBranch != steamBuild.Branch)
                    ? $"  (Steam has the {steamBuild.Branch ?? "unknown"} branch, build {steamBuild.BuildId}: tld-installs update {c.Name})" : "";
                Console.WriteLine($"{c.Name,-12} {c.KindLabel,-9} {(Launcher.IsRunning(c) ? "RUNNING " : "")}{Builder.Describe(c)}{behind}");
                Console.WriteLine($"{"",-12} {c.Dir}" + (c.LastLaunched is { } l ? $", last launched {l.ToLocalTime():yyyy-MM-dd HH:mm}" : ""));
                if (!string.IsNullOrWhiteSpace(c.LaunchOptions)) Console.WriteLine($"{"",-12} launch options: {c.LaunchOptions}");
            }
            return 0;
        }
        case "make":
        {
            string? version = Opt("--release");
            string? reference = Opt("--source");
            Kind kind = reference != null ? Kind.Source : version != null ? Kind.Release : Flag("--clean") ? Kind.Clean : Flag("--vanilla") ? Kind.Vanilla : Flag("--dev") ? Kind.Dev : Flag("--test") ? Kind.Test
                      : throw new UserError("make NAME needs --clean, --vanilla, --release [VERSION], --dev or --test");
            var name = Name();
            await b.Make(name, kind, version is { Length: > 0 } ? version : null, reference is { Length: > 0 } ? reference : null);
            return 0;
        }
        case "update":
        {
            var v = Opt("--release");
            var reference = Opt("--ref");
            if (reference == "") throw new UserError("--ref needs a branch or commit SHA");
            var name = Name();
            await b.Update(name, v is { Length: > 0 } ? v : null, reference);
            return 0;
        }
        case "build":
            await b.Build(Name());
            return 0;
        case "sync":
            b.Sync(Name());
            return 0;
        case "run":
        {
            var force = Flag("--force");
            var options = Opt("--options");
            var c = store.Need(Name());
            var p = Launcher.Launch(store, c, Log, force, extra, options: options);
            if (p != null)
            {
                Console.WriteLine($"Waiting for the game to close (data stays in {c.UserData})…");
                p.WaitForExit();
            }
            return 0;
        }
        case "options":
        {
            var clear = Flag("--clear");
            var c = store.Need(Name());
            string? line = clear ? "" : argv.Count == 3 ? argv[2] : argv.Count > 3 ? LaunchOptions.Join(argv.Skip(2)) : dd >= 0 ? LaunchOptions.Join(extra) : null;
            if (line != null)
            {
                LaunchOptions.Parse(line);   // a mistake is refused here, not at the next launch
                c.LaunchOptions = string.IsNullOrWhiteSpace(line) ? null : line.Trim();
                c.Save();
            }
            Console.WriteLine(c.LaunchOptions is { } now ? $"{c.Name}: {now}" : $"{c.Name}: no launch options");
            if (c.LaunchOptions != null) Console.WriteLine("  " + LaunchOptions.Parse(c.LaunchOptions).Describe());
            return 0;
        }
        case "stop":
            Launcher.Stop(store, store.Need(Name()), Log);
            return 0;
        case "run2":
        {
            var c = store.Need(Name());
            if (Opt("--monitor") is { Length: > 0 } m) { c.SecondMonitor = int.Parse(m); c.Save(); }
            SecondAccount.Launch(store, c, Log, c.SecondMonitor);
            return 0;
        }
        case "stop2":
            SecondAccount.Stop(store.Need(Name()), Log);
            return 0;
        case "check":
        {
            var t = Opt("--timeout") is { Length: > 0 } ts ? int.Parse(ts) : 300;
            var force = Flag("--force");
            var c = store.Need(Name());
            var r = Launcher.Check(store, c, Log, t, force);
            bool wantMod = c.HasRevamp;
            bool ok = r.Started && (!wantMod || r.Loaded == c.RevampVersion) && r.BridgeAnswers == c.Bridge && (r.LogBridge ?? false) == c.Bridge;
            Console.WriteLine(JsonSerializer.Serialize(new { copy = c.Name, kind = c.KindLabel, r.Started, expected = c.RevampVersion, r.Loaded, r.GameBuild, bridgeExpected = c.Bridge, r.LogBridge, r.BridgeAnswers, r.Note, ok }));
            Console.WriteLine(ok ? "PASS" : "FAIL");
            return ok ? 0 : 1;
        }
        case "remove":
        {
            var yes = Flag("--yes");
            var name = Name();
            if (!yes) throw new UserError($"this deletes '{name}' with its saves: add --yes");
            b.Remove(name);
            return 0;
        }
        case "report":
        case "bug":
        {
            var choice = Opt("--files");
            var c = store.Need(Name());
            var text = string.Join(" ", argv.Skip(2));
            if (!c.HasRevamp) throw new UserError("bug reports are for RevaMP copies");
            if (string.IsNullOrWhiteSpace(text)) throw new UserError("report NAME TEXT: what's broken?");
            Log(BugReportFiles.DescriptionNotice);
            Log(BugReportFiles.Notice);
            Log(BugReportFiles.Contents);
            var files = BugReportFiles.Discover(c, Log);
            for (int i = 0; i < files.Count; i++) Log($"{i + 1}. [{(files[i].Selected ? "x" : " ")}] {files[i].Label}");
            if (choice == null && files.Count > 0)
            {
                Console.Write("File numbers to keep (comma-separated), 'none', or Enter for the ticks shown: ");
                choice = Console.ReadLine() ?? throw new UserError("no selection read: use --files default, --files none, or --files 1,2");
            }
            var selected = files.Where(f => f.Selected).ToList();
            if (choice == "none") selected.Clear();
            else if (!string.IsNullOrEmpty(choice) && choice != "default")
            {
                selected.Clear();
                foreach (var part in choice.Split(','))
                {
                    if (!int.TryParse(part.Trim(), out var n) || n < 1 || n > files.Count) throw new UserError("--files needs file numbers from the list, default, or none");
                    selected.Add(files[n - 1]);
                }
            }
            var telemetry = BugReportFiles.Pack(c, text, selected, Log);
            if (telemetry != null)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(telemetry.Path)!) { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log("  couldn't open the private zip folder; its path is above"); }
            }
            Console.WriteLine(await BugReports.Send(c, text, Log, telemetry));
            return 0;
        }
        case "releases":
        {
            var list = await Releases.List(log: Log);
            var cur = Releases.Current(list);
            foreach (var r in list) Console.WriteLine($"{r.Label,-40} {r.Published:yyyy-MM-dd} {(r == cur && !r.Draft ? "(the installer's)" : "")}");
            return 0;
        }
        case "where":
        {
            var g = Steam.FindGame();
            Console.WriteLine($"copies:   {store.Root}");
            Console.WriteLine($"steam:    {(g == null ? "game not found" : $"{g.GameDir} (build {g.BuildId})")}");
            if (g != null && !Env.Windows) Console.WriteLine($"proton:   {Steam.Proton(g)}\nruntime:  {Steam.Runtime(g)}");
            Console.WriteLine($"checkout: {Env.RepoDir ?? "none (dev and test copies need one)"}");
            Console.WriteLine($"cache:    {Env.CacheDir}");
            return 0;
        }
        default:
            Console.Error.Write(Usage);
            return 2;
    }
}
catch (UserError e)
{
    Console.Error.WriteLine("tld-installs: " + e.Message);
    return 1;
}
