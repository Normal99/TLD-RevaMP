using System.Collections.Generic;
using UnityEngine;

namespace HeadUnit
{
    /// The names of the game's radio stations, from The Long Drive wiki's "Radio Stations" pages (read 2026-10-15),
    /// by band and frequency. The game's own list (mainscript.radioChs / radioChsAM) says which stations exist and
    /// where; a station the wiki doesn't know keeps the game's name, or just its frequency.
    public static class Stations
    {
        public struct Info { public float Fr; public string Short, Full; }

        private static readonly Info[] Fm =
        {
            new Info { Fr = 88.8f, Short = "Joke Radio", Full = "Hungarian Joke Radio" },
            new Info { Fr = 89.2f, Short = "Acoustic", Full = "acoustic and light rock" },
            new Info { Fr = 90.5f, Short = "Classical", Full = "classical music" },
            new Info { Fr = 91.2f, Short = "Funk", Full = "funk" },
            new Info { Fr = 93.0f, Short = "Hungaria", Full = "Hungária Rádió (rock and metal)" },
            new Info { Fr = 94.1f, Short = "Steel Pants", Full = "Steel Pants Radio" },
            new Info { Fr = 95.0f, Short = "Chat 'til You Die", Full = "Chat 'til You Die" },
            new Info { Fr = 96.6f, Short = "Angry Greg", Full = "Angry Greg Rage Radio" },
            new Info { Fr = 98.4f, Short = "Neon Synth", Full = "Neon Synth Radio" },
            new Info { Fr = 100.5f, Short = "Poem Chill", Full = "Poem Chill Radio" },
            new Info { Fr = 105.0f, Short = "CB chatter", Full = "CB chatter (Spanish)" },
        };

        private static readonly Info[] Am =
        {
            new Info { Fr = 630f, Short = "Rock", Full = "rock" },
            new Info { Fr = 800f, Short = "Oldie Radio", Full = "Oldie Radio" },
            new Info { Fr = 1080f, Short = "Custom Radio", Full = "Custom Radio (your own music)" },
            new Info { Fr = 1330f, Short = "King Matthias", Full = "King Matthias Radio" },
        };

        /// The wiki's name for a station of the game's list (short: for the station buttons).
        public static string Name(bool am, mainscript.radioCH ch, bool full)
        {
            float tol = am ? 6f : 0.15f;
            foreach (var i in am ? Am : Fm)
                if (Mathf.Abs(i.Fr - ch.FR) <= tol) return full ? i.Full : i.Short;
            if (ch.custom) return "Custom Radio";
            // the one FM station the wiki has no frequency for
            string n = ch.name ?? "";
            if (n.IndexOf("ber", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("german", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "Übermensch Radio";
            if (n.Length > 0 && !float.TryParse(n, out _)) return full || n.Length <= 16 ? n : n.Substring(0, 16);
            return am ? $"AM {ch.FR:0}" : $"FM {ch.FR:0.0}";
        }

        /// The band's stations, low to high.
        public static List<mainscript.radioCH> Band(bool am)
        {
            var src = mainscript.s == null ? null : am ? mainscript.s.radioChsAM : mainscript.s.radioChs;
            var l = src != null ? new List<mainscript.radioCH>(src) : new List<mainscript.radioCH>();
            l.RemoveAll(c => c == null);
            l.Sort((a, b) => a.FR.CompareTo(b.FR));
            return l;
        }

        public static string Freq(bool am, float fr) => am ? $"{fr:0}" : $"{fr:0.0}";
    }
}
