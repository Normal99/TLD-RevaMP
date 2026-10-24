using System;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Cinematic
{
    /// The cinematic camera: a local, presentation-only camera for filming.
    ///
    /// Guarantees (each one is a property of the design, not a promise):
    ///  - The game's cameras (player.Cam, player.mainCam, player.farCamera, player.skyCamera,
    ///    player.saveScreenshotCam) are never moved, reparented, repointed, re-FOVed or disabled. The camera
    ///    renders through its own Camera component on its own root.
    ///  - The player and its network root are never moved, so the position the network reports is unchanged.
    ///  - Nothing is sent over the network: the feature has no protocol, no message and no shared state.
    ///  - The cursor is only taken when the camera really needs it, and only restored if the camera still owns it.
    public static class CinematicCamera
    {
        public const string Name = "cinematic camera";

        internal static readonly CinematicState State = new CinematicState();
        private static readonly CinematicRig Rig = new CinematicRig();
        private static readonly CinematicPlayback Playback = new CinematicPlayback();

        /// The player's world position as the camera last saw it, and the world's shift offset as the camera last
        /// saw it. Together they tell the camera whether the world moved under it.
        private static Vector3 _playerPosition;
        private static Vector3d _lastRebase;
        private static bool _havePlayerPosition;

        /// The gameplay camera's enabled state when the cinematic camera started. The gameplay camera is never
        /// disabled by this feature; this is recorded so a later change cannot silently assume otherwise.
        private static bool _gameplayCameraWasEnabled;

        /// Set when the camera is on but the world is not ready (a menu, a loading screen, a death).
        private static string _suspended = "";

        public static bool Active => State.Active;
        public static CinematicMode Mode => State.Active ? State.Mode : DefaultMode;
        /// The camera's field of view. Set by Settings → Revamp → Filmmaking and by the bridge.
        public static float Fov
        {
            get => State.Fov;
            set => State.Fov = CinematicLimits.ClampFov(value);
        }

        /// The mode the camera opens in. Changed by Settings → Revamp → Filmmaking.
        public static CinematicMode DefaultMode = CinematicMode.Free;

        /// The camera's speed, in metres per second. Changed by Settings → Revamp → Filmmaking.
        public static float Speed = CinematicLimits.DefaultSpeed;

        /// Whether the status and help line is drawn. The line is drawn outside the performance overlay, so it
        /// stays visible when the overlay is hidden.
        public static bool ShowStatus = true;

        /// The key that opens and closes the camera. Set by Plugin.cs; unbound by default.
        public static string ExitKeyHint = "the cinematic camera key";

        /// The camera's own status line, drawn by its own pass (never the performance overlay's).
        internal static string StatusLine = "";
        internal static string HelpLine = "";

        /// Set when something the player should know happened (a refused entry, a teardown).
        internal static string LastNotice = "";

        // ------------------------------------------------------------------ the replay viewer's hooks

        /// Set by the replay viewer (Replay.ReplayViewer): the camera shows this pose instead of flying, for its
        /// first-person and follow views. The look state follows it, so switching back to free flight carries on from
        /// the same view.
        internal static bool Override;
        internal static Vector3 OverridePosition;
        internal static Quaternion OverrideRotation = Quaternion.identity;
        /// Whether the arrow keys turn the camera. The replay viewer uses them for its timeline.
        internal static bool ArrowLook = true;
        /// Set by the replay viewer while the player holds Alt to use the mouse on the timeline: the camera gives the
        /// cursor back and the mouse does not turn it.
        internal static bool CursorFree;

        /// Moves the orbit's centre (the replay viewer keeps it on the player being watched).
        internal static void FollowTarget(Vector3 target)
        {
            State.TargetX = target.x; State.TargetY = target.y; State.TargetZ = target.z;
            State.Normalize();
        }

        /// Changes the mode of a camera that is already on without rebuilding it, so the view does not jump back to
        /// the player's own camera. Orbit then circles the point it is looking at, at its current distance.
        internal static string SwitchMode(CinematicMode mode)
        {
            if (!State.Active || mode == CinematicMode.Off) return SetOn(mode, "replay");
            if (State.Mode == mode) return Status();
            if (mode == CinematicMode.Orbit)
            {
                Vector3 target = Rig.Position + Rig.Rotation * Vector3.forward * State.Radius;
                State.TargetX = target.x; State.TargetY = target.y; State.TargetZ = target.z;
                var offset = Rig.Position - target;
                // the inverse of CinematicOrbit.Offset: azimuth from +z towards +x, polar is the elevation
                State.Azimuth = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                State.Polar = Mathf.Asin(Mathf.Clamp(offset.y / Mathf.Max(0.001f, offset.magnitude), -1f, 1f)) * Mathf.Rad2Deg;
            }
            State.Mode = mode;
            State.Normalize();
            BuildStatus();
            return Status();
        }

        // ------------------------------------------------------------------ lifecycle

        /// Turns the camera on or off. Returns the state as JSON.
        public static string Toggle() => State.Active ? SetOff("key") : SetOn(DefaultMode, "key");

        /// Whether the camera's own key may act right now. The key is ignored while a panel holds the keyboard
        /// (the chat box, the marks panel) and while the world is not ready, so the key cannot open the camera
        /// behind a loading screen or over an open menu.
        public static bool KeyAllowed()
        {
            if (MarkPanel.Opened || Net.Chat.Typing) return false;
            if (Net.JoinLoading.Visible) return false;
            if (mainscript.s == null || mainscript.s.player == null) return false;
            if (KaposztalevesOpen()) return false;
            return true;
        }

        /// Turns the camera on in the given mode. Fails (with a reason) when the world is not in a state where a
        /// cinematic camera makes sense.
        public static string SetOn(CinematicMode mode, string source)
        {
            if (mode == CinematicMode.Off) return SetOff(source);
            if (State.Active && State.Mode == mode) return Status();
            string refusal = RefusalReason();
            if (refusal != null) { LastNotice = refusal; return "{\"error\":" + Json.Str(refusal) + "}"; }
            if (State.Active) Teardown(false);

            var player = Player();
            if (player == null) { LastNotice = "there is no player yet"; return "{\"error\":\"there is no player yet\"}"; }

            State.Mode = mode;
            State.Pending = true;
            State.Activations++;
            State.Speed = CinematicLimits.ClampSpeed(Speed);
            State.Fov = CinematicLimits.ClampFov(State.Fov);

            // Start where the player's own camera is, so switching on feels like a continuation and not a jump.
            var gameplay = player.Cam;
            if (gameplay != null)
            {
                _gameplayCameraWasEnabled = gameplay.enabled;
                Rig.MirrorStack(gameplay, player.farCamera, player.skyCamera);
                Rig.Position = gameplay.transform.position;
                Rig.Rotation = gameplay.transform.rotation;
                var e = gameplay.transform.eulerAngles;
                State.Look.Yaw = e.y;
                State.Look.Pitch = NormalizePitch(e.x);
            }
            else
            {
                Rig.Position = player.transform.position + Vector3.up * 1.6f;
                Rig.Rotation = player.transform.rotation;
                State.Look.Yaw = Rig.Rotation.eulerAngles.y;
                State.Look.Pitch = 0f;
            }
            State.Speed = CinematicLimits.ClampSpeed(Speed);
            Rig.Fov = State.Fov;
            _havePlayerPosition = false;
            _lastRebase = Rebase();
            State.TargetX = Rig.Position.x;
            State.TargetY = Rig.Position.y + 1.2f;
            State.TargetZ = Rig.Position.z;
            State.Normalize();

            var cam = Rig.Camera();
            cam.enabled = true;
            Rig.Apply();
            // The game's cameras keep rendering; ours draws in front of them. Turning the game's camera off would
            // change what every gameplay reader sees, so it stays on.
            Playback.Playing = false;
            Playback.Time = 0;
            BuildStatus();
            LastNotice = "";
            Plugin.Log?.LogInfo("Cinematic camera on (" + CinematicState.ModeName(mode) + ")");
            return Status();
        }

        /// Turns the camera off and puts everything back exactly as it was. Safe to call when it is already off.
        public static string SetOff(string source)
        {
            if (!State.Active) { Teardown(false); return Status(); }
            Teardown(true);
            LastNotice = "cinematic camera off";
            Plugin.Log?.LogInfo("Cinematic camera off (" + source + ")");
            return Status();
        }

        /// Puts the camera back the way it found it. `counted` distinguishes a user turning it off from the game
        /// taking it away (a death, a scene change): only the user turns count as a deactivation.
        private static void Teardown(bool counted)
        {
            if (counted) State.Deactivations++;
            State.Mode = CinematicMode.Off;
            State.Pending = false;
            // The camera is going away for good: give the cursor back and forget what it was, so the next time the
            // camera opens it takes the cursor from whatever state it finds (the same way the game's own panels do).
            ReleaseCursor();
            State.CursorRemembered = false;
            Rig.SetEnabled(false);
            Rig.Dispose();
            Playback.Playing = false;
            _suspended = "";
            State.Normalize();
            BuildStatus();
        }

        /// Why the camera cannot be used right now, or null when it can. The rule itself is in the pure core, so
        /// it is tested rather than trusted.
        private static string RefusalReason()
        {
            bool noWorld = mainscript.s == null;
            bool noPlayer = !noWorld && mainscript.s.player == null;
            bool mainMenu = DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu;
            bool dead = !noPlayer && mainscript.s.player.died;
            return CinematicLifecyclePolicy.Refusal(noWorld, noPlayer, mainMenu, dead, InVR(),
                                                    Net.JoinLoading.Visible, Net.Mp.SceneTearingDown,
                                                    KaposztalevesOpen());
        }

        /// Whether the game is in VR. The game re-parents its camera rig to the headset and rewrites the head
        /// transform every frame, so a cinematic camera would fight it; the camera is refused in VR rather than
        /// half-working. The camera never switches VR on or off itself: that is the OpenXR bridge's business.
        private static bool InVR()
        {
            try
            {
                if (settingsscript.s == null || settingsscript.s.S == null) return false;
                return settingsscript.s.S.BVR;
            }
            catch { return false; }
        }

        private static bool KaposztalevesOpen()
        {
            try { return kaposztaleves.s != null && kaposztaleves.s.isOpen; }
            catch { return false; }
        }

        // ------------------------------------------------------------------ per frame

        /// The camera's per-frame work, called from the mod's own runner (so its cost shows up in the profiler).
        public static void Tick()
        {
            try
            {
                if (!State.Active) return;

                // The world can change under the camera without warning: a death, a scene swap, a session
                // stopping. Each of those takes the camera down rather than leaving it pointing at nothing.
                string refusal = RefusalReason();
                if (refusal != null)
                {
                    if (_suspended != refusal) { _suspended = refusal; LastNotice = refusal; }
                    Teardown(false);
                    return;
                }
                _suspended = "";

                var player = Player();
                if (player == null) { Teardown(false); return; }

                FollowWorldShift(player);

                float dt = CinematicLimits.ClampDeltaTime(Time.deltaTime);

                if (Playback.Playing && CurrentShot != null)
                {
                    Playback.Advance(dt, CurrentShot, out bool ended);
                    // A cut is a hard jump in the take: the playhead moves to the cut and the shot at that moment
                    // takes over. The cut itself carries the time it happens at, so the take's own state at the
                    // cut is what the camera shows.
                    if (Playback.Time >= _nextCut)
                    {
                        _nextCut = CurrentShot.NextCut(Playback.Time) ?? double.MaxValue;
                        Playback.JumpToNextCut(CurrentShot, Playback.Time - 0.0001);
                    }
                    ApplyShot(CurrentShot, Playback.Time);
                    if (ended) LastNotice = "the take has ended";
                }

                if (Override)
                {
                    Rig.Position = OverridePosition;
                    Rig.Rotation = OverrideRotation;
                    var e = OverrideRotation.eulerAngles;
                    State.Look.Yaw = e.y;
                    State.Look.Pitch = CinematicLimits.ClampPitch(NormalizePitch(e.x));
                }
                else if (State.Mode == CinematicMode.Free) TickFree(dt);
                else if (State.Mode == CinematicMode.Orbit) TickOrbit(dt);

                State.Normalize();
                Rig.Fov = State.Fov;
                // The game's camera stack can change while filming (the draw distance turns the far layer on or
                // off, the sky-objects setting the sky layer), so the rig follows it every frame. Reads only.
                var stack = Player();
                if (stack != null) Rig.MirrorStack(stack.Cam, stack.farCamera, stack.skyCamera);
                Rig.Apply();
                Rig.SetEnabled(true);
                UpdateCursor();
                BuildStatus();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Cinematic camera tick: " + e.Message);
                Teardown(false);
            }
        }

        /// The world shifts by about 2 km now and then (mainscript.visszarakva). The camera lives in world space, so
        /// it has to come along or it would be left kilometres behind.
        private static void FollowWorldShift(fpscontroller player)
        {
            Vector3 now = player.transform.position;
            if (!_havePlayerPosition)
            {
                _playerPosition = now;
                _lastRebase = Rebase();
                _havePlayerPosition = true;
                return;
            }
            Vector3d rebase = Rebase();
            double dx = rebase.x - _lastRebase.x, dy = rebase.y - _lastRebase.y, dz = rebase.z - _lastRebase.z;
            if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > 0.5)
            {
                // The world moved under us: the camera comes along by the same amount, so the shot stays put in
                // world terms. Only the rig's own transform and the orbit target are written; the player is not
                // touched, and the target moves with the camera so the orbit does not swing away.
                var shift = new Vector3((float)dx, (float)dy, (float)dz);
                Rig.Position += shift;
                State.TargetX += shift.x; State.TargetY += shift.y; State.TargetZ += shift.z;
                _lastRebase = rebase;
            }
            _playerPosition = now;
        }

        // ------------------------------------------------------------------ free mode

        private static void TickFree(float dt)
        {
            var move = ReadMove();
            var look = ReadLook(dt);

            if (dt > 0f)
            {
                float speed = State.Speed * CinematicInput.SpeedFactor;
                Quaternion rot = RotationFrom(State.Look);
                Vector3 delta = (rot * Vector3.forward * move.Forward + rot * Vector3.right * move.Right + Vector3.up * move.Up) * speed * dt;
                // A frame may not move the camera further than a bounded step, however long the frame was.
                if (delta.magnitude > CinematicLimits.MaxStepMetres)
                    delta = delta.normalized * CinematicLimits.MaxStepMetres;
                Rig.Position += delta;
            }
            State.Look.Yaw += look.Yaw;
            State.Look.Pitch = CinematicLimits.ClampPitch(State.Look.Pitch + look.Pitch);
            Rig.Rotation = RotationFrom(State.Look);
        }

        // ------------------------------------------------------------------ orbit mode

        private static void TickOrbit(float dt)
        {
            var look = ReadLook(dt);
            var move = ReadMove();

            // The mouse orbits the target the way the free camera would look around it: the view sweeps to the
            // right across the subject, and looking up raises the camera so it looks down at the subject. The
            // camera's own look direction is then derived from the geometry, not accumulated.
            CinematicOrbit.Follow(look.Yaw, look.Pitch, 0.5f, ref State.Azimuth, ref State.Polar);

            if (dt > 0f)
            {
                if (move.Forward != 0f)
                    State.Radius = CinematicLimits.ClampRadius(State.Radius - move.Forward * CinematicLimits.OrbitZoomSpeed * dt);
                if (move.Up != 0f)
                {
                    State.TargetY += move.Up * State.Speed * CinematicInput.SpeedFactor * dt;
                    State.Normalize();
                }
                if (move.Right != 0f)
                    State.Azimuth = CinematicLimits.WrapAzimuth(State.Azimuth + move.Right * CinematicLimits.OrbitTurnSpeed * dt);
            }

            Vector3 target = new Vector3((float)State.TargetX, (float)State.TargetY, (float)State.TargetZ);
            CinematicOrbit.Position(target.x, target.y, target.z, State.Azimuth, State.Polar, State.Radius,
                                    out float px, out float py, out float pz);
            Vector3 position = new Vector3(px, py, pz);
            // The camera must never end up inside the target.
            if ((position - target).magnitude < CinematicLimits.MinTargetSeparation)
                position = target + Vector3.up * CinematicLimits.MinTargetSeparation;
            Rig.Position = position;
            Vector3 lookDir = target - position;
            if (lookDir.sqrMagnitude > 1e-6f)
            {
                Rig.Rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                var e = Rig.Rotation.eulerAngles;
                State.Look.Yaw = e.y;
                State.Look.Pitch = NormalizePitch(e.x);
            }
        }

        /// Puts the orbit target back in front of the camera, at the current radius.
        public static string Recentre()
        {
            if (!State.Active) return "{\"error\":\"the cinematic camera is not on\"}";
            Vector3 forward = Rig.Rotation * Vector3.forward;
            Vector3 target = Rig.Position + forward * State.Radius;
            State.TargetX = target.x; State.TargetY = target.y; State.TargetZ = target.z;
            State.Recentres++;
            State.Normalize();
            return Status();
        }

        // ------------------------------------------------------------------ input

        /// Reads the movement keys. The game's own inputs are used where they exist, so the cinematic camera
        /// answers to the same keys the player already knows (W A S D, Shift to hurry, Ctrl to creep).
        private static CinematicMove ReadMove()
        {
            var move = new CinematicMove();
            // The values the camera uses are the ones the player's own movement axes had before the camera took
            // them over (CinematicInput). Reading the game's live axes here would give zeroes, because the
            // takeover zeroes them on purpose.
            var axes = CinematicInputPolicy.ForCamera(CinematicInput.Forward, CinematicInput.Backward,
                                                      CinematicInput.Left, CinematicInput.Right);
            CinematicInputPolicy.Movement(axes, out move.Forward, out move.Right);
            // The game has no "up" axis: Q and E are the camera's own climb keys (as in the game's own dead
            // ExtendedFlycam class, and as most flycams do).
            if (Input.GetKey(KeyCode.Q)) move.Up += 1f;
            if (Input.GetKey(KeyCode.E)) move.Up -= 1f;
            return move;
        }

        /// Reads the look keys: the mouse, and the arrow keys for a mouse-free setup.
        private static CinematicLook ReadLook(float dt)
        {
            var look = new CinematicLook();
            float mouseX = 0f, mouseY = 0f;
            try
            {
                if (!CursorFree)
                {
                    mouseX = Input.GetAxis("Mouse X");
                    mouseY = Input.GetAxis("Mouse Y");
                }
            }
            catch { }
            look.Yaw = mouseX * CinematicLimits.LookSpeed * dt;
            look.Pitch = -mouseY * CinematicLimits.LookSpeed * dt;
            // The arrow keys turn the camera, so the camera is usable without a mouse.
            if (!ArrowLook) return look;
            if (Input.GetKey(KeyCode.LeftArrow)) look.Yaw -= CinematicLimits.LookSpeed * dt;
            if (Input.GetKey(KeyCode.RightArrow)) look.Yaw += CinematicLimits.LookSpeed * dt;
            if (Input.GetKey(KeyCode.UpArrow)) look.Pitch += CinematicLimits.LookSpeed * dt;
            if (Input.GetKey(KeyCode.DownArrow)) look.Pitch -= CinematicLimits.LookSpeed * dt;
            return look;
        }

        private static inputscript PlayerInput()
        {
            try
            {
                if (mainscript.s == null || mainscript.s.player == null) return null;
                return mainscript.s.player.input;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ cursor

        /// Takes the cursor only when the camera needs it, and only while no other panel holds it.
        private static void TakeCursor()
        {
            if (State.OwnsCursor) return;
            if (!CinematicCursorPolicy.MayTake(MarkPanel.Opened, Net.Chat.Typing, Net.JoinLoading.Visible)) return;
            // Remember what the cursor was *before* the camera took it, but only the first time: if a panel took
            // the cursor while the camera owned it, the camera's own "before" state is still the right one to go
            // back to when the panel closes.
            if (!State.CursorRemembered)
            {
                State.CursorWasVisible = Cursor.visible;
                State.CursorWasLockState = (int)Cursor.lockState;
                State.CursorRemembered = true;
            }
            int lockState = (int)Cursor.lockState;
            bool visible = Cursor.visible;
            CinematicCursorPolicy.ApplyTake(ref lockState, ref visible);
            Cursor.lockState = (CursorLockMode)lockState;
            Cursor.visible = visible;
            State.OwnsCursor = true;
        }

        /// Gives the cursor back, but only if the camera still owns it. If another panel took it in the meantime,
        /// that panel's state is left alone: this restores only what this feature changed.
        private static void ReleaseCursor()
        {
            if (!State.OwnsCursor) return;
            // Only restore if the cursor is still the one we set. If a panel has since unlocked it, that panel
            // owns the cursor now and we must not lock it again.
            if (CinematicCursorPolicy.MayRestore((int)Cursor.lockState, Cursor.visible, CinematicCursorPolicy.LockLocked, false))
            {
                Cursor.lockState = (CursorLockMode)State.CursorWasLockState;
                Cursor.visible = State.CursorWasVisible;
                State.CursorRemembered = false;   // the camera gave the cursor back for good
            }
            State.OwnsCursor = false;
        }

        /// Keeps the cursor in the right state while the camera is on: taken when the camera is driving, given
        /// back when a panel wants it.
        private static void UpdateCursor()
        {
            bool anotherOwner = MarkPanel.Opened || Net.Chat.Typing || Net.JoinLoading.Visible || CursorFree;
            if (anotherOwner) { ReleaseCursor(); return; }
            TakeCursor();
        }

        // ------------------------------------------------------------------ shot list

        private static CinematicShot _shot;
        private static string _shotError = "";
        /// The next cut time in the take being played; double.MaxValue when the take has no more cuts.
        private static double _nextCut = double.MaxValue;

        /// The take currently loaded, if any.
        public static CinematicShot CurrentShot => _shot;

        /// Loads a shot list from text. The text is parsed by the pure codec; nothing is executed.
        public static string LoadShot(string text)
        {
            string error;
            var shot = CinematicShotCodec.Parse(text, out error);
            if (shot == null) { _shotError = error; return "{\"error\":" + Json.Str(error) + "}"; }
            _shot = shot;
            _shotError = "";
            Playback.Time = 0;
            Playback.Playing = true;
            Playback.Speed = 1f;
            _nextCut = shot.NextCut(0.0) ?? double.MaxValue;
            LastNotice = "shot list loaded (" + shot.Steps.Count + " steps)";
            return ShotStatus();
        }

        public static string UnloadShot()
        {
            _shot = null;
            Playback.Playing = false;
            Playback.Time = 0;
            _nextCut = double.MaxValue;
            return ShotStatus();
        }

        /// Applies the take's state at the playhead.
        ///
        /// The positions in a shot list are **global** coordinates - the ones the game's own map and coordinates
        /// show, and the ones that stay the same when the world shifts by two kilometres. They are converted to the
        /// game's internal coordinates here, so a take written today still frames the same place tomorrow.
        private static void ApplyShot(CinematicShot shot, double now)
        {
            var move = shot.LastOf("move", now);
            if (move != null && move.Values.Length >= 6)
            {
                Rig.Position = ToUnity(move.Values[0], move.Values[1], move.Values[2]);
                State.Look.Yaw = (float)move.Values[3];
                State.Look.Pitch = CinematicLimits.ClampPitch((float)move.Values[4]);
                State.Fov = CinematicLimits.ClampFov((float)move.Values[5]);
                Rig.Rotation = RotationFrom(State.Look);
            }
            var target = shot.LastOf("target", now);
            if (target != null && target.Values.Length >= 3)
            {
                var unity = ToUnity(target.Values[0], target.Values[1], target.Values[2]);
                State.TargetX = unity.x; State.TargetY = unity.y; State.TargetZ = unity.z;
            }
            var speed = shot.LastOf("speed", now);
            if (speed != null && speed.Values.Length >= 1) Playback.Speed = (float)speed.Values[0];
        }

        /// A global position (the world's own coordinates) in the game's internal ones.
        private static Vector3 ToUnity(double x, double y, double z)
        {
            try
            {
                if (mainscript.s == null) return new Vector3((float)x, (float)y, (float)z);
                return mainscript.UnityPosFromGlobal(new Vector3d(x, y, z));
            }
            catch { return new Vector3((float)x, (float)y, (float)z); }
        }

        /// The camera's own position in the world's coordinates, for the status and for authoring a take.
        public static Vector3d GlobalPosition()
        {
            try
            {
                if (mainscript.s == null) return new Vector3d(Rig.Position.x, Rig.Position.y, Rig.Position.z);
                return mainscript.GlobalFromUnityPos(Rig.Position);
            }
            catch { return new Vector3d(Rig.Position.x, Rig.Position.y, Rig.Position.z); }
        }

        // ------------------------------------------------------------------ screenshots

        /// Where the camera's own screenshots go: a local folder, never in a report, never shared.
        /// The repo's telemetry deliberately holds no screenshots (docs/TELEMETRY.md), so the camera writes
        /// them somewhere of its own, and only when the player asks for one.
        public static string ShotFolder => System.IO.Path.Combine(
            BepInEx.Paths.BepInExRootPath, "tldrevamp-cinematic");

        /// Takes a screenshot of the cinematic camera's view. The file is written where the player can find it,
        /// and the path is returned so a tool can pick it up.
        ///
        /// The name is made of the camera's own state, never of anything the player typed: a screenshot's file
        /// name ends up in a folder listing, and a name typed by a player would carry whatever they typed.
        public static string Screenshot()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ShotFolder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                string name = "shot-" + stamp + ".png";
                string path = System.IO.Path.Combine(ShotFolder, name);
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                LastNotice = "screenshot saved: " + name;
                return "{\"path\":" + Json.Str(path) + ",\"name\":" + Json.Str(name) + "}";
            }
            catch (Exception e)
            {
                return "{\"error\":" + Json.Str("the screenshot could not be saved: " + e.Message) + "}";
            }
        }

        // ------------------------------------------------------------------ status

        /// The camera's state, as JSON. Read-only: this changes nothing.
        public static string Status()
        {
            var sb = new StringBuilder();
            sb.Append("{\"mode\":").Append(Json.Str(CinematicState.ModeName(State.Mode)));
            sb.Append(",\"active\":").Append(State.Active ? "true" : "false");
            sb.Append(",\"fov\":").Append(State.Fov.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"speed\":").Append(State.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"position\":[").Append(Num(Rig.Position.x)).Append(',').Append(Num(Rig.Position.y)).Append(',').Append(Num(Rig.Position.z)).Append(']');
            var global = GlobalPosition();
            sb.Append(",\"global\":[").Append(Num((float)global.x)).Append(',').Append(Num((float)global.y)).Append(',').Append(Num((float)global.z)).Append(']');
            sb.Append(",\"yaw\":").Append(Num(State.Look.Yaw)).Append(",\"pitch\":").Append(Num(State.Look.Pitch));
            sb.Append(",\"radius\":").Append(Num(State.Radius)).Append(",\"polar\":").Append(Num(State.Polar)).Append(",\"azimuth\":").Append(Num(State.Azimuth));
            sb.Append(",\"target\":[").Append(Num((float)State.TargetX)).Append(',').Append(Num((float)State.TargetY)).Append(',').Append(Num((float)State.TargetZ)).Append(']');
            sb.Append(",\"cursorOwned\":").Append(State.OwnsCursor ? "true" : "false");
            sb.Append(",\"activations\":").Append(State.Activations).Append(",\"deactivations\":").Append(State.Deactivations);
            sb.Append(",\"recentres\":").Append(State.Recentres);
            sb.Append(",\"exitKey\":").Append(Json.Str(ExitKeyHint));
            sb.Append(",\"notice\":").Append(Json.Str(LastNotice ?? ""));
            sb.Append('}');
            return sb.ToString();
        }

        /// The shot list's state, as JSON.
        public static string ShotStatus()
        {
            var sb = new StringBuilder();
            sb.Append("{\"loaded\":").Append(_shot != null ? "true" : "false");
            sb.Append(",\"steps\":").Append(_shot != null ? _shot.Steps.Count : 0);
            sb.Append(",\"duration\":").Append(_shot != null ? _shot.Duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "0");
            sb.Append(",\"playing\":").Append(Playback.Playing ? "true" : "false");
            sb.Append(",\"time\":").Append(Playback.Time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"speed\":").Append(Playback.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"version\":").Append(_shot != null ? _shot.Version : CinematicShotFormat.CurrentVersion);
            sb.Append(",\"error\":").Append(Json.Str(_shotError ?? ""));
            sb.Append('}');
            return sb.ToString();
        }

        private static string Num(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        private static Quaternion RotationFrom(CinematicLook look) => Quaternion.Euler(look.Pitch, look.Yaw, 0f);

        private static float NormalizePitch(float degrees)
        {
            if (degrees > 180f) degrees -= 360f;
            return degrees;
        }

        private static Vector3d Rebase()
        {
            try { return mainscript.s != null ? mainscript.s.visszarakva : Vector3d.zero; }
            catch { return Vector3d.zero; }
        }

        private static fpscontroller Player()
        {
            try { return mainscript.s != null ? mainscript.s.player : null; }
            catch { return null; }
        }

        /// The status and help lines, shown while the camera is on. They live here rather than in the performance
        /// overlay, so they stay visible when the overlay is hidden (which is exactly when a filmmaker wants them).
        private static void BuildStatus()
        {
            if (!State.Active)
            {
                StatusLine = "";
                HelpLine = "";
                return;
            }
            string mode = State.Mode == CinematicMode.Free ? "FREE" : "ORBIT";
            StatusLine = "Cinematic camera: " + mode +
                         "   FOV " + State.Fov.ToString("F0") +
                         "   speed " + State.Speed.ToString("F0") + " m/s" +
                         (State.Mode == CinematicMode.Orbit ? "   radius " + State.Radius.ToString("F1") + " m" : "");
            HelpLine = State.Mode == CinematicMode.Free
                ? "W A S D move · Q E up and down · Shift faster · Ctrl slower · mouse or arrows look · " + ExitKeyHint + " exit"
                : "mouse or arrows orbit · W S zoom · Q E raise and lower the target · A D turn · " + ExitKeyHint + " exit";
        }

        /// Draws the status and help. Called from the mod's own GUI pass, before the performance overlay's
        /// visibility check, so it is never hidden with the overlay.
        public static void Draw()
        {
            if (!ShowStatus) return;
            if (!State.Active || string.IsNullOrEmpty(StatusLine)) return;
            if (Event.current.type != EventType.Repaint) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false, wordWrap = false };
            style.normal.textColor = new Color(1f, 0.92f, 0.45f);
            float y = 8f;
            GUI.Label(new Rect(12f, y, Screen.width - 24f, 20f), StatusLine, style);
            style.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
            GUI.Label(new Rect(12f, y + 20f, Screen.width - 24f, 20f), HelpLine, style);
            if (!string.IsNullOrEmpty(LastNotice))
            {
                style.normal.textColor = new Color(1f, 0.6f, 0.4f);
                GUI.Label(new Rect(12f, y + 40f, Screen.width - 24f, 20f), LastNotice, style);
            }
        }
    }
}
