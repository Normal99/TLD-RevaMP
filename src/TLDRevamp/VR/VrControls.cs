using System;

namespace TLDRevamp.VR
{
    /// One button of one controller, with the edges worked out here rather than taken from the game.
    ///
    /// The game derives Down/Up from its own last-frame Hold and never clears them when a controller goes away.
    /// This one is fed once per frame and can be cleared, so a lost controller or an open menu releases cleanly.
    public sealed class VrButton
    {
        private bool _last;
        public bool Hold { get; private set; }
        public bool Down { get; private set; }
        public bool Up { get; private set; }

        public void Feed(bool pressed)
        {
            Down = pressed && !_last;
            Up = !pressed && _last;
            Hold = pressed;
            _last = pressed;
        }

        /// Forgets the button without an Up edge (an open menu, a lost controller).
        public void Clear() { _last = false; Hold = Down = Up = false; }
    }

    /// The buttons of one controller that the native VR controls read.
    public sealed class VrHandButtons
    {
        public readonly VrButton Trigger = new VrButton();
        public readonly VrButton Grip = new VrButton();
        public readonly VrButton Primary = new VrButton();     // A on the right controller, X on the left

        public void Feed(bool trigger, bool grip, bool primary)
        {
            Trigger.Feed(trigger);
            Grip.Feed(grip);
            Primary.Feed(primary);
        }

        public void Clear() { Trigger.Clear(); Grip.Clear(); Primary.Clear(); }

        public bool AnyDown => Trigger.Down || Grip.Down || Primary.Down;
        public bool AnyHold => Trigger.Hold || Grip.Hold || Primary.Hold;
    }

    /// What the game's input should read this frame, decided from both controllers.
    public struct VrGameInput
    {
        public bool Use, UseDown, UseUp;        // inputscript.use*: doors, buttons, seats, keys (the "E" of flat play)
        public bool Lmb, LmbDown, LmbUp;        // inputscript.lmb*: use the item in hand (drink, pour, fire)
        public bool Mount, MountDown, MountUp;  // inputscript.mount*: attach or detach a part
        public int Pointer;                     // the hand the game's interaction ray comes from: 0 left, 1 right
        public int GrabLeft, GrabRight;         // 1 when that hand's grip went down this frame
        public bool Release;                    // the holding hand let go this frame
    }

    /// The native VR controls, as one pure decision.
    ///
    ///   grip      grabs with that hand; letting go drops or throws what it holds
    ///   trigger   on a free hand: "use" what that hand points at; on the holding hand: use the held item
    ///   A / X     attach or detach a part, at what that hand points at
    ///
    /// The game's interaction ray comes from the hand that pressed something last, or from the holding hand while
    /// nothing else is pressed. A trigger press keeps the meaning it had when it went down until it comes up, so
    /// letting go of an item with the trigger still held never turns into a "use" of whatever is behind it.
    public sealed class VrControlMap
    {
        public const int Left = 0, Right = 1, None = -1;

        private int _pointer = Right;
        private readonly bool[] _triggerIsLmb = new bool[2];
        private readonly bool[] _triggerLive = new bool[2];

        public int Pointer => _pointer;

        /// holder: the hand holding an item, or None. menu: a menu is open (the game's own menu laser takes over).
        public VrGameInput Map(VrHandButtons left, VrHandButtons right, int holder, bool menu)
        {
            var r = new VrGameInput { Pointer = _pointer };
            if (left == null || right == null) return r;
            if (menu)
            {
                // a menu owns the controllers: nothing reaches the world, and nothing is left half pressed
                _triggerLive[0] = _triggerLive[1] = false;
                r.Pointer = _pointer;
                return r;
            }

            // the pointer: the hand that pressed something this frame (right wins a tie); while nothing is pressed,
            // the hand that holds the item; otherwise it stays where it was
            if (right.AnyDown) _pointer = Right;
            else if (left.AnyDown) _pointer = Left;
            else if (holder != None && !left.AnyHold && !right.AnyHold) _pointer = holder;
            r.Pointer = _pointer;

            var hands = new[] { left, right };
            for (int h = 0; h < 2; h++)
            {
                var t = hands[h].Trigger;
                if (t.Down)
                {
                    _triggerLive[h] = true;
                    _triggerIsLmb[h] = h == holder;
                }
                if (_triggerLive[h])
                {
                    if (_triggerIsLmb[h])
                    {
                        r.Lmb |= t.Hold; r.LmbDown |= t.Down; r.LmbUp |= t.Up;
                    }
                    else
                    {
                        r.Use |= t.Hold; r.UseDown |= t.Down; r.UseUp |= t.Up;
                    }
                }
                if (t.Up || !t.Hold) _triggerLive[h] = false;

                var p = hands[h].Primary;
                r.Mount |= p.Hold; r.MountDown |= p.Down; r.MountUp |= p.Up;
            }

            if (left.Grip.Down) r.GrabLeft = 1;
            if (right.Grip.Down) r.GrabRight = 1;
            if (holder != None && hands[holder].Grip.Up) r.Release = true;
            return r;
        }

        public void Reset()
        {
            _pointer = Right;
            _triggerLive[0] = _triggerLive[1] = false;
            _triggerIsLmb[0] = _triggerIsLmb[1] = false;
        }
    }

    /// How a held item comes to be released.
    public enum VrHoldMode
    {
        /// Grabbed with the grip: letting go of the grip releases it.
        Grip,
        /// Put into the hand by the game (a part detached with A/X): the next grip press takes it over, and letting go
        /// of that press releases it.
        Sticky,
    }

    /// The throw: the hand's speed over the last few frames, so a flick throws and a slow put-down only drops.
    public static class VrThrow
    {
        public const float MinSpeed = 1.2f;    // m/s: below this, letting go is a drop
        public const float MaxSpeed = 18f;     // m/s: a cap against a tracking glitch
        public const float Gain = 1.15f;       // a little more than the hand: the arm's last movement is lost in the hand

        /// The speed the item leaves the hand with, or 0 for a plain drop.
        public static float Speed(float handSpeed)
        {
            if (float.IsNaN(handSpeed) || handSpeed < MinSpeed) return 0f;
            return Math.Min(MaxSpeed, handSpeed * Gain);
        }
    }
}
