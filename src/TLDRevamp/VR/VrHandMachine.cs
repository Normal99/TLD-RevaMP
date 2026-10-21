using System;

namespace TLDRevamp.VR
{
    /// What a hand is doing with the neutral pose: the state the hand is in while it holds nothing.
    public enum VrHandPose
    {
        Unknown,    // the hand is not known yet
        Relaxed,    // open, nothing held
        Pointing,   // a finger is extended (the game calls this a "point")
        Holding,    // the hand holds something
        Interacting // the hand works a control (a trigger, a button)
    }

    /// The state of one hand's action, as a contract.
    ///
    /// The game reads the hands through handControllerInputScript: every button produces a Down, a Hold and an Up
    /// edge from the previous frame's Hold. vrInteractHandScript.Update turns a held grip or trigger into a grab of
    /// whatever the sphere collider overlaps, and slides or rotates it. The edge flags are the game's, and a dropped
    /// controller leaves the last Hold in place: the hand can stay "holding" a grab that no longer exists.
    ///
    /// This machine owns the action instead. A hold only begins on a real press edge, a release only on a real
    /// release edge, and a lost device cancels the hold instead of leaving it stuck.
    public enum VrHandAction
    {
        Idle,       // nothing pressed
        Pressed,    // the action went down this frame
        Holding,    // the action is down and has been for a while
        Released,   // the action came up this frame
        Cancelled   // the action was cancelled (the device went away, the session failed, the seat changed)
    }

    /// The neutral-action-hold contract of one hand.
    public sealed class VrHandMachine
    {
        private readonly string _name;
        private VrHandAction _action = VrHandAction.Idle;
        private VrHandPose _pose = VrHandPose.Unknown;
        private bool _gripDown, _triggerDown;
        private bool _gripHold, _triggerHold;
        private int _presses, _releases, _cancels;

        public VrHandMachine(string name)
        {
            _name = name ?? "";
        }

        public string Name { get { return _name; } }
        public VrHandAction Action { get { return _action; } }
        public VrHandPose Pose { get { return _pose; } }
        public int Presses { get { return _presses; } }
        public int Releases { get { return _releases; } }
        public int Cancels { get { return _cancels; } }

        /// True while the hand holds the action. The game reads this to decide whether to keep a grab.
        public bool Holding => _action == VrHandAction.Holding || _action == VrHandAction.Pressed;
        /// True only on the frame the action went down. One press, one event: a held button does not repeat.
        public bool PressEdge => _action == VrHandAction.Pressed;
        /// True only on the frame the action came up.
        public bool ReleaseEdge => _action == VrHandAction.Released;

        /// The neutral pose of the hand: the game asks for it through vrFingerScript and the hand pose tables.
        public void SetNeutralPose(VrHandPose pose) { _pose = pose; }

        /// The device reported the buttons of one frame. The machine derives the edges itself, so a caller that
        /// reports the same hold twice produces one press and no second press.
        public void ReportButtons(bool gripHold, bool triggerHold)
        {
            if (gripHold != _gripHold) { _gripDown = gripHold; _gripHold = gripHold; }
            if (triggerHold != _triggerHold) { _triggerDown = triggerHold; _triggerHold = triggerHold; }
        }

        /// The action went down this frame. Idempotent within a frame: a second call does not press again.
        public bool Press()
        {
            if (_action == VrHandAction.Pressed || _action == VrHandAction.Holding)
                return false;
            _action = VrHandAction.Pressed;
            _presses++;
            return true;
        }

        /// The action came up this frame. A press that was never seen is ignored: there is no release without a press.
        public bool Release()
        {
            if (_action == VrHandAction.Idle || _action == VrHandAction.Released || _action == VrHandAction.Cancelled)
                return false;
            _action = VrHandAction.Released;
            _releases++;
            return true;
        }

        /// The action is down and has been for more than a frame.
        public void Sustain()
        {
            if (_action == VrHandAction.Pressed) _action = VrHandAction.Holding;
        }

        /// The action was cancelled: the device went away, the session failed, the player left the seat. A cancelled
        /// hold releases the grab; it is not a release the game can act on (nothing was let go by the player).
        public bool Cancel()
        {
            if (_action == VrHandAction.Idle || _action == VrHandAction.Cancelled)
                return false;
            _action = VrHandAction.Cancelled;
            _cancels++;
            return true;
        }

        /// A new frame: the press and release edges are single-frame facts.
        public void EndFrame()
        {
            if (_action == VrHandAction.Pressed) _action = VrHandAction.Holding;
            else if (_action == VrHandAction.Released) _action = VrHandAction.Idle;
        }

        /// The whole contract in one line, for the diagnostics bridge. No pose, no device name.
        public string Describe()
        {
            return _name + " action=" + _action.ToString().ToLowerInvariant() + " pose=" + _pose.ToString().ToLowerInvariant() +
                   " presses=" + _presses + " releases=" + _releases + " cancels=" + _cancels;
        }
    }
}
