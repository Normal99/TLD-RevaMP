using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    /// A digital head unit for the car's radio slot (an example mod for TLD Revamp's multiplayer API).
    ///  - Rare loot, in a few models; each model fits only some cars (Units.cs).
    ///  - The game's radio keeps working inside it (it is the game's radio item with a screen added).
    ///  - YouTube with a shared queue: everyone in or near the car sees the same video at the same moment. Nothing is
    ///    streamed between players: each machine plays the video itself, from a shared position on the session clock
    ///    (Video.cs, State.cs).
    ///  - Navigation: a framework that reads the road ahead (Nav.cs); the first provider shows bends and descents.
    ///  - A touch screen like a car's head unit (Touch.cs): look at a button and click, as with the game's knobs. The
    ///    radio's knobs are on it too. A text field opens only to type a link.
    [BepInPlugin(Guid, "Head Unit", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.headunit";
        public const string Version = "0.2.1";

        internal static ManualLogSource Log;
        internal static ModInfo Mod;
        /// The object coroutines run on: our own (the game destroys the plugin's, so one started there never ran).
        internal static Runner Runner;
        internal static UnitStateType StateType;

        internal static ConfigEntry<float> VideoVolume;
        internal static ConfigEntry<int> MaxHeight;
        internal static ConfigEntry<bool> Convert;

        private void Awake()
        {
            Log = Logger;
            VideoVolume = Config.Bind("Video", "Volume", 0.5f, new ConfigDescription("Video loudness on this machine (the head unit's VOL buttons): each player sets their own", new AcceptableValueRange<float>(0f, 1f)));
            MaxHeight = Config.Bind("Video", "Resolution", 720, new ConfigDescription("Highest video resolution to download (lines: 360, 480, 720 or 1080). Above 360 needs ffmpeg, offered with yt-dlp", new AcceptableValueRange<int>(144, 1080)));
            Convert = Config.Bind("Video", "ConvertToVP8", false, "Convert each video to VP8 after downloading it: for Steam's own Proton, which has no H.264 decoder (turned on by itself the first time a video doesn't decode here; needs ffmpeg)");
            Mod = MpApi.RegisterMod("headunit", "Head Unit", Version);
            StateType = MpApi.RegisterType(Mod, new UnitStateType());
            Ytdlp.Init(Paths.ConfigPath, Paths.CachePath);

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            // The game destroys BepInEx's manager object (this plugin's) once its scene loads, which silently stops
            // Update and OnGUI here: the per-frame work runs on an object of our own (as TLD Revamp's does).
            var go = new GameObject("HeadUnitRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            Runner = go.AddComponent<Runner>();
            Application.quitting += () => { Ytdlp.KillAll(); Ytdlp.Clear(); };   // a VP8 conversion went on for minutes after the game closed
            Log.LogInfo($"Head Unit {Version}: multiplayer API {(MpApi.Available ? "found" : "not found (single player only)")}");
        }
    }

    public sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            // each on its own: one that throws doesn't stop the others for the frame
            try { Controls.Tick(); } catch (System.Exception e) { Fault("controls", e); }
            try { Ytdlp.Tick(); } catch (System.Exception e) { Fault("yt-dlp", e); }
            try { Video.Tick(); } catch (System.Exception e) { Fault("video", e); }
        }

        private float _faultAt = -100f;
        private void Fault(string what, System.Exception e)
        {
            if (Time.realtimeSinceStartup - _faultAt < 10f) return;   // once in a while, not every frame
            _faultAt = Time.realtimeSinceStartup;
            Plugin.Log.LogError(what + ": " + e);
        }

        private void OnGUI() => Panel.Draw();
    }
}
