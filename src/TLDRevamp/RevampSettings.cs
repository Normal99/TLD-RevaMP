using System;
using System.Collections.Generic;
using System.IO;
using TLDRevamp.Fixes;

namespace TLDRevamp
{
    /// The mod's player-facing settings: what the "Revamp" tab in the game's settings menu shows, with explanations.
    /// Stored in the mod's own file (BepInEx/config/tldrevamp-settings.txt), never in the game's settings.
    public static class RevampSettings
    {
        public enum Kind { Header, Toggle, Choice }

        public sealed class Entry
        {
            public string Key, Label, Help;
            public Kind Kind;
            public bool RuntimeOnly;
            public string[] Options;          // Choice
            public int Default;               // Toggle: 0/1, Choice: index
            public Func<int> Get;
            public Action<int> Set;
        }

        public static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<string, string> TextValues = new Dictionary<string, string>();
        private static readonly Dictionary<string, int> IntegerValues = new Dictionary<string, int>();

        internal static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "tldrevamp-settings.txt");

        /// The settings file's path (the migration of the old plaintext password needs it).
        internal static string SettingsFilePath => FilePath;

        static RevampSettings()
        {
            Header("Visual trade-offs",
                "These change what you see, a little, in exchange for speed. Each one was checked in game and chosen so " +
                "the default looks the same as the original during normal play.");
            Choice("detailedTerrain", "Detailed terrain distance", new[] { "Game's own (about 300 m)", "500 m", "750 m", "1000 m" }, 2,
                "The ground around you is drawn in full detail only so far; past that you see the coarse distant terrain, " +
                "which is shaped differently. In the game it ends about 300 m away, so the detailed ground pops in ahead of " +
                "you and roads float over or sink into the hills beyond it.\n\nThis draws the game's own detailed ground further around you, and roads with it." +
                "\n\nFurther costs some FPS and memory; only your own surroundings are extended, not other players'.",
                () => Math.Max(0, Array.IndexOf(DetailedTerrain.Choices, DetailedTerrain.DistanceM)),
                v => { DetailedTerrain.DistanceM = DetailedTerrain.Choices[Math.Max(0, Math.Min(DetailedTerrain.Choices.Length - 1, v))]; DetailedTerrain.Apply(); });
            Choice("mirrors", "Mirror refresh rate", new[] { "Every frame (original)", "Every 2nd frame", "Every 3rd frame" }, 1,
                "Car mirrors draw the world behind you with their own camera, a full extra view of the scene every frame." +
                "\n\nEvery 2nd frame: looks the same while driving, about +10% FPS in a car." +
                "\nEvery 3rd frame: faster, but the mirror image gets visibly choppy.",
                () => !MirrorThrottle.Enabled ? 0 : Math.Min(2, Math.Max(1, MirrorThrottle.EveryN - 1)),
                v => { MirrorThrottle.Enabled = v > 0; if (v > 0) MirrorThrottle.EveryN = v + 1; });
            Toggle("mirrorShadows", "Shadows in mirrors", 1,
                "Mirror images include sun shadows, which the game computes again for each mirror." +
                "\n\nOff: flatter mirror image, a few percent faster while mirrors are visible.",
                () => MirrorThrottle.NoShadows ? 0 : 1, v => MirrorThrottle.NoShadows = v == 0);
            Choice("tinyParts", "Hide tiny distant parts", new[] { "Off (original)", "Smaller than 2 px", "Smaller than 4 px", "Smaller than 6 px" }, 2,
                "Cars and items are built from hundreds of small parts (bolts, gauges, interior pieces), each drawn separately. " +
                "Parts smaller than this on screen are skipped and drawn again as soon as they get bigger." +
                "\n\n4 px: invisible in practice, +12% FPS with many cars in view.",
                () => !TinyRendererCull.Enabled ? 0 : TinyRendererCull.ThresholdPx <= 2f ? 1 : TinyRendererCull.ThresholdPx <= 4f ? 2 : 3,
                v => { TinyRendererCull.Enabled = v > 0; if (v > 0) TinyRendererCull.ThresholdPx = v * 2f; });
            Choice("grass", "Grass bending updates", new[] { "Every frame (original)", "Every 2nd frame", "Every 3rd frame" }, 2,
                "Grass bends away from you and your car using a small extra camera that looks down at your surroundings." +
                "\n\nUpdating it every 3rd frame looks the same and gives about +9% FPS.",
                () => !GrassCameraThrottle.Enabled ? 0 : Math.Min(2, Math.Max(1, GrassCameraThrottle.EveryN - 1)),
                v => { GrassCameraThrottle.Enabled = v > 0; if (v > 0) GrassCameraThrottle.EveryN = v + 1; });
            Choice("shadowCascades", "Sun shadow detail", new[] { "Original (4 cascades)", "2 cascades", "1 cascade" }, 0,
                "The sun's shadows are drawn in up to 4 layers of detail, from sharp near you to coarse far away. Fewer " +
                "layers draw faster; shadows near you get blurrier." +
                "\n\n2 cascades: +1–3% FPS.\n1 cascade: +3–6% FPS, noticeably softer shadows.",
                () => RenderOptions.ShadowCascades == 2 ? 1 : RenderOptions.ShadowCascades == 1 ? 2 : 0,
                v => RenderOptions.ShadowCascades = v == 1 ? 2 : v == 2 ? 1 : 0);
            Choice("reflections", "Reflection updates", new[] { "Every frame (original)", "4 times a second", "Once a second" }, 0,
                "Every car, bike and some items keep a live picture of their surroundings for their shiny surfaces " +
                "(paint, chrome, glass), redrawn all the time. Updating it less often is faster; reflections on your car " +
                "then catch up with the scenery a moment later." +
                "\n\nWhile driving: 4 times a second +9% FPS, once a second +13% FPS.",
                () => RenderOptions.ProbeInterval <= 0f ? 0 : RenderOptions.ProbeInterval < 0.5f ? 1 : 2,
                v => RenderOptions.ProbeInterval = v == 1 ? 0.25f : v == 2 ? 1f : 0f);
            Toggle("smallItemShadows", "No shadows from small items", 0,
                "Items and car parts smaller than about half a metre (bolts, gauges, cans, small car parts) cast no sun " +
                "shadow. Bigger things keep theirs." +
                "\n\n+3.5% FPS in a town with a few cars.",
                () => RenderOptions.SmallItemShadows ? 1 : 0, v => RenderOptions.SmallItemShadows = v == 1);
            Toggle("binoculars", "Binocular lens only when seen", 1,
                "Binoculars render the view through their lens all the time, even lying in a house far away." +
                "\n\nOn: the lens is only rendered while it's on screen (+3% FPS).",
                () => BinocularLensCamera.Enabled ? 1 : 0, v => BinocularLensCamera.Enabled = v == 1);

            Header("Performance fixes",
                "These make the game do the same work faster or skip work that changes nothing. The world, the physics and " +
                "what you see stay exactly the same. Only switch one off if you suspect it causes a problem.");
            Toggle("roadPlace", "Road piece placement cache", 1,
                "The game re-checked every road piece within 3.5 km every frame to decide what to show. " +
                "This only checks what can have changed.\n\nPart of the +60% FPS while driving.",
                () => RoadPlaceCache.Enabled ? 1 : 0, v => RoadPlaceCache.Enabled = v == 1);
            Toggle("scenery", "Faster scenery generation", 1,
                "Rocks, bushes and cacti are placed by testing every spot of a grid with its own random number generator. " +
                "This computes the same numbers without the waste.\n\nRemoves 15–20 ms stutters while driving.",
                () => ObjGenFast.Enabled ? 1 : 0, v => ObjGenFast.Enabled = v == 1);
            Toggle("sceneryConsistent", "Scenery matches roads and buildings", 1,
                "The game decided where trees and rocks may stand from a snapshot of the roads and buildings nearby, taken " +
                "whenever that spot was first needed. Arriving fast (or loading, or teleporting), the snapshot could miss a " +
                "building or road that appeared a moment later: trees inside building areas or on roads, and scenery that " +
                "differed between visits and between players. The ground under a building could stay unflattened the same way.\n\nNow scenery and ground are updated when a road or building appears, so " +
                "every player sees the same world. Needed for multiplayer.",
                () => SceneryTileSync.Enabled && TerrainSync.Enabled ? 1 : 0, v => { SceneryTileSync.Enabled = v == 1; TerrainSync.Enabled = v == 1; });
            Toggle("roadConnect", "Road connection memory", 1,
                "The game recalculated the same road connections around you every frame. They're now remembered and reused.",
                () => RoadConnectMemo.Enabled ? 1 : 0, v => RoadConnectMemo.Enabled = v == 1);
            Toggle("roadChunks", "Road chunk memory", 1,
                "The ring of road map pieces around you was destroyed and rebuilt every frame. They're now remembered " +
                "and reused, which removes most of the game's garbage-collection pauses.",
                () => DotChunkMemo.Enabled ? 1 : 0, v => DotChunkMemo.Enabled = v == 1);
            Toggle("terrain", "Background terrain building", 1,
                "New terrain tiles are computed on other CPU cores instead of freezing the game for ~5 ms each.",
                () => AsyncTerrain.Enabled ? 1 : 0, v => AsyncTerrain.Enabled = v == 1);
            Toggle("terrainSeams", "Correct distant terrain under roads", 1,
                "Distant terrain is a coarse picture made by a different generator from the road-aware ground around you. " +
                "This pulls its vertices onto the road's height near real roads, so distant roads no longer float above hills.",
                () => TerrainHorizon.CorrectFar ? 1 : 0,
                v =>
                {
                    TerrainHorizon.CorrectFar = v == 1;
                    // The far mesh belongs to the game: switching the correction off puts the game's own heights
                    // back, so the world is not left deformed by a feature that is no longer on.
                    if (!TerrainHorizon.CorrectFar) TerrainHorizon.RestoreFarMeshes();
                });
            Toggle("distantRoads", "Draw roads into the distance", 1,
                "Draws lightweight road ribbons beyond the detailed 500 m area. This removes floating roads at crossroads without " +
                "loading full road pieces, poles, props or physics far away.",
                () => TerrainHorizon.DistantRoads ? 1 : 0, v => TerrainHorizon.DistantRoads = v == 1);
            Toggle("parallelRoads", "Parallel road building", 1,
                "New roads near buildings are computed on several CPU cores at once, removing long freezes when a town appears.",
                () => ParallelRoadGen.Enabled ? 1 : 0, v => ParallelRoadGen.Enabled = v == 1);
            Toggle("lists", "Reuse world lists", 1,
                "The world generator rebuilt and sorted several lists every frame. They're now refilled in place (less garbage).",
                () => ChunksNeededInPlace.Enabled && ObjPlaceInPlace.Enabled ? 1 : 0,
                v => { ChunksNeededInPlace.Enabled = v == 1; ObjPlaceInPlace.Enabled = v == 1; });
            Toggle("itemSpawn", "Spread item spawning", 1,
                "When many items appear at once (arriving at a place), they're spawned over a few frames instead of all in one.",
                () => ItemSpawnSpread.Enabled ? 1 : 0, v => ItemSpawnSpread.Enabled = v == 1);
            Toggle("itemPlace", "Spread item placing", 1,
                "Coming back to a place you've been (a town, your base), the game put all its items back in one frame. " +
                "They're put back over a few frames instead, in the same order. Single player.\n\nWorst frame 80 → 19 ms.",
                () => FarItemLookup.Spread ? 1 : 0, v => FarItemLookup.Spread = v == 1);
            Toggle("partCondition", "Skip idle rust and paint updates", 1,
                "Every car part's rust and paint state was checked every frame, though it only changes while you clean or " +
                "repair it. Idle ones are skipped until a repair stroke touches them.\n\n+3% FPS in a car park.",
                () => PartConditionIdle.Enabled ? 1 : 0, v => PartConditionIdle.Enabled = v == 1);
            Toggle("wheels", "Skip resting wheels", 1,
                "Parked cars redrew their wheels every frame although nothing moved. Resting wheels are now updated less " +
                "often (at most 2 mm off).\n\n+25% FPS in a car park.",
                () => SleepingWheelSkip.Enabled ? 1 : 0, v => SleepingWheelSkip.Enabled = v == 1);
            Toggle("impactSounds", "Limit impact sounds", 1,
                "When many items collide at once (a pile falls), only a few impact sounds start per frame. Halves the stutter.",
                () => ImpactSoundLimiter.Mode != 0 ? 1 : 0, v => ImpactSoundLimiter.Mode = v == 1 ? 2 : 0);
            Toggle("worldShift", "Smooth world shift", 1,
                "Every ~4.5 km the game moves the whole world back towards its centre. Particle effects nearby then did a " +
                "huge amount of useless work.\n\nFreeze at that moment: 164 → 36 ms.",
                () => RebaseParticles.Enabled ? 1 : 0, v => RebaseParticles.Enabled = v == 1);
            Toggle("buildingRoads", "Smart building placement", 1,
                "To place one building next to a road, the game built every road in a 30 × 30 km area first (up to 80 at " +
                "once) just to find the nearest one. Now only roads that can be the nearest are built; the building lands in " +
                "exactly the same spot.\n\nArriving somewhere new: worst stutter about half as long.",
                () => AlignNearestRoad.Enabled ? 1 : 0, v => AlignNearestRoad.Enabled = v == 1);
            Toggle("aiRays", "AI vision without garbage", 1,
                "Ghosts and other AI characters look around with many rays every frame, and each look created new memory " +
                "garbage. Same rays, same results, reused memory.\n\nNear the haunted mansion: garbage −55%, no more collection pauses.",
                () => AiRaycastNoAlloc.Enabled ? 1 : 0, v => AiRaycastNoAlloc.Enabled = v == 1);
            Toggle("outfitGarbage", "Player model without garbage", 1,
                "Before and after every camera picture the game adjusts your own character model, and each time it made " +
                "new memory garbage. Same settings, reused memory.\n\nStanding still: garbage −50%, no collection pauses.",
                () => OutfitDistanceNoAlloc.Enabled ? 1 : 0, v => OutfitDistanceNoAlloc.Enabled = v == 1);
            Toggle("textureStreaming", "Idle texture streaming off", 1,
                "The game's quality settings run Unity's texture streaming every frame, but none of the game's textures " +
                "stream. It's off while no loaded texture streams, and on again if one does. Textures look the same.\n\n+2% FPS.",
                () => IdleTextureStreaming.Enabled ? 1 : 0, v => IdleTextureStreaming.Enabled = v == 1);
            Toggle("pour", "Pause idle pour effects", 1,
                "Every canister, barrel and bottle keeps an invisible pouring effect running. It's paused until you actually pour." +
                "\n\n+9% FPS around many containers.",
                () => IdlePourPause.Enabled ? 1 : 0, v => IdlePourPause.Enabled = v == 1);
            Toggle("carParticles", "Pause idle car smoke and dust", 1,
                "Every car keeps its exhaust smoke, radiator steam and wheel dust effects running while it stands with the " +
                "engine off. They're paused until the engine runs, the radiator boils or a wheel throws dust.\n\n+3% FPS in a car park.",
                () => IdleCarParticles.Enabled ? 1 : 0, v => IdleCarParticles.Enabled = v == 1);
            Header("Trailers and driving",
                "Trailer brakes, and driving assists for the car you drive. The assists are off unless you turn them on: " +
                "they change how cars handle, for everyone riding in the car you drive.");
            Toggle("trailerBrakes", "Trailer brakes", 1,
                "A hitched trailer brakes with the car towing it: its brake pedal and its handbrake lever. While driving, " +
                "the run key (Left Shift) brakes the trailers alone, to steady a swaying trailer or hold it back downhill; " +
                "the trailer's brake lights come on.\n\nOff: trailers brake only by their own lever, as in the original.",
                () => TrailerBrakes.Enabled ? 1 : 0, v => TrailerBrakes.Enabled = v == 1);
            Toggle("swayAssist", "Trailer sway reduction", 0,
                "A towed trailer's swinging from side to side is damped, so a fishtailing trailer settles.",
                () => DrivingAssists.Sway ? 1 : 0, v => DrivingAssists.Sway = v == 1);
            Toggle("rollAssist", "Roll correction", 0,
                "Your car leans less in fast corners and rolls over less easily. It can still tip over on an obstacle or a " +
                "steep slope.",
                () => DrivingAssists.Roll ? 1 : 0, v => DrivingAssists.Roll = v == 1);
            Toggle("tractionAssist", "Traction control", 0,
                "A driven wheel spinning on sand, grass or ice gets less engine power until it grips again.",
                () => DrivingAssists.Traction ? 1 : 0, v => DrivingAssists.Traction = v == 1);
            Header("World",
                "What happens in the world around you.");
            Choice("sandstorms", "Sandstorms", new[] { "Off", "Rare", "Normal", "Often" }, 2,
                "Now and then a sandstorm comes up in the desert and drifts across the sand; it dies down after 15 to 30 " +
                "minutes, or when it leaves the sand.\nRare: one every 1 to 2.5 hours while you are on sand, a quarter of " +
                "them come your way.\nNormal: one every 30 to 75 minutes, 4 in 10 come your way; the others pass by in the " +
                "distance.\nOften: one every 15 to 45 minutes, most of them come your way.\nOff: none come up.\n\nIn " +
                "multiplayer the host's setting counts.",
                () => Sandstorms.Frequency, v => Sandstorms.SetFrequency(v));
            Header("Multiplayer",
                "Voice and text chat with the other players, and what your game allows when you host. Host or join from the game's Multiplayer button (or F11).");
            Toggle("joinLoading", "Loading screen when joining", 1,
                "Cover the game and mute audio while the host's world, nearby terrain, roads and shared objects load. Escape cancels the join.",
                () => Net.JoinLoading.Enabled ? 1 : 0, v => Net.JoinLoading.Enabled = v == 1);
            Choice("voice", "Voice chat", new[] { "Push to talk", "Open microphone", "Off" }, 0,
                "Push to talk: hold the game's \"Voice chat\" key (Settings → Controls; K if you never set one)." +
                "\nOpen microphone: Steam sends your voice whenever you speak." +
                "\n\nYour microphone is the one chosen in Steam (Steam → Settings → Voice). Players hear you within about " +
                "60 m, from where you stand.",
                () => (int)Net.Voice.Setting, v => Net.Voice.Setting = (Net.Voice.Mode)Math.Max(0, Math.Min(2, v)));
            Choice("voiceVolume", "Voice volume", new[] { "25%", "50%", "75%", "100%", "150%" }, 3,
                "How loud the other players' voices are.",
                () => Net.Voice.Volume <= 0.3f ? 0 : Net.Voice.Volume <= 0.6f ? 1 : Net.Voice.Volume <= 0.8f ? 2 : Net.Voice.Volume <= 1.1f ? 3 : 4,
                v => Net.Voice.Volume = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f }[Math.Max(0, Math.Min(4, v))]);
            Choice("tpa", "Teleport requests (when you host)", new[] { "Off", "On, no wait", "On, every 1 min", "On, every 5 min", "On, every 15 min" }, 3,
                "Players in your game can ask to teleport to another player with /tpa <name> in the chat; that player " +
                "answers /y or /n. The wait is per player, between two requests. The same as the button on your own card " +
                "in the Multiplayer screen.",
                () => Math.Max(0, Array.IndexOf(Net.Tpa.Choices, Net.Tpa.Enabled ? Net.Tpa.CooldownS : -1)),
                v => { int c = Net.Tpa.Choices[Math.Max(0, Math.Min(Net.Tpa.Choices.Length - 1, v))]; Net.Tpa.Enabled = c >= 0; if (c >= 0) Net.Tpa.CooldownS = c; });
            Header("VR",
                "How VR plays with the headset on. None of this changes anything in flat play, and your own game " +
                "settings (head bob, motion blur) stay saved as they are.");
            Toggle("vrComfort", "Comfort in VR", 1,
                "While the headset is on: no walking or driving head bob (they move your whole view in VR), no motion " +
                "blur and no depth of field. Flat play keeps your own settings for them.",
                () => VR.Native.VrNative.Comfort ? 1 : 0, v => VR.Native.VrNative.Comfort = v == 1);
            Toggle("vrHands", "Hands", 1,
                "Grip: grab what a hand touches, or pull in what it points at; let go to drop, or throw with a swing.\n" +
                "Trigger: use what that hand points at (doors, buttons, seats, keys), or use the item it holds (drink, " +
                "pour).\nA / X: attach the part you hold where you point, or detach the part you point at.\n\n" +
                "Off: the game's original VR controls (A / X to use, looking at things to aim).",
                () => VR.Native.VrNative.Hands ? 1 : 0, v => VR.Native.VrNative.Hands = v == 1);
            Toggle("vrHeight", "Height from your head", 1,
                "Standing up straight is always full height, for you and for the players who see you: each recenter " +
                "(B) learns your standing height, and it rises on its own if your head is higher. Crouching for real " +
                "crouches your body.\n\nOff: the game's original placement (the height of the last recenter).",
                () => VR.Native.VrHeight.Enabled ? 1 : 0, v => VR.Native.VrHeight.Enabled = v == 1);
            Toggle("vrDesktopMirror", "Show VR on the monitor", 0,
                "Copies the left eye to the game window every frame. Off: the window stays dark while the headset is " +
                "on, and that copy costs nothing.",
                () => VR.Native.VrPerf.DesktopMirror ? 1 : 0, v => VR.Native.VrPerf.DesktopMirror = v == 1);
            Choice("vrRenderScale", "VR render scale", new[] { "100%", "90%", "80%", "70%", "60%" }, 0,
                "The share of the headset's resolution the game renders. The headset's own resolution (and a stream " +
                "scale such as WiVRn's) sets 100%. Lower is faster and softer. A change in the headset applies at " +
                "once; going back up applies the next time VR starts.",
                () => Math.Min(4, Math.Max(0, (int)Math.Round((1f - VR.Native.VrPerf.RenderScale) * 10f))),
                v => VR.Native.VrPerf.RenderScale = 1f - v * 0.1f);
            Choice("vrMsaa", "VR anti-aliasing", new[] { "4x", "2x", "Your game setting" }, 0,
                "Smooths edges. The game's Ultra preset is 8x, which at headset resolution costs about a third of the " +
                "frame rate (measured on a Quest 3: 28 fps at 8x, 39 fps at 4x). 2x looks a bit rougher and measured " +
                "no faster than 4x. Flat play keeps your game setting.",
                () => VR.Native.VrPerf.MaxMsaa == 2 ? 1 : VR.Native.VrPerf.MaxMsaa <= 0 ? 2 : 0,
                v => VR.Native.VrPerf.MaxMsaa = v == 1 ? 2 : v == 2 ? 0 : 4);
            Toggle("vrSharedMirrors", "Car mirrors render once in VR", 1,
                "The game renders every car mirror once per eye. On: once per frame for both eyes, which is the same " +
                "picture at half the cost.",
                () => VR.Native.VrPerf.SharedMirrors ? 1 : 0, v => VR.Native.VrPerf.SharedMirrors = v == 1);
            Header("Filmmaking",
                "A separate camera for filming. It does not touch the game's own camera, and it never moves your " +
                "character: what the other players see stays exactly the same. The camera key is unbound by default; " +
                "set it under Settings → Controls, or in BepInEx/config/tldrevamp.core.cfg (Keys → CinematicCamera).");
            Choice("cinemaMode", "Camera when it opens", new[] { "Free (fly)", "Orbit (around a point)" }, 0,
                "Free: the camera flies where you take it (W A S D, Q and E for height, the mouse to look).\n\n" +
                "Orbit: the camera circles a point you place in front of it and stays at a fixed distance; the mouse " +
                "turns it around the point and the wheel moves it nearer or further.\n\n" +
                "Press the camera key again to close it.",
                () => Cinematic.CinematicState.TryParseMode(Cinematic.CinematicCamera.Mode.ToString(), out var m) && m == Cinematic.CinematicMode.Orbit ? 1 : 0,
                v => Cinematic.CinematicCamera.DefaultMode = v == 1 ? Cinematic.CinematicMode.Orbit : Cinematic.CinematicMode.Free);
            Choice("cinemaSpeed", "Camera speed", new[] { "Slow", "Normal", "Fast" }, 1,
                "How fast the camera moves and orbits.\n\nSlow: about 2 m/s. Normal: about 8 m/s. Fast: about 24 m/s. " +
                "Hold Shift to go three times faster, or Ctrl to creep.",
                () => Cinematic.CinematicLimits.ClampSpeed(Cinematic.CinematicCamera.Speed) <= 4f ? 0 :
                      Cinematic.CinematicLimits.ClampSpeed(Cinematic.CinematicCamera.Speed) <= 12f ? 1 : 2,
                v => Cinematic.CinematicCamera.Speed = v == 0 ? 2f : v == 1 ? Cinematic.CinematicLimits.DefaultSpeed : 24f);
            Choice("cinemaFov", "Camera field of view", new[] { "Narrow (35°)", "Normal (60°)", "Wide (85°)" }, 1,
                "How much the camera sees. The game's own camera is not changed: this is the filming camera's own view.",
                () => Cinematic.CinematicLimits.ClampFov(Cinematic.CinematicCamera.Fov) <= 45f ? 0 :
                      Cinematic.CinematicLimits.ClampFov(Cinematic.CinematicCamera.Fov) <= 70f ? 1 : 2,
                v => Cinematic.CinematicCamera.Fov = v == 0 ? 35f : v == 1 ? Cinematic.CinematicLimits.DefaultFov : 85f);
            Toggle("cinemaStatus", "Show the camera status", 1,
                "Draws a small line with the camera's mode, field of view and controls while the camera is on. " +
                "It stays up even when the performance overlay is hidden, so you always know the camera is driving.",
                () => Cinematic.CinematicCamera.ShowStatus ? 1 : 0,
                v => Cinematic.CinematicCamera.ShowStatus = v == 1);
            Entries.Add(new Entry { Key = "replayRecording", Label = "Record a replay", Kind = Kind.Toggle, RuntimeOnly = true,
                Help = "Records what happens around you so you can watch it again with the filming camera, like a replay mod. " +
                    "Every player's body and every car, part and item within about 250 m are recorded 20 times a second. " +
                    "Works in single player and multiplayer, and nothing is sent to the other players.\n\n" +
                    "Also Ctrl+F9 (Keys → ReplayRecord in tldrevamp.core.cfg). Stop to save: recordings go to " +
                    "BepInEx/tldrevamp-replays and hold numbers only, no names. A recording stops by itself after an hour.\n\n" +
                    "Lights are recorded. Not recorded yet: sounds, smoke and dust, animals, and dents.",
                Get = () => Replay.ReplayRecorder.Recording ? 1 : 0,
                Set = v => { if (v == 1) Replay.ReplayRecorder.Start(); else Replay.ReplayRecorder.Stop(); } });
            Entries.Add(new Entry { Key = "replayPlaying", Label = "Watch the latest replay", Kind = Kind.Toggle, RuntimeOnly = true,
                Help = "Plays the replay you just recorded, or the newest one saved (also Ctrl+F10). Your character and the world " +
                    "stay where they are: the recorded things are drawn in their place until you leave.\n\n" +
                    "Space play/pause · ← → jump 5 s · ↑ ↓ speed (0.1× to 4×) · 1 free camera · 2 orbit · 3 through the " +
                    "player's eyes · 4 follow · Tab next player · hold Alt and click the timeline to scrub · H hides the " +
                    "timeline · Page Up/Down older or newer replay · Backspace or Ctrl+F10 to leave.",
                Get = () => Replay.ReplayViewer.Active ? 1 : 0,
                Set = v => { if (v == 1) Replay.ReplayViewer.PlayLatest(); else Replay.ReplayViewer.End("closed"); } });
            Toggle("replayBanner", "Show \"Recording\" while a replay records", 1,
                "A small banner in the top-right corner while a replay is recording. Turn it off for clean footage.",
                () => Replay.ReplayRecorder.ShowBanner ? 1 : 0,
                v => Replay.ReplayRecorder.ShowBanner = v == 1);
            Header("Branding",
                "Controls the small development-state mark drawn over the game view.");
            Toggle("showWipMark", "Show WIP mark", 1,
                "Draw a small \"TLD RevaMP <version> · WIP\" label in the bottom-right corner. It appears in screenshots and recordings, so shared footage shows that this build is still work in progress and which version it is.",
                () => Plugin.WipMarkEnabled ? 1 : 0,
                v => Plugin.SetWipMark(v == 1));
            Header("Debugging",
                "Page Down saves a debug report with recent game state, performance/network measurements, numeric settings " +
                "and hardware models. Scroll Lock opens session marks: Enter saves a note, Escape cancels. Browse and rename " +
                "notes from the list. The keys can be changed in BepInEx/config/tldrevamp.core.cfg (DebugReport and Mark).");
            Choice("debugView", "Debug view", new[] { "Off", "Focus", "Problems", "Everything" }, 0,
                "Off: no object debug work.\nFocus: one target card, ghost and links.\nProblems: Focus plus tags on faulty objects.\nEverything: all nearby labels and ghosts. Key: DebugView (unbound).",
                () => (int)DebugView.Mode, v => DebugView.Mode = (DebugViewMode)v);
            Toggle("overlay", "Performance overlay", 1,
                "The small FPS graph in the top left corner (also Page Up).",
                // ReferenceEquals: the game destroys the BepInEx plugin object at startup (Unity's == null is then true),
                // but the plugin's C# fields — the overlay included — live on and are used by the mod's runner.
                () => !ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null && Plugin.Instance.Overlay.Visible ? 1 : 0,
                v => { if (!ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null) Plugin.Instance.Overlay.Visible = v == 1; });
            Toggle("netPackets", "Network packets", 0,
                "In a multiplayer game: a panel in the top right corner with what your game sends and receives each second, " +
                "by kind of message (how many, how many kilobytes, and how many milliseconds your game spends on them).",
                () => Net.NetMeter.Show ? 1 : 0, v => Net.NetMeter.Show = v == 1);
            Toggle("itemTraffic", "Item traffic", 0,
                "In a multiplayer game: download and upload arrows over shared cars, items and mod objects, with how many " +
                "bytes this machine receives and sends each second (also Insert).",
                () => Net.ItemTraffic.Show ? 1 : 0, v => Net.ItemTraffic.SetVisible(v == 1));
            Toggle("ownershipOverlay", "Ownership and sync", 0,
                "World labels: problems only in Problems, full labels in Everything (nearest 40 within 300 m). Enabling from Off opens Everything.",
                () => WorldOverlays.Ownership ? 1 : 0, v => WorldOverlays.Ownership = v == 1);
            Toggle("ownerGhost", "Owner ghost", 0,
                "Owner pose box for the focused object; Problems also shows faulty copies. Locally owned objects have no received owner pose.",
                () => WorldOverlays.Ghost ? 1 : 0, v => WorldOverlays.Ghost = v == 1);
            Toggle("ownerGhostAll", "Owner ghosts for nearby objects", 0,
                "Everything only: with Owner ghost enabled, draw boxes for the nearest 40 shared objects within 300 m.",
                () => WorldOverlays.GhostAll ? 1 : 0, v => WorldOverlays.GhostAll = v == 1);
            Toggle("linkView", "Link view", 0,
                "Focus/Problems: target links only. Everything: nearby lock, hitch, part and rider links; red marks owner or reverse-link faults.",
                () => WorldOverlays.Links ? 1 : 0, v => WorldOverlays.Links = v == 1);
            Toggle("objectComparison", "Live partner comparison", 0,
                "Compare the focused shared object with every session player's view through the host, twice a second. " +
                "Inside the focus card, differences only; net compare all shows all fields. Turn off to stop requests. " +
                "Assign ObjectComparison in the BepInEx key settings; unbound by default.",
                () => ObjectPanels.Compare ? 1 : 0, v => ObjectPanels.Compare = v == 1);
            Toggle("objectEvents", "Focused object event feed", 0,
                "Expand recent events in the focus card from three to six per view (up to 35 seconds); empty histories collapse. " +
                "Local events need no report sharing; partner events require it. " +
                "Assign ObjectEvents in the BepInEx key settings; unbound by default.",
                () => ObjectPanels.Events ? 1 : 0, v => ObjectPanels.Events = v == 1);
            NetworkConditions.Settings();
            Entries.Add(new Entry { Key = "reproRecording", Label = "Repro recorder", Kind = Kind.Toggle, RuntimeOnly = true,
                Help = "Record local multiplayer actions and position/look at 10 Hz (also Ctrl+Insert). Stop to save JSON in the marks session folder. " +
                    "No chat text, command arguments or player identities. Stops at 60,000 rows or on leaving the world. Always off at startup; key: ReproRecorder in tldrevamp.core.cfg.",
                Get = () => ReproRecorder.Recording ? 1 : 0, Set = v => { if (v == 1) ReproRecorder.Start(); else ReproRecorder.Stop(); } });
            Toggle("shareReports", "Share my marks and reports with the players in this session", 0,
                "Share copies of your /mark black boxes and Page Down reports with every other sharing player in this session, " +
                "including the host if they share. You receive their files only while you share yours. " +
                "A host relays between sharing players even when its own sharing is off; it then keeps no copy." +
                "\n\nBlack boxes and reports include hardware models, numeric settings, positions and game state. Notes are privacy-cleaned. " +
                "Screenshots, raw logs, personal identities, chat and paths are excluded. Received filenames use the sending player's session index. " +
                "Note renames also reach sharing players. Files go through the host; the host can inspect data it relays. " +
                "Live comparison also shares your recent object events while this is on; object-state replies are always available. " +
                "\n\nOff by default: your report files and recent events stay local and you receive no report files. This applies to hosts too. " +
                "Turning it on shares new captures only, with the sharing players currently in the session.",
                () => Net.Reports.Share ? 1 : 0, v => Net.Reports.Share = v == 1);
            Toggle("askReports", "Ask players for their reports", 1,
                "When you host: Page Down asks sharing players to capture a report at the same moment. " +
                "Each report goes to all sharing players. You only keep their copies if you share too. " +
                "Received reports are saved in BepInEx/tldrevamp-feedback/players/<player>/.",
                () => Net.Reports.AskPlayers ? 1 : 0, v => Net.Reports.AskPlayers = v == 1);
        }

        private static void Header(string label, string help) =>
            Entries.Add(new Entry { Key = null, Label = label, Help = help, Kind = Kind.Header });

        private static void Toggle(string key, string label, int def, string help, Func<int> get, Action<int> set) =>
            Entries.Add(new Entry { Key = key, Label = label, Help = help, Kind = Kind.Toggle, Default = def, Get = get, Set = set });

        private static void Choice(string key, string label, string[] options, int def, string help, Func<int> get, Action<int> set) =>
            Entries.Add(new Entry { Key = key, Label = label, Help = help, Kind = Kind.Choice, Options = options, Default = def, Get = get, Set = set });

        /// The terrain picked on the new-game screen for the next new game (TerrainNewGameUi).
        internal const string NewGameTerrainKey = "newGameTerrain";

        internal static int Int(string key, int fallback) => IntegerValues.TryGetValue(key, out int value) ? value : fallback;
        internal static string Text(string key, string fallback) => TextValues.TryGetValue(key, out string value) ? value : fallback;
        internal static void SetInt(string key, int value) => IntegerValues[key] = value;
        internal static void SetText(string key, string value) => TextValues[key] = value ?? "";
        internal static void SetChoice(string key, int value)
        {
            var entry = Entries.Find(x => x.Key == key);
            if (entry?.Set != null) entry.Set(Math.Max(0, Math.Min((entry.Options?.Length ?? 1) - 1, value)));
        }

        /// Apply saved values (called once at plugin start; missing keys keep the built-in defaults).
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var saved = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#")) continue;
                    saved[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                foreach (var e in Entries)
                    if (e.Key != null && !e.RuntimeOnly && saved.TryGetValue(e.Key, out string text) && int.TryParse(text, out int v)) e.Set(v);
                if (saved.TryGetValue("debugView", out string modeText) && int.TryParse(modeText, out int mode) && mode == 0) DebugView.Mode = DebugViewMode.Off;
                TextValues.Clear(); IntegerValues.Clear();
                // The host password is not a persisted value: the old plaintext line is removed by the migration
                // (Net.SessionSettingsDefaults.Migrate) and the value lives in memory for this session only.
                if (saved.TryGetValue("hostJoinAtHost", out string joinAtHost) && int.TryParse(joinAtHost, out int join)) IntegerValues["hostJoinAtHost"] = join == 0 ? 0 : 1;
                // The terrain for the next new game is picked on the new-game screen only (never in Settings); the last
                // pick is remembered here. "terrainMode" is the key the Settings row used to write.
                if ((saved.TryGetValue(NewGameTerrainKey, out string terrainText) || saved.TryGetValue("terrainMode", out terrainText))
                    && int.TryParse(terrainText, out int terrain))
                {
                    IntegerValues[NewGameTerrainKey] = terrain;
                    WorldGen.TerrainHooks.Select((WorldGen.TerrainMode)terrain);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("RevampSettings.Load: " + ex.Message); }
        }

        public static void Save()
        {
            try
            {
                var lines = new List<string> { "# TLD Revamp settings (edit in game: Settings → Revamp)" };
                foreach (var e in Entries) if (e.Key != null && !e.RuntimeOnly) lines.Add(e.Key + "=" + e.Get());
                var textKeys = new List<string>(TextValues.Keys); textKeys.Sort(StringComparer.Ordinal);
                foreach (string key in textKeys) lines.Add(key + "=" + (TextValues[key] ?? "").Replace("\r", " ").Replace("\n", " "));
                var intKeys = new List<string>(IntegerValues.Keys); intKeys.Sort(StringComparer.Ordinal);
                foreach (string key in intKeys) lines.Add(key + "=" + IntegerValues[key]);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("RevampSettings.Save: " + ex.Message); }
        }

        public static void ResetToDefaults()
        {
            foreach (var e in Entries) if (e.Key != null) e.Set(e.Default);
            Save();
        }
    }
}
