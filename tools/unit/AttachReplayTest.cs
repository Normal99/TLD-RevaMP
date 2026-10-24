using System;
using System.Collections.Generic;
using TLDRevamp.Net;

/// Entities.ReplayAttaches' choice: car H (net 1) and car J (net 2), J's engine (net 3, a part of J) bolted into H, H's
/// engine (net 4, a part of H) bolted into J, H's wheel (net 5, a part of H) bolted back onto H.
static class AttachReplayTest
{
    private static int _failed;
    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failed++;
    }
    static readonly Dictionary<uint, uint> Parent = new Dictionary<uint, uint> { [3] = 1, [4] = 2, [5] = 1 };   // kept attaches
    static readonly Dictionary<uint, uint[]> Kids = new Dictionary<uint, uint[]> { [1] = new uint[] { 3, 5 }, [2] = new uint[] { 4 } };
    static readonly Dictionary<uint, uint[]> Parts = new Dictionary<uint, uint[]> { [1] = new uint[] { 4, 5 }, [2] = new uint[] { 3 } };

    /// a joiner sent the cars in this order: what each send replays
    static List<uint> Join(uint[] order, bool parts)
    {
        var known = new HashSet<uint>(); var all = new List<uint>(); var into = new List<uint>();
        foreach (var car in order)
        {
            foreach (var p in Parts[car]) known.Add(p);   // SendObject: its parts that came off, then the car
            known.Add(car);
            AttachReplay.Pick(car, false, 0, Kids[car], parts ? Parts[car] : null, n => Parent.ContainsKey(n),
                              n => Parent.TryGetValue(n, out var pa) ? pa : 0, n => known.Contains(n), into);
            all.AddRange(into);
        }
        return all;
    }

    private static int Main()
    {
        foreach (var order in new[] { new uint[] { 1, 2 }, new uint[] { 2, 1 } })
        {
            var got = Join(order, true);
            string o = order[0] + " then " + order[1];
            Check("every kept attach replayed (" + o + ")", got.Contains(3) && got.Contains(4) && got.Contains(5));
            Check("each exactly once (" + o + ")", got.Count == 3 && new HashSet<uint>(got).Count == 3);
        }
        var before = Join(new uint[] { 1, 2 }, false);
        Check("switch off: J's engine in H, H sent first, never replayed (the bug)", !before.Contains(3) && before.Contains(4) && before.Contains(5));
        var into2 = new List<uint>();
        AttachReplay.Pick(2, false, 0, null, new uint[] { 3 }, n => Parent.ContainsKey(n), n => Parent[n], n => n == 3, into2);
        Check("a part bolted onto something the player doesn't know yet waits for that one", into2.Count == 0);
        AttachReplay.Pick(2, false, 0, null, new uint[] { 6 }, n => n == 6, n => 0, n => false, into2);
        Check("a part attached to the world (parent 0) is replayed", into2.Count == 1 && into2[0] == 6);
        AttachReplay.Pick(7, true, 1, null, null, n => false, n => 0, n => n == 1, into2);
        Check("the object's own attach first, when its parent is known", into2.Count == 1 && into2[0] == 7);
        AttachReplay.Pick(7, true, 1, null, null, n => false, n => 0, n => false, into2);
        Check("not when its parent is unknown", into2.Count == 0);
        AttachReplay.Pick(2, false, 0, null, new uint[] { 8 }, n => false, n => 1, n => true, into2);
        Check("a part with no kept attach (lying loose) sends nothing", into2.Count == 0);
        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
