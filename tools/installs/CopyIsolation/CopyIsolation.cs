using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TLDInstalls.CopyIsolation;

// Keeps a copy's data and registry settings apart from Steam's and every other copy's.
[BepInPlugin(Guid, "TLD Copies isolation", "1.0.0")]
public sealed class CopyIsolationPlugin : BaseUnityPlugin
{
    public const string Guid = "tldrevamp.copyisolation";
    internal static string DataDir;
    internal static string PrefPrefix;

    void Awake()
    {
        var root = new DirectoryInfo(Paths.GameRootPath);
        // Managed copies have copy.json beside game/; standalone game folders use their own root.
        if (root.Parent != null && File.Exists(Path.Combine(root.Parent.FullName, "copy.json"))) root = root.Parent;
        DataDir = Path.Combine(root.FullName, "UserData").Replace('\\', '/');
        Directory.CreateDirectory(DataDir);
        PrefPrefix = "copy[" + root.Name + "]:";
        new Harmony(Guid).PatchAll();
        Logger.LogInfo("This copy keeps its saves and settings in " + DataDir);
    }
}

[HarmonyPatch(typeof(pathscript), nameof(pathscript.path))]
internal static class OwnDataFolder
{
    static bool Prefix(ref string __result)
    {
        __result = CopyIsolationPlugin.DataDir;
        return false;
    }
}

// Only the managed overloads used by the game.
[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetString), typeof(string), typeof(string))]
internal static class PrefSetString { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }

[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetFloat), typeof(string), typeof(float))]
internal static class PrefSetFloat { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }

[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetInt), typeof(string), typeof(int))]
internal static class PrefSetInt { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }

[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), typeof(string))]
internal static class PrefGetString { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }

[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetFloat), typeof(string))]
internal static class PrefGetFloat { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }

[HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), typeof(string))]
internal static class PrefGetInt { static void Prefix(ref string key) { key = CopyIsolationPlugin.PrefPrefix + key; } }
