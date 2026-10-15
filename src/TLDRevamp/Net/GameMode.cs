using HarmonyLib;

namespace TLDRevamp.Net
{
    /// The host's game mode for everyone in its world (v0.67.0). The game's own settings (gamemodesettingsscript.S:
    /// survival, peaceful enemies, biome, homesick mode) were the joiner's own: in the host's world a joiner could play
    /// without hunger while the host starved, or generate it with another biome. The host now sends its mode in
    /// Welcome and the joiner takes it before the world loads — in memory only: its own settings file is never
    /// written while it plays in the host's world, and its own mode comes back when it leaves. Friends see the mode
    /// on the host's card in the multiplayer menu before they join (rich presence). Sync false = before.
    public static class GameMode
    {
        public static bool Sync = true;
        public struct Mode { public int Survival, Peaceful, Biome, Homesick; }
        private static Mode? _own;   // the joiner's own, while it plays with the host's
        private static Mode _host;
        public static long Reapplied;
        public static long Applied, Restored, SavesBlocked;

        public static bool Local(out Mode m)
        {
            m = default;
            var g = gamemodesettingsscript.s;
            if (g == null || g.S == null) return false;
            m = new Mode { Survival = g.S.survival, Peaceful = g.S.peacefulEnemies, Biome = g.S.biome, Homesick = g.S.map.homesickMode };
            return true;
        }

        internal static void Write(NetWriter w)
        {
            Local(out var m);
            w.I32(m.Survival); w.I32(m.Peaceful); w.I32(m.Biome); w.I32(m.Homesick);
        }

        internal static Mode Read(NetReader r) => new Mode { Survival = r.I32(), Peaceful = r.I32(), Biome = r.I32(), Homesick = r.I32() };

        /// The joiner, from Welcome, before the host's world loads.
        internal static void TakeHost(Mode host)
        {
            if (!Sync || host.Survival == 0) return;   // 0: a host that sent none
            var g = gamemodesettingsscript.s;
            if (g == null || g.S == null || !Local(out var own)) return;
            if (_own == null) _own = own;
            _host = host;
            Set(g, host);
            Applied++;
            Plugin.Log.LogInfo("game mode: the host's — " + Describe(host) + " (own: " + Describe(_own.Value) + ")");
        }

        /// Leaving the host's world: the joiner's own mode again.
        internal static void RestoreOwn()
        {
            if (_own == null) return;
            var g = gamemodesettingsscript.s;
            if (g != null && g.S != null) Set(g, _own.Value);
            _own = null;
            Restored++;
        }

        private static void Set(gamemodesettingsscript g, Mode m)
        {
            g.S.survival = m.Survival; g.S.peacefulEnemies = m.Peaceful; g.S.biome = m.Biome;
            var map = g.S.map; map.homesickMode = m.Homesick; g.S.map = map;   // a struct: copied back
        }

        /// The world load reads the settings file again (LoadSettings): the host's mode on top, while we play in its world.
        [HarmonyPatch(typeof(gamemodesettingsscript), nameof(gamemodesettingsscript.LoadSettings))]
        private static class AfterLoad
        {
            [HarmonyPostfix]
            private static void Postfix(gamemodesettingsscript __instance)
            {
                if (_own == null || __instance == null || __instance.S == null) return;
                Set(__instance, _host);
                Reapplied++;
            }
        }

        /// Never the host's mode into the joiner's own settings file.
        [HarmonyPatch(typeof(gamemodesettingsscript), nameof(gamemodesettingsscript.SaveSettings))]
        private static class KeepOwnFile
        {
            [HarmonyPrefix]
            private static bool Prefix() { if (_own == null) return true; SavesBlocked++; return false; }
        }

        /// For people: "Survival on, enemies peaceful, biome Desert".
        public static string Describe(Mode m)
        {
            string biome = "biome " + m.Biome;
            try
            {
                var g = gamemodesettingsscript.s;
                var opts = g != null && g.dropdrops != null && g.dropdrops.Count > 1 && g.dropdrops[1] != null ? g.dropdrops[1].dropdown.options : null;
                if (opts != null && m.Biome >= 0 && m.Biome < opts.Count && !string.IsNullOrEmpty(opts[m.Biome].text)) biome = "biome " + opts[m.Biome].text;
            }
            catch { }
            return "Survival " + (m.Survival == 2 ? "on" : "off") + ", enemies " + (m.Peaceful == 2 ? "peaceful" : "hostile") + ", " + biome +
                   (m.Homesick == 2 ? ", homesick" : "");
        }

        /// Rich presence: "s,p,b,h".
        internal static string Code() => Local(out var m) ? m.Survival + "," + m.Peaceful + "," + m.Biome + "," + m.Homesick : "";
        internal static bool FromCode(string s, out Mode m)
        {
            m = default;
            var f = (s ?? "").Split(',');
            return f.Length == 4 && int.TryParse(f[0], out m.Survival) && int.TryParse(f[1], out m.Peaceful) && int.TryParse(f[2], out m.Biome) && int.TryParse(f[3], out m.Homesick);
        }

        public static string Stats() => "{\"sync\":" + (Sync ? "true" : "false") + ",\"local\":" + Json.Str(Local(out var m) ? Describe(m) : "") +
                                        ",\"own\":" + Json.Str(_own != null ? Describe(_own.Value) : "") + ",\"applied\":" + Applied + ",\"restored\":" + Restored +
                                        ",\"savesBlocked\":" + SavesBlocked + ",\"reapplied\":" + Reapplied + "}";
    }
}
