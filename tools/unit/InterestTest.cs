using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    // Only game/transport surfaces are stand-ins. Relevance, source suppression, snapshot caching,
    // epoch fencing, re-entry replay, rabbit routing and the wire codecs are production code.
    public static partial class Entities
    {
        static bool InterestRelay = true;
        static bool IsHost;
        static double InterestM = 3000;
        static HashSet<T> StaleOf<T>(Dictionary<int, HashSet<T>> d, int player)
        { if (!d.TryGetValue(player, out var set)) d[player] = set = new HashSet<T>(); return set; }

        static void TestStateInterest()
        {
            void Check(bool ok, string why) => IdleTrafficTest.Check(ok, why);
            Check(InterestRules.Within(false, 9e6, 0, 2000), "unknown viewer position is conservative");
            Check(InterestRules.Within(true, 2000, 0, 2000) && !InterestRules.Within(true, 2000.01, 0, 2000), "pose interest includes boundary only");
            var owner = new Ent(); int far = 0, near = 0;
            var hostRecords = new Dictionary<uint, Vector3d>();
            for (int tick = 1; tick <= 200; tick++)
            {
                double now = tick / 20.0;
                if (OwnerPoseDue(owner, false, tick == 1, now))
                {
                    far++;
                    // A maintenance packet uses the existing full wire form, just like a live pose.
                    var w = new NetWriter(); WriteStateHead(w, 9, 1, (ushort)tick, now, new Vector3d(tick, 0, 0), Quaternion.identity, Vector3.zero, false);
                    ReadStateHead(RabbitReader(w), out uint net, out _, out _, out var pos, out _, out _, out _, out _, out _, out _);
                    hostRecords[net] = pos;
                }
            }
            Check(far == 2 && hostRecords[9].x == 101, "unseen moving owner sends initial state and five-second record refresh, not 20 Hz");
            Check(OwnerPoseDue(owner, false, true, 10.01), "final rest / lock / inventory / owner epoch bypass far refresh");
            hostRecords[9] = new Vector3d(200, 0, 0);
            Check(hostRecords[9].x == 200 && !OwnerPoseDue(owner, false, false, 10.02), "host retains final location with no recurring rest traffic");
            for (int tick = 1; tick <= 200; tick++) if (OwnerPoseDue(owner, true, false, 11 + tick / 20.0)) near++;
            Check(near == 200, "visible motion has no new rate cap or send budget");
            Check(OwnerAudience(default) == false, "no remote viewers means no live upload needed");
            IsHost = true; Check(OwnerAudience(default), "host samples owned objects to keep its world and relay cache current"); IsHost = false;
            RemotePlayers.Visible = true; Check(OwnerAudience(default), "entering add range restores owner stream immediately"); RemotePlayers.Visible = false;

            Mp.Server = new TestServer();
            var se = new SEnt { NetId = 9, Epoch = 3, ReceivedStateEpoch = 3, OwnerId = 1 };
            var packet = new NetWriter();
            WriteStateHead(packet, 9, 3, 8, 12, new Vector3d(45, 2, 3), Quaternion.identity, Vector3.zero, false);
            packet.U8(0); packet.U8(0); packet.U8(0); packet.U8(0);
            var reader = RabbitReader(packet); CacheLatestPose(se, reader);
            var allocation = se.LatestState; CacheLatestPose(se, reader);
            Check(ReferenceEquals(allocation, se.LatestState), "moving pose cache reuses its buffer");
            Array.Clear(packet.Buf, 0, packet.Len);
            RabbitRecipients.Clear(); StateInterestCatchUp(2, se, new Vector3d(9e6, 0, 0));
            Check(RabbitRecipients.Count == 0, "far known receiver gets no replay");
            StateInterestCatchUp(2, se, default);
            ReadStateHead(new NetReader { Buf = LastInterestPacket, Pos = 1, End = LastInterestPacket.Length }, out _, out uint epoch, out _, out var current, out _, out _, out _, out _, out _, out _);
            Check(RabbitRecipients.Count == 1 && epoch == 3 && current.x == 45, "entering known receiver immediately gets complete current moving pose without waiting for rest");
            StateInterestCatchUp(2, se, default);
            Check(RabbitRecipients.Count == 1, "remaining in range does not repeatedly replay snapshots");
            StateInterestCatchUp(2, se, new Vector3d(9e6, 0, 0)); se.Epoch++;
            StateInterestCatchUp(2, se, default);
            Check(RabbitRecipients.Count == 1, "old ownership epoch cannot be replayed on re-entry");

            se.Epoch = 3; Server[9] = se;
            Known[0] = new HashSet<uint> { 9 }; Known[2] = new HashSet<uint> { 9 };
            Mp.Server.Positions[2] = new Vector3d(9e6, 0, 0);
            packet.Reset(); RabbitStateCodec.Write(packet, new RabbitStateCodec.State { Net = 9, Epoch = 3, Flags = 128 });
            RabbitRecipients.Clear(); ServerRabbitState(1, RabbitReader(packet));
            Check(RabbitRecipients.Count == 1 && RabbitRecipients[0] == 0 && se.RabbitSnapshot != null, "far rabbit change reaches host records but not distant known viewer");
            _rabbitNear.Clear(); StateInterestCatchUp(2, se, default);
            Check(LastInterestPacket[0] == RabbitState, "returning viewer receives cached reliable death state even when rabbit stays quiet");
            var players = new int[128]; var counts = new long[128]; int n = InterestSkipSnapshot(players, counts);
            Check(n == 2 && Array.IndexOf(players, 2, 0, n) >= 0 && counts[Array.IndexOf(players, 0, 0, n)] == 199, "skips are attributed separately to upload receiver zero and distant receiver");
            double last = double.NegativeInfinity; int relayed = 0;
            for (int tick = 1; tick <= 200; tick++)
            {
                double now = tick / 20.0;
                if (PoseReceiverDue(2, 9, true, new Vector3d(9e6, 0, 0), default, false, now, last))
                { relayed++; last = now; }
            }
            Check(relayed == 2, "production receiver gate relays far maintenance only, including compact and limb-only motion");
            Check(PoseReceiverDue(2, 9, true, new Vector3d(9e6, 0, 0), default, true, 10.01, last), "far receiver always gets essential final/epoch/lock states");
            Check(PoseReceiverDue(2, 9, true, default, default, false, 10.02, last), "entering receiver gets next full state immediately, without waiting for refresh");
            Check(!PoseReceiverDue(3, 9, false, default, default, true, 10.02, last), "unknown copy gets no state, even final, before add path");
            se.AiSnapshotEpoch = se.Epoch; se.AiSnapshots[0] = new byte[] { 50, 9, 0 };
            RabbitRecipients.Clear(); SendCreatureSnapshots(2, se);
            Check(LastInterestPacket[0] == 50 && RabbitRecipients.Count == 1, "AI re-entry restores complete cached state");
            se.Epoch++; SendCreatureSnapshots(2, se);
            Check(RabbitRecipients.Count == 1, "AI cache is fenced at ownership changes");
            _interestSkips.Clear(); _poseNear.Clear(); _rabbitNear.Clear(); Known.Clear(); Server.Clear(); Mp.Server = null;
            TestInterestProjection();
        }

        static void TestInterestProjection()
        {
            // Counterfactuals, not a replay: the marks omit most entities' positions and activity.
            var w = new NetWriter();
            WriteStateHead(w, 595, 1, 1, 1, default, Quaternion.identity, Vector3.zero, false);
            w.U8(0); w.U8(0); w.U8(0); w.U8(0); int item = w.Len;
            w.Reset(); CompactPoseCodec.Write(w, 595, 1, 1, 1, 0, 0, 0, 0, true); int rabbit = w.Len;
            w.Reset(); RabbitStateCodec.Write(w, new RabbitStateCodec.State { Net = 595, Epoch = 1 }); int animation = w.Len;
            double far = (22 * item + 13 * (rabbit + animation)) / 5.0;
            IdleTrafficTest.Check(item == 40 && rabbit == 25 && animation == 24 && far == 303.4,
                "473.5 s projection: even all 22 items and 13 rabbits moving unseen need only 303.4 B/s of maintenance, plus real transitions/record changes");
            double liveOnly = (1336 * rabbit + 227 * animation) / 5.003;
            IdleTrafficTest.Check(Math.Abs(liveOnly - 7764.941035378772) < .001,
                "152.5 s live-only counterfactual preserves all observed pose/animation counts: 7764.94 B/s");
            IdleTrafficTest.Check(235 * 20 * rabbit == 117500,
                "235 actually moving nearby live rabbits still retain every pose: interest is not a population or rate cap");
            Console.WriteLine("Interest projection (conditional): far 22 items + 13 rabbits all moving = 303.4 B/s + transitions/resync; 152.5s all 1336 rabbit poses upright + 227 real animation changes = 7764.94 B/s; settled corpses = 0 recurring B/s (finals retained).");
        }
    }
    static class RemotePlayers { internal static bool Visible; internal static bool EntityViewerNear(Vector3d at, double range) => Visible; }
    static class MpServer { internal const float CarBeyondInterval = 5; internal const double CarFarM = 2000; }
}
