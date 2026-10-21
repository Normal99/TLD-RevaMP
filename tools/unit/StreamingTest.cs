#pragma warning disable CS8981 // Aliases match the game API names.
global using carscript = TestCar;
global using tosaveitemscript = TestItem;
global using savedatascript = TestSave;
global using mainscript = TestMain;
global using menuhandler = TestMenu;
global using savedata = TestSaveData;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using TLDRevamp;
using TLDRevamp.Fixes;
using UnityEngine;

// Pure scheduling/store tests. Unity instantiation, physics and Harmony dispatch still need the in-game test.
static class StreamingTest
{
    static readonly Func<itemPlaceRemoveScript, bool> Select = (Func<itemPlaceRemoveScript, bool>)Delegate.CreateDelegate(typeof(Func<itemPlaceRemoveScript, bool>), typeof(FarItemLookup).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static));
    internal static readonly Action PartsBefore = Method<Action>("Prefix");
    internal static readonly Action<tosaveitemscript> PartsAfter = Method<Action<tosaveitemscript>>("Postfix");
    internal static readonly Action PartsFinally = Method<Action>("Finalizer");
    static T Method<T>(string name) where T : Delegate => (T)Delegate.CreateDelegate(typeof(T), typeof(PlaceWithParts).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void NewWorld(int count = 3)
    {
        Time.frameCount++;
        FarItemLookup.Spread = FarItemLookup.SpreadMultiplayer = FarItemLookup.Enabled = true;
        FarItemLookup.BudgetMs = 0;
        PlaceWithParts.Enabled = PlaceWithParts.ReuseIndex = true;
        TLDRevamp.Net.Mp.Client = new object(); TLDRevamp.Net.Mp.Server = null;
        savedatascript.s = new savedatascript(); itemPlaceRemoveScript.s = new itemPlaceRemoveScript();
        menuhandler.s = new menuhandler(); mainscript.Offset = default;
        var chunk = new HashSet<uint>(); savedatascript.s.itemChunks[default] = chunk;
        for (uint id = 1; id <= count; id++)
        {
            savedatascript.s.data.itemData.items.Add(new save_item { id = id }); chunk.Add(id);
        }
    }
    static long FrameBudgetWindow(FrameWorkBudget b, long ms, int from, int to)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = from; i < to; i++) { b.Begin(i, 3); b.Charge(ms); }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    static void Main()
    {
        var b = new FrameWorkBudget(); long ms = Stopwatch.Frequency / 1000;
        b.Begin(1, 3); Check(b.CanRun, "first operation"); b.Charge(ms * 2);
        b.Begin(1, 3); Check(b.CanRun, "same frame retains remaining allowance"); b.Charge(ms);
        b.Begin(1, 3); Check(!b.CanRun, "same-frame second caller cannot reset budget");
        b.Begin(2, 3); Check(b.CanRun, "next frame progresses"); b.Charge(ms * 10); Check(!b.CanRun, "one oversize operation stops the frame");
        b.Begin(3, 0); Check(b.CanRun, "zero budget progresses once"); b.Charge(0); Check(!b.CanRun, "zero budget stops after first");
        b.Begin(4, double.NaN); b.Charge(0); Check(!b.CanRun, "invalid budget is bounded");
        // The budget's own arithmetic must not allocate. A tier-0 method's first long-running loop carries a
        // one-time JIT/OSR cost that the .NET 10 runtime charges to the measuring thread (24 bytes; it does not
        // scale with the trip count and is gone with TieredCompilation=0 or OnStackReplacement=0). That cost is a
        // runtime artifact, not the budget's, so the loop runs over two windows and only the warmed one is
        // asserted: the code under test must add no bytes of its own.
        long first = FrameBudgetWindow(b, ms, 5, 10005);
        long warmed = FrameBudgetWindow(b, ms, 10005, 20005);
        Check(warmed == 0, warmed == 0 ? "warmed frame budget does not allocate"
            : "warmed frame budget allocates " + warmed + " bytes in the warmed window (first window: " + first + ")");

        NewWorld(); var s = itemPlaceRemoveScript.s;
        Check(!Select(s), "replacement selection"); Check(s.Placed.Count == 1, "MP places one indivisible item at zero budget");
        Check(FarItemLookup.Claimed(2) && FarItemLookup.Claimed(3), "all remaining ids claimed before parent loads");
        FarItemLookup.Tick(); Check(s.Placed.Count == 1, "runner shares coroutine budget");
        Select(s); Check(s.Placed.Count == 1, "repeated selection neither resets budget nor duplicates ids");
        Time.frameCount++; FarItemLookup.Tick(); Time.frameCount++; FarItemLookup.Tick();
        Check(string.Join(",", s.Placed) == "1,2,3", "FIFO, duplicate selections coalesced");
        Check(!FarItemLookup.Claimed(2), "claim released");

        NewWorld(); s = itemPlaceRemoveScript.s; Select(s);
        savedatascript.s.data.itemData.items[0].transform.pos = new Vector3d(1000, 0, 0);
        Time.frameCount++; FarItemLookup.Tick(); Check(s.Placed.Count == 1, "moved-out queued record not spawned");
        Time.frameCount++; FarItemLookup.Tick(); Check(string.Join(",", s.Placed) == "1,3", "later valid record still progresses");

        NewWorld(); s = itemPlaceRemoveScript.s; Select(s);
        savedatascript.s.items[2] = new tosaveitemscript { preDestroyed = true };
        Time.frameCount++; FarItemLookup.Tick();
        Check(savedatascript.IndexOfID(savedatascript.s.data.itemData.items, 2, out _), "dying object's record preserved");
        savedatascript.s.items.Remove(2); Time.frameCount++; Select(s); FarItemLookup.Flush();
        Check(s.Placed.Contains(2), "dying item can be selected again after destruction");

        NewWorld(); s = itemPlaceRemoveScript.s; Select(s);
        savedatascript.s.items[2] = new tosaveitemscript();
        Time.frameCount++; FarItemLookup.Tick();
        Check(!s.Placed.Contains(2) && !savedatascript.IndexOfID(savedatascript.s.data.itemData.items, 2, out _), "already loaded by another path not spawned twice");
        FarItemLookup.Flush();

        NewWorld(); s = itemPlaceRemoveScript.s; Select(s);
        mainscript.Offset = new Vector3d(4500, 0, 0);
        menuhandler.s.currentMainMap.genArounds[0].upos = new Vector3d(-4500, 0, 0);
        Time.frameCount++; FarItemLookup.Tick(); Check(s.Placed.Count == 2, "range check survives origin shift");
        typeof(FarItemLookup).GetMethod("BeforeSave", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        Check(s.Placed.Count == 3 && !FarItemLookup.Claimed(3), "save drains remaining records");

        NewWorld(); s = itemPlaceRemoveScript.s; Select(s);
        savedatascript.s = new savedatascript(); Time.frameCount++; FarItemLookup.Tick();
        Check(!FarItemLookup.Claimed(2), "world replacement discards old ids");
        NewWorld(); s = itemPlaceRemoveScript.s; FarItemLookup.SpreadMultiplayer = false; Select(s);
        Check(s.Placed.Count == 3, "A/B restores synchronous MP placement");

        NewWorld(); s = itemPlaceRemoveScript.s;
        savedatascript.s.data.itemData.attachable.Add(new save_attachable { id = 2, parentid = 1, attachType = 1 });
        savedatascript.s.data.itemData.attachable.Add(new save_attachable { id = 3, parentid = 2, attachType = 1 });
        long builds = PlaceWithParts.IndexBuilds;
        s.GetOne(1, out _);
        Check(PlaceWithParts.IndexBuilds == builds + 1, "one index build for recursive load");
        Check(string.Join(",", s.Placed) == "1,2,3", "recursive children survive removal of attachment records during load");
        Check(PlaceWithParts.IndexBuilds > 0 && PlaceWithParts.ReusedLoads > 0, "recursive loads use the stable snapshot");
        NewWorld(); s = itemPlaceRemoveScript.s; PlaceWithParts.ReuseIndex = false;
        savedatascript.s.data.itemData.attachable.Add(new save_attachable { id = 2, parentid = 1, attachType = 1 });
        s.GetOne(1, out _); Check(s.Placed.Count == 2, "A/B original parts path still loads children");
        NewWorld(); s = itemPlaceRemoveScript.s;
        savedatascript.s.data.itemData.attachable.Add(new save_attachable { id = 2, parentid = 1, attachType = 1 });
        Select(s); Check(s.Placed.Count == 1 && FarItemLookup.Claimed(2), "selected child waits for its own frame");
        FarItemLookup.Flush();
        Console.WriteLine($"Streaming: {checks} checks passed");
    }
}

namespace UnityEngine
{
    public static class Time { public static int frameCount; }
    public class Transform { public Transform parent; public Vector3d position; }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)] public class HarmonyPatch : Attribute { public HarmonyPatch(params object[] args) { } }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public class HarmonyFinalizer : Attribute { }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int Last = 0; }
}
namespace TLDRevamp
{
    public static class Plugin { public static Logger Log = new Logger(); }
    public class Logger { public void LogWarning(string s) { throw new Exception(s); } public void LogInfo(string s) { } }
}
namespace TLDRevamp.Net
{
    public static class Mp { public static object Client, Server; }
    public static class Entities { public static string PrefabName(int id) => "item"; }
}
public struct Vector2d { public double x, y; }
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double a, double b, double c) { x = a; y = b; z = c; }
    public double magnitude => Math.Sqrt(x*x + y*y + z*z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x-b.x, a.y-b.y, a.z-b.z);
}
public class TestCar { }
public class TestItem
{
    public uint idInSave; public bool mapSpawned, preDestroyed; public string name = "item";
    public Transform transform = new Transform();
    public T GetComponent<T>() where T : class => null;
    public T[] GetComponentsInChildren<T>(bool includeInactive = false) => Array.Empty<T>();
}
public class save_transform { public Vector3d pos; }
public class save_item { public uint id; public int prefabID; public save_transform transform = new save_transform(); }
public class save_attachable { public uint id, parentid; public int attachType; }
public class itemDataClass { public List<save_item> items = new List<save_item>(); public List<save_attachable> attachable = new List<save_attachable>(); }
public class TestSaveData { public itemDataClass itemData = new itemDataClass(); }
public class TestSave
{
    public static savedatascript s;
    public const double itemChunkSize = 25000;
    public savedata data = new savedata();
    public Dictionary<uint, tosaveitemscript> items = new Dictionary<uint, tosaveitemscript>();
    public Dictionary<Vector2d, HashSet<uint>> itemChunks = new Dictionary<Vector2d, HashSet<uint>>();
    public static Vector2d SnapChunkPos(Vector3d p) => default;
    public static bool IndexOfID(List<save_item> list, uint id, out int index) { for (index = list.Count - 1; index >= 0; index--) if (list[index].id == id) return true; return false; }
    public void SaveToData() { }
}
public class itemPlaceRemoveScript
{
    public static itemPlaceRemoveScript s;
    public float itemSpawnDist = 250;
    public List<uint> addFromFar = new List<uint>(), Placed = new List<uint>();
    public List<Vector2d> placeChunks = new List<Vector2d>();
    public void PlaceStuff() { }
    public tosaveitemscript PlaceOne(uint id, int index)
    {
        StreamingTest.PartsBefore();
        try
        {
            var it = new tosaveitemscript { idInSave = id }; savedatascript.s.items[id] = it;
            savedatascript.s.data.itemData.items.RemoveAt(index);
            savedatascript.s.data.itemData.attachable.RemoveAll(a => a.id == id);
            Placed.Add(id); StreamingTest.PartsAfter(it); return it;
        }
        finally { StreamingTest.PartsFinally(); }
    }
    public bool GetOne(uint id, out tosaveitemscript it)
    {
        if (savedatascript.s.items.TryGetValue(id, out it)) return true;
        if (!savedatascript.IndexOfID(savedatascript.s.data.itemData.items, id, out int k)) return false;
        it = PlaceOne(id, k); return true;
    }
}
public class mapSettingScript
{
    public class genAround { public Vector3d upos; }
    public List<genAround> genArounds = new List<genAround> { new genAround() };
}
public class TestMenu { public static menuhandler s; public mapSettingScript currentMainMap = new mapSettingScript(); }
public static class TestMain
{
    public static Vector3d Offset;
    public static Vector3d GlobalFromUnityPos(Vector3d p) => new Vector3d(p.x + Offset.x, p.y + Offset.y, p.z + Offset.z);
}
public static class syncScript { public static bool IsConnected() => false; public static bool IsHost() => false; }
