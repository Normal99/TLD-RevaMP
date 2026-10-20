using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    [BepInPlugin(Guid, "TLD Revamp", BaseVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.core";
        public static BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> KeyMpScreen, KeyOverlay, KeyReport, KeyItemTraffic, KeyMark;
        internal static string KeyName(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> k) =>
            k == null || k.Value.MainKey == KeyCode.None ? "no key" : k.Value.ToString().Replace("PageUp", "PgUp").Replace("PageDown", "PgDn");
        internal static bool KeyDown(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> k) =>
            k != null && !Net.Chat.Typing && !MarkPanel.Opened && k.Value.MainKey != KeyCode.None && k.Value.IsDown();
        public const string BaseVersion = "0.69.1";
        public static readonly string Version = Metadata("RevampVersion", BaseVersion);
        public static readonly string BuildCommit = Metadata("SourceCommit", "unknown");
        public static readonly bool BuildDirty = Metadata("SourceDirty", "true") != "false";
        public static string CommitLabel => (BuildCommit.Length == 40 ? BuildCommit.Substring(0, 7) : "unknown") + (BuildDirty ? "+changes" : "");
        public static string BuildIdentity => BuildCommit + ":" + (BuildDirty ? "dirty" : "clean") + ":" + BuildHash;

        static string Metadata(string key, string fallback)
        {
            foreach (var a in typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (((System.Reflection.AssemblyMetadataAttribute)a).Key == key)
                    return ((System.Reflection.AssemblyMetadataAttribute)a).Value;
            return fallback;
        }

        /// Fingerprint of the DLL this game actually loaded (tools restart a game only when it runs a different build).
        public static readonly string BuildHash = ComputeBuildHash();

        private static string ComputeBuildHash()
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location);
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    return System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        /// The game's own code (Assembly-CSharp.dll, sha256, first 12 hex — what tools/game_build.json records): two players
        /// on different builds of the game can generate different worlds — a building that exists for one only (the
        /// missing diner, playtest 2026-10-09/10). Compared at join.
        public static readonly string GameBuild = ComputeGameBuild();
        public static int BridgePort = 27050;

        private static string ComputeGameBuild()
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(typeof(mainscript).Assembly.Location);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal Telemetry Telemetry;
        internal Overlay Overlay;
        internal DebugBridge Bridge;
        internal static bool BridgeEnabled;
        internal FeedbackReporter Feedback;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ScriptProfiler.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            Diagnostics.ErrorLog.Install();   // counts the game's errors for tests (bridge `errors`)
            TestSafety.Init();
            Telemetry = new Telemetry();
            SessionLog.Install();
            Overlay = new Overlay(Telemetry);
            Feedback = new FeedbackReporter(Telemetry);
            Net.ModCatalog.Url = Config.Bind("Mods", "CatalogUrl", Net.ModCatalog.DefaultUrl,
                "Catalog URL (HTTPS, or file URI for local testing), fetched only when opening Mods or pressing Refresh. Files still require local approved-mods.txt approval.");
            Net.DedicatedServer.Awake(Config);
            Net.ModObjects.Install();   // the multiplayer API other mods reference (TLDRevamp.Multiplayer.dll)
            // The bridge runs any command it's sent (reflection included): developer test tooling only, off unless this
            // machine's config turns it on (the test machines do; a player's install never does).
            BridgeEnabled = Config.Bind("Debug", "Bridge", false,
                "Developer test tooling: a command port on 127.0.0.1:27050 for automated tests. Leave it off.").Value;
            // a second copy of the game on a test machine (a third player: tools/thirdplayer.py) takes another port: -tldbridge N
            var cl = Environment.GetCommandLineArgs();
            int ci = Array.IndexOf(cl, "-tldbridge");
            if (ci >= 0 && ci + 1 < cl.Length && int.TryParse(cl[ci + 1], out int bp) && bp > 1024 && bp < 65536) BridgePort = bp;
            if (BridgeEnabled) { Bridge = new DebugBridge(BridgePort); Bridge.Start(); }

            // The mod's keys: none the game uses. The game's own F1–F10, Home and End (dev mode: F8 a test ragdoll, F9
            // kills you, F10 skips radio tracks) and its default bindings (F4–F7: drop cam, 3D/360 screenshots, 360 camera)
            // stay the game's (2026-10-08: F7–F9 were ours too). F12 is Steam's screenshot key.
            KeyMpScreen = Config.Bind("Keys", "MultiplayerScreen", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F11),
                "Opens the multiplayer screen (also the game's own Multiplayer button).");
            KeyOverlay = Config.Bind("Keys", "PerformanceOverlay", new BepInEx.Configuration.KeyboardShortcut(KeyCode.PageUp),
                "Shows or hides the performance overlay (also Settings → Revamp).");
            KeyItemTraffic = Config.Bind("Keys", "ItemTraffic", new BepInEx.Configuration.KeyboardShortcut(KeyCode.Insert),
                "Shows or hides each object's network traffic (an arrow and a rate over it; also Settings → Revamp → Debugging).");
            KeyReport = Config.Bind("Keys", "DebugReport", new BepInEx.Configuration.KeyboardShortcut(KeyCode.PageDown),
                "Saves a debug report (BepInEx/tldrevamp-feedback); with Settings → Revamp → \"Send my reports to the host\" on, the host gets a copy.");
            KeyMark = Config.Bind("Keys", "Mark", new BepInEx.Configuration.KeyboardShortcut(KeyCode.ScrollLock),
                "Opens session marks: Enter saves a note, Escape cancels. Notes can be renamed from the list.");

            // Worker fixes (terrain, roads) queue short jobs on the .NET thread pool and sometimes wait for them. Mono's pool
            // starts small and only adds threads slowly when busy, so batches could run on just a few threads.
            System.Threading.ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
            System.Threading.ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
            int want = Math.Min(Environment.ProcessorCount, 16);
            if (minWorkers < want) System.Threading.ThreadPool.SetMinThreads(want, minIo);
            System.Threading.ThreadPool.GetMinThreads(out int nowMin, out _);
            Log.LogInfo($"Thread pool: cores {Environment.ProcessorCount}, min workers {minWorkers} -> {nowMin}, max {maxWorkers}");

            // Must run before the world creates its Vector3d-keyed collections
            Fixes.Vector3dComparer.Install();
            // Patch each class on its own so one broken fix can't take the others down
            var harmony = new Harmony(Guid);
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); Log.LogInfo("Patched " + type.Name); }
                catch (Exception e) { Log.LogError("Patch failed for " + type.Name + ": " + e); }
            }

            // The BepInEx manager object can be destroyed or disabled by the game, which silently stops
            // Update/OnGUI. Run the per-frame work from our own hidden, persistent object instead.
            RevampSettings.Load(); // player's saved choices from the settings menu's Revamp tab
            var go = new GameObject("TLDRevampRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>().useGUILayout = false; // overlay uses no GUILayout: skip IMGUI's layout pass
            ModHost.Start();   // TLDLoader mods from BepInEx/TLDLoaderMods (TLDLoader itself doesn't run on the beta)
            if (Config.Bind("Mods", "ScanLoaderMods", false,
                    "For mod authors: read every TLD Loader mod in BepInEx/TLDLoaderMods and write what it would need for multiplayer (a report and a code skeleton) to BepInEx/TLDRevampModScan.").Value)
                try { LoaderScan.ScanAll(); } catch (Exception e) { Log.LogWarning("modscan: " + e.Message); }
            Net.Entities.CopyFarShadowsM = Mathf.Max(0f, Config.Bind("Multiplayer", "CopyShadowsOffBeyondM", 0f,
                    "Other players' cars farther than this (metres) cast no shadows here; 0 = always. A slower machine in a big session: try 30 (17 cars 20-45 m away, all without shadows: the slowest frames 2.6 ms quicker on the test desktop).").Value);

            Net.Entities.TelemetryAllOwnedPhysics = Config.Bind("Telemetry", "AllOwnedPhysics", false,
                "Probe up to 4096 owned bodies per physics step; off limits anomaly probes to the eight focus entities.").Value;

            // Test tooling: -tldset Path=Value[,Path=Value...] sets runtime switches before the first frame (A/B a fix in a
            // TLDBench run), like bridge `set`. Only where the bridge is on (test machines).
            int si = Array.IndexOf(cl, "-tldset");
            if (BridgeEnabled && si >= 0 && si + 1 < cl.Length)
                foreach (var pair in cl[si + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    try { Log.LogInfo("-tldset " + pair + ": " + (eq > 0 ? Reflect.Set(pair.Substring(0, eq), pair.Substring(eq + 1)) : "no '='")); }
                    catch (Exception e) { Log.LogWarning("-tldset " + pair + " failed: " + e.Message); }
                }

            if (Config.Bind("Telemetry", "CoarseTimers", true, "Bounded coarse method groups in black boxes; detailed ScriptTimers remain opt-in.").Value) ScriptProfiler.CoarseStart();
            SessionLog.TimerColumnsChanged();
            // Telemetry script timers (docs/TELEMETRY.md): off unless this machine's config turns them on; bridge `timers` too.
            var timers = Config.Bind("Telemetry", "ScriptTimers", false,
                "Developer profiling: time a list of the game's hottest scripts and RevaMP's own per-frame work (ms/frame, calls) into the play log and black box. Costs roughly 0.1 µs per timed call.").Value;
            var timerList = Config.Bind("Telemetry", "ScriptTimerMethods", "default",
                "Type.Method names separated by spaces; \"default\" = the built-in hot list, \"all\" = every Update/LateUpdate/FixedUpdate/OnGUI of the game.").Value;
            var timerAlloc = Config.Bind("Telemetry", "ScriptTimerAlloc", false,
                "Also estimate allocations per timed method from heap growth during the call (includes worker threads; slower).").Value;
            ScriptProfiler.MaxColumns = Mathf.Clamp(Config.Bind("Telemetry", "ScriptTimerColumns", 16,
                "How many of the timed methods (in list order) get their own black-box column (0-32).").Value, 0, 32);
            ScriptProfiler.TickTop = Mathf.Clamp(Config.Bind("Telemetry", "ScriptTimerTop", 10,
                "How many timed methods each 5 s play-log tick lists (slowest first).").Value, 1, 200);
            if (timers || Array.IndexOf(cl, "-tldtimers") >= 0)
                try
                {
                    Log.LogInfo("Script timers: " + ScriptProfiler.TimersStart(timerList.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries), timerAlloc));
                    SessionLog.TimerColumnsChanged();
                    Log.LogInfo("Player loop phases: " + PlayerLoopProfiler.Start()); // engine phases (physics, rendering) per tick
                }
                catch (Exception e) { Log.LogWarning("Script timers failed: " + e.Message); }

            Application.quitting += () =>
            {
                Log.LogInfo("Game quitting: stopping bridge and background work");
                Fixes.AsyncTerrain.Enabled = false; // no new worker jobs during shutdown
                Bridge?.Stop();
                SessionLog.Shutdown();
            };

            Log.LogInfo($"TLD Revamp {Version} loaded. Unity {Application.unityVersion}, game build {GameBuild}. " + (BridgeEnabled ? "Bridge on 127.0.0.1:" + BridgePort : "bridge off"));
        }

        private void OnDisable() => Log.LogWarning("BepInEx manager object disabled:\n" + Environment.StackTrace);
        private void OnDestroy() => Log.LogWarning("BepInEx manager object destroyed:\n" + Environment.StackTrace);
    }

    public class Runner : MonoBehaviour
    {
        internal static long Updates;
        private bool _loggedUpdate, _loggedGui;

        private void Update()
        {
            Updates++;
            if (!_loggedUpdate) { _loggedUpdate = true; Plugin.Log.LogInfo("Runner: first Update"); }

            var p = Plugin.Instance;
            ScriptProfiler.FrameEnd(); // closes the frame window before telemetry samples it
            p.Telemetry.Tick();
            p.Bridge?.PumpMainThread();
            Fixes.ItemSpawnSpread.Tick();
            Fixes.Sandstorms.Tick();
            Fixes.Rabbits.Tick();
            Fixes.GrassCameraThrottle.Tick(); Fixes.IdleTextureStreaming.Tick(); Fixes.PartConditionIdle.Tick(); Fixes.FarItemLookup.Tick(); Fixes.RenderOptions.Tick(); MpLab.Tick(); Net.NetLab.Tick(); Net.Mp.Tick(); Net.PlayerSave.Tick(); Net.Tpa.Tick(); Net.Voice.Tick(); Net.Chat.Tick(); Net.VehicleLab.Tick(); Net.StreamLab.Tick();
            Fixes.TinyRendererCull.Tick(); WorldGenLab.AllocTick(); RenderLab.Tick(); RebaseLab.Tick(); Fixes.RebaseParticles.Tick(); Net.DedicatedServer.Tick();
            ModHost.Tick();
            if (Plugin.KeyDown(Plugin.KeyOverlay)) { p.Overlay.Visible = !p.Overlay.Visible; RevampSettings.Save(); } // same setting as the Revamp tab
            if (Plugin.KeyDown(Plugin.KeyItemTraffic)) { Net.ItemTraffic.Show = !Net.ItemTraffic.Show; RevampSettings.Save(); p.Overlay.Toast(Net.ItemTraffic.Show ? "Item traffic on" : "Item traffic off", 2f); }
            if (Plugin.KeyDown(Plugin.KeyReport)) Net.Reports.LocalReport("hotkey");
            if (Plugin.KeyDown(Plugin.KeyMark)) MarkPanel.Open();
            MarkPanel.Tick();
            p.Feedback.Tick();
            Net.Reports.Tick();
            Net.ModCatalog.Tick();
            if (Plugin.KeyDown(Plugin.KeyMpScreen)) Net.MpScreen.Toggle();   // the multiplayer screen (also the game's own Multiplayer button)
        }

        private void OnGUI()
        {
            if (!_loggedGui) { _loggedGui = true; Plugin.Log.LogInfo("Runner: first OnGUI"); }
            Plugin.Instance.Overlay.Draw();
            MarkPanel.Draw();
            Net.Banner.Draw();
            ModHost.Gui();
        }

        private void FixedUpdate() { Net.Entities.FixedTick(); ModHost.FixedTick(); DebugBridge.FixedTick(); Fixes.DrivingAssists.FixedTick(); }
        private void LateUpdate() { Net.Entities.WornLate(); Net.Entities.LateSample(); }

        private void OnDisable() => Plugin.Log.LogWarning("Runner disabled:\n" + Environment.StackTrace);
        private void OnDestroy()
        {
            Plugin.Log.LogWarning("Runner destroyed:\n" + Environment.StackTrace);
            Plugin.Instance?.Bridge?.Stop();
        }
    }
}
