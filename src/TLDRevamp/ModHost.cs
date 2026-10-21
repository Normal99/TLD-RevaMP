using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// Runs TLDLoader mods under BepInEx on the beta (TLDLoader itself doesn't work there). The mods' DLLs go in
    /// BepInEx/TLDLoaderMods/; the stub TLDLoader.dll next to our plugin provides the API they reference (Mod, its
    /// logger, the config folder). Lifecycle as TLDLoader's: OnMenuLoad in the main menu (mods with LoadInMenu), OnLoad
    /// once a game world is up (the player exists), OnUnload when that world ends, Config for the mod's own settings
    /// window, and Update/FixedUpdate/OnGUI for mods that override them.
    ///
    /// Everything goes through reflection: TLDRevamp has no compile-time reference to the stub, so a missing stub or a
    /// broken mod can never stop TLDRevamp's own types (and patches) from loading.
    public static class ModHost
    {
        private sealed class Loaded
        {
            public object Mod; public string Id, Name, Version, File;
            public MethodInfo OnMenuLoad, OnLoad, OnUnload, Config, Update, FixedUpdate, OnGUI;   // null: not overridden
            public string LastError;
        }

        private static readonly List<Loaded> Mods = new List<Loaded>();
        private static bool _started, _worldUp, _gameLoadedCalled, _menuCalled, _unloadCalled;
        public static string Info = "not started";

        public static string ModsDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "TLDLoaderMods");

        public static void Start()
        {
            if (_started) return;
            _started = true;
            try
            {
                if (!Directory.Exists(ModsDir)) { Info = "no " + ModsDir; return; }
                var dlls = Directory.GetFiles(ModsDir, "*.dll");
                if (dlls.Length == 0) { Info = "no mods in " + ModsDir; return; }
                string stubPath = Path.Combine(Path.GetDirectoryName(typeof(ModHost).Assembly.Location), "TLDLoader.dll");
                if (!File.Exists(stubPath)) { Info = "stub TLDLoader.dll missing next to TLDRevamp.dll"; Plugin.Log.LogWarning("ModHost: " + Info); return; }
                var stub = Assembly.LoadFrom(stubPath);
                var modBase = stub.GetType("TLDLoader.Mod");
                stub.GetType("TLDLoader.ModLoader").GetField("ConfigRoot").SetValue(null, Path.Combine(BepInEx.Paths.ConfigPath, "TLDLoaderMods"));
                var hostLogger = stub.GetType("TLDLoader.HostLogger");
                foreach (var dll in dlls)
                {
                    Type[] types;
                    try { types = Assembly.LoadFrom(dll).GetTypes(); }
                    catch (ReflectionTypeLoadException e) { Plugin.Log.LogError("ModHost: " + Path.GetFileName(dll) + " types failed: " + e.LoaderExceptions[0]); continue; }
                    catch (Exception e) { Plugin.Log.LogError("ModHost: " + Path.GetFileName(dll) + " failed to load: " + e.Message); continue; }
                    foreach (var t in types)
                    {
                        if (t.IsAbstract || !modBase.IsAssignableFrom(t)) continue;
                        try
                        {
                            var mod = Activator.CreateInstance(t);
                            var l = new Loaded { Mod = mod, File = Path.GetFileName(dll) };
                            l.Id = (string)modBase.GetProperty("ID").GetValue(mod);
                            l.Name = (string)modBase.GetProperty("Name").GetValue(mod);
                            l.Version = (string)modBase.GetProperty("Version").GetValue(mod);
                            var log = BepInEx.Logging.Logger.CreateLogSource(l.Id);
                            modBase.GetProperty("Logger").SetValue(mod, Activator.CreateInstance(hostLogger,
                                (Action<string>)(m => log.LogInfo(m)), (Action<string>)(m => log.LogWarning(m)), (Action<string>)(m => log.LogError(m))));
                            bool menu = (bool)modBase.GetProperty("LoadInMenu").GetValue(mod);
                            // Every lifecycle method is bound the same way: the mod's own
                            // override, or nothing.  (OnMenuLoad additionally needs the mod
                            // to ask for it with LoadInMenu.)  The base class's methods are
                            // empty, so calling them would be a no-op; not binding them
                            // keeps "did the mod override it" answerable.
                            l.OnMenuLoad = menu ? Overridden(t, modBase, "OnMenuLoad") : null;
                            l.OnLoad = Overridden(t, modBase, "OnLoad") ?? modBase.GetMethod("OnLoad");
                            l.OnUnload = Overridden(t, modBase, "OnUnload");
                            l.Config = Overridden(t, modBase, "Config");
                            l.Update = Overridden(t, modBase, "Update");
                            l.FixedUpdate = Overridden(t, modBase, "FixedUpdate");
                            l.OnGUI = Overridden(t, modBase, "OnGUI");
                            Mods.Add(l);
                            Plugin.Log.LogInfo($"ModHost: loaded {l.Name} {l.Version} ({l.Id}) from {l.File}");
                        }
                        catch (Exception e) { Plugin.Log.LogError("ModHost: creating " + t.FullName + " failed: " + (e.InnerException ?? e)); }
                    }
                }
                Info = Mods.Count + " mod(s)";
                AutopilotBridge.Install();   // test tooling: the autopilot port made aware of other players
            }
            catch (Exception e) { Info = "failed: " + e.Message; Plugin.Log.LogError("ModHost: " + e); }
        }

        private static MethodInfo Overridden(Type t, Type modBase, string name)
        {
            // A lookup that throws (an odd declaration) must not cost the mod its
            // whole registration: the caller is inside a try, but a throw here would
            // skip the rest of the mod's setup.
            try
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return m != null && m.DeclaringType != modBase ? m : null;
            }
            catch { return null; }
        }

        private static void Call(Loaded l, MethodInfo m, string what)
        {
            if (m == null) return;
            try { m.Invoke(l.Mod, null); }
            catch (Exception e)
            {
                string err = what + ": " + (e.InnerException ?? e).Message;
                if (err != l.LastError) { l.LastError = err; Plugin.Log.LogError("ModHost: " + l.Id + " " + err); }   // once, not per frame
            }
        }

        /// Per frame (Runner.Update): menu/game transitions, then the mods' own Update.
        public static void Tick()
        {
            if (Mods.Count == 0) return;
            // The world's own signal is the PLAYER, not the scene name: the game loads
            // scenes for a restart and for a new game, and the active scene is briefly
            // something else while it does. Keying the unload on the scene would fire
            // OnUnload for a scene change that does not end the world. The player
            // appears with the world and is gone when it ends, which is what OnLoad
            // and OnUnload are about.
            bool worldUp = mainscript.s != null && mainscript.s.player != null;
            if (worldUp != _worldUp)
            {
                bool wasUp = _worldUp;
                _worldUp = worldUp;
                _gameLoadedCalled = false;
                _menuCalled = false;
                // Leaving the world unloads the mod: TLDLoader calls OnUnload when the
                // game it loaded into goes away, so the mod can undo what it did.
                if (wasUp && !worldUp) { _unloadCalled = true; foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload"); }
            }
            // The menu is the game's own main-menu scene; the world is up whenever the
            // player exists. In the menu the player does not exist, so a mod that asked
            // for the menu gets its OnMenuLoad there.
            bool inMenu = !worldUp && !_gameLoadedCalled && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != menuhandler.sceneName;
            if (inMenu && !_menuCalled) { _menuCalled = true; foreach (var l in Mods) Call(l, l.OnMenuLoad, "OnMenuLoad"); }
            // OnLoad once the world is actually up (the player exists), as TLDLoader calls it after the game loaded
            if (worldUp && !_gameLoadedCalled)
            {
                _gameLoadedCalled = true;
                _unloadCalled = false;
                foreach (var l in Mods) Call(l, l.OnLoad, "OnLoad");
            }
            foreach (var l in Mods) Call(l, l.Update, "Update");
            AutopilotBridge.Tick();
        }

        /// The game is quitting or the plugin is going away: the world ends without a
        /// scene change, so OnUnload has to be called here or the mods never get to
        /// undo what they did.
        public static void Shutdown()
        {
            if (!_worldUp) return;
            _worldUp = false;
            _gameLoadedCalled = false;
            _unloadCalled = true;
            foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload");
        }

        public static void FixedTick() { foreach (var l in Mods) Call(l, l.FixedUpdate, "FixedUpdate"); }
        public static void Gui() { foreach (var l in Mods) Call(l, l.OnGUI, "OnGUI"); }

        /// Set by the settings screen while it is open: a Loader mod's Config() draws
        /// its own window, and it must only be called then.  Unity runs the IMGUI
        /// pass several times per frame, so calling Config() unconditionally would
        /// run a mod's settings code many times a second for as long as the game is
        /// open - and for a mod that does one-time work in Config() (creating
        /// objects, reading files) that is a real bug, not a detail.
        public static bool ConfigScreenOpen;

        /// The mod's own settings window.  TLDLoader mods draw it with IMGUI
        /// (GUI.*) inside Config(), so the host calls it in its own IMGUI pass, and
        /// only while the settings screen is open.  The mod draws where it wants; a
        /// mod that ignores ConfigScrollHeight still works, because the host does
        /// not reserve an area for it.
        public static void DrawConfigs()
        {
            if (!ConfigScreenOpen) return;
            foreach (var l in Mods) Call(l, l.Config, "Config");
        }

        /// The settings screen may be closed without a tab press (the back button),
        /// so the flag is refreshed from the screen's own state every frame instead
        /// of only when a tab is pressed.  A Loader mod's Config() must not run
        /// while no settings screen is open.
        public static void RefreshConfigScreen()
        {
            bool open = false;
            try
            {
                var s = settingsscript.s;
                if (s != null)
                {
                    var root = s.transform.Find("NewSettings");
                    if (root != null)
                    {
                        var tab = root.Find("TabRevamp");
                        open = tab != null && tab.gameObject.activeInHierarchy;
                    }
                }
            }
            catch { }
            ConfigScreenOpen = open;
        }

        /// Whether any mod wants a settings window of its own (so the tab can make room).
        public static bool AnyConfig => Mods.Exists(l => l.Config != null);

        /// Whether OnUnload has run since the last OnLoad (the status line reports it).
        public static bool UnloadCalled => _unloadCalled;

        public static string Status()
        {
            var rows = new List<string>();
            foreach (var l in Mods) rows.Add("{\"id\":" + Json.Str(l.Id) + ",\"name\":" + Json.Str(l.Name) + ",\"version\":" + Json.Str(l.Version) + ",\"lastError\":" + Json.Str(l.LastError ?? "") + "}");
            return "{\"info\":" + Json.Str(Info) + ",\"inGame\":" + (_worldUp ? "true" : "false") + ",\"onLoadCalled\":" + (_gameLoadedCalled ? "true" : "false") +
                   ",\"unloadedSinceLastLoad\":" + (_unloadCalled ? "true" : "false") + ",\"hasConfig\":" + (AnyConfig ? "true" : "false") + ",\"mods\":[" + string.Join(",", rows) + "]}";
        }
    }
}
