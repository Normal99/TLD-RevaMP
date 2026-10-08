using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Objects are simulated where players are. An object is simulated by its owner's machine only while that machine has
    /// it loaded — around its own player. When the owner travels away (or is the dedicated server's phantom, which never
    /// moves), the object goes dormant there, and every other player's copy is a kinematic stand-in that nothing drives:
    /// a creature in a building stood frozen while another player walked through it, a car or a can stayed unpushable
    /// until someone happened to claim it. The game's own MP has no such case (one host simulates everything around it).
    /// So the server hands an object to the nearest player who has it once its owner's player is beyond the game's item
    /// FREEZE distance (itemPlaceRemoveScript.itemFreezeDist, 200 m: the owner's game has frozen it — and beyond 300 m
    /// removed it into the far store) and that player is within the UNFREEZE distance (itemUnFreezeDist, 100 m: where
    /// the game runs items' physics around a player). The 100 m between the two is the hysteresis: an object never
    /// bounces between two players. (v0.63.0 used 1 km / 250 m — invented, not the game's: an owner 300 m away had it
    /// stored and kept it; handoff run 3.)
    public static partial class Entities
    {
        public static bool HandoffEnabled = true;
        public static double HandFarM, HandNearM;     // 0: the game's own (freeze / unfreeze distance)
        public static int HandPerTick = 16;
        public static long Handoffs, HandoffKeptCarried;
        public static bool HandoffKeepCarried = true;   // A/B: false = before v0.65.55 (invsync.py --carry)
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
            var ipr = itemPlaceRemoveScript.s;
            double farM = HandFarM > 0 ? HandFarM : ipr != null && ipr.itemFreezeDist > 0 ? ipr.itemFreezeDist : 200;
            double nearM = HandNearM > 0 ? HandNearM : ipr != null && ipr.itemUnFreezeDist > 0 ? ipr.itemUnFreezeDist : 100;
            if (nearM >= farM) nearM = farM * 0.5;
            double far2 = farM * farM;
            int n = 0;
            foreach (var se in Server.Values)
            {
                if (se.PartIndex >= 0 || se.CrashReturnTo >= 0) continue;                       // parts go with their car
                if (se.Driven && Time.realtimeSinceStartup - se.DrivenAt < 1f) continue;        // its driver keeps it
                if (HandoffKeepCarried && (se.Held || se.Stored)) { HandoffKeptCarried++; continue; }   // in someone's hands or inventory: theirs, wherever it was last seen
                double ownerD2 = double.MaxValue, bestD2 = nearM * nearM;              // owner not playing: as far as it gets
                int best = -1;
                foreach (var p in _handPlayers)
                {
                    double dx = p.Value.x - se.Where.x, dz = p.Value.z - se.Where.z, d2 = dx * dx + dz * dz;
                    if (p.Key == se.OwnerId) ownerD2 = d2;
                    else if (d2 < bestD2 && (p.Key == 0 || KnownBy(p.Key).Contains(se.NetId))) { bestD2 = d2; best = p.Key; }
                }
                if (best < 0 || ownerD2 <= far2 || bestD2 >= ownerD2) continue;
                OwnerLog(se, best, "hand-off (nearer player)");
                se.OwnerId = best; se.Epoch++; Handoffs++;
                W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)best); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                if (++n >= HandPerTick) break;
            }
        }
    }
}
