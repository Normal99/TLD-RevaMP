using System;

namespace TLDRevamp.VR
{
    /// The tracking state of one tracked source (the head, the left hand, the right hand).
    public enum VrTrackingState
    {
        Untracked,  // never seen: the runtime has no device for it
        Lost,       // it was seen and is now gone
        Tracked     // it is seen now
    }

    /// What the caller may do with a tracked source in its current state.
    public struct VrTrackingDecision
    {
        public bool UsePose;        // the pose may be read and applied
        public bool FreezePose;     // the last known pose has to be held
        public bool ReleaseInteractions; // a held grab or a UI press has to be let go
        public bool ShowHand;       // the hand visual may be drawn
        public bool Changed;
    }

    /// The tracking validity, loss and recovery contract.
    ///
    /// The game reads the controllers through handControllerInputScript.Update: it asks the device for the stick, the
    /// trigger, the grip and the buttons, and it only asks when mainscript.IsVR() is true. A device that is not valid
    /// keeps its last stick and button values, so a controller that drops out leaves the game with a half-pressed
    /// button and a stick that is still at its last angle. This machine owns the validity instead: a lost source
    /// releases what it held and stops feeding the game.
    ///
    /// The pose itself never leaves this machine. There is no pose in the state: the caller asks "may I use the pose"
    /// and gets an answer, so nothing here can end up in a packet.
    public sealed class VrTrackingMachine
    {
        private readonly string _name;
        private VrTrackingState _state = VrTrackingState.Untracked;
        private int _lossCount, _recoveryCount;
        private double _lostSince;
        private double _now;

        public VrTrackingMachine(string name)
        {
            _name = name ?? "";
        }

        public string Name { get { return _name; } }
        public VrTrackingState State { get { return _state; } }
        public int LossCount { get { return _lossCount; } }
        public int RecoveryCount { get { return _recoveryCount; } }
        public bool Tracked { get { return _state == VrTrackingState.Tracked; } }

        /// The clock the caller feeds in. The machine never reads a clock of its own, so a test is deterministic.
        public void SetTime(double now) { _now = now; }

        /// The device was seen. The first sighting after a loss is a recovery.
        public VrTrackingDecision ReportPresent()
        {
            if (_state == VrTrackingState.Tracked)
                return Decision(false);
            if (_state == VrTrackingState.Lost)
            {
                _recoveryCount++;
                _state = VrTrackingState.Tracked;
                return Decision(true);   // the interactions the loss released may be taken again
            }
            _state = VrTrackingState.Tracked;
            return Decision(true);
        }

        /// The device went away. A loss releases every interaction the source held: a held grab, a pressed UI button.
        public VrTrackingDecision ReportLost()
        {
            if (_state != VrTrackingState.Tracked)
                return Decision(false);
            _state = VrTrackingState.Lost;
            _lossCount++;
            _lostSince = _now;
            return Decision(true);
        }

        /// The device was never there and still is not. Nothing is released (there was nothing to release) and the
        /// state does not change: the machine does not count a loss for a device that was never seen.
        public VrTrackingDecision ReportAbsent()
        {
            if (_state == VrTrackingState.Untracked)
                return Decision(false);
            if (_state == VrTrackingState.Lost)
                return Decision(false);
            return ReportLost();
        }

        /// Seconds since the last loss. Zero while the source is tracked.
        public double SecondsSinceLoss
        {
            get { return _state == VrTrackingState.Lost ? _now - _lostSince : 0.0; }
        }

        public VrTrackingDecision Decision()
        {
            switch (_state)
            {
                case VrTrackingState.Tracked:
                    return new VrTrackingDecision { UsePose = true, FreezePose = false, ReleaseInteractions = false, ShowHand = true };
                case VrTrackingState.Lost:
                    // the pose is held, not read: a lost device must not snap the hand to its last known place
                    return new VrTrackingDecision { UsePose = false, FreezePose = true, ReleaseInteractions = true, ShowHand = false };
                default:
                    return new VrTrackingDecision { UsePose = false, FreezePose = true, ReleaseInteractions = true, ShowHand = false };
            }
        }

        private VrTrackingDecision Decision(bool changed)
        {
            var d = Decision();
            d.Changed = changed;
            return d;
        }

        public string Describe()
        {
            return _name + "=" + _state.ToString().ToLowerInvariant() + " losses=" + _lossCount + " recoveries=" + _recoveryCount;
        }
    }

    /// The tracking of the head and of both hands, as one contract. The head drives the camera and the look; the hands
    /// drive the grab and the UI ray. The three are tracked separately: a lost left hand must not take the head look
    /// down with it.
    public sealed class VrTrackingSet
    {
        public readonly VrTrackingMachine Head = new VrTrackingMachine("head");
        public readonly VrTrackingMachine LeftHand = new VrTrackingMachine("left");
        public readonly VrTrackingMachine RightHand = new VrTrackingMachine("right");

        public void SetTime(double now)
        {
            Head.SetTime(now);
            LeftHand.SetTime(now);
            RightHand.SetTime(now);
        }

        /// The head is the one that decides whether the world may be looked around in.
        public bool CanLook => Head.Decision().UsePose;
        public bool BothHandsTracked => LeftHand.Tracked && RightHand.Tracked;
        public bool AnyHandTracked => LeftHand.Tracked || RightHand.Tracked;

        public string Describe()
        {
            return Head.Describe() + " " + LeftHand.Describe() + " " + RightHand.Describe();
        }
    }
}
