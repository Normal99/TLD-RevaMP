using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed partial class SEnt
        {
            internal byte[] RagdollKeyState;
            internal uint RagdollKeyEpoch;
        }

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

        public static long ServerLateKeys;
        /// A Key that came after a newer state: cached when it is newer than the cached Key (anchor ids count up).
        private static bool ServerLateKey(SEnt se, NetReader r)
        {
            if (!StateTail.TryRead(r.Buf, 0, r.End, out var tail) || tail.Mode != StateTail.Mode.Key || tail.Epoch != se.Epoch) return false;
            if (se.RagdollKeyState != null && se.RagdollKeyEpoch == se.Epoch && StateTail.TryRead(se.RagdollKeyState, out var held) &&
                held.Mode == StateTail.Mode.Key && (sbyte)(tail.Anchor - held.Anchor) <= 0) return false;
            if (se.RagdollKeyState == null || se.RagdollKeyState.Length != r.End) se.RagdollKeyState = new byte[r.End];
            System.Buffer.BlockCopy(r.Buf, 0, se.RagdollKeyState, 0, r.End);
            se.RagdollKeyEpoch = se.Epoch;
            ServerLateKeys++;
            return true;
        }

        private static void CacheLatestPose(SEnt se, NetReader r)
        {
            if (se.RagdollKeyEpoch != se.Epoch) { se.RagdollKeyState = null; se.RagdollKeyEpoch = 0; }
            if (StateTail.TryRead(r.Buf, 0, r.End, out var tail) && tail.Mode == StateTail.Mode.Key && tail.Epoch == se.Epoch)
            {
                if (se.RagdollKeyState == null || se.RagdollKeyState.Length != r.End) se.RagdollKeyState = new byte[r.End];
                System.Buffer.BlockCopy(r.Buf, 0, se.RagdollKeyState, 0, r.End);
                se.RagdollKeyEpoch = se.Epoch;
            }
            if (se.LatestState == null || se.LatestState.Length != r.End) se.LatestState = new byte[r.End];
            System.Buffer.BlockCopy(r.Buf, 0, se.LatestState, 0, r.End);
        }

        public static long ReplaysWithoutKey;   // join / interest replays without a usable ragdoll Key (limbs cut, or the tail unsized)

        private static void SendSettledState(int playerId, SEnt se)
        {
            var state = se.LatestState ?? se.SettledState;
            if (state == null || se.ReceivedStateEpoch != se.Epoch) return;
            if (se.RagdollKeyEpoch != se.Epoch) { se.RagdollKeyState = null; se.RagdollKeyEpoch = 0; }
            if (!StateTail.ReplayKey(state, se.RagdollKeyState, se.Epoch, out var key))
            {
                // the pose still goes (a join or interest entry would otherwise see the object at its record until it moves):
                // a Delta without its Key without its limbs - a copy keeps limbs it has (ReadRagdoll), where a "none" block
                // would let a limp body go - and a tail StateTail can't size as it is, as before ragdoll Keys
                if (StateTail.TryRead(state, out var tail) && tail.Mode == StateTail.Mode.Delta) { var cut = new byte[tail.Offset]; System.Buffer.BlockCopy(state, 0, cut, 0, cut.Length); state = cut; }
                if (ReplaysWithoutKey++ < 3) Plugin.Log.LogWarning($"[anomaly] replay without its ragdoll Key: net {se.NetId}, {state.Length} bytes sent");
            }
            ServerLockedPose(se);
            if (key != null) SendPoseReplay(playerId, se, key);
            SendPoseReplay(playerId, se, state);
        }

        private static void SendPoseReplay(int playerId, SEnt se, byte[] state)
        {
            if (se.LockFrame != 0) ReplayLockedState(WS, state, se.Pos, se.Rot);
            else { WS.Reset(); WS.Bytes(state, 0, state.Length); }
            ServerSendTo(playerId, WS, true);
        }
    }
}
