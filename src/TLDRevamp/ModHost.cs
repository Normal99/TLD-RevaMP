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
    /// once a game world is up, and Update/FixedUpdate/OnGUI for mods that override them.
    ///
    /// Everything goes through reflection: TLDRevamp has no compile-time reference to the stub, so a missing stub or a
    /// broken mod can never stop TLDRevamp's own types (and patches) from loading.
    public static class ModHost
    {
        private sealed class Loaded
        {
            public object Mod; public string Id, Name, Version, File;
            public MethodInfo OnMenuLoad, OnLoad, Update, FixedUpdate, OnGUI;   // null: not overridden
            public string LastError;
        }

        private static readonly List<Loaded> Mods = new List<Loaded>();
        private static bool _started, _inGame, _gameLoadedCalled, _menuCalled;
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
                            l.OnMenuLoad = menu ? Overridden(t, modBase, "OnMenuLoad") ?? modBase.GetMethod("OnMenuLoad") : null;
                            l.OnLoad = modBase.GetMethod("OnLoad");
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
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return m != null && m.DeclaringType != modBase ? m : null;
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
            bool inGame = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == menuhandler.sceneName;
            if (inGame != _inGame) { _inGame = inGame; _gameLoadedCalled = false; _menuCalled = false; }
            if (!inGame && !_menuCalled) { _menuCalled = true; foreach (var l in Mods) Call(l, l.OnMenuLoad, "OnMenuLoad"); }
            // OnLoad once the world is actually up (the player exists), as TLDLoader calls it after the game loaded
            if (inGame && !_gameLoadedCalled && mainscript.s != null && mainscript.s.player != null)
            {
                _gameLoadedCalled = true;
                foreach (var l in Mods) Call(l, l.OnLoad, "OnLoad");
            }
            foreach (var l in Mods) Call(l, l.Update, "Update");
            AutopilotBridge.Tick();
        }

        public static void FixedTick() { foreach (var l in Mods) Call(l, l.FixedUpdate, "FixedUpdate"); }
        public static void Gui() { foreach (var l in Mods) Call(l, l.OnGUI, "OnGUI"); }

        public static string Status()
        {
            var rows = new List<string>();
            foreach (var l in Mods) rows.Add("{\"id\":" + Json.Str(l.Id) + ",\"name\":" + Json.Str(l.Name) + ",\"version\":" + Json.Str(l.Version) + ",\"lastError\":" + Json.Str(l.LastError ?? "") + "}");
            return "{\"info\":" + Json.Str(Info) + ",\"inGame\":" + (_inGame ? "true" : "false") + ",\"onLoadCalled\":" + (_gameLoadedCalled ? "true" : "false") + ",\"mods\":[" + string.Join(",", rows) + "]}";
        }
    }
}
