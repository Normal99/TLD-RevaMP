using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Objects are simulated where players are. An object is simulated by its owner's machine only while that machine has
    /// it loaded — around its own player. When the owner travels away (or is the dedicated server's phantom, which never
    /// moves), the object goes dormant there, and every other player's copy is a kinematic stand-in that nothing drives:
    /// a creature in a building stood frozen while another player walked through it, a car or a can stayed unpushable
    /// until someone happened to claim it. The game's own MP has no such case (one host simulates everything around it).
    /// So the server hands an object to the nearest player once its owner is far away (beyond HandFarM — further than the
    /// game keeps items loaded around a player) and that player is near it (within HandNearM) and already has it.
    /// The new owner is near, the old one far: an object never bounces between two players.
    public static partial class Entities
    {
        public static bool HandoffEnabled = true;
        public static double HandFarM = 1000, HandNearM = 250;
        public static int HandPerTick = 16;
        public static long Handoffs;
        private static float _handAcc;
        private static readonly List<KeyValuePair<int, Vector3d>> _handPlayers = new List<KeyValuePair<int, Vector3d>>();

        private static void ServerHandoffTick(float dt)
        {
            if (Mp.Server == null || !HandoffEnabled) return;
            _handAcc += dt;
            if (_handAcc < 1f) return;
            _handAcc = 0f;
            Mp.Server.ReadyPeers(_handPlayers);
            if (!DedicatedServer.Enabled && mainscript.s != null && mainscript.s.player != null)
                _handPlayers.Add(new KeyValuePair<int, Vector3d>(0, mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position)));
            if (_handPlayers.Count < 1) return;
            double far2 = HandFarM * HandFarM;
            int n = 0;
            foreach (var se in Server.Values)
            {
                if (se.PartIndex >= 0 || se.CrashReturnTo >= 0) continue;                       // parts go with their car
                if (se.Driven && Time.realtimeSinceStartup - se.DrivenAt < 1f) continue;        // its driver keeps it
                double ownerD2 = double.MaxValue, bestD2 = HandNearM * HandNearM;              // owner not playing: as far as it gets
                int best = -1;
                foreach (var p in _handPlayers)
                {
                    double dx = p.Value.x - se.Where.x, dz = p.Value.z - se.Where.z, d2 = dx * dx + dz * dz;
                    if (p.Key == se.OwnerId) ownerD2 = d2;
                    else if (d2 < bestD2 && (p.Key == 0 || KnownBy(p.Key).Contains(se.NetId))) { bestD2 = d2; best = p.Key; }
                }
                if (best < 0 || ownerD2 <= far2) continue;
                se.OwnerId = best; se.Epoch++; Handoffs++;
                W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)best); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                if (++n >= HandPerTick) break;
            }
        }
    }
}
