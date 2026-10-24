using System;
using System.Collections.Generic;
using System.Linq;
using TLDRevamp.WorldGen;
using TLDRevamp.WorldGen.Old2023;

// Offline unit test of where RevaMP looks for the 2023 game files, what it pins and what it tells the player.
// dotnet run --project tools/unit/Old2023FilesTest.csproj
//
// It links the production plain C# file (WorldGen/Old2023/OldBuildFiles.cs), so the folders and the texts are tested
// exactly as they run in the game.
static class Old2023FilesTest
{
    static int checks, fails;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static Func<string, string> Env(Dictionary<string, string> d) => k => d.TryGetValue(k, out var v) ? v : null;

    static int Main()
    {
        // ---------------------------------------------------------------- what is pinned
        Check("the console command names app, depot and manifest of v2023.05.02d",
            OldBuildFiles.DownloadCommand == "download_depot 1017180 1017181 7731485613881054580");
        Check("every file has a SHA-256", OldBuildFiles.Names.Length == OldBuildFiles.Sha256.Length
            && OldBuildFiles.Sha256.All(h => h.Length == 64 && h.All(c => "0123456789abcdef".IndexOf(c) >= 0)));
        Check("the game code is the first file", OldBuildFiles.Names[0] == "Assembly-CSharp.dll");
        Check("hex is lower case, two digits a byte", OldBuildFiles.Hex(new byte[] { 0x00, 0x0f, 0xa0, 0xff }) == "000fa0ff");

        // ---------------------------------------------------------------- what the player reads
        Check("the new-game hint is one line with the command", !OldBuildFiles.NewGameHint.Contains("\n")
            && OldBuildFiles.NewGameHint.Contains(OldBuildFiles.DownloadCommand) && OldBuildFiles.NewGameHint.Contains("steam://open/console"));
        Check("a 2023 host refuses a joiner without the files", OldBuildFiles.JoinRefusal(TerrainMode.OldTerrain, false) != null
            && OldBuildFiles.JoinRefusal(TerrainMode.OldTerrain, false).Contains(OldBuildFiles.DownloadCommand));
        Check("a 2023 host takes a joiner with the files", OldBuildFiles.JoinRefusal(TerrainMode.OldTerrain, true) == null);
        Check("other hosts never ask for the files", OldBuildFiles.JoinRefusal(TerrainMode.Crossroads, false) == null
            && OldBuildFiles.JoinRefusal(TerrainMode.MainRoadOnly, false) == null);

        // ---------------------------------------------------------------- where it looks: Linux under Proton
        var proton = OldBuildFiles.SteamRoots("Z:\\srv\\pat\\.local\\share\\Steam\\steamapps\\common\\The Long Drive",
            Env(new Dictionary<string, string> { ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = "/srv/pat/.local/share/Steam", ["HOME"] = "/srv/pat" }), true);
        Check("Proton: Steam's own folder first, through drive Z:", proton[0] == "Z:/srv/pat/.local/share/Steam");
        Check("Proton: the game's library adds no duplicate", proton.Count(r => r.Equals("Z:/srv/pat/.local/share/Steam", StringComparison.OrdinalIgnoreCase)) == 1);
        Check("Proton: Flatpak Steam is looked at", proton.Contains("Z:/srv/pat/.var/app/com.valvesoftware.Steam/.local/share/Steam"));
        var depots = OldBuildFiles.DepotDirs(proton);
        Check("the console download folder of the Linux client is looked at",
            depots.Contains("Z:/srv/pat/.local/share/Steam/ubuntu12_32/steamapps/content/app_1017180/depot_1017181"));
        Check("and the one of the Windows client", depots.Contains("Z:/srv/pat/.local/share/Steam/steamapps/content/app_1017180/depot_1017181"));
        var noHome = OldBuildFiles.SteamRoots("D:\\games\\TLD", Env(new Dictionary<string, string>()), true, new[] { "/srv/sam" });
        Check("Proton without HOME: the other home folders are tried", noHome.Contains("Z:/srv/sam/.local/share/Steam"));
        Check("a game outside steamapps/common gives no library root", !noHome.Any(r => r.StartsWith("D:", StringComparison.Ordinal)));

        // ---------------------------------------------------------------- Windows
        var win = OldBuildFiles.SteamRoots("C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Long Drive",
            Env(new Dictionary<string, string> { ["ProgramFiles(x86)"] = "C:\\Program Files (x86)" }), true);
        Check("Windows: the game's Steam folder, once", win.Count(r => r.Equals("C:/Program Files (x86)/Steam", StringComparison.OrdinalIgnoreCase)) == 1);
        Check("Windows: no Linux folders", !win.Any(r => r.StartsWith("Z:", StringComparison.Ordinal)));

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} old-2023 file checks)" : $"{fails} of {checks} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
