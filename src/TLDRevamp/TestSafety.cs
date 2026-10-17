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
        /// Leaving the host's world (left, kicked, the host lost): the session is gone but this is still the host's world
        /// until the next scene is in — the menu load is a coroutine a frame later, and if it failed the player would
        /// play on in it with the block already off (Mp.Stop clears InHostWorld) and the next autosave would write the
        /// host's world into their own slot. Cleared when a scene has loaded.
        public static bool LeavingHostWorld = false;
        public static long Blocked;

        public static void Init()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (sc, mode) =>
            {
                if (LeavingHostWorld) Plugin.Log.LogInfo("left the host's world: saving allowed again (" + sc.name + " loaded)");
                LeavingHostWorld = false;
                Net.PlayerSave.SceneLoaded();
            };
        }

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
            if (!BlockAutoSave && !InHostWorld && !LeavingHostWorld) return true;
            Blocked++;
            return false;
        }
    }
}

namespace TLDRevamp
{
    /// A joiner plays in the host's world, and the game's Save menu would write that world into the joiner's own slot
    /// (under their own save's name it replaces their world with the host's). The host owns the shared save; in a
    /// joiner's game, saving does nothing and says why. The host saves as before. Enabled false = before v0.66.9.
    [HarmonyPatch]
    public static class JoinerSaveBlock
    {
        public static bool Enabled = true;
        public static long Blocked;
        private static bool InHostWorld => TestSafety.InHostWorld || TestSafety.LeavingHostWorld;

        /// The Save menu's button (the "saved" sign and the slot list skipped too).
        [HarmonyPrefix, HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.SaveAndRefresh))]
        private static bool SaveButton() => Allow();

        /// Every other way into a save file.
        [HarmonyPrefix, HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        private static bool SaveFile() => Allow();

        /// Test (bridge `mp trysave <name>`): the Save menu's button with this name typed in. Did a file appear? One that
        /// did (the block off) is deleted again, and the game's "last save" goes back to what it was.
        public static string Try(string name)
        {
            var sc = savedatascript.s != null ? savedatascript.s.saveScreen : null;
            if (sc == null) sc = UnityEngine.Object.FindObjectOfType<newSaveScreenScript>(true);
            if (sc == null || sc.TextSaveNameInput == null) return "{\"error\":\"save screen not found\"}";
            var path = pathscript.SaveDataName(name);
            if (System.IO.File.Exists(path)) return "{\"error\":" + Json.Str("a save named " + name + " exists") + "}";
            string lastPath = UnityEngine.PlayerPrefs.GetString("LastSavePath"), lastName = UnityEngine.PlayerPrefs.GetString("LastSaveName"),
                   lastVer = UnityEngine.PlayerPrefs.GetString("LastSaveVersion");
            sc.TextSaveNameInput.GetType().GetProperty("text").SetValue(sc.TextSaveNameInput, name);
            long b0 = Blocked;
            string err = "";
            try { sc.SaveAndRefresh(); } catch (System.Exception e) { err = e.GetType().Name + ": " + e.Message; }
            bool written = System.IO.File.Exists(path);
            if (written)
            {
                pathscript.Delete(path); pathscript.Delete(pathscript.SavePropertiesName(name)); pathscript.Delete(pathscript.SaveThumbnailName(name));
                UnityEngine.PlayerPrefs.SetString("LastSavePath", lastPath); UnityEngine.PlayerPrefs.SetString("LastSaveName", lastName);
                UnityEngine.PlayerPrefs.SetString("LastSaveVersion", lastVer); UnityEngine.PlayerPrefs.Save();
            }
            return "{\"written\":" + (written ? "true" : "false") + ",\"blocked\":" + (Blocked - b0) + ",\"inHostWorld\":" + (InHostWorld ? "true" : "false") +
                   ",\"fault\":" + Json.Str(err) + "}";
        }

        private static bool Allow()
        {
            if (!Enabled || !InHostWorld) return true;
            Blocked++;
            Plugin.Log.LogInfo("save refused: this is the host's world (JoinerSaveBlock)");
            Net.Session.Show(1, "", "You can't save in someone else's world: the host saves it. Your own saves are untouched.");
            return false;
        }
    }
}
