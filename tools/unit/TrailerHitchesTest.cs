using System;
using System.Collections.Generic;
using System.Globalization;
using TrailerHitches;
using TLDRevamp.Fixes;

static class TrailerHitchesTest
{
    static int fails;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
    static bool Load(attachablescript part, int index = 3, int type = 2, int target = 1) => MissingTrailerSlot.Prefix(part, type, target, 42, index);
    static int Main()
    {
        var ids = new HashSet<int>();
        var positions = new Dictionary<string, HitchPosition>();
        foreach (var model in HitchPosition.Models)
        {
            positions.Add(model.Key, model.Position);
            foreach (int id in model.Items) Check("unique item " + id, ids.Add(id) && HitchPosition.ForItem(id) == model);
        }
        Check("nine missing stock receivers; existing receivers excluded", ids.Count == 9 && HitchPosition.ForItem(1) == null && HitchPosition.ForItem(140) == null);
        Check("transport frame extends behind van", HitchPosition.ForItem(396).Position.Z < HitchPosition.ForItem(371).Position.Z - 1.5f);
        Check("nominal level target relative to rear axle", Math.Abs(HitchPosition.ForItem(243).Position.Y - (-0.0222f - 0.0723f)) < 0.00001f &&
            Math.Abs(HitchPosition.ForItem(371).Position.Y - (-0.2168f - 0.0723f)) < 0.00001f);
        foreach (string raw in new[] { "", "0,1", "0,1,2,3", "NaN,0,0", "0,Infinity,0", "0,0,-21", "0,0,1e30", "a,b,c" })
            Check("invalid override refused: " + raw, !HitchPosition.TryParse(raw, out _));
        var culture = CultureInfo.CurrentCulture;
        string hash = HitchPosition.Fingerprint(positions);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check("overrides use decimal point in every locale", HitchPosition.TryParse(" 0, -0.0945, -2.26 ", out var p) && p.ToString() == "0,-0.0945,-2.26");
            Check("locale independent multiplayer fingerprint", hash == HitchPosition.Fingerprint(positions));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var reversed = new Dictionary<string, HitchPosition>();
        for (int i = HitchPosition.Models.Length - 1; i >= 0; i--) reversed.Add(HitchPosition.Models[i].Key, HitchPosition.Models[i].Position);
        Check("dictionary order cannot change identity", hash == HitchPosition.Fingerprint(reversed));
        reversed["car09"] = new HitchPosition(0f, -0.3f, -2.34f);
        Check("different config refuses the same-version peer", hash != HitchPosition.Fingerprint(reversed));

        var parent = new TrailerSaveStub { car = new object(), partslotscripts = new List<object> { new object(), new object(), new object() } };
        itemPlaceRemoveScript.s = new itemPlaceRemoveScript { Parent = parent };
        var part = new attachablescript { utanfuto = new object(), tosave = new TrailerSaveStub { P = new PickupStub { crafted = true } } };
        Check("existing stock slot follows game load", Load(part, 2));
        MissingTrailerSlot.Enabled = false;
        Check("disabled recovery exposes game's unknown-slot fallback", Load(part));
        MissingTrailerSlot.Enabled = true;
        Check("unknown appended slot bypasses generic attachment fallback", !Load(part) && !part.attached && !part.tosave.P.crafted && part.CraftedCalls == 1);
        Check("load leaves movement guard cleared", !part.tempDontMoveAtAttach);
        part.attached = true;
        Check("already attached drawbar detached without removing its item", !Load(part, 10) && part.DetachCalls == 1 && part.tosave != null && part.utanfuto != null);
        part.utanfuto = null;
        Check("non-trailer unknown slots retain game's fallback", Load(part));
        part.utanfuto = new object();
        Check("collider and POI records unchanged", Load(part, type: 1) && Load(part, target: 2));
        Check("negative index unchanged", Load(part, -1));
        parent.car = null;
        Check("non-car parent unchanged", Load(part));
        parent.car = new object();
        itemPlaceRemoveScript.s.Parent = null;
        Check("not-yet-loaded parent unchanged", Load(part));
        Check("null drawbar unchanged", Load(null));
        itemPlaceRemoveScript.s.Parent = parent;
        parent.partslotscripts.Add(new object());
        Check("reinstalled receiver follows normal slot load", Load(part));
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}

// Minimal load-boundary doubles; the game's implementation is never copied into the repository.
public sealed class attachablescript
{
    public object utanfuto;
    public bool attached, tempDontMoveAtAttach = true;
    public TrailerSaveStub tosave;
    public int CraftedCalls, DetachCalls;
    public void Detach() { attached = false; DetachCalls++; }
    public void Crafted(bool mounted) { CraftedCalls++; }
}
public sealed class TrailerSaveStub { public object car; public List<object> partslotscripts; public PickupStub P; }
public sealed class PickupStub { public bool crafted; public void Crafted() { } }
public sealed class itemPlaceRemoveScript
{
    public static itemPlaceRemoveScript s;
    public TrailerSaveStub Parent;
    public bool GetOne(uint id, out TrailerSaveStub parent) { parent = Parent; return parent != null && id == 42; }
}
public sealed class save_attachable { public static void Load() { } }
namespace UnityEngine { public struct Vector3d { } }
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method, Type[] args) { } }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPrefix : Attribute { }
}
