using System;

namespace TLDRevamp.VR
{
    /// The lifecycle of the game's XR session, as issue #41 needs it. Pure and hardware independent: the caller feeds
    /// in what the runtime reported and reads back the state and the reason it changed. The game's own switch is
    /// mainscript.SetVR(bool) -> SetXR(bool) -> XRGeneralSettings.Instance.Manager (InitializeLoaderSync /
    /// StartSubsystems, and StopSubsystems / DeinitializeLoader for the way back); mainscript.IsVR() only reads
    /// settingsscript.s.S.BVR, so the flag and the runtime can disagree. This machine is that disagreement's owner.
    public enum VrSessionState
    {
        Off,            // no XR session; the game plays flat
        Initializing,   // the loader is being initialized / started
        Active,         // the session runs
        Suspended,      // the runtime put the session on hold (headset off the head, system menu, focus lost)
        Failed          // the runtime refused; the legacy path stays available
    }

    /// Why the session left Active. Kept apart from the state so a caller can tell a fallback from a suspend.
    public enum VrSessionReason
    {
        None,
        Requested,      // the user or the game asked for it
        RuntimeLost,    // the runtime reported the session as gone
        RuntimeError,   // the runtime reported an error
        NoRuntime,      // no XR runtime / no loader could be installed
        Suspended       // the runtime asked for a suspend
    }

    /// The decisions a caller has to make from the session state. Everything here is a pure function of the inputs:
    /// no Unity, no runtime, no headset. The VR-specific code asks this machine and acts on the answer.
    public struct VrSessionDecision
    {
        public bool StartLoader;      // the XR loader has to be initialized and started
        public bool StopLoader;       // the XR loader has to be stopped and deinitialized
        public bool UseLegacyVr;      // the game's own VR path stays in charge
        public bool HandsActive;      // the hand visuals and their input may be live
        public bool UiWorldSpace;     // the menu canvas belongs in world space
        public bool AllowHeadLook;    // the player may look around with the headset
        public bool Changed;

        public override string ToString() =>
            "start=" + StartLoader + " stop=" + StopLoader + " legacy=" + UseLegacyVr + " hands=" + HandsActive +
            " worldui=" + UiWorldSpace + " look=" + AllowHeadLook + " changed=" + Changed;
    }

    /// The XR session state machine of issue #41.
    ///
    /// Observed game order (see docs/VR-REWRITE.md):
    ///   mainscript.SetVR(true)  -> SetXR(false)            (the loader is torn down first)
    ///                       -> settingsscript.s.S.BVR = true
    ///                       -> SetXR(true)               (InitializeLoaderSync + StartSubsystems)
    ///                       -> vrHandsScript.s.Init()    (hands on or off)
    ///                       -> vrusables[i].UpdVR()      (per-usable VR trigger setup)
    ///                       -> mainScript2.s.UpdVRCanv() (the menu canvas becomes a world-space canvas)
    /// The game has no state machine here: a failed InitializeLoaderSync leaves IsVR() true, the hands hidden and
    /// the canvas in world space in front of a player who is not in VR. This machine owns that decision instead.
    public sealed class VrSessionMachine
    {
        private VrSessionState _state = VrSessionState.Off;
        private VrSessionReason _reason = VrSessionReason.None;
        private string _error = "";
        private int _startCount, _stopCount, _failureCount;

        public VrSessionState State { get { return _state; } }
        public VrSessionReason Reason { get { return _reason; } }
        public string Error { get { return _error; } }
        public int StartCount { get { return _startCount; } }
        public int StopCount { get { return _stopCount; } }
        public int FailureCount { get { return _failureCount; } }

        /// True while the game should behave as if it were in VR: the session runs and nothing failed.
        public bool IsVr => _state == VrSessionState.Active;

        /// True while the game is in VR mode at all, whatever the runtime does. The game's own IsVR() reads only
        /// settingsscript.s.S.BVR; this is the same flag the contract keeps honest.
        public bool VrMode => _state != VrSessionState.Off;

        /// The user asked for VR. Remembered so a runtime failure can fall back without forgetting the wish.
        public bool Requested { get; private set; }

        /// The user asked for VR. Idempotent: asking twice is one start, and the machine does not change state twice.
        public VrSessionDecision RequestStart()
        {
            Requested = true;
            if (_state == VrSessionState.Active || _state == VrSessionState.Initializing)
                return Decision(false);   // already on: the loader is not started twice
            _state = VrSessionState.Initializing;
            _reason = VrSessionReason.Requested;
            _error = "";
            return Decision(true);
        }

        /// The user asked for flat play. Idempotent: asking twice is one stop.
        public VrSessionDecision RequestStop()
        {
            Requested = false;
            if (_state == VrSessionState.Off)
                return Decision(false);   // already off: nothing to tear down
            var wasActive = _state == VrSessionState.Active;
            _state = VrSessionState.Off;
            _reason = VrSessionReason.Requested;
            _error = "";
            if (wasActive) _stopCount++;
            return Decision(false);
        }

        /// The loader initialized and the subsystems started.
        public VrSessionDecision ReportStarted()
        {
            if (_state == VrSessionState.Active)
                return Decision(false);   // already running: a repeated report changes nothing
            if (_state != VrSessionState.Initializing)
                return Decision(false);   // a start that was never asked for is ignored
            _state = VrSessionState.Active;
            _reason = VrSessionReason.None;
            _error = "";
            _startCount++;
            return Decision(false);
        }

        /// The runtime suspended the session (headset off, system menu, focus lost). The session is still ours, so
        /// the hands and the world-space canvas stay as they are; only the head look and the input are held back.
        public VrSessionDecision ReportSuspended()
        {
            if (_state != VrSessionState.Active && _state != VrSessionState.Initializing)
                return Decision(false);
            _state = VrSessionState.Suspended;
            _reason = VrSessionReason.Suspended;
            return Decision(false);
        }

        /// The runtime resumed a suspended session.
        public VrSessionDecision ReportResumed()
        {
            if (_state != VrSessionState.Suspended)
                return Decision(false);
            _state = VrSessionState.Active;
            _reason = VrSessionReason.None;
            return Decision(false);
        }

        /// The runtime failed. The legacy VR path takes over: the game stays playable, without the XR runtime.
        public VrSessionDecision ReportFailure(string error, bool noRuntime = false)
        {
            if (_state == VrSessionState.Off && !Requested)
                return Decision(false);   // nothing to fail
            _state = VrSessionState.Failed;
            _reason = noRuntime ? VrSessionReason.NoRuntime : VrSessionReason.RuntimeError;
            _error = error ?? "";
            _failureCount++;
            return Decision(false);
        }

        /// The runtime went away without an error (a cable, a runtime crash). Treated as a failure with a fallback.
        public VrSessionDecision ReportLost()
        {
            if (_state == VrSessionState.Off || _state == VrSessionState.Failed)
                return Decision(false);
            _state = VrSessionState.Failed;
            _reason = VrSessionReason.RuntimeLost;
            _error = "the XR runtime stopped";
            _failureCount++;
            return Decision(false);
        }

        /// The game's own teardown finished: the session is off. A separate report from RequestStop, because the
        /// game stops the loader itself and the machine must not tear it down twice.
        public VrSessionDecision ReportStopAfterGame()
        {
            if (_state == VrSessionState.Off)
                return Decision(false);
            _state = VrSessionState.Off;
            _reason = VrSessionReason.Requested;
            if (_stopCount == 0) _stopCount++;
            return Decision(false);
        }

        /// The game's own legacy VR path took over: the session is not ours, but VR still happens.
        ///
        /// This is the case where the OpenXR bridge is disabled or gave up: the game runs its own XR setup and the
        /// player is in VR, but nothing here owns that session. The state says so honestly ("the legacy path is in
        /// charge") instead of pretending the session is off, and the decision keeps the hands and the canvas as the
        /// game's own VR mode asks for them.
        public VrSessionDecision ReportLegacyPath()
        {
            if (_state == VrSessionState.Failed || _state == VrSessionState.Off)
                return Decision(false);
            _state = VrSessionState.Active;
            _reason = VrSessionReason.Requested;
            _error = "";
            _startCount++;
            return Decision(false);
        }

        /// A fresh session after a failure: the user asked for VR again.
        public VrSessionDecision Retry()
        {
            if (_state != VrSessionState.Failed && _state != VrSessionState.Off)
                return Decision(false);
            return RequestStart();
        }

        /// The decision the VR-specific code acts on. It is a pure function of the state: the same state always gives
        /// the same decision, so a caller that polls it cannot drift from the machine.
        public VrSessionDecision Decision()
        {
            switch (_state)
            {
                case VrSessionState.Active:
                    return new VrSessionDecision
                    {
                        HandsActive = true,
                        UiWorldSpace = true,
                        AllowHeadLook = true,
                        UseLegacyVr = false,
                    };
                case VrSessionState.Initializing:
                    // the hands and the canvas wait for the session: the game has no VR world yet
                    return new VrSessionDecision { UseLegacyVr = false };
                case VrSessionState.Suspended:
                    // the session is ours but the runtime is on hold: the world-space canvas stays, the hands freeze
                    return new VrSessionDecision { HandsActive = false, UiWorldSpace = true, AllowHeadLook = false };
                case VrSessionState.Failed:
                    // the legacy path takes over: the game's own VR setup runs, the canvas stays flat until it says otherwise
                    return new VrSessionDecision { UseLegacyVr = true, HandsActive = false, UiWorldSpace = false, AllowHeadLook = false };
                default:
                    return new VrSessionDecision { HandsActive = false, UiWorldSpace = false, AllowHeadLook = false };
            }
        }

        private VrSessionDecision Decision(bool changed)
        {
            var d = Decision();
            d.Changed = changed;
            return d;
        }

        /// A short, stable description for the diagnostics bridge. Never contains a device or a pose.
        public string Describe()
        {
            return _state.ToString().ToLowerInvariant() + ":" + _reason.ToString().ToLowerInvariant() +
                   " err=" + _error + " starts=" + _startCount + " stops=" + _stopCount + " fails=" + _failureCount;
        }
    }
}
