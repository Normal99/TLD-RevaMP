using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte RabbitState = 90;
        static long RabbitStatesStale, RabbitStatesApplied;
        static readonly List<int> RabbitRecipients = new List<int>();
        static nyulscript NyOf(Ent e) => e.Root?.nyul;
        static void ServerSendTo(int player, NetWriter w, bool reliable)
        {
            IdleTrafficTest.Check(reliable, "rabbit changes and cached snapshots use reliable transport");
            RabbitRecipients.Add(player);
        }
        static NetReader RabbitReader(NetWriter w) => new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };

        static void TestLiveRabbitTraffic()
        {
            var rabbit = new Ent(); rabbit.Root.nyul = new TestRabbit { AI = new TestOldAi { alive = true } };
            rabbit.Root.Add(new TestRagdoll());
            rabbit.Root.ragdollPos = new TestLimbList { childs = new[] { new Transform() } };
            IdleTrafficTest.Check(CompactGround(rabbit), "live rabbit with inactive ragdoll uses compact ground pose");
            rabbit.Root.P = new TestPickup();
            IdleTrafficTest.Check(CompactGround(rabbit), "pickupable live rabbit still uses compact ground pose when loose");
            rabbit.Root.P.physlock = new TestMount();
            IdleTrafficTest.Check(!CompactGround(rabbit), "physlocked live rabbit keeps full pose with lock and parent reference");
            rabbit.Root.P.physlock = null;
            IdleTrafficTest.Check(CompactGround(rabbit), "unlocked live rabbit resumes compact ground pose");
            rabbit.Root.GetComponent<TestRagdoll>().ragdoll = true;
            IdleTrafficTest.Check(!CompactGround(rabbit), "activated ragdoll retains complete limb poses");
            rabbit.Root.GetComponent<TestRagdoll>().ragdoll = false; rabbit.Root.nyul.AI.alive = false;
            IdleTrafficTest.Check(!CompactGround(rabbit), "dead rabbit cannot enter compact path before ragdoll activation");
            rabbit.Root.nyul.AI.alive = true; rabbit.Root.npc = new object();
            IdleTrafficTest.Check(!CompactGround(rabbit), "NPCs keep their full pose path"); rabbit.Root.npc = null;
            rabbit.PartIndex = 0; IdleTrafficTest.Check(!CompactGround(rabbit), "detached parts keep their full pose path"); rabbit.PartIndex = -1;

            // The mark's category contains 647 messages: 568 full poses plus 79 RabbitStates = 24,300 bytes.
            // Keep every message (including all 79 animation changes) for a conservative per-update comparison.
            var w = new NetWriter();
            WriteStateHead(w, 595, 1, 1, 1, default, Quaternion.identity, Vector3.zero, false);
            w.U8(0); w.U8(0); w.U8(0); WriteRagdoll(w, rabbit);
            int before = w.Len;
            w.Reset(); CompactPoseCodec.Write(w, 595, 1, 1, 1, 0, 0, 0, 0, true); int after = w.Len;
            IdleTrafficTest.Check(before == 40 && after == 25, "live pose shrinks 40 to 25 bytes for two-byte net id");
            IdleTrafficTest.Check(647 * before / 30.0 == 862.6666666666666 && Math.Abs(647 * after / 30.0 - 539.1666666666666) < 1e-9,
                "647 poses / five seconds / six rabbits: 862.67 to 539.17 bytes per rabbit-second, same cadence");
            w.Reset(); RabbitStateCodec.Write(w, new RabbitStateCodec.State { Net = 595, Epoch = 3, Flags = 4, Run = 90, Seen = true,
                X = -12345.678, Y = 1000.014, Z = 12000.005 });
            int animationBytes = w.Len;
            IdleTrafficTest.Check(animationBytes == 24 && 568 * before + 79 * 20 == 24300 && 568 * after + 79 * animationBytes == 16096,
                "actual mark category: 810 to 536.53 B/rabbit-second even if all 79 animation packets are real changes");
            Console.WriteLine("Live rabbit bytes: pose 40 -> 25; 647 poses/5s/6 = 862.67 -> 539.17 B/rabbit-s. Actual category (568 poses + 79 animations): 24300 -> 16096 bytes/5s/6 = 810 -> 536.53 B/rabbit-s (same message counts).");
            var decoded = RabbitStateCodec.Read(RabbitReader(w));
            IdleTrafficTest.Check(decoded.Net == 595 && decoded.Epoch == 3 && decoded.Flags == 4 && decoded.Run == 90 && decoded.Seen &&
                Math.Abs(decoded.X + 12345.678) <= .005 && Math.Abs(decoded.Y - 1000.014) <= .005, "epoch and global target round trip at centimetre precision");
            for (int n = 1; n < w.Len; n++)
            {
                var truncated = new NetReader { Buf = w.Buf, Pos = 1, End = n }; RabbitStateCodec.Read(truncated);
                IdleTrafficTest.Check(truncated.Bad, "truncated rabbit change rejected");
            }
            IdleTrafficTest.Check(BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, true, 0), "target visibility change sends immediately");
            IdleTrafficTest.Check(BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, false, .25), "real target change sends immediately");
            IdleTrafficTest.Check(!BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, false, .249), "target jitter below existing half-metre threshold skips");
            var target = new Vector3d(50, 2, 30);
            var relativeBefore = target - new Vector3d(0, 2, 0);
            var relativeAfter = target - new Vector3d(10, 2, 0);
            IdleTrafficTest.Check((relativeAfter - relativeBefore).sqrMagnitude == 100 && !BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, false, (target - target).sqrMagnitude),
                "walking towards fixed global target does not create animation/target updates");

            var se = new SEnt { NetId = 595, Epoch = 3, OwnerId = 1 }; Server[595] = se;
            Known[0] = new HashSet<uint> { 595 }; Known[1] = new HashSet<uint> { 595 }; Known[2] = new HashSet<uint> { 595 }; Known[3] = new HashSet<uint>();
            RabbitRecipients.Clear(); ServerRabbitState(1, RabbitReader(w));
            IdleTrafficTest.Check(RabbitRecipients.Count == 2 && RabbitRecipients.Contains(0) && RabbitRecipients.Contains(2) && se.RabbitSnapshot != null,
                "reliable change reaches host and all known copies; owner and unknown viewers excluded");
            RabbitRecipients.Clear(); SendRabbitSnapshot(3, se);
            IdleTrafficTest.Check(RabbitRecipients.Count == 1 && RabbitRecipients[0] == 3, "late joiner gets cached animation/target without a keepalive");
            long stale = RabbitStatesStale; ServerRabbitState(2, RabbitReader(w)); se.Epoch++;
            ServerRabbitState(1, RabbitReader(w)); SendRabbitSnapshot(3, se);
            IdleTrafficTest.Check(RabbitStatesStale == stale + 2 && RabbitRecipients.Count == 1, "wrong owner and old epoch cannot replace or replay snapshot");
            var copy = new Ent { Proxy = true, Epoch = 3, Root = null }; ByNet[595] = copy;
            ApplyRabbitState(RabbitReader(w));
            IdleTrafficTest.Check(copy.RabbitSnapshot != null, "streamed-out copy caches rabbit changes without resolving a GameObject");
            copy.Root = new TestItem { nyul = new TestRabbit() }; RestoreRabbitState(copy);
            IdleTrafficTest.Check(RabViews[copy.Root.nyul].Has && RabViews[copy.Root.nyul].Run == .45f && RabSeen[copy.Root.nyul], "streamed-in rabbit restores cached animation and seen state");
            IdleTrafficTest.Check(Math.Abs(RabViews[copy.Root.nyul].Interest.x - decoded.X) < 1e-9, "cached target remains global while rabbit and floating origin move");
            var cached = copy.RabbitSnapshot; copy.Epoch++;
            ApplyRabbitState(RabbitReader(w)); IdleTrafficTest.Check(ReferenceEquals(cached, copy.RabbitSnapshot), "client rejects old owner epoch");
            ByNet.Clear(); Server.Clear(); Known.Clear();

            foreach (string idle in new[] { "standing", "eating", "sitting" })
            {
                var tail = new BandwidthRules.Tail(); bool rest = false; int finals = 0;
                for (int tick = 1; tick <= 1200; tick++)
                {
                    // Solver awake/speed jitter does not turn in-place animation into world motion.
                    bool velocityMoving = tick % 2 == 0;
                    bool moving = StateMoving(rabbit, false, false, true, ref velocityMoving, CompactGround(rabbit));
                    var quiet = BandwidthRules.StateQuiet(ref tail, tick / 20.0, false, false, false, false, velocityMoving,
                        moving, rest, false, false, tick / 20.0);
                    if (quiet == BandwidthRules.Quiet.Final) { finals++; rest = true; }
                    else IdleTrafficTest.Check(quiet == BandwidthRules.Quiet.Pose, idle + " sends no recurring pose bytes for 60 seconds");
                    IdleTrafficTest.Check(!BandwidthRules.RabbitDue(false, 0, 0, 0, 0, false, false, 0), idle + " sends no recurring animation bytes");
                }
                IdleTrafficTest.Check(finals == 1, idle + " sends one final and then 0 B/rabbit-second");
            }
            for (int tick = 0; tick < 100; tick++)
            {
                bool velocityMoving = true;
                IdleTrafficTest.Check(StateMoving(rabbit, true, false, true, ref velocityMoving, true), "real ground motion retains every due update without a rate cap");
            }
            var hopVelocity = CompactVelocity(true, new Vector3d(.5, .2, 0), 1.05, default, 1);
            IdleTrafficTest.Check(Math.Abs(hopVelocity.y - 4) < .001 && Math.Abs(hopVelocity.x - 10) < .001, "derived velocity retains hop height and horizontal motion");
            var ip = new PoseInterpolator();
            ip.Add(new PoseInterpolator.Sample { T = 1, Pos = default, Rot = Quaternion.identity }, 1.1);
            ip.Add(new PoseInterpolator.Sample { T = 1.05, Pos = new Vector3d(.5, .2, 0), Rot = Quaternion.identity, Vel = hopVelocity }, 1.15);
            ip.Evaluate(1.19, .01f, out var shown, out _);
            IdleTrafficTest.Check(shown.y > 0 && shown.y < .2 && shown.x > 0 && shown.x < .5, "receiver interpolates compact ground hop in all three dimensions");
            var stop = new Vector3d(.5, 0, 0);
            ip.Add(new PoseInterpolator.Sample { T = 1.1, Pos = stop, Rot = Quaternion.identity, Vel = CompactVelocity(false, stop, 1.1, default, 1.05) }, 1.2);
            for (int tick = 0; tick < 150; tick++) ip.Evaluate(1.2 + tick / 60.0, 1f / 60, out shown, out _);
            IdleTrafficTest.Check((shown - stop).sqrMagnitude < 1e-8, "final compact pose has zero velocity and no idle drift");
        }
    }
}
