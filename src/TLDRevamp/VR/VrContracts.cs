using System;

namespace TLDRevamp.VR
{
    /// The VR contracts of issue #41, as one object.
    ///
    /// Everything in here is pure: the objects hold state and answer questions, they never read a headset, a clock or
    /// a scene. The VR-specific code (OpenXrBridge and the seams around it) feeds the runtime's answers in and reads
    /// the decisions out. Nothing in here can put a pose into a packet: the state has no pose fields at all.
    ///
    /// The owners (see docs/VR-REWRITE.md):
    ///   VrSessionMachine    the XR session lifecycle and the fallback to the legacy path
    ///   VrTrackingSet       the validity, loss and recovery of the head and the hands
    ///   VrHandMachine       the neutral-action-hold contract of one hand
    ///   VrUiRayMachine      the UI ray: hit, focus, one activation, cancel
    ///   VrRigPolicy         which transforms the headset may move (the view, never the gameplay root)
    ///   VrLocomotionPolicy  the seated rule: a seated player does not walk
    ///   VrReconnectPolicy   what a reconnect clears
    public sealed class VrContracts
    {
        public readonly VrSessionMachine Session = new VrSessionMachine();
        public readonly VrTrackingSet Tracking = new VrTrackingSet();
        public readonly VrHandMachine LeftHand = new VrHandMachine("left");
        public readonly VrHandMachine RightHand = new VrHandMachine("right");
        public readonly VrUiRayMachine LeftUi = new VrUiRayMachine("left");
        public readonly VrUiRayMachine RightUi = new VrUiRayMachine("right");
        public readonly VrRigPolicy Rig = new VrRigPolicy();
        public readonly VrLocomotionPolicy Locomotion = new VrLocomotionPolicy();
        public readonly VrReconnectState ReconnectState = new VrReconnectState();

        private VrReconnectPolicy _reconnect;

        /// The reconnect policy, wired to the objects above.
        public VrReconnectPolicy Reconnect
        {
            get
            {
                if (_reconnect == null)
                    _reconnect = new VrReconnectPolicy(Session, Tracking, LeftHand, RightHand, LeftUi, RightUi, Locomotion);
                return _reconnect;
            }
        }

        /// The session state and the tracking state pushed into the rig policy, so the rig always sees the truth.
        public void Sync()
        {
            Rig.SessionActive = Session.IsVr;
            Rig.HeadTracked = Tracking.Head.Tracked;
        }

        /// A frame of the whole contract. The caller feeds the runtime's answers in and gets the decisions out.
        public void Tick(double now, bool seated, bool driving, bool headPresent, bool leftPresent, bool rightPresent)
        {
            Tracking.SetTime(now);
            if (headPresent) Tracking.Head.ReportPresent(); else Tracking.Head.ReportLost();
            if (leftPresent) Tracking.LeftHand.ReportPresent(); else Tracking.LeftHand.ReportLost();
            if (rightPresent) Tracking.RightHand.ReportPresent(); else Tracking.RightHand.ReportLost();
            Locomotion.Seated = seated;
            Locomotion.Driving = driving;
            Rig.Seated = seated;      // the rig policy needs the seat too: a seated head comes from the seat
            Sync();

            // a hand that is not tracked may not keep a grab or a UI press
            if (!Tracking.LeftHand.Tracked) { LeftHand.Cancel(); LeftUi.Cancel(); }
            if (!Tracking.RightHand.Tracked) { RightHand.Cancel(); RightUi.Cancel(); }

            LeftHand.EndFrame();
            RightHand.EndFrame();
            LeftUi.EndFrame();
            RightUi.EndFrame();
        }

        /// The whole state as one line, for the diagnostics bridge. It carries counts and names, never a pose.
        public string Describe()
        {
            return "session " + Session.Describe() + " | tracking " + Tracking.Describe() +
                   " | " + LeftHand.Describe() + " | " + RightHand.Describe() +
                   " | " + LeftUi.Describe() + " | " + RightUi.Describe() +
                   " | rig " + Rig.PolicyReason + " | locomotion " + Locomotion.Describe() +
                   " | reconnect " + (ReconnectState.Any ? "dirty" : "clean");
        }
    }
}
