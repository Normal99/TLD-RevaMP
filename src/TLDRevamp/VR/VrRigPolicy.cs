using System;

namespace TLDRevamp.VR
{
    /// Which transforms the VR rig may move.
    ///
    /// The game has two roots that must never be confused:
    ///   - the gameplay root: the character's body (Th), the root transform and the Rigidbody. The network moves it.
    ///   - the view root: the camera rig (CamParent) and the head lean transform (THeadLean). The headset moves it.
    ///
    /// Observed order (docs/VR-REWRITE.md): fpscontroller.Update, when mainscript.IsVR() is true, copies
    ///   Th.position = mainCam.transform.position; Th.rotation = mainCam.transform.rotation;
    /// and then anim.HHTarget takes Th's place and rotation. The head IK target is what the network writes
    /// (PlayerLook.WriteLocalPose sends a.HHTarget and the other limbs as the local player's body), so a headset
    /// pose in the view root reaches the network through the head IK target.
    ///
    /// The contract: the view root may follow the headset, the gameplay root may not. When the rig has to keep the
    /// gameplay root where it is, the head IK target is taken from the gameplay root instead of from the camera.
    public enum VrRigTarget
    {
        Head,     // the head IK target (anim.HHTarget)
        LeftHand, // the left hand IK target (anim.LHTarget)
        RightHand // the right hand IK target (anim.RHTarget)
    }

    /// What the rig is allowed to do for one target this frame.
    public struct VrRigDecision
    {
        public bool FromViewRoot;   // the target may be taken from the camera rig (the headset)
        public bool FromGameplayRoot; // the target must be taken from the gameplay root
        public bool AllowBodyMotion;  // the gameplay root may move because of the headset
        public bool Changed;

        public override string ToString() =>
            "view=" + FromViewRoot + " gameplay=" + FromGameplayRoot + " bodymotion=" + AllowBodyMotion;
    }

    /// The rig policy of issue #41: the headset moves the view, never the gameplay root.
    ///
    /// The policy is a pure function of the session state, the tracking state, the seat state and the pose policy.
    /// It holds no pose and computes no transform: the caller reads the decision and applies it to the game's own
    /// transforms. That is what makes it testable without a headset and what keeps the network protocol untouched.
    public sealed class VrRigPolicy
    {
        /// The reason the gameplay root is not allowed to follow the headset.
        public const string LocalViewOnly = "local head view only";

        private bool _seated;
        private bool _headTracked = true;
        private bool _sessionActive;
        private bool _allowGameplayMotion;

        public VrRigPolicy()
        {
            _allowGameplayMotion = false;   // the default and the contract: the gameplay root never follows the headset
        }

        /// Whether the player is seated. A seated player has no body to move: the seat owns the body.
        public bool Seated { get { return _seated; } set { _seated = value; } }

        /// Whether the head is tracked. A lost head may not drive the view.
        public bool HeadTracked { get { return _headTracked; } set { _headTracked = value; } }

        /// Whether the XR session runs.
        public bool SessionActive { get { return _sessionActive; } set { _sessionActive = value; } }

        /// Whether anything may move the gameplay root because of the headset. Off by default and the only way it
        /// ever turns on: a caller that wants it has to say so out loud, and the contract below still refuses it
        /// while the player is seated.
        public bool AllowGameplayMotion
        {
            get { return _allowGameplayMotion; }
            set { _allowGameplayMotion = value; }
        }

        /// The decision for one rig target.
        public VrRigDecision Decide(VrRigTarget target)
        {
            var d = new VrRigDecision();
            if (!_sessionActive)
            {
                // no session: nothing may follow a headset that is not there
                d.FromGameplayRoot = true;
                d.AllowBodyMotion = false;
                return d;
            }
            if (!_headTracked)
            {
                // the head is lost: the view holds its last pose, the gameplay root is still untouched
                d.FromGameplayRoot = true;
                d.AllowBodyMotion = false;
                return d;
            }
            if (_seated)
            {
                // a seated player: the seat owns the body, so the head and the hands come from the seat pose
                d.FromGameplayRoot = true;
                d.AllowBodyMotion = false;
                return d;
            }
            d.FromViewRoot = target == VrRigTarget.Head;   // the head follows the camera
            d.FromGameplayRoot = !d.FromViewRoot;         // the hands follow their own controllers
            d.AllowBodyMotion = false;                     // the gameplay root never follows the headset
            return d;
        }

        /// The decision for the head IK target, the one that goes over the network.
        public VrRigDecision HeadDecision() { return Decide(VrRigTarget.Head); }

        /// True when the head IK target may be read from the camera rig. Only then may a headset pose reach the
        /// network; when it is false the head IK target is taken from the gameplay root.
        public bool HeadMayFollowCamera => HeadDecision().FromViewRoot;

        /// The reason the gameplay root is not moved, for the diagnostics bridge.
        public string PolicyReason
        {
            get
            {
                if (!_sessionActive) return "no session";
                if (!_headTracked) return "head not tracked";
                if (_seated) return "seated";
                return LocalViewOnly;
            }
        }
    }
}
