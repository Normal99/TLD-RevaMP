using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace TvBrowser
{
    /// The browser itself: Chromium's headless shell (Google's "Chrome for Testing" build of the open-source Chromium,
    /// BSD licence), a separate program this mod starts and drives over the DevTools protocol (Cdp). One program for
    /// every TV (a page each), started when a TV near the player is on and closed a minute after the last one goes.
    ///
    /// It is not shipped with the mod: the first time a player takes a TV's keyboard, the TV asks to download it (one
    /// pinned version, about 100 MB, checked against the SHA-256 written below before anything is kept). Its profile
    /// (cookies, cache) lives in a temporary folder that is deleted when the game closes: nothing a page stored stays.
    ///
    /// The game is a Windows program, under Proton too, so the Windows build runs in both. Under Wine every one of its
    /// processes holds its own copy of the 221 MB program (Wine loads the file's image into private memory): four
    /// processes took 1.4 GB on the desktop (GE-Proton 11-3), one 0.7 GB. So under Wine it runs as a single process
    /// (PlatformArgs; Plugin.SingleProcess); on Windows as Chrome does, sandboxed, with one page process for every TV.
    internal static class Browser
    {
        // the pinned build (tools/tvbrowser.py --pin prints these for a newer one)
        public const string Version = "156.0.8078.47";
        public const string ZipUrl = "https://storage.googleapis.com/chrome-for-testing-public/" + Version + "/win64/chrome-headless-shell-win64.zip";
        public const string ZipSha256 = "e0af9b23f8051cca4f37ca49cd425e8055701bfc0383c198d67f8d5cc5884249";
        public const long ZipBytes = 126051683;
        public const string ExeInZip = "chrome-headless-shell-win64/chrome-headless-shell.exe";

        public enum Phase { Missing, Downloading, Off, Starting, Running, Failed }
        public static Phase Now = Phase.Missing;
        public static string Status = "";
        public static float DownloadFrac = -1f;
        /// The player agreed to the download (this game session).
        public static bool Accepted;

        public static string Dir, ProgramDir, Exe, ProfileRoot;
        public static Cdp Connection;
        private static Process _proc;
        private static string _profile;
        private static float _idleSince = -1f, _startedAt;
        private static readonly List<Tab> Tabs = new List<Tab>();
        private static readonly HashSet<string> OurTargets = new HashSet<string>();
        /// The blank page the browser opens with: the first TV takes it (one page process fewer).
        private static string _spare;
        private static readonly Dictionary<string, string> Popups = new Dictionary<string, string>();   // popup target -> its opener's target
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> Main = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        public static int Starts, TabsMade;

        public static bool Installed => Exe != null && File.Exists(Exe);
        public static int OpenTabs => Tabs.Count;
        public static long ProcessMB { get { try { return _proc != null && !_proc.HasExited ? _proc.WorkingSet64 / 1048576 : 0; } catch { return 0; } } }

        public static void Init(string configDir)
        {
            Dir = Path.Combine(configDir, "TvBrowser");
            ProgramDir = Path.Combine(Dir, "chromium-" + Version);
            Exe = Path.Combine(ProgramDir, ExeInZip.Replace('/', Path.DirectorySeparatorChar));
            ProfileRoot = Path.Combine(Path.GetTempPath(), "TvBrowser");
            try { Directory.CreateDirectory(Dir); } catch { }
            Now = Installed ? Phase.Off : Phase.Missing;
            ThreadPool.QueueUserWorkItem(_ => Leftovers());
        }

        // ------------------------------------------------------------------ the download

        public static void Download()
        {
            if (Installed || Now == Phase.Downloading) return;
            if (Version.StartsWith("PINNED")) { Fail("this build of the mod has no browser version pinned"); return; }
            Accepted = true;
            Now = Phase.Downloading; Status = "downloading the browser...";
            Plugin.Runner.StartCoroutine(DownloadRoutine());
        }

        private static IEnumerator DownloadRoutine()
        {
            string part = Path.Combine(Dir, "chrome-headless-shell-win64.zip.part");
            Plugin.Log.LogInfo("downloading the browser (Chromium headless shell " + Version + ")");
            using (var req = new UnityWebRequest(ZipUrl, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(part) { removeFileOnAbort = true }, null))
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    DownloadFrac = ZipBytes > 0 ? Mathf.Clamp01(req.downloadedBytes / (float)ZipBytes) : Mathf.Clamp01(req.downloadProgress);
                    Status = $"downloading the browser... {req.downloadedBytes / 1048576f:0} of {ZipBytes / 1048576f:0} MB";
                    yield return null;
                }
                DownloadFrac = -1f;
                if (req.result != UnityWebRequest.Result.Success) { TryDelete(part); Fail("the browser's download failed: " + req.error); yield break; }
            }
            Status = "checking the browser's download...";
            string hash = null, error = null;
            bool done = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var sha = SHA256.Create()) using (var f = File.OpenRead(part))
                        hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (hash == ZipSha256) Unpack(part);
                }
                catch (Exception e) { error = e.Message; }
                done = true;
            });
            while (!done) yield return null;
            TryDelete(part);
            if (error != null) { Fail("the browser couldn't be unpacked: " + error); yield break; }
            if (hash != ZipSha256) { Fail("the browser's download didn't match its checksum: thrown away"); yield break; }
            Plugin.Log.LogInfo($"browser downloaded and verified (sha256 {hash.Substring(0, 12)})");
            Now = Phase.Off; Status = "";
        }

        /// The zip's files into ProgramDir (only inside it: an entry that would land elsewhere stops the unpacking).
        private static void Unpack(string zipPath)
        {
            string tmp = ProgramDir + ".part";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string to = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!to.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("an entry outside the folder: " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    using (var src = e.Open()) using (var dst = File.Create(to)) src.CopyTo(dst);
                }
            if (!File.Exists(Path.Combine(tmp, ExeInZip.Replace('/', Path.DirectorySeparatorChar)))) throw new IOException("no " + ExeInZip + " in it");
            if (Directory.Exists(ProgramDir)) Directory.Delete(ProgramDir, true);
            Directory.Move(tmp, ProgramDir);
        }

        /// A player asks again after a failure (taking a TV's keyboard): the next TV that needs the browser starts it.
        public static void Retry() { if (Now == Phase.Failed) Now = Installed ? Phase.Off : Phase.Missing; }

        public static void Fail(string why)
        {
            Status = why; Now = Phase.Failed; DownloadFrac = -1f;
            Plugin.Log.LogWarning("TV browser: " + why);
            Stop();
        }

        // ------------------------------------------------------------------ the program

        /// Under Wine (Proton) Chromium's sandbox can't start; on Windows it stays on.
        public static bool UnderWine
        {
            get
            {
                if (_wine == null)
                {
                    try { var ntdll = GetModuleHandle("ntdll.dll"); _wine = ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero; }
                    catch { _wine = false; }
                }
                return _wine.Value;
            }
        }
        private static bool? _wine;
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static string PlatformArgs => UnderWine && Plugin.SingleProcess.Value
            ? " --single-process --no-zygote --disable-gpu"
            : " --disable-gpu --in-process-gpu --enable-features=NetworkServiceInProcess2 --renderer-process-limit=1";

        private static void Start()
        {
            if (Now != Phase.Off && Now != Phase.Failed || !Installed) return;
            Now = Phase.Starting; Status = "starting the browser..."; _startedAt = Time.realtimeSinceStartup; Starts++;
            _profile = Path.Combine(ProfileRoot, "profile-" + Process.GetCurrentProcess().Id + "-" + Starts);
            string args = "--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --user-data-dir=\"" + _profile + "\"" +
                          " --no-first-run --no-default-browser-check --mute-audio --hide-scrollbars --disable-extensions --disable-sync" +
                          " --disable-background-networking --disable-component-update --disable-features=Translate,MediaRouter,OptimizationHints" +
                          " --autoplay-policy=user-gesture-required --window-size=" + Tab.Width + "," + Tab.Height + PlatformArgs + " about:blank";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(_profile);
                    var psi = new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Exe),
                                                                RedirectStandardError = true, RedirectStandardOutput = true };
                    var p = new Process { StartInfo = psi };
                    p.OutputDataReceived += (o, e) => { };
                    p.ErrorDataReceived += (o, e) => { };
                    p.Start();
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    _proc = p;
                    try { File.WriteAllText(Path.Combine(ProfileRoot, "browser.pid"), p.Id.ToString()); } catch { }
                    // the program writes the port it listens on, and the browser's address, once it is up
                    string portFile = Path.Combine(_profile, "DevToolsActivePort");
                    var sw = Stopwatch.StartNew();
                    string[] lines = null;
                    while (sw.ElapsedMilliseconds < 45000)
                    {
                        if (p.HasExited) throw new IOException("the browser closed at once (exit code " + p.ExitCode + ")");
                        try { if (File.Exists(portFile)) { lines = File.ReadAllLines(portFile); if (lines.Length >= 2) break; } } catch (IOException) { }
                        Thread.Sleep(100);
                    }
                    if (lines == null || lines.Length < 2) throw new IOException("the browser didn't start in 45 s");
                    var cdp = Cdp.Connect(int.Parse(lines[0].Trim()), lines[1].Trim());
                    Main.Enqueue(() => Connected(cdp));
                }
                catch (Exception e) { Main.Enqueue(() => Fail("the browser couldn't start: " + e.Message)); }
            });
        }

        private static void Connected(Cdp cdp)
        {
            if (Now != Phase.Starting) { cdp.Dispose(); return; }
            Connection = cdp; Now = Phase.Running; Status = "";
            Plugin.Log.LogInfo($"TV browser running ({(UnderWine ? "Wine" : "Windows")}, started in {Time.realtimeSinceStartup - _startedAt:0.0} s)");
            cdp.Send("Browser.setDownloadBehavior", Json.O("behavior", "deny"));
            cdp.Send("Target.getTargets", null, null, r =>
            {
                if (r.Result != null && r.Result.TryGetValue("targetInfos", out var o) && o is List<object> list)
                    foreach (var x in list)
                        if (x is Dictionary<string, object> info && Json.Str(info, "type") == "page") { _spare = Json.Str(info, "targetId"); OurTargets.Add(_spare); break; }
                cdp.Send("Target.setDiscoverTargets", Json.O("discover", true));
                foreach (var t in Tabs) if (!t.Ready && t.TargetId == null) { t.Create(); TabsMade++; }
            });
        }

        /// Stops the program and forgets every page (the TVs make new ones when they need them).
        public static void Stop()
        {
            foreach (var t in Tabs) t.Screen.TabLost(t);
            Tabs.Clear(); OurTargets.Clear(); Popups.Clear(); _spare = null;
            var cdp = Connection; Connection = null;
            if (cdp != null) { try { cdp.Send("Browser.close"); } catch { } cdp.Dispose(); }
            var p = _proc; _proc = null;
            string profile = _profile; _profile = null;
            if (Now == Phase.Running || Now == Phase.Starting) Now = Installed ? Phase.Off : Phase.Missing;
            if (p == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!p.WaitForExit(3000)) KillTree(p); } catch { }
                Thread.Sleep(500);
                if (profile != null) TryDeleteDir(profile);
            });
        }

        /// The game is closing: the program goes with it, and its profile.
        public static void Shutdown()
        {
            var p = _proc;
            Stop();
            if (p != null) { try { if (!p.HasExited) KillTree(p); } catch { } }
        }

        private static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { UseShellExecute = false, CreateNoWindow = true }))
                    k?.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// What a crashed game left: its browser (by the pid it wrote) and old profiles.
        private static void Leftovers()
        {
            try
            {
                string pidFile = Path.Combine(ProfileRoot, "browser.pid");
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                {
                    try
                    {
                        var old = Process.GetProcessById(pid);
                        if (old.ProcessName.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) KillTree(old);
                    }
                    catch { }
                    TryDelete(pidFile);
                }
                if (Directory.Exists(ProfileRoot))
                    foreach (var d in Directory.GetDirectories(ProfileRoot, "profile-*")) TryDeleteDir(d);
            }
            catch { }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        // ------------------------------------------------------------------ pages

        /// A page for a TV: made now if the browser runs, else when it has started.
        public static Tab Open(TvScreen screen)
        {
            var t = new Tab(screen);
            Tabs.Add(t);
            _idleSince = -1f;
            if (Now == Phase.Running && Connection != null) { t.Create(); TabsMade++; }
            else if (Now == Phase.Off) Start();
            return t;
        }

        public static void Close(Tab t)
        {
            if (t == null) return;
            Tabs.Remove(t);
            if (!string.IsNullOrEmpty(t.TargetId)) OurTargets.Remove(t.TargetId);
            t.Dispose();
        }

        /// The browser's first blank page, once (null after).
        internal static string TakeSpare() { var s = _spare; _spare = null; return s; }

        internal static void CloseTarget(string targetId)
        {
            if (targetId == null) return;
            OurTargets.Remove(targetId);
            Connection?.Send("Target.closeTarget", Json.O("targetId", targetId));
        }

        public static void Tick()
        {
            while (Main.TryDequeue(out var a)) { try { a(); } catch (Exception e) { Plugin.Log.LogError("TV browser: " + e); } }
            if (Connection != null)
            {
                Connection.Pump(OnEvent);
                if (!Connection.Open) { Fail("the browser stopped: " + Connection.Problem); return; }
                foreach (var t in Tabs) { if (!string.IsNullOrEmpty(t.TargetId)) OurTargets.Add(t.TargetId); t.Tick(); }
            }
            if (_proc != null && Now == Phase.Running) { bool gone; try { gone = _proc.HasExited; } catch { gone = true; } if (gone) { Fail("the browser closed"); return; } }
            // no TV needs it for a minute: it closes (it comes back when one does)
            if ((Now == Phase.Running || Now == Phase.Starting) && Tabs.Count == 0)
            {
                if (_idleSince < 0f) _idleSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - _idleSince > IdleSeconds) { Plugin.Log.LogInfo("TV browser closed: no TV in use"); Stop(); }
            }
            else _idleSince = -1f;
        }

        public static float IdleSeconds = 60f;

        private static void OnEvent(Cdp.Message m)
        {
            if (m.Session != null)
            {
                foreach (var t in Tabs) if (t.Session == m.Session) { t.OnEvent(m); return; }
                return;
            }
            var info = Json.Obj(m.Params, "targetInfo");
            switch (m.Method)
            {
                case "Target.targetCreated":
                {
                    // a page we didn't make: a popup or a link that opens a new window. It opens in its TV instead.
                    string id = Json.Str(info, "targetId"), type = Json.Str(info, "type"), opener = Json.Str(info, "openerId");
                    if (id == null || type != "page" || OurTargets.Contains(id) || Tabs.Exists(t => t.TargetId == id)) break;
                    Popups[id] = opener;
                    Popup(id, Json.Str(info, "url"));
                    break;
                }
                case "Target.targetInfoChanged":
                {
                    string id = Json.Str(info, "targetId");
                    if (id != null && Popups.ContainsKey(id)) Popup(id, Json.Str(info, "url"));
                    break;
                }
                case "Target.targetDestroyed":
                    Popups.Remove(Json.Str(m.Params, "targetId") ?? "");
                    break;
            }
        }

        private static void Popup(string id, string url)
        {
            // one of ours after all (its creation was announced before the browser answered with its id)
            if (OurTargets.Contains(id) || Tabs.Exists(t => t.TargetId == id)) { Popups.Remove(id); return; }
            if (string.IsNullOrEmpty(url) || url == "about:blank") return;   // its address comes a moment later
            Popups.TryGetValue(id, out var opener);
            Popups.Remove(id);
            Connection?.Send("Target.closeTarget", Json.O("targetId", id));
            var tab = Tabs.Find(t => t.TargetId == opener);
            string clean = Address.Clean(url);
            if (tab != null && clean != null) tab.Screen.PopupOpened(tab, clean);
        }

        public static string Describe() =>
            $"{Now}{(Status.Length > 0 ? " (" + Status + ")" : "")}, tabs {Tabs.Count}, starts {Starts}, wine {UnderWine}, installed {Installed}, " +
            $"process {ProcessMB} MB, sent {Connection?.Sent ?? 0}, received {Connection?.Received ?? 0}, pictures {(Connection?.PictureBytes ?? 0) / 1024} KB";
    }
}
