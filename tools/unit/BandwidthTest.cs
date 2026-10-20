#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

static class BandwidthTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Main()
    {
        var w = new NetWriter();
        foreach (uint net in new uint[] { 1, 128, 65536, uint.MaxValue })
        {
            w.Reset(); w.U8(23); w.VarU32(net); w.VarU32(1); w.U16(1); w.U32(100);
            for (int i = 0; i < 30; i++) w.U8((byte)i);
            var previous = new byte[w.Len]; Array.Copy(w.Buf, previous, w.Len);
            int pose = BandwidthRules.StatePoseAt(w.Buf, w.Len);
            w.Buf[pose - 6]++; w.Buf[pose - 4]++;
            Check(BandwidthRules.SameState(previous, previous.Length, w.Buf, w.Len), "sequence/time alone carry no state");
            for (int i = pose; i < w.Len; i++)
            {
                w.Buf[i] ^= 1;
                Check(!BandwidthRules.SameState(previous, previous.Length, w.Buf, w.Len), "pose/velocity/flags/limbs always differ");
                w.Buf[i] ^= 1;
            }
            w.Buf[pose - 7]++;
            Check(!BandwidthRules.SameState(previous, previous.Length, w.Buf, w.Len), "epoch always differs");
        }
        Check(!BandwidthRules.SameState(Array.Empty<byte>(), 0, w.Buf, w.Len), "first state always goes out");
        Check(!BandwidthRules.SkipState(true, true, false, false, false), "comes-to-rest state always goes out, even identical");
        Check(BandwidthRules.SkipState(true, true, false, true, false), "repeat of an already sent rest state is waste");
        Check(!BandwidthRules.SkipState(false, true, false, true, false), "waste switch off restores send");
        Check(!BandwidthRules.SkipState(true, true, true, false, false), "real motion keeps its full state rate");
        Check(!BandwidthRules.SkipState(true, true, false, true, true), "driver/owner confirmation and signal repeats go out");
        Check(!BandwidthRules.SkipState(true, false, false, true, false), "changed state always goes out");
        Check(!BandwidthRules.CreatureDue(80, 80, true, .9f, 0), "frozen dead animation produces no false savings count");
        Check(BandwidthRules.CreatureDue(80, 80, false, .1f, 0), "dead look/run churn is a suppressed candidate");
        Check(BandwidthRules.CreatureDue(80, 64, true, .1f, 0), "creature flag changes always go out");
        Check(BandwidthRules.CreatureDue(80, 80, true, 1, 0), "existing one-second creature refresh remains");
        Entities.TestBandwidth();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            BandwidthRules.SameState(w.Buf, w.Len, w.Buf, w.Len);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "warmed byte comparison allocates nothing");
        Console.WriteLine("ALL PASS (" + checks + " checks)");
    }
}

namespace TLDRevamp.Net
{
    // The entity/Unity/transport surfaces only; frame selection, visibility and relay methods are production code.
    public static partial class Entities
    {
        public static double StateInterestM = 1000;
        static readonly Dictionary<int, HashSet<uint>> Known = new Dictionary<int, HashSet<uint>>();
        static readonly NetWriter WS = new NetWriter();
        static int delivered;
        static void SkippedByInterest(int player) { }
        class Ent { public TestRoot Root = new TestRoot(); public Transform[] TestLimbs; public bool IsLimp; }
        class TestRoot { public object car; public Transform transform = new Transform(); }
        static Transform[] Limbs(Ent e) => e.TestLimbs;
        static bool Limp(Ent e) => e.IsLimp;
        class SEnt { public uint NetId; public Vector3d Where; }
        static void ServerFwdAll(int from, NetReader r, bool reliable) { Mp.Server.Sends = 3; }
        static void Deliver(NetWriter w) { delivered++; }
        internal static void TestBandwidth()
        {
            var body = new Ent { IsLimp = true, TestLimbs = new Transform[] { null, new Transform() } };
            var frame = RagdollFrame(body);
            body.Root.transform.position = new Vector3d(100, 0, 0);
            BandwidthTest.Check(frame == body.TestLimbs[1] && RagdollFrame(body).position.sqrMagnitude == 0, "drifting corpse root cannot move network frame");
            var animatedParent = new Transform { Body = null };
            var realLimb = body.TestLimbs[1]; body.TestLimbs = new[] { body.Root.transform, animatedParent, realLimb };
            BandwidthTest.Check(RagdollFrame(body) == realLimb, "frame skips unused root and animated parents in limb list");
            body.TestLimbs = new[] { realLimb };
            body.TestLimbs[0].position = new Vector3d(1, 0, 0);
            BandwidthTest.Check(RagdollFrame(body).position.x == 1, "real corpse motion moves network frame");
            body.Root.car = new object();
            BandwidthTest.Check(RagdollFrame(body) == body.Root.transform, "vehicle limbs keep car frame");
            body.Root.car = null; body.IsLimp = false;
            BandwidthTest.Check(RagdollFrame(body) == body.Root.transform, "live creature keeps root frame");
            body.IsLimp = true; WasteFixes = false;
            BandwidthTest.Check(RagdollFrame(body) == body.Root.transform, "waste switch restores original corpse frame");
            WasteFixes = true;
            Mp.Server = new MpServer();
            var candidate = new SEnt { NetId = 3 };
            var creature = new NetReader { Buf = new byte[] { 128, 0, 0 }, End = 3 };
            Known[1] = new HashSet<uint> { candidate.NetId }; Known[2] = new HashSet<uint> { candidate.NetId };
            Mp.Server.Spots.Add(new MpServer.PeerSpot { Id = 1, HasPos = true });
            Mp.Server.Spots.Add(new MpServer.PeerSpot { Id = 2, HasPos = true, Pos = new Vector3d(5000, 0, 0) });
            Mp.Server.Spots.Add(new MpServer.PeerSpot { Id = 3 });
            RelayCreature(candidate, 0, creature);
            BandwidthTest.Check(Mp.Server.Sends == 1 && StatesSkippedUnknown == 1 && StatesSkippedCreatureRange == 1, "relay sends only to known nearby viewers");
            Mp.Server.Sends = 0; WasteFixes = false; RelayCreature(candidate, 0, creature); WasteFixes = true;
            BandwidthTest.Check(Mp.Server.Sends == 3, "relay switch off restores fanout");
            Mp.Server.Sends = 0; RelayCreature(candidate, 1, creature);
            BandwidthTest.Check(delivered == 1 && Mp.Server.Sends == 0, "host always keeps owner state, sender excluded");

            BandwidthTest.Check(CreatureVisible(1, candidate.NetId, true, new Vector3d(1000, 0, 0), default), "creature at interest boundary is visible");
            BandwidthTest.Check(CreatureVisible(1, candidate.NetId, false, new Vector3d(5000, 0, 0), default), "known creature sends before viewer position arrives");
            BandwidthTest.Check(!CreatureVisible(3, candidate.NetId, false, default, default), "unknown creature skips even without viewer position");

            var snapshot = new long[5];
            StatesSkippedIdentical = 11; StatesSkippedCorpseRoot = 12; StatesSkippedDeadAnimation = 13;
            BandwidthSnapshot(snapshot);
            BandwidthTest.Check(snapshot[0] == 11 && snapshot[1] == 12 && snapshot[2] == StatesSkippedUnknown &&
                snapshot[3] == StatesSkippedCreatureRange && snapshot[4] == 13, "snapshot preserves all five waste counters");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { RagdollFrame(body); CreatureVisible(1, candidate.NetId, true, default, default); BandwidthSnapshot(snapshot); }
            BandwidthTest.Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "warmed frame, visibility and snapshot checks allocate nothing");
        }
    }
    public class MpServer
    {
        public struct PeerSpot { public int Id; public bool HasPos; public Vector3d Pos; }
        public readonly List<PeerSpot> Spots = new List<PeerSpot>();
        public int Sends;
        public void SendCreatureState(NetWriter w, int except, uint net, Vector3d pos)
        {
            foreach (var s in Spots) if (s.Id != except && Entities.CreatureVisible(s.Id, net, s.HasPos, s.Pos, pos)) Sends++;
        }
    }
    public static class Mp { public static MpServer Server; }
}
namespace UnityEngine
{
    public class Rigidbody { }
    public class Transform
    {
        public Vector3d position;
        public object Body = new Rigidbody();
        public T GetComponent<T>() where T : class => Body as T;
    }
}
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public double sqrMagnitude => x * x + y * y + z * z;
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
}
