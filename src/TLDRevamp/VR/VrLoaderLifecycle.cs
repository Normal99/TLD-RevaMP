using System;

namespace TLDRevamp.VR
{
    /// The state of the XR loader's life, and the explicit transitions between them.
    ///
    /// The old bridge drove the loader itself: it called `loader.Initialize()` and `loader.Start()` behind the XR
    /// manager's back, so the manager's `activeLoader` and its "initialization complete" flag never matched what was
    /// actually running. The game then called `manager.StopSubsystems()` on a manager that had never started
    /// anything, and the XR session was left running while the game believed VR was off.
    ///
    /// This machine is the lifecycle's owner. It is pure: it holds the state and answers what has to happen next.
    /// The runtime (src/TLDRevamp/Fixes/OpenXrBridge.cs) asks it and does exactly one thing per step, through the
    /// XR manager. Every transition is explicit, so a start can be retried and a failure never leaves the machine in
    /// a state it cannot leave.
    public enum VrLoaderState
    {
        Idle,       // nothing has been asked for; the game is flat
        Starting,   // a start was asked for and is in progress
        Running,    // the loader is initialized and the subsystems run
        Stopping,   // a stop was asked for and is in progress
        Failed      // the last start failed; the game stays flat and playable
    }

    /// Why a start failed. Every value is a fixed string: nothing the runtime said is copied verbatim.
    public enum VrLoaderFailure
    {
        None,
        NoManager,     // there is no XR manager in this game build
        NoLoader,      // the manager holds no OpenXR loader
        RefusedLoader, // the only loader there is one this install does not allow
        Initialize,    // the loader refused to initialize
        Start,         // the subsystems did not start
        Verify         // the loader started but the display or the input is not running
    }

    /// What the caller has to do next. Exactly one action, so the runtime cannot do two things at once and drift
    /// away from the manager again.
    public enum VrLoaderAction
    {
        None,             // nothing to do
        ChooseLoader,     // pick the loader (the policy decides which)
        ConfigureFeatures,// switch the controller profiles on
        InitializeLoader, // manager.InitializeLoaderSync()
        StartSubsystems,   // manager.StartSubsystems()
        Verify,           // look at the display and the input
        StopSubsystems,   // manager.StopSubsystems()
        DeinitializeLoader, // manager.DeinitializeLoader()
        Fail              // the start failed; the machine waits for an explicit retry
    }

    /// The lifecycle of one XR loader, as a pure state machine.
    public sealed class VrLoaderLifecycle
    {
        private VrLoaderState _state = VrLoaderState.Idle;
        private VrLoaderFailure _failure = VrLoaderFailure.None;
        private string _error = "";
        private int _startCount, _stopCount, _failureCount, _retryCount;
        private bool _started;

        public VrLoaderState State { get { return _state; } }
        public VrLoaderFailure Failure { get { return _failure; } }
        public string Error { get { return _error; } }
        public int StartCount { get { return _startCount; } }
        public int StopCount { get { return _stopCount; } }
        public int FailureCount { get { return _failureCount; } }
        /// How often a failed start was retried. A retry is always explicit: the count is how often it happened.
        public int RetryCount { get { return _retryCount; } }

        /// True while the loader is initialized and its subsystems run.
        public bool Running { get { return _state == VrLoaderState.Running; } }
        /// True while a start is in progress.
        public bool Starting { get { return _state == VrLoaderState.Starting; } }

        /// A start was asked for. Idempotent: asking twice while a start is in progress changes nothing.
        /// A failed start is not retried by this call: a retry is explicit (Retry), so a broken runtime cannot spin.
        public VrLoaderAction RequestStart()
        {
            if (_state == VrLoaderState.Running || _state == VrLoaderState.Starting) return VrLoaderAction.None;
            if (_state == VrLoaderState.Failed) return VrLoaderAction.Fail;   // an explicit retry is needed
            _state = VrLoaderState.Starting;
            _failure = VrLoaderFailure.None;
            _error = "";
            return VrLoaderAction.ChooseLoader;
        }

        /// A retry of a failed start. This is the only way a failed start happens again, and it is always explicit.
        public VrLoaderAction Retry()
        {
            if (_state != VrLoaderState.Failed) return VrLoaderAction.None;
            _retryCount++;
            _state = VrLoaderState.Starting;
            _failure = VrLoaderFailure.None;
            _error = "";
            return VrLoaderAction.ChooseLoader;
        }

        /// The loader was picked (or refused).
        public VrLoaderAction ReportLoaderChosen(bool found)
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            if (!found) return Fail(VrLoaderFailure.NoLoader, "no OpenXR loader in the XR manager");
            return VrLoaderAction.ConfigureFeatures;
        }

        /// The controller profiles were configured (before the loader is initialized: the loader reads the enabled
        /// features when it initializes, so a profile switched on afterwards is never used for that session).
        public VrLoaderAction ReportFeaturesConfigured()
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            return VrLoaderAction.InitializeLoader;
        }

        /// The XR manager already runs the session (the game started it through its own SetVR path). The session is
        /// adopted as it is: initializing it a second time is what the manager warns about.
        public VrLoaderAction ReportAdopted()
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            return VrLoaderAction.Verify;
        }

        /// The loader was initialized by the manager.
        public VrLoaderAction ReportInitialized()
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            return VrLoaderAction.StartSubsystems;
        }

        /// The manager's subsystems were started.
        public VrLoaderAction ReportStarted()
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            return VrLoaderAction.Verify;
        }

        /// The loader is initialized and the subsystems run.
        public VrLoaderAction ReportVerified(bool displayRunning, bool inputRunning)
        {
            if (_state != VrLoaderState.Starting) return VrLoaderAction.None;
            if (!displayRunning && !inputRunning)
                return Fail(VrLoaderFailure.Verify, "the loader started but neither the display nor the input runs");
            _state = VrLoaderState.Running;
            _failure = VrLoaderFailure.None;
            _error = "";
            _startCount++;
            _started = true;
            return VrLoaderAction.None;
        }

        /// The game started the session itself (its own SetVR path drives the manager). The machine adopts it: the
        /// session really runs, and the machine's state has to say so.
        public VrLoaderAction ReportExternallyStarted()
        {
            if (_state == VrLoaderState.Running) return VrLoaderAction.None;
            if (_state == VrLoaderState.Failed) return VrLoaderAction.None;   // a failed start stays failed until it is retried
            _state = VrLoaderState.Running;
            _failure = VrLoaderFailure.None;
            _error = "";
            _startCount++;
            _started = true;
            return VrLoaderAction.None;
        }

        /// The game stopped the session itself. The machine follows: the session is off.
        public VrLoaderAction ReportExternallyStopped()
        {
            if (_state == VrLoaderState.Idle) return VrLoaderAction.None;
            if (_state == VrLoaderState.Failed) return VrLoaderAction.None;
            _state = VrLoaderState.Idle;
            _failure = VrLoaderFailure.None;
            _error = "";
            if (_started) { _stopCount++; _started = false; }
            return VrLoaderAction.None;
        }

        /// A stop was asked for. Idempotent: asking twice stops once.
        ///
        /// A FAILED start is not stopped: nothing is running, and the failure has to survive, because an
        /// explicit retry needs it. A stop that turned a failure into a stop would leave the machine in
        /// `Stopping`, where a retry is refused, and the failure would be lost.
        ///
        /// A stop that is already IN PROGRESS is finished by the same request: the caller asked for the
        /// session to be off, so the pending stop is carried out rather than left half done.
        public VrLoaderAction RequestStop()
        {
            if (_state == VrLoaderState.Idle || _state == VrLoaderState.Failed) return VrLoaderAction.None;
            if (_state == VrLoaderState.Stopping) return VrLoaderAction.StopSubsystems;
            _state = VrLoaderState.Stopping;
            return VrLoaderAction.StopSubsystems;
        }

        /// The manager's subsystems were stopped.
        public VrLoaderAction ReportSubsystemsStopped()
        {
            if (_state != VrLoaderState.Stopping) return VrLoaderAction.None;
            return VrLoaderAction.DeinitializeLoader;
        }

        /// The manager deinitialized the loader. The session is off and the machine is ready for a fresh start.
        public VrLoaderAction ReportDeinitialized()
        {
            if (_state != VrLoaderState.Stopping) return VrLoaderAction.None;
            _state = VrLoaderState.Idle;
            _failure = VrLoaderFailure.None;
            _error = "";
            if (_started) { _stopCount++; _started = false; }
            return VrLoaderAction.None;
        }

        /// The start failed. The machine goes to `Failed`: the game stays flat and playable, and a new attempt
        /// needs an explicit retry. The failure is never sticky in a way that blocks a retry.
        public VrLoaderAction Fail(VrLoaderFailure failure, string error)
        {
            _state = VrLoaderState.Failed;
            _failure = failure;
            _error = error ?? "";
            _failureCount++;
            _started = false;
            return VrLoaderAction.Fail;
        }

        /// The runtime went away (a cable, a runtime crash): the same as a failure, but the reason says so.
        public VrLoaderAction ReportLost()
        {
            if (_state == VrLoaderState.Idle || _state == VrLoaderState.Failed) return VrLoaderAction.None;
            return Fail(VrLoaderFailure.Start, "the XR runtime stopped");
        }

        /// Puts the machine back into its initial state: idle, no failure, and no counts.
        ///
        /// The offline test drives the production bridge from a known state, and the machine's fields are
        /// private, so this is the seam that makes that possible. The game never calls it.
        public void Reset()
        {
            _state = VrLoaderState.Idle;
            _failure = VrLoaderFailure.None;
            _error = "";
            _startCount = 0; _stopCount = 0; _failureCount = 0; _retryCount = 0;
            _started = false;
        }

        /// A short, stable description for the diagnostics bridge. Never contains a device or a pose.
        public string Describe()
        {
            return _state.ToString().ToLowerInvariant() + " failure=" + _failure.ToString().ToLowerInvariant() +
                   " err=" + _error + " starts=" + _startCount + " stops=" + _stopCount + " fails=" + _failureCount +
                   " retries=" + _retryCount;
        }
    }
}
