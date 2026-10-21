using System;
using System.Collections.Generic;
using TLDRevamp.WorldGen;

// Offline unit test of the part of the terrain choice that touches the game's road generator (issue #32).
// dotnet run --project tools/unit/TerrainApplyTest.csproj
//
// The game's roadGenScript is replaced by a stub with the same two fields, and the game's map holder by a stub with
// the same list of generators. The production TerrainChoice code is linked unchanged, so the rules under test are
// the rules the game runs: which flags a mode sets, on which generators, and that a mode we do not offer changes
// nothing.
static class TerrainApplyTest
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
        // --- the game as it ships: the default map's generator has a main road and side roads
        var gen = new roadGenScript { hasMainRoad = true, hasSideRoad = true };
        var map = new mapSettingScript { roadGens = new List<roadGenScript> { gen } };

        Check("a fresh generator starts as the game has it", gen.hasMainRoad && gen.hasSideRoad);

        // --- Crossroads: the game's own world, so nothing has to change
        bool changed = TerrainChoice.ApplyTo(map, TerrainMode.Crossroads);
        Check("Crossroads leaves the shipped flags alone", !changed && gen.hasMainRoad && gen.hasSideRoad);
        Check("Crossroads is the mode the flags describe", TerrainChoice.ModeOf(map) == TerrainMode.Crossroads);

        // --- MainRoadOnly: the same main road, no side roads
        changed = TerrainChoice.ApplyTo(map, TerrainMode.MainRoadOnly);
        Check("MainRoadOnly changes the flags", changed);
        Check("MainRoadOnly keeps the main road", gen.hasMainRoad);
        Check("MainRoadOnly removes the side roads", !gen.hasSideRoad);
        Check("MainRoadOnly is the mode the flags describe", TerrainChoice.ModeOf(map) == TerrainMode.MainRoadOnly);

        // --- applying the same mode again changes nothing (a world is not restarted by the same choice)
        changed = TerrainChoice.ApplyTo(map, TerrainMode.MainRoadOnly);
        Check("MainRoadOnly again changes nothing", !changed && gen.hasMainRoad && !gen.hasSideRoad);

        // --- back to Crossroads: the game's own world again
        changed = TerrainChoice.ApplyTo(map, TerrainMode.Crossroads);
        Check("Crossroads restores the side roads", changed && gen.hasMainRoad && gen.hasSideRoad);

        // --- the disabled old terrain must not touch the flags
        changed = TerrainChoice.ApplyTo(map, TerrainMode.OldTerrain);
        Check("the old terrain changes nothing", !changed && gen.hasMainRoad && gen.hasSideRoad);

        // --- a map with several generators: all of them get the mode
        var g1 = new roadGenScript { hasMainRoad = true, hasSideRoad = true };
        var g2 = new roadGenScript { hasMainRoad = true, hasSideRoad = true };
        var g3 = new roadGenScript { hasMainRoad = true, hasSideRoad = true };
        var multi = new mapSettingScript { roadGens = new List<roadGenScript> { g1, g2, g3 } };
        changed = TerrainChoice.ApplyTo(multi, TerrainMode.MainRoadOnly);
        Check("every generator of the map gets the mode", changed
            && g1.hasMainRoad && !g1.hasSideRoad
            && g2.hasMainRoad && !g2.hasSideRoad
            && g3.hasMainRoad && !g3.hasSideRoad);

        // --- a map with no generator at all must not throw
        var empty = new mapSettingScript { roadGens = new List<roadGenScript>() };
        Check("a map without generators is handled", !TerrainChoice.ApplyTo(empty, TerrainMode.MainRoadOnly));
        Check("a null map is handled", !TerrainChoice.ApplyTo(null, TerrainMode.MainRoadOnly));

        // --- a map whose generator already has no side roads: that is MainRoadOnly, not Crossroads
        var noSide = new roadGenScript { hasMainRoad = true, hasSideRoad = false };
        var noSideMap = new mapSettingScript { roadGens = new List<roadGenScript> { noSide } };
        Check("a generator without side roads reads as MainRoadOnly", TerrainChoice.ModeOf(noSideMap) == TerrainMode.MainRoadOnly);
        Check("a generator without side roads needs no change", !TerrainChoice.ApplyTo(noSideMap, TerrainMode.MainRoadOnly));

        // --- the flags the game reads back
        TerrainChoice.ReadFlags(map, out bool main, out bool side);
        Check("the flags read back as set", main && side);

        // --- the map name a save gets
        Check("a save is tagged with the mode",
            TerrainChoice.TaggedMapName("Dust Belt", TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");
        Check("a save is tagged with the game's own mode",
            TerrainChoice.TaggedMapName("Dust Belt", TerrainMode.Crossroads) == "Dust Belt [Crossroads]");
        Check("a save is not tagged twice",
            TerrainChoice.TaggedMapName("Dust Belt [MainRoadOnly]", TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");
        Check("a save's mode is read back",
            TerrainChoice.ModeOfSave("Dust Belt [MainRoadOnly]") == TerrainMode.MainRoadOnly);

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }
}

// The stubs of the game's types that TerrainChoice touches. The game's own types live in the global namespace,
// so the stubs do too: same names, same fields.
public class roadGenScript
{
    public bool hasMainRoad;
    public bool hasSideRoad;
}

public class mapSettingScript
{
    public List<roadGenScript> roadGens;
}

namespace TLDRevamp
{
    public static class Json
    {
        public static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");
    }
}
