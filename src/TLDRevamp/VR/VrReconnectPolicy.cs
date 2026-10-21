using System;

namespace TLDRevamp.VR
{
    /// What has to be cleared when the session is re-entered after a disconnect.
    public sealed class VrReconnectState
    {
        public bool Seated;              // the player was sitting somewhere
        public bool GrabbingsHeld;       // a grab is held
        public bool UiPressOpen;         // a UI press is open
        public bool HeadLookHeld;        // the look is held
        public bool RecenterPending;     // a recenter is queued
        public bool DuckToggle;          // the duck toggle is on
        public bool LocomotionLatched;   // a movement axis is stuck

        public void ClearAll()
        {
            Seated = false;
            GrabbingsHeld = false;
            UiPressOpen = false;
            HeadLookHeld = false;
            RecenterPending = false;
            DuckToggle = false;
            LocomotionLatched = false;
        }

        public bool Any =>
            Seated || GrabbingsHeld || UiPressOpen || HeadLookHeld || RecenterPending || DuckToggle || LocomotionLatched;
    }

    /// The reconnect sanitization contract.
    ///
    /// A reconnect re-creates the player, but the VR state of the old player is not recreated with it: the seated
    /// flag, the held grab, the open UI press, the latched look, the duck toggle and the latched movement axis are
    /// all per-player fields. Left alone they read as "the player is still holding the thing they were holding" and
    /// the player walks with a stick that is still pushed.
    ///
    /// The contract: a reconnect (or a fresh spawn) clears every one of them, and the order matters — the session and
    /// the hands are reset first, so nothing that is cleared can be re-armed by a frame that is still in flight.
    /// The policy is pure: it takes a state and returns a clean one. It never touches the network protocol and never
    /// carries a pose.
    public sealed class VrReconnectPolicy
    {
        private readonly VrSessionMachine _session;
        private readonly VrTrackingSet _tracking;
        private readonly VrHandMachine _left, _right;
        private readonly VrUiRayMachine _leftUi, _rightUi;
        private readonly VrLocomotionPolicy _locomotion;

        public VrReconnectPolicy(VrSessionMachine session, VrTrackingSet tracking,
                                 VrHandMachine left, VrHandMachine right,
                                 VrUiRayMachine leftUi, VrUiRayMachine rightUi,
                                 VrLocomotionPolicy locomotion)
        {
            _session = session;
            _tracking = tracking;
            _left = left;
            _right = right;
            _leftUi = leftUi;
            _rightUi = rightUi;
            _locomotion = locomotion;
        }

        /// The state the player is in right now, as the contract sees it.
        public VrReconnectState Capture(VrReconnectState state)
        {
            if (state == null) state = new VrReconnectState();
            state.UiPressOpen = _leftUi.Pressed != null || _rightUi.Pressed != null;
            state.GrabbingsHeld = _left.Holding || _right.Holding;
            state.HeadLookHeld = _left.Holding || _right.Holding;
            return state;
        }

        /// A reconnect happened: clear everything, in the order the runtime needs it.
        ///
        /// The order is the contract:
        ///   1. the session is stopped, so no frame of the old session is still being read
        ///   2. the hands cancel their holds, so no grab survives the disconnect
        ///   3. the UI rays cancel their presses, so no button stays down
        ///   4. the tracking is released, so the next frame re-acquires instead of using a stale pose
        ///   5. the locomotion is cleared, so no axis is latched
        public void Sanitize(VrReconnectState state)
        {
            if (state == null) state = new VrReconnectState();

            _session.RequestStop();

            _left.Cancel();
            _right.Cancel();

            _leftUi.Cancel();
            _rightUi.Cancel();
            _leftUi.FocusLost();
            _rightUi.FocusLost();

            _tracking.Head.ReportAbsent();
            _tracking.LeftHand.ReportAbsent();
            _tracking.RightHand.ReportAbsent();

            _locomotion.Seated = false;
            _locomotion.Driving = false;

            state.ClearAll();
        }

        /// The state a fresh spawn starts from. The same clean state, without the stop: a spawn is not a disconnect.
        public VrReconnectState Fresh(VrReconnectState state)
        {
            var clean = new VrReconnectState();
            Sanitize(clean);
            return clean;
        }
    }
}
