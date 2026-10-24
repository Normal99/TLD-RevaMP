using System;
using System.Collections.Generic;
using System.IO;

namespace TLDRevamp.WorldGen.Old2023
{
    /// Where the 2023 build's code comes from: the player's own Steam download of the public build v2023.05.02d
    /// (docs/OLD2023-FROM-STEAM.md). RevaMP never ships or downloads it; it only looks for it, checks it and loads it.
    /// No Unity or BepInEx types here, so it is unit tested outside the game (tools/unit/Old2023FilesTest.cs).
    public static class OldBuildFiles
    {
        public const string App = "1017180", Depot = "1017181", Manifest = "7731485613881054580";

        /// What the player types into Steam's console. Steam writes the whole depot (321 MB) into
        /// <Steam>/steamapps/content/app_1017180/depot_1017181 (the Linux client: <Steam>/ubuntu12_32/steamapps/content).
        public const string DownloadCommand = "download_depot " + App + " " + Depot + " " + Manifest;

        /// The one line on the new-game screen when the files are not there.
        public const string NewGameHint =
            "Needs the 2023 game files: open Steam's console (steam -console or steam://open/console) and run " + DownloadCommand;

        /// The files RevaMP needs from that depot (in TheLongDrive_Data/Managed) and their SHA-256. A hash of the
        /// player's file, not the file: every machine in a session runs the same old code.
        ///   Assembly-CSharp.dll   - the 2023 game code
        ///   Unity.Collections.dll - TerrainGenerationJob's NativeList fields (the beta ships no Unity.Collections)
        ///   Unity.Mathematics.dll - referenced by the job's other methods
        public static readonly string[] Names = { "Assembly-CSharp.dll", "Unity.Collections.dll", "Unity.Mathematics.dll" };
        public static readonly string[] Sha256 =
        {
            "9919a9eeddcce23bb4ff86e890437f32b4a3ef3c10af2efe3b8d889d894dd62e",
            "fedbd58da08818027c7a3ca4f735a412f54637689158d354dfb9728870ece5f7",
            "4af4869ab6e59a7afd8c4e185c3ffe3c602fa7df7134e63672f75e6eef0721a0",
        };

        public const string ManagedSubdir = "TheLongDrive_Data/Managed";

        /// A joiner without the files can't build a 2023 host's world: terrain is generated on every machine.
        /// Null when the join may go on.
        public static string JoinRefusal(TerrainMode hostMode, bool available) =>
            hostMode == TerrainMode.OldTerrain && !available
                ? "The host plays the old 2023 terrain, which needs the 2023 game files on your machine too. Open Steam's console (steam -console or steam://open/console), run " +
                  DownloadCommand + ", restart the game and join again."
                : null;

        /// The Steam folders that may hold a console download, best first. env reads an environment variable;
        /// gameRoot is the game's folder (…/steamapps/common/The Long Drive); windows: the game runs as a Windows
        /// program (also under Proton, where the Linux side's paths are reached through drive Z:). otherHomes: more Linux
        /// home folders to try (under Proton: the folders in /home, when HOME doesn't reach the game).
        public static List<string> SteamRoots(string gameRoot, Func<string, string> env, bool windows, IEnumerable<string> otherHomes = null)
        {
            var roots = new List<string>();
            void Add(string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                if (windows && p.StartsWith("/", StringComparison.Ordinal)) p = "Z:" + p;   // a Linux path under Proton
                p = p.Replace('\\', '/').TrimEnd('/');
                foreach (var r in roots) if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) return;
                roots.Add(p);
            }
            // Proton names the Steam client it runs under
            Add(env("STEAM_COMPAT_CLIENT_INSTALL_PATH"));
            // the library the game is in: Steam's own folder when the game is in the default library
            if (!string.IsNullOrEmpty(gameRoot))
            {
                string g = gameRoot.Replace('\\', '/').TrimEnd('/');
                int i = g.LastIndexOf("/steamapps/common/", StringComparison.OrdinalIgnoreCase);
                if (i > 0) Add(g.Substring(0, i));
            }
            string data = env("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(data) && data.StartsWith("/", StringComparison.Ordinal)) Add(data + "/Steam");
            var homes = new List<string> { env("HOME") };
            if (otherHomes != null) homes.AddRange(otherHomes);
            foreach (var home in homes)
            {
                if (string.IsNullOrEmpty(home) || !home.StartsWith("/", StringComparison.Ordinal)) continue;
                Add(home + "/.local/share/Steam");                                  // Linux, native client
                Add(home + "/.steam/steam");
                Add(home + "/.var/app/com.valvesoftware.Steam/.local/share/Steam"); // Flatpak
                Add(home + "/.var/app/com.valvesoftware.Steam/data/Steam");
                Add(home + "/snap/steam/common/.local/share/Steam");                 // Snap
            }
            foreach (var pf in new[] { env("ProgramFiles(x86)"), env("ProgramFiles") })
                if (!string.IsNullOrEmpty(pf)) Add(pf + "/Steam");                  // Windows
            return roots;
        }

        /// The depot folders under those roots, best first.
        public static List<string> DepotDirs(IEnumerable<string> roots)
        {
            var dirs = new List<string>();
            foreach (var r in roots)
            {
                dirs.Add(r + "/steamapps/content/app_" + App + "/depot_" + Depot);
                dirs.Add(r + "/ubuntu12_32/steamapps/content/app_" + App + "/depot_" + Depot);
            }
            return dirs;
        }

        /// The folder of a depot download that holds the files.
        public static string ManagedDir(string depotDir) => Path.Combine(depotDir, ManagedSubdir);

        public static string Hex(byte[] hash)
        {
            var c = new char[hash.Length * 2];
            const string digits = "0123456789abcdef";
            for (int i = 0; i < hash.Length; i++) { c[2 * i] = digits[hash[i] >> 4]; c[2 * i + 1] = digits[hash[i] & 15]; }
            return new string(c);
        }
    }
}
