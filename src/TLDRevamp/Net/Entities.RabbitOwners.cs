using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// Server, four times a second: each rabbit to the machine whose player or moving car (driven, or faster than
        /// RabbitCarMoveMps) is nearest it, within RabbitNearM, by RabbitSwitchM more than its owner's - not within
        /// RabbitHoldS of a claim (a car about to hit it, it about to hit something).
        private static readonly List<KeyValuePair<int, Vector3d>> _rabAnchors = new List<KeyValuePair<int, Vector3d>>();
        private static void ServerRabbitTick(float dt)
        {
            if (Mp.Server == null || !RabbitSync || !RabbitOwners) return;
            _rabSrvAcc += dt;
            if (_rabSrvAcc < 0.25f) return;
            _rabSrvAcc = 0f;
            bool any = false;
            foreach (var se in Server.Values) if (se.Rabbit) { any = true; break; }
            if (!any) return;
            Mp.Server.ReadyPeers(_rabAnchors);
            if (!DedicatedServer.Enabled && mainscript.s != null && mainscript.s.player != null)
                _rabAnchors.Add(new KeyValuePair<int, Vector3d>(0, mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position)));
            float rt = Time.realtimeSinceStartup;
            foreach (var c in Server.Values)
                if (c.Car && c.PartIndex < 0 && c.HasState && ((c.Driven && rt - c.DrivenAt < 1f) || c.Speed > RabbitCarMoveMps))
                    _rabAnchors.Add(new KeyValuePair<int, Vector3d>(c.OwnerId, c.Where));
            if (_rabAnchors.Count < 2) return;   // one machine: nothing to decide
            double near2 = RabbitNearM * (double)RabbitNearM;
            foreach (var se in Server.Values)
            {
                if (!se.Rabbit || se.PartIndex >= 0 || se.CrashReturnTo >= 0 || se.Held || se.Stored || rt < se.RabbitHoldUntil) continue;
                int best = -1; double bestD = double.MaxValue, curD = double.MaxValue;
                foreach (var p in _rabAnchors)
                {
                    double d = (p.Value - se.Where).sqrMagnitude;
                    if (p.Key == se.OwnerId) { if (d < curD) curD = d; continue; }
                    if (d < bestD && (p.Key == 0 || KnownBy(p.Key).Contains(se.NetId))) { bestD = d; best = p.Key; }
                }
                if (best < 0 || bestD > near2) continue;
                double bd = System.Math.Sqrt(bestD), cd = curD == double.MaxValue ? double.MaxValue : System.Math.Sqrt(curD);
                if (cd <= bd + RabbitSwitchM) continue;
                RabbitLastMove = "net " + se.NetId + ": " + se.OwnerId + " -> " + best + " (" + bd.ToString("F1") + " m against " + (cd == double.MaxValue ? "none" : cd.ToString("F1")) + ")";
                OwnerLog(se, best, "rabbit: nearest player / moving car");
                se.OwnerId = best; se.Epoch++; RabbitOwnerMoves++;
                W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)best); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                CascadeOwner(se);
            }
        }

    }
}
