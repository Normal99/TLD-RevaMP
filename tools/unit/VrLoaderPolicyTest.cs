// The XR loader's rules without a game: which loader is picked, in which order the start happens, what a failure
// leaves behind, and who owns the recenter. No Unity, no runtime, no headset.
using System;
using System.Collections.Generic;
using TLDRevamp.VR;

static class VrLoaderPolicyTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }
    private static void CheckEq(string name, string got, string want) => Check(name + " (got \"" + got + "\")", got == want);

    private static int Main(string[] args)
    {
        LoaderKinds();
        LoaderChoice();
        FeatureOrder();
        StartOrder();
        Lifecycle();
        Failures();
        Retry();
        ExternalStart();
        Recenter();
        BridgeCommands();
        return _fails == 0 ? 0 : 1;
    }

    // ---- the kind of a loader, from its name
    private static void LoaderKinds()
    {
        Check("the plain OpenXR loader is known", VrLoaderPolicy.KindOf(VrLoaderPolicy.OpenXrName) == VrLoaderKind.OpenXr);
        Check("the no-pre-init loader is known", VrLoaderPolicy.KindOf(VrLoaderPolicy.OpenXrNoPreInitName) == VrLoaderKind.OpenXrNoPreInit);
        Check("an unknown loader is not OpenXR", VrLoaderPolicy.KindOf("UnityEngine.XR.OpenVR.OpenVRLoader") == VrLoaderKind.None);
        Check("a null name is not OpenXR", VrLoaderPolicy.KindOf(null) == VrLoaderKind.None);
        Check("an empty name is not OpenXR", VrLoaderPolicy.KindOf("") == VrLoaderKind.None);
    }

    // ---- which loader the session uses: by the policy, never by the list's order
    private static void LoaderChoice()
    {
        var plain = VrLoaderPolicy.OpenXrName;
        var noPre = VrLoaderPolicy.OpenXrNoPreInitName;
        var other = "UnityEngine.XR.OpenVR.OpenVRLoader";

        // the plain loader wins in either order
        var a = VrLoaderPolicy.Choose(new List<string> { plain, noPre }, true);
        var b = VrLoaderPolicy.Choose(new List<string> { noPre, plain }, true);
        Check("the plain loader wins when it is first", a.Kind == VrLoaderKind.OpenXr && a.Usable);
        Check("the plain loader wins when it is second", b.Kind == VrLoaderKind.OpenXr && b.Usable);
        Check("the choice does not depend on the list's order", a.Index != b.Index && a.Kind == b.Kind);

        // the no-pre-init loader only when it is the only one, and only when it is allowed
        var only = VrLoaderPolicy.Choose(new List<string> { noPre }, true);
        Check("the no-pre-init loader is used when it is the only one", only.Kind == VrLoaderKind.OpenXrNoPreInit && only.Usable);
        var refused = VrLoaderPolicy.Choose(new List<string> { noPre }, false);
        Check("the no-pre-init loader is refused when it is not allowed", refused.Kind == VrLoaderKind.OpenXrNoPreInit && !refused.Usable);
        Check("the refusal says why", refused.Reason.Length > 0);

        // a foreign loader is never used
        var foreign = VrLoaderPolicy.Choose(new List<string> { other }, true);
        Check("a foreign loader is refused", foreign.Kind == VrLoaderKind.None && !foreign.Usable);
        Check("the refusal names the reason", foreign.Reason.Length > 0);

        // an empty list
        var none = VrLoaderPolicy.Choose(new List<string>(), true);
        Check("an empty list is refused", none.Kind == VrLoaderKind.None && !none.Usable);
        Check("a null list is refused", !VrLoaderPolicy.Choose(null, true).Usable);

        // the chosen loader's name is reported
        Check("the chosen loader's name is reported", a.Name == plain);
        Check("the chosen loader's index is reported", a.Index == 0);
    }

    // ---- the controller profiles: a fixed set, in a fixed order
    private static void FeatureOrder()
    {
        Check("the profiles are known", VrLoaderPolicy.ControllerProfiles.Length >= 7);
        Check("the Quest Touch Pro profile is there", VrLoaderPolicy.IsControllerProfile("UnityEngine.XR.OpenXR.Features.Interactions.MetaQuestTouchProControllerProfile"));
        Check("the Valve Index profile is there", VrLoaderPolicy.IsControllerProfile("UnityEngine.XR.OpenXR.Features.Interactions.ValveIndexControllerProfile"));
        Check("the KHR simple profile is there", VrLoaderPolicy.IsControllerProfile("UnityEngine.XR.OpenXR.Features.Interactions.KHRSimpleControllerProfile"));
        Check("a foreign profile is not one of them", !VrLoaderPolicy.IsControllerProfile("UnityEngine.XR.OpenXR.Features.Interactions.EyeGazeInteraction"));
        Check("a null name is not one of them", !VrLoaderPolicy.IsControllerProfile(null));
        // the order is fixed: the same list every time
        var again = VrLoaderPolicy.ControllerProfiles;
        bool same = again.Length == VrLoaderPolicy.ControllerProfiles.Length;
        for (int i = 0; same && i < again.Length; i++) same = again[i] == VrLoaderPolicy.ControllerProfiles[i];
        Check("the profile order is stable", same);
        // no duplicates
        var seen = new HashSet<string>();
        bool unique = true;
        for (int i = 0; i < VrLoaderPolicy.ControllerProfiles.Length; i++) unique &= seen.Add(VrLoaderPolicy.ControllerProfiles[i]);
        Check("the profile list has no duplicates", unique);
    }

    // ---- the start's order: the features are configured BEFORE the loader is initialized
    private static void StartOrder()
    {
        var steps = VrLoaderPolicy.StartSteps();
        Check("the start has steps", steps.Length == 5);
        int choose = -1, features = -1, init = -1, start = -1, verify = -1;
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i] == VrLoaderPolicy.VrStartStep.ChooseLoader) choose = i;
            if (steps[i] == VrLoaderPolicy.VrStartStep.ConfigureFeatures) features = i;
            if (steps[i] == VrLoaderPolicy.VrStartStep.Initialize) init = i;
            if (steps[i] == VrLoaderPolicy.VrStartStep.StartSubsystems) start = i;
            if (steps[i] == VrLoaderPolicy.VrStartStep.Verify) verify = i;
        }
        Check("the loader is chosen first", choose >= 0 && choose < features);
        Check("the features are configured before the loader is initialized", features >= 0 && features < init);
        Check("the loader is initialized before the subsystems start", init >= 0 && init < start);
        Check("the session is verified last", start >= 0 && start < verify);

        var stop = VrLoaderPolicy.StopSteps();
        Check("the stop has steps", stop.Length == 3);
        Check("the subsystems are stopped before the loader is deinitialized", stop[0] == VrLoaderPolicy.VrStopStep.StopSubsystems && stop[1] == VrLoaderPolicy.VrStopStep.DeinitializeLoader);
    }

    // ---- the lifecycle's transitions
    private static void Lifecycle()
    {
        var m = new VrLoaderLifecycle();
        Check("a fresh machine is idle", m.State == VrLoaderState.Idle);
        Check("a fresh machine is not running", !m.Running);
        Check("a start asks for the loader", m.RequestStart() == VrLoaderAction.ChooseLoader);
        Check("a start puts the machine in starting", m.State == VrLoaderState.Starting);
        Check("a start again does nothing", m.RequestStart() == VrLoaderAction.None);
        Check("a second start does not change the state", m.State == VrLoaderState.Starting);
        Check("a chosen loader asks for the features", m.ReportLoaderChosen(true) == VrLoaderAction.ConfigureFeatures);
        Check("configured features ask for the initialize", m.ReportFeaturesConfigured() == VrLoaderAction.InitializeLoader);
        Check("an initialized loader asks for the subsystems", m.ReportInitialized() == VrLoaderAction.StartSubsystems);
        Check("started subsystems ask for the verification", m.ReportStarted() == VrLoaderAction.Verify);
        Check("a running session is verified", m.ReportVerified(true, true) == VrLoaderAction.None);
        Check("the machine runs", m.Running && m.State == VrLoaderState.Running);
        Check("the start is counted", m.StartCount == 1);
        Check("there is no failure", m.Failure == VrLoaderFailure.None);

        // a start while it runs does nothing
        Check("a start while it runs does nothing", m.RequestStart() == VrLoaderAction.None);
        Check("the start is not counted twice", m.StartCount == 1);

        // the way back
        Check("a stop asks for the subsystems to stop", m.RequestStop() == VrLoaderAction.StopSubsystems);
        Check("stopped subsystems ask for the deinitialize", m.ReportSubsystemsStopped() == VrLoaderAction.DeinitializeLoader);
        Check("a deinitialized loader leaves the machine idle", m.ReportDeinitialized() == VrLoaderAction.None);
        Check("the machine is idle", m.State == VrLoaderState.Idle);
        Check("the stop is counted", m.StopCount == 1);
        Check("a stop again does nothing", m.RequestStop() == VrLoaderAction.None);

        // a fresh start after a stop works
        Check("a start after a stop asks for the loader", m.RequestStart() == VrLoaderAction.ChooseLoader);
        m.ReportLoaderChosen(true); m.ReportFeaturesConfigured(); m.ReportInitialized(); m.ReportStarted();
        Check("a second session runs", m.ReportVerified(true, true) == VrLoaderAction.None && m.Running);
        Check("both starts are counted", m.StartCount == 2);
    }

    // ---- what a failure leaves behind
    private static void Failures()
    {
        var m = new VrLoaderLifecycle();
        m.RequestStart();
        Check("a failure stops the start", m.Fail(VrLoaderFailure.Initialize, "the loader refused") == VrLoaderAction.Fail);
        Check("the machine failed", m.State == VrLoaderState.Failed);
        Check("the failure is named", m.Failure == VrLoaderFailure.Initialize);
        Check("the error is kept", m.Error == "the loader refused");
        Check("the failure is counted", m.FailureCount == 1);
        Check("a failed machine does not run", !m.Running);

        // a failed start is not retried by asking for a start again: that is what "retry" is for
        Check("a start after a failure does nothing", m.RequestStart() == VrLoaderAction.Fail);
        Check("the machine stays failed", m.State == VrLoaderState.Failed);

        // the display may be down while the input runs: that is a session (a headless one), not a failure
        var headless = new VrLoaderLifecycle();
        headless.RequestStart(); headless.ReportLoaderChosen(true); headless.ReportFeaturesConfigured();
        headless.ReportInitialized(); headless.ReportStarted();
        Check("a headless session is still a session", headless.ReportVerified(false, true) == VrLoaderAction.None);
        Check("the headless machine runs", headless.Running);

        // nothing running at all is a failure
        var empty = new VrLoaderLifecycle();
        empty.RequestStart(); empty.ReportLoaderChosen(true); empty.ReportFeaturesConfigured();
        empty.ReportInitialized(); empty.ReportStarted();
        Check("a session with neither display nor input fails", empty.ReportVerified(false, false) == VrLoaderAction.Fail);
        Check("the failure is the verification", empty.Failure == VrLoaderFailure.Verify);
        Check("the empty machine does not run", !empty.Running);

        // the reason of a lost runtime
        var lost = new VrLoaderLifecycle();
        lost.RequestStart(); lost.ReportLoaderChosen(true); lost.ReportFeaturesConfigured();
        lost.ReportInitialized(); lost.ReportStarted(); lost.ReportVerified(true, true);
        Check("a lost runtime fails", lost.ReportLost() == VrLoaderAction.Fail);
        Check("the loss is named", lost.Failure == VrLoaderFailure.Start);
        Check("the lost machine does not run", !lost.Running);
    }

    // ---- a retry is explicit
    private static void Retry()
    {
        var m = new VrLoaderLifecycle();
        m.RequestStart();
        m.Fail(VrLoaderFailure.Start, "the runtime refused");
        Check("a retry of a failed start asks for the loader", m.Retry() == VrLoaderAction.ChooseLoader);
        Check("the retry is counted", m.RetryCount == 1);
        Check("the machine starts again", m.State == VrLoaderState.Starting);
        Check("the failure is cleared", m.Failure == VrLoaderFailure.None);
        m.ReportLoaderChosen(true); m.ReportFeaturesConfigured(); m.ReportInitialized(); m.ReportStarted();
        Check("the retried session runs", m.ReportVerified(true, true) == VrLoaderAction.None && m.Running);
        Check("the start is counted", m.StartCount == 1);

        // a retry of a machine that is not failed does nothing
        Check("a retry of a running machine does nothing", m.Retry() == VrLoaderAction.None);
        Check("the retry count did not move", m.RetryCount == 1);

        // a fresh machine has nothing to retry
        var fresh = new VrLoaderLifecycle();
        Check("a fresh machine has nothing to retry", fresh.Retry() == VrLoaderAction.None);
    }

    // ---- the game may start the session itself: the machine follows it
    private static void ExternalStart()
    {
        var m = new VrLoaderLifecycle();
        Check("a fresh machine is idle", m.State == VrLoaderState.Idle);
        // the game drove the manager: the session really runs
        Check("an externally started session is adopted", m.ReportExternallyStarted() == VrLoaderAction.None);
        Check("the machine runs", m.Running && m.State == VrLoaderState.Running);
        Check("the start is counted", m.StartCount == 1);
        Check("an adopted session is not adopted twice", m.ReportExternallyStarted() == VrLoaderAction.None);
        Check("the start is not counted twice", m.StartCount == 1);

        // the game stopped it: the machine follows
        Check("an externally stopped session is followed", m.ReportExternallyStopped() == VrLoaderAction.None);
        Check("the machine is idle again", m.State == VrLoaderState.Idle);
        Check("the stop is counted", m.StopCount == 1);
        Check("a second stop does nothing", m.ReportExternallyStopped() == VrLoaderAction.None);
        Check("the stop is not counted twice", m.StopCount == 1);

        // a failed start stays failed: an external observation does not paper over it
        var failed = new VrLoaderLifecycle();
        failed.RequestStart();
        failed.Fail(VrLoaderFailure.Initialize, "the loader refused");
        Check("a failed start is not adopted", failed.ReportExternallyStarted() == VrLoaderAction.None);
        Check("the machine stays failed", failed.State == VrLoaderState.Failed);
        Check("a failed start is not stopped from the outside", failed.ReportExternallyStopped() == VrLoaderAction.None);
        Check("the machine is still failed", failed.State == VrLoaderState.Failed);
    }

    // ---- the bridge's command surface: which forms read and which change the session
    private static void BridgeCommands()
    {
        // the reads: the bare command and the documented `state`
        Check("the bare command is a query", VrBridgeCommandPolicy.Classify("") == VrBridgeVerb.Query);
        Check("the bare command is a read", VrBridgeCommandPolicy.IsQuery(""));
        Check("the state command is a query", VrBridgeCommandPolicy.Classify("state") == VrBridgeVerb.Query);
        Check("the state command is a read", VrBridgeCommandPolicy.IsQuery("state"));
        Check("the state command is case insensitive", VrBridgeCommandPolicy.IsQuery("STATE"));
        Check("the state command tolerates spaces", VrBridgeCommandPolicy.IsQuery("  state  "));

        // the mutations: every verb that changes the session
        foreach (var verb in new[] { "start", "retry", "stop", "recenter" })
        {
            Check(verb + " is a mutation", VrBridgeCommandPolicy.Classify(verb) == VrBridgeVerb.Mutation);
            Check(verb + " changes the session", VrBridgeCommandPolicy.IsMutation(verb));
            Check(verb + " is not a read", !VrBridgeCommandPolicy.IsQuery(verb));
        }

        // `recenter origin` moves the runtime's tracking origin: it is a mutation, not a read
        Check("recenter origin is a mutation", VrBridgeCommandPolicy.Classify("recenter origin") == VrBridgeVerb.Mutation);
        Check("recenter origin is not a read", !VrBridgeCommandPolicy.IsQuery("recenter origin"));
        Check("recenter origin is case insensitive", VrBridgeCommandPolicy.IsMutation("RECENTER ORIGIN"));

        // an unknown form is neither: it is refused, and a refusal is not a read
        Check("an unknown form is unknown", VrBridgeCommandPolicy.Classify("nonsense") == VrBridgeVerb.Unknown);
        Check("an unknown form is not a read", !VrBridgeCommandPolicy.IsQuery("nonsense"));
        Check("an unknown form is not a mutation", !VrBridgeCommandPolicy.IsMutation("nonsense"));
        Check("an empty query verb list would break the rule", VrBridgeCommandPolicy.QueryVerbs.Length > 0);
        Check("an empty mutation verb list would break the rule", VrBridgeCommandPolicy.MutationVerbs.Length > 0);

        // the syntax the help prints names every documented form
        foreach (var part in new[] { "state", "start", "retry", "stop", "recenter" })
            Check("the syntax names " + part, VrBridgeCommandPolicy.Syntax.Contains(part));
        Check("the syntax is the documented one",
            VrBridgeCommandPolicy.Syntax == "openxr [state|start|retry|stop|recenter [origin]]");
    }

    // ---- who owns the recenter and the tracking origin
    private static void Recenter()
    {
        // the game owns its own rig recenter: the runtime's origin is not moved as well
        Check("the game owns the recenter", VrTrackingPolicy.RecenterOwner(true, true) == VrRecenterOwner.GameRig);
        Check("the runtime owns the recenter only when the game does not", VrTrackingPolicy.RecenterOwner(false, true) == VrRecenterOwner.Runtime);
        Check("nobody owns the recenter when nobody asked", VrTrackingPolicy.RecenterOwner(false, false) == VrRecenterOwner.None);
        Check("the game owns the recenter even when the runtime asks", VrTrackingPolicy.RecenterOwner(true, false) == VrRecenterOwner.GameRig);

        // the tracking origin: the game's rig recenter moves the camera rig, so the runtime's origin stays
        Check("the origin is left alone by default", VrTrackingPolicy.OriginFor(false, true, true, true) == VrTrackingOrigin.Unchanged);
        Check("the origin is left alone when the game recenters", VrTrackingPolicy.OriginFor(true, true, true, true) == VrTrackingOrigin.Unchanged);
        Check("the origin may be the device when the game does not recenter", VrTrackingPolicy.OriginFor(true, false, true, false) == VrTrackingOrigin.Device);
        Check("the origin may be the floor when the device is not supported", VrTrackingPolicy.OriginFor(true, false, false, true) == VrTrackingOrigin.Floor);
        Check("the origin stays when neither is supported", VrTrackingPolicy.OriginFor(true, false, false, false) == VrTrackingOrigin.Unchanged);
    }
}
