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
        private static bool _worldStarted, _hosted, _audioStripped;
        private static float _worldAt = -1f;

        /// From Plugin.Awake, before anything hosts.
        public static void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            Enabled = Array.IndexOf(args, "-batchmode") >= 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-tldport") int.TryParse(args[i + 1], out Port);
                if (args[i] == "-tldseed") int.TryParse(args[i + 1], out Seed);
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
            if (_hosted) return;
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
            _hosted = true;
            Plugin.Log.LogInfo($"Dedicated server: hosting on {Port}");
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
