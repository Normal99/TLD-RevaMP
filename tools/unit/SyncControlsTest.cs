#pragma warning disable CS8981, CS0649
global using tosaveitemscript = SyncFakes.Item;
global using carscript = SyncFakes.Car;
global using usablescript = SyncFakes.Usable;
global using attachablescript = SyncFakes.Attach;
global using partslotscript = SyncFakes.Slot;
global using mainscript = SyncFakes.Main;
global using save_item = SyncFakes.Record;
using System;
using System.Collections.Generic;
using System.Reflection;
using TLDRevamp.Net;
using UnityEngine;

static class SyncControlsTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main()
    {
        Entities.Tests(Check);
        var u = new usablescript { tuneAble = true };
        UsableMerge.Apply(u, 0, 0, 0, 0, .8f);
        Check(u.tuned && u.tunedUp && u.tunedLerp == .8f && !u.turned, "reliable frequency/volume enters tune path without a switch event");
        u.tuned = u.tunedUp = false;
        UsableMerge.Apply(u, 0, 0, 0, 0, 0);
        Check(u.tuned && u.tunedDown && u.tunedLerp == 0, "volume zero reaches radio off");
        UsableMerge.Apply(u, 0, 0, 0, 1, .4f);
        Check(u.turned && u.Switches == 1 && u.currentTurnState == 1 && u.tunedLerp == .4f, "AM/FM switch and tune applied together");
        u.turned = u.tuned = false;
        UsableMerge.Apply(u, 0, 0, 0, 1, .4f);
        Check(!u.turned && !u.tuned && u.Switches == 1, "final reliable duplicate does not replay radio input");
        var momentary = new usablescript { turnable = true, maxTurnState = 0 };
        UsableMerge.Apply(momentary, 0, 0, 0, 0, 0, inputEvent: true);
        Check(momentary.turned && momentary.Switches == 1, "live momentary controls keep their zero-state input event");
        Check(UsableMerge.Ignition(false, true, true) && !UsableMerge.Ignition(true, true, false), "live owner ignition wins over older car records in both directions");
        Check(UsableMerge.Ignition(true, false, false) && !UsableMerge.Ignition(false, false, true), "initial saved ignition works before first signal");
        Console.WriteLine("ALL PASS (" + checks + " sync control checks)"); return 0;
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal sealed class Ent
        {
            internal uint NetId, Epoch, ParentNet, RootId; internal int OwnerId, PartIndex = -1; internal bool Proxy, SentAtRest;
            internal PoseInterpolator Ip;
            internal tosaveitemscript Root; internal List<tosaveitemscript> Items = new List<tosaveitemscript>(); internal HashSet<int> Split;
        }
        internal sealed class SEnt { internal uint AttachParent, ParentNet; internal int OwnerId, PartIndex = -1; internal bool Car = true; internal SyncFakes.Group Record; }
        static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly Dictionary<SyncFakes.Engine, Ent> ProxyEngineOwner = new Dictionary<SyncFakes.Engine, Ent>();
        static readonly NetWriter W = new NetWriter();
        static readonly object _editBuckets = new object();
        const byte DetachReq = 37, PartOff = 26, DetachSync = 42;
        const int MyId = 1;
        static bool InSession = true, EditValidation = true, EditRangeCheck = true, Near = true, Token = true;
        static float EditRatePerS = 20, EditBurst = 40;
        static long DetachReqsSent, EditsOutOfReach, AutoDetachesIgnored, SuppressedFallOff, PartsReported, DetachSyncsSent, DetachSyncsApplied, DetachesStored;
        static bool _applyingDetach, PlayerActing = true, StoredAttach = true;
        static tosaveitemscript _handFor;
        static float _handAt;
        static tosaveitemscript Handed;
        static void HandOver(tosaveitemscript it) { if (_handFor == it) { Handed = it; _handFor = null; } }
        static bool DetachStored(uint id, Vector3d pos, Quaternion rot) => false;
        static void ApplyProxyBodies(Ent e) { }
        static int Sent, Target;
        static bool IsProxy(object c) => c is carscript car ? car.Proxy : c is attachablescript at ? at.InheritedProxy : true;
        static bool Resolve(Ent e) => true;
        static bool HitchDetach(attachablescript at) => true;
        static SyncFakes.Group GroupRecord(SEnt e) => e.Record;
        static bool AtObject(int from, SEnt e) => Near;
        static bool TakeToken(object bucket, int from, float rate, float burst) => Token;
        static void Refuse(ref long count, string reason, string what) => count++;
        static void ToServer(NetWriter w, bool reliable) => Sent++;
        static void ServerSendTo(int id, NetWriter w, bool reliable) { Target = id; Sent++; }
        static Ent OwnEntity(tosaveitemscript it) { foreach (var e in ByNet.Values) if (e.Root == it) return e; return null; }
        static NetReader Reader() => new NetReader { Buf = W.Buf, Pos = 1, End = W.Len };
        internal static void RunDetach(attachablescript at)
        {
            var hook = typeof(Entities).GetNestedType("DetachHook", BindingFlags.NonPublic);
            bool runOriginal = (bool)hook.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { at });
            if (runOriginal)
            { at.attached = false; at.slot = null; }
            hook.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { at, runOriginal });
        }
        internal static bool RunUnCraft(partslotscript slot, bool fallOff)
        {
            bool result = false;
            var args = new object[] { slot, result };
            var hook = typeof(Entities).GetNestedType("ProxyUnCraft", BindingFlags.NonPublic);
            if (!(bool)hook.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args)) return (bool)args[1];
            var at = slot.part();
            if (slot.Covered && !fallOff) return false;
            at.tosave.Crafted = false;
            at.Detach();
            slot.Part = null;
            return true;
        }
        internal static void Tests(Action<bool, string> check)
        {
            var body = new tosaveitemscript(); var radio = new tosaveitemscript(); var lamp = new tosaveitemscript();
            var car = new Ent { NetId = 1, Root = body, Proxy = true, Split = new HashSet<int> { 1 } };
            car.Items.AddRange(new[] { body, radio, lamp }); ByNet[1] = car;
            var separate = new Ent { NetId = 7, Root = radio, Proxy = false, OwnerId = MyId, PartIndex = 1 };
            separate.Items.Add(radio); ByNet[7] = separate;
            check(EditEntityOf(radio) == separate && EditEntityOf(lamp) == car, "detached radio addresses its own entity before stale car membership");
            var at = new attachablescript { tosave = radio };
            check(!PartIsProxy(at) && !RequestDetach(at), "owned headlight on a proxy frame may detach locally without a claim");
            separate.Proxy = true; separate.OwnerId = 2;
            check(PartIsProxy(at) && RequestDetach(at), "non-owner removal asks the part owner");
            var request = Reader(); check(request.U32() == 7 && request.VarU32() == 0, "removal addresses current entity and member");
            separate.Root = new tosaveitemscript(); separate.Items.Add(separate.Root); car.Split.Add(2); separate.Items.Add(lamp);
            check(EditEntityOf(lamp) == separate && RequestDetach(new attachablescript { tosave = lamp }), "nested member of a detached frame can request removal");
            Server[1] = new SEnt { OwnerId = 2 }; Server[7] = new SEnt { OwnerId = 2, AttachParent = 1 }; Sent = 0;
            var radioRecord = new save_item { prefabID = 9 };
            Server[1].Record = new SyncFakes.Group(); Server[1].Record.items.AddRange(new[] { new save_item(), radioRecord });
            Server[7].ParentNet = 1; Server[7].PartIndex = 1;
            check(EditItemRecord(Server[1], 1) == radioRecord && EditItemRecord(Server[7], 0) == radioRecord, "live radio resolves its prefab after becoming a separate part entity");
            check(EditItemRecord(Server[7], 1) == null && EditItemRecord(Server[7], -1) == null, "part prefab resolution refuses invalid member addresses");
            check(EditReachEntity(Server[7]) == Server[1], "bolted part reach follows the moving car, not its old loose position");
            ServerDetachReq(1, 7, 2); check(Sent == 1 && Target == 2, "server arbitrates to owner without transferring frame ownership");
            Near = false; ServerDetachReq(1, 7, 2); check(Sent == 1, "out-of-reach removal refused"); Near = true;
            Token = false; ServerDetachReq(1, 7, 2); check(Sent == 1, "existing edit validation applies to removal"); Token = true;
            ServerDetachReq(1, 7, -1); ServerDetachReq(1, 7, 4097);
            check(Sent == 1, "malformed requests do not forward");
            ServerDetachReq(2, 7, 2);
            check(Sent == 2 && Target == 2, "ownership overtaking a suppressed dismount still forwards to the current owner");
            RemovalTests(check);
            var engine = new SyncFakes.Engine { running = true };
            body.car = new carscript { Engine = engine }; car.Epoch = 4; BindProxyEngine(car);
            BindProxyEngine(separate);
            check(ProxyEngineOwner[engine] == car, "registering a proxy part cannot overwrite its containing car engine authority");
            separate.Proxy = false; BindProxyEngine(separate);
            check(ProxyEngineOwner[engine] == car, "taking a part cannot remove its containing car engine authority");
            car.Proxy = false; BindProxyEngine(car);
            check(!ProxyEngineOwner.ContainsKey(engine), "taking car ownership releases engine simulation");
            var c = new carscript { Proxy = true, ignition = true, ignitionUsable = new usablescript { Held = true } };
            var prefix = typeof(Entities).GetNestedType("KeyOnCopy", BindingFlags.NonPublic).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            check(!(bool)prefix.Invoke(null, new object[] { c }), "proxy key is captured but cannot simulate its own ignition toggle");
            c.Proxy = false; check((bool)prefix.Invoke(null, new object[] { c }), "owner retains vanilla ignition simulation");
        }
    }
}

namespace SyncFakes
{
    class Record { internal int prefabID; }
    class Group { internal List<save_item> items = new List<save_item>(); }
    class Item
    {
        internal carscript car;
        internal attachablescript attachable;
        internal bool Crafted = true;
        internal Transform transform = new Transform();
    }
    class Attach
    {
        internal tosaveitemscript tosave;
        internal partslotscript slot;
        internal bool attached = true, InheritedProxy;
        public T GetComponent<T>() where T : class => tosave as T;
        internal void Detach() => Entities.RunDetach(this);
    }
    class Slot
    {
        internal attachablescript Part;
        internal bool Covered;
        internal attachablescript part() => Part;
        internal bool UnCraft(bool fallOff) => Entities.RunUnCraft(this, fallOff);
    }
    class Main
    {
        internal static Vector3d GlobalFromUnityPos(Vector3 v) => new Vector3d(v.x, v.y, v.z);
        internal static Vector3 UnityPosFromGlobal(Vector3d v) => new Vector3((float)v.x, (float)v.y, (float)v.z);
    }
    class Engine { internal bool running; }
    class Car { internal bool Proxy, ignition; internal Engine Engine; internal usablescript ignitionUsable; internal float startingTime = .3f; }
    class Usable
    {
        internal float tunedFloat, tempTunedFloat, tunedLerp, lastHoldTime;
        internal int currentTurnState, Switches, maxTurnState = 1;
        internal bool turnable, tuneAble, tuned, tunedUp, tunedDown, turned, Held;
        internal bool Holding() => Held;
        internal void FStart() { }
        internal void Tuned(float delta, bool syncinmulti) => tunedLerp = tunedFloat;
        internal void Refresh(float slide, float x, float y, int turn) { currentTurnState = turn; Tuned(0, false); }
        internal void UpdFromMulti(float slide, float x, float y, int turn, float tune)
        { tuned = tune != tunedFloat; tunedFloat = tempTunedFloat = tune; Refresh(slide, x, y, turn); turned = true; Switches++; }
    }
}
namespace UnityEngine
{
    struct Vector3 { internal float x, y, z; internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    struct Quaternion { internal float x, y, z, w; internal Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
    class Transform
    {
        internal Vector3 position;
        internal Quaternion rotation;
        internal void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
    }
    static class Time { internal static float time => 10; internal static float realtimeSinceStartup => 10; }
    static class Mathf
    {
        internal static float Min(float a, float b) => Math.Min(a, b);
        internal static float Max(float a, float b) => Math.Max(a, b);
        internal static int RoundToInt(float v) => (int)Math.Round(v);
        internal static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] class HarmonyPatch : Attribute { internal HarmonyPatch(Type type, string name) { } internal HarmonyPatch(Type type, string name, Type[] args) { } }
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPostfix : Attribute { }
}
struct Vector3d { internal double x, y, z; internal Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; } }
namespace TLDRevamp.Net { class PoseInterpolator { } }
static class SessionLog { internal static void Event(string kind, uint net, uint other = 0, int item = -1, int from = -1, int to = -1, uint epoch = 0, string result = "ok") { } }
