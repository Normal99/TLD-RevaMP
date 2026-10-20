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

        Entities.TestSync(Check);
        Console.WriteLine($"Teleport: {checks} checks passed");
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        sealed class SEnt { internal uint NetId, ParentNet; internal int PartIndex = -1; internal Vector3d Where; }
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
        static void Add(uint id, double x, double z) => Server.Add(id, new SEnt { NetId = id, Where = new Vector3d(x, 0, z) });
        internal static void TestSync(Action<bool, string> check)
        {
            var destination = new Vector3d(-8_000_000, 50, -6_000_000);
            Mp.Server = new MpServer { Pos = new Vector3d(-2000, 0, -35000) };
            for (uint id = 1; id <= 1005; id++) { Add(id, destination.x + id / 10.0, destination.z); Known.Add(id); }
            Add(2000, -2000, -35000); // old interest area
            Add(2001, destination.x + InterestM, destination.z); // outside boundary
            Add(3000, destination.x + 5000, destination.z); // parent car outside interest
            Server[3001] = new SEnt { NetId = 3001, ParentNet = 3000, PartIndex = 2, Where = destination };
            ServerTpaBegin(1, destination); At(1);
            check(Sent.Count == 0, "cached pre-teleport position cannot trigger destination sync");
            Mp.Server.Pos = destination; At(2);
            check(Sent.Count == 150, "full refresh bounded per tick even when everything was already Known");
            for (int i = 0; i < 10; i++) At(2.2f + i * .2f);
            check(Sent.Count == 1006 && new HashSet<uint>(Sent).Count == 1006, "every in-range group sent exactly once regardless of Known");
            check(Sent.Contains(3000) && !Sent.Contains(3001), "in-range detached part requires full parent first");
            check(!Sent.Contains(2000) && !Sent.Contains(2001), "old area and out-of-range groups excluded");
            Add(4000, destination.x + 2, destination.z); At(4.5f);
            check(Sent.Contains(4000), "generation sharing late objects is picked up during arrival");
            At(8);
            check(SessionLog.LastCount == 1007 && TpaSyncs.Count == 0, "arrival telemetry counts full groups sent and finishes");
            Sent.Clear(); ServerTpaBegin(1, new Vector3d(-2000, 0, -35000));
            Mp.Server.Pos = new Vector3d(-2000, 0, -35000); At(9);
            check(Sent.Count == 1 && Sent[0] == 2000, "second large jump rebuilds delivery set");
            Sent.Clear(); ServerTpaBegin(1, destination); Mp.Server.Pos = destination; At(10);
            check(Sent.Count == 150, "return jump resends full records even if Known still contains them");
            ResetTpaSync();
            check(TpaSyncs.Count == 0, "session reset discards queued sync");
            Server.Clear(); Sent.Clear(); Add(5000, destination.x, destination.z);
            ServerTpaBegin(1, destination); FailNext = true; At(11);
            check(TpaSyncs[1].Delivery.Sent.Count == 0, "partially queued object is still owed even if Known was updated");
            At(11.2f);
            check(Sent.Count == 2 && TpaSyncs[1].Delivery.Sent.Count == 1, "failed full send retried before marking complete");
            ResetTpaSync();

            Mp.Server = null; Tpa.WaitingWorld = true;
            ClientReceive(1, new NetReader { Buf = new byte[] { 7 }, End = 1 });
            ClientReceive(2, new NetReader { Buf = new byte[] { 8 }, End = 1 });
            DrainTpaInbox(); check(Applied.Count == 0, "no objects or states applied before destination geometry exists");
            Tpa.WaitingWorld = false;
            ClientReceive(3, new NetReader { Buf = new byte[] { 9 }, End = 1 });
            DrainTpaInbox();
            check(string.Join(",", Applied) == "1007,2008,3009", "reliable add, parts and states keep wire order through readiness transition");
            for (int i = 0; i < 450; i++) { Tpa.WaitingWorld = true; ClientReceive(1, new NetReader { Buf = new byte[] { 1 }, End = 1 }); }
            Tpa.WaitingWorld = false; int before = Applied.Count; DrainTpaInbox();
            check(Applied.Count - before == 200 && !TpaInboxEmpty, "client replay work is bounded");
            DrainTpaInbox(); DrainTpaInbox(); check(TpaInboxEmpty && Applied.Count == before + 450, "all deferred adds eventually applied");
            Tpa.WaitingWorld = true; ClientReceive(1, new NetReader { Buf = new byte[] { 2 }, End = 1 });
            ResetTpaSync(); Tpa.WaitingWorld = false; before = Applied.Count; DrainTpaInbox();
            check(TpaInboxEmpty && Applied.Count == before, "session reset cannot replay previous world's objects");
        }
    }
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
        internal static void Event(string kind, int to = -1, float value = 0, int count = 0, float dx = 0, float dy = 0, float dz = 0, string result = "ok")
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
