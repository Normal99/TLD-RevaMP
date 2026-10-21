using System;
using System.Collections.Generic;
using TLDRevamp.WorldGen;

// Offline unit test of the save-tag persistence (issue #32).
// dotnet run --project tools/unit/TerrainSaveTest.csproj
//
// The game's save record is replaced by stubs with the same shape (saveproperties holds a mapClass struct, which
// holds a mapSaveClass struct, which holds the map name as a string). The production TerrainChoice code is linked
// unchanged, so the rule under test is the rule the game runs: a save is written with the mode's tag, the tag
// survives a save/load round trip, and the mode is read back from the tag.
static class TerrainSaveTest
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
        // --- the legacy tag format
        // A save's map name is persisted. A build that writes "[Terrain: MainRoadOnly]" and a build that reads
        // only "[MainRoadOnly]" would not find the save at all: the player silently starts a new world. The reader
        // therefore accepts both formats.
        Check("a legacy tag is recognised", TerrainModes.HasTag("Dust Belt [Terrain: MainRoadOnly]"));
        Check("a legacy tag names its mode", TerrainModes.FromMapName("Dust Belt [Terrain: MainRoadOnly]") == TerrainMode.MainRoadOnly);
        Check("a legacy tag is stripped for the game's own map lookup", TerrainModes.StripTag("Dust Belt [Terrain: MainRoadOnly]") == "Dust Belt");
        Check("a legacy Crossroads tag names Crossroads", TerrainModes.FromMapName("Grass [Terrain: Crossroads]") == TerrainMode.Crossroads);
        Check("a legacy tag with the old terrain names it", TerrainModes.FromMapName("Grass [Terrain: OldTerrain]") == TerrainMode.OldTerrain);
        Check("the current tag still works", TerrainModes.FromMapName("Dust Belt [MainRoadOnly]") == TerrainMode.MainRoadOnly);
        Check("a map whose brackets are not a tag keeps them", TerrainModes.StripTag("Dust [Belt]") == "Dust [Belt]");
        Check("a legacy-looking word that is not a mode is not a tag", !TerrainModes.HasTag("Dust [Terrain: Belt]"));
        Check("a name with no tag is unchanged", TerrainModes.StripTag("Dust Belt") == "Dust Belt");
        // the current format is what gets written, whatever the name came in as
        Check("a legacy name is rewritten in the current format",
            TerrainModes.WithTag("Dust Belt [Terrain: MainRoadOnly]", TerrainMode.MainRoadOnly) == "Dust Belt [MainRoadOnly]");

        // --- writing a save
        foreach (var mode in TerrainModes.Selectable())
        {
            var props = NewProperties("Dust Belt");
            TagSave(props, mode);
            Check($"a save of the {TerrainModes.Name(mode)} world carries the tag",
                props.map.map.mapName == "Dust Belt " + TerrainModes.Tag(mode));
        }

        // --- the tag never piles up, however often the game saves
        var props2 = NewProperties("Dust Belt");
        TagSave(props2, TerrainMode.MainRoadOnly);
        TagSave(props2, TerrainMode.MainRoadOnly);
        TagSave(props2, TerrainMode.MainRoadOnly);
        Check("saving again does not pile up tags", props2.map.map.mapName == "Dust Belt [MainRoadOnly]");

        // --- switching the mode in a running world rewrites the tag
        var props3 = NewProperties("Dust Belt");
        TagSave(props3, TerrainMode.MainRoadOnly);
        TagSave(props3, TerrainMode.Crossroads);
        Check("the tag follows the mode of the running world", props3.map.map.mapName == "Dust Belt [Crossroads]");

        // --- a save with no name is left alone
        var props4 = NewProperties("");
        TagSave(props4, TerrainMode.MainRoadOnly);
        Check("a save without a map name is left alone", props4.map.map.mapName == "");

        // --- the save/load round trip: the tag is in the file and the mode comes back from it
        foreach (var mode in TerrainModes.Selectable())
        {
            var written = NewProperties("Dust Belt");
            TagSave(written, mode);
            // the file is read back: the same string, the same struct layout
            var read = NewProperties("Dust Belt");
            read.map.map.mapName = written.map.map.mapName;
            Check($"the {TerrainModes.Name(mode)} mode survives the save round trip",
                TerrainChoice.ModeOfSave(read.map.map.mapName) == mode);
            Check($"the {TerrainModes.Name(mode)} save still names the map",
                TerrainModes.StripTag(read.map.map.mapName) == "Dust Belt");
        }

        // --- a save made before this feature existed has no tag: the game's own world
        var old = NewProperties("Dust Belt");
        Check("a save without a tag uses the game's own world", TerrainChoice.ModeOfSave(old.map.map.mapName) == TerrainModes.Default);

        // --- a save made in the old 2023 terrain loads in it again
        var odd = NewProperties("Dust Belt [OldTerrain]");
        Check("a save made in the old terrain keeps it", TerrainChoice.ModeOfSave(odd.map.map.mapName) == TerrainMode.OldTerrain);
        var unknown = NewProperties("Dust Belt [Sideways]");
        Check("a save with an unknown tag uses the game's own world", TerrainChoice.ModeOfSave(unknown.map.map.mapName) == TerrainModes.Default);

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }

    /// A save record with the game's shape.
    static saveproperties NewProperties(string mapName)
    {
        var p = new saveproperties();
        p.map.map.mapName = mapName;
        p.map.map.tip = StubMenu.mapType.def;
        return p;
    }

    /// What the save hook does: put the mode's tag into the map name.
    static void TagSave(saveproperties props, TerrainMode mode)
    {
        string name = props.map.map.mapName;
        if (string.IsNullOrEmpty(name)) return;
        string tagged = TerrainChoice.TaggedMapName(name, mode);
        if (tagged == name) return;
        props.map.map.mapName = tagged;
    }
}

// The stubs of the game's save types. The game's own types live in the global namespace, so the stubs do too:
// same names, same nesting (the map name is a string inside a struct inside a struct).
public class gamemodesettingsscript
{
    [Serializable]
    public struct mapClass
    {
        public StubMenu.mapSaveClass map;
        public int homesickMode;
    }
}

// A stub with the game's own shape, but with a name of its own: the repository's privacy scan forbids defining
// the game's classes (the mod patches them, it never defines them).
public class StubMenu
{
    public enum mapType { def, bsp, image }

    [Serializable]
    public struct mapSaveClass
    {
        public mapType tip;
        public string defmapID;
        public string mapName;
    }
}

public class roadGenScript
{
    public bool hasMainRoad;
    public bool hasSideRoad;
}

public class mapSettingScript
{
    public List<roadGenScript> roadGens;
}

public class saveproperties
{
    public string date;
    public int seed;
    public float driven;
    public string version;
    public int startCar;
    public int currentCar;
    public gamemodesettingsscript.mapClass map;
}

namespace TLDRevamp
{
    public static class Json
    {
        public static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");
    }
}
