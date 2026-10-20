#pragma warning disable CS8981, CS0649, CS0169, CS0414
global using tosaveitemscript = LiveItem;
global using mainscript = LiveMain;
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

static class LiveUsablesTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main() { Entities.Tests(); Console.WriteLine("ALL PASS (" + checks + " live usable lifecycle checks)"); return 0; }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal sealed class Ent
        {
            internal uint NetId, Epoch = 1; internal int OwnerId; internal bool Proxy;
            internal tosaveitemscript Root; internal List<tosaveitemscript> Items = new List<tosaveitemscript>();
            internal HashSet<int> Split; internal Dictionary<int, float> EditHoldUntil; internal ulong[] ResyncHashes;
        }
        internal sealed class SEnt
        {
            internal uint NetId, Epoch = 1, ParentNet, AttachParent; internal int OwnerId, PartIndex = -1;
            internal Group Record; internal HashSet<int> Gone; internal List<uint> Parts = new List<uint>(); internal Vector3d Where;
        }
        internal class Group { internal List<save_item> items = new List<save_item>(); }
        struct PoiState { internal float XRot, YRot, Slide, Tuned; internal int Turn; }
        static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly Dictionary<uint, object> _storedDoorAttaches = new Dictionary<uint, object>();
        static readonly Dictionary<tosaveitemscript, float> EditDirty = new Dictionary<tosaveitemscript, float>();
        static readonly Dictionary<tosaveitemscript, int> EditKinds = new Dictionary<tosaveitemscript, int>();
        static float _editFlushAt; static int _resyncFirst;
        static bool InSession = true, _applyingState, EditValidation, EditRangeCheck = true;
        static int MyId = 1; const float EditHold = 1.5f, StateInterestM = 300;
        static long EditRefusedValues, EditsOutOfReach, DetachReqsSent;
        static readonly object _editBuckets = new object(); static float EditRatePerS, EditBurst;
        const byte DetachReq = 37; static readonly NetWriter W = new NetWriter();
        static readonly List<Spot> _spots = new List<Spot>();
        static readonly List<byte[]> Sent = new List<byte[]>();
        static bool Resolve(Ent e) => e.Root != null;
        static Ent OwnEntity(tosaveitemscript it) { foreach (var e in ByNet.Values) if (e.Root == it) return e; return null; }
        static Ent SharedGroupOf(tosaveitemscript it) => EditEntityOf(it);
        static Group GroupRecord(SEnt se) => se.Record;
        static bool IsProxy(object it) => false;
        static bool AtObject(int from, SEnt se) => true;
        static bool TakeToken(object b, int p, float r, float burst) => true;
        static void ServerSendTo(int id, NetWriter w, bool reliable) { }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        static bool Sane(float n) => Finite(n);
        static void Refuse(ref long n, string why, string what) => n++;
        static bool Near(Spot s, double x, double z) => s.Near;
        static void ToServer(NetWriter w, bool reliable) => Sent.Add(w.Buf.AsSpan(0, w.Len).ToArray());
        static void Deliver(NetWriter w) => ApplyLiveUsable(Reader(w.Buf.AsSpan(0, w.Len).ToArray()));
        static void MarkEdit(tosaveitemscript it, string kind) { }
        static bool BusControl(tosaveitemscript it, usablescript u) => false;
        static tosaveitemscript ItemOf(Component c, uint id, bool hasId) => hasId && savedatascript.s.items.TryGetValue(id, out var it) ? it : c.GetComponentInParent<tosaveitemscript>();
        static void TelemetryTouch(uint net) { }
        static tosaveitemscript _handFor; static float _handAt;
        static NetReader Reader(byte[] b) => new NetReader { Buf = b, Pos = 1, End = b.Length };
        static void Input(usablescript u, float angle)
        {
            LiveUsableInputPrefix(u); u.xRot = angle; MarkLiveUsable(u);
        }
        static void LiveUsableInputPrefix(usablescript u)
        {
            typeof(LiveUsableInput).GetMethod("Prefix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, new object[] { u, true });
        }
        static void Check(bool ok, string why) => LiveUsablesTest.Check(ok, why);
        internal static void Tests()
        {
            foreach (int owner in new[] { 0, 1, 2 }) foreach (int attacher in new[] { 0, 1, 2 }) foreach (int actor in new[] { 0, 1, 2 })
            {
                ByNet.Clear(); Server.Clear(); LiveUsablesReset(); Sent.Clear(); Mp.Server = new FakeServer();
                MyId = actor; Time.realtimeSinceStartup = 10; SessionLog.St = 10;
                var body = new tosaveitemscript { idInSave = 100 };
                var door = new tosaveitemscript { idInSave = 200 };
                var u = new usablescript { tosaveid = 200, setid = true, ParentItem = door };
                door.usables.Add(u); savedatascript.s.items[200] = door;
                var car = new Ent { NetId = 10, Root = body, OwnerId = owner, Proxy = owner != actor };
                car.Items.AddRange(new[] { body, door }); ByNet[10] = car;
                itemdatabase.s.Prefab = door;
                var sc = new SEnt { NetId = 10, OwnerId = owner, Record = new Group() };
                sc.Record.items.AddRange(new[] { new save_item(), new save_item { prefabID = 7 } }); Server[10] = sc;
                Input(u, 10); LiveUsableTick();
                Check(_localUsables[u].Key.Net == 10, "original control uses car address");
                // Batch 9's first door is still in the original car group, before any split/reattach.
                // Exercise the real sender with steady frames for every host/joiner/owner combination.
                Sent.Clear();
                for (int frame = 0; frame < 600; frame++)
                {
                    Time.realtimeSinceStartup = 10 + frame / 120f; SessionLog.St = Time.realtimeSinceStartup;
                    Input(u, 10 + frame / 10f); LiveUsableTick();
                }
                Check(Sent.Count / 5.0 >= 25, "original car door sends at least 25 Hz with a steady source");
                Check(Sent.Count <= 302, "steady original door retains its sixty Hz cadence");
                _localUsables.Clear();
                long originalApplied = LiveUsablesApplied;
                foreach (var packet in Sent)
                {
                    ReadLiveUsable(Reader(packet), out _, out _, out double stamp, out _);
                    SessionLog.St = stamp; ApplyLiveUsable(Reader(packet));
                }
                Check((LiveUsablesApplied - originalApplied) / 5.0 >= 25 && _remoteUsables.ContainsKey(new UsableKey { Net = 10, Epoch = 1, Item = 1, Usable = 0 }),
                    "original car door applies the live stream independently of entity pose quieting");
                // The captures alternate ~100 ms gaps and several short frames. Resetting the deadline to
                // now + step after each gap discards the next input opportunities even with >30 source FPS.
                LiveUsablesReset(); Sent.Clear(); u.xRot = 0;
                double jitterNow = 20;
                for (int frame = 0; frame < 176; frame++)
                {
                    jitterNow += frame % 4 == 0 ? .09 : .008;
                    Time.realtimeSinceStartup = (float)jitterNow; SessionLog.St = jitterNow;
                    Input(u, frame * .2f); LiveUsableTick();
                }
                Check(Sent.Count / (jitterNow - 20) >= 25, "original door retains 25 Hz through uneven >30 FPS source frames");
                int afterMotion = Sent.Count;
                Time.realtimeSinceStartup += 1; SessionLog.St += 1; LiveUsableTick();
                Check(Sent.Count <= afterMotion + 1, "stall recovery sends current input without replaying missed samples");
                afterMotion = Sent.Count;
                Time.realtimeSinceStartup += 1; SessionLog.St += 1; LiveUsableTick();
                Check(Sent.Count == afterMotion, "original door stays silent after uneven motion settles");
                LiveUsablesReset(); Time.realtimeSinceStartup = 10; SessionLog.St = 10;
                Input(u, 10); LiveUsableTick();
                var oldKey = _localUsables[u].Key;
                _localUsables.Clear();
                WriteLiveUsable(WL, oldKey, 1, 10, UsableBits(u)); Deliver(WL);
                Check(_remoteUsables.ContainsKey(oldKey), "original remote interpolation exists");
                var oldRemote = _remoteUsables[oldKey];
                car.Split = new HashSet<int> { 1 }; LiveUsableTick();
                Check(!_remoteUsables.ContainsKey(oldKey), "tick cannot render an old split address");
                car.Split = null; _remoteUsables[oldKey] = oldRemote;
                // PartDetached: the same Unity usable now belongs to a new entity and item zero.
                LiveUsablesChanged(door); car.Split = new HashSet<int> { 1 };
                var part = new Ent { NetId = 20, Root = door, OwnerId = attacher, Proxy = attacher != actor };
                part.Items.Add(door); ByNet[20] = part;
                sc.Parts.Add(20); Server[20] = new SEnt { NetId = 20, ParentNet = 10, PartIndex = 1, OwnerId = attacher };
                Check(_remoteUsables.Count == 0 && LiveUsableOf(oldKey, out _) == null, "detach retires original interpolation and address");
                Input(u, 20); LiveUsableTick();
                var key = _localUsables[u].Key;
                Check(key.Net == 20 && key.Item == 0 && EditEntityOf(door) == part, "loose part uses independent entity");
                ushort seq = _localUsables[u].Seq;
                // AttachHook / owner broadcast: re-bolted parts stay independent and follow the car's owner.
                LiveUsablesChanged(door); part.OwnerId = owner; part.Proxy = owner != actor;
                Server[20].OwnerId = owner; Server[20].AttachParent = 10;
                if (owner != attacher) { part.Epoch++; Server[20].Epoch++; }
                Check(_localUsables.Count == 0, "reattach drops old sender baseline");
                Input(u, 20); LiveUsableTick();
                Check(!_localUsables[u].Dirty && !_localUsables[u].Final, "reattached zero input stays silent");
                Input(u, 24); LiveUsableTick();
                Check(_localUsables[u].Key.Net == 20 && _localUsables[u].Key.Epoch == part.Epoch, "same usable rebinds after owner/epoch change");
                // Registration can be rebuilt in place while mounting; cache must follow registered order.
                var replacement = new usablescript { tosaveid = 200, setid = true, ParentItem = door };
                Check(ItemUsables(door)[0] == u, "old registration cached");
                door.usables[0] = replacement;
                Check(ItemUsables(door)[0] == replacement, "registration replacement refreshes cache");
                u = replacement; Input(u, 25); LiveUsableTick(); key = _localUsables[u].Key;
                Check(key.Net == 20 && key.Item == 0 && key.Epoch == part.Epoch, "reattached sender uses current entity/epoch");
                if (owner == attacher) Check(_localUsables[u].Seq > seq, "same-epoch reattach preserves outgoing sequence");
                Sent.Clear();
                // Actual hook/tick sender runs at 120 FPS; actual server validates and relays to owner + third viewer.
                for (int frame = 0; frame < 240; frame++)
                {
                    Time.realtimeSinceStartup = 11 + frame / 120f; SessionLog.St = Time.realtimeSinceStartup;
                    Input(u, 30 + frame / 4f); LiveUsableTick();
                }
                Time.realtimeSinceStartup += .025f; SessionLog.St = Time.realtimeSinceStartup; LiveUsableTick();
                Check(Sent.Count >= 70, "reattached live sender remains smooth-rate");
                _localUsables.Clear(); Mp.Server.Recipients.Clear();
                for (int viewer = 0; viewer < 3; viewer++) Mp.Server.Spots.Add(new Spot { Id = viewer, Near = viewer != owner });
                long refused = LiveUsablesRefused;
                foreach (var b in Sent) { ReadLiveUsable(Reader(b), out _, out _, out double stamp, out _); SessionLog.St = stamp; ServerLiveUsable(actor, Reader(b)); }
                Check(LiveUsablesRefused == refused && Mp.Server.Packets.Count >= 70, "server accepts part prefab fallback after reattach");
                Check(!Mp.Server.Recipients.Contains(actor) && (owner == actor || Mp.Server.Recipients.Contains(owner)), "relay excludes actor and includes current owner");
                foreach (int viewer in new[] { 0, 1, 2 })
                {
                    if (viewer == actor) continue;
                    _remoteUsables.Clear(); _liveIn.Clear(); MyId = viewer; part.Proxy = viewer != owner;
                    u.xRot = 25; int next = 0, updates = 0; float was = u.xRot;
                    for (int frame = 0; frame < 280; frame++)
                    {
                        double now = 11 + frame / 120.0; SessionLog.St = now; Time.realtimeSinceStartup = (float)now;
                        while (next < Sent.Count)
                        {
                            ReadLiveUsable(Reader(Sent[next]), out _, out _, out double stamp, out _);
                            if (stamp > now) break;
                            ApplyLiveUsable(Reader(Sent[next++]));
                        }
                        LiveUsableTick();
                        if (frame > 30 && frame < 220 && u.xRot > was) updates++;
                        was = u.xRot;
                    }
                    Check(updates > 175, "owner/non-owner/third viewer interpolate between packets after reattach");
                    Check(Math.Abs(u.xRot - 89.75f) < .01, "reattached motion settles exactly");
                }
                // Pending send and receive state must stop even if topology changed before its lifecycle hook ran.
                MyId = actor; Input(u, 40); var local = _localUsables[u];
                part.Split = new HashSet<int> { 0 }; int sent = Sent.Count; LiveUsableTick();
                Check(!_localUsables.ContainsKey(u) && Sent.Count == sent, "tick cannot send stale split address");
                part.Split = null; _localUsables.Clear(); SessionLog.St = Time.realtimeSinceStartup + 1;
                WriteLiveUsable(WL, key, 500, SessionLog.St, UsableBits(u)); Deliver(WL);
                var remote = _remoteUsables[key]; LiveUsablesChanged(door);
                WriteLiveUsable(WL, key, 501, SessionLog.St - .01, UsableBits(u)); Deliver(WL);
                Check(!_remoteUsables.ContainsKey(key), "delayed pre-reattach packet cannot resurrect retired interpolation");
                SessionLog.St += .1; WriteLiveUsable(WL, key, 501, SessionLog.St, UsableBits(u)); Deliver(WL);
                Check(!ReferenceEquals(remote, _remoteUsables[key]), "reattach rebuilds interpolator for same net/epoch");
            }
        }
    }
    class Spot { internal int Id; internal bool Near; }
    class FakeServer
    {
        internal List<Spot> Spots = new List<Spot>(); internal List<int> Recipients = new List<int>(); internal List<byte[]> Packets = new List<byte[]>();
        internal void ReadySpots(List<Spot> spots) { spots.Clear(); spots.AddRange(Spots); }
        internal void SendToPlayer(int id, NetWriter w, bool reliable) { Recipients.Add(id); Packets.Add(w.Buf.AsSpan(0, w.Len).ToArray()); }
    }
    static class Mp { internal static FakeServer Server; internal static Vector3d LocalGlobal() => default; }
    static class Protocol { internal const byte LiveUsable = 93; }
    static class Json { internal static string Str(string s) => "\"" + s + "\""; }
}

class LiveItem : Component { internal uint idInSave; internal List<usablescript> usables = new List<usablescript>(); }
class usablescript : Component
{
    internal uint tosaveid; internal bool setid, setidPoi, turnable, rotateAble = true, slideAble, tuneAble, invX, invY, slideX, slideXi, slideY, slideYi, slideZ, slideZi, tunedUp, tunedDown, tuned;
    internal float xRot, yRot, slideValue, tunedFloat, tempTunedFloat, minX, maxX = 90, minY, maxY, tunedFloatMin, tunedFloatMax = 1, tuneSpeed = 1, slideSpeed = 1;
    internal int currentTurnState, maxTurnState = 1; internal Transform RT1 = new Transform(); internal Vector3 slideMultiplier;
    internal void FStart() { }
    internal void Rot(float x, float y, bool sync, bool vr = false) { }
    internal void Slide(float x, float y, float z, bool sync) { }
    internal void Tuned(float delta, bool syncinmulti) { }
    internal void UpdTurnState(int turn, bool sync) { }
    internal void Turn() { }
    internal void Refresh(float slide, float x, float y, int turn) { slideValue = slide; xRot = x; yRot = y; currentTurnState = turn; }
    internal void UpdFromMulti(float slide, float x, float y, int turn, float tune) { Refresh(slide, x, y, turn); tunedFloat = tune; }
}
class attachablescript : Component { internal tosaveitemscript tosave; }
class save_item { internal int prefabID; }
class itemdatabase { internal static itemdatabase s = new itemdatabase(); internal tosaveitemscript Prefab; internal Component Item(int id) => Prefab; }
class savedatascript { internal static savedatascript s = new savedatascript(); internal Dictionary<uint, tosaveitemscript> items = new Dictionary<uint, tosaveitemscript>(); }
class LiveMain
{
    internal static mainscript s = new mainscript(); internal Player player = new Player(); internal float rotSens = 1, slideSens = 1;
    internal static Vector3d GlobalFromUnityPos(Vector3 v) => default;
}
class Player { internal usablescript rotatings; }
struct Vector3d { internal double x, y, z; }
static class SessionLog
{
    internal static double St;
    internal static void Event(string kind, uint net, int item = -1, int slot = -1, int from = -1, int to = -1, uint epoch = 0, bool sample = false, string result = "ok") { }
}
namespace UnityEngine
{
    class Component
    {
        internal string name = "door"; internal Transform transform = new Transform(); internal tosaveitemscript ParentItem;
        internal T GetComponent<T>() where T : Component => this as T;
        internal T GetComponentInParent<T>() where T : Component => ParentItem as T;
    }
    class Transform { internal string name = "hinge"; internal Transform parent; internal Vector3 position; }
    struct Vector3 { internal float x, y, z; }
    static class Time { internal static float realtimeSinceStartup; }
    static class Mathf
    {
        internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        internal static float InverseLerp(float a, float b, float v) => a == b ? 0 : Clamp01((v - a) / (b - a));
        internal static float Clamp01(float v) => Math.Clamp(v, 0, 1);
        internal static int RoundToInt(float v) => (int)Math.Round(v);
        internal static float Abs(float v) => Math.Abs(v);
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] class HarmonyPatch : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPrefix : Attribute { }
    static class AccessTools { internal static System.Reflection.MethodBase Method(Type type, string name, Type[] args) => type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); }
}
