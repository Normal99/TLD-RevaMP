using System;

namespace TLDRevamp.VR
{
    /// The locomotion the VR input asked for this frame, before the seated rule.
    public struct VrMoveIntent
    {
        public float Forward;   // 0..1
        public float Backward;  // 0..1
        public float Left;      // 0..1
        public float Right;     // 0..1
        public bool Run;        // the stick was pushed past the run threshold
        public bool Jump;
        public bool Duck;

        public bool Any => Forward > 0f || Backward > 0f || Left > 0f || Right > 0f;
    }

    /// The locomotion the player may actually have, after the seated rule.
    public struct VrMoveAllowed
    {
        public float Forward, Backward, Left, Right;
        public bool Run, Jump, Duck;
        public bool Suppressed;   // the seated rule took the move away

        public bool Any => Forward > 0f || Backward > 0f || Left > 0f || Right > 0f;

        public override string ToString() =>
            "f=" + Forward + " b=" + Backward + " l=" + Left + " r=" + Right + " run=" + Run + " suppressed=" + Suppressed;
    }

    /// The seated locomotion suppression contract.
    ///
    /// Observed order (docs/VR-REWRITE.md): inputscript.Update, when mainscript.IsVR() is true, reads the left stick
    /// into forward/backward/left/right and the right stick into the look. The stick past 0.9 also sets run = true.
    /// fpscontroller then uses those values in GetDir / GetM / Move, and the move is applied to the character's
    /// Rigidbody — the gameplay root. A seated player therefore walks out of the seat with the left stick, and the
    /// walk is networked as a body move.
    ///
    /// The contract: while the player is seated, the stick may not move the body. The look (the right stick) and the
    /// seated controls stay untouched: the seat keeps its own look rules (seat.lookInsteadOfLean, seat.rotLimit).
    public sealed class VrLocomotionPolicy
    {
        private bool _seated;
        private bool _driving;
        private int _suppressedFrames;

        /// The player is seated. A seated player may not walk.
        public bool Seated { get { return _seated; } set { _seated = value; } }

        /// The player drives. A driver may not walk either, but the reason is different: the driver's own controls
        /// (gas, brake, steer) are the ones that matter and they are read from the same left stick.
        public bool Driving { get { return _driving; } set { _driving = value; } }

        /// How many frames the seated rule has taken a move away. A count, not a rate: a test can read it.
        public int SuppressedFrames { get { return _suppressedFrames; } }

        /// The locomotion the player gets. The seated rule takes the movement axes and the run away; jump and duck
        /// are the game's own seated rules (seat.lookInsteadOfLean and friends) and are left to the game.
        public VrMoveAllowed Apply(VrMoveIntent intent)
        {
            var outMove = new VrMoveAllowed
            {
                Forward = intent.Forward,
                Backward = intent.Backward,
                Left = intent.Left,
                Right = intent.Right,
                Run = intent.Run,
                Jump = intent.Jump,
                Duck = intent.Duck,
            };
            if (!_seated && !_driving)
                return outMove;

            outMove.Forward = 0f;
            outMove.Backward = 0f;
            outMove.Left = 0f;
            outMove.Right = 0f;
            outMove.Run = false;
            outMove.Suppressed = intent.Any || intent.Run;
            if (outMove.Suppressed) _suppressedFrames++;
            return outMove;
        }

        /// The seated player may still look: the look is not the body.
        public static VrMoveAllowed LookOnly(VrMoveIntent intent)
        {
            return new VrMoveAllowed { Jump = intent.Jump, Duck = intent.Duck };
        }

        public string Describe()
        {
            return "seated=" + _seated + " driving=" + _driving + " suppressed=" + _suppressedFrames;
        }
    }
}
