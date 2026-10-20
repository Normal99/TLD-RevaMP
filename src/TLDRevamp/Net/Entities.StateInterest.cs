using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static readonly Dictionary<int, long> _interestSkips = new Dictionary<int, long>();
        private static readonly Dictionary<int, HashSet<uint>> _poseNear = new Dictionary<int, HashSet<uint>>();
        private static readonly Dictionary<int, HashSet<uint>> _rabbitNear = new Dictionary<int, HashSet<uint>>();

        internal static void SkippedByInterest(int receiver)
        {
            _interestSkips.TryGetValue(receiver, out long count);
            _interestSkips[receiver] = count + 1;
        }

        internal static int InterestSkipSnapshot(int[] players, long[] counts)
        {
            int n = 0;
            foreach (var kv in _interestSkips)
            {
                if (n == players.Length) break;
                players[n] = kv.Key; counts[n++] = kv.Value;
            }
            return n;
        }

        private static bool OwnerAudience(Vector3d at) =>
            !InterestRelay || IsHost || RemotePlayers.EntityViewerNear(at, InterestM);

        // Called after sampling, so suppressed poses still advance motion/rest detection. Finals therefore
        // describe the actual stop, rather than waiting for the next far-store refresh to notice it.
        private static bool OwnerPoseDue(Ent e, bool audience, bool essential, double now)
        {
            if (InterestRules.Due(audience, essential, now, e.InterestSentAt, MpServer.CarBeyondInterval))
            { e.InterestSentAt = now; return true; }
            SkippedByInterest(0); return false;
        }

        private static bool RabbitReceiverNear(int player, SEnt se)
        {
            if (player == 0 || !InterestRelay || Mp.Server == null) return true;
            bool has = Mp.Server.PeerPos(player, out var pos);
            if (InterestRules.Within(has, pos.x - se.Where.x, pos.z - se.Where.z, StateInterestM)) return true;
            SkippedByInterest(player); return false;
        }

        internal static bool PoseReceiverDue(int player, uint net, bool hasPosition, Vector3d viewer, Vector3d at,
                                             bool essential, double now, double sentAt)
        {
            if (WasteFixes && !StateKnown(player, net))
            { StatesSkippedUnknown++; SkippedByInterest(player); return false; }
            bool near = InterestRules.Within(hasPosition, viewer.x - at.x, viewer.z - at.z, MpServer.CarFarM);
            // Known far copies still need maintenance and transitions to keep their saved records current.
            if (!InterestRelay || InterestRules.Due(near, essential, now, sentAt, MpServer.CarBeyondInterval)) return true;
            SkippedByInterest(player); return false;
        }

        // Known is lifetime knowledge (including stored copies), not current relevance. On re-entry replay
        // the complete most recent pose, including limbs, and the reliable rabbit snapshot even if at rest.
        private static void StateInterestCatchUp(int player, SEnt se, Vector3d viewer)
        {
            if (player == se.OwnerId) return;
            var poses = StaleOf(_poseNear, player);
            bool pose = InterestRules.Within(true, viewer.x - se.Where.x, viewer.z - se.Where.z, MpServer.CarFarM);
            if (!pose) poses.Remove(se.NetId);
            else if (poses.Add(se.NetId)) SendSettledState(player, se);
            var rabbits = StaleOf(_rabbitNear, player);
            bool rabbit = InterestRules.Within(true, viewer.x - se.Where.x, viewer.z - se.Where.z, StateInterestM);
            if (!rabbit) rabbits.Remove(se.NetId);
            else if (rabbits.Add(se.NetId)) { SendRabbitSnapshot(player, se); SendCreatureSnapshots(player, se); }
        }

        private static void SendCreatureSnapshots(int player, SEnt se)
        {
            if (se.AiSnapshotEpoch != se.Epoch) return;
            foreach (var bytes in se.AiSnapshots.Values)
            { WS.Reset(); WS.Bytes(bytes, 0, bytes.Length); ServerSendTo(player, WS, true); }
        }

        private static void CacheLatestPose(SEnt se, NetReader r)
        {
            if (se.LatestState == null || se.LatestState.Length != r.End) se.LatestState = new byte[r.End];
            System.Buffer.BlockCopy(r.Buf, 0, se.LatestState, 0, r.End);
        }

        private static void SendSettledState(int playerId, SEnt se)
        {
            var state = se.LatestState ?? se.SettledState;
            if (state == null || se.ReceivedStateEpoch != se.Epoch) return;
            ServerLockedPose(se);
            if (se.LockFrame != 0) ReplayLockedState(WS, state, se.Pos, se.Rot);
            else { WS.Reset(); WS.Bytes(state, 0, state.Length); }
            ServerSendTo(playerId, WS, true);
        }
    }
}
