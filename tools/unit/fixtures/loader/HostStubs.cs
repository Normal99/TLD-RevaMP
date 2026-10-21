// The minimum the loader host needs from the game and BepInEx, so the REAL
// src/TLDRevamp/ModHost.cs can run in a test without the game.
//
// The game's own class names are aliased, never defined here: the repository
// must not contain the game's code, and tools/privacy_scan.py checks for it.
global using menuhandler = TLDRevamp.Fixtures.TestMenu;
global using mainscript = TLDRevamp.Fixtures.TestMain;

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Rect { public float x, y, width, height; }
    public class Object { public string name; }
    public static class Debug
    {
        public static readonly List<string> Logged = new List<string>();
        public static void Log(object o) { Logged.Add("log: " + o); }
        public static void LogError(object o) { Logged.Add("error: " + o); }
        public static void LogWarning(object o) { Logged.Add("warning: " + o); }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager
    {
        /// The test sets this to the menu scene or the game scene.
        public static string ActiveSceneName = "menu";
        public static Scene GetActiveScene() => new Scene { name = ActiveSceneName };
    }
}

namespace BepInEx
{
    public static class Paths
    {
        /// The test points these at a temporary BepInEx folder.
        public static string BepInExRootPath = "";
        public static string ConfigPath = "";
    }
}

namespace BepInEx.Logging
{
    public class Logger
    {
        public void LogInfo(object o) { UnityEngine.Debug.Log(o); }
        public void LogWarning(object o) { UnityEngine.Debug.LogWarning(o); }
        public void LogError(object o) { UnityEngine.Debug.LogError(o); }
        public static Logger CreateLogSource(string name) => new Logger();
    }
}

namespace TLDRevamp.Fixtures
{
    /// The menu the game is in; the test flips the scene name instead.
    public static class TestMenu
    {
        public const string sceneName = "game";
    }

    /// The game's player; the test creates one to make the world "up".
    public class TestMain
    {
        public static TestMain s;
        public Player player;
    }

    public class Player { }
}

namespace TLDRevamp
{
    static class Json
    {
        internal static string Str(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    class Plugin
    {
        public static BepInEx.Logging.Logger Log = new BepInEx.Logging.Logger();
    }

    static class AutopilotBridge
    {
        public static void Install() { }
        public static void Tick() { }
    }

    /// The game's settings screen, as the host looks it up: the Revamp tab's own
    /// GameObject under "NewSettings".  The test makes it open or closed.
    class settingsscript
    {
        public static settingsscript s;
        public Transform transform;
    }

    class Transform
    {
        public GameObject gameObject = new GameObject();
        public Transform Find(string path) => null;
    }

    class GameObject
    {
        public bool activeInHierarchy = false;
    }
}
