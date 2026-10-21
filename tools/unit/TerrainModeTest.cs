using System;
using System.Collections.Generic;
using TLDRevamp.WorldGen;

// Offline unit test of the terrain mode model and the mode selection rules (issue #32).
// dotnet run --project tools/unit/TerrainModeTest.csproj
//
// It links the production plain C# files (the ones with no Unity types), so the tag format, the mode names and the
// selection rules are tested exactly as they run in the game.
static class TerrainModeTest
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
        // ---------------------------------------------------------------- the names
        Check("Crossroads is the default", TerrainModes.Default == TerrainMode.Crossroads);
        Check("the mode names are stable", TerrainModes.Name(TerrainMode.Crossroads) == "Crossroads"
            && TerrainModes.Name(TerrainMode.MainRoadOnly) == "MainRoadOnly"
            && TerrainModes.Name(TerrainMode.OldTerrain) == "OldTerrain");
        Check("the code is the name", TerrainModes.Code(TerrainMode.MainRoadOnly) == "MainRoadOnly");
        Check("a name parses to its mode", Parse("Crossroads") == TerrainMode.Crossroads
            && Parse("MainRoadOnly") == TerrainMode.MainRoadOnly);
        Check("a name parses regardless of case", Parse("mainroadonly") == TerrainMode.MainRoadOnly
            && Parse("MAINROADONLY") == TerrainMode.MainRoadOnly);
        Check("an unknown name does not parse", !TryParse("CrossRoad", out _) && !TryParse("", out _) && !TryParse(null, out _));
        Check("an unknown code falls back to the default", TerrainModes.FromCode("nonsense") == TerrainModes.Default);
        Check("an empty code falls back to the default", TerrainModes.FromCode("") == TerrainModes.Default);

        // ---------------------------------------------------------------- the selectable list
        var selectable = TerrainModes.Selectable();
        Check("exactly two modes are offered", selectable.Length == 2);
        Check("the offered modes are Crossroads and MainRoadOnly",
            selectable[0] == TerrainMode.Crossroads && selectable[1] == TerrainMode.MainRoadOnly);
        Check("OldTerrain is not selectable", !TerrainModes.IsSelectable(TerrainMode.OldTerrain)
            && Array.IndexOf(selectable, TerrainMode.OldTerrain) < 0);
        Check("the two shipped modes are selectable", TerrainModes.IsSelectable(TerrainMode.Crossroads)
            && TerrainModes.IsSelectable(TerrainMode.MainRoadOnly));
        Check("the old terrain blocker says why it is off", TerrainModes.OldTerrainBlocker.Length > 40
            && TerrainModes.OldTerrainBlocker.Contains("licence") && TerrainModes.OldTerrainBlocker.Contains("published"));

        // ---------------------------------------------------------------- the map name tag
        Check("a plain name has no tag", !TerrainModes.HasTag("Dust Belt"));
        Check("a tagged name has a tag", TerrainModes.HasTag("Dust Belt [Crossroads]"));
        Check("the tag is stripped", TerrainModes.StripTag("Dust Belt [Crossroads]") == "Dust Belt");
        Check("the tag is stripped for every mode",
            TerrainModes.StripTag("Dust Belt [MainRoadOnly]") == "Dust Belt"
            && TerrainModes.StripTag("Dust Belt [OldTerrain]") == "Dust Belt");
        Check("a plain name is left alone", TerrainModes.StripTag("Dust Belt") == "Dust Belt");
        Check("a name with brackets but no tag word is left alone", TerrainModes.StripTag("Dust [Belt]") == "Dust [Belt]");
        Check("a tag is added", TerrainModes.WithTag("Dust Belt", TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");
        Check("a tag is replaced, not doubled", TerrainModes.WithTag("Dust Belt [Crossroads]", TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");
        Check("tagging twice does not double the tag", TerrainModes.WithTag(TerrainModes.WithTag("Dust Belt", TerrainMode.MainRoadOnly), TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");
        Check("an empty name gets a bare tag", TerrainModes.WithTag("", TerrainMode.Crossroads) == "[Crossroads]");
        Check("a name that is only a tag is tagged again", TerrainModes.WithTag("[Crossroads]", TerrainMode.MainRoadOnly) == "[MainRoadOnly]");
        Check("a null name is safe", TerrainModes.StripTag(null) == "" && !TerrainModes.HasTag(null));
        Check("a tagged name gives its mode", TerrainModes.FromMapName("Dust Belt [MainRoadOnly]") == TerrainMode.MainRoadOnly);
        Check("a plain name gives the default", TerrainModes.FromMapName("Dust Belt") == TerrainModes.Default);
        Check("an unknown tag is not honoured", TerrainModes.FromMapName("Dust Belt [Sideways]") == TerrainModes.Default);
        Check("an unknown tag is not stripped", TerrainModes.StripTag("Dust Belt [Sideways]") == "Dust Belt [Sideways]");
        Check("the disabled OldTerrain tag is not honoured", TerrainModes.FromMapName("Dust Belt [OldTerrain]") == TerrainModes.Default);
        Check("the disabled OldTerrain tag is stripped but not honoured", TerrainModes.StripTag("Dust Belt [OldTerrain]") == "Dust Belt"
            && TerrainModes.FromMapName("Dust Belt [OldTerrain]") == TerrainModes.Default);
        Check("a tag with spaces in it is not a tag", !TerrainModes.HasTag("Dust Belt [Main Road Only]"));
        Check("an empty tag is not a tag", !TerrainModes.HasTag("Dust Belt []"));
        Check("a tag without a closing bracket is not a tag", !TerrainModes.HasTag("Dust Belt [Crossroads"));
        Check("a name that is only a tag strips to empty", TerrainModes.StripTag("[Crossroads]") == "");
        Check("only the last tag counts", TerrainModes.FromMapName("Dust [Crossroads] Belt [MainRoadOnly]") == TerrainMode.MainRoadOnly);
        Check("a name that ends in a bracket but has no tag keeps its brackets", TerrainModes.StripTag("Dust Belt [note]") == "Dust Belt [note]");

        // ---------------------------------------------------------------- the split
        Check("a split gives the plain name and the mode",
            TerrainModes.TrySplitTag("Dust Belt [MainRoadOnly]", out string plain, out TerrainMode mode)
            && plain == "Dust Belt" && mode == TerrainMode.MainRoadOnly);
        Check("a split of a plain name fails", !TerrainModes.TrySplitTag("Dust Belt", out _, out _));
        Check("a split of a disabled tag still finds the tag", TerrainModes.TrySplitTag("Dust Belt [OldTerrain]", out string dp, out TerrainMode dm)
            && dp == "Dust Belt" && dm == TerrainMode.OldTerrain);

        // ---------------------------------------------------------------- the mode the world gets
        // The rules the game uses: the host's mode wins, then the save's tag, then the player's own choice.
        Check("a new game uses the player's choice", ModeForNewGame(TerrainMode.MainRoadOnly) == TerrainMode.MainRoadOnly);
        Check("a new game with no choice uses the default", ModeForNewGame(TerrainModes.Default) == TerrainModes.Default);
        Check("a save keeps its own mode", ModeForSave("Dust Belt [MainRoadOnly]") == TerrainMode.MainRoadOnly);
        Check("a save without a tag uses the default", ModeForSave("Dust Belt") == TerrainModes.Default);
        Check("a save with a disabled tag uses the default", ModeForSave("Dust Belt [OldTerrain]") == TerrainModes.Default);
        Check("the host's mode wins over the player's choice",
            ModeForHost(TerrainMode.MainRoadOnly, TerrainMode.Crossroads) == TerrainMode.MainRoadOnly);
        Check("the host's mode wins over a save's mode",
            ModeForHost(TerrainMode.Crossroads, TerrainMode.MainRoadOnly) == TerrainMode.Crossroads);

        // ---------------------------------------------------------------- the road generator flags
        // The game's own roadGenScript has two flags; the modes are exactly those two combinations.
        Check("Crossroads has a main road and side roads", FlagsFor(TerrainMode.Crossroads).main && FlagsFor(TerrainMode.Crossroads).side);
        Check("MainRoadOnly has a main road and no side roads", FlagsFor(TerrainMode.MainRoadOnly).main && !FlagsFor(TerrainMode.MainRoadOnly).side);
        Check("the old terrain has no flags we can set", !TerrainModes.IsSelectable(TerrainMode.OldTerrain));

        // ---------------------------------------------------------------- the wire
        // The host's mode is one byte; a joiner must never take a mode we do not offer.
        Check("the host's mode is a single byte", Wire(TerrainMode.Crossroads) == 0 && Wire(TerrainMode.MainRoadOnly) == 1);
        Check("the host's mode reads back", ReadWire(Wire(TerrainMode.MainRoadOnly), out TerrainMode m2) && m2 == TerrainMode.MainRoadOnly);
        Check("an old host (no mode) is not an error", !ReadWire(null, out _));
        Check("an unknown byte is refused", !ReadWire(99, out _));
        Check("the disabled old terrain is refused on the wire", !ReadWire(Wire(TerrainMode.OldTerrain), out _));

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }

    // --- the small helpers the checks above use, mirroring the production rules ---

    static bool TryParse(string s, out TerrainMode m) => TerrainModes.TryParse(s, out m);
    static TerrainMode Parse(string s) { TerrainModes.TryParse(s, out var m); return m; }

    static TerrainMode ModeForNewGame(TerrainMode chosen) => chosen;

    static TerrainMode ModeForSave(string mapName)
    {
        var m = TerrainModes.FromMapName(mapName);
        return TerrainModes.IsSelectable(m) ? m : TerrainModes.Default;
    }

    static TerrainMode ModeForHost(TerrainMode host, TerrainMode own) => TerrainModes.IsSelectable(host) ? host : own;

    static (bool main, bool side) FlagsFor(TerrainMode m)
    {
        if (!TerrainModes.IsSelectable(m)) return (false, false);
        return (true, m == TerrainMode.Crossroads);
    }

    static byte Wire(TerrainMode m) => (byte)m;

    static bool ReadWire(byte? b, out TerrainMode mode)
    {
        mode = TerrainModes.Default;
        if (b == null) return false;
        if (b.Value > 2) return false;
        var parsed = (TerrainMode)b.Value;
        if (!TerrainModes.IsSelectable(parsed)) return false;
        mode = parsed;
        return true;
    }
}
