using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The host checks what players report about themselves and their cars (architecture §4a: "owner-reported state
    /// needs plausibility checks on the server"). A player's position and a car's state are the owner's word; a
    /// modified client could move itself or its car anywhere, at any speed. The host now flags:
    ///  - a player on foot who moved farther between two states than anyone can walk, run or fall (a teleport);
    ///  - a car whose reported speed is beyond the game's own speed cap (mainscript.maxCarVelocity), or that moved
    ///    farther between two states than its speeds allow (a teleport).
    /// What the host itself ordered is not flagged: a teleport request it accepted (Tpa) gives that player and its
    /// things a grace period. Flags are logged ("[plausibility]") and the host sees one line in its chat per player at
    /// most every AnnounceS. Like a different game build, a flag never refuses anything unless Enforce is on (then the
    /// state is dropped: the copies keep the last plausible one).
    /// Runtime switches (bridge `set`): TLDRevamp.Net.Plausibility.Enabled, .Enforce
    public static class Plausibility
    {
        public static bool Enabled = true;
        public static bool Enforce = false;
        public static float FootSlackM = 40f, FootSpeedMS = 25f;     // on foot: a fall or a ragdoll fling stays well inside
        public static float CarSlackM = 40f, CarSpeedSlackMS = 15f;
        public static float GraceS = 20f, AnnounceS = 60f, MinJumpM = 100f;
        // the time a jump may have taken, at most: a car at rest sends nothing, so its last state can be minutes old —
        // a teleport after that would otherwise count as minutes of driving (plausibility.py C, first run)
        public static float MaxGapS = 10f;
        public static long Checked, PlayerJumps, CarJumps, CarSpeeding, Graced, Dropped;
        public static string Last = "";

        private sealed class Track { public double X, Z; public float At; public bool Has; }
        private static readonly Dictionary<int, Track> _players = new Dictionary<int, Track>();
        private static readonly Dictionary<uint, Track> _cars = new Dictionary<uint, Track>();
        private static readonly Dictionary<int, float> _graceUntil = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _announced = new Dictionary<int, float>();

        public static void Reset() { _players.Clear(); _cars.Clear(); _graceUntil.Clear(); _announced.Clear(); }

        /// The host sent this player somewhere (an accepted teleport request): its next jumps are the host's doing.
        internal static void Grace(int player) => _graceUntil[player] = Time.realtimeSinceStartup + GraceS;
        internal static void Forget(int player) { _players.Remove(player); _graceUntil.Remove(player); _announced.Remove(player); }

        private static bool InGrace(int player, float now) => _graceUntil.TryGetValue(player, out var u) && now < u;

        /// A player's state (MpServer, Protocol.State). False: drop it (Enforce only).
        internal static bool PlayerState(int player, string name, Vector3d pos, bool seated, bool onSomething)
        {
            if (!Enabled) return true;
            float now = Time.realtimeSinceStartup;
            if (!_players.TryGetValue(player, out var t)) _players[player] = t = new Track();
            bool ok = true;
            if (t.Has && !seated && !onSomething)
            {
                Checked++;
                double d = Flat(pos, t);
                float dt = Mathf.Clamp(now - t.At, 0f, MaxGapS);
                double allowed = FootSlackM + FootSpeedMS * (dt + 1f);
                if (d > allowed && d > MinJumpM)
                {
                    if (InGrace(player, now)) Graced++;
                    else { PlayerJumps++; ok = Flag(player, name, $"moved {Dist(d)} on foot in {dt:F1} s"); }
                }
            }
            if (ok || !Enforce) { t.X = pos.x; t.Z = pos.z; t.At = now; t.Has = true; }
            return ok || !Enforce;
        }

        /// A car's state from its owner (Entities.ServerReceive State). False: drop it (Enforce only).
        internal static bool CarState(int owner, string name, uint net, Vector3d pos, float speed, float lastSpeed, bool stored, bool held)
        {
            if (!Enabled || owner == 0) return true;   // the host's own word is the host's business
            float now = Time.realtimeSinceStartup;
            if (!_cars.TryGetValue(net, out var t)) _cars[net] = t = new Track();
            bool ok = true;
            if (!stored && !held)
            {
                Checked++;
                float cap = MaxCarSpeed();
                bool grace = InGrace(owner, now);
                if (speed > cap + CarSpeedSlackMS)
                {
                    if (grace) Graced++;
                    else { CarSpeeding++; ok = Flag(owner, name, $"object {net} reported {speed * 3.6f:F0} km/h (the game's cap is {cap * 3.6f:F0})"); }
                }
                if (t.Has)
                {
                    double d = Flat(pos, t);
                    float dt = Mathf.Clamp(now - t.At, 0f, MaxGapS);
                    double allowed = CarSlackM + (Mathf.Max(speed, lastSpeed) + CarSpeedSlackMS) * (dt + 1f);
                    if (d > allowed && d > MinJumpM)
                    {
                        if (grace) Graced++;
                        else { CarJumps++; ok = Flag(owner, name, $"object {net} moved {Dist(d)} in {dt:F1} s") && ok; }
                    }
                }
            }
            if (ok || !Enforce) { t.X = pos.x; t.Z = pos.z; t.At = now; t.Has = true; }
            return ok || !Enforce;
        }

        internal static void ForgetCar(uint net) => _cars.Remove(net);

        private static float MaxCarSpeed()
        {
            var m = mainscript.s;
            return m != null && m.maxCarVelocity > 1f ? m.maxCarVelocity : 70f;
        }

        private static bool Flag(int player, string name, string what)
        {
            Last = name + ": " + what;
            Plugin.Log.LogWarning("[plausibility] player " + player + " (" + name + ") " + what + (Enforce ? " - dropped" : ""));
            float now = Time.realtimeSinceStartup;
            if (!_announced.TryGetValue(player, out var at) || now - at > AnnounceS)
            {
                _announced[player] = now;
                Session.Show(1, "", name + ": implausible movement (" + what + ")" + (Enforce ? ", ignored." : "."));
            }
            if (Enforce) Dropped++;
            return false;
        }

        private static double Flat(Vector3d p, Track t) { double dx = p.x - t.X, dz = p.z - t.Z; return System.Math.Sqrt(dx * dx + dz * dz); }
        private static string Dist(double m) => m >= 1000 ? (m / 1000).ToString("F1") + " km" : m.ToString("F0") + " m";

        public static string Stats() =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"enforce\":" + (Enforce ? "true" : "false") + ",\"checked\":" + Checked +
            ",\"playerJumps\":" + PlayerJumps + ",\"carJumps\":" + CarJumps + ",\"carSpeeding\":" + CarSpeeding + ",\"graced\":" + Graced +
            ",\"dropped\":" + Dropped + ",\"capKmh\":" + (MaxCarSpeed() * 3.6f).ToString("F0") + ",\"last\":" + Json.Str(Last) + "}";
    }
}
