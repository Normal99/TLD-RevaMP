#pragma warning disable CS0649, CS8981
global using carscript = TestCar;
global using randomFlyer = TestFlyer;
global using ragdollactivatescript = TestRagdoll;
global using newAiScript = TestAi;
global using aiscript = TestOldAi;
global using pickupable = TestPickup;
global using nyulscript = TestRabbit;
global using tosaveitemscript = TestItem;
global using mainscript = TestMain;
global using itemdatabase = TestDatabase;
global using mountStuff = TestMount;
global using layerScript = TestLayer;
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

static class IdleTrafficTest
{
    internal static int Checks;
    internal static void Check(bool ok, string why) { Checks++; if (!ok) throw new Exception(why); }
    static BandwidthRules.Quiet Quiet(bool pose = false, bool limbs = false, bool visual = false, bool control = false,
                                     bool moving = false, bool rest = true, bool locked = false, bool attached = false, double elapsed = .05, bool first = false) =>
        Decide(first, pose, limbs, visual, control, moving, rest, locked, attached, elapsed);
    static BandwidthRules.Quiet Decide(bool first, bool pose, bool limbs, bool visual, bool control, bool moving,
                                       bool rest, bool locked, bool attached, double elapsed, bool driven = false)
    {
        var tail = new BandwidthRules.Tail();
        return BandwidthRules.StateQuiet(ref tail, elapsed, first, pose, limbs, visual, control, moving, rest, locked, attached, elapsed, driven);
    }
    static void Timelines()
    {
        foreach (string mode in new[] { "resting item", "sleeping item", "stored item", "physlocked item", "attached item", "settled corpse" })
        {
            var tail = new BandwidthRules.Tail();
            for (int tick = 1; tick <= 1200; tick++)
            {
                double now = tick / 20.0;
                var q = BandwidthRules.StateQuiet(ref tail, now, false, false, false, false, false, false, true,
                                                 mode == "stored item" || mode == "physlocked item", mode == "attached item", now);
                Check(q != BandwidthRules.Quiet.None && q != BandwidthRules.Quiet.Final, mode + " sends nothing for 60 seconds");
            }
        }
        foreach (string mode in new[] { "stored", "locked", "attached", "already quiet awake" })
        {
            var quietTail = new BandwidthRules.Tail();
            for (int tick = 1; tick <= 1200; tick++)
            {
                double now = tick / 20.0;
                var q = BandwidthRules.StateQuiet(ref quietTail, now, false, false, false, false, false, true, true,
                                                 mode == "locked", mode == "attached", now, stored: mode == "stored");
                Check(q != BandwidthRules.Quiet.None && q != BandwidthRules.Quiet.Final, mode + " gets no tail from awake alone");
            }
        }
        foreach (string mode in new[] { "jittering awake item", "parked unoccupied car" })
        {
            var tail = new BandwidthRules.Tail(); double last = 0; bool rest = false; int keepalives = 0, finals = 0;
            Check(BandwidthRules.StateQuiet(ref tail, 0, false, true, false, false, false, true, false, false, false, 0) == BandwidthRules.Quiet.None, mode + " sends real visual change");
            for (int tick = 1; tick <= 1200; tick++)
            {
                double now = tick / 20.0;
                var q = BandwidthRules.StateQuiet(ref tail, now, false, false, false, false, false, true, rest, false, false, now - last);
                if (q == BandwidthRules.Quiet.None) { Check(now < 5, mode + " keepalive only in tail"); keepalives++; last = now; }
                if (q == BandwidthRules.Quiet.Final) { Check(now == 5, mode + " final at five seconds"); finals++; rest = true; last = now; }
            }
            Check(keepalives == 4 && finals == 1 && !tail.Active, mode + " has four keepalives, one final, then silence");
            Check(BandwidthRules.StateQuiet(ref tail, 61, false, true, false, false, false, true, rest, false, false, 56) == BandwidthRules.Quiet.None && tail.Active, mode + " visible motion restarts tail");
        }
        var drivenTail = new BandwidthRules.Tail(); double sent = 0; int refreshes = 0;
        for (int tick = 1; tick <= 1200; tick++)
        {
            double now = tick / 20.0;
            var q = BandwidthRules.StateQuiet(ref drivenTail, now, false, false, false, false, false, false, true, false, false, now - sent, true);
            if (q == BandwidthRules.Quiet.None) { Check(now - sent == .5, "driven parked car refreshes exactly every half second"); refreshes++; sent = now; }
        }
        Check(refreshes == 120, "driver seat keeps its lease for all 60 seconds");
        drivenTail.Active = true; drivenTail.VisualAt = 60;
        Check(BandwidthRules.StateQuiet(ref drivenTail, 60.5, false, false, false, false, false, false, false, false, false, .5, true) == BandwidthRules.Quiet.None,
            "driver refresh during a new stop bypasses the rest confirmation interval");
        Check(BandwidthRules.StateQuiet(ref drivenTail, 60.05, false, false, false, false, true, false, true, false, false, .05) == BandwidthRules.Quiet.None, "driver exit sends flag change");
        for (int tick = 1; tick <= 1200; tick++)
            Check(BandwidthRules.StateQuiet(ref drivenTail, 60.05 + tick / 20.0, false, false, false, false, false, false, true, false, false, tick / 20.0) == BandwidthRules.Quiet.Pose, "empty driver seat stops lease refreshes");
    }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Run()
    {
        Check(!BandwidthRules.PoseChanged(.009 * .009, .29f), "sub-threshold jitter carries no visible pose");
        Check(BandwidthRules.PoseChanged(.011 * .011, 0), "cumulative centimetre motion is visible");
        Check(BandwidthRules.PoseChanged(0, .31f), "cumulative rotation is visible");
        foreach (string category in new[] { "car", "trailer", "item", "rabbit", "flyer", "corpse" })
        {
            Check(Quiet() == BandwidthRules.Quiet.Pose, category + " at rest skips jitter");
            for (int tick = 0; tick < 20; tick++) Check(Quiet(pose: true, moving: true, rest: false) == BandwidthRules.Quiet.None, category + " keeps full 20 Hz motion");
            Check(Quiet(elapsed: 60) == BandwidthRules.Quiet.Pose, category + " resting state never refreshes");
            Check(Quiet(rest: false) == BandwidthRules.Quiet.Final, category + " sends final rest state");
            Check(Quiet(first: true) == BandwidthRules.Quiet.None, category + " first state/epoch confirmation");
        }
        foreach (string exemption in new[] { "held", "stored", "driver", "signal repeat", "spray repeat" })
            Check(Quiet(control: true, locked: true) == BandwidthRules.Quiet.None, exemption + " stays exempt");
        Check(Quiet(locked: true, elapsed: 10) == BandwidthRules.Quiet.Locked, "still physlock suppresses even keepalive");
        Check(Quiet(locked: true, pose: true, moving: true, rest: false) == BandwidthRules.Quiet.None, "moving physlock retains pose rate");
        Check(Quiet(attached: true, pose: true, moving: true, elapsed: 10) == BandwidthRules.Quiet.Attached, "attached parts never stream loose poses");
        Check(Quiet(attached: true, control: true) == BandwidthRules.Quiet.None, "attachment can clear held/inventory flag");
        Check(Quiet(limbs: true) == BandwidthRules.Quiet.None && Quiet() == BandwidthRules.Quiet.Pose, "limb motion sends; settled limbs skip");
        Check(Decide(false, false, false, false, false, false, true, false, false, .49, true) == BandwidthRules.Quiet.Pose, "still driven car skips between driver refreshes");
        Check(Decide(false, false, false, false, false, false, true, false, false, .5, true) == BandwidthRules.Quiet.None, "driver refresh precedes existing one-second lease expiry");
        Check(!BandwidthRules.RabbitDue(false, 128, 128, 0, 0, false, false), "dead rabbit animation has no periodic traffic");
        Check(!BandwidthRules.RabbitDue(false, 0, 0, 100, 102, false, false), "rabbit run jitter skips");
        Check(BandwidthRules.RabbitDue(false, 0, 0, 0, 3, false, false), "real rabbit animation change sends");
        Check(BandwidthRules.RabbitDue(false, 0, 2, 0, 0, false, false), "rabbit roar/attack flags send");
        Check(BandwidthRules.RabbitDue(true, 128, 128, 0, 0, false, false), "dead rabbit refreshes on epoch change");
        Check(!BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, false), "live rabbit needs no periodic refresh with reliable changes");
        var old = new byte[] { 0x81, 0, 0, 0, 0, 0, 0, 3, 0xE8, 3, 1, 0, 0 };
        var current = (byte[])old.Clone(); current[1] = 4; current[5] = 255; current[6] = 127; current[8] = 16; current[9] = 4;
        Check(!BandwidthRules.CarVisualChanged(old, old.Length, current, current.Length), "parked suspension/rpm and torque jitter skips");
        current[1] = 5; Check(BandwidthRules.CarVisualChanged(old, old.Length, current, current.Length), "suspension change beyond two percent sends");
        current = (byte[])old.Clone(); current[2] = 1; Check(BandwidthRules.CarVisualChanged(old, old.Length, current, current.Length), "steer change sends");
        current = (byte[])old.Clone(); current[8] = 27; current[9] = 4; Check(BandwidthRules.CarVisualChanged(old, old.Length, current, current.Length), "engine rpm change sends");
        foreach (int at in new[] { 7, 10, 11, 12 })
        { current = (byte[])old.Clone(); current[at] ^= 16; Check(BandwidthRules.CarVisualChanged(old, old.Length, current, current.Length), "engine/signal/gear/horn visual sends"); }
        Timelines();
        TestRestCreep();
        Entities.TestIdle();
        Entities.TestPhysLock();
        Console.WriteLine("ALL PASS (" + Checks + " checks)");
    }

    static void TestRestCreep()
    {
        // batch17 net 199: one position wake plus one final, with no awake/rotation counter advance.
        var tail = new BandwidthRules.Tail(); double last = 0;
        for (int tick = 1; tick <= 1200; tick++)
        {
            double z = .01 + (tick % 2 == 0 ? .00001 : -.00001);
            bool pose = BandwidthRules.PositionChanged((z - last) * (z - last), true);
            var quiet = BandwidthRules.StateQuiet(ref tail, tick / 20.0, false, pose, false, false, false, pose, true, false, false, tick / 20.0);
            Check(quiet == BandwidthRules.Quiet.Pose, "parked 1 cm suspension creep stays silent across its old boundary");
        }
        Check(BandwidthRules.PositionChanged(.011 * .011, false), "moving cars and ordinary items keep the 1 cm threshold");
        Check(!BandwidthRules.PositionChanged(.019 * .019, true), "parked position hysteresis stays quiet below 2 cm");
        int sends = 0; bool rest = true;
        for (int tick = 1; tick <= 100; tick++)
        {
            double z = tick * .001;
            bool pose = BandwidthRules.PositionChanged((z - last) * (z - last), rest);
            if (!pose) continue;
            Check(BandwidthRules.StateQuiet(ref tail, tick / 20.0, false, pose, false, false, false, true, rest, false, false, .05) == BandwidthRules.Quiet.None,
                "accumulated slow motion wakes immediately and retains each moving pose");
            last = z; rest = false; sends++;
        }
        Check(sends > 5 && last > .09, "suppressed samples never advance the last-sent baseline or hide sustained slow movement");
        Check(BandwidthRules.PositionChanged(.011 * .011, false) && !BandwidthRules.PositionChanged(.011 * .011, true),
            "moving and resting car thresholds form hysteresis without a send cap");

        // batch17 net 78: 302 -> 303, sentRest false -> true, why unchanged, 0.246 degrees.
        tail = default; tail.Active = true; tail.VisualAt = 0; int finals = 0;
        rest = false;
        for (int tick = 1; tick <= 1200; tick++)
        {
            double now = tick / 20.0;
            bool pose = BandwidthRules.PoseChanged(0, .24623f);
            var quiet = BandwidthRules.StateQuiet(ref tail, now, false, pose, false, false, false, false, rest, false, false, now);
            Check(quiet != BandwidthRules.Quiet.None, "wundergk sub-threshold rotation cannot restart its stream");
            if (quiet == BandwidthRules.Quiet.Final) { finals++; rest = true; }
        }
        Check(finals == 1 && rest, "wundergk keeps one required final and remains silent afterwards");
        Check(BandwidthRules.PoseChanged(0, .31f), "real item rotation still resumes immediately");
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte State = 23, CompactState = 98, Claim = 24, Owner = 25;
        public class Ent
        {
            public TestItem Root = new TestItem(); public int PartIndex = -1; public Rag Rag; public bool Proxy, Held, Stored, ReleaseGhost;
            public byte[] RabbitSnapshot;
            internal double InterestSentAt, LastPoseStamp; public float OwnerAt;
            internal uint PickupOwnerEpoch; internal int PickupOwnerId, HitchAuthorityOwner;
            internal int SentWornSlot, SentStateLength; internal uint LastStateEpoch; internal double CompactStamp; internal bool HitchWaiting;
            public readonly List<Rigidbody> MadeKinematic = new List<Rigidbody>();
            public uint NetId, Epoch = 1, LockFrame, LockContainer, SentLockContainer, ReceivedStateEpoch = 1; public int OwnerId, LockItem = -1, LockMount = -1;
            public TestMount PoseLockMount;
            public int WornBy = -1; public bool SentAtRest, PartsColOff, SentHeld; internal PhysLockPose LockPose; internal BandwidthRules.Tail QuietTail;
            public object[] Wheels;
            public bool PoseLocked; public Vector3 LockLocalPos; public Quaternion LockLocalRot;
            public Vector3d CarriedGlobal, FarPos; public Quaternion CarriedGlobalRot, FarRot; public bool FarDirty;
            public List<TestItem> Items = new List<TestItem>();
            public HashSet<int> Split;
            public uint CarriedBy; public PoseInterpolator Ip = new PoseInterpolator(); public Vector3 LastVel; public bool MovedByBody;
        }
        class SEnt
        {
            public int PrevOwnerId = -1; public float PrevOwnerAt = -100; public uint RabbitHandoverEpoch;
            public uint NetId, Epoch, LockFrame, AttachParent, PickupEpoch, ParentNet; public HashSet<uint> AttachKids; public int OwnerId; public byte[] RabbitSnapshot;
            internal byte[] LatestState, SettledState;
            internal uint ReceivedStateEpoch, AiSnapshotEpoch;
            internal readonly Dictionary<int, byte[]> AiSnapshots = new Dictionary<int, byte[]>();
            public bool Held, Stored, Driven, Rabbit, PickupPending, PickupHeld, Car; public int PartIndex = -1; public float Speed; public uint HitchCar; public int CrashReturnTo = -1, ClaimedBy; public float ClaimedAt, DrivenAt, RabbitHoldUntil; internal double ReceivedStateAt;
            internal PickupRace PickupRace;
            public bool HasState; public Vector3d Where, Pos; public Quaternion Rot; public Vector3 LockLocalPos; public Quaternion LockLocalRot;
        }
        static bool RidesBolted(Ent e) => false;
        static bool WornRides(Ent e) => false;
        static void StopBodyMotion(Ent e) { e.MovedByBody = false; }
        static void Coast(Ent e) { }
        static bool CargoClaims;
        static float CargoClaimCooldownS = 2;
        static long CargoClaimed;
        static bool RidesParent(Ent e) => false;
        static void LocalSeat(out uint car, out int seat) { car = 0; seat = 0; }
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        static readonly Dictionary<int, HashSet<uint>> Known = new Dictionary<int, HashSet<uint>>();
        static readonly HashSet<GameObject> ProxyItems = new HashSet<GameObject>();
        static readonly NetWriter WS = new NetWriter();
        static double StateInterestM = 1000;
        static void ServerFwdAll(int from, NetReader r, bool reliable) { }
        static void Deliver(NetWriter w) { }
        static bool Resolve(Ent e) => e.Root != null;
        static bool LimbsByBody(Ent e) => false;
        internal static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        internal static void TestIdle()
        {
            TestPickupRace();
            TestLiveRabbitTraffic();
            TestStateInterest();
            TestCorpseTraffic();
            TestSolverTraffic();
            TestIntermittentMotion();
            var part = new Ent(); part.Root.attachable = new TestAttach { attached = true, isPart = true, slot = new object(), point = new Transform() };
            part.Root.attachable.point.Add(new TestCar());
            IdleTrafficTest.Check(AttachedCarPart(part), "joint-mounted engine/coolant counts as attached without parenting");
            part.Root.car = new TestCar(); IdleTrafficTest.Check(!AttachedCarPart(part), "jointed vehicle/trailer keeps pose stream");
            part.Root.car = null; part.Root.attachable.attached = false;
            IdleTrafficTest.Check(!AttachedCarPart(part), "detached part resumes loose pose stream");
            var flyer = new Ent(); flyer.Root.Add(new TestFlyer());
            IdleTrafficTest.Check(CompactFlyer(flyer), "random flyer selects compact pose");
            flyer.Root.P = new TestPickup(); IdleTrafficTest.Check(!CompactFlyer(flyer), "pickup items keep full pose");
            var w = new NetWriter();
            foreach (uint net in new uint[] { 1, 595, uint.MaxValue })
            {
                w.Reset(); CompactPoseCodec.Write(w, net, 3, 15, 123.456, -12345.678, 1000.014, 12345.678, -90, true);
                var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadEntityHead(r, true, out uint id, out uint epoch, out double stamp, out var p, out var q, out _, out bool driven, out bool stored, out bool held, out bool spray);
                IdleTrafficTest.Check(!r.Bad && r.Pos == r.End && id == net && epoch == 3 && stamp == 123.456 && Math.Abs(p.x + 12345.678) <= .005 && Math.Abs(p.y - 1000.014) <= .005 && Math.Abs(p.z - 12345.678) <= .005, "compact global pose round trip");
                IdleTrafficTest.Check(Quaternion.Angle(q, Quaternion.Euler(0, 270, 0)) < .01 && ReadCompactMoving && !driven && !stored && !held && !spray, "compact yaw and flags round trip");
                IdleTrafficTest.Check(w.Len == (net == 1 ? 24 : net == 595 ? 25 : 28), "compact pose size");
                for (int n = 1; n < w.Len; n++)
                { var truncated = new NetReader { Buf = w.Buf, Pos = 1, End = n }; ReadEntityHead(truncated, true, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _); IdleTrafficTest.Check(truncated.Bad, "truncated compact pose rejected"); }
            }
            w.Reset(); WriteStateHead(w, 1, 1, 1, 1, default, Quaternion.identity, Vector3.zero, false, final: true);
            var finalReader = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(finalReader, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            IdleTrafficTest.Check(ReadFinalState && !finalReader.Bad, "normal final flag survives wire codec");
            var snapshot = FinalSnapshot(finalReader, ReadFinalState);
            w.Buf[0] = 0;
            IdleTrafficTest.Check(snapshot[0] == State && snapshot.Length == w.Len && FinalSnapshot(finalReader, false) == null, "retained final owns its bytes; newer motion clears it");
            var replay = new NetReader { Buf = snapshot, Pos = 1, End = snapshot.Length };
            ReadEntityHead(replay, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            IdleTrafficTest.Check(ReadFinalState && !replay.Bad, "late join replays the complete final without a live owner keepalive");
            w.Reset(); CompactPoseCodec.Write(w, 1, 1, 1, 1, 0, 0, 0, 0, false, true);
            finalReader = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(finalReader, true, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            IdleTrafficTest.Check(ReadFinalState && !ReadCompactMoving && !finalReader.Bad && w.Len == 24, "compact final is stationary without increasing packet size");
            w.Reset(); CompactPoseCodec.Write(w, 1, 1, 2, 2, 0, 0, 0, 0, true);
            finalReader = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(finalReader, true, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            IdleTrafficTest.Check(!ReadFinalState && ReadCompactMoving, "ordinary state clears final flag");
            double previous = 0; uint previousEpoch = 0;
            IdleTrafficTest.Check(NewerState(5, 1, ref previous, ref previousEpoch) && !NewerState(4, 1, ref previous, ref previousEpoch) && !NewerState(5, 1, ref previous, ref previousEpoch), "delayed reliable final and older motion cannot replace newer state");
            IdleTrafficTest.Check(NewerState(1, 2, ref previous, ref previousEpoch), "new ownership epoch resets timestamp ordering");
            var ip = new PoseInterpolator();
            ip.Add(new PoseInterpolator.Sample { T = 1, Pos = new Vector3d(0, 0, 0), Rot = Quaternion.identity }, 1.1);
            var velocity = CompactVelocity(true, new Vector3d(1, 0, 0), 1.05, default, 1);
            ip.Add(new PoseInterpolator.Sample { T = 1.05, Pos = new Vector3d(1, 0, 0), Rot = Quaternion.identity, Vel = velocity }, 1.15);
            ip.Evaluate(1.19, .01f, out var shown, out _);
            IdleTrafficTest.Check(shown.x > 0 && shown.x < 1, "received compact poses interpolate between samples");
            IdleTrafficTest.Check(CompactVelocity(false, new Vector3d(1, 0, 0), 1.1, default, 1).sqrMagnitude == 0 && CompactVelocity(true, default, 1, default, 2).sqrMagnitude == 0, "compact rest and old stamps cannot extrapolate");
            var body = new Ent();
            var hip = new Transform { position = new Vector3(10, 2, 3), rotation = Quaternion.Euler(0, 90, 0) }; hip.Add(new Rigidbody());
            var hand = new Transform { position = new Vector3(11, 2, 3) }; hand.Add(new Rigidbody());
            body.Root.ragdollPos = new TestLimbList { childs = new[] { body.Root.transform, hip, hand } };
            body.Root.transform.position = new Vector3(10000, 0, 0);
            w.Reset(); WriteRagdoll(w, body);
            IdleTrafficTest.Check(w.Len == 1 + 3 * 19 && !RagdollMoved(body), "complete final limb pose is the sent baseline");
            body.Root.transform.position = new Vector3(20000, 0, 0);
            IdleTrafficTest.Check(!RagdollMoved(body), "unused corpse root drift cannot cause limb traffic");
            var hipRotation = hip.rotation;
            hip.rotation = Quaternion.Euler(0, 90.2f, 0);
            hand.rotation = Quaternion.Euler(0, -.2f, 0);
            IdleTrafficTest.Check(!RagdollMoved(body), "opposing sub-threshold world rotations cannot amplify into visible limb motion");
            hip.rotation = hipRotation; hand.rotation = Quaternion.identity;
            hand.position += new Vector3(.005f, 0, 0); IdleTrafficTest.Check(!RagdollMoved(body), "limb jitter below a centimetre skips");
            hand.position += new Vector3(.01f, 0, 0); IdleTrafficTest.Check(RagdollMoved(body), "real limb movement sends");
            w.Reset(); WriteRagdoll(w, body);
            var copy = new Ent { Proxy = true };
            var copyHip = new Transform(); copyHip.Add(new Rigidbody()); var copyHand = new Transform(); copyHand.Add(new Rigidbody());
            copy.Root.ragdollPos = new TestLimbList { childs = new[] { copy.Root.transform, copyHip, copyHand } };
            IdleTrafficTest.Check(CargoBody(copy), "limp limbs without a root pickupable can be released as cargo");
            var live = new Ent(); live.Root.Add(new TestRagdoll { ragdoll = false }); live.Root.ragdollPos = copy.Root.ragdollPos;
            IdleTrafficTest.Check(!CargoBody(live), "a live creature without a pickupable is not loose cargo");
            copy.Root.transform.SetPositionAndRotation(hip.position, hip.rotation);
            var rr = new NetReader { Buf = w.Buf, End = w.Len }; ReadRagdoll(rr, copy); RagdollTick(1);
            IdleTrafficTest.Check(copy.Rag != null && copy.Rag.Has, "drifting unused root cannot reject remote corpse payload");
            IdleTrafficTest.Check((copyHip.position - hip.position).sqrMagnitude < 1e-8f && (copyHand.position - hand.position).sqrMagnitude < 1e-8f, "remote final limb positions match owner frame");
            IdleTrafficTest.Check(Quaternion.Angle(copyHip.rotation, hip.rotation) < .03f && Quaternion.Angle(copyHand.rotation, hand.rotation) < .03f, "remote final limb rotations match owner");
            copy.Held = true; RagdollTick(1);
            IdleTrafficTest.Check(!copyHip.GetComponent<Rigidbody>().detectCollisions && !copyHand.GetComponent<Rigidbody>().detectCollisions, "detached held limbs cannot shove the sled");
            copy.Held = false; copy.Stored = true; RagdollTick(1);
            IdleTrafficTest.Check(!copyHand.GetComponent<Rigidbody>().detectCollisions, "stored limbs stay collisionless");
            copy.Stored = false; RagdollTick(1);
            IdleTrafficTest.Check(copyHand.GetComponent<Rigidbody>().detectCollisions, "loose limbs collide again");
            IdleTrafficTest.Check(LooseRelease(7, 0, false, false) && !LooseRelease(7, 0, true, false) && !LooseRelease(7, 0, false, true) && !LooseRelease(7, 8, true, false), "only loose release in the driver's car can claim cargo");
            bool active = false, moving = false;
            IdleTrafficTest.Check(ReleaseMotion(ref active, true, false, false, ref moving) && moving, "release before physics is never sent as final rest");
            var releaseTail = new BandwidthRules.Tail();
            IdleTrafficTest.Check(BandwidthRules.StateQuiet(ref releaseTail, 20, false, false, false, false, active, moving, true, false, false, 10) == BandwidthRules.Quiet.None, "release bypasses a completed quiet tail");
            moving = true;
            IdleTrafficTest.Check(ReleaseMotion(ref active, false, false, false, ref moving), "falling release retains full motion even below pose thresholds");
            moving = false;
            IdleTrafficTest.Check(!ReleaseMotion(ref active, false, false, false, ref moving) && BandwidthRules.StateQuiet(ref releaseTail, 21, false, false, false, false, active, moving, false, false, false, .05) == BandwidthRules.Quiet.Final, "released body sends a reliable final when physics rests");
            // Production release path: control changes only on release/velocity transitions, not every awake tick.
            releaseTail = new BandwidthRules.Tail(); active = false; moving = false;
            ReleaseMotion(ref active, true, false, false, ref moving);
            BandwidthRules.StateQuiet(ref releaseTail, 0, true, false, false, false, true, moving, true, false, false, 10);
            int finals = 0;
            for (int tick = 1; tick <= 140; tick++)
            {
                moving = true; ReleaseMotion(ref active, false, false, false, ref moving);
                var quiet = BandwidthRules.StateQuiet(ref releaseTail, tick / 20.0, false, false, false, false, false,
                                                     moving, finals > 0, false, false, .05, resumedMotion: active);
                if (quiet == BandwidthRules.Quiet.Final) finals++;
                if (tick < 100) IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.None, "resumed motion keeps full rate between visible samples");
                if (tick > 100) IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.Pose, "released awake jitter stays silent after visible tail");
            }
            IdleTrafficTest.Check(finals == 1, "released awake jitter sends exactly one reliable final after five seconds");
            moving = false; ReleaseMotion(ref active, true, false, false, ref moving);
            IdleTrafficTest.Check(!ReleaseMotion(ref active, false, true, false, ref moving), "picking up again cancels released motion");
            TestFallingTrace();
            var counts = new long[10]; StatesSkippedPose = 1; StatesSkippedLocked = 2; StatesSkippedAttached = 3; StatesSkippedRabbitAnimation = 4; StatesSkippedRagdoll = 5; BandwidthSnapshot(counts);
            IdleTrafficTest.Check(counts[5] == 1 && counts[6] == 2 && counts[7] == 3 && counts[8] == 4 && counts[9] == 5, "all idle skip reasons reach telemetry");
        }

        static void TestFallingTrace()
        {
            // Vertical positions only: inventory-reentry mark, then pallet-bridge owner samples across handover.
            // No player/session metadata is needed to reproduce the send decision.
            var traces = new[] {
                new double[] { 45.81, 45.80, 45.79, 45.73, 45.70, 45.70, 45.71 },
                new double[] { 45.73, 45.73, 45.72, 45.70, 45.57, 45.53, 45.48, 45.44, 45.36,
                               45.27, 45.22, 45.10, 45.04, 44.97, 44.82, 44.80, 44.78, 44.75,
                               44.74, 44.73, 44.70, 44.67, 44.65, 44.60, 44.57, 44.54, 44.53,
                               44.51, 44.50, 44.48, 44.39, 44.32, 44.30, 44.28 }
            };
            foreach (var positions in traces)
            foreach (string resume in new[] { "release", "handover", "unlock" })
            {
                var tail = new BandwidthRules.Tail(); bool active = false, rest = true;
                double lastY = positions[0], sentAt = 0; int finals = 0, visible = 0;
                for (int tick = 0; tick < positions.Length + 1200; tick++)
                {
                    double now = 20 + tick / 20.0;
                    double y = positions[Math.Min(tick, positions.Length - 1)];
                    bool pose = BandwidthRules.PoseChanged((y - lastY) * (y - lastY), 0);
                    // Deliberately no solver velocity/awake flag: displacement alone must stream the fall.
                    bool moving = pose;
                    ReleaseMotion(ref active, tick == 0 && resume == "release", false, false, ref moving,
                                  first: tick == 0 && resume == "handover", unlocked: tick == 0 && resume == "unlock");
                    var quiet = BandwidthRules.StateQuiet(ref tail, now, tick == 0, pose, false, false, false,
                                                         moving, rest, false, false, now - sentAt, resumedMotion: active);
                    if (pose) { visible++; IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.None,
                        resume + " sends every visible fall sample despite zero solver velocity"); }
                    if (quiet == BandwidthRules.Quiet.Final) finals++;
                    if (quiet == BandwidthRules.Quiet.None || quiet == BandwidthRules.Quiet.Final)
                    { lastY = y; sentAt = now; rest = !moving || quiet == BandwidthRules.Quiet.Final; }
                    if (tick > positions.Length + 20) IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.Pose,
                        resume + " resting/sleeping object stays silent for 60 seconds after recorded fall");
                }
                // Rounded trace positions include stationary samples before the next visible fall segment.
                IdleTrafficTest.Check(visible >= 3 && finals >= 1 && rest && !tail.Active,
                    resume + " trace reaches reliable final rest without losing visible motion");
            }
        }

        static void TestSolverTraffic()
        {
            foreach (bool awake in new[] { false, true })
            {
                var e = new Ent(); var tail = new BandwidthRules.Tail();
                double last = 0; bool rest = false, sentSpeed = true; int keepalives = 0, finals = 0;
                BandwidthRules.StateQuiet(ref tail, 0, false, true, false, false, false, true, false, false, false, 0);
                for (int tick = 1; tick <= 1200; tick++)
                {
                    double now = tick / 20.0; bool speed = tick % 2 == 0;
                    bool moving = StateMoving(e, false, false, awake || speed, ref speed);
                    bool control = SampleControl(false, false, false, false, false, false, speed, sentSpeed);
                    var quiet = BandwidthRules.StateQuiet(ref tail, now, false, false, false, false, control,
                        moving, rest, false, false, now - last);
                    if (quiet == BandwidthRules.Quiet.None || quiet == BandwidthRules.Quiet.Final)
                    {
                        IdleTrafficTest.Check(now <= 5, "solver threshold crossings never send beyond the visual quiet tail");
                        if (quiet == BandwidthRules.Quiet.Final) finals++; else keepalives++;
                        rest = !moving || quiet == BandwidthRules.Quiet.Final; last = now; sentSpeed = speed;
                    }
                }
                IdleTrafficTest.Check(finals == 1 && keepalives <= 4 && rest && !tail.Active,
                    "item with unchanged visible pose sends at most four tail keepalives and one final despite speed chatter");
                IdleTrafficTest.Check(SampleControl(true, false, false, false, false, false, false, false) &&
                    SampleControl(false, false, true, false, false, false, false, false), "lock and held changes remain essential");
            }
        }

        static void TestIntermittentMotion()
        {
            var e = new Ent(); var tail = new BandwidthRules.Tail();
            Known[2] = new HashSet<uint> { 91 };
            bool rest = true; double sent = 0, farAt = double.NegativeInfinity; int poses = 0, finals = 0, far = 0;
            for (int tick = 0; tick < 600; tick++)
            {
                double now = tick / 20.0;
                bool visible = tick < 400 && tick % 2 == 0, speed = false;
                bool moving = StateMoving(e, visible, false, false, ref speed, ground: true);
                var quiet = BandwidthRules.StateQuiet(ref tail, now, tick == 0, visible, false, false,
                    SampleControl(false, false, false, false, false, false, speed, false), moving, rest, false, false, now - sent);
                if (quiet != BandwidthRules.Quiet.None && quiet != BandwidthRules.Quiet.Final) continue;
                bool final = FinalState(false, 0, true, quiet, moving, false);
                if (final) { finals++; IdleTrafficTest.Check(now >= 20.9, "intermittent rabbit/item motion does not force a far final between visible steps"); }
                else { poses++; IdleTrafficTest.Check(visible, "every non-final packet represents real visible motion"); }
                var packet = new NetWriter();
                CompactPoseCodec.Write(packet, 91, 1, (ushort)(poses + finals), now, tick, 0, 0, 0, !final, final);
                ReadEntityHead(new NetReader { Buf = packet.Buf, Pos = 1, End = packet.Len }, true,
                    out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                IdleTrafficTest.Check(ReadFinalState == final, "only the confirmed stop sets the compact final flag");
                if (PoseReceiverDue(2, 91, true, new Vector3d(9e6, 0, 0), default, tick == 0 || final, now, farAt))
                { far++; farAt = now; }
                rest = final; sent = now;
            }
            IdleTrafficTest.Check(poses == 200 && finals == 1 && far == 5, "all 200 visible steps send nearby; far receives four maintenance poses and one reliable stop");
            Known.Remove(2);
        }

        static void TestCorpseTraffic()
        {
            int legacy = 0, sent = 0, finals = 0, bytes = 0;
            var diagnostic = new Ent { NetId = 78 };
            var visible = new Transform { position = new Vector3(1, 2, 3), rotation = Quaternion.Euler(0, 20, 0) };
            diagnostic.Root.transform.position = new Vector3(999, 0, 0);
            diagnostic.Root.ragdollPos = new TestLimbList { childs = new[] { diagnostic.Root.transform, visible } };
            ByNet[78] = diagnostic;
            using (var status = System.Text.Json.JsonDocument.Parse(RagdollStatus(78)))
            {
                var pose = status.RootElement.GetProperty("restPose");
                IdleTrafficTest.Check(pose.GetArrayLength() == 1 && pose[0].GetArrayLength() == 7 && pose[0][0].GetDouble() == 1 && pose[0][4].GetDouble() != 0,
                    "rest diagnostics expose visible global limb position and quaternion while excluding the drifting root");
            }
            ByNet.Remove(78);
            // Nine remote rabbits and two locally owned corpses: dividing received by owned invents ~90 Hz/body.
            // A rabbit has fourteen physical limbs. No limb has its own entity or send clock in this fixture.
            for (int rabbit = 0; rabbit < 9; rabbit++)
            {
                var e = new Ent(); var ls = new Transform[14];
                for (int i = 0; i < ls.Length; i++) { ls[i] = new Transform { position = new Vector3(i * .05f, 0, 0) }; ls[i].Add(new Rigidbody()); }
                e.Root.ragdollPos = new TestLimbList { childs = ls };
                var w = new NetWriter(); WriteRagdoll(w, e);
                var oldRot = new Quaternion[ls.Length];
                for (int i = 0; i < ls.Length; i++) LocalPose(ls[0], ls[i], out _, out oldRot[i]);
                var tail = new BandwidthRules.Tail(); bool rest = false, previousVelocity = false; double last = 0;
                for (int tick = 1; tick <= 1200; tick++)
                {
                    float angle = tick % 2 == 0 ? 0 : .2f;
                    ls[0].rotation = Quaternion.Euler(0, angle, 0);
                    for (int i = 1; i < ls.Length; i++) ls[i].rotation = Quaternion.Euler(0, -angle, 0);
                    bool oldMoved = false;
                    for (int i = 0; i < ls.Length; i++)
                    {
                        LocalPose(ls[0], ls[i], out _, out var local);
                        if (Quaternion.Angle(local, oldRot[i]) > BandwidthRules.RotationDeg) oldMoved = true;
                    }
                    if (oldMoved)
                    {
                        if (tick <= 100) legacy++;
                        for (int i = 0; i < ls.Length; i++) LocalPose(ls[0], ls[i], out _, out oldRot[i]);
                    }
                    bool limbMoved = RagdollMoved(e), velocity = tick % 2 != 0;
                    bool moving = StateMoving(e, false, limbMoved, true, ref velocity);
                    var quiet = BandwidthRules.StateQuiet(ref tail, tick / 20.0, false, false, limbMoved, false,
                        velocity != previousVelocity, moving, rest, false, false, tick / 20.0 - last);
                    IdleTrafficTest.Check(!moving && !velocity, "solver velocity and opposing jitter are not visible corpse motion");
                    if (quiet == BandwidthRules.Quiet.None || quiet == BandwidthRules.Quiet.Final)
                    {
                        sent++; if (quiet == BandwidthRules.Quiet.Final) finals++;
                        w.Reset(); WriteStateHead(w, (uint)(rabbit + 1), 1, (ushort)sent, tick / 20.0, default,
                            ls[0].rotation, Vector3.zero, false, final: quiet == BandwidthRules.Quiet.Final);
                        w.U8(0); w.U8(0); w.U8(0); WriteRagdoll(w, e); bytes += w.Len;
                        var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                        ReadStateHead(r, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                        IdleTrafficTest.Check(ReadFinalState && w.Buf[r.Pos + 3] == 14 && w.Len == 305,
                            "one reliable final contains all fourteen limbs and the unchanged wire format");
                        rest = true; previousVelocity = velocity; last = tick / 20.0;
                    }
                    else IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.Pose, "settled corpse stays silent for sixty seconds");
                }
                for (int tick = 0; tick < 100; tick++)
                {
                    ls[13].position += new Vector3(.011f, 0, 0);
                    bool velocity = false, limbMoved = RagdollMoved(e);
                    bool moving = StateMoving(e, false, limbMoved, false, ref velocity);
                    IdleTrafficTest.Check(moving && BandwidthRules.StateQuiet(ref tail, 61 + tick / 20.0, false, false,
                        limbMoved, false, false, moving, rest, false, false, .05) == BandwidthRules.Quiet.None,
                        "real distal limb movement resumes the full twenty Hz without a rate cap");
                    w.Reset(); WriteRagdoll(w, e); rest = false;
                }
                bool stoppedSpeed = true;
                bool stopped = StateMoving(e, false, RagdollMoved(e), true, ref stoppedSpeed);
                IdleTrafficTest.Check(!stopped && BandwidthRules.StateQuiet(ref tail, 66, false, false, false, false,
                    false, stopped, rest, false, false, .05) == BandwidthRules.Quiet.Pose &&
                    BandwidthRules.StateQuiet(ref tail, 67, false, false, false, false, false, stopped, rest, false, false, 1.05) == BandwidthRules.Quiet.Final,
                    "visible limb motion ends with one final after a quiet interval even while physics remains awake");
                ls[13].rotation = Quaternion.Euler(0, .4f, 0);
                IdleTrafficTest.Check(RagdollMoved(e), "visible distal rotation also resumes sending");
                w.Reset(); WriteRagdoll(w, e);
                foreach (var l in ls) l.position += new Vector3(1000, 0, 0);
                TestMain.Origin = new Vector3d(-1000, 0, 0);
                IdleTrafficTest.Check(!RagdollMoved(e), "floating origin shift does not move globally settled limbs");
                foreach (var l in ls) l.position -= new Vector3(1000, 0, 0);
                TestMain.Origin = default;
                // Root/whole-body movement is also visible; a vehicle retains its physical motion policy.
                bool speed = true;
                IdleTrafficTest.Check(StateMoving(e, true, false, false, ref speed), "whole corpse movement sends");
                e.Root.car = new TestCar(); speed = true;
                IdleTrafficTest.Check(StateMoving(e, false, false, true, ref speed) && speed, "vehicle joints retain velocity transitions");
            }
            IdleTrafficTest.Check(legacy == 900 && legacy / 5.0 / 2 == 90, "old frame check reproduces apparent ~89 Hz per owned body");
            IdleTrafficTest.Check(sent == 9 && finals == 9 && bytes == 9 * 305, "nine complete finals then zero recurring corpse bytes");
            Console.WriteLine("Corpse regression: old 900 states/5s (~90 Hz if divided by 2 local owners); fixed 9 finals, 2745 bytes, then 0 B/s");
        }
    }
    public static class Protocol { public const int StateHz = 20; }
    public static class Mp { public static TestServer Server; }
    public class TestServer
    {
        public readonly Dictionary<int, Vector3d> Positions = new Dictionary<int, Vector3d>();
        public bool PeerPos(int id, out Vector3d pos) => Positions.TryGetValue(id, out pos);
        public void ReadyPeers(List<KeyValuePair<int, Vector3d>> into) { into.Clear(); foreach (var kv in Positions) into.Add(kv); }
        public void SendCreatureState(NetWriter w, int from, uint net, Vector3d where) { }
    }
    public static class Json { public static string Str(string s) => "\"" + s + "\""; }
}

// Only Unity/game surfaces are faked; decisions, wire codec, interpolation and limb application above are production code.
public class TestComponent
{
    public int MountScans;
    readonly Dictionary<Type, object> components = new Dictionary<Type, object>();
    public T GetComponent<T>() where T : class => components.TryGetValue(typeof(T), out var c) ? c as T : null;
    public T GetComponentInChildren<T>(bool inactive = false) where T : class => GetComponent<T>();
    public T GetComponentInParent<T>() where T : class => GetComponent<T>();
    public T[] GetComponentsInChildren<T>(bool inactive) where T : class { MountScans++; return GetComponent<T[]>() ?? Array.Empty<T>(); }
    public void Add<T>(T c) => components[typeof(T)] = c;
}
public class TestCar { public Transform transform = new Transform(); public float speed, lspeed; public Vector3 LV; }
public class TestFlyer { }
public class TestRabbit { public TestOldAi AI; public float runValue, targetForgetTime = 2, lastSee; public bool attacking, roaring; public GameObject playerDeathTrigger; public TestMove move; }
public class TestMove { public bool modifier; public Rigidbody RB; }
public class TestAi { public bool died; public void Die(bool _playSound) { died = true; } }
public class TestOldAi { public bool fleeing, goToTarget; public TestMove move; public Vector3 interestPos, lastVelocity; public bool alive; public void Die(bool _playSound) { alive = false; } }
public class TestRagdoll { public bool ragdoll; public Rigidbody[] RBs; public void SetRagdol(bool v) { ragdoll = v; } }
public class TestPickup
{
    public float FrayRange = 3, maxPickupDist = 1, dropDist = 1.25f;
    public bool pickedUp, inRightHand; public string name; public GameObject gameObject = new GameObject(); public Transform transform = new Transform();
    public TestMount physlock; public void Pickup() { } public void UnPhysicsLock() { physlock = null; transform.parent = null; }
}
public class TestLayer { public TestPickup p; }
public class TestMount
{
    public TestItem parentTosave; public Transform transform = new Transform();
    public bool noPhysicsLock, onlyManual, CanHold = true; public object col = new object();
    public TestHit[] cols = Array.Empty<TestHit>(); public bool CanStore() => CanHold; public void Scan() { }
    public void Store(TestPickup p, bool ignoreBody) { p.physlock = this; p.transform.parent = transform; }
}
public class TestAttach { public bool attached, isPart; public object slot; public Transform point; public GameObject gameObject = new GameObject(); }
public class TestLimbList { public Transform[] childs; public TestPickup[] ps; public bool PickedUp() => ps != null && Array.Exists(ps, p => p.pickedUp); }
public class TestItem : TestComponent
{
    public string name; public TestCar car; public TestPickup P; public object npc; public TestRabbit nyul; public TestLimbList ragdollPos; public TestAttach attachable;
    public Transform transform = new Transform();
    public GameObject gameObject = new GameObject(); public Rigidbody RB; public bool buried;
    public void DistUnFreezeRB() { RB.isKinematic = false; }
}
public class TestPlayer : TestPickup { public new TestPickup pickedUp; public int Drops; public void Drop() { Drops++; if (pickedUp != null) pickedUp.pickedUp = false; pickedUp = null; } }
public class TestMain { public static Vector3 UnityPosFromGlobal(Vector3d p) => (Vector3)(p - Origin); public static TestMain s; public TestPlayer player; public static Vector3d Origin; public static Vector3d GlobalFromUnityPos(Vector3 p) => (Vector3d)p + Origin; }
public class TestDatabase { public static TestDatabase s; public GameObject[] items; }
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public double sqrMagnitude => x * x + y * y + z * z;
    public double magnitude => Math.Sqrt(sqrMagnitude);
    public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3d operator *(Vector3d a, double n) => new Vector3d(a.x * n, a.y * n, a.z * n);
    public static implicit operator Vector3d(Vector3 p) => new Vector3d(p.x, p.y, p.z);
    public static explicit operator Vector3(Vector3d p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
}
namespace UnityEngine
{
    public class GameObject : TestComponent { public string name; public void SetActive(bool active) { } }
    public class Rigidbody { public bool isKinematic; public bool detectCollisions = true; public Vector3 velocity, angularVelocity; public Vector3 GetPointVelocity(Vector3 p) => velocity; }
    public class Transform : TestComponent
    {
        public Transform parent;
        public Vector3 position; public Quaternion rotation = Quaternion.identity; public GameObject gameObject = new GameObject();
        public Transform root => parent != null ? parent.root : this; public Vector3 forward => rotation * new Vector3(0, 0, 1);
        public Vector3 TransformPoint(Vector3 local) => position + rotation * local;
        public Vector3 InverseTransformPoint(Vector3 world) => Quaternion.Inverse(rotation) * (world - position);
        public void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => this / magnitude;
        public static Vector3 Project(Vector3 v, Vector3 onto) => onto * ((v.x * onto.x + v.y * onto.y + v.z * onto.z) / onto.sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float n) => new Vector3(a.x * n, a.y * n, a.z * n);
        public static Vector3 operator /(Vector3 a, float n) => a * (1 / n);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float n) => a + (b - a) * n;
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        static System.Numerics.Quaternion N(Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        static Quaternion U(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public static Quaternion Euler(float x, float y, float z) => U(System.Numerics.Quaternion.CreateFromYawPitchRoll(y * MathF.PI / 180, x * MathF.PI / 180, z * MathF.PI / 180));
        public static Quaternion Inverse(Quaternion q) => U(System.Numerics.Quaternion.Inverse(N(q)));
        public static Quaternion operator *(Quaternion a, Quaternion b) => U(N(a) * N(b));
        public static Vector3 operator *(Quaternion a, Vector3 b) { var v = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(b.x, b.y, b.z), N(a)); return new Vector3(v.X, v.Y, v.Z); }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => U(System.Numerics.Quaternion.Slerp(N(a), N(b), t));
        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Math.Min(1, Math.Abs(System.Numerics.Quaternion.Dot(N(a), N(b))));
            return dot > 1 - 1e-6f ? 0 : (float)(Math.Acos(dot) * 360 / Math.PI);
        }
    }
    public static class Mathf
    {
        public static float Abs(float v) => Math.Abs(v);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Exp(float v) => MathF.Exp(v);
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static int RoundToInt(float v) => (int)MathF.Round(v);
        public static float MoveTowards(float a, float b, float d) => a + Math.Clamp(b - a, -d, d);
    }
}
