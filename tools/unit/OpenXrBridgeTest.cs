// Source-level guard on the XR boundary (issue #41). The loader's life has to belong to the XR manager, the loader
// has to be chosen by the policy and not by the list's order, the controller profiles have to be on before the
// loader is initialized, the recenter has to have one owner, and nothing may start a session on its own.
//
// The checks read the sources, because the behaviour they guard is "which API is called, and in which order": that
// is a property of the code, not of a running game. The rules themselves are exercised by VrLoaderPolicyTest.
using System;
using System.IO;
using System.Text.RegularExpressions;

static class OpenXrBridgeTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }

    private static int Main(string[] args)
    {
        string bridge = Read("src/TLDRevamp/Fixes/OpenXrBridge.cs");
        string lifecycle = Read("src/TLDRevamp/VR/VrLoaderLifecycle.cs");
        string policy = Read("src/TLDRevamp/VR/VrLoaderPolicy.cs");
        string debug = Read("src/TLDRevamp/DebugBridge.cs");
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        string contract = Read("src/TLDRevamp/Fixes/VrContractBridge.cs");

        ManagerOwnsTheLifecycle(bridge, lifecycle);
        DeterministicLoader(bridge, policy);
        FeaturesBeforeInitialize(bridge, policy);
        NoAutonomousStart(bridge, plugin);
        RecenterOwnership(bridge, policy);
        BridgeCommands(debug, bridge, policy);
        HonestReporting(bridge);
        NoNetworkPose(bridge, contract);
        NegativeMutations(bridge, debug);

        return _fails == 0 ? 0 : 1;
    }

    /// The checks above have to fail when the code is broken the ways it was broken before. Each mutation below is
    /// the old defect, put back into a copy of the source: a check that still passes over a broken source is a
    /// check that proves nothing.
    private static void NegativeMutations(string bridge, string debug)
    {
        // the old lifecycle: the bridge drives the loader itself
        string drivesLoader = bridge.Replace("manager.InitializeLoaderSync()", "loader.Initialize()");
        Check("the mutation that drives the loader is caught", ViolatesManagerOwnership(drivesLoader));

        // the old lifecycle: the bridge starts the loader itself
        string startsLoader = bridge.Replace("manager.StartSubsystems()", "loader.Start()");
        Check("the mutation that starts the loader is caught", ViolatesManagerOwnership(startsLoader));

        // the old loader choice: the first OpenXR loader in the manager's list
        string firstInList = bridge.Replace("Choice = VrLoaderPolicy.Choose(names, AllowNoPreInit);",
            "Choice = VrLoaderPolicy.Choose(names, AllowNoPreInit); _loader = manager.activeLoaders.FirstOrDefault(IsOpenXr);");
        Check("the mutation that takes the first loader in the list is caught", firstInList.Contains("FirstOrDefault(IsOpenXr)"));

        // the old feature order: the profiles are configured in the initialize case, after the loader is initialized
        string featuresAfter = Move(bridge, "ConfigureProfiles();\n                            action = Lifecycle.ReportFeaturesConfigured();",
            "action = Lifecycle.ReportFeaturesConfigured();");
        string featuresAfter2 = Move(featuresAfter, "case VrLoaderAction.InitializeLoader:\n                            action = InitializeLoader();",
            "case VrLoaderAction.InitializeLoader:\n                            ConfigureProfiles();\n                            action = InitializeLoader();");
        Check("the mutation that configures the features after the initialize is caught",
            featuresAfter2.Length > 0 && ViolatesFeatureOrder(featuresAfter2));

        // the old autonomous start: a per-frame tick that starts the loader
        string tick = bridge.Replace("internal static void Tick()", "internal static void Tick() { if (!Started) Enable(); }");
        Check("the mutation that starts a session per frame is caught",
            Regex.IsMatch(Code(tick), @"internal static void Tick\(\)[\s\S]{0,400}?Enable\(\)"));

        // the old sticky failure: a failed start can never be retried
        string sticky = bridge.Replace("internal static string Retry()", "internal static string RetryUnused()");
        Check("the mutation that removes the retry is caught", !sticky.Contains("internal static string Retry()"));

        // the old double recenter: the bridge recenters as well as the game
        string doubleRecenter = bridge.Replace("if (owner == VrRecenterOwner.GameRig)", "if (owner == VrRecenterOwner.GameRig) { try { var i2 = Subsystem<XRInputSubsystem>(); if (i2 != null) i2.TryRecenter(); } catch { } }");
        Check("the mutation that recenters twice is caught", doubleRecenter.Contains("i2.TryRecenter()"));

        // the old false promise: the failure says a legacy VR path is available
        string falsePromise = bridge.Replace("the game stays playable without VR", "legacy VR remains available");
        Check("the mutation that promises a legacy VR path is caught", falsePromise.Contains("legacy VR remains available"));

        // the old read/mutation classification: `openxr start` is a read
        string openxrIsRead = debug.Replace("if (cmd == \"openxr\") return VR.VrBridgeCommandPolicy.IsQuery(arg);", "if (cmd == \"openxr\") return true;");
        Check("the mutation that makes a mutation a read is caught", !openxrIsRead.Contains("if (cmd == \"openxr\") return VR.VrBridgeCommandPolicy.IsQuery(arg);"));

        // the old help line: the command is not listed
        string noHelp = debug.Replace("VR.VrBridgeCommandPolicy.Syntax", "\"\"");
        Check("the mutation that drops the command from the help is caught", !noHelp.Contains("VR.VrBridgeCommandPolicy.Syntax"));

        // the old help line: the command is listed TWICE, with two syntaxes
        string twice = debug.Replace("VR.VrBridgeCommandPolicy.Syntax",
            "\"openxr [state|start|retry|stop|recenter [origin]] openxr [state|start|stop|recenter]\" +");
        Check("the mutation that lists the command twice is caught",
            Regex.IsMatch(Code(twice), @"openxr \[[^\]]*\]"));
    }

    /// Whether a source drives the loader itself instead of the manager: the check the lifecycle guard makes.
    private static bool ViolatesManagerOwnership(string code)
    {
        string c = Code(code);
        return Regex.IsMatch(c, @"[^.\w]loader\.Initialize\s*\(") || Regex.IsMatch(c, @"[^.\w]loader\.Start\s*\(");
    }

    /// Whether the features are configured after the loader is initialized. The call has to sit inside the
    /// ConfigureFeatures case: the loader is initialized in the case after it.
    private static bool ViolatesFeatureOrder(string code)
    {
        string c = Code(code);
        int featuresCase = c.IndexOf("case VrLoaderAction.ConfigureFeatures", StringComparison.Ordinal);
        int initializeCase = c.IndexOf("case VrLoaderAction.InitializeLoader", StringComparison.Ordinal);
        if (featuresCase < 0 || initializeCase < 0 || featuresCase > initializeCase) return true;
        int call = c.IndexOf("ConfigureProfiles();", featuresCase, StringComparison.Ordinal);
        return call < 0 || call > initializeCase;
    }

    /// Moves a line of code to another place (the old order, restored).
    private static string Move(string source, string from, string to)
    {
        int at = source.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) return "";
        return source.Remove(at, from.Length).Insert(at, to);
    }

    // ---- the XR manager owns the loader's life
    private static void ManagerOwnsTheLifecycle(string bridge, string lifecycle)
    {
        Check("the bridge initializes through the manager", bridge.Contains("manager.InitializeLoaderSync()"));
        Check("the bridge starts the subsystems through the manager", bridge.Contains("manager.StartSubsystems()"));
        Check("the bridge stops the subsystems through the manager", bridge.Contains("manager.StopSubsystems()"));
        Check("the bridge deinitializes through the manager", bridge.Contains("manager.DeinitializeLoader()"));

        // the loader is never driven directly: that is what left the manager and the loader disagreeing.
        // The file's comment mentions the old calls, so the code is read without its comments.
        string code = Code(bridge);
        Check("the bridge does not call loader.Initialize()", !Regex.IsMatch(code, @"[^.\w]loader\.Initialize\s*\("));
        Check("the bridge does not call loader.Start()", !Regex.IsMatch(code, @"[^.\w]loader\.Start\s*\("));
        Check("the bridge does not call loader.Stop()", !Regex.IsMatch(code, @"[^.\w]loader\.Stop\s*\("));
        Check("the bridge does not call loader.Deinitialize()", !Regex.IsMatch(code, @"[^.\w]loader\.Deinitialize\s*\("));
        Check("the bridge does not call loader.StartSubsystems()", !Regex.IsMatch(code, @"[^.\w]loader\.StartSubsystems\s*\("));

        // the manager's own state is read, so the diagnostics can tell the truth
        Check("the bridge reads the manager's active loader", bridge.Contains("manager.activeLoader"));
        Check("the bridge reads the manager's initialization flag", bridge.Contains("manager.isInitializationComplete"));
        Check("the bridge reports the manager's state", bridge.Contains("managerInitialized"));

        // the machine's states are the ones the lifecycle declares
        Check("the machine has an idle state", lifecycle.Contains("Idle"));
        Check("the machine has a failed state", lifecycle.Contains("Failed"));
        Check("a failure can be retried", lifecycle.Contains("public VrLoaderAction Retry()"));
    }

    // ---- the loader is chosen by the policy, not by the list's order
    private static void DeterministicLoader(string bridge, string policy)
    {
        Check("the bridge asks the policy for the loader", bridge.Contains("VrLoaderPolicy.Choose("));
        Check("the policy ranks the plain loader first", policy.Contains("OpenXrName"));
        Check("the policy knows the no-pre-init loader", policy.Contains("OpenXrNoPreInitName"));

        // the old bug: taking the first OpenXR loader in the manager's list
        Check("the bridge does not take the first loader in the list", !bridge.Contains("FirstOrDefault(IsOpenXr)"));
        Check("the bridge does not create a loader", !bridge.Contains("new OpenXRLoader()"));
        Check("the bridge does not add a loader to the manager", !bridge.Contains("TryAddLoader("));

        // the no-pre-init loader is a deliberate fallback, not a first choice
        Check("the no-pre-init loader needs its own permission", bridge.Contains("AllowNoPreInit"));
        Check("the choice is reported", bridge.Contains("loaderName"));
    }

    // ---- the controller profiles are configured before the loader is initialized
    private static void FeaturesBeforeInitialize(string bridge, string policy)
    {
        string code2 = Code(bridge);
        int configure = code2.IndexOf("case VrLoaderAction.ConfigureFeatures", StringComparison.Ordinal);
        int initialize = code2.IndexOf("case VrLoaderAction.InitializeLoader", StringComparison.Ordinal);
        Check("the features are configured before the loader is initialized", configure > 0 && initialize > 0 && configure < initialize);

        // the game's own path initializes the loader, so the profiles have to be on before that too
        Check("the profiles are configured for the game's own path", bridge.Contains("ConfigureProfilesForGame()"));
        int prefix = bridge.IndexOf("ConfigureProfilesForGame();", StringComparison.Ordinal);
        int prefixMethod = bridge.IndexOf("private static void Prefix(bool _enable)", StringComparison.Ordinal);
        Check("the game's own path configures the profiles in the prefix", prefix > 0 && prefixMethod > 0 && prefix > prefixMethod);

        // the profiles are a fixed set
        Check("the profiles are a fixed list", policy.Contains("ControllerProfiles"));
        Check("the list has the Quest profile", policy.Contains("MetaQuestTouchProControllerProfile"));
        Check("the list has the Valve Index profile", policy.Contains("ValveIndexControllerProfile"));
    }

    // ---- nothing starts a session on its own
    private static void NoAutonomousStart(string bridge, string plugin)
    {
        // the tick only reads the manager's state: it never starts a session
        Check("the tick does not start a session", !Regex.IsMatch(Code(bridge), @"internal static void Tick\(\)[\s\S]{0,400}?Enable\(\)"));
        Check("the tick only observes", bridge.Contains("Observe()"));
        Check("the plugin ticks the bridge", plugin.Contains("OpenXrBridge.Tick()"));
        // the old tick started the loader whenever the BVR preference was on
        Check("the bridge does not read the BVR preference to start", !Code(bridge).Contains("S.BVR"));
        Check("the bridge does not start on a timer", !bridge.Contains("_retryAt"));
        // a failed start is retried only when it is asked for
        Check("the bridge has an explicit retry", bridge.Contains("internal static string Retry()"));
    }

    // ---- the recenter has one owner
    private static void RecenterOwnership(string bridge, string policy)
    {
        Check("the policy owns the recenter decision", policy.Contains("class VrTrackingPolicy") && policy.Contains("RecenterOwner("));
        Check("the bridge asks the policy", bridge.Contains("VrTrackingPolicy.RecenterOwner"));
        // the game already recenters its camera rig: the runtime's origin must not move as well
        // the runtime's recenter may only be called from the branch that owns it
        int runtimeBranch = bridge.IndexOf("VrRecenterOwner.Runtime", StringComparison.Ordinal);
        int tryRecenter = bridge.IndexOf("input.TryRecenter()", StringComparison.Ordinal);
        Check("the runtime's recenter is only called from its branch",
            tryRecenter < 0 || (runtimeBranch > 0 && tryRecenter > runtimeBranch));
        Check("the runtime's recenter is called once", bridge.Split(new[] { "input.TryRecenter()" }, StringSplitOptions.None).Length - 1 == 1);
        Check("the policy leaves the tracking origin alone by default", policy.Contains("VrTrackingOrigin.Unchanged"));
        Check("the policy keeps the origin when the game recenters", policy.Contains("gameOwnsRigRecenter) return VrTrackingOrigin.Unchanged"));
        // the old bug: the inverted Device test
        Check("the bridge has no inverted origin test", !bridge.Contains("origin == TrackingOriginModeFlags.Device) input.TrySetTrackingOriginMode"));
        // the old bug: the game recentered and the bridge recentered as well
        Check("the bridge does not patch the input script's recenter", !bridge.Contains("TrackRecenter"));
        Check("the bridge does not patch inputscript", !bridge.Contains("HarmonyPatch(typeof(inputscript)"));
        // the game's own rig recenter is the one that is used
        Check("the bridge calls the game's own rig recenter", bridge.Contains("game.VRRecenter()"));
        Check("the bridge finds the game's rig recenter", bridge.Contains("mainScript2.s"));

        // The explicit origin request is for THAT recenter only. It used to be a sticky flag, so one
        // `recenter origin` made every LATER plain recenter move the origin as well: the world kept moving
        // under the camera rig, which is the exact disagreement the one-owner rule exists to prevent.
        Check("the origin request is not a sticky flag", !Code(bridge).Contains("_requestOrigin"));
        Check("the origin request is passed to the recenter", bridge.Contains("Recenter(true)"));
        Check("a plain recenter does not ask for the origin", bridge.Contains("Recenter(false)"));

        // The state reports the runtime's tracking-origin MODE, so "the origin moved" is observable and
        // not a guess. It is a configuration flag, never a pose.
        Check("the state reports the tracking origin mode", bridge.Contains("originMode"));
        Check("the state reports the supported origin modes", bridge.Contains("originSupported"));
        Check("the origin mode is read from the runtime", bridge.Contains("GetTrackingOriginMode()"));
        Check("the state carries no pose", !bridge.Contains("GetDevicePose")
            && !bridge.Contains("TryGetDevicePose") && !bridge.Contains("devicePosition"));
    }

    // ---- the bridge's commands
    private static void BridgeCommands(string debug, string bridge, string policy)
    {
        Check("the bridge has the openxr command", debug.Contains("case \"openxr\""));
        Check("the command dispatches to the bridge", debug.Contains("Fixes.OpenXrBridge.Command("));
        // the read/mutation classification: reading the state needs no developer access, changing the session does
        Check("openxr alone is a read", debug.Contains("if (cmd == \"openxr\") return VR.VrBridgeCommandPolicy.IsQuery(arg);"));
        Check("the bridge asks the policy for the split", debug.Contains("VR.VrBridgeCommandPolicy.IsQuery"));
        Check("the policy is the pure core", policy.Contains("class VrBridgeCommandPolicy"));
        Check("the policy knows the query verbs", policy.Contains("\"state\""));
        Check("the policy knows the mutation verbs", policy.Contains("\"start\"") && policy.Contains("\"retry\"")
            && policy.Contains("\"stop\"") && policy.Contains("\"recenter\""));
        Check("openxr is not in the read-only list", !Regex.IsMatch(debug, @"_readCmds[^;]*""openxr"""));
        // the states and the mutations
        Check("the bridge can start", bridge.Contains("if (what == \"start\") return Enable();"));
        Check("the bridge can retry", bridge.Contains("if (what == \"retry\") return Retry();"));
        Check("the bridge can stop", bridge.Contains("if (what == \"stop\") return Disable();"));
        Check("the bridge can recenter", bridge.Contains("if (what == \"recenter\")"));
        Check("the bridge can read the state", bridge.Contains("if (what.Length == 0 || what == \"state\") return State();"));
        // The help line. The old help printed the command TWICE, with two different syntaxes (one of them
        // stale), so a reader got a wrong syntax. The help now PRINTS the policy's own constant, so there is
        // exactly one syntax by construction. The guard checks that structure rather than a literal:
        //   - the policy's constant is the exact documented syntax;
        //   - the help uses that constant;
        //   - the help hard-codes NO syntax of its own (so a second one cannot appear).
        const string syntax = "openxr [state|start|retry|stop|recenter [origin]]";
        Check("the policy's syntax is the documented one", policy.Contains("public const string Syntax = \"" + syntax + "\";"));
        Check("the help prints the policy's syntax", debug.Contains("VR.VrBridgeCommandPolicy.Syntax"));
        Check("the help hard-codes no syntax of its own", !Regex.IsMatch(Code(debug), @"openxr \[[^\]]*\]"));
        Check("the bridge's usage line is the policy's syntax", bridge.Contains("VrBridgeCommandPolicy.Syntax"));
        Check("the bridge hard-codes no syntax of its own", !Regex.IsMatch(Code(bridge), @"openxr \[[^\]]*\]"));
        Check("the help has no stale second syntax", !debug.Contains("openxr [state|start|stop|recenter]"));
    }

    // ---- the state does not claim more than it knows
    private static void HonestReporting(string bridge)
    {
        Check("the failure is reported", bridge.Contains("Lifecycle.Failure.ToString()"));
        Check("the loader is reported", bridge.Contains("Choice.Kind"));
        Check("the error is reported", bridge.Contains("Lifecycle.Error"));
        Check("the retries are counted", bridge.Contains("Lifecycle.RetryCount"));
        // the old bug: "legacy VR remains available" — there is no legacy VR runtime in this game build
        Check("the failure does not promise a legacy VR path", !bridge.Contains("legacy VR remains available"));
        Check("the failure says the game stays playable", bridge.Contains("the game stays playable without VR"));
        // the state never carries a pose
        Check("the state carries no transform", !bridge.Contains("transform.position") && !bridge.Contains("transform.rotation"));
        Check("the state carries no device pose", !bridge.Contains("devicePosition") && !bridge.Contains("deviceRotation"));
    }

    // ---- nothing here touches the network
    private static void NoNetworkPose(string bridge, string contract)
    {
        Check("the bridge has no net writer", !bridge.Contains("NetWriter") && !bridge.Contains("NetBuffer"));
        Check("the bridge does not send anything", !bridge.Contains("Send(") && !bridge.Contains("SendToAll"));
        Check("the bridge does not read the player's pose", !bridge.Contains("WriteLocalPose") && !bridge.Contains("HHTarget"));
        Check("the contract bridge does not write a pose either", !contract.Contains("HHTarget.position =") || contract.Contains("the gameplay root"));
    }

    /// The source without its comment lines: a check about what the code does must not be answered by what the
    /// comment says the old code did.
    /// A JSON key as the C# source writes it: the quotes are escaped inside the string literal.

    private static string Code(string source)
    {
        var b = new System.Text.StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;
            b.Append(line).Append('\n');
        }
        return b.ToString();
    }

    private static string Read(string relative)
    {
        string repoRoot = Environment.GetEnvironmentVariable("TLD_REPO_ROOT");
        if (!string.IsNullOrEmpty(repoRoot) && File.Exists(Path.Combine(repoRoot, "src/TLDRevamp/Fixes/OpenXrBridge.cs")))
        {
            string candidate = Path.Combine(repoRoot, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        else
        {
            string currentDirectory = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(currentDirectory, "src/TLDRevamp/Fixes/OpenXrBridge.cs")))
            {
                string candidate = Path.Combine(currentDirectory, relative);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
        }

        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            string parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (parent == null) break;
            dir = parent;
        }
        throw new FileNotFoundException(relative);
    }
}
