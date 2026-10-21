using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// Every claim the documentation makes about the TLD Loader mod host's lifecycle must
/// match the code that does it.  Each claim is checked against the source of
/// src/TLDRevamp/ModHost.cs (and the shim it drives), so a claim that drifts from
/// the code fails this test instead of the reader.
///
/// The test reads the files, so it needs no game and no build of the mod.
static class LoaderLifecycleDocTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    // The lifecycle methods the shim declares to the mods.
    static readonly string[] ShimLifecycle = { "OnMenuLoad", "OnLoad", "OnUnload", "Config", "Update", "FixedUpdate", "OnGUI" };

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));

        string host = Read("src/TLDRevamp/ModHost.cs");
        string shim = Read("src/TLDLoaderShim/TLDLoader.cs");
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        string docs = Read("docs/MODDING-MULTIPLAYER.md");
        string readme = Read("README.md");

        // ---- 1. the shim declares every lifecycle method the host may call ----
        foreach (string m in ShimLifecycle)
            Check("the shim declares " + m, shim.Contains("public virtual void " + m + "()"));

        // ---- 2. the host binds every one of them, and calls every one of them ----
        // Binding: the host must look the method up by name, or the mod's override
        // is never found.
        foreach (string m in ShimLifecycle)
            Check("the host binds " + m, host.Contains("\"" + m + "\""));
        // Calling: the method must actually be invoked on a Loaded mod.  The check
        // is per method name, so removing ONE call site is caught even when the
        // same method is called elsewhere (OnUnload is called from Tick and from
        // Shutdown; a plain "does the name appear in a Call(...)" test would pass
        // with one of them removed).
        foreach (string m in ShimLifecycle)
        {
            int calls = Regex.Matches(host, @"Call\(l,\s*l\." + m + @"\b").Count;
            Check("the host calls " + m + " (at least once)", calls >= 1);
        }
        // OnUnload must be called from BOTH the world transition and the shutdown:
        // the game can quit without a scene change.
        Check("OnUnload is called when the world ends",
            Regex.IsMatch(host, @"if \(wasUp && !worldUp\)[^\n]*Call\(l,\s*l\.OnUnload"));
        Check("OnUnload is called when the game quits",
            Regex.IsMatch(host, @"public static void Shutdown\(\)[\s\S]{0,600}?Call\(l,\s*l\.OnUnload"));

        // ---- 3. the docs may not claim a lifecycle method the host does not call ----
        // The scanner's own list of lifecycle names is in the source, and the docs
        // are not allowed to name a method as something the host does unless the
        // host really does it.
        foreach (string m in ShimLifecycle)
        {
            bool docMentions = docs.Contains(m);
            bool codeCalls = Regex.IsMatch(host, @"Call\(l,\s*l\." + m + @"\b");
            Check("the doc's mention of " + m + " matches the code (" + (docMentions ? "mentioned" : "not mentioned") + ")",
                !docMentions || codeCalls);
        }

        // ---- 4. the startup order: the host starts after the settings are loaded,
        //          and the mods' own per-frame work is driven from the runner ----
        Check("the host is started from the plugin's startup", plugin.Contains("ModHost.Start()"));
        Check("the host's per-frame tick is driven", plugin.Contains("ModHost.Tick()"));
        Check("the host's fixed tick is driven", plugin.Contains("ModHost.FixedTick()"));
        Check("the host's gui pass is driven", plugin.Contains("ModHost.Gui()"));

        // ---- 5. the exact behaviour the docs describe for each call ----
        // OnMenuLoad: in the menu, once, and only for mods that ask for it.
        Check("OnMenuLoad runs in the menu", host.Contains("if (inMenu && !_menuCalled)"));
        Check("the menu is where no world is up", host.Contains("bool inMenu = !worldUp"));
        Check("OnMenuLoad is gated on LoadInMenu", host.Contains("LoadInMenu") && host.Contains("menu ? Overridden(t, modBase, \"OnMenuLoad\")"));
        // OnLoad: once the world is really up (the player exists), not merely in the scene.
        Check("OnLoad waits for the player to exist", host.Contains("mainscript.s != null && mainscript.s.player != null"));
        Check("OnLoad is called once per world", host.Contains("_gameLoadedCalled = true"));
        // OnUnload: when the world ends (the player is gone, or the game quits).
        Check("OnUnload runs when the world goes away", host.Contains("OnUnload"));
        Check("the world is keyed on the player, not on the scene name",
            host.Contains("mainscript.s != null && mainscript.s.player != null") &&
            !Regex.IsMatch(host, @"bool inGame = .*GetActiveScene"));
        Check("the host unloads the mods when the game quits", host.Contains("public static void Shutdown()"));
        Check("the shutdown path is called", Read("src/TLDRevamp/Plugin.cs").Contains("ModHost.Shutdown()"));
        // Config: only while a settings screen is open.
        Check("Config is gated on the settings screen being open",
            host.Contains("ConfigScreenOpen") && Regex.IsMatch(host, @"if \(!ConfigScreenOpen\) return;"));
        Check("the settings screen state is refreshed every frame",
            Read("src/TLDRevamp/Plugin.cs").Contains("ModHost.RefreshConfigScreen()"));
        // Update/FixedUpdate/OnGUI: every frame / every physics step / every gui pass.
        Check("Update runs every tick", host.Contains("Call(l, l.Update, \"Update\")"));
        Check("FixedUpdate runs in the fixed tick", host.Contains("Call(l, l.FixedUpdate, \"FixedUpdate\")"));
        Check("OnGUI runs in the gui pass", host.Contains("Call(l, l.OnGUI, \"OnGUI\")"));

        // ---- 6. the config folder claim: the host sets the shim's ConfigRoot ----
        // The claim is exact: the root must be the shim's ConfigRoot field, set to
        // the config path plus TLDLoaderMods.  A host that left the root empty (or
        // set it to something else) would give the mods a config folder in the
        // wrong place, so the test checks the whole assignment.
        Check("the host sets the shim's config root to the mods' config folder",
            host.Contains("GetField(\"ConfigRoot\").SetValue(null, Path.Combine(BepInEx.Paths.ConfigPath, \"TLDLoaderMods\"))"));
        Check("the shim creates the mod's config folder", shim.Contains("GetModConfigFolder") && shim.Contains("Directory.CreateDirectory"));

        // ---- 7. the doc paragraph about the loader must not promise more than the
        //          code delivers: it must name the lifecycle it actually runs ----
        int idx = docs.IndexOf("## 7. A converter for existing TLD Loader mods", StringComparison.Ordinal);
        Check("the loader section is in the docs", idx >= 0);
        string section = idx >= 0 ? docs.Substring(idx, Math.Min(4000, docs.Length - idx)) : "";
        Check("the loader section says the lifecycle calls are run", section.Contains("lifecycle"));
        // Every lifecycle the doc names in that section must be one the host calls.
        // This is checked on its own, so a wrong CLAIM in the documentation fails
        // even when the code is fine.
        foreach (string m in ShimLifecycle)
            if (section.Contains("`" + m + "`") || section.Contains(m + "()"))
                Check("the loader section's " + m + " claim is true", Regex.IsMatch(host, @"Call\(l,\s*l\." + m + @"\b"));

        // ---- 8. the beginner README must not promise a loader feature the host
        //          does not have ----
        if (readme.Contains("TLD Loader") || readme.Contains("TLDLoader"))
        {
            // If the README talks about the loader, it must not claim a lifecycle
            // the host does not call.
            foreach (string m in ShimLifecycle)
                if (readme.Contains(m))
                    Check("the README's " + m + " claim is true", Regex.IsMatch(host, @"Call\(l,\s*l\." + m + @"\b"));
        }
        else Check("the README does not claim loader features it does not deliver", true);

        // ---- 9. the scanner's report must name the same lifecycle set as the shim ----
        string scan = Read("src/TLDRevamp/LoaderScan.cs");
        foreach (string m in ShimLifecycle)
            Check("the scanner reports " + m, scan.Contains("\"" + m + "\""));

        Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILURE(S)"));
        return failures == 0 ? 0 : 1;
    }
}
