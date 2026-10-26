using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;

namespace RoadEnd
{
    /// Whether the running world has an end, and where. It is chosen for each new game on the new-game screen
    /// (NewGameUi), off unless picked, and kept with the world: a list next to the game's saves holds the seed of each
    /// world started with it, with its distance and direction, so loading a save of that world brings its end back.
    ///
    /// The game's saves are not touched. A world is found by its seed alone, so two worlds started with the same seed
    /// share the choice made for the later one.
    [HarmonyPatch]
    public static class World
    {
        public static bool Active;                 // this world has an end
        public static double Km = 5000;            // where (from the world's own entry; new games take the config's)
        public static int Sign = 1;
        public static string Source = "none";      // "new game", "save", "host"

        /// What the new-game screen says for the next new game. Off at every start of the game.
        public static bool ChosenForNewGame;

        public const string FileName = "RoadEnd-worlds.txt";

        static string ListPath()
        {
            try { return Path.Combine(pathscript.SavePath(), FileName); }
            catch { return null; }
        }

        /// seed -> (km, sign) of every world started with an end.
        public static Dictionary<int, (double km, int sign)> Read()
        {
            var d = new Dictionary<int, (double, int)>();
            var p = ListPath();
            if (p == null || !File.Exists(p)) return d;
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(' ');
                if (f.Length < 3 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) continue;
                if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double km)) continue;
                d[seed] = (km, f[2] == "S" ? -1 : 1);
            }
            return d;
        }

        static void Write(Dictionary<int, (double km, int sign)> d)
        {
            var p = ListPath();
            if (p == null) return;
            var lines = new List<string> { "# Road End: the worlds started with an end of the road (seed, km, N or S). Delete a line to take a world's end away." };
            foreach (var kv in d)
                lines.Add(kv.Key.ToString(CultureInfo.InvariantCulture) + " " + kv.Value.km.ToString("0.###", CultureInfo.InvariantCulture) + " " + (kv.Value.sign < 0 ? "S" : "N"));
            File.WriteAllLines(p, lines);
        }

        /// Is a world with this seed one with an end (as a save of it would load)?
        public static bool Has(int seed) => Read().ContainsKey(seed);

        /// The map starts generating: a new game takes the new-game screen's choice (and is written down), a loaded
        /// game what was written down for its seed.
        [HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.FStart))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void BeforeMapStart(int _seed, bool _newGame)
        {
            try { Start(_seed, _newGame); }
            catch (Exception e) { Plugin.Once("world choice", e); Active = false; }
        }

        public static void Start(int seed, bool newGame)
        {
            // a joiner's world is the host's: it comes from the session (never a new game here) right after the join
            if (!newGame && _hostValue != null && (DateTime.UtcNow - _hostAt).TotalSeconds < 120)
            {
                var f = _hostValue.Split(' ');
                Active = Plugin.Enabled && f.Length >= 3 && f[0] == "on";
                if (Active) { double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Km); Sign = f[2] == "S" ? -1 : 1; }
                Source = "host";
                _hostValue = null;
                Plugin.Log.LogInfo($"world {seed}: the host's, the end of the road is {(Active ? $"on, at about {Km:0.###} km" : "off")}");
                return;
            }
            var list = Read();
            if (newGame)
            {
                Active = Plugin.Enabled && ChosenForNewGame;
                Km = Plugin.EndKm; Sign = Plugin.Sign; Source = "new game";
                if (Active) list[seed] = (Km, Sign); else list.Remove(seed);
                Write(list);
            }
            else if (list.TryGetValue(seed, out var w))
            {
                Active = Plugin.Enabled; Km = w.km; Sign = w.sign; Source = "save";
            }
            else { Active = false; Source = "save"; }
            Plugin.Log.LogInfo($"world {seed} ({Source}): the end of the road is {(Active ? $"on, at about {Km:0.###} km {(Sign > 0 ? "north" : "south")}" : "off")}");
        }

        /// The multiplayer session setting: what this world is (the host's value reaches every joiner).
        public static string Describe() => Active ? $"on {Km.ToString("0.###", CultureInfo.InvariantCulture)} {(Sign < 0 ? "S" : "N")}" : "off";

        static string _hostValue;
        static DateTime _hostAt;

        /// The host's value, for the world this machine joins next (applied when it starts: Start).
        public static void FromHost(string v) { _hostValue = v; _hostAt = DateTime.UtcNow; }
    }
}
