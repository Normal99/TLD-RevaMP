using HarmonyLib;

namespace TLDRevamp
{
    /// Runtime-only guards for automated tests. Nothing here is saved to disk.
    ///
    /// BlockAutoSave: test worlds must not autosave (they would replace the player's autosave slots). Earlier the test
    /// tools switched the game's own setting (settingsscript.S.BAutoSave) off instead; the game writes its settings file
    /// whenever the settings menu closes, so that "off" reached the player's settings.tldc on 2026-09-25 and turned
    /// autosave off in their real game. This flag lives only in the mod's memory and is reset on every game start.
    [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.AutoSave))]
    public static class TestSafety
    {
        public static bool BlockAutoSave = false;
        /// Multiplayer's own block (the client plays in the host's world; the host owns the save). Separate from the
        /// test flag: leaving a session cleared the shared flag, and a test world left running after `mp stop` then
        /// autosaved into the laptop's slots after the test had restored them (2026-10-01, found from the slot times).
        public static bool InHostWorld = false;
        public static long Blocked;

        /// Load a named save through the game's own save screen (bridge `loadsave <name>`). Autosave is blocked first so
        /// a test session can never write into the player's autosave slots.
        public static string LoadSave(string name)
        {
            var path = pathscript.SaveDataName(name);
            if (!System.IO.File.Exists(path)) return "{\"error\":" + Json.Str("no save named " + name) + "}";
            var screen = savedatascript.s != null ? savedatascript.s.saveScreen : null;
            if (screen == null) screen = UnityEngine.Object.FindObjectOfType<newSaveScreenScript>(true);
            if (screen == null) return "{\"error\":\"save screen not found\"}";
            BlockAutoSave = true;
            screen.Load(path);
            return "{\"loading\":" + Json.Str(path) + "}";
        }

        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!BlockAutoSave && !InHostWorld) return true;
            Blocked++;
            return false;
        }
    }
}
