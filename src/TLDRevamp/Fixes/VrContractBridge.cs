using System;
using System.Text;
using HarmonyLib;
using TLDRevamp.VR;
using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.Fixes
{
    /// The safe seam between the game's VR code and the VR contracts of issue #41.
    ///
    /// This is the only place that touches the game. It does three things and nothing else:
    ///   - it reads what the runtime reports and feeds it into the contracts
    ///   - it reads the contracts' decisions and applies the two that are safe to apply
    ///   - it reports the state to the diagnostics bridge
    ///
    /// What it does NOT do:
    ///   - it does not change the network protocol and does not write a pose into a packet
    ///   - it does not move the gameplay root: the rig policy refuses that by contract
    ///   - it does not enable a VR feature that the game does not have: it only stops the hands from being live
    ///     when the session is not running, and keeps the menu canvas flat when the session failed
    ///
    /// The call order it follows (docs/VR-REWRITE.md):
    ///   mainscript.SetVR(true)  -> SetXR(false) -> BVR = true -> SetXR(true)
    ///                           -> vrHandsScript.s.Init() -> vrusables[i].UpdVR() -> mainScript2.s.UpdVRCanv()
    /// The seam hooks the two ends of that: the session decision before the hands are initialized, and the seated
    /// rule after the input has been read.
    public static class VrContractBridge
    {
        internal static readonly VrContracts Contracts = new VrContracts();
        private static bool _seated, _driving;
        private static bool _headPresent, _leftPresent, _rightPresent;
        private static double _now;
        /// The contracts, for the bridge and the unit tests.
        internal static VrContracts State { get { return Contracts; } }

        /// A frame of the contract. Called from the plugin's update, next to the OpenXR tick.
        internal static void Tick()
        {
            _now += Time.unscaledDeltaTime;
            ReadRuntime();
            Contracts.Tick(_now, _seated, _driving, _headPresent, _leftPresent, _rightPresent);
            ApplySeatedRule();
        }

        /// What the runtime says about the devices. Read-only: it never opens a device or a subsystem.
        private static void ReadRuntime()
        {
            _headPresent = false;
            _leftPresent = _rightPresent = false;
            try
            {
                var devices = new System.Collections.Generic.List<InputDevice>();
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, devices);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    if (!d.isValid) continue;
                    bool left = (d.characteristics & InputDeviceCharacteristics.Left) != 0;
                    bool right = (d.characteristics & InputDeviceCharacteristics.Right) != 0;
                    if (left) _leftPresent = true;
                    if (right) _rightPresent = true;
                }
            }
            catch { }
            // the head is tracked while the display subsystem runs: without a headset there is no head
            _headPresent = OpenXrBridge.DisplayRunning;
        }

        /// The seated rule: a seated player may not walk with the stick.
        ///
        /// The game reads the left stick into input.forward/backward/left/right in inputscript.Update, when VR is on.
        /// The seam reads the same values the game just read, applies the rule, and writes the result back. The look
        /// (the right stick) and the seated controls are not touched.
        private static void ApplySeatedRule()
        {
            try
            {
                var input = inputscript.i;
                if (input == null) return;
                if (!Contracts.Locomotion.Seated && !Contracts.Locomotion.Driving) return;
                var intent = new VrMoveIntent
                {
                    Forward = input.forward,
                    Backward = input.backward,
                    Left = input.left,
                    Right = input.right,
                    Run = input.run,
                };
                var allowed = Contracts.Locomotion.Apply(intent);
                if (allowed.Suppressed)
                {
                    input.forward = 0f;
                    input.backward = 0f;
                    input.left = 0f;
                    input.right = 0f;
                    input.run = false;
                }
            }
            catch { }
        }

        /// The seat and the driving state, from the player.
        internal static void SetSeatState(bool seated, bool driving)
        {
            _seated = seated;
            _driving = driving;
            Contracts.Rig.Seated = seated;
            Contracts.Locomotion.Seated = seated;
            Contracts.Locomotion.Driving = driving;
        }

        /// The rig rule, applied to the game.
        ///
        /// The game, when VR is on, copies the camera's pose into the head transform (`Th`) and then copies `Th` into
        /// the head IK target (`anim.HHTarget`) — and the head IK target is what the network sends (PlayerLook.
        /// WriteLocalPose). That is the path a headset pose would take to the wire.
        ///
        /// The contract says: the gameplay root does not follow the headset. So when the policy refuses the view root
        /// (a seated player, a head that is not tracked, no session), the head IK target is taken from the gameplay
        /// root instead of from the camera, and the gameplay root is left exactly where the game put it. When the
        /// policy allows the view root, nothing is touched: the game's own behaviour is already the local head view.
        ///
        /// This never writes a transform of its own: it only chooses between the two sources the game already has.
        internal static void ApplyRigPolicy(fpscontroller player)
        {
            if (player == null) return;
            var decision = Contracts.Rig.HeadDecision();
            if (decision.FromViewRoot) return;          // the game's own local head view: nothing to do
            var anim = player.anim;
            if (anim == null || anim.HHTarget == null || player.Th == null) return;
            // the head IK target comes from the gameplay root, not from the camera
            anim.HHTarget.position = player.Th.position;
            anim.HHTarget.rotation = player.Th.rotation;
        }

        /// A reconnect happened: clear the VR state so nothing survives the disconnect.
        internal static void SanitizeReconnect()
        {
            var state = Contracts.Reconnect.Capture(Contracts.ReconnectState);
            Contracts.Reconnect.Sanitize(state);
            Contracts.ReconnectState.ClearAll();
        }

        /// The state, for the diagnostics bridge. No pose, ever.
        internal static string Json() { return VrDiagnostics.Json(Contracts); }

        /// The session decision the hands and the canvas follow. The game's own SetVR calls the hands and the canvas
        /// after the XR manager; the seam gives them the state the contract holds.
        internal static VrSessionDecision SessionDecision() { return Contracts.Session.Decision(); }

        /// Whether the hands may be live. The game's vrHandsScript.Init turns them on when mainscript.IsVR() is true;
        /// the seam refuses that when the session is not running, so a failed start does not leave a live hand.
        internal static bool HandsMayBeLive() { return Contracts.Session.Decision().HandsActive; }

        /// Whether the menu canvas belongs in world space. The game's mainScript2.UpdVRCanv uses mainscript.IsVR();
        /// the seam keeps the canvas flat when the session failed or never started.
        internal static bool CanvasMayBeWorldSpace() { return Contracts.Session.Decision().UiWorldSpace; }

        // ---- the seams the OpenXR bridge calls
        //
        // The bridge owns the XR loader's life; the contracts own what the game may do while it runs. These four
        // calls are the whole conversation between them: a request, the outcome, and the way back.

        /// The game asked for VR. The contract records the request; the bridge does the work.
        internal static void OnVrRequested() { Contracts.Session.RequestStart(); }

        /// The game's own XR setup finished. The contract learns whether a session really runs.
        internal static void OnVrSettled()
        {
            if (OpenXrBridge.Failed) Contracts.Session.ReportFailure(OpenXrBridge.LastError);
            else if (OpenXrBridge.Started) Contracts.Session.ReportStarted();
            else Contracts.Session.ReportFailure("the XR loader did not start");
        }

        /// The game let go of VR.
        internal static void OnVrReleased() { Contracts.Session.RequestStop(); }

        /// The game's own teardown finished.
        internal static void OnVrStopped() { Contracts.Session.ReportStopAfterGame(); }

        // ---- the seams on the game's own methods

        /// mainscript.SetVR is the game's single VR entry: the menu toggle, the refresh button and the settings
        /// refresh all come through it. The prefix turns the request into a session decision before the game runs its
        /// own XR setup, and the postfix applies the hands and the canvas from the contract.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SetVR))]
        private static class SetVrSeam
        {
            private static void Prefix(mainscript __instance, bool _enable)
            {
                if (_enable) Contracts.Session.RequestStart();
                else Contracts.Session.RequestStop();
            }

            private static void Postfix(mainscript __instance, bool _enable)
            {
                // the game's own XR setup ran: report what the runtime did, so the contract knows whether the
                // session is really running or only the flag says so
                if (_enable)
                {
                    if (OpenXrBridge.Failed) Contracts.Session.ReportFailure(OpenXrBridge.LastError);
                    else if (OpenXrBridge.Started) Contracts.Session.ReportStarted();
                    else Contracts.Session.ReportFailure("the XR loader did not start");
                }
                else Contracts.Session.ReportStopAfterGame();
            }
        }

        /// The hands: the game's vrHandsScript.Init turns them on when mainscript.IsVR() is true — the flag alone.
        /// When the session is not running (a failed start), the seam turns them off, so a failed VR start does not
        /// leave a live hand in the world reading a device that is not there.
        [HarmonyPatch(typeof(vrHandsScript), nameof(vrHandsScript.Init))]
        private static class HandsSeam
        {
            private static void Postfix(vrHandsScript __instance)
            {
                if (__instance == null) return;
                if (Contracts.Session.Decision().HandsActive) return;   // the session runs: the game's own call stands
                __instance.SetVisible(false);
                __instance.gameObject.SetActive(false);
                __instance.enabled = false;
            }
        }

        /// The menu canvas: the game's mainScript2.UpdVRCanv puts the canvas in world space when IsVR() is true —
        /// the flag alone. When the session is not running, the seam puts it back to a screen-space overlay, so a
        /// failed VR start does not leave a menu floating in the world in front of a player who is not in VR.
        [HarmonyPatch(typeof(mainScript2), nameof(mainScript2.UpdVRCanv), new[] { typeof(bool) })]
        private static class CanvasSeam
        {
            private static void Postfix(mainScript2 __instance)
            {
                if (__instance == null) return;
                if (Contracts.Session.Decision().UiWorldSpace) return;   // the session runs: the game's own call stands
                if (__instance.canv != null) __instance.canv.renderMode = RenderMode.ScreenSpaceOverlay;
            }
        }

        /// The input: the game's VR block reads the sticks. The seam's postfix applies the seated rule to the values
        /// the game just read.
        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class InputSeam
        {
            private static void Postfix()
            {
                ApplySeatedRule();
            }
        }

        /// The seat: the player's seated and driving state decides whether the stick may move the body.
        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.GetIn), new[] { typeof(seatscript) })]
        private static class SeatSeam
        {
            private static void Postfix(fpscontroller __instance)
            {
                if (__instance == null) return;
                SetSeatState(true, __instance.Bdriving);
            }
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.GetOut))]
        private static class SeatOutSeam
        {
            private static void Postfix(fpscontroller __instance)
            {
                if (__instance == null) return;
                SetSeatState(false, false);
            }
        }

        /// The rig: the game copies the camera into the head transform and the head transform into the head IK
        /// target. The postfix applies the rig policy to the head IK target, so a seated or untracked player does not
        /// carry a camera pose into the body the network sends.
        [HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class RigSeam
        {
            private static void Postfix(fpscontroller __instance)
            {
                ApplyRigPolicy(__instance);
            }
        }
    }
}
