using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Dedicated server mode: the game runs headless (`-batchmode -nographics`), starts a world by itself —
    /// continuing the last save (the server owns the save) or a configured seed — and hosts automatically. Its
    /// phantom player is suppressed: no pose, outfit or player-state for id 0 (clients must not see a ghost at
    /// the spawn), and god stays on (a phantom starves to death otherwise). The phantom never travels, so the
    /// server never streams new pois and never asks for leases — it stays lease-neutral by design and clients
    /// hold all leases; distant entities on the server live as records that follow owner states.
    ///
    /// Launch: `steam -applaunch 1017180 -batchmode -nographics [-tldport 27070] [-tldseed 20260924]`
    public static class DedicatedServer
    {
        public static bool Enabled;
        public static int Port = 27070;
        public static int Seed;                       // 0: continue the last save, or a random new world
        // the server's settings (BepInEx/config/tldrevamp.core.cfg, [Server]; read only in server mode). A dedicated
        // server has no multiplayer screen to set them on.
        public static string Password = "";
        public static int MaxPlayers = 16;
        public static bool TpaOn = true; public static int TpaCooldownS = 300;
        public static float AutoSaveMinutes = 5f;
        public static long AutoSaves;
        /// The admin console's stop: save, then quit.
        public static bool StopRequested;
        /// What a server with no viewer doesn't need, switched off as it appears (every StripEveryS): sounds and
        /// particle effects (batchmode skips drawing, not their simulation). Game logic never reads them.
        public static bool StripVisuals = true;
        public static float StripEveryS = 10f;
        public static long StrippedAudio, StrippedParticles, StripSweeps;
        public static float StripLastMs;
        private static float _stripAt;
        private static float _savedAt;
        private static bool _worldStarted, _hosted, _audioStripped;
        private static float _worldAt = -1f;

        /// From Plugin.Awake, before anything hosts.
        public static void Awake(BepInEx.Configuration.ConfigFile cfg)
        {
            var args = Environment.GetCommandLineArgs();
            Enabled = Array.IndexOf(args, "-batchmode") >= 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-tldport") int.TryParse(args[i + 1], out Port);
                if (args[i] == "-tldseed") int.TryParse(args[i + 1], out Seed);
            }
            if (cfg != null)
                MpServer.SetAdmins(cfg.Bind("Server", "Admins", "", "Steam IDs (comma-separated) who may use the admin commands in chat (/kick, /ban, /save, /status...). The host always may.").Value);
            if (Enabled && cfg != null)
            {
                Port = cfg.Bind("Server", "Port", Port, "Dedicated server: the port it hosts on (-tldport overrides it).").Value;
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-tldport") int.TryParse(args[i + 1], out Port);
                Password = cfg.Bind("Server", "Password", "", "Dedicated server: players must type this to join (empty: no password).").Value ?? "";
                MaxPlayers = Mathf.Clamp(cfg.Bind("Server", "MaxPlayers", 16, "Dedicated server: players at most (the server itself counts as one).").Value, 2, 64);
                TpaOn = cfg.Bind("Server", "TeleportRequests", true, "Dedicated server: players may ask to teleport to each other (/tpa).").Value;
                TpaCooldownS = cfg.Bind("Server", "TeleportCooldownSeconds", 300, "Dedicated server: seconds between one player's teleport requests (0: none).").Value;
                StripVisuals = cfg.Bind("Server", "StripVisuals", true, "Dedicated server: switch off sounds and particle effects as they appear (nobody sees or hears them).").Value;
                AutoSaveMinutes = cfg.Bind("Server", "AutoSaveMinutes", 5f, "Dedicated server: saves the world (and every player's record) this often (0: only when it stops).").Value;
            }
            if (Enabled)
            {
                // strip the client-side costs: a server has no viewer, so cap the loop and kill vsync
                Application.targetFrameRate = 60;
                QualitySettings.vSyncCount = 0;
                Plugin.Log.LogInfo($"Dedicated server mode: port {Port}, seed {(Seed == 0 ? "<continue the last save, or random>" : Seed.ToString())}, frame cap 60");
            }
        }

        /// Runner, once per frame.
        public static void Tick()
        {
            if (!Enabled || DataFromMenuScript.s == null) return;
            if (DataFromMenuScript.s.mainmenu)
            {
                if (!_worldStarted && menuhandler.s != null && savedatascript.s != null) TryStartWorld();
                return;
            }
            if (_hosted)
            {
                AdminConsole.Tick();
                if (StripVisuals && Time.realtimeSinceStartup - _stripAt > StripEveryS) { _stripAt = Time.realtimeSinceStartup; Strip(); }
                if (StopRequested)
                {
                    StopRequested = false;
                    try { Plugin.Log.LogInfo("Dedicated server: stop: " + Mp.SaveTail()); } catch (Exception e) { Plugin.Log.LogWarning("Dedicated server: save before stop failed: " + e.Message); }
                    Mp.Stop();
                    Application.Quit();
                    return;
                }
                // the world and the players' records to disk now and then: a dedicated server has nobody to press Save
                if (AutoSaveMinutes > 0f && Mp.Server != null && Time.realtimeSinceStartup - _savedAt > AutoSaveMinutes * 60f)
                {
                    _savedAt = Time.realtimeSinceStartup;
                    try { Plugin.Log.LogInfo("Dedicated server: autosave " + Mp.SaveTail(automatic: true)); AutoSaves++; }
                    catch (Exception e) { Plugin.Log.LogWarning("Dedicated server: autosave failed: " + e.Message); }
                }
                return;
            }
            if (mainscript.s == null || mainscript.s.player == null) return;
            if (_worldAt < 0f)
            {
                _worldAt = Time.realtimeSinceStartup;
                AudioListener.volume = 0f;   // headless is not silent: batchmode only kills graphics
                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>(true)) src.enabled = false;
                _audioStripped = true;
            }
            if (Time.realtimeSinceStartup - _worldAt < 5f) return;   // let the world settle, like the client's Ready does
            try { kaposztaleves.s.settings.god = true; } catch { }   // the phantom must never die
            Mp.Host((ushort)Port);
            _hosted = true; _savedAt = Time.realtimeSinceStartup;
            if (Mp.Server != null) Mp.Server.Password = Password;
            MpServer.MaxPlayers = MaxPlayers;
            Tpa.Enabled = TpaOn; Tpa.CooldownS = TpaCooldownS;
            Plugin.Log.LogInfo($"Dedicated server: hosting on {Port}");
        }

        /// Bridge `server strip`: one sweep now; counts.
        public static string Strip()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                if (src.enabled) { src.Stop(); src.enabled = false; StrippedAudio++; }
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
            {
                var em = ps.emission;
                if (!em.enabled && !ps.isPlaying) continue;
                em.enabled = false; ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); StrippedParticles++;
            }
            StripSweeps++; StripLastMs = (float)sw.Elapsed.TotalMilliseconds;
            int audioOn = 0, particlesOn = 0;   // what is still running after the sweep (0 and 0 on a server)
            foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>()) if (src.enabled) audioOn++;
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>()) if (ps.isPlaying) particlesOn++;
            return "{\"sweeps\":" + StripSweeps + ",\"audio\":" + StrippedAudio + ",\"particles\":" + StrippedParticles + ",\"audioOn\":" + audioOn + ",\"particlesOn\":" + particlesOn +
                   ",\"lastMs\":" + StripLastMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        private static void TryStartWorld()
        {
            _worldStarted = true;   // once: a failed start must not loop
            if (Seed != 0)          // an explicit seed forces a fresh world (a deterministic first boot)
            {
                DataFromMenuScript.s.seed = Seed;
                Plugin.Log.LogInfo($"Dedicated server: new world, seed {Seed}");
                menuhandler.s.PressedStart();
                return;
            }
            var last = mainscript.GetLastSaveName();
            var lastPath = string.IsNullOrEmpty(last) ? null : pathscript.SaveDataName(last);
            if (!string.IsNullOrEmpty(last) && System.IO.File.Exists(lastPath))
            {
                Plugin.Log.LogInfo($"Dedicated server: continuing the last save '{last}'");
                var screen = savedatascript.s.saveScreen != null ? savedatascript.s.saveScreen : UnityEngine.Object.FindObjectOfType<newSaveScreenScript>(true);
                if (screen != null) { screen.Load(lastPath); return; }
                Plugin.Log.LogInfo("Dedicated server: no save screen found — falling back to a new world");
            }
            DataFromMenuScript.s.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            Plugin.Log.LogInfo($"Dedicated server: new world, seed {DataFromMenuScript.s.seed}");
            menuhandler.s.PressedStart();
        }
    }
}
