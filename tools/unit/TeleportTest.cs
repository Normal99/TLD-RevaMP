using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

static class TeleportTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Main()
    {
        Check(TpaArrival.Close(1, .2, 1), "one to two metre arrival accepted");
        Check(TpaArrival.Close(2, 0, 0) && !TpaArrival.Close(2.001, 0, 0), "two metre boundary");
        Check(!TpaArrival.Close(0, -6.56, 0), "reported wrong floor rejected");
        Check(!TpaArrival.Close(double.NaN, 0, 0), "invalid arrival rejected");
        Check(!TpaArrival.Arrived(false, 0, .5, 0), "held near the target without a floor is not an arrival");
        Check(TpaArrival.Arrived(true, 1, .2, 0), "close grounded arrival accepted");
        Check(TpaArrival.Arrived(true, 2, .114, 0) && TpaArrival.Arrived(true, 2.001, .038, .002), "batch13 indoor ring landing accepts settling height and centimetre player quantisation");
        Check(TpaArrival.Arrived(true, 3, .5, 0) && TpaArrival.Arrived(true, 2.122, .5, 2.122), "outer three metre search ring is a valid arrival");
        Check(!TpaArrival.Arrived(true, 3.051, 0, 0) && !TpaArrival.Arrived(true, 0, -6.56, 0) && !TpaArrival.Arrived(false, 2, .114, 0), "arrival still rejects outside search, wrong floor and unready ground");
        Check(TpaArrival.Floor(49, 50) && !TpaArrival.Floor(42.44, 50), "wait for target floor, not terrain below");
        Check(!TpaArrival.Floor(52, 50), "roof not selected");
        // Subtract in double precision before conversion to local float coordinates, including after a rebase.
        double global = -8_000_000.123, origin = -8_000_000;
        float local = (float)(global - origin);
        Check(TpaArrival.Close(origin + local - global, 0, 0), "arrival survives far origin shift");

        var roof = new[] { new TpaArrival.Candidate(49, true, false) };
        var ring = new[] { new TpaArrival.Candidate(49, false, true), new TpaArrival.Candidate(49, true, true) };
        Check(TpaArrival.ChooseFloor(roof, 50, true, 0) == 0, "vehicle roof is a floor once static world is ready");
        Check(TpaArrival.ChooseFloor(ring, 50, true, 0) == 1, "wall or car body at centre uses clear ring");
        Check(TpaArrival.ChooseFloor(roof, 50, false, 30) == -1, "not ready for three seconds never falls back or lands on a floating roof");
        var low = new[] { new TpaArrival.Candidate(40, true, true) };
        Check(TpaArrival.ChooseFloor(low, 50, true, 0) == -1 && TpaArrival.ChooseFloor(low, 50, true, 2.99) == -1,
            "readiness starts its own three second window");
        Check(TpaArrival.ChooseFloor(low, 50, true, 3) == -2, "ready high seated target selects real static fallback tiers");
        Check(!TpaArrival.WorldFloor(roof[0]) && TpaArrival.WorldFloor(low[0]), "held bodies and proxies cannot establish world readiness");
        Check(TpaArrival.CarLanding(10, 18, out double down) && Math.Abs(down + 7.6) < .001, "car lowered onto lower ground");
        Check(TpaArrival.CarLanding(25, 18, out double up) && up > 0, "car raised onto higher ground");
        Check(!TpaArrival.Retry(true, 0, -6, 0, 20, 10) && !TpaArrival.Retry(true, 0, -1, 0, 20, 10), "same fallback never reapplies, including valid landing below seated target");
        Check(TpaArrival.Retry(true, 0, -6, 0, 20, 10, TpaArrival.Improves(49, 44, 50)), "new higher support still repairs a late floor");
        Check(!TpaArrival.Improves(43, 44, 50) && !TpaArrival.Improves(49, 50, 50), "same or worse support never retries");
        Check(!TpaArrival.Retry(true, 0, -6, 0, 30, 0) && !TpaArrival.Retry(true, 0, 0, 0, 1, 29), "retry ends at world deadline or successful arrival");
        Entities.TestSync(Check);
        Console.WriteLine($"Teleport: {checks} checks passed");
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        sealed class SEnt { internal uint NetId, ParentNet; internal int PartIndex = -1; internal Vector3d Where;
            internal List<uint> Parts = new List<uint>(); internal List<int> ItemState = new List<int>(); }
        const byte Add = 22, State = 23, CompactState = 98, Resync = 31, PartDetached = 27, AttachSync = 41, DetachSync = 42,
            PhysLockSync = 55, MemberGone = 86, RemoveItem = 35, Worn = 85, RabbitState = 90, AiState = 50, Owner = 25, HandoverState = 59, Edit = 32,
            HitchCommand = 100, HitchFreeze = 101, HitchFrozen = 102, Refetch = 58, RadioSync = 54, IgnitionKey = 96;
        static readonly Dictionary<int, HashSet<uint>> _staleEnts = new Dictionary<int, HashSet<uint>>();
        static readonly NetWriter W = new NetWriter(), WS = new NetWriter();
        static readonly List<uint> States = new List<uint>();
        static int Resyncs, Refetched;
        static byte FailType;
        static HashSet<uint> KnownBy(int player) => Known;
        static void SendSettledState(int player, SEnt se) { States.Add(se.NetId); ServerSendTo(player, W, true); }
        static void SendRabbitSnapshot(int player, SEnt se) { }
        static void SendCreatureSnapshots(int player, SEnt se) { }
        static void WritePartDetached(NetWriter w, SEnt se) { w.Reset(); w.U8(PartDetached); }
        static void WriteResyncAll(NetWriter w, SEnt se) { Resyncs++; w.Reset(); w.U8(Resync); }
        static void ServerSendTo(int player, NetWriter w, bool reliable)
        { if (_tpaSending && (FailNext || (FailType != 0 && w.Len > 0 && w.Buf[0] == FailType)))
            { _tpaSendFailed = true; FailNext = false; FailType = 0; } }
        static void InterestGotAll(int player, uint net) { _staleEnts[player].Remove(net); }
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly HashSet<uint> Known = new HashSet<uint>();
        static readonly List<uint> Sent = new List<uint>();
        static readonly Dictionary<uint, object> ByNet = new Dictionary<uint, object>();
        static readonly List<uint> Refetches = new List<uint>();
        static void AddReject(uint net, string why) { }
        static void ToServer(NetWriter w, bool reliable)
        {
            var r = new NetReader { Buf = w.Buf, End = w.Len };
            if (r.U8() == Refetch) Refetches.Add(r.U32());
        }
        static readonly List<int> Applied = new List<int>();
        static bool FailNext;
        static double InterestM = 3000;
        static int InterestPerTick = 150;
        static void ServerLockedPose(SEnt se) { }
        static void SendObject(int player, SEnt se)
        {
            Sent.Add(se.NetId); Known.Add(se.NetId);
            if (FailNext && _tpaSending) { _tpaSendFailed = true; FailNext = false; }
        }
        static void ClientReceive(byte type, NetReader r)
        {
            if (DeferTpa(type, r)) return;
            TpaCopyMessage(type, r);
            Applied.Add(type * 1000 + r.U8());
        }
        static void At(float now) { Time.realtimeSinceStartup = now; ServerTpaTick(); }
        static void AddEntity(uint id, double x, double z) => Server.Add(id, new SEnt { NetId = id, Where = new Vector3d(x, 0, z) });
        internal static void TestSync(Action<bool, string> check)
        {
            var destination = new Vector3d(-8_000_000, 50, -6_000_000);
            Mp.Server = new MpServer { Pos = new Vector3d(-2000, 0, -35000) };
            for (uint id = 1; id <= 1005; id++) { AddEntity(id, destination.x + id / 10.0, destination.z); Known.Add(id); }
            AddEntity(2000, -2000, -35000); // old interest area
            AddEntity(2001, destination.x + InterestM, destination.z); // outside boundary
            AddEntity(3000, destination.x + 5000, destination.z); // parent car outside interest
            Server[3001] = new SEnt { NetId = 3001, ParentNet = 3000, PartIndex = 2, Where = destination };
            ServerTpaBegin(1, destination); At(1);
            check(Sent.Count == 0, "cached pre-teleport position cannot trigger destination sync");
            Mp.Server.Pos = destination; At(2);
            check(Sent.Count == 0 && States.Count == 150, "known copies get states without full Add records");
            for (int i = 0; i < 10; i++) At(2.2f + i * .2f);
            check(Sent.Count == 1 && States.Count == 1005, "only missing parent gets a full record; known groups refresh once");
            check(Sent.Contains(3000) && !Sent.Contains(3001), "in-range detached part requires full parent first");
            check(!Sent.Contains(2000) && !Sent.Contains(2001), "old area and out-of-range groups excluded");
            AddEntity(4000, destination.x + 2, destination.z); At(4.5f);
            check(Sent.Contains(4000), "generation sharing late objects is picked up during arrival");
            At(8);
            check(SessionLog.LastCount == 1007 && TpaSyncs.Count == 0, "arrival telemetry counts full groups sent and finishes");
            ServerRefetch(1, 3001);
            check(Refetched == 1 && Sent[Sent.Count - 1] == 3000, "absent detached-part state refetches full parent before replaying the part");
            Sent.Clear(); ServerTpaBegin(1, new Vector3d(-2000, 0, -35000));
            Mp.Server.Pos = new Vector3d(-2000, 0, -35000); At(9);
            check(Sent.Count == 1 && Sent[0] == 2000, "second large jump rebuilds delivery set");
            Sent.Clear(); ServerTpaBegin(1, destination); Mp.Server.Pos = destination; At(10);
            check(Sent.Count == 0, "return jump uses stored copies without full records");
            Server[1].ItemState.Add(1); _staleEnts[1] = new HashSet<uint> { 1 };
            SendTpaState(1, Server[1]); SendTpaState(1, Server[1]);
            check(Resyncs == 1 && _staleEnts[1].Count == 0, "only changed known records receive a resync");
            ResetTpaSync();
            check(TpaSyncs.Count == 0, "session reset discards queued sync");
            Server.Clear(); Sent.Clear(); AddEntity(5000, destination.x, destination.z);
            ServerTpaBegin(1, destination); FailNext = true; At(11);
            check(TpaSyncs[1].Delivery.Sent.Count == 0, "partially queued object is still owed even if Known was updated");
            Known.Add(5000); // normal interest may have touched Known between arrival retries
            At(11.2f);
            check(Sent.Count == 2 && TpaSyncs[1].Delivery.Sent.Count == 1, "failed full send retried before marking complete");
            ResetTpaSync();
            Server[5000].Parts.Add(5001); Server[5001] = new SEnt { NetId = 5001 };
            Server[5000].ItemState.Add(1); _staleEnts[1] = new HashSet<uint> { 5000 };
            Known.Remove(5001); _tpaSending = true; _tpaSendFailed = false; FailType = PartDetached;
            SendTpaState(1, Server[5000]);
            check(_tpaSendFailed && !Known.Contains(5001) && _staleEnts[1].Contains(5000), "failed known-copy sends retain part and stale resync debt");
            _tpaSendFailed = false; FailType = Resync; SendTpaState(1, Server[5000]);
            check(Known.Contains(5001) && _staleEnts[1].Contains(5000), "failed resync retains InterestGotAll debt after part succeeds");
            _tpaSendFailed = false; SendTpaState(1, Server[5000]); _tpaSending = false;
            check(Known.Contains(5001) && !_staleEnts[1].Contains(5000), "retry completes known-copy part and resync delivery");

            Mp.Server = null; Tpa.RefreshingObjects = true; BeginTpaRefresh();
            var missingState = new NetWriter(); missingState.VarU32(5000); missingState.VarU32(1);
            var mr = new NetReader { Buf = missingState.Buf, End = missingState.Len };
            ClientReceive(State, mr); mr.Pos = 0; ClientReceive(CompactState, mr);
            check(Known.Contains(5000) && Refetches.Count == 1 && Refetches[0] == 5000, "Known but absent client copy requests one refetch across state formats");
            BeginTpaRefresh(); mr.Pos = 0; ClientReceive(State, mr);
            check(Refetches.Count == 2, "next arrival may refetch a repeat loss");
            ByNet[5000] = new object(); mr.Pos = 0; ClientReceive(State, mr);
            check(Tpa.Applied.Contains(5000) && Refetches.Count == 2, "known State counts applied without refetch");
            check(TpaDuplicate(5000) && !TpaDuplicate(5002), "duplicate Add uses stored copy without spawning it synchronously");
            foreach (byte type in new[] { Resync, RabbitState })
            {
                var w = new NetWriter(); w.U32((uint)(6000 + type));
                var r = new NetReader { Buf = w.Buf, End = w.Len }; TpaCopyMessage(type, r); TpaCopyMessage(type, r);
            }
            check(Refetches.Count == 4, "missing Resync and RabbitState nets each refetch once");
            Tpa.RefreshingObjects = false; Applied.Clear();

            Mp.Server = null; Tpa.WaitingWorld = true;
            ClientReceive(Add, new NetReader { Buf = new byte[] { 7 }, End = 1 });
            ClientReceive(PartDetached, new NetReader { Buf = new byte[] { 8 }, End = 1 });
            DrainTpaInbox(); check(Applied.Count == 0, "no objects or states applied before destination geometry exists");
            ClientReceive(97, new NetReader { Buf = new byte[] { 9 }, End = 1 });
            check(Applied.Count == 1 && Applied[0] == 97009, "world clock bypasses ground wait"); Applied.Clear();
            ClientReceive(Owner, new NetReader { Buf = new byte[] { 9 }, End = 1 });
            check(Applied.Count == 1, "known ownership bypasses ground wait"); Applied.Clear();
            Tpa.WaitingWorld = false;
            ClientReceive(State, new NetReader { Buf = new byte[] { 9 }, End = 1 });
            DrainTpaInbox();
            check(string.Join(",", Applied) == "22007,27008,23009", "reliable add, parts and states keep wire order through readiness transition");
            for (int i = 0; i < 450; i++) { Tpa.WaitingWorld = true; ClientReceive(Add, new NetReader { Buf = new byte[] { 1 }, End = 1 }); }
            Tpa.WaitingWorld = false; int before = Applied.Count; DrainTpaInbox();
            check(Applied.Count - before == 200 && !TpaInboxEmpty, "client replay work is bounded");
            DrainTpaInbox(); DrainTpaInbox(); check(TpaInboxEmpty && Applied.Count == before + 450, "all deferred adds eventually applied");
            Tpa.WaitingWorld = true; ClientReceive(Add, new NetReader { Buf = new byte[] { 2 }, End = 1 });
            ResetTpaSync(); Tpa.WaitingWorld = false; before = Applied.Count; DrainTpaInbox();
            check(TpaInboxEmpty && Applied.Count == before, "session reset cannot replay previous world's objects");

            Tpa.WaitingWorld = true;
            before = Applied.Count;
            var add = new NetReader { Buf = new byte[] { 42, 0, 0, 0 }, End = 4 };
            ClientReceive(Add, add);
            ClientReceive(Owner, new NetReader { Buf = new byte[] { 42, 0, 0, 0 }, End = 4 });
            ClientReceive(65, new NetReader { Buf = new byte[] { 42, 0, 0, 0 }, End = 4 });
            check(Applied.Count == before + 1 && Applied[before] == 65042, "census control bypasses even a queued net ID");
            Tpa.WaitingWorld = false; DrainTpaInbox();
            check(Applied[before + 1] == 22042 && Applied[before + 2] == 25042, "ownership of a queued Add waits for its copy");
            ResetTpaSync();
            before = Applied.Count; Tpa.WaitingWorld = true;
            ClientReceive(PartDetached, new NetReader { Buf = new byte[] { 42, 0, 0, 0, 2, 43, 0, 0, 0 }, End = 9 });
            ClientReceive(Owner, new NetReader { Buf = new byte[] { 43, 0, 0, 0 }, End = 4 });
            ClientReceive(101, new NetReader { Buf = new byte[] { 43, 0, 0, 0 }, End = 4 });
            ClientReceive(HitchFrozen, new NetReader { Buf = new byte[] { 43, 0, 0, 0 }, End = 4 });
            check(Applied.Count == before, "hitch freeze and final pose wait for their queued part");
            Tpa.WaitingWorld = false; DrainTpaInbox();
            check(Applied[before] == 27042 && Applied[before + 1] == 25043 && Applied[before + 2] == 101043 && Applied[before + 3] == 102043,
                "part, ownership, hitch freeze and frozen pose keep wire order");
            ResetTpaSync();
        }
    }
    public static class Protocol { public const byte LiveUsable = 93; }
    public static class Mp { public static MpServer Server; }
    public sealed class MpServer
    {
        internal Vector3d Pos;
        internal bool PeerPos(int id, out Vector3d pos) { pos = Pos; return true; }
    }
    public static class Tpa
    {
        internal static bool WaitingWorld, RefreshingObjects;
        internal static readonly HashSet<uint> Applied = new HashSet<uint>();
        internal static void ObjectApplied(uint net) { Applied.Add(net); }
    }
}
namespace TLDRevamp
{
    public static class SessionLog
    {
        internal static int LastCount;
        internal static void Event(string kind, int from = -1, int to = -1, float value = 0, int count = 0, float dx = 0, float dy = 0, float dz = 0, string result = "ok")
        { if (kind == "tpa-arrived") LastCount = count; }
    }
}
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public double magnitude => Math.Sqrt(x * x + y * y + z * z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
}
