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
        Check(TpaArrival.Floor(49, 50) && !TpaArrival.Floor(42.44, 50), "wait for target floor, not terrain below");
        Check(!TpaArrival.Floor(52, 50), "roof not selected");
        // Subtract in double precision before conversion to local float coordinates, including after a rebase.
        double global = -8_000_000.123, origin = -8_000_000;
        float local = (float)(global - origin);
        Check(TpaArrival.Close(origin + local - global, 0, 0), "arrival survives far origin shift");

        Check(TpaArrival.ChooseFloor(new double?[] { 49 }, 50) == 0, "vehicle roof is a floor");
        Check(TpaArrival.ChooseFloor(new double?[] { null, null, 49 }, 50) == 2, "wall-blocked centre uses clear ring floor");
        Check(TpaArrival.ChooseFloor(new double?[] { null, 40 }, 50) == -1, "no target floor waits briefly");
        Check(!TpaArrival.Fallback(2.99) && TpaArrival.Fallback(3), "ordinary any-floor fallback after three seconds");
        Check(!TpaArrival.Landing(false, true, 49, 50, 4), "fallback still requires a floor");
        Check(!TpaArrival.Landing(true, false, 49, 50, 4), "vehicle body and wall reject landing");
        Check(TpaArrival.Landing(true, true, 40, 50, 3), "high seated target can use lower floor after short wait");
        Check(TpaArrival.CarLanding(true, true, 10, 18, out double down) && Math.Abs(down + 7.6) < .001, "car lowered to ground without nearY minus three rejection");
        Check(TpaArrival.CarLanding(true, true, 25, 18, out double up) && up > 0, "car can land on higher terrain");
        Check(!TpaArrival.CarLanding(false, true, 0, 18, out _) && !TpaArrival.CarLanding(true, false, 0, 18, out _), "car never released without floor and bounds");
        Check(TpaArrival.Retry(false, 0, -6, 0, 1) && !TpaArrival.Retry(false, 0, -6, 0, 3), "rebase retry does not restart strict waits forever");
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
            HitchCommand = 100, RadioSync = 54, IgnitionKey = 96;
        static readonly Dictionary<int, HashSet<uint>> _staleEnts = new Dictionary<int, HashSet<uint>>();
        static readonly NetWriter W = new NetWriter(), WS = new NetWriter();
        static readonly List<uint> States = new List<uint>();
        static int Resyncs;
        static HashSet<uint> KnownBy(int player) => Known;
        static void SendSettledState(int player, SEnt se) { States.Add(se.NetId); }
        static void SendRabbitSnapshot(int player, SEnt se) { }
        static void SendCreatureSnapshots(int player, SEnt se) { }
        static void WritePartDetached(NetWriter w, SEnt se) { }
        static void WriteResyncAll(NetWriter w, SEnt se) { Resyncs++; }
        static void ServerSendTo(int player, NetWriter w, bool reliable) { }
        static void InterestGotAll(int player, uint net) { _staleEnts[player].Remove(net); }
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly HashSet<uint> Known = new HashSet<uint>();
        static readonly List<uint> Sent = new List<uint>();
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
            if (!DeferTpa(type, r)) Applied.Add(type * 1000 + r.U8());
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
            check(Applied.Count == before + 1 && Applied[before] == 101043, "hitch freeze control continues during part placement wait");
            Tpa.WaitingWorld = false; DrainTpaInbox();
            check(Applied[before + 1] == 27042 && Applied[before + 2] == 25043, "detached part exists before its ownership update");
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
    public static class Tpa { internal static bool WaitingWorld; }
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
