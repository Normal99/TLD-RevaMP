// The BEHAVIOUR of the XR bridge (issue #41), driven against the production code.
//
// The other XR guards (OpenXrBridgeTest) read the source, because "which API is called, and in which order"
// is a property of the code. This test goes one step further: it COMPILES the production bridge
// (src/TLDRevamp/Fixes/OpenXrBridge.cs) against faithful Unity/OpenXR stubs and DRIVES it. So the properties
// it checks are properties of the running code, not of a string:
//
//   - a stop really stops: the manager's StopSubsystems and DeinitializeLoader are called, in that order;
//   - the lifecycle and the REPORTED state always agree;
//   - a Disable() from every state leaves the machine in a state it can leave;
//   - the controller profiles are configured BEFORE the loader is initialized (the loader records which
//     profiles were enabled when it initialized);
//   - the recenter moves the rig ONCE and the runtime's origin only when it was asked for.
//
// A substring cannot lie about any of this, which is the point: the guards that were text matches were
// defeated by exactly the defects they claimed to protect against.
using System;
using System.Collections.Generic;
using System.Text.Json;
using TLDRevamp;
using TLDRevamp.Fixes;
using TLDRevamp.VR;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

static class OpenXrBridgeBehaviourTest
{
    private static int _fails;
    private static int _checks;
    private static void Check(string name, bool ok)
    {
        _checks++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _fails++;
    }
    private static void CheckEq(string name, object got, object want)
    {
        Check(name + " (got \"" + got + "\", want \"" + want + "\")", Equals(got, want));
    }

    private static int Main(string[] args)
    {
        StopReallyStops();
        StopWithTheManagerAlreadyGone();
        DisableFromEveryState();
        RetryAfterFailedDisable();
        ProfilesBeforeInitialize();
        ProfilesBeforeTheGamesOwnInitialize();
        NoAutonomousStart();
        RecenterMovesTheRigOnce();
        OriginOnlyWhenAsked();
        OriginIsReported();
        CommandSurface();
        Console.WriteLine(_fails == 0
            ? "OpenXR bridge behaviour: all " + _checks + " checks passed"
            : "OpenXR bridge behaviour: " + _fails + " of " + _checks + " checks failed");
        return _fails == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the harness

    /// A fresh world: a manager with one plain OpenXR loader, an OpenXR settings instance with the seven
    /// controller profiles, and the bridge's own state cleared.
    private static void World(bool withLoader = true, bool noPreInitOnly = false)
    {
        var manager = new XRManagerSettings();
        if (withLoader)
            manager.activeLoaders.Add(noPreInitOnly ? (XRLoader)new OpenXRLoaderNoPreInit() : new OpenXRLoader());
        XRGeneralSettings.Instance = new XRGeneralSettings { Manager = manager };

        var settings = new OpenXRSettings();
        foreach (var type in new[]
        {
            typeof(MetaQuestTouchProControllerProfile), typeof(OculusTouchControllerProfile),
            typeof(ValveIndexControllerProfile), typeof(HTCViveControllerProfile),
            typeof(HPReverbG2ControllerProfile), typeof(MicrosoftMotionControllerProfile),
            typeof(KHRSimpleControllerProfile),
        })
            settings.Features[type] = new OpenXRFeature();
        OpenXRSettings.Instance = settings;

        UnityEngine.Debug.Warnings.Clear();
        TLDRevamp.Plugin.Log.Warnings.Clear();
        InputDevices.Devices.Clear();
        mainScript2.s = null;
        mainScript2.RigRecenters = 0;
        UnityEngine.Time.unscaledTime = 1000f;

        // the bridge's own state, reset through the production seam
        OpenXrBridge.ResetForTest();
        OpenXrBridge.Enabled = true;
        OpenXrBridge.AllowNoPreInit = false;
    }

    private static JsonElement State()
    {
        var text = OpenXrBridge.State();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// Whether the bridge REFUSED a command, told apart the way the tool tells it apart: a refusal carries
    /// an "error" and no "state". The state's own JSON has an "error" field too (the reason of a failed
    /// start), so the presence of an error alone is not a refusal.
    private static bool Refused(string answer)
    {
        using var doc = JsonDocument.Parse(answer);
        var root = doc.RootElement;
        return root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String && error.GetString() != ""
            && !root.TryGetProperty("state", out _);
    }

    /// The state the bridge reports, as the machine's own state.
    private static string ReportedState() { return State().GetProperty("state").GetString(); }

    /// The state the lifecycle is really in.
    private static VrLoaderState LifecycleState() { return OpenXrBridge.Lifecycle.State; }

    // ---------------------------------------------------------------- a stop really stops

    /// The defect this whole test exists for: `Disable()` used to throw away the action the lifecycle
    /// returned, and `Drive()` used to always start at `ChooseLoader`, so the stop steps were never run at
    /// all: the manager's StopSubsystems and DeinitializeLoader were never called.
    private static void StopReallyStops()
    {
        World();
        OpenXrBridge.Enable();
        Check("the session runs before the stop", OpenXrBridge.Lifecycle.Running);

        var manager = XRGeneralSettings.Instance.Manager;
        manager.Calls.Clear();
        OpenXrBridge.Disable();

        Check("the manager's subsystems were stopped", manager.Calls.Contains("StopSubsystems"));
        Check("the manager's loader was deinitialized", manager.Calls.Contains("DeinitializeLoader"));
        Check("the stop came before the deinitialize",
            manager.Calls.IndexOf("StopSubsystems") < manager.Calls.IndexOf("DeinitializeLoader"));
        CheckEq("the machine is idle after the stop", LifecycleState(), VrLoaderState.Idle);
        CheckEq("the reported state is idle after the stop", ReportedState(), "idle");
        Check("the manager is no longer initialized", !manager.isInitializationComplete);
        CheckEq("the stop is counted", OpenXrBridge.Lifecycle.StopCount, 1);

        // and the reported state and the machine agree
        Check("the machine and the reported state agree after a stop",
            ReportedState() == LifecycleState().ToString().ToLowerInvariant());
    }

    /// The other half of the defect: the manager is gone (the game tore the session down itself) by the time
    /// the bridge is asked to stop. The old code left the machine in `Stopping` with the stop unaccounted for.
    private static void StopWithTheManagerAlreadyGone()
    {
        World();
        OpenXrBridge.Enable();
        var manager = XRGeneralSettings.Instance.Manager;
        manager.SimulateGameToreItDown();
        manager.Calls.Clear();

        OpenXrBridge.Disable();

        CheckEq("the machine is idle when the manager is already gone", LifecycleState(), VrLoaderState.Idle);
        CheckEq("the reported state is idle when the manager is already gone", ReportedState(), "idle");
        Check("the stop is counted when the manager is already gone", OpenXrBridge.Lifecycle.StopCount == 1);
        Check("the machine and the reported state agree when the manager is already gone",
            ReportedState() == LifecycleState().ToString().ToLowerInvariant());
        // the manager is not stopped through: it is already down, and the real manager warns when it is
        Check("a manager that is already down is not stopped through", !manager.Calls.Contains("StopSubsystems"));
    }

    // ---------------------------------------------------------------- Disable() from every state

    /// A Disable() from every state must leave the lifecycle and the reported state in AGREEMENT, and must
    /// leave the machine in a state it can leave (a later start or retry must work).
    private static void DisableFromEveryState()
    {
        // --- idle
        World();
        OpenXrBridge.Disable();
        Check("a disable from idle leaves the machine idle", LifecycleState() == VrLoaderState.Idle);
        Check("a disable from idle agrees with the reported state", Agree());
        Check("a disable from idle does not count a stop", OpenXrBridge.Lifecycle.StopCount == 0);

        // --- starting (a start is in progress, the loader has not been initialized yet)
        World();
        OpenXrBridge.Lifecycle.RequestStart();          // the machine is now `starting`
        OpenXrBridge.Disable();
        Check("a disable from starting leaves the machine idle", LifecycleState() == VrLoaderState.Idle);
        Check("a disable from starting agrees with the reported state", Agree());
        Check("a disable from starting can be followed by a start", CanStartAgain());

        // --- running
        World();
        OpenXrBridge.Enable();
        OpenXrBridge.Disable();
        Check("a disable from running leaves the machine idle", LifecycleState() == VrLoaderState.Idle);
        Check("a disable from running agrees with the reported state", Agree());
        Check("a disable from running can be followed by a start", CanStartAgain());

        // --- stopping (a stop is already in progress: the pending stop is carried out)
        World();
        OpenXrBridge.Enable();
        OpenXrBridge.Lifecycle.RequestStop();            // the machine is now `stopping`
        OpenXrBridge.Disable();
        Check("a disable from stopping leaves the machine idle", LifecycleState() == VrLoaderState.Idle);
        Check("a disable from stopping agrees with the reported state", Agree());
        Check("a disable from stopping can be followed by a start", CanStartAgain());

        // --- failed (a failed start stays failed: it needs an explicit retry, and the failure survives)
        World();
        OpenXrBridge.Lifecycle.RequestStart();
        OpenXrBridge.Lifecycle.Fail(VrLoaderFailure.Initialize, "the loader refused");
        OpenXrBridge.Disable();
        Check("a disable from failed leaves the machine failed", LifecycleState() == VrLoaderState.Failed);
        Check("a disable from failed agrees with the reported state", Agree());
        Check("a disable from failed keeps the failure", OpenXrBridge.Lifecycle.Failure == VrLoaderFailure.Initialize);
    }

    /// The machine and the reported state must always be the same thing.
    private static bool Agree()
    {
        return ReportedState() == LifecycleState().ToString().ToLowerInvariant();
    }

    /// A machine that can be started again: the start is accepted and the machine leaves `starting`.
    private static bool CanStartAgain()
    {
        var action = OpenXrBridge.Lifecycle.RequestStart();
        if (action == VrLoaderAction.Fail) return false;    // a failed start would refuse the start
        return action != VrLoaderAction.None;
    }

    /// A retry must still work after a Disable() from `failed`: the failure is the failure, and the retry is
    /// the explicit way out of it.
    private static void RetryAfterFailedDisable()
    {
        World();
        OpenXrBridge.Lifecycle.RequestStart();
        OpenXrBridge.Lifecycle.Fail(VrLoaderFailure.Initialize, "the loader refused");
        OpenXrBridge.Disable();

        Check("the machine is failed after the disable", LifecycleState() == VrLoaderState.Failed);
        Check("the failure survives the disable", OpenXrBridge.Lifecycle.Failure == VrLoaderFailure.Initialize);

        var retry = OpenXrBridge.Retry();
        Check("a retry after a disable from failed is accepted", OpenXrBridge.Lifecycle.State == VrLoaderState.Running);
        Check("the retry cleared the failure", OpenXrBridge.Lifecycle.Failure == VrLoaderFailure.None);
        Check("the retry agrees with the reported state", Agree());
        Check("the retried session really runs", State().GetProperty("running").GetBoolean());
        Check("the retried session is reported as running", ReportedState() == "running");
    }

    // ---------------------------------------------------------------- the profiles come before the initialize

    /// The OpenXR loader reads the enabled features when it initializes. The stub's loader records the
    /// profiles that were enabled AT THAT MOMENT, so the order is observed, not assumed.
    private static void ProfilesBeforeInitialize()
    {
        World();
        OpenXrBridge.Enable();

        var loader = XRGeneralSettings.Instance.Manager.activeLoader as OpenXRLoader;
        Check("the plain OpenXR loader was chosen", loader != null);
        if (loader == null) return;

        Check("the loader was initialized", loader.Initialized);
        Check("the controller profiles were enabled when the loader was initialized",
            loader.FeaturesEnabledAtInitialize.Count == 7);
        Check("the Quest profile was enabled when the loader was initialized",
            loader.FeaturesEnabledAtInitialize.Contains("UnityEngine.XR.OpenXR.Features.Interactions.MetaQuestTouchProControllerProfile"));
        Check("the Valve Index profile was enabled when the loader was initialized",
            loader.FeaturesEnabledAtInitialize.Contains("UnityEngine.XR.OpenXR.Features.Interactions.ValveIndexControllerProfile"));

        // the order of the manager's calls: the loader is initialized, then the subsystems start
        var calls = XRGeneralSettings.Instance.Manager.Calls;
        Check("the loader was initialized through the manager", calls.Contains("InitializeLoaderSync"));
        Check("the subsystems were started through the manager", calls.Contains("StartSubsystems"));
        Check("the initialize came before the start",
            calls.IndexOf("InitializeLoaderSync") < calls.IndexOf("StartSubsystems"));
    }

    /// The game's own path (its SetVR) initializes the loader too, so the profiles have to be on before that.
    /// The bridge's Harmony prefix does that: the stub's prefix is called directly.
    private static void ProfilesBeforeTheGamesOwnInitialize()
    {
        World();
        OpenXrBridge.ConfigureProfilesForGame();
        var loader = new OpenXRLoader();
        loader.Initialize();
        Check("the profiles are enabled for the game's own path", loader.FeaturesEnabledAtInitialize.Count == 7);
    }

    // ---------------------------------------------------------------- nothing starts a session on its own

    /// The frame tick only reads: it must never start a session behind the player's back. The old code started
    /// the loader from the per-frame tick whenever the BVR preference was on, and its failure was sticky, so a
    /// headset plugged in later never worked.
    private static void NoAutonomousStart()
    {
        World();
        // the tick is called many times, as the frame loop would
        for (int i = 0; i < 20; i++)
        {
            UnityEngine.Time.unscaledTime += 1f;
            OpenXrBridge.Tick();
        }
        Check("the tick did not start a session", !OpenXrBridge.Lifecycle.Running);
        CheckEq("the machine is still idle after the ticks", LifecycleState(), VrLoaderState.Idle);
        Check("the tick did not initialize the loader", !XRGeneralSettings.Instance.Manager.isInitializationComplete);
        Check("the tick did not call the manager's initialize",
            !XRGeneralSettings.Instance.Manager.Calls.Contains("InitializeLoaderSync"));
        Check("the tick did not count a start", OpenXrBridge.Lifecycle.StartCount == 0);
    }

    // ---------------------------------------------------------------- the recenter has one owner

    /// The game owns the rig recenter. The runtime's tracking origin must not move with it: doing both on
    /// one key press moves the rig once and the world once, and the two disagree.
    private static void RecenterMovesTheRigOnce()
    {
        World();
        mainScript2.s = new mainScript2();          // the game has a rig
        OpenXrBridge.Enable();

        OpenXrBridge.Recenter();
        Check("the game's rig recentered once", mainScript2.RigRecenters == 1);
        Check("the recenter was counted once", OpenXrBridge.RecenterCount == 1);

        // a second explicit recenter moves the rig once more, and still not the origin
        OpenXrBridge.Recenter();
        Check("the game's rig recentered once more", mainScript2.RigRecenters == 2);
        Check("each recenter moved the rig once", OpenXrBridge.RecenterCount == 2);
    }

    /// The runtime's tracking origin moves only when it is asked for explicitly.
    private static void OriginOnlyWhenAsked()
    {
        World();
        mainScript2.s = null;                        // no rig: the runtime owns the recenter
        OpenXrBridge.Enable();
        var input = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
        Check("the input subsystem is there", input != null);
        if (input == null) return;

        OpenXrBridge.Recenter();
        Check("the runtime's recenter was called", input.TryRecenterCalls == 1);
        Check("the origin was not moved without being asked for", input.SetOriginCalls == 0);
    }

    /// The tracking origin is REPORTED, so the hardware check can see it.
    ///
    /// The mode is a configuration flag (the device, the floor, a tracking reference), never a pose: the
    /// state carries no coordinate and no device name, so nothing about the player's position leaves the
    /// machine. Without it, "the origin moved" could only be guessed at from the recenter counter.
    private static void OriginIsReported()
    {
        World();
        mainScript2.s = null;                        // no rig: the runtime owns the recenter
        OpenXrBridge.Enable();
        var input = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
        Check("the input subsystem is there", input != null);
        if (input == null) return;

        // the state reports the mode the runtime is set up around
        CheckEq("the state reports the tracking origin mode", State().GetProperty("originMode").GetString(), "device");
        Check("the state reports the supported origin modes",
              State().GetProperty("originSupported").GetString().Contains("floor"));

        // an explicit `recenter origin` moves it, and the state says so afterwards
        var moved = OpenXrBridge.Command("recenter origin");
        Check("the explicit origin request moved the origin", input.SetOriginCalls == 1);
        Check("the origin mode is reported after the move",
              moved.Contains("\"originMode\":\"device\""));

        // a plain recenter must NOT move it: the origin stays where it was
        var before = input.SetOriginCalls;
        OpenXrBridge.Command("recenter");
        Check("a plain recenter did not move the origin", input.SetOriginCalls == before);

        // the state never carries a pose: the origin mode is a flag, not a position
        var state = OpenXrBridge.State();
        foreach (var forbidden in new[] { "position", "rotation", "pose", "headPosition" })
            Check("the state carries no '" + forbidden + "'", !state.ToLowerInvariant().Contains(forbidden.ToLowerInvariant()));
    }

    // ---------------------------------------------------------------- the command surface

    /// The bridge's commands: which are reads, which are mutations, and what the usage line says.
    private static void CommandSurface()
    {
        World();

        // the reads
        Check("the bare command is a query", VrBridgeCommandPolicy.IsQuery(""));
        Check("the state command is a query", VrBridgeCommandPolicy.IsQuery("state"));
        CheckEq("the bare command's verb", VrBridgeCommandPolicy.Classify(""), VrBridgeVerb.Query);
        CheckEq("the state command's verb", VrBridgeCommandPolicy.Classify("state"), VrBridgeVerb.Query);

        // the mutations
        foreach (var verb in new[] { "start", "retry", "stop", "recenter" })
            Check(verb + " is a mutation", VrBridgeCommandPolicy.IsMutation(verb));
        Check("recenter origin is a mutation", VrBridgeCommandPolicy.IsMutation("recenter origin"));
        Check("the query verbs are not mutations", !VrBridgeCommandPolicy.IsMutation("state"));

        // an unknown form is neither: it is refused, and it is not a read
        Check("an unknown form is not a query", !VrBridgeCommandPolicy.IsQuery("nonsense"));
        Check("an unknown form is not a mutation", !VrBridgeCommandPolicy.IsMutation("nonsense"));

        // the usage line names every documented form
        var syntax = VrBridgeCommandPolicy.Syntax;
        foreach (var part in new[] { "state", "start", "retry", "stop", "recenter" })
            Check("the usage line names " + part, syntax.Contains(part));

        // the bridge refuses an unknown form with the usage line
        var unknown = OpenXrBridge.Command("nonsense");
        Check("an unknown form is refused", unknown.Contains("\"error\""));
        Check("the refusal carries the usage line", unknown.Contains(syntax));

        // the bare command and `state` really read the state
        var bare = OpenXrBridge.Command("");
        var state = OpenXrBridge.Command("state");
        Check("the bare command answers with the state", bare.Contains("\"state\""));
        Check("the state command answers with the state", state.Contains("\"state\""));

        // every documented form is dispatched and answers with the state (never with a refusal).
        // The state's own JSON carries an "error" field (the reason of a failed start), so a refusal is
        // told apart the way the tool tells it apart: a refusal has an "error" and no "state".
        foreach (var form in new[] { "", "state", "start", "retry", "stop", "recenter", "recenter origin" })
        {
            var answer = OpenXrBridge.Command(form);
            Check("the form '" + form + "' is accepted", !Refused(answer));
            Check("the form '" + form + "' answers with the state", answer.Contains("\"state\""));
        }

        // a form the bridge does not know is REFUSED, with the usage line
        foreach (var bad in new[] { "nonsense", "start now", "recenter sideways", "origin", "recenter origin origin" })
        {
            var refusal = OpenXrBridge.Command(bad);
            Check("the form '" + bad + "' is refused", Refused(refusal));
            Check("the refusal of '" + bad + "' carries the usage line", refusal.Contains(VrBridgeCommandPolicy.Syntax));
        }

        // the argument is trimmed and case insensitive
        Check("the argument is trimmed", !Refused(OpenXrBridge.Command("  state  ")));
        Check("the argument is case insensitive", !Refused(OpenXrBridge.Command("STATE")));
    }
}
