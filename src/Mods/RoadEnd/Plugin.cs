using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RoadEnd
{
    /// The long drive's road ends: the main road (the one with the power poles) stops at a fixed distance from the
    /// start, and where it stops stands the game's own roadside grave, with a note on the ground in front of it. The
    /// land, the side roads and everything on them go on, so people can still drive on.
    ///
    /// It is chosen for each new game on the game's new-game screen (NewGameUi), off unless picked, and each world keeps
    /// its own choice (World). A separate mod, not part of TLD Revamp. With TLD Revamp's multiplayer it registers
    /// itself, so the join check makes every player have it, and the host's world decides whether it has an end.
    [BepInPlugin(Guid, "Road End", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.roadend", Version = "0.1.0";

        internal static ManualLogSource Log;
        public static bool Enabled = true, Verbose;
        public static int Sign = 1;                 // the way the road is driven from the start: +1 north (+z), -1 south
        public static double EndKm = 5000;
        public static double ObjectClearance = 15;     // no tree or rock this close to the end of the road
        public static double BuildingClearance = 80;   // no building this close to it
        public static double GraveDistance = 6, NoteDistance = 2.5;
        public static float GraveYaw = 0f, NoteYaw = 0f, NoteWidth = 0.6f;
        public static string Text = "Sorry she died....", FontName = "";

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("End", "Enabled", true, "Off: the mod does nothing, and the new-game screen doesn't offer the end of the road.").Value;
            EndKm = Config.Bind("End", "DistanceKm", 5000.0,
                "Where the main road ends in new worlds (each world keeps the distance it was started with), in km from the start along the main road. The game builds its road in 10 km pieces; the end goes " +
                "on the stretch between two of them, so values like 4995 or 5005 km are exact and others move to the nearest such stretch " +
                "(at most about 5 km; the log says where).").Value;
            string dir = Config.Bind("End", "Direction", "North",
                new ConfigDescription("Which way along the main road the end is in new worlds, from the start: North or South.",
                    new AcceptableValueList<string>("North", "South"))).Value;
            Sign = dir == "South" ? -1 : 1;
            Text = Config.Bind("Grave", "NoteText", "Sorry she died....", "What the note in front of the grave says.").Value;
            Verbose = Config.Bind("Debug", "Verbose", false, "Log every road the end cuts.").Value;
            EndKm = Math.Max(1.0, EndKm);
            EndPlan.Reset();

            var go = new GameObject("RoadEndRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();

            bool multiplayer = RegisterWithMultiplayerApi(Version);
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Road End {Version}: multiplayer API {(multiplayer ? "registered" : "not found; local play only")}");
        }

        private static bool RegisterWithMultiplayerApi(string identity)
        {
            var api = Type.GetType("TLDRevamp.Multiplayer.MpApi, TLDRevamp.Multiplayer", false);
            var register = api?.GetMethod("RegisterMod", new[] { typeof(string), typeof(string), typeof(string), typeof(Assembly) });
            if (register == null) return false;
            var mod = register.Invoke(null, new object[] { "roadend", "Road End", identity, typeof(Plugin).Assembly });
            // each world has its own end or none: the host's world decides for everyone in the session
            var setting = api.GetMethod("RegisterSetting");
            if (mod != null && setting != null)
                setting.Invoke(null, new object[] { mod, "world", (Func<string>)World.Describe, (Action<string>)World.FromHost });
            return true;
        }

        static readonly HashSet<string> _logged = new HashSet<string>();
        internal static void Once(string what, Exception e)
        {
            if (_logged.Add(what)) Log.LogError(what + " failed: " + e);
        }

        /// The game destroys BepInEx's manager object, so a plugin's own Update never runs: this object does.
        private class Runner : MonoBehaviour
        {
            private void LateUpdate()
            {
                try { Grave.Tick(); }
                catch (Exception e) { Once("note", e); }
            }
        }
    }
}
