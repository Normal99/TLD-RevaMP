using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TLDRevamp.VR;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace TLDRevamp.Fixes
{
    /// The OpenXR boundary of issue #41.
    ///
    /// What the game ships (read out of the game's own files):
    ///   - `globalgamemanagers.assets` lists two loaders in the XR General Settings: `OpenXRLoader` and
    ///     `OpenXRLoaderNoPreInit`, and all seven controller interaction profiles.
    ///   - the managed folder has `Unity.XR.OpenXR.dll` and no other XR runtime's assembly at all: there is no
    ///     legacy VR runtime to fall back to. The "fallback" when OpenXR cannot start is non-VR gameplay.
    ///   - `mainscript.SetVR(bool)` is the game's only VR entry: it sets `settingsscript.s.S.BVR`, calls
    ///     `SetXR(bool)` (which is `XRGeneralSettings.Instance.Manager.InitializeLoaderSync()` +
    ///     `StartSubsystems()`, and `StopSubsystems()` + `DeinitializeLoader()` for the way back) and then turns the
    ///     hands, the usables and the menu canvas on.
    ///
    /// How this file differs from the first wave, and why (each point is a defect the review found in it):
    ///   1. THE XR MANAGER OWNS THE LIFECYCLE. The old file called `loader.Initialize()` and `loader.Start()`
    ///      itself, so `XRManagerSettings.activeLoader` stayed null and its "initialization complete" flag stayed
    ///      false: the game's own `SetXR(false)` then hit "StopSubsystems without an initialized manager" and the XR
    ///      session was left running while the game believed VR was off. Here every step goes through the manager
    ///      (`InitializeLoaderSync`, `StartSubsystems`, `StopSubsystems`, `DeinitializeLoader`), so the manager and
    ///      the loader always agree.
    ///   2. THE LOADER IS CHOSEN BY A POLICY, NOT BY THE LIST'S ORDER. The old file took the first OpenXR loader
    ///      in the manager's list, which is `OpenXRLoader` or `OpenXRLoaderNoPreInit` depending on an order nobody
    ///      promised. The plain loader wins now; the no-pre-init one is a deliberate fallback (VrLoaderPolicy).
    ///   3. THE CONTROLLER PROFILES ARE CONFIGURED BEFORE THE LOADER IS INITIALIZED. The OpenXR loader reads the
    ///      enabled features when it initializes, so the old file's "configure after start" could never enable a
    ///      profile for that session.
    ///   4. THERE IS NO AUTONOMOUS START. The old file's per-frame tick started the loader behind the player's back
    ///      whenever the `BVR` preference was on, and its failure was sticky, so a headset plugged in later never
    ///      worked. There is no per-frame start here any more: the lifecycle is explicit, and a failed start needs an
    ///      explicit retry.
    ///   5. THE RECENTER HAS ONE OWNER. The game already recenters its camera rig (`mainScript2.VRRecenter`, from
    ///      `mainScript2.LateUpdate`). The old file also called `XRInputSubsystem.TryRecenter()`, which moves the
    ///      runtime's tracking origin: the rig and the world then disagreed, and the disagreement compounded on the
    ///      next press. The game owns the recenter; the runtime's origin is only touched when it is asked for
    ///      explicitly and the game does not recenter itself (VrTrackingPolicy).
    ///
    /// Nothing here reads or writes a headset pose, and nothing here touches the network.
    public static class OpenXrBridge
    {
        /// The lifecycle's owner. Pure: it holds the state and says what has to happen next.
        internal static readonly VrLoaderLifecycle Lifecycle = new VrLoaderLifecycle();
        /// The loader the session uses, and why (the policy's answer).
        internal static VrLoaderChoice Choice;

        /// Whether the bridge is allowed to start a session at all (a config switch).
        internal static bool Enabled = true;
        /// Whether the no-pre-init loader may be used when it is the only one there.
        internal static bool AllowNoPreInit;
        /// The last error, for the diagnostics and the log. Never a device name and never a pose.
        internal static string LastError = "";
        /// Whether the last start failed. Kept for the bridge's answer and the seams.
        internal static bool Failed { get { return Lifecycle.Failure != VrLoaderFailure.None; } }
        /// Whether a session is running now.
        internal static bool Started { get { return Lifecycle.Running; } }
        /// Whether the loader is initialized and the subsystems run (the display and the input really are up).
        internal static bool Initialized { get { return Lifecycle.Running; } }
        /// The runtime's name and API version, for the diagnostics.
        internal static string Runtime = "unknown";
        internal static string ApiVersion = "unknown";
        internal static long RecenterCount, StartCount, StopCount;

        /// The XR manager, when the game has one.
        private static XRManagerSettings _manager;
        private static XRLoader _loader;
        private static bool _featuresConfigured;

        /// Puts the bridge back into its initial state: a fresh lifecycle, no manager, no loader, no
        /// configured features and no pending origin request.
        ///
        /// The lifecycle is a readonly field, so it cannot be swapped from the outside; this is the seam that
        /// lets the offline test drive the production bridge from a known state. It is not used by the game.
        internal static void ResetForTest()
        {
            Lifecycle.Reset();
            Choice = new VrLoaderChoice();
            LastError = "";
            Runtime = "unknown";
            ApiVersion = "unknown";
            RecenterCount = 0;
            StartCount = 0;
            StopCount = 0;
            _manager = null;
            _loader = null;
            _featuresConfigured = false;
        }

        /// The bridge's entry point: `openxr [state|start|retry|stop|recenter]`.
        ///
        /// `state` is a pure read (the state), the rest change the session. Which is which is decided by
        /// VrBridgeCommandPolicy (the pure core), so the debug bridge's read/mutation split and this bridge's
        /// dispatch are the same rule and cannot drift apart.
        internal static string Command(string arg)
        {
            var what = (arg ?? "").Trim().ToLowerInvariant();
            if (what.Length == 0 || what == "state") return State();
            if (what == "start") return Enable();
            if (what == "retry") return Retry();
            if (what == "stop") return Disable();
            if (what == "recenter") { Recenter(false); return State(); }
            // `recenter origin`: the runtime's tracking origin as well. Explicit, because the game's own rig
            // recenter moves the camera rig and the two have to stay in step.
            //
            // The request is for THIS recenter only. It used to be a sticky flag, so one `recenter origin`
            // made every LATER plain recenter move the origin as well: the world kept moving under the rig
            // and the two drifted apart again, which is the exact failure the one-owner rule exists to stop.
            if (what == "recenter origin") { Recenter(true); return State(); }
            // The usage line is the policy's, so the help and the bridge can never disagree about it.
            return "{\"error\":\"usage: " + VrBridgeCommandPolicy.Syntax + "\"}";
        }

        /// A start was asked for. The steps are driven one at a time, so a failure stops where it happened and the
        /// next attempt starts from the beginning.
        internal static string Enable()
        {
            if (!Enabled) return Fail(VrLoaderFailure.NoLoader, "OpenXR is disabled in this install's config");
            var action = Lifecycle.RequestStart();
            if (action == VrLoaderAction.None) return State();   // already running or already starting
            if (action == VrLoaderAction.Fail) return State();    // a failed start needs an explicit retry
            return Drive(action);
        }

        /// A retry of a failed start. This is the explicit way back: nothing retries on its own.
        internal static string Retry()
        {
            if (!Enabled) return Fail(VrLoaderFailure.NoLoader, "OpenXR is disabled in this install's config");
            var action = Lifecycle.Retry();
            if (action == VrLoaderAction.None) return State();
            return Drive(action);
        }

        /// A stop was asked for. Only what the manager actually started is stopped: the manager warns when
        /// StopSubsystems is called on a manager that never initialized anything, and the game's own SetVR(false)
        /// has already torn the session down by then.
        internal static string Disable()
        {
            var manager = _manager ?? Manager();
            var action = Lifecycle.RequestStop();
            if (manager == null || !manager.isInitializationComplete)
            {
                // The manager is gone (the game tore the session down itself), so there is nothing left to stop
                // through it. The stop is still ACCOUNTED FOR: `RequestStop()` moves the machine, and its result
                // used to be dropped here, which left the machine in `Stopping` with the stop never counted and
                // the reported state wrong ("stopping" for a session that is off).
                //
                // The manager is already down, so the machine cannot be driven through it: the stop is completed
                // as "the runtime is already gone" and the state is read from the machine afterwards.
                if (action != VrLoaderAction.None) Lifecycle.ReportExternallyStopped();
                _loader = null;
                _featuresConfigured = false;
                return State();
            }
            if (action == VrLoaderAction.None) return State();
            return Drive(action);
        }

        /// The recenter.
        ///
        /// The game owns the rig recenter: `mainScript2.VRRecenter()` moves the camera rig onto the head-lean
        /// transform, and `mainScript2.LateUpdate` calls it whenever the recenter input is pressed. That is the
        /// recenter the player sees, so it is the one an explicit request uses too: the bridge calls the game's own
        /// method, and the rig, the canvas and the player's camera end up where the game puts them.
        ///
        /// The runtime's `TryRecenter()` moves a different thing (the runtime's tracking origin). Doing both on one
        /// key press is what made the rig and the world disagree, so the runtime path is only used when the game has
        /// no rig to recenter (a build without mainScript2), and the origin moves only when it was asked for.
        internal static string Recenter(bool requestOrigin = false)
        {
            var game = GameRecenter();
            var owner = VrTrackingPolicy.RecenterOwner(game != null, true);
            if (owner == VrRecenterOwner.GameRig)
            {
                try
                {
                    game.VRRecenter();
                    RecenterCount++;
                    return "{\"recenter\":\"game rig\"}";
                }
                catch (Exception e) { return Fail(VrLoaderFailure.Start, e.GetType().Name + ": " + e.Message); }
            }
            if (owner == VrRecenterOwner.Runtime)
            {
                try
                {
                    var input = Subsystem<XRInputSubsystem>();
                    if (input == null) return "{\"error\":\"no XR input subsystem\"}";
                    // The tracking origin belongs to the policy: the game's own rig recenter moves the camera rig,
                    // so the runtime's origin is only moved when the policy says it may be. The old code's test was
                    // inverted (it set the mode only when the mode already was that one) and it moved the origin on
                    // every recenter, which is what made the rig and the world disagree.
                    var origin = VrTrackingPolicy.OriginFor(requestOrigin, false, input != null, input != null);
                    if (origin != VrTrackingOrigin.Unchanged) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
                    input.TryRecenter();
                    RecenterCount++;
                    return "{\"recenter\":\"runtime\"}";
                }
                catch (Exception e) { return Fail(VrLoaderFailure.Start, e.GetType().Name + ": " + e.Message); }
            }
            return "{\"recenter\":\"none\"}";
        }

        /// The game's own rig recenter, when the game has one. Null when the game has no rig to recenter.
        private static mainScript2 GameRecenter()
        {
            try { return mainScript2.s; } catch { return null; }
        }

        /// Runs the lifecycle's steps until the machine has nothing left to do. Every step goes through the manager.
        ///
        /// The drive starts at the action the caller was given. It used to always start at `ChooseLoader`, which
        /// meant a STOP was never carried out: `Disable()` asked the lifecycle for `StopSubsystems` and then
        /// threw that answer away by driving from the wrong step, so `manager.StopSubsystems()` and
        /// `manager.DeinitializeLoader()` were never called and the session kept running.
        private static string Drive(VrLoaderAction first)
        {
            try
            {
                var action = first;
                int guard = 0;
                while (action != VrLoaderAction.None && action != VrLoaderAction.Fail)
                {
                    if (++guard > 16) return Fail(VrLoaderFailure.Start, "the XR lifecycle did not settle");
                    switch (action)
                    {
                        case VrLoaderAction.ChooseLoader:
                            action = ChooseLoader();
                            break;
                        case VrLoaderAction.ConfigureFeatures:
                            ConfigureProfiles();
                            action = Lifecycle.ReportFeaturesConfigured();
                            break;
                        case VrLoaderAction.InitializeLoader:
                            action = InitializeLoader();
                            break;
                        case VrLoaderAction.StartSubsystems:
                            action = StartSubsystems();
                            break;
                        case VrLoaderAction.Verify:
                            action = Verify();
                            break;
                        case VrLoaderAction.StopSubsystems:
                            action = StopSubsystems();
                            break;
                        case VrLoaderAction.DeinitializeLoader:
                            action = DeinitializeLoader();
                            break;
                    }
                }
                if (action == VrLoaderAction.Fail) return State();
                return State();
            }
            catch (Exception e)
            {
                return Fail(VrLoaderFailure.Start, e.GetType().Name + ": " + e.Message);
            }
        }

        /// Picks the loader through the policy and puts it where the manager can use it.
        private static VrLoaderAction ChooseLoader()
        {
            var manager = Manager();
            if (manager == null)
            {
                Lifecycle.Fail(VrLoaderFailure.NoManager, "this game build has no XR manager");
                return VrLoaderAction.Fail;
            }
            _manager = manager;
            var names = new List<string>();
            var loaders = manager.activeLoaders;
            for (int i = 0; i < loaders.Count; i++) names.Add(loaders[i] != null ? loaders[i].GetType().FullName : "");
            Choice = VrLoaderPolicy.Choose(names, AllowNoPreInit);
            if (!Choice.Usable)
            {
                Lifecycle.Fail(Choice.Kind == VrLoaderKind.None ? VrLoaderFailure.NoLoader : VrLoaderFailure.RefusedLoader, Choice.Reason);
                return VrLoaderAction.Fail;
            }
            _loader = loaders[Choice.Index];
            // the manager must hold the loader we picked: it is the one that will initialize and start it
            if (_loader == null)
            {
                Lifecycle.Fail(VrLoaderFailure.NoLoader, "the chosen loader is gone");
                return VrLoaderAction.Fail;
            }
            // The game may have started the session itself already: its own SetVR calls SetXR, which drives the
            // manager. Initializing a second time is what the manager warns about ("XR Management has already
            // initialized an active loader in this scene"), so the session the manager already runs is adopted
            // instead of started again.
            if (manager.isInitializationComplete && manager.activeLoader != null)
            {
                _loader = manager.activeLoader;
                return Lifecycle.ReportAdopted();
            }
            return Lifecycle.ReportLoaderChosen(true);
        }

        /// Switches the controller profiles on before the game initializes the loader (the game's own SetVR path).
        internal static void ConfigureProfilesForGame()
        {
            try { ConfigureProfiles(); }
            catch (Exception e) { Plugin.Log.LogWarning("OpenXR: controller profiles: " + e.Message); }
        }

        /// Switches the controller profiles on, BEFORE the loader is initialized: the OpenXR loader reads the
        /// enabled features when it initializes, so a profile switched on afterwards is never used for the session.
        private static void ConfigureProfiles()
        {
            _featuresConfigured = false;
            var settings = OpenXRSettings.Instance;
            if (settings == null) return;   // no settings: nothing to configure, the loader decides on its own
            for (int i = 0; i < VrLoaderPolicy.ControllerProfiles.Length; i++)
            {
                var type = FeatureType(VrLoaderPolicy.ControllerProfiles[i]);
                if (type == null) continue;
                try
                {
                    var feature = settings.GetFeature(type);
                    if (feature == null) continue;    // this build does not ship that profile
                    feature.enabled = true;
                    _featuresConfigured = true;
                }
                catch (Exception e) { Plugin.Log.LogWarning("OpenXR: profile " + type.Name + ": " + e.Message); }
            }
        }

        /// The feature's type, by its full name. The profiles are named as strings so that a build that does not ship
        /// one of them still compiles (the game ships all seven, but a copy of another build may not).
        private static Type FeatureType(string fullName)
        {
            var all = new[]
            {
                typeof(MetaQuestTouchProControllerProfile),
                typeof(OculusTouchControllerProfile),
                typeof(ValveIndexControllerProfile),
                typeof(HTCViveControllerProfile),
                typeof(HPReverbG2ControllerProfile),
                typeof(MicrosoftMotionControllerProfile),
                typeof(KHRSimpleControllerProfile),
            };
            for (int i = 0; i < all.Length; i++) if (all[i].FullName == fullName) return all[i];
            return null;
        }

        /// The manager initializes the loader. The manager sets its own `activeLoader` and its "initialization
        /// complete" flag, so the game and the bridge always agree about what runs.
        private static VrLoaderAction InitializeLoader()
        {
            var manager = _manager ?? Manager();
            if (manager == null) { Lifecycle.Fail(VrLoaderFailure.NoManager, "this game build has no XR manager"); return VrLoaderAction.Fail; }
            _manager = manager;
            manager.InitializeLoaderSync();
            if (manager.activeLoader == null)
            {
                Lifecycle.Fail(VrLoaderFailure.Initialize, "the XR manager did not accept the loader");
                return VrLoaderAction.Fail;
            }
            _loader = manager.activeLoader;
            return Lifecycle.ReportInitialized();
        }

        /// The manager starts the subsystems. This is the same call the game makes in its own SetXR(true).
        private static VrLoaderAction StartSubsystems()
        {
            var manager = _manager ?? Manager();
            if (manager == null) { Lifecycle.Fail(VrLoaderFailure.NoManager, "this game build has no XR manager"); return VrLoaderAction.Fail; }
            manager.StartSubsystems();
            return Lifecycle.ReportStarted();
        }

        /// Checks that the session really runs: the loader is initialized and its subsystems are up.
        private static VrLoaderAction Verify()
        {
            bool display = DisplayRunning, input = InputRunning;
            return Lifecycle.ReportVerified(display, input);
        }

        /// The manager stops the subsystems.
        private static VrLoaderAction StopSubsystems()
        {
            var manager = _manager ?? Manager();
            if (manager == null) { Lifecycle.Fail(VrLoaderFailure.NoManager, "this game build has no XR manager"); return VrLoaderAction.Fail; }
            manager.StopSubsystems();
            return Lifecycle.ReportSubsystemsStopped();
        }

        /// The manager deinitializes the loader. It stops the subsystems itself first, so the XR session ends cleanly.
        private static VrLoaderAction DeinitializeLoader()
        {
            var manager = _manager ?? Manager();
            if (manager == null) { Lifecycle.Fail(VrLoaderFailure.NoManager, "this game build has no XR manager"); return VrLoaderAction.Fail; }
            manager.DeinitializeLoader();
            _loader = null;
            _featuresConfigured = false;
            return Lifecycle.ReportDeinitialized();
        }

        /// The frame tick. It only reads: the session is never started from here (that is the whole point of the
        /// explicit lifecycle), but the machine follows the manager so the state does not drift. It runs at most a few
        /// times a second, not every frame.
        private static float _nextObserve;
        internal static void Tick()
        {
            if (Time.unscaledTime < _nextObserve) return;
            _nextObserve = Time.unscaledTime + 0.5f;
            Observe();
        }

        /// Reads what the XR manager really holds and lets the machine follow it.
        ///
        /// The game drives the manager on its own (its SetVR calls SetXR), so the manager's state is the truth about
        /// the session. The machine adopts a session the game started and follows a session the game stopped: without
        /// this the bridge's state would say "idle" while the game is in VR, and the diagnostics would lie.
        internal static void Observe()
        {
            try
            {
                var manager = _manager ?? Manager();
                if (manager == null) return;
                bool initialized = manager.isInitializationComplete;
                bool display = DisplayRunning, input = InputRunning;
                if (initialized && (display || input)) Lifecycle.ReportExternallyStarted();
                else if (!initialized && !Lifecycle.Starting) Lifecycle.ReportExternallyStopped();
            }
            catch { }
        }

        /// The XR manager, when the game has one.
        internal static XRManagerSettings Manager()
        {
            if (XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null) return XRGeneralSettings.Instance.Manager;
            var all = Resources.FindObjectsOfTypeAll<XRGeneralSettings>();
            return all.FirstOrDefault(x => x != null && x.Manager != null)?.Manager;
        }

        /// The display subsystem, from the loader the manager owns.
        internal static bool DisplayRunning
        {
            get { try { var s = Subsystem<XRDisplaySubsystem>(); return s != null && s.running; } catch { return false; } }
        }

        /// The input subsystem, from the loader the manager owns.
        internal static bool InputRunning
        {
            get { try { var s = Subsystem<XRInputSubsystem>(); return s != null && s.running; } catch { return false; } }
        }

        /// The runtime's tracking-origin MODE, as the runtime reports it, and the modes it supports.
        ///
        /// This is a configuration flag, not a pose: it says which origin the runtime is set up around (the
        /// device, the floor, a tracking reference), never where the player is and never a coordinate. It is
        /// what the hardware check needs: without it, "the origin moved" could only be guessed at from the
        /// recenter counter, and the difference between "the rig moved" and "the world moved" is invisible.
        ///
        /// An empty string means the runtime does not say (no input subsystem, or the call failed): the
        /// diagnostics then report "unknown" rather than a mode nobody observed.
        internal static string TrackingOriginMode()
        {
            try
            {
                var input = Subsystem<XRInputSubsystem>();
                if (input == null) return "";
                return OriginModeName(input.GetTrackingOriginMode());
            }
            catch { return ""; }
        }

        /// The tracking-origin modes the runtime supports, as the runtime reports them.
        internal static string TrackingOriginSupported()
        {
            try
            {
                var input = Subsystem<XRInputSubsystem>();
                if (input == null) return "";
                return OriginModeName(input.GetSupportedTrackingOriginModes());
            }
            catch { return ""; }
        }

        /// A tracking-origin mode set, as a stable lowercase name. A mode the runtime does not name is "unknown",
        /// so a new mode never turns into a wrong claim.
        private static string OriginModeName(TrackingOriginModeFlags flags)
        {
            var names = new List<string>();
            if ((flags & TrackingOriginModeFlags.Device) != 0) names.Add("device");
            if ((flags & TrackingOriginModeFlags.Floor) != 0) names.Add("floor");
            if ((flags & TrackingOriginModeFlags.TrackingReference) != 0) names.Add("trackingreference");
            if ((flags & TrackingOriginModeFlags.Unbounded) != 0) names.Add("unbounded");
            if (names.Count == 0) return "unknown";
            return string.Join("|", names.ToArray());
        }

        /// The loader the manager owns, so the subsystems are the ones the game is actually using.
        private static XRLoader ActiveLoader()
        {
            var manager = _manager ?? Manager();
            if (manager != null && manager.activeLoader != null) return manager.activeLoader;
            return _loader;
        }

        private static T Subsystem<T>() where T : class, ISubsystem
        {
            var loader = ActiveLoader();
            return loader != null ? loader.GetLoadedSubsystem<T>() : null;
        }

        /// A failure: the reason is recorded and the answer carries it. The player gets non-VR gameplay, not a
        /// promise of a fallback that does not exist in this game build.
        private static string Fail(VrLoaderFailure failure, string message)
        {
            Lifecycle.Fail(failure, message);
            LastError = message ?? "";
            Plugin.Log.LogWarning("OpenXR: " + LastError + " (the game stays playable without VR)");
            return State();
        }

        /// The state, for the diagnostics bridge. No pose, ever.
        internal static string State()
        {
            var controllers = new StringBuilder("[");
            try
            {
                var devices = new List<InputDevice>();
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, devices);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i]; bool valid = d.isValid;
                    if (i > 0) controllers.Append(',');
                    controllers.Append("{\"name\":").Append(Json.Str(d.name)).Append(",\"valid\":").Append(valid ? "true" : "false")
                        .Append(",\"left\":").Append((d.characteristics & InputDeviceCharacteristics.Left) != 0 ? "true" : "false")
                        .Append(",\"right\":").Append((d.characteristics & InputDeviceCharacteristics.Right) != 0 ? "true" : "false").Append('}');
                }
            }
            catch { }
            controllers.Append(']');
            if (Lifecycle.Running)
            {
                try { Runtime = OpenXRRuntime.name; ApiVersion = OpenXRRuntime.apiVersion; } catch { }
            }
            var manager = _manager ?? Manager();
            bool managerActive = manager != null && manager.activeLoader != null;
            return "{\"enabled\":" + (Enabled ? "true" : "false") +
                   ",\"state\":" + Json.Str(Lifecycle.State.ToString().ToLowerInvariant()) +
                   ",\"failure\":" + Json.Str(Lifecycle.Failure.ToString().ToLowerInvariant()) +
                   ",\"running\":" + (Lifecycle.Running ? "true" : "false") +
                   ",\"failed\":" + (Failed ? "true" : "false") +
                   ",\"loader\":" + Json.Str(Choice.Kind == VrLoaderKind.None ? "none" : Choice.Kind.ToString().ToLowerInvariant()) +
                   ",\"loaderName\":" + Json.Str(Choice.Name ?? "") +
                   ",\"loaderUsable\":" + (Choice.Usable ? "true" : "false") +
                   ",\"managerActive\":" + (managerActive ? "true" : "false") +
                   ",\"managerInitialized\":" + (manager != null && manager.isInitializationComplete ? "true" : "false") +
                   ",\"featuresConfigured\":" + (_featuresConfigured ? "true" : "false") +
                   ",\"runtime\":" + Json.Str(Runtime) + ",\"api\":" + Json.Str(ApiVersion) +
                   ",\"originMode\":" + Json.Str(TrackingOriginMode()) + ",\"originSupported\":" + Json.Str(TrackingOriginSupported()) +
                   ",\"display\":" + (DisplayRunning ? "true" : "false") + ",\"input\":" + (InputRunning ? "true" : "false") +
                   ",\"controllers\":" + controllers +
                   ",\"recenter\":" + RecenterCount + ",\"starts\":" + Lifecycle.StartCount + ",\"stops\":" + Lifecycle.StopCount +
                   ",\"failures\":" + Lifecycle.FailureCount + ",\"retries\":" + Lifecycle.RetryCount +
                   ",\"error\":" + Json.Str(Lifecycle.Error) + "}";
        }

        /// The game's own VR entry. The game owns it: the bridge does not start a session behind the player's back.
        /// The prefix only records the request, so the contracts know what the game wants; the postfix reports what
        /// the runtime did.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SetVR))]
        private static class SetGameVR
        {
            private static void Prefix(bool _enable)
            {
                if (_enable)
                {
                    // The game calls SetXR right after this, and SetXR initializes the loader. The OpenXR loader
                    // reads the enabled features when it initializes, so the controller profiles have to be on
                    // BEFORE that happens: a profile switched on afterwards is never used for the session.
                    ConfigureProfilesForGame();
                    VrContractBridge.OnVrRequested();
                }
                else VrContractBridge.OnVrReleased();
            }

            private static void Postfix(bool _enable)
            {
                // The game drove the manager itself (SetXR -> InitializeLoaderSync + StartSubsystems), so the
                // machine follows what the manager really holds: the session runs, or it did not start.
                Observe();
                if (_enable) VrContractBridge.OnVrSettled();
                else VrContractBridge.OnVrStopped();
            }
        }
    }
}
