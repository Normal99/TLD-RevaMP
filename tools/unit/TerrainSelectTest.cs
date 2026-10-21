using System;
using TLDRevamp.WorldGen;

// Offline unit test of the rule that decides which terrain a world gets (issue #32).
// dotnet run --project tools/unit/TerrainSelectTest.csproj
//
// It links the production TerrainSelect and TerrainModes, so the precedence the game uses is the precedence tested
// here: the host's mode first, then the save's tag, then the player's own choice.
static class TerrainSelectTest
{
    static int checks, fails;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static int Main()
    {
        const int None = -1;
        var crossroads = TerrainMode.Crossroads;
        var mainOnly = TerrainMode.MainRoadOnly;

        // --- a new game: the player's own choice
        Check("a new game with Crossroads",
            TerrainSelect.Choose(None, null, crossroads, out string s1) == crossroads && s1 == TerrainSelect.SourceNewGame);
        Check("a new game with MainRoadOnly",
            TerrainSelect.Choose(None, null, mainOnly, out string s2) == mainOnly && s2 == TerrainSelect.SourceNewGame);
        Check("a new game with an empty save name",
            TerrainSelect.Choose(None, "", mainOnly, out _) == mainOnly);

        // --- a save keeps its own terrain
        Check("a save with the MainRoadOnly tag",
            TerrainSelect.Choose(None, "Dust Belt [MainRoadOnly]", crossroads, out string s3) == mainOnly && s3 == TerrainSelect.SourceSave);
        Check("a save with the Crossroads tag",
            TerrainSelect.Choose(None, "Dust Belt [Crossroads]", mainOnly, out string s4) == crossroads && s4 == TerrainSelect.SourceSave);
        Check("a save without a tag uses the player's choice",
            TerrainSelect.Choose(None, "Dust Belt", mainOnly, out string s5) == mainOnly && s5 == TerrainSelect.SourceNewGame);

        // --- a save with a tag we do not offer must not put the game in a state it cannot generate
        Check("a save made in the old terrain keeps it",
            TerrainSelect.Choose(None, "Dust Belt [OldTerrain]", mainOnly, out string s6) == TerrainMode.OldTerrain && s6 == TerrainSelect.SourceSave);
        Check("a save with an unknown tag falls back to the player's choice",
            TerrainSelect.Choose(None, "Dust Belt [Sideways]", mainOnly, out string s7) == mainOnly && s7 == TerrainSelect.SourceNewGame);
        Check("a new game in the old terrain uses it",
            TerrainSelect.Choose(None, null, TerrainMode.OldTerrain, out _) == TerrainMode.OldTerrain);

        // --- the host's mode wins over everything: it is the world we are driving in
        Check("the host's mode wins over the player's choice",
            TerrainSelect.Choose((int)mainOnly, null, crossroads, out string s8) == mainOnly && s8 == TerrainSelect.SourceHost);
        Check("the host's mode wins over the save's tag",
            TerrainSelect.Choose((int)crossroads, "Dust Belt [MainRoadOnly]", mainOnly, out string s9) == crossroads && s9 == TerrainSelect.SourceHost);
        Check("the host's Crossroads wins over a MainRoadOnly save",
            TerrainSelect.Choose((int)crossroads, "Dust Belt [MainRoadOnly]", crossroads, out _) == crossroads);

        // --- the host's mode has to be one we can generate
        Check("the host's old terrain wins",
            TerrainSelect.Choose((int)TerrainMode.OldTerrain, null, mainOnly, out string s10) == TerrainMode.OldTerrain && s10 == TerrainSelect.SourceHost);
        Check("a host mode outside the range is ignored",
            TerrainSelect.Choose(99, null, mainOnly, out string s11) == mainOnly && s11 == TerrainSelect.SourceNewGame);
        Check("a negative host mode means no host",
            TerrainSelect.Choose(-1, "Dust Belt [MainRoadOnly]", crossroads, out string s12) == mainOnly && s12 == TerrainSelect.SourceSave);

        // --- the round trip: a mode the player picks is the mode the save gets, and back
        foreach (var mode in TerrainModes.Selectable())
        {
            string tagged = TerrainModes.WithTag("Dust Belt", mode);
            var back = TerrainSelect.Choose(None, tagged, TerrainModes.Default, out string src);
            Check($"a mode survives the save round trip ({TerrainModes.Name(mode)})",
                back == mode && src == TerrainSelect.SourceSave);
        }

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }
}
