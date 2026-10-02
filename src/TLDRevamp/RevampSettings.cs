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
            public string[] Options;          // Choice
            public int Default;               // Toggle: 0/1, Choice: index
            public Func<int> Get;
            public Action<int> Set;
        }

        public static readonly List<Entry> Entries = new List<Entry>();

        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "tldrevamp-settings.txt");

        static RevampSettings()
        {
            Header("Visual trade-offs",
                "These change what you see, a little, in exchange for speed. Each one was checked in game and chosen so " +
                "the default looks the same as the original during normal play.");
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
            Toggle("binoculars", "Binocular lens only when seen", 1,
                "Binoculars render the view through their lens all the time, even lying in a house far away." +
                "\n\nOn: the lens is only rendered while it's on screen (+3% FPS).",
                () => BinocularLensCamera.Enabled ? 1 : 0, v => BinocularLensCamera.Enabled = v == 1);
            Toggle("overlay", "Performance overlay", 1,
                "The small FPS graph in the top left corner (also F8). F9 saves a feedback report for the mod author.",
                // ReferenceEquals: the game destroys the BepInEx plugin object at startup (Unity's == null is then true),
                // but the plugin's C# fields — the overlay included — live on and are used by the mod's runner.
                () => !ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null && Plugin.Instance.Overlay.Visible ? 1 : 0,
                v => { if (!ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null) Plugin.Instance.Overlay.Visible = v == 1; });

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
            Toggle("pour", "Pause idle pour effects", 1,
                "Every canister, barrel and bottle keeps an invisible pouring effect running. It's paused until you actually pour." +
                "\n\n+9% FPS around many containers.",
                () => IdlePourPause.Enabled ? 1 : 0, v => IdlePourPause.Enabled = v == 1);
            Header("Multiplayer",
                "Voice and text chat with the other players. Host or join from the game's Multiplayer button (or F7).");
            Choice("voice", "Voice chat", new[] { "Push to talk", "Open microphone", "Off" }, 0,
                "Push to talk: hold the game's \"Voice chat\" key (Settings → Controls; V if you never set one)." +
                "\nOpen microphone: Steam sends your voice whenever you speak." +
                "\n\nYour microphone is the one chosen in Steam (Steam → Settings → Voice). Players hear you within about " +
                "60 m, from where you stand.",
                () => (int)Net.Voice.Setting, v => Net.Voice.Setting = (Net.Voice.Mode)Math.Max(0, Math.Min(2, v)));
            Choice("voiceVolume", "Voice volume", new[] { "25%", "50%", "75%", "100%", "150%" }, 3,
                "How loud the other players' voices are.",
                () => Net.Voice.Volume <= 0.3f ? 0 : Net.Voice.Volume <= 0.6f ? 1 : Net.Voice.Volume <= 0.8f ? 2 : Net.Voice.Volume <= 1.1f ? 3 : 4,
                v => Net.Voice.Volume = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f }[Math.Max(0, Math.Min(4, v))]);
        }

        private static void Header(string label, string help) =>
            Entries.Add(new Entry { Key = null, Label = label, Help = help, Kind = Kind.Header });

        private static void Toggle(string key, string label, int def, string help, Func<int> get, Action<int> set) =>
            Entries.Add(new Entry { Key = key, Label = label, Help = help, Kind = Kind.Toggle, Default = def, Get = get, Set = set });

        private static void Choice(string key, string label, string[] options, int def, string help, Func<int> get, Action<int> set) =>
            Entries.Add(new Entry { Key = key, Label = label, Help = help, Kind = Kind.Choice, Options = options, Default = def, Get = get, Set = set });

        /// Apply saved values (called once at plugin start; missing keys keep the built-in defaults).
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var saved = new Dictionary<string, int>();
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#")) continue;
                    if (int.TryParse(line.Substring(eq + 1).Trim(), out int v)) saved[line.Substring(0, eq).Trim()] = v;
                }
                foreach (var e in Entries)
                    if (e.Key != null && saved.TryGetValue(e.Key, out int v)) e.Set(v);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("RevampSettings.Load: " + ex.Message); }
        }

        public static void Save()
        {
            try
            {
                var lines = new List<string> { "# TLD Revamp settings (edit in game: Settings → Revamp)" };
                foreach (var e in Entries) if (e.Key != null) lines.Add(e.Key + "=" + e.Get());
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
