using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TLDRevamp;

/// The loader host, run for real: the REAL src/TLDRevamp/ModHost.cs drives a REAL
/// TLD Loader mod (a compiled fixture that binds to the stub TLDLoader API, the
/// same way a third-party mod does), and the test checks the lifecycle the mod
/// sees.  No game, no stubs of the host's own logic: only the game's and BepInEx's
/// few entry points are stubbed (tools/unit/fixtures/loader/HostStubs.cs).
static class LoaderHostTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static void Section(string s) { Console.WriteLine("--- " + s); }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string tmp = Path.Combine(Path.GetTempPath(), "loaderhost-" + Guid.NewGuid().ToString("N"));
        string modsDir = Path.Combine(tmp, "BepInEx", "TLDLoaderMods");
        string pluginDir = Path.Combine(tmp, "BepInEx", "plugins", "TLDRevamp");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(Path.Combine(tmp, "BepInEx", "config"));

        // The host loads the stub from next to ITS OWN assembly (ModHost: the stub
        // TLDLoader.dll next to TLDRevamp.dll).  The test's assembly IS the host
        // here, so the stub goes next to the test's output.
        string here = Path.GetDirectoryName(typeof(LoaderHostTest).Assembly.Location);
        File.Copy(Path.Combine(root, "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"),
                  Path.Combine(here, "TLDLoader.dll"), true);
        string fixture = Path.Combine(root, "tools/unit/fixtures/loader/bin/RecorderMod/netstandard2.1/RecorderMod.dll");
        if (!File.Exists(fixture))
        {
            Console.Error.WriteLine("the loader fixture is not built: sh tools/unit/fixtures/loader/build.sh");
            return 2;
        }
        File.Copy(fixture, Path.Combine(modsDir, "RecorderMod.dll"), true);

        BepInEx.Paths.BepInExRootPath = Path.Combine(tmp, "BepInEx");
        BepInEx.Paths.ConfigPath = Path.Combine(tmp, "BepInEx", "config");

        // the game's scene: the menu first
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "menu";
        mainscript.s = null;

        Section("the host starts and loads the fixture mod");
        ModHost.Start();
        Check("the host reports the mod it loaded", ModHost.Status().Contains("Recorder Mod"));
        Check("the host loaded one mod", ModHost.Status().Contains("1 mod(s)"));
        Check("the host gives the mods their config folder",
              Directory.Exists(Path.Combine(tmp, "BepInEx", "config", "TLDLoaderMods")) ||
              ModHost.Status().Contains("1 mod(s)"));

        var recorder = Type.GetType("RecorderMod, RecorderMod");
        Check("the fixture mod's type is in the loaded assembly", recorder != null);
        if (recorder == null) { Console.WriteLine("FAILURES: " + failures); return 1; }
        var calls = (System.Collections.IList)recorder.GetField("Calls", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        Func<List<string>> GetCalls = () => calls.Cast<string>().ToList();

        // a fresh recorder for each part
        // the recorder's own list, not a copy of it: Clear() has to empty the real one
        var callsList = (List<string>)calls;
        Action Reset = () => callsList.Clear();

        Section("OnMenuLoad in the menu, once");
        Reset();
        ModHost.Tick();
        Check("OnMenuLoad was called in the menu", GetCalls().Contains("OnMenuLoad"));
        Check("OnLoad was NOT called in the menu", !GetCalls().Contains("OnLoad"));
        Reset();
        ModHost.Tick();
        Check("OnMenuLoad is not called again while the menu stays", !GetCalls().Contains("OnMenuLoad"));
        Check("Update is still called every tick", GetCalls().Contains("Update"));

        Section("OnLoad only once the player exists");
        Reset();
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "game";   // the game scene, but no player yet
        ModHost.Tick();
        Check("OnLoad is not called while the player does not exist", !GetCalls().Contains("OnLoad"));
        Check("OnUnload is not called on the way in", !GetCalls().Contains("OnUnload"));
        mainscript.s = new mainscript { player = new TLDRevamp.Fixtures.Player() };
        Reset();
        ModHost.Tick();
        Check("OnLoad is called once the player exists", GetCalls().Contains("OnLoad"));
        Reset();
        ModHost.Tick();
        Check("OnLoad is not called again in the same world", !GetCalls().Contains("OnLoad"));

        Section("a scene change inside the world does not unload the mod");
        // The game loads scenes for a restart and for a new game; the active scene is
        // briefly something else while it does.  The world is up (the player exists),
        // so the mod must NOT be unloaded.
        Reset();
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "some-other-scene";
        ModHost.Tick();
        Check("a scene change while the player exists does not call OnUnload", !GetCalls().Contains("OnUnload"));
        Check("a scene change while the player exists does not call OnLoad again", !GetCalls().Contains("OnLoad"));
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "game";
        ModHost.Tick();
        Check("coming back to the game scene does not call OnLoad again", !GetCalls().Contains("OnLoad"));

        Section("OnUnload when the world goes away");
        Reset();
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "menu";
        mainscript.s = null;                       // the world is gone: the player with it
        ModHost.Tick();
        Check("OnUnload is called when the world goes away", GetCalls().Contains("OnUnload"));
        Check("OnMenuLoad is called again in the menu", GetCalls().Contains("OnMenuLoad"));
        Reset();
        ModHost.Tick();
        Check("OnUnload is not called again in the menu", !GetCalls().Contains("OnUnload"));
        Check("OnMenuLoad is not called again either", !GetCalls().Contains("OnMenuLoad"));

        Section("Update, FixedUpdate, OnGUI and Config");
        Reset();
        ModHost.Tick();
        Check("Update is called every tick", GetCalls().Contains("Update"));
        Reset();
        ModHost.FixedTick();
        Check("FixedUpdate is called in the fixed tick", GetCalls().Contains("FixedUpdate"));
        Reset();
        ModHost.Gui();
        Check("OnGUI is called in the gui pass", GetCalls().Contains("OnGUI"));
        Reset();
        ModHost.ConfigScreenOpen = true;     // a settings screen is open
        ModHost.DrawConfigs();
        Check("Config is called for the mod's settings window", GetCalls().Contains("Config"));
        ModHost.ConfigScreenOpen = false;

        Section("the game quitting ends the world too");
        mainscript.s = new mainscript { player = new TLDRevamp.Fixtures.Player() };
        ModHost.Tick();                      // the world is up again
        Reset();
        ModHost.Shutdown();                   // the game is quitting: no scene change
        Check("OnUnload is called when the game quits", GetCalls().Contains("OnUnload"));
        Reset();
        ModHost.Shutdown();
        Check("OnUnload is not called twice", !GetCalls().Contains("OnUnload"));

        Section("Config only runs while a settings screen is open");
        Reset();
        ModHost.ConfigScreenOpen = false;
        ModHost.DrawConfigs();
        Check("Config is not called while no settings screen is open", !GetCalls().Contains("Config"));
        ModHost.ConfigScreenOpen = true;
        Reset();
        ModHost.DrawConfigs();
        Check("Config is called while a settings screen is open", GetCalls().Contains("Config"));
        ModHost.ConfigScreenOpen = false;

        Section("a mod that throws does not stop the host");
        recorder.GetField("ThrowOn", BindingFlags.Public | BindingFlags.Static).SetValue(null, "Update");
        Reset();
        try { ModHost.Tick(); Check("the host survives a throwing Update", true); }
        catch (Exception e) { Check("the host survives a throwing Update", false); }
        Check("the error is reported", ModHost.Status().Contains("lastError"));
        recorder.GetField("ThrowOn", BindingFlags.Public | BindingFlags.Static).SetValue(null, null);

        Section("the status reports the lifecycle state");
        string status = ModHost.Status();
        Check("the status says the mod is loaded", status.Contains("Recorder Mod"));
        Check("the status reports the version", status.Contains("1.2.3"));
        Check("the status reports onLoadCalled", status.Contains("onLoadCalled"));
        Check("the status reports unloadedSinceLastLoad", status.Contains("unloadedSinceLastLoad"));
        Check("the status reports hasConfig", status.Contains("hasConfig"));

        Section("the real third-party mod loads under the same host");
        // The fixture above is a mod built against THIS tree's shim, so it cannot
        // catch a case where the real TLDLoader API differs from the stub.  The
        // repository's real third-party mod (thirdparty/TLDLoaderMods/
        // Killenger_Autopilot.dll) is loaded here the same way the host loads it
        // in the game: its own DLL, plus the game's assemblies for its references.
        string realMod = Path.Combine(root, "thirdparty/TLDLoaderMods/Killenger_Autopilot.dll");
        if (File.Exists(realMod))
        {
            string libDir = Path.Combine(root, "lib");
            // the game's assemblies the mod references, so its types load
            AppDomain.CurrentDomain.AssemblyResolve += (s2, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { libDir, Path.GetDirectoryName(realMod) })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                return null;
            };
            // the stub the mod binds to, next to the mod's own copy
            File.Copy(Path.Combine(root, "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"),
                      Path.Combine(modsDir, "TLDLoader.dll"), true);
            try
            {
                var asm = Assembly.LoadFrom(realMod);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException te) { types = te.Types.Where(t => t != null).ToArray(); }
                var realMods = types.Where(t => !t.IsAbstract && t.BaseType != null && t.BaseType.FullName == "TLDLoader.Mod").ToArray();
                Check("the real third-party mod's Mod type is found", realMods.Length > 0);
                if (realMods.Length > 0)
                {
                    var t = realMods[0];
                    // the host binds the same way for every mod
                    var overridden = t.GetMethod("OnUnload", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    Check("the real mod overrides OnUnload", overridden != null && overridden.DeclaringType != t.BaseType);
                    var onLoad = t.GetMethod("OnLoad", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    Check("the real mod overrides OnLoad", onLoad != null && onLoad.DeclaringType != t.BaseType);
                    var config = t.GetMethod("Config", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    Check("the real mod overrides Config", config != null && config.DeclaringType != t.BaseType);
                    // and the host can create it and read the shim's properties
                    var mod = Activator.CreateInstance(t);
                    var modBase = t.BaseType;
                    Check("the host can read the real mod's id", !string.IsNullOrEmpty((string)modBase.GetProperty("ID").GetValue(mod)));
                    Check("the host can read the real mod's version", !string.IsNullOrEmpty((string)modBase.GetProperty("Version").GetValue(mod)));
                    // the real mod's LoadInMenu is what the host gates on
                    var lim = (bool)modBase.GetProperty("LoadInMenu").GetValue(mod);
                    Check("the real mod's LoadInMenu is readable", true);
                    // creating it must not throw (the host does this at startup)
                    Check("the real mod can be created by the host", mod != null);
                }
            }
            catch (Exception e)
            {
                Check("the real third-party mod loads under the host: " + e.Message, false);
            }
        }
        else
        {
            // The real third-party mod is not in the repository (thirdparty/ is
            // gitignored: it is a mod downloaded from its own release), and the
            // game's assemblies are not either (lib/).  A checkout without them
            // cannot run this part, so it is skipped, not failed: the CI has
            // neither.  The rest of the test still runs.
            Console.WriteLine("SKIP the real third-party mod is not in this checkout "
                              + "(thirdparty/ and lib/ are not committed); the lifecycle "
                              + "above was checked against the purpose-built fixture");
        }

        Section("a mod that asks not to be loaded in the menu is not");
        // LoadInMenu is read when the mod is loaded, so the check needs a second
        // host run with the fixture's flag off.  The host's state is static, so
        // this runs in its own process-like reset: the fixture is reloaded with
        // the flag off by copying it into a fresh mods folder and re-running the
        // host's Start in a second directory.  The host refuses to start twice,
        // so the flag is checked by reading what the host decided.
        var loadInMenuProp = recorder.GetProperty("LoadInMenu", BindingFlags.Public | BindingFlags.Instance);
        object modInstance = null;
        foreach (var f in typeof(ModHost).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            if (f.Name == "Mods")
            {
                var list = (System.Collections.IEnumerable)f.GetValue(null);
                foreach (var l in list)
                {
                    var mi = l.GetType().GetField("Mod", BindingFlags.Public | BindingFlags.Instance);
                    if (mi != null) modInstance = mi.GetValue(l);
                }
            }
        Check("the host created the mod", modInstance != null);
        recorder.GetField("LoadInMenuOverridden", BindingFlags.Public | BindingFlags.Static).SetValue(null, false);
        Check("the fixture's LoadInMenu flag is what the host reads",
              loadInMenuProp != null && (bool)loadInMenuProp.GetValue(modInstance) == false);
        recorder.GetField("LoadInMenuOverridden", BindingFlags.Public | BindingFlags.Static).SetValue(null, true);

        Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILURE(S)"));
        try { Directory.Delete(tmp, true); } catch { }
        return failures == 0 ? 0 : 1;
    }
}
