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
        public static BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> KeyMpScreen, KeyOverlay, KeyReport, KeyItemTraffic, KeyMark, KeyOwnership, KeyOwnerGhost, KeyLinks, KeyCompare, KeyEvents, KeyRepro, KeyDebugView, KeyCinematic, KeyReplayRecord, KeyReplayPlay;
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

        /// The Harmony patches that did not apply. A patch that fails is dead code, and the game's log is the
        /// only place that says so: the count is here so the mod's own status can report it.
        public static int PatchFailures;
        public static readonly System.Collections.Generic.List<string> FailedPatches = new System.Collections.Generic.List<string>();
        public static BepInEx.Configuration.ConfigEntry<bool> ShowWipMark;

        /// The WIP mark's state. One source of truth: the overlay's runtime flag. The config entry is only the
        /// persistent backing (it is written when the value changes and read back at startup), so the settings
        /// screen and the overlay can never disagree about whether the mark is on.
        public static bool WipMarkEnabled
        {
            get
            {
                var overlay = Instance?.Overlay;
                if (overlay != null) return overlay.ShowWip;
                return ShowWipMark == null || ShowWipMark.Value;
            }
        }

        public static void SetWipMark(bool value)
        {
            var overlay = Instance?.Overlay;
            if (overlay != null) overlay.ShowWip = value;   // the runtime truth, first
            if (ShowWipMark != null && ShowWipMark.Value != value) ShowWipMark.Value = value;   // then the persistent copy
        }

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
            try { Initialize(); }
            catch { StartupSplash.Report(StartupSplash.Failed); throw; }
        }

        private void Initialize()
        {
            Instance = this;
            Log = Logger;
            ScriptProfiler.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (!Config.Bind("General", "StartupSplash", true,
                    "Show the native RevaMP startup splash until the first game frame. Takes effect at next start.").Value)
                StartupSplash.Report(StartupSplash.Failed);

            Diagnostics.ErrorLog.Install();   // counts the game's errors for tests (bridge `errors`)
            TestSafety.Init();
            Telemetry = new Telemetry();
            SessionLog.Install();
            Overlay = new Overlay(Telemetry);
            // The cinematic camera (issue #36): a separate camera for filming. Unbound by default, so it never
            // steals a key the game or the player uses; set it in BepInEx/config/tldrevamp.core.cfg.
            KeyCinematic = Config.Bind("Keys", "CinematicCamera", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Opens or closes the cinematic camera (a separate camera for filming; the gameplay camera stays as it is). Unbound by default.");
            Cinematic.CinematicCamera.ExitKeyHint = KeyName(KeyCinematic) + " (Settings → Revamp → Filmmaking, or BepInEx/config/tldrevamp.core.cfg)";
            // The replay (issue #36): record gameplay, then play it back with the cinematic camera.
            KeyReplayRecord = Config.Bind("Keys", "ReplayRecord", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Starts or stops recording a replay (everything near you, every player; saved to BepInEx/tldrevamp-replays).");
            KeyReplayPlay = Config.Bind("Keys", "ReplayPlay", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Plays the latest replay, or closes the one playing.");
            ShowWipMark = Config.Bind("Branding", "ShowWipMark", true,
                "Draw a subtle TLD Revamp WIP mark in the bottom-right corner. It is also configurable under Settings → Revamp → Branding.");
            Overlay.ShowWip = ShowWipMark.Value;
            ShowWipMark.SettingChanged += (_, __) => { if (Overlay != null) Overlay.ShowWip = ShowWipMark.Value; };
            Feedback = new FeedbackReporter(Telemetry);
            Net.ModCatalog.Url = Config.Bind("Mods", "CatalogUrl", Net.ModCatalog.DefaultUrl,
                "Catalog URL (HTTPS, or file URI for local testing), fetched only when opening Mods or pressing Refresh. Files still require local approved-mods.txt approval.");
            Net.DedicatedServer.Awake(Config);
            Net.ModObjects.Install();   // the multiplayer API other mods reference (TLDRevamp.Multiplayer.dll)
            // The bridge runs any command it's sent (reflection included): developer test tooling only, off unless this
            // machine's config turns it on (the test machines do; a player's install never does).
            BridgeEnabled = Config.Bind("Debug", "Bridge", false,
                "Developer test tooling: a command port on 127.0.0.1:27050 for automated tests. Leave it off.").Value;
            // The XR session (issue #41). OpenXR is what the game ships and what it uses; the bridge only owns the
            // loader's life when the session is asked for. The no-pre-init loader is a deliberate fallback and is off
            // by default: the plain loader is the one the game expects.
            Fixes.OpenXrBridge.Enabled = Config.Bind("Multiplayer", "OpenXrEnabled", true,
                "Use the OpenXR loader for the VR session. Off: the game keeps its own XR setup.").Value;
            Fixes.OpenXrBridge.AllowNoPreInit = Config.Bind("Multiplayer", "OpenXrAllowNoPreInit", false,
                "Allow the OpenXR no-pre-init loader when it is the only one the XR manager holds. Off by default: the plain OpenXR loader is preferred.").Value;
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
            KeyDebugView = Config.Bind("Keys", "DebugView", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Cycles Off / Focus / Problems / Everything. Unbound by default; choose a free key, leaving F11, Page Up/Down, Scroll Lock, Insert and Ctrl+Insert available.");
            KeyOwnership = Config.Bind("Keys", "OwnershipOverlay", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Shows or hides ownership and sync error labels (also Settings > Revamp > Debugging). Unbound by default.");
            KeyOwnerGhost = Config.Bind("Keys", "OwnerGhost", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Shows or hides the crosshair object's last received owner pose. Unbound by default.");
            KeyLinks = Config.Bind("Keys", "LinkView", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Shows or hides attachment, lock and rider links. Unbound by default.");
            KeyReport = Config.Bind("Keys", "DebugReport", new BepInEx.Configuration.KeyboardShortcut(KeyCode.PageDown),
                "Saves a debug report (BepInEx/tldrevamp-feedback); with Settings → Revamp → \"Send my reports to the host\" on, the host gets a copy.");
            KeyMark = Config.Bind("Keys", "Mark", new BepInEx.Configuration.KeyboardShortcut(KeyCode.ScrollLock),
                "Opens session marks: Enter saves a note, Escape cancels. Notes can be renamed from the list.");
            KeyCompare = Config.Bind("Keys", "ObjectComparison", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Shows or hides live partner views of the focused shared object. Unbound by default; also Settings > Revamp > Debugging.");
            KeyEvents = Config.Bind("Keys", "ObjectEvents", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Shows or hides recent telemetry events for the focused object. Unbound by default; also Settings > Revamp > Debugging.");
            KeyRepro = Config.Bind("Keys", "ReproRecorder", new BepInEx.Configuration.KeyboardShortcut(KeyCode.Insert, KeyCode.LeftControl),
                "Starts or stops local repro recording (also Settings → Revamp → Debugging). Always off at startup.");

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
            var patchTypes = Array.FindAll(typeof(Plugin).Assembly.GetTypes(), type => type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0);
            int patched = 0;
            StartupSplash.Report(StartupSplash.Patching, 0, patchTypes.Length);
            foreach (var type in patchTypes)
            {
                try { harmony.CreateClassProcessor(type).Patch(); Log.LogInfo("Patched " + type.Name); }
                catch (Exception e)
                {
                    // A patch that fails is dead code: the feature it carries simply does not happen, and nothing
                    // else says so. The count is reported in the mod's own status, so a test can see it.
                    StartupSplash.Report(StartupSplash.Failed);
                    PatchFailures++;
                    FailedPatches.Add(type.Name);
                    Log.LogError("Patch failed for " + type.Name + ": " + e);
                }
                StartupSplash.Report(StartupSplash.Patching, ++patched, patchTypes.Length);
            }
            if (PatchFailures > 0) Log.LogError(PatchFailures + " patch(es) failed to apply: " + string.Join(", ", FailedPatches));
            StartupSplash.Report(StartupSplash.Starting);

            // The BepInEx manager object can be destroyed or disabled by the game, which silently stops
            // Update/OnGUI. Run the per-frame work from our own hidden, persistent object instead.
            RevampSettings.Load(); // player's saved choices from the settings menu's Revamp tab
            // The old settings file could hold the host's password in plaintext. It is taken into memory for this
            // session and removed from the file (backward compatible: the file keeps its other lines).
            try { if (Net.SessionSettingsDefaults.Migrate()) Log.LogInfo("session settings: the old plaintext host password was removed from the settings file (it is kept in memory for this session only)"); }
            catch (Exception e) { Log.LogWarning("session settings: " + e.Message); }
            var go = new GameObject("TLDRevampRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>().useGUILayout = false; // overlay uses no GUILayout: skip IMGUI's layout pass
            go.AddComponent<Net.HandItemRender>();
            ModHost.Start();   // TLDLoader mods from BepInEx/TLDLoaderMods (TLDLoader itself doesn't run on the beta)
            if (Config.Bind("Mods", "ScanLoaderMods", false,
                    "For mod authors: read every TLD Loader mod in BepInEx/TLDLoaderMods and write what it would need for multiplayer (a report and a code skeleton) to BepInEx/TLDRevampModScan.").Value)
                try { LoaderScan.ScanAll(); } catch (Exception e) { StartupSplash.Report(StartupSplash.Failed); Log.LogWarning("modscan: " + e.Message); }
            Net.Entities.CopyFarShadowsM = Mathf.Max(0f, Config.Bind("Multiplayer", "CopyShadowsOffBeyondM", 0f,
                    "Other players' cars farther than this (metres) cast no shadows here; 0 = always. A slower machine in a big session: try 30 (17 cars 20-45 m away, all without shadows: the slowest frames 2.6 ms quicker on the test desktop).").Value);

            Net.PlayerLook.LocalDriverHands = Net.RemotePlayers.LocalDriverHands;   // the copies place a driver's hands (RemotePlayers.DriverHands)
            Net.Entities.TelemetryAllOwnedPhysics = Config.Bind("Telemetry", "AllOwnedPhysics", false,
                "Probe up to 4096 owned bodies per physics step; off limits anomaly probes to trace focus entities.").Value;

            // Test tooling: -tldset Path=Value[,Path=Value...] sets runtime switches before the first frame (A/B a fix in a
            // TLDBench run), like bridge `set`. Only where the bridge is on (test machines).
            int si = Array.IndexOf(cl, "-tldset");
            if (BridgeEnabled && si >= 0 && si + 1 < cl.Length)
                foreach (var pair in cl[si + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    try { Log.LogInfo("-tldset " + pair + ": " + (eq > 0 ? Reflect.Set(pair.Substring(0, eq), pair.Substring(eq + 1)) : "no '='")); }
                    catch (Exception e) { StartupSplash.Report(StartupSplash.Failed); Log.LogWarning("-tldset " + pair + " failed: " + e.Message); }
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
                catch (Exception e) { StartupSplash.Report(StartupSplash.Failed); Log.LogWarning("Script timers failed: " + e.Message); }

            Application.quitting += () =>
            {
                StartupSplash.Report(StartupSplash.Failed);
                Log.LogInfo("Game quitting: stopping bridge and background work");
                Fixes.AsyncTerrain.Enabled = false; // no new worker jobs during shutdown
                Bridge?.Stop();
                SessionLog.Shutdown();
            };

            Log.LogInfo($"TLD Revamp {Version} loaded. Unity {Application.unityVersion}, game build {GameBuild}. " + (BridgeEnabled ? "Bridge on 127.0.0.1:" + BridgePort : "bridge off"));
            StartupSplash.Report(StartupSplash.Waiting);
        }

        private void OnDisable() => Log.LogWarning("BepInEx manager object disabled:\n" + Environment.StackTrace);
        private void OnDestroy() => Log.LogWarning("BepInEx manager object destroyed:\n" + Environment.StackTrace);
    }

    public class Runner : MonoBehaviour
    {
        internal static long Updates;
        private bool _loggedUpdate, _loggedGui;
        private int _debugViewRevision = -1;

        private void Update()
        {
            Updates++;
            if (!_loggedUpdate) { _loggedUpdate = true; StartupSplash.Report(StartupSplash.Finished); Plugin.Log.LogInfo("Runner: first Update"); }

            var p = Plugin.Instance;
            ScriptProfiler.FrameEnd(); // closes the frame window before telemetry samples it
            p.Telemetry.Tick();
            p.Bridge?.PumpMainThread();
            Fixes.ItemSpawnSpread.Tick();
            Fixes.Sandstorms.Tick();
            Fixes.Rabbits.Tick();
            Fixes.GrassCameraThrottle.Tick(); Fixes.IdleTextureStreaming.Tick(); Fixes.PartConditionIdle.Tick(); Fixes.FarItemLookup.Tick(); Fixes.RenderOptions.Tick();
            if (Plugin.KeyDown(Plugin.KeyReplayRecord)) Replay.ReplayRecorder.Toggle();
            if (Plugin.KeyDown(Plugin.KeyReplayPlay)) { if (Replay.ReplayViewer.Active) Replay.ReplayViewer.End("closed"); else Replay.ReplayViewer.PlayLatest(); }
            Replay.ReplayRecorder.Tick();
            Replay.ReplayViewer.Tick();   // before the camera, so the camera shows this frame's pose
            Cinematic.CinematicCamera.Tick();
            if (Plugin.KeyDown(Plugin.KeyCinematic) && Cinematic.CinematicCamera.KeyAllowed()) Cinematic.CinematicCamera.Toggle();
            Fixes.TerrainHorizon.Tick(); Fixes.OpenXrBridge.Tick(); Fixes.VrContractBridge.Tick(); MpLab.Tick(); Net.NetLab.Tick(); Net.Mp.Tick(); Net.PlayerSave.Tick(); Net.Tpa.Tick(); Net.Voice.Tick(); Net.Chat.Tick(); Net.VehicleLab.Tick(); Net.StreamLab.Tick();
            Fixes.TinyRendererCull.Tick(); WorldGenLab.AllocTick(); RenderLab.Tick(); RebaseLab.Tick(); Fixes.RebaseParticles.Tick(); Net.DedicatedServer.Tick();
            ModHost.Tick();
            if (Plugin.KeyDown(Plugin.KeyOverlay)) { p.Overlay.Visible = !p.Overlay.Visible; RevampSettings.Save(); } // same setting as the Revamp tab
            if (Plugin.KeyDown(Plugin.KeyItemTraffic)) { Net.ItemTraffic.SetVisible(!Net.ItemTraffic.Show); RevampSettings.Save(); p.Overlay.Toast(Net.ItemTraffic.Show ? "Item traffic on" : "Item traffic off", 2f); }
            if (Plugin.KeyDown(Plugin.KeyOwnership)) { WorldOverlays.Ownership = !WorldOverlays.Ownership; RevampSettings.Save(); }
            if (Plugin.KeyDown(Plugin.KeyOwnerGhost)) { WorldOverlays.Ghost = !WorldOverlays.Ghost; RevampSettings.Save(); }
            if (Plugin.KeyDown(Plugin.KeyLinks)) { WorldOverlays.Links = !WorldOverlays.Links; RevampSettings.Save(); }
            if (Plugin.KeyDown(Plugin.KeyReport)) Net.Reports.LocalReport("hotkey");
            if (Plugin.KeyDown(Plugin.KeyMark)) MarkPanel.Open();
            if (Plugin.KeyDown(Plugin.KeyCompare)) { ObjectPanels.Compare = !ObjectPanels.Compare; RevampSettings.Save(); }
            if (Plugin.KeyDown(Plugin.KeyEvents)) { ObjectPanels.Events = !ObjectPanels.Events; RevampSettings.Save(); }
            if (Plugin.KeyDown(Plugin.KeyDebugView)) { DebugView.Mode = DebugView.Next(DebugView.Mode); RevampSettings.Save(); }
            if (_debugViewRevision != DebugView.Revision)
            {
                _debugViewRevision = DebugView.Revision; p.Overlay.Toast("Debug view: " + DebugView.Mode, 2f);
            }
            ObjectPanels.Tick();
            if (Plugin.KeyDown(Plugin.KeyRepro)) ReproRecorder.Toggle();
            ReproRecorder.Tick();
            MarkPanel.Tick();
            p.Feedback.Tick();
            Net.Reports.Tick();
            Net.ModCatalog.Tick();
            Net.JoinLoading.Tick();
            if (Plugin.KeyDown(Plugin.KeyMpScreen)) Net.MpScreen.Toggle();   // the multiplayer screen (also the game's own Multiplayer button)
        }

        private void OnGUI()
        {
            if (!_loggedGui) { _loggedGui = true; Plugin.Log.LogInfo("Runner: first OnGUI"); }
            if (Net.JoinLoading.Showing) { Net.JoinLoading.Draw(); return; }
            Plugin.Instance.Overlay.Draw();
            // The cinematic camera's status and help: its own pass, so they stay visible when the performance
            // overlay is hidden (a filmmaker turns the overlay off to get a clean frame). GUI calls belong here, in
            // OnGUI: from Update they threw every frame the camera was on and cut short the rest of Update (the
            // network tick among it).
            Cinematic.CinematicCamera.Draw();
            Replay.ReplayViewer.Draw(); Replay.ReplayRecorder.Draw();
            NetworkConditions.Draw(); ReproRecorder.Draw();
            MarkPanel.Draw();
            ObjectPanels.Draw();
            Net.Banner.Draw();
            ModHost.Gui();
            ModHost.RefreshConfigScreen();   // is a settings screen open right now?
            ModHost.DrawConfigs();   // the Loader mods' own settings windows (IMGUI)
        }

        private void FixedUpdate() { Net.Entities.FixedTick(); ModHost.FixedTick(); DebugBridge.FixedTick(); Fixes.DrivingAssists.FixedTick(); Net.JoinLoading.FixedTick(); }
        private void LateUpdate() { Net.Entities.WornLate(); Net.Entities.LateSample(); }

        private void OnApplicationQuit() => Net.JoinLoading.End("quit");
        private void OnDisable() { Net.JoinLoading.End("disabled"); Plugin.Log.LogWarning("Runner disabled:\n" + Environment.StackTrace); }
        private void OnDestroy()
        {
            Net.JoinLoading.End("destroyed");
            Plugin.Log.LogWarning("Runner destroyed:\n" + Environment.StackTrace);
            Plugin.Instance?.Bridge?.Stop();
            ModHost.Shutdown();   // the world ends here: the Loader mods get their OnUnload
        }
    }
}
