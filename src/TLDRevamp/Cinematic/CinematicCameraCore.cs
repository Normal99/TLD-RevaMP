using System;

namespace TLDRevamp.Cinematic
{
    /// What the cinematic camera is doing. Nothing here touches Unity: the state machine, the bounds and the
    /// teardown policy are plain numbers, so they can be tested offline (tools/unit/CinematicCameraTest.csproj)
    /// and reasoned about without a game running.
    public enum CinematicMode
    {
        Off = 0,
        Free = 1,   // fly: the camera goes where you tell it, and looks where the mouse points
        Orbit = 2,  // the camera circles a fixed target at a fixed distance
    }

    /// The camera's own limits. Every continuous quantity the user can change is clamped here, once, so that a
    /// key-repeat burst, a huge frame time or a hand-edited config cannot push the camera somewhere absurd.
    public static class CinematicLimits
    {
        // Movement speed, in metres per second. The slow factor divides, the fast factor multiplies.
        public const float MinSpeed = 0.25f;
        public const float MaxSpeed = 60f;
        public const float DefaultSpeed = 8f;
        public const float SlowFactor = 0.25f;
        public const float FastFactor = 3f;

        // Vertical field of view, in degrees. The game's own range is roughly 50..90 (settingsscript.s.S.FFieldOfViewFoot).
        public const float MinFov = 10f;
        public const float MaxFov = 120f;
        public const float DefaultFov = 60f;

        // Orbit distance from the target, in metres.
        public const float MinRadius = 1f;
        public const float MaxRadius = 400f;
        public const float DefaultRadius = 12f;

        // Orbit polar angle from the horizon, in degrees. Kept strictly inside ±90 so the view never flips over
        // the pole (the gimbal flip) and the up vector stays well defined.
        public const float MinPolar = -89f;
        public const float MaxPolar = 89f;
        public const float DefaultPolar = 20f;

        // Orbit azimuth, in degrees. Wraps, so it is not clamped but folded into [0, 360).
        public const float AzimuthPeriod = 360f;

        // How fast the orbit turns and how fast it zooms, in degrees per second and metres per second.
        public const float OrbitTurnSpeed = 120f;
        public const float OrbitZoomSpeed = 20f;

        // Mouse look, in degrees per second at full deflection.
        public const float LookSpeed = 180f;

        // The camera's pitch, in degrees. Bounded so the view never turns straight up or down into the ground.
        public const float MinPitch = -89f;
        public const float MaxPitch = 89f;

        /// How far above the ground the free camera may drop below its starting height. Keeps the camera out of
        /// the terrain without needing a raycast every frame.
        public const float MinHeightAboveGround = 0.2f;

        /// A frame may not move the camera further than this many metres, however long it was. A stall (a world
        /// build, a save load) must not fling the camera across the map.
        public const float MaxStepMetres = 12f;

        /// The largest frame time the camera integrates. Longer frames are treated as this, so a hitch cannot
        /// produce a huge jump.
        public const float MaxDeltaTime = 0.1f;

        /// How close the orbit target may be to the camera, in metres. Below this the camera would be inside the
        /// target's head.
        public const float MinTargetSeparation = 0.05f;

        public static float ClampSpeed(float value) => Clamp(value, MinSpeed, MaxSpeed);
        public static float ClampFov(float value) => Clamp(value, MinFov, MaxFov);
        public static float ClampRadius(float value) => Clamp(value, MinRadius, MaxRadius);
        public static float ClampPolar(float value) => Clamp(value, MinPolar, MaxPolar);
        public static float ClampPitch(float value) => Clamp(value, MinPitch, MaxPitch);
        public static float ClampDeltaTime(float value) =>
            float.IsNaN(value) || value < 0f ? 0f : (value > MaxDeltaTime ? MaxDeltaTime : value);

        /// The azimuth is an angle: it wraps instead of clamping, so turning past 360 keeps turning.
        public static float WrapAzimuth(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            float wrapped = value % AzimuthPeriod;
            if (wrapped < 0f) wrapped += AzimuthPeriod;
            return wrapped;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;   // a NaN would otherwise poison every later value
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// Who owns the cursor, and when the camera may take it back.
    ///
    /// Three components in this mod want the cursor at different times: the marks panel, the chat box and the
    /// join-loading screen each capture it, force it their own way and restore it when they close. If the
    /// cinematic camera were a fourth independent capture/restore pair, the last one to close would win and the
    /// others would be left with a cursor they did not expect (a locked cursor over an open panel, or a visible
    /// cursor in the middle of a filmed shot).
    ///
    /// The rule the camera follows: it takes the cursor only while nobody else wants it, and it gives it back
    /// only while the cursor is still the one it set. If a panel has taken it in the meantime, the panel's state
    /// is left exactly as it is.
    public static class CinematicCursorPolicy
    {
        /// The cursor states the game uses (Unity's CursorLockMode): 0 = None, 1 = Locked, 2 = Confined.
        public const int LockNone = 0;
        public const int LockLocked = 1;

        /// The camera may take the cursor only when no other panel holds it.
        public static bool MayTake(bool marksPanelOpen, bool chatTyping, bool joinLoadingVisible) =>
            !marksPanelOpen && !chatTyping && !joinLoadingVisible;

        /// The camera may restore the cursor only when the cursor is still exactly the one it set. If it is not
        /// (a panel has since unlocked it or shown it), that panel owns the cursor now and the camera must not
        /// touch it.
        public static bool MayRestore(int currentLockState, bool currentVisible, int ownedLockState, bool ownedVisible) =>
            currentLockState == ownedLockState && currentVisible == ownedVisible;

        /// The state the camera puts the cursor into while it drives.
        public static void ApplyTake(ref int lockState, ref bool visible)
        {
            lockState = LockLocked;
            visible = false;
        }
    }

    /// The orbit geometry, as plain numbers.
    ///
    /// The orbit camera circles a target. Its position comes from three numbers: the azimuth (where it is around
    /// the target), the polar angle (how high above the horizon it is) and the radius (how far away it is).
    ///
    /// The convention, which the tests pin down because getting it backwards is invisible until someone uses it:
    /// the camera behaves like a free camera that always keeps the target in the middle of the frame. Looking
    /// right sweeps the view to the right across the subject, and looking up raises the camera so it looks down at
    /// the subject.
    public static class CinematicOrbit
    {
        /// The camera's offset from the target, in the game's own axes (+X right, +Y up, +Z forward).
        public static void Offset(float azimuthDegrees, float polarDegrees, float radius,
                                  out float x, out float y, out float z)
        {
            double azimuth = azimuthDegrees * Math.PI / 180.0;
            double polar = polarDegrees * Math.PI / 180.0;
            x = (float)(Math.Cos(polar) * Math.Sin(azimuth) * radius);
            y = (float)(Math.Sin(polar) * radius);
            z = (float)(Math.Cos(polar) * Math.Cos(azimuth) * radius);
        }

        /// The camera's position: the target plus the offset.
        public static void Position(float targetX, float targetY, float targetZ,
                                    float azimuthDegrees, float polarDegrees, float radius,
                                    out float x, out float y, out float z)
        {
            Offset(azimuthDegrees, polarDegrees, radius, out float ox, out float oy, out float oz);
            x = targetX + ox; y = targetY + oy; z = targetZ + oz;
        }

        /// How the orbit follows the look input. Looking right sweeps the view right across the subject, which
        /// carries the camera to its own left around the target; looking up raises the camera.
        public static void Follow(float lookYaw, float lookPitch, float turnFactor,
                                  ref float azimuth, ref float polar)
        {
            azimuth = CinematicLimits.WrapAzimuth(azimuth + lookYaw * turnFactor);
            polar = CinematicLimits.ClampPolar(polar + lookPitch * turnFactor);
        }
    }

    /// What the camera does with the player's own movement keys.
    ///
    /// The camera flies with the keys the character walks with. While the camera is on, those keys belong to the
    /// camera: the player must not move, because the player's position is what the network reports and every
    /// other player would see this one walk away mid-take.
    ///
    /// The rule is here, in the pure core, so it can be tested: the camera reads the values the player had, and
    /// the player is given zeroes. The two always agree about what was pressed.
    public static class CinematicInputPolicy
    {
        /// The axes the player's movement uses, and the camera's own take of them.
        public struct Axes
        {
            public float Forward, Backward, Left, Right;
        }

        /// What the camera reads: the axes the player had this frame.
        public static Axes ForCamera(float forward, float backward, float left, float right) =>
            new Axes { Forward = forward, Backward = backward, Left = left, Right = right };

        /// What the player is left with: nothing, so the character does not walk while the camera flies.
        public static Axes ForPlayer(float forward, float backward, float left, float right) =>
            new Axes { Forward = 0f, Backward = 0f, Left = 0f, Right = 0f };

        /// The camera's net movement on the horizontal plane: forward minus back, right minus left.
        public static void Movement(in Axes axes, out float forward, out float right)
        {
            forward = axes.Forward - axes.Backward;
            right = axes.Right - axes.Left;
        }

        /// The speed multiplier from the modifier keys: Shift hurries, Ctrl creeps.
        public static float SpeedFactor(bool run, bool duck)
        {
            if (run) return CinematicLimits.FastFactor;
            if (duck) return CinematicLimits.SlowFactor;
            return 1f;
        }
    }

    /// How the bridge treats the cinematic camera's commands.
    ///
    /// The bridge has one rule that decides everything: a command that changes nothing is a *query* and may be
    /// run by anyone; a command that changes something is a *mutation* and may only be run when the host's
    /// developer policy allows it. Getting this wrong in either direction is bad: a mutation classified as a
    /// query would let any joiner move the camera on a machine it does not own, and a query classified as a
    /// mutation would make the camera's status unaskable for a joiner (the test tooling needs it).
    ///
    /// The camera's status is the only query. Everything else — switching modes, changing the speed, the field of
    /// view, the orbit distance, loading a shot list — changes what this machine shows.
    public static class CinematicCommandPolicy
    {
        /// Whether the command only reads. `arg` is everything after the command name.
        ///
        /// Only the status is a query. A screenshot is a mutation even though it changes nothing on screen: it
        /// writes a file, and a file is something a machine should only write when its owner says so.
        public static bool IsReadOnly(string arg)
        {
            if (arg == null) return true;
            arg = arg.Trim();
            if (arg.Length == 0) return true;                       // `cinema` on its own: the status
            if (arg == "status") return true;
            if (arg.StartsWith("status ", StringComparison.Ordinal)) return true;
            return false;
        }

        /// Whether the command would be allowed, given the host's policy. The host's own policy is not re-implemented
        /// here: the caller passes in what the bridge would allow.
        /// <param name="readOnly">whether the command is a query.</param>
        /// <param name="developerAccessAllowed">whether the host's policy lets this machine run mutations.</param>
        public static bool Allowed(bool readOnly, bool developerAccessAllowed) => readOnly || developerAccessAllowed;
    }

    /// One axis of the free camera's motion, in the camera's own frame: forward, right and up.
    public struct CinematicMove
    {
        public float Forward;   // +1 forward, -1 back
        public float Right;     // +1 right, -1 left
        public float Up;        // +1 up, -1 down
    }

    /// The camera's orientation, kept as angles so it can be clamped without a quaternion (and so it survives a
    /// save/restore round trip through the bridge).
    public struct CinematicLook
    {
        public float Yaw;    // degrees, around the world up axis
        public float Pitch;  // degrees, positive looks up
    }

    /// The moments the camera must put itself away, and whether it may run at all.
    ///
    /// The camera is a local view of the world, and the world can end under it: the player dies, the world is
    /// replaced, a session stops, the game is in VR, a menu is open, the world is still loading. Each of these
    /// either refuses the camera or takes it down.
    ///
    /// The rule is here, in the pure core, so it can be tested: the camera runs only in a world that is ready, and
    /// every teardown is idempotent (a death and then a scene change both fire; the second must change nothing).
    public static class CinematicLifecyclePolicy
    {
        /// Why the camera may not run, or null when it may. The order matters: the first reason is the one the
        /// player is told, and the most fundamental one comes first.
        public static string Refusal(bool noWorld, bool noPlayer, bool mainMenu, bool dead, bool vr,
                                     bool joinLoading, bool sceneTearingDown, bool menuOpen)
        {
            if (noWorld) return "there is no world yet";
            if (noPlayer) return "there is no player yet";
            if (mainMenu) return "the camera is not available in the main menu";
            if (dead) return "the camera is not available while you are dead";
            if (vr) return "the camera is not available in VR";
            if (joinLoading) return "the camera is not available while the world is still loading";
            if (sceneTearingDown) return "the camera is not available while the world is changing";
            if (menuOpen) return "the camera is not available while a menu is open";
            return null;
        }

        /// Whether the camera may be turned on right now.
        public static bool MayStart(string refusal) => refusal == null;

        /// Whether the camera must be taken down right now. A teardown is idempotent: running it twice is the same
        /// as running it once, so the caller does not need to know whether it has already run.
        public static bool MustTearDown(string refusal) => refusal != null;

        /// The reasons the camera gives for being taken down. They are the same ones the refusal uses, so the
        /// player is told why the camera went away.
        public static readonly string[] TeardownReasons =
        {
            "death", "respawn", "scene load", "main menu", "session stop", "game quit", "world change",
        };
    }

    /// The whole camera state, without Unity. The Unity adapter owns a Camera component and a transform; this owns
    /// what the camera should look like and where it is allowed to go.
    public sealed class CinematicState
    {
        public CinematicMode Mode = CinematicMode.Off;
        public float Speed = CinematicLimits.DefaultSpeed;
        public float Fov = CinematicLimits.DefaultFov;
        public float Radius = CinematicLimits.DefaultRadius;
        public float Polar = CinematicLimits.DefaultPolar;   // degrees
        public float Azimuth = CinematicLimits.WrapAzimuth(0f);
        public CinematicLook Look;

        /// The orbit target, in the camera's own coordinate space (see CinematicRig: the camera's world position
        /// plus this offset). Keeping the target relative means a world shift does not need to rewrite it.
        public double TargetX, TargetY, TargetZ;

        /// Set while the camera is between "asked for" and "actually on screen": the rig may still be building.
        public bool Pending;

        /// How the camera was entered, so a teardown can undo exactly what this session changed.
        public bool OwnsCursor;
        /// Whether the camera has remembered the cursor state it took over (so it can give it back the same way).
        public bool CursorRemembered;
        public bool CursorWasVisible;
        public int CursorWasLockState;      // CursorLockMode as an int, so this file needs no Unity
        public bool SavedGameCameraEnabled;

        /// Counts of the things a test wants to see: how often the camera was turned on, off, and re-targeted.
        public long Activations, Deactivations, Recentres;

        public bool Active => Mode != CinematicMode.Off;

        /// Applies every bound at once. Called after any user input, so the state can never hold a value the
        /// limits would refuse.
        public void Normalize()
        {
            Speed = CinematicLimits.ClampSpeed(Speed);
            Fov = CinematicLimits.ClampFov(Fov);
            Radius = CinematicLimits.ClampRadius(Radius);
            Polar = CinematicLimits.ClampPolar(Polar);
            Look.Pitch = CinematicLimits.ClampPitch(Look.Pitch);
            Azimuth = CinematicLimits.WrapAzimuth(Azimuth);
            if (double.IsNaN(TargetX) || double.IsInfinity(TargetX)) TargetX = 0;
            if (double.IsNaN(TargetY) || double.IsInfinity(TargetY)) TargetY = 0;
            if (double.IsNaN(TargetZ) || double.IsInfinity(TargetZ)) TargetZ = 0;
        }

        /// The mode names the bridge and the settings accept. Anything else is refused, so a typo cannot leave the
        /// camera in an unknown state.
        public static bool TryParseMode(string text, out CinematicMode mode)
        {
            mode = CinematicMode.Off;
            if (string.IsNullOrEmpty(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "off": mode = CinematicMode.Off; return true;
                case "free": mode = CinematicMode.Free; return true;
                case "orbit": mode = CinematicMode.Orbit; return true;
                default: return false;
            }
        }

        public static string ModeName(CinematicMode mode)
        {
            switch (mode)
            {
                case CinematicMode.Free: return "free";
                case CinematicMode.Orbit: return "orbit";
                default: return "off";
            }
        }
    }
}
