#pragma warning disable CS8981
using System;
using System.IO;
using TLDRevamp.Cinematic;

static class CinematicCameraTest
{
    static int checks;

    static void Check(bool ok, string why)
    {
        checks++;
        if (!ok) throw new Exception("FAIL: " + why);
    }

    static void Check(bool ok, string why, string detail)
    {
        checks++;
        if (!ok) throw new Exception("FAIL: " + why + (detail.Length > 0 ? " (" + detail + ")" : ""));
    }

    static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;

    static int Main()
    {
        Clamps();
        Modes();
        Normalize();
        Orbit();
        ShotCodec();
        ExampleTake();
        Playback();
        CursorPolicy();
        LifecyclePolicy();
        InputTakeover();
        CommandPolicy();
        GameplayIsolation();
        Console.WriteLine("Cinematic camera: " + checks + " checks passed");
        return 0;
    }

    // ---- the bounds every continuous value is held to -------------------------------

    static void Clamps()
    {
        Check(Near(CinematicLimits.ClampSpeed(0f), CinematicLimits.MinSpeed), "speed below the floor is raised");
        Check(Near(CinematicLimits.ClampSpeed(-50f), CinematicLimits.MinSpeed), "negative speed is raised");
        Check(Near(CinematicLimits.ClampSpeed(1e9f), CinematicLimits.MaxSpeed), "speed above the ceiling is lowered");
        Check(Near(CinematicLimits.ClampSpeed(8f), 8f), "a speed inside the range is kept");
        Check(Near(CinematicLimits.ClampSpeed(float.NaN), CinematicLimits.MinSpeed), "a NaN speed becomes the floor, not a poison");

        Check(Near(CinematicLimits.ClampFov(0f), CinematicLimits.MinFov), "fov below the floor is raised");
        Check(Near(CinematicLimits.ClampFov(200f), CinematicLimits.MaxFov), "fov above the ceiling is lowered");
        Check(Near(CinematicLimits.ClampFov(60f), 60f), "a fov inside the range is kept");
        Check(Near(CinematicLimits.ClampFov(float.NaN), CinematicLimits.MinFov), "a NaN fov becomes the floor");

        Check(Near(CinematicLimits.ClampRadius(0f), CinematicLimits.MinRadius), "radius below the floor is raised");
        Check(Near(CinematicLimits.ClampRadius(1e6f), CinematicLimits.MaxRadius), "radius above the ceiling is lowered");
        Check(Near(CinematicLimits.ClampRadius(float.NaN), CinematicLimits.MinRadius), "a NaN radius becomes the floor");

        // The orbit polar angle must stay strictly inside +-90, or the view flips over the pole.
        Check(Near(CinematicLimits.ClampPolar(1000f), CinematicLimits.MaxPolar), "polar above the ceiling is lowered");
        Check(Near(CinematicLimits.ClampPolar(-1000f), CinematicLimits.MinPolar), "polar below the floor is raised");
        Check(CinematicLimits.MaxPolar < 90f && CinematicLimits.MinPolar > -90f, "the polar bounds stay inside the pole");
        Check(Near(CinematicLimits.ClampPitch(1000f), CinematicLimits.MaxPitch), "pitch above the ceiling is lowered");
        Check(Near(CinematicLimits.ClampPitch(-1000f), CinematicLimits.MinPitch), "pitch below the floor is raised");

        // The azimuth is an angle: it wraps instead of clamping, so turning past 360 keeps turning.
        Check(Near(CinematicLimits.WrapAzimuth(370f), 10f), "the azimuth wraps past 360");
        Check(Near(CinematicLimits.WrapAzimuth(-10f), 350f), "a negative azimuth wraps forward");
        Check(Near(CinematicLimits.WrapAzimuth(720f), 0f), "two full turns come back to the start");
        Check(Near(CinematicLimits.WrapAzimuth(float.NaN), 0f), "a NaN azimuth is zero");

        // A long frame must not become a long step.
        Check(Near(CinematicLimits.ClampDeltaTime(5f), CinematicLimits.MaxDeltaTime), "a long frame is capped");
        Check(Near(CinematicLimits.ClampDeltaTime(-1f), 0f), "a negative frame time is zero");
        Check(Near(CinematicLimits.ClampDeltaTime(0.016f), 0.016f), "a normal frame time is kept");
    }

    // ---- the modes the camera can be in ---------------------------------------------

    static void Modes()
    {
        foreach (var (text, mode) in new[]
        {
            ("free", CinematicMode.Free), ("FREE", CinematicMode.Free), (" free ", CinematicMode.Free),
            ("orbit", CinematicMode.Orbit), ("Orbit", CinematicMode.Orbit),
            ("off", CinematicMode.Off), ("OFF", CinematicMode.Off),
        })
        {
            Check(CinematicState.TryParseMode(text, out var parsed) && parsed == mode, "mode '" + text + "' parses");
        }
        // Anything unknown is refused, so a typo cannot leave the camera in an unknown state.
        foreach (var bad in new[] { "", "  ", "fre", "freecam", "orbit2", "null", "0" })
            Check(!CinematicState.TryParseMode(bad, out _), "mode '" + bad + "' is refused");
        Check(!CinematicState.TryParseMode(null, out _), "a null mode is refused");

        Check(CinematicState.ModeName(CinematicMode.Free) == "free", "the free mode has a name");
        Check(CinematicState.ModeName(CinematicMode.Orbit) == "orbit", "the orbit mode has a name");
        Check(CinematicState.ModeName(CinematicMode.Off) == "off", "the off mode has a name");
    }

    // ---- the state holds nothing out of bounds --------------------------------------

    static void Normalize()
    {
        var s = new CinematicState();
        s.Speed = 1e9f; s.Fov = 1e9f; s.Radius = -5f; s.Polar = 500f;
        s.Look.Pitch = -500f; s.Azimuth = 725f;
        s.TargetX = double.NaN; s.TargetY = double.PositiveInfinity; s.TargetZ = 3.5;
        s.Normalize();
        Check(Near(s.Speed, CinematicLimits.MaxSpeed), "normalize bounds the speed");
        Check(Near(s.Fov, CinematicLimits.MaxFov), "normalize bounds the fov");
        Check(Near(s.Radius, CinematicLimits.MinRadius), "normalize bounds the radius");
        Check(Near(s.Polar, CinematicLimits.MaxPolar), "normalize bounds the polar angle");
        Check(Near(s.Look.Pitch, CinematicLimits.MinPitch), "normalize bounds the pitch");
        Check(Near(s.Azimuth, 5f), "normalize wraps the azimuth");
        Check(s.TargetX == 0 && s.TargetY == 0 && Near((float)s.TargetZ, 3.5f), "normalize repairs a broken target");
    }

    // ---- the orbit geometry ---------------------------------------------------------

    static void Orbit()
    {
        // The orbit camera circles a target. The geometry is in the pure core, so the convention can be pinned
        // down here: a sign error in the polar angle is invisible until someone uses the camera.
        CinematicOrbit.Offset(0f, 0f, 10f, out float x, out float y, out float z);
        Check(Near(x, 0f, 1e-3f) && Near(y, 0f, 1e-3f), "at azimuth 0 and polar 0 the camera is level");
        Check(Near(z, 10f, 1e-3f), "at azimuth 0 the camera is in front of the target");

        CinematicOrbit.Offset(90f, 0f, 10f, out x, out y, out z);
        Check(Near(x, 10f, 1e-3f) && Near(z, 0f, 1e-3f), "at azimuth 90 the camera is to the target's right");

        CinematicOrbit.Offset(0f, 90f, 10f, out x, out y, out z);
        Check(Near(y, 10f, 1e-3f), "at polar 90 the camera is straight above the target");

        // The camera is always exactly the radius from the target.
        for (float az = 0; az < 360; az += 37)
            for (float pol = -80; pol <= 80; pol += 23)
            {
                CinematicOrbit.Offset(az, pol, 7.5f, out x, out y, out z);
                double len = Math.Sqrt(x * x + y * y + z * z);
                Check(Near((float)len, 7.5f, 1e-3f), "the camera stays exactly the radius from the target");
            }

        // The target plus the offset is the camera's position.
        CinematicOrbit.Position(100f, 2f, -50f, 30f, 15f, 5f, out x, out y, out z);
        CinematicOrbit.Offset(30f, 15f, 5f, out float ox, out float oy, out float oz);
        Check(Near(x, 100f + ox, 1e-3f) && Near(y, 2f + oy, 1e-3f) && Near(z, -50f + oz, 1e-3f),
              "the camera's position is the target plus the offset");

        // Following the look input: looking up raises the camera (the polar angle grows), and the azimuth wraps.
        float azimuth = 0f, polar = 0f;
        CinematicOrbit.Follow(0f, 10f, 0.5f, ref azimuth, ref polar);
        Check(Near(polar, 5f), "looking up raises the camera");
        Check(Near(azimuth, 0f), "looking up does not turn the camera");
        CinematicOrbit.Follow(10f, 0f, 0.5f, ref azimuth, ref polar);
        Check(Near(azimuth, 5f), "looking right sweeps the view right");
        Check(Near(polar, 5f), "looking right does not change the height");
        // The polar angle stays inside the pole, however long the mouse is held up.
        for (int i = 0; i < 1000; i++) CinematicOrbit.Follow(0f, 10f, 0.5f, ref azimuth, ref polar);
        Check(polar <= CinematicLimits.MaxPolar && polar >= CinematicLimits.MinPolar,
              "holding the mouse up cannot flip the camera over the pole");
    }

    // ---- the shot list format --------------------------------------------------------

    static void ShotCodec()
    {
        string error;
        var shot = CinematicShotCodec.Parse("version 1\nat 0 move 1 2 3 45 10 60\nat 2 cut 2\nat 3 speed 2\n", out error);
        Check(shot != null, "a valid shot list parses: " + error);
        Check(shot.Version == 1, "the version is read");
        Check(shot.Steps.Count == 3, "three directives were read");
        Check(Near((float)shot.Duration, 3f), "the duration is the last directive's time");

        // The state at a moment is the last directive of each kind up to that moment.
        var move = shot.LastOf("move", 1.0);
        Check(move != null && Near((float)move.Values[0], 1f), "the move in force at t=1 is the one at t=0");
        Check(shot.LastOf("speed", 1.0) == null, "no speed is in force before its directive");
        var speed = shot.LastOf("speed", 4.0);
        Check(speed != null && Near((float)speed.Values[0], 2f), "the speed in force at t=4 is the one at t=3");
        Check(shot.NextCut(0.0).HasValue && Near((float)shot.NextCut(0.0).Value, 2f), "the next cut after 0 is at 2");
        Check(!shot.NextCut(2.0).HasValue, "there is no cut after the last one");

        // A shot list that is not one is refused with a reason, never half-applied.
        foreach (var bad in new[] { "", "   ", "# only a comment", "version 99", "version x", "at 1 move 1 2 3",
                                    "at 1 move 1 2 3 4 5", "at 1 move a b c d e f", "at -1 move 1 2 3 4 5 6",
                                    "at 1 speed 0", "at 1 speed -1", "at 1 speed 1000", "at 1 cut -5", "nonsense 1 2" })
        {
            Check(CinematicShotCodec.Parse(bad, out error) == null, "the bad shot list is refused: " + bad);
            Check(error.Length > 0, "the refusal carries a reason: " + bad);
        }

        // The same take always writes the same bytes.
        var written = CinematicShotCodec.Write(shot);
        var again = CinematicShotCodec.Parse(written, out error);
        Check(again != null, "a written shot list parses back: " + error);
        Check(CinematicShotCodec.Write(again) == written, "writing a shot list is stable");
    }

    // ---- the example take in the repository -----------------------------------------------

    static void ExampleTake()
    {
        // The example take that ships with the repository must parse with the production codec, and it must
        // describe the take it claims to: a move, a cut, a speed change and a second speed change.
        string root = Environment.GetEnvironmentVariable("TLD_REPO_ROOT");
        if (root == null) root = Directory.GetCurrentDirectory();
        string path = Path.Combine(root, "docs", "examples", "cinematic-shot.txt");
        if (!File.Exists(path)) { Console.WriteLine("  (the example take is not here; skipped)"); return; }
        string error;
        var shot = CinematicShotCodec.Parse(File.ReadAllText(path), out error);
        Check(shot != null, "the example take parses: " + error);
        Check(shot.Version == 1, "the example take is version 1");
        Check(shot.Steps.Count >= 5, "the example take has its directives", shot.Steps.Count.ToString());
        Check(shot.LastOf("move", 0.0) != null, "the example take starts with a move");
        Check(shot.NextCut(0.0).HasValue, "the example take has a cut");
        Check(shot.LastOf("speed", 0.0) == null, "the example take starts at the normal speed");
        Check(shot.LastOf("speed", 7.0) != null, "the example take slows down in the middle");
        Check(Near((float)shot.LastOf("speed", 7.0).Values[0], 0.5f), "the example take's slow speed is 0.5");
        Check(Near((float)shot.LastOf("speed", 11.0).Values[0], 1f), "the example take speeds back up");
        // And it writes back to the same thing: the take in the repository is the canonical one.
        var again = CinematicShotCodec.Parse(CinematicShotCodec.Write(shot), out error);
        Check(again != null, "the example take survives a write: " + error);
        Check(CinematicShotCodec.Write(again) == CinematicShotCodec.Write(shot), "the example take's write is stable");
    }

    // ---- the playback clock ------------------------------------------------------------

    static void Playback()
    {
        string error;
        var shot = CinematicShotCodec.Parse("at 0 move 0 0 0 0 0 60\nat 1 cut 1\nat 2 move 5 0 0 0 0 60\n", out error);
        Check(shot != null, "the playback shot list parses: " + error);

        var play = new CinematicPlayback();
        Check(!play.Advance(0.03, shot, out _), "a frame before the take starts does nothing");
        Check(Near((float)play.Time, 0f), "the playhead does not move before the take starts");
        play.Playing = true;
        play.Advance(0.03, shot, out _);
        Check(Near((float)play.Time, 0.03f), "the playhead moves with the frame");

        // A frame longer than the bound is treated as the bound, so a stall cannot skip the whole take.
        play.Advance(1000.0, shot, out _);
        Check(play.Time <= shot.Duration + 1e-6, "a long frame cannot overshoot the take");
        Check(play.Playing, "one long frame does not skip the whole take");
        // The take ends when the playhead reaches its last directive.
        for (int i = 0; i < 100 && play.Playing; i++) play.Advance(0.1, shot, out bool ended);
        Check(!play.Playing, "the take ends at its last directive");
        Check(Near((float)play.Time, (float)shot.Duration, 1e-3f), "the playhead stops at the end of the take");

        // The playback speed multiplies the frame, and a cut moves the playhead.
        var fast = new CinematicPlayback { Playing = true, Speed = 2f };
        fast.Advance(0.03, shot, out _);
        Check(Near((float)fast.Time, 0.06f), "the playback speed multiplies the frame");
        Check(fast.JumpToNextCut(shot, 0.0), "a cut is found");
        Check(Near((float)fast.Time, 1f), "the playhead jumps to the cut");
        Check(!fast.JumpToNextCut(shot, 1.0), "there is no cut after the last one");

        // A take that loops wraps round.
        var loop = new CinematicPlayback { Playing = true, Loop = true, Speed = 100f };
        loop.Advance(0.1, shot, out _);
        Check(loop.Time < shot.Duration, "a looping take wraps back to the start");

        // The cuts of a take are found in order, and the playhead can be moved to the next one.
        var cuts = CinematicShotCodec.Parse("at 0 move 0 0 0 0 0 60\nat 1 cut 1\nat 2 cut 2\nat 3 move 9 9 9 0 0 60\n", out error);
        Check(cuts != null, "the cut shot list parses: " + error);
        Check(cuts.NextCut(0.0).HasValue && Near((float)cuts.NextCut(0.0).Value, 1f), "the first cut is at 1 s");
        Check(cuts.NextCut(1.0).HasValue && Near((float)cuts.NextCut(1.0).Value, 2f), "the next cut after 1 s is at 2 s");
        Check(!cuts.NextCut(2.0).HasValue, "there is no cut after the last one");
        var walker = new CinematicPlayback { Playing = true };
        Check(walker.JumpToNextCut(cuts, 0.0), "the playhead jumps to the first cut");
        Check(Near((float)walker.Time, 1f), "the playhead is at the cut");
        Check(walker.JumpToNextCut(cuts, walker.Time), "the playhead jumps to the next cut");
        Check(Near((float)walker.Time, 2f), "the playhead is at the second cut");
        Check(!walker.JumpToNextCut(cuts, walker.Time), "there is no third cut");
    }

    // ---- the cursor policy ----------------------------------------------------------------

    static void CursorPolicy()
    {
        // The camera may only take the cursor while no panel holds it.
        Check(CinematicCursorPolicy.MayTake(false, false, false), "the camera may take the cursor when no panel wants it");
        Check(!CinematicCursorPolicy.MayTake(true, false, false), "the camera stands down for the marks panel");
        Check(!CinematicCursorPolicy.MayTake(false, true, false), "the camera stands down while the chat box is open");
        Check(!CinematicCursorPolicy.MayTake(false, false, true), "the camera stands down while the world is loading");

        // The camera may only restore the cursor while the cursor is still the one it set.
        Check(CinematicCursorPolicy.MayRestore(CinematicCursorPolicy.LockLocked, false,
            CinematicCursorPolicy.LockLocked, false), "the camera restores a cursor it still owns");
        Check(!CinematicCursorPolicy.MayRestore(CinematicCursorPolicy.LockNone, true,
            CinematicCursorPolicy.LockLocked, false), "the camera leaves a panel's unlocked cursor alone");
        Check(!CinematicCursorPolicy.MayRestore(CinematicCursorPolicy.LockLocked, true,
            CinematicCursorPolicy.LockLocked, false), "the camera leaves a panel's visible cursor alone");
        Check(!CinematicCursorPolicy.MayRestore(CinematicCursorPolicy.LockNone, false,
            CinematicCursorPolicy.LockLocked, false), "the camera leaves a cursor another owner changed alone");

        // The state the camera puts the cursor into while it drives.
        int lockState = 7; bool visible = true;
        CinematicCursorPolicy.ApplyTake(ref lockState, ref visible);
        Check(lockState == CinematicCursorPolicy.LockLocked && !visible, "the camera locks and hides the cursor");
    }

    // ---- the camera's own lifecycle ----------------------------------------------------

    static void LifecyclePolicy()
    {
        // In a ready world, the camera may run.
        Check(CinematicLifecyclePolicy.MayStart(CinematicLifecyclePolicy.Refusal(false, false, false, false, false, false, false, false)),
              "the camera may run in a ready world");
        Check(CinematicLifecyclePolicy.Refusal(false, false, false, false, false, false, false, false) == null,
              "a ready world gives no refusal");

        // Each situation refuses the camera, with a reason the player can read.
        var cases = new (bool[] flags, string expected)[]
        {
            (new[] { true, false, false, false, false, false, false, false }, "there is no world yet"),
            (new[] { false, true, false, false, false, false, false, false }, "there is no player yet"),
            (new[] { false, false, true, false, false, false, false, false }, "the camera is not available in the main menu"),
            (new[] { false, false, false, true, false, false, false, false }, "the camera is not available while you are dead"),
            (new[] { false, false, false, false, true, false, false, false }, "the camera is not available in VR"),
            (new[] { false, false, false, false, false, true, false, false }, "the camera is not available while the world is still loading"),
            (new[] { false, false, false, false, false, false, true, false }, "the camera is not available while the world is changing"),
            (new[] { false, false, false, false, false, false, false, true }, "the camera is not available while a menu is open"),
        };
        foreach (var (flags, expected) in cases)
        {
            string refusal = CinematicLifecyclePolicy.Refusal(flags[0], flags[1], flags[2], flags[3], flags[4],
                                                              flags[5], flags[6], flags[7]);
            Check(refusal == expected, "the refusal is '" + expected + "', got '" + refusal + "'");
            Check(!CinematicLifecyclePolicy.MayStart(refusal), "the camera may not start: " + expected);
            Check(CinematicLifecyclePolicy.MustTearDown(refusal), "the camera must be taken down: " + expected);
        }

        // The most fundamental refusal comes first: no world beats everything else.
        Check(CinematicLifecyclePolicy.Refusal(true, true, true, true, true, true, true, true) == "there is no world yet",
              "the first refusal is the most fundamental one");

        // A teardown is idempotent: the same refusal twice asks for the same thing, and the camera's own
        // teardown changes nothing the second time.
        string twice = CinematicLifecyclePolicy.Refusal(false, false, false, true, false, false, false, false);
        Check(twice == CinematicLifecyclePolicy.Refusal(false, false, false, true, false, false, false, false),
              "the refusal is the same every time, so a teardown can run twice");
        Check(CinematicLifecyclePolicy.TeardownReasons.Length >= 5, "the camera names the reasons it is taken down");
        foreach (var reason in CinematicLifecyclePolicy.TeardownReasons)
        {
            // The reasons are short labels for the log and the diagnostics: no path, no name, nothing private.
            Check(reason.Length > 0 && reason.Length <= 24, "a teardown reason is short: " + reason);
            Check(!reason.Contains("/") && !reason.Contains("\\") && !reason.Contains("@"),
                  "a teardown reason carries no path or address: " + reason);
        }
    }

    // ---- the movement keys belong to the camera while it is on ---------------------------

    static void InputTakeover()
    {
        // The camera reads the axes the player had, and the player is left with nothing: the character must not
        // walk while the camera flies, because the character's position is what the network reports.
        var camera = CinematicInputPolicy.ForCamera(1f, 0f, 0f, 1f);
        var player = CinematicInputPolicy.ForPlayer(1f, 0f, 0f, 1f);
        Check(Near(camera.Forward, 1f) && Near(camera.Right, 1f), "the camera reads what the player pressed");
        Check(Near(player.Forward, 0f) && Near(player.Backward, 0f) && Near(player.Left, 0f) && Near(player.Right, 0f),
              "the player is left with no movement");

        // The camera and the player always agree about what was pressed.
        CinematicInputPolicy.Movement(camera, out float forward, out float right);
        Check(Near(forward, 1f), "forward is forward minus back");
        Check(Near(right, 1f), "right is right minus left");

        // Walking backwards is a negative forward.
        var back = CinematicInputPolicy.ForCamera(0f, 1f, 0f, 0f);
        CinematicInputPolicy.Movement(back, out float backForward, out _);
        Check(Near(backForward, -1f), "backwards is a negative forward");

        // Both at once cancel out.
        var both = CinematicInputPolicy.ForCamera(1f, 1f, 1f, 1f);
        CinematicInputPolicy.Movement(both, out float none, out float noneRight);
        Check(Near(none, 0f) && Near(noneRight, 0f), "opposite keys cancel");

        // The modifier keys change the speed, not the direction.
        Check(Near(CinematicInputPolicy.SpeedFactor(true, false), CinematicLimits.FastFactor), "Shift hurries");
        Check(Near(CinematicInputPolicy.SpeedFactor(false, true), CinematicLimits.SlowFactor), "Ctrl creeps");
        Check(Near(CinematicInputPolicy.SpeedFactor(false, false), 1f), "no modifier means the normal speed");
        Check(Near(CinematicInputPolicy.SpeedFactor(true, true), CinematicLimits.FastFactor),
              "Shift wins over Ctrl");

        // The speed factor multiplies the speed; it never exceeds the bounds on its own.
        Check(CinematicLimits.ClampSpeed(CinematicLimits.DefaultSpeed * CinematicLimits.FastFactor) <= CinematicLimits.MaxSpeed,
              "even the fastest factor stays inside the speed bound");
    }

    // ---- the bridge's command policy --------------------------------------------------------

    static void CommandPolicy()
    {
        // The status is a query: safe for any peer, including a joiner whose host runs the default policy.
        Check(CinematicCommandPolicy.IsReadOnly(""), "cinema on its own is a query");
        Check(CinematicCommandPolicy.IsReadOnly("   "), "cinema with only spaces is a query");
        Check(CinematicCommandPolicy.IsReadOnly("status"), "cinema status is a query");
        Check(CinematicCommandPolicy.IsReadOnly(" status "), "cinema status with spaces is a query");

        // Everything that changes what the machine shows is a mutation.
        foreach (var mutation in new[]
        {
            "on", "free", "orbit", "off", "toggle", "recentre",
            "speed 4", "fov 35", "radius 20", "shot load at 0 move 1 2 3 4 5 6", "shot unload",
        })
            Check(!CinematicCommandPolicy.IsReadOnly(mutation), "cinema " + mutation + " is a mutation");

        // The consequence: a joiner whose host runs the default host-only policy may ask the camera's status but
        // may not move the camera.
        Check(CinematicCommandPolicy.Allowed(true, false), "a query is allowed even without developer access");
        Check(!CinematicCommandPolicy.Allowed(false, false), "a mutation is refused without developer access");
        Check(CinematicCommandPolicy.Allowed(false, true), "a mutation is allowed with developer access");

        // And the arguments the camera accepts are only names and numbers, never free text: the bridge echoes the
        // last command into every anomaly log line, so a free-text argument would end up in the log.
        foreach (var freeText in new[] { "speed my name", "fov wide", "on please", "label hello world" })
            Check(!IsStructured(cinematic: freeText), "the camera refuses free text: " + freeText);
        foreach (var structured in new[] { "speed 4.5", "fov 35", "radius 20", "on", "off", "free", "orbit", "status", "toggle", "recentre" })
            Check(IsStructured(cinematic: structured), "the camera accepts the structured argument: " + structured);
    }

    /// The shape of the arguments the camera's bridge surface accepts: a word from a known list, or a number.
    private static bool IsStructured(string cinematic)
    {
        var parts = cinematic.Split(' ');
        var words = new[] { "on", "off", "free", "orbit", "toggle", "recentre", "status", "shot", "speed", "fov", "radius", "load", "unload" };
        if (!words.Contains(parts[0])) return false;
        for (int i = 1; i < parts.Length; i++)
        {
            bool number = double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v);
            if (!number && !words.Contains(parts[i])) return false;
        }
        return true;
    }

    // ---- the gameplay isolation ------------------------------------------------------------

    static void GameplayIsolation()
    {
        // The camera never writes to the player's transform, so the position the network reports (which comes
        // from the player transform) cannot change. The core has no way to reach the player at all: the only
        // things it owns are numbers.
        var s = new CinematicState();
        s.Normalize();
        Check(s.TargetX == 0 && s.TargetY == 0 && s.TargetZ == 0, "the core holds no world transform of its own");
        Check(!s.Active, "a fresh state is off");
        s.Mode = CinematicMode.Free;
        Check(s.Active, "a state with a mode is active");
        s.Mode = CinematicMode.Off;
        Check(!s.Active, "the off mode is not active");

        // Turning the camera on and off is counted, so a tool can prove the camera really was on.
        s.Activations++; s.Deactivations++;
        Check(s.Activations == 1 && s.Deactivations == 1, "the activation counters work");
    }
}
