using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    [BepInPlugin(Guid, "TLD Revamp", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.core";
        public const string Version = "0.64.13";

        /// Fingerprint of the DLL this game actually loaded (tools restart a game only when it runs a different build).
        public static readonly string BuildHash = ComputeBuildHash();

        private static string ComputeBuildHash()
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location);
                using (var sha = System.Security.Cryptography.SHA1.Create())
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

            Telemetry = new Telemetry();
            Overlay = new Overlay(Telemetry);
            Feedback = new FeedbackReporter(Telemetry);
            Net.DedicatedServer.Awake();
            // The bridge runs any command it's sent (reflection included): developer test tooling only, off unless this
            // machine's config turns it on (the test machines do; a player's install never does).
            BridgeEnabled = Config.Bind("Debug", "Bridge", false,
                "Developer test tooling: a command port on 127.0.0.1:27050 for automated tests. Leave it off.").Value;
            if (BridgeEnabled) { Bridge = new DebugBridge(27050); Bridge.Start(); }

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

            Application.quitting += () =>
            {
                Log.LogInfo("Game quitting: stopping bridge and background work");
                Fixes.AsyncTerrain.Enabled = false; // no new worker jobs during shutdown
                Bridge?.Stop();
            };

            Log.LogInfo($"TLD Revamp {Version} loaded. Unity {Application.unityVersion}. " + (BridgeEnabled ? "Bridge on 127.0.0.1:27050" : "bridge off"));
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
            p.Telemetry.Tick();
            p.Bridge?.PumpMainThread();
            Fixes.ItemSpawnSpread.Tick();
            Fixes.GrassCameraThrottle.Tick(); MpLab.Tick(); Net.NetLab.Tick(); Net.Mp.Tick(); Net.VehicleLab.Tick(); Net.StreamLab.Tick();
            Fixes.TinyRendererCull.Tick(); ScriptProfiler.FrameEnd(); WorldGenLab.AllocTick(); RenderLab.Tick(); RebaseLab.Tick(); Fixes.RebaseParticles.Tick(); Net.DedicatedServer.Tick();
            ModHost.Tick();
            if (Input.GetKeyDown(KeyCode.F8)) { p.Overlay.Visible = !p.Overlay.Visible; RevampSettings.Save(); } // same setting as the Revamp tab
            if (Input.GetKeyDown(KeyCode.F9)) p.Feedback.Capture("hotkey");
            p.Feedback.Tick();
            SessionLog.Tick();
            if (Input.GetKeyDown(KeyCode.F7)) Net.MpPanel.Toggle();
            useGUILayout = Net.MpPanel.Open; // the panel needs IMGUI's layout pass; the overlay alone doesn't
        }

        private void OnGUI()
        {
            if (!_loggedGui) { _loggedGui = true; Plugin.Log.LogInfo("Runner: first OnGUI"); }
            Plugin.Instance.Overlay.Draw();
            Net.MpPanel.Draw();
            Net.Banner.Draw();
            ModHost.Gui();
        }

        private void FixedUpdate() { Net.Entities.FixedTick(); ModHost.FixedTick(); DebugBridge.FixedTick(); }

        private void OnDisable() => Plugin.Log.LogWarning("Runner disabled:\n" + Environment.StackTrace);
        private void OnDestroy()
        {
            Plugin.Log.LogWarning("Runner destroyed:\n" + Environment.StackTrace);
            Plugin.Instance?.Bridge?.Stop();
        }
    }
}
