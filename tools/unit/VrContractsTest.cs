using System;
using System.Reflection;
using TLDRevamp.VR;

static class VrContractsTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    static int Main()
    {
        SessionTransitions();
        IdempotentStartStop();
        FailureFallback();
        TrackingRecovery();
        HandActionHolds();
        RigPolicy();
        NoGameplayRootMotion();
        LocomotionSuppression();
        UiRayEvents();
        ReconnectSanitization();
        NoPoseSerialization();
        ContractsTogether();
        DiagnosticsShape();

        Console.WriteLine("VR contracts: " + checks + " checks passed");
        return 0;
    }

    // ---- the XR session states: off, initializing, active, suspended, failed
    static void SessionTransitions()
    {
        var s = new VrSessionMachine();
        Check(s.State == VrSessionState.Off, "a fresh session is off");
        Check(s.Decision().HandsActive == false && s.Decision().UiWorldSpace == false, "an off session has no hands and no world-space canvas");

        s.RequestStart();
        Check(s.State == VrSessionState.Initializing, "a start request initializes");
        Check(s.Decision().HandsActive == false, "the hands wait for the session to actually run");
        Check(s.Decision().UiWorldSpace == false, "the canvas waits for the session to actually run");

        s.ReportStarted();
        Check(s.State == VrSessionState.Active, "the runtime's start makes the session active");
        Check(s.Decision().HandsActive && s.Decision().UiWorldSpace && s.Decision().AllowHeadLook, "an active session has hands, a world-space canvas and head look");
        Check(s.Decision().UseLegacyVr == false, "an active session does not fall back to the legacy path");

        s.ReportSuspended();
        Check(s.State == VrSessionState.Suspended, "a suspend is its own state");
        Check(s.Decision().HandsActive == false, "a suspended session holds the hands back");
        Check(s.Decision().UiWorldSpace == true, "a suspended session keeps the world-space canvas");
        Check(s.Decision().AllowHeadLook == false, "a suspended session holds the head look back");

        s.ReportResumed();
        Check(s.State == VrSessionState.Active, "a resume goes back to active");

        s.RequestStop();
        Check(s.State == VrSessionState.Off, "a stop request goes off");
        Check(s.Decision().HandsActive == false && s.Decision().UiWorldSpace == false, "an off session has no hands and no world-space canvas again");
    }

    // ---- starting and stopping twice is one start and one stop
    static void IdempotentStartStop()
    {
        var s = new VrSessionMachine();
        s.RequestStart();
        var second = s.RequestStart();
        Check(!second.Changed, "a second start request changes nothing");
        Check(s.State == VrSessionState.Initializing, "a second start request leaves the state alone");
        s.ReportStarted();
        s.RequestStart();
        Check(s.StartCount == 1, "the session started once, however often VR was asked for");
        Check(s.State == VrSessionState.Active, "a start request while active does not knock the session down");

        var again = s.ReportStarted();
        Check(!again.Changed, "a repeated start report changes nothing");
        Check(s.StartCount == 1, "a repeated start report does not start a second time");

        s.RequestStop();
        var stopAgain = s.RequestStop();
        Check(!stopAgain.Changed, "a second stop request changes nothing");
        Check(s.StopCount == 1, "the session stopped once, however often flat play was asked for");
        Check(s.State == VrSessionState.Off, "a stop request while off leaves the state alone");
    }

    // ---- a runtime failure falls back to the legacy path and can be retried
    static void FailureFallback()
    {
        var s = new VrSessionMachine();
        s.RequestStart();
        s.ReportStarted();
        s.ReportFailure("the runtime refused", noRuntime: true);
        Check(s.State == VrSessionState.Failed, "a failure is its own state");
        Check(s.Reason == VrSessionReason.NoRuntime, "a missing runtime is told apart from a runtime error");
        Check(s.Decision().UseLegacyVr, "a failure falls back to the legacy VR path");
        Check(s.Decision().HandsActive == false, "a failure does not leave live hands");
        Check(s.Decision().UiWorldSpace == false, "a failure keeps the menu canvas flat");
        Check(s.Error == "the runtime refused", "the failure keeps its reason");

        s.Retry();
        Check(s.State == VrSessionState.Initializing, "a retry after a failure starts again");
        s.ReportStarted();
        Check(s.State == VrSessionState.Active, "the retried session runs");
        Check(s.FailureCount == 1, "the failure is counted once");

        var lost = new VrSessionMachine();
        lost.RequestStart();
        lost.ReportStarted();
        lost.ReportLost();
        Check(lost.State == VrSessionState.Failed, "a runtime that goes away is a failure");
        Check(lost.Reason == VrSessionReason.RuntimeLost, "a lost runtime is told apart from an error");
        Check(lost.Decision().UseLegacyVr, "a lost runtime falls back to the legacy path");
    }

    // ---- tracking loss and recovery
    static void TrackingRecovery()
    {
        var t = new VrTrackingMachine("head");
        Check(t.State == VrTrackingState.Untracked, "a source is untracked before it is ever seen");
        Check(t.Decision().UsePose == false, "an untracked source has no pose to use");

        t.ReportPresent();
        Check(t.State == VrTrackingState.Tracked, "a seen source is tracked");
        Check(t.Decision().UsePose, "a tracked source may be used");

        t.SetTime(10.0);
        t.ReportLost();
        Check(t.State == VrTrackingState.Lost, "a source that goes away is lost");
        Check(t.Decision().UsePose == false, "a lost source has no pose to use");
        Check(t.Decision().FreezePose, "a lost source freezes its last pose instead of snapping");
        Check(t.Decision().ReleaseInteractions, "a lost source releases what it held");
        Check(t.Decision().ShowHand == false, "a lost source is not drawn");
        Check(Math.Abs(t.SecondsSinceLoss - 0.0) < 1e-9, "a lost source has no time since the loss yet");

        t.SetTime(12.5);
        Check(Math.Abs(t.SecondsSinceLoss - 2.5) < 1e-9, "the time since the loss is measured from the caller's clock");

        var again = t.ReportLost();
        Check(!again.Changed, "a second loss of the same source changes nothing");
        Check(t.LossCount == 1, "the loss is counted once");

        t.ReportPresent();
        Check(t.State == VrTrackingState.Tracked, "a source that comes back is tracked again");
        Check(t.RecoveryCount == 1, "the recovery is counted");
        Check(t.Decision().ReleaseInteractions == false, "a recovered source may take its interactions again");

        // a source that was never there is not a loss
        var never = new VrTrackingMachine("never");
        never.ReportAbsent();
        Check(never.State == VrTrackingState.Untracked, "a source that was never seen stays untracked");
        Check(never.LossCount == 0, "a source that was never seen has no loss");
        Check(never.Decision().ReleaseInteractions, "an untracked source still releases what it held");

        // the head and the hands are tracked apart
        var set = new VrTrackingSet();
        set.SetTime(1.0);
        set.Head.ReportPresent();
        set.LeftHand.ReportPresent();
        set.RightHand.ReportPresent();
        set.RightHand.ReportLost();
        Check(set.Head.Tracked && set.LeftHand.Tracked, "a lost hand does not take the head or the other hand down");
        Check(!set.RightHand.Tracked, "the lost hand is lost");
        Check(set.BothHandsTracked == false, "both hands are not tracked while one is lost");
        Check(set.AnyHandTracked, "one hand is still tracked");
    }

    // ---- the hand's neutral-action-hold states
    static void HandActionHolds()
    {
        var h = new VrHandMachine("right");
        Check(h.Action == VrHandAction.Idle, "a hand starts idle");
        Check(h.Press(), "a press is accepted");
        Check(h.PressEdge, "a press is an edge");
        Check(!h.Press(), "a second press in the same state is not another press");
        Check(h.Presses == 1, "the press is counted once");

        h.EndFrame();
        Check(h.Action == VrHandAction.Holding, "a press becomes a hold on the next frame");
        Check(!h.PressEdge, "a hold is not an edge any more");
        Check(h.Holding, "a hold is a hold");

        h.Release();
        Check(h.ReleaseEdge, "a release is an edge");
        Check(h.Releases == 1, "the release is counted");
        h.EndFrame();
        Check(h.Action == VrHandAction.Idle, "a release returns the hand to idle");

        Check(!h.Release(), "a release without a press does nothing");
        Check(h.Releases == 1, "the stray release is not counted");

        // a hold that is cancelled releases the grab without a release the game can act on
        h.Press();
        h.EndFrame();
        Check(h.Cancel(), "a held action can be cancelled");
        Check(h.Action == VrHandAction.Cancelled, "a cancelled action is its own state");
        Check(h.Cancels == 1, "the cancel is counted");
        Check(!h.Holding, "a cancelled action does not hold");
        Check(!h.Cancel(), "a second cancel changes nothing");

        // the neutral pose
        h.SetNeutralPose(VrHandPose.Pointing);
        Check(h.Pose == VrHandPose.Pointing, "the neutral pose is the hand's own");
    }

    // ---- the rig policy
    static void RigPolicy()
    {
        var r = new VrRigPolicy();
        r.SessionActive = true;
        r.HeadTracked = true;
        var d = r.HeadDecision();
        Check(d.FromViewRoot, "an active, tracked head follows the view root");
        Check(d.FromGameplayRoot == false, "an active, tracked head does not come from the gameplay root");
        Check(d.AllowBodyMotion == false, "the gameplay root does not move");

        r.HeadTracked = false;
        d = r.HeadDecision();
        Check(d.FromViewRoot == false, "a head that is not tracked does not follow the view root");
        Check(d.FromGameplayRoot, "a head that is not tracked comes from the gameplay root");
        Check(d.AllowBodyMotion == false, "a head that is not tracked does not move the gameplay root");

        r.HeadTracked = true;
        r.Seated = true;
        d = r.HeadDecision();
        Check(d.FromGameplayRoot, "a seated player's head comes from the seat, not from the headset");
        Check(d.AllowBodyMotion == false, "a seated player does not move the gameplay root");
    }

    // ---- the gameplay root never moves because of the headset
    static void NoGameplayRootMotion()
    {
        var r = new VrRigPolicy();
        r.SessionActive = true;
        r.HeadTracked = true;
        r.Seated = false;

        // even when a caller explicitly asks for it, the contract refuses while the player is standing
        r.AllowGameplayMotion = true;
        Check(r.HeadDecision().AllowBodyMotion == false, "the gameplay root does not move for the headset");

        // and it refuses in every session state
        foreach (var active in new[] { false, true })
        {
            foreach (var tracked in new[] { false, true })
            {
                foreach (var seated in new[] { false, true })
                {
                    r.SessionActive = active; r.HeadTracked = tracked; r.Seated = seated;
                    Check(r.HeadDecision().AllowBodyMotion == false,
                        "the gameplay root never moves for the headset (active=" + active + " tracked=" + tracked + " seated=" + seated + ")");
                    Check(r.Decide(VrRigTarget.LeftHand).AllowBodyMotion == false, "the hand does not move the gameplay root");
                    Check(r.Decide(VrRigTarget.RightHand).AllowBodyMotion == false, "the hand does not move the gameplay root");
                }
            }
        }

        r.AllowGameplayMotion = false;
        r.SessionActive = true; r.HeadTracked = true; r.Seated = false;
        Check(r.PolicyReason == VrRigPolicy.LocalViewOnly, "the reason for the rule is the local view only rule");
    }

    // ---- a seated player does not walk
    static void LocomotionSuppression()
    {
        var p = new VrLocomotionPolicy();
        var intent = new VrMoveIntent { Forward = 1f, Right = 0.5f, Run = true };
        var outMove = p.Apply(intent);
        Check(outMove.Forward == 1f && outMove.Right == 0.5f && outMove.Run, "a standing player walks");

        p.Seated = true;
        outMove = p.Apply(intent);
        Check(outMove.Forward == 0f && outMove.Backward == 0f && outMove.Left == 0f && outMove.Right == 0f,
            "a seated player does not walk");
        Check(outMove.Run == false, "a seated player does not run");
        Check(outMove.Suppressed, "the seated rule says it took the move away");
        Check(p.SuppressedFrames == 1, "the suppression is counted");

        // a seated player with no stick is not a suppression
        p.Apply(new VrMoveIntent());
        Check(p.SuppressedFrames == 1, "a seated player with no stick raises no suppression");

        // a driver may not walk either
        var d = new VrLocomotionPolicy();
        d.Driving = true;
        var dm = d.Apply(intent);
        Check(dm.Forward == 0f && dm.Run == false, "a driver does not walk with the stick");

        // the look is not the body
        var look = VrLocomotionPolicy.LookOnly(intent);
        Check(look.Forward == 0f && look.Left == 0f, "the look alone moves nothing");
    }

    // ---- the UI ray: hit, focus, one activation, cancel
    static void UiRayEvents()
    {
        var u = new VrUiRayMachine("right");
        Check(u.Hit("button.a") == VrUiEvent.HoverChanged, "a hit is a focus change");
        Check(u.Focus == "button.a", "the focus is the control under the ray");
        Check(u.Hit("button.a") == VrUiEvent.None, "the same control again is not a focus change");
        Check(u.Hit("button.b") == VrUiEvent.HoverChanged, "another control is a focus change");
        Check(u.Focus == "button.b", "the focus moved");

        Check(u.Press() == VrUiEvent.Pressed, "a press opens");
        Check(u.Pressed == "button.b", "the press is on the focused control");
        Check(u.Press() == VrUiEvent.None, "a second press while one is open is ignored");

        Check(u.Activate() == VrUiEvent.Activated, "the press activates once");
        Check(u.Activate() == VrUiEvent.None, "a held press does not activate twice");
        Check(u.Activations == 1, "the activation is counted once");

        u.EndFrame();
        Check(u.Release() == VrUiEvent.None, "a release after the activation is not another event");
        Check(u.Pressed == null, "the press is closed");

        // a press that is released without an activation
        var u2 = new VrUiRayMachine("left");
        u2.Hit("button.c");
        u2.Press();
        Check(u2.Release() == VrUiEvent.Released, "a press that never activated is released");

        // a cancel releases the control without an activation
        var u3 = new VrUiRayMachine("left");
        u3.Hit("button.d");
        u3.Press();
        Check(u3.Cancel() == VrUiEvent.Cancelled, "a press can be cancelled");
        Check(u3.Pressed == null, "the cancelled press is closed");
        Check(u3.Activations == 0, "a cancelled press does not activate");
        Check(u3.Cancellations == 1, "the cancel is counted");

        // losing the focus while a press is open cancels it
        var u4 = new VrUiRayMachine("right");
        u4.Hit("button.e");
        u4.Press();
        Check(u4.FocusLost() == VrUiEvent.Cancelled, "losing the focus cancels the open press");
        Check(u4.Pressed == null, "the cancelled press is closed");

        // a press on nothing does nothing
        var u5 = new VrUiRayMachine("right");
        Check(u5.Press() == VrUiEvent.None, "a press with no control under the ray does nothing");
        Check(u5.Activations == 0, "a press with no control does not activate");
    }

    // ---- a reconnect clears the VR state
    static void ReconnectSanitization()
    {
        var session = new VrSessionMachine();
        var tracking = new VrTrackingSet();
        var left = new VrHandMachine("left");
        var right = new VrHandMachine("right");
        var leftUi = new VrUiRayMachine("left");
        var rightUi = new VrUiRayMachine("right");
        var loco = new VrLocomotionPolicy();
        var policy = new VrReconnectPolicy(session, tracking, left, right, leftUi, rightUi, loco);

        session.RequestStart();
        session.ReportStarted();
        tracking.SetTime(1.0);
        tracking.Head.ReportPresent();
        tracking.LeftHand.ReportPresent();
        tracking.RightHand.ReportPresent();
        right.Press();
        right.EndFrame();
        leftUi.Hit("button.a");
        leftUi.Press();
        loco.Seated = true;

        var state = new VrReconnectState { Seated = true, DuckToggle = true, RecenterPending = true, LocomotionLatched = true };
        var captured = policy.Capture(state);
        Check(captured.GrabbingsHeld, "the held grab is seen before the reconnect");
        Check(captured.UiPressOpen, "the open UI press is seen before the reconnect");

        policy.Sanitize(captured);

        Check(session.State == VrSessionState.Off, "the session is stopped by the reconnect");
        Check(!right.Holding, "the held grab does not survive the reconnect");
        Check(leftUi.Pressed == null, "the open UI press does not survive the reconnect");
        Check(!tracking.Head.Tracked && !tracking.LeftHand.Tracked && !tracking.RightHand.Tracked,
            "the tracking is released so the next frame re-acquires");
        Check(!loco.Seated, "the seated state is cleared");
        Check(!captured.Any, "the reconnect state is clean afterwards");
        Check(captured.DuckToggle == false && captured.RecenterPending == false && captured.LocomotionLatched == false,
            "the duck toggle, the pending recenter and the latched axis are cleared");

        // a fresh spawn starts from the same clean state
        var fresh = policy.Fresh(new VrReconnectState { Seated = true });
        Check(!fresh.Any, "a fresh spawn starts clean");
    }

    // ---- nothing in the contracts can carry a pose
    static void NoPoseSerialization()
    {
        // by construction: no field of any contract type may be a position, a rotation or a transform
        var contractTypes = new[]
        {
            typeof(VrSessionMachine), typeof(VrTrackingMachine), typeof(VrTrackingSet),
            typeof(VrHandMachine), typeof(VrRigPolicy), typeof(VrLocomotionPolicy),
            typeof(VrUiRayMachine), typeof(VrReconnectState), typeof(VrReconnectPolicy),
            typeof(VrContracts), typeof(VrMoveIntent), typeof(VrMoveAllowed),
            typeof(VrSessionDecision), typeof(VrTrackingDecision), typeof(VrRigDecision),
        };
        var banned = new[] { "Vector3", "Vector2", "Vector4", "Quaternion", "Matrix4x4", "Transform", "Pose", "Bounds", "Ray" };
        foreach (var t in contractTypes)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                var ft = f.FieldType;
                var name = ft.Name;
                bool banned1 = false;
                foreach (var b in banned) if (name == b) banned1 = true;
                Check(!banned1, t.Name + "." + f.Name + " is a pose type (" + ft.Name + ")");
            }
        }

        // and the rendered diagnostics hold no position-shaped number
        var c = new VrContracts();
        c.Session.RequestStart();
        c.Session.ReportStarted();
        c.Tracking.Head.ReportPresent();
        c.Tracking.LeftHand.ReportPresent();
        c.Tracking.RightHand.ReportPresent();
        var json = VrDiagnostics.Json(c);
        Check(json.Contains("\"session\""), "the diagnostics carry the session state");
        Check(json.Contains("\"rig\""), "the diagnostics carry the rig state");
        Check(!json.Contains("position") && !json.Contains("rotation") && !json.Contains("\"pose\":") && !json.Contains("transform"),
            "the diagnostics carry no spatial pose field");
        // no decimal number that could be a coordinate: the diagnostics hold only small counts
        Check(!HoldsDecimal(json), "the diagnostics hold no decimal number");
        // the guard itself is checked: a coordinate would be caught
        Check(HoldsDecimal("{\"x\":12.345}"), "the decimal guard catches a coordinate");
        Check(HoldsDecimal("{\"x\":-0.5}"), "the decimal guard catches a negative coordinate");
        Check(!HoldsDecimal("{\"x\":12}"), "the decimal guard leaves a plain count alone");
    }

    /// True when the text holds a decimal number, which is the shape a coordinate has and a count does not.
    /// Only the value after a colon is looked at: a key or a word never counts.
    static bool HoldsDecimal(string json)
    {
        foreach (var part in json.Split(','))
        {
            var trimmed = part.Trim().Trim('{', '}', '"');
            int colon = trimmed.LastIndexOf(':');
            if (colon < 0) continue;
            var value = trimmed.Substring(colon + 1).Trim().Trim('"', '}', ']');
            int dot = value.IndexOf('.');
            if (dot < 0) continue;
            var numPart = value.Substring(0, dot);
            if (numPart.StartsWith("-")) numPart = numPart.Substring(1);
            bool numeric = numPart.Length > 0;
            foreach (var ch in numPart) if (ch < '0' || ch > '9') numeric = false;
            if (numeric) return true;
        }
        return false;
    }

    // ---- the whole contract, as the bridge drives it
    static void ContractsTogether()
    {
        var c = new VrContracts();

        // a VR session that starts
        c.Session.RequestStart();
        c.Session.ReportStarted();
        c.Tick(0.0, false, false, true, true, true);
        Check(c.Session.IsVr, "the session runs");
        Check(c.Session.Decision().HandsActive, "the hands are live");
        Check(c.Tracking.Head.Tracked && c.Tracking.LeftHand.Tracked && c.Tracking.RightHand.Tracked, "everything is tracked");
        Check(c.Rig.HeadMayFollowCamera, "the head follows the camera while the player stands");

        // a seat: the head comes from the seat and the stick may not walk
        c.Tick(1.0, true, false, true, true, true);
        Check(c.Rig.Seated, "the rig knows the player is seated");
        Check(c.Rig.HeadMayFollowCamera == false, "a seated player's head does not follow the camera");
        var seated = c.Locomotion.Apply(new VrMoveIntent { Forward = 1f, Run = true });
        Check(seated.Forward == 0f && seated.Run == false, "a seated player does not walk");

        // the head is lost: the view holds, the body is untouched
        c.Tick(2.0, false, false, false, true, true);
        Check(c.Tracking.Head.State == VrTrackingState.Lost, "the head is lost");
        Check(c.Rig.HeadMayFollowCamera == false, "a lost head does not follow the camera");
        Check(c.Rig.HeadDecision().AllowBodyMotion == false, "a lost head does not move the gameplay root");

        // a hand is lost: its grab and its UI press go
        var ui = c.RightUi;
        ui.Hit("button.a");
        ui.Press();
        c.RightHand.Press();
        c.RightHand.EndFrame();
        Check(c.RightHand.Holding, "the right hand holds");
        c.Tick(3.0, false, false, true, true, false);
        Check(c.RightHand.Holding == false, "a lost hand does not keep its hold");
        Check(c.RightUi.Pressed == null, "a lost hand does not keep its UI press");
        Check(c.Tracking.LeftHand.Tracked, "the other hand is not taken down with it");

        // the head comes back
        c.Tick(4.0, false, false, true, true, true);
        Check(c.Tracking.Head.Tracked, "the head recovers");
        Check(c.Tracking.Head.RecoveryCount == 1, "the recovery is counted");

        // a session failure: the legacy path takes over
        c.Session.ReportFailure("the runtime refused");
        Check(c.Session.State == VrSessionState.Failed, "the session failed");
        Check(c.Session.Decision().UseLegacyVr, "the legacy path takes over");
        Check(c.Session.Decision().HandsActive == false, "the hands are off");
        Check(c.Session.Decision().UiWorldSpace == false, "the canvas is flat");
    }

    // ---- the diagnostics have the shape the bridge expects
    static void DiagnosticsShape()
    {
        var c = new VrContracts();
        c.Session.RequestStart();
        c.Session.ReportStarted();
        var json = VrDiagnostics.Json(c);
        Check(json.StartsWith("{") && json.EndsWith("}"), "the diagnostics are one JSON object");
        Check(json.Contains("\"state\":\"active\""), "the session state is in the diagnostics");
        Check(json.Contains("\"head\""), "the head tracking is in the diagnostics");
        Check(json.Contains("\"activations\""), "the UI activation count is in the diagnostics");
        Check(json.Contains("\"clean\""), "the reconnect state is in the diagnostics");
        Check(json.Contains("\"allowBodyMotion\":false"), "the rig's body-motion refusal is in the diagnostics");
    }
}
