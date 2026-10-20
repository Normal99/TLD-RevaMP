using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        // Reliable manifest followed by the ordinary full records and an end marker. No sending limits.
        internal static void ServerJoinLoading(int player, Vector3d at)
        {
            var ids = new HashSet<uint>(); var roots = new HashSet<uint>();
            foreach (var se in Server.Values)
            {
                ServerLockedPose(se);
                double dx = se.Where.x - at.x, dz = se.Where.z - at.z;
                if (dx * dx + dz * dz > 300 * 300) continue;
                ids.Add(se.NetId); roots.Add(se.PartIndex >= 0 ? se.ParentNet : se.NetId);
            }
            foreach (uint root in roots) ids.Add(root);
            W.Reset(); W.U8(Protocol.JoinLoading); W.U8(0); W.VarU32((uint)ids.Count);
            foreach (uint id in ids) W.U32(id);
            if (Mp.Server == null || !Mp.Server.SendToPlayer(player, W, true)) return;
            _tpaSending = true; _tpaSendFailed = false;
            try
            {
                foreach (uint id in roots)
                    if (Server.TryGetValue(id, out var se))
                    {
                        if (!KnownBy(player).Contains(id)) SendObject(player, se);
                        else SendTpaState(player, se);
                    }
                if (_tpaSendFailed) return;
                W.Reset(); W.U8(Protocol.JoinLoading); W.U8(1); ServerSendTo(player, W, true);
            }
            finally { _tpaSending = false; }
        }

        internal static int JoinApplied(HashSet<uint> ids)
        {
            int n = 0;
            foreach (uint id in ids)
            {
                // Far records are applied too; nearby records must first be placed by the game's far store.
                if (!ByNet.TryGetValue(id, out var e)) continue;
                if (e.Root != null || (e.FarPos - Mp.LocalGlobal()).magnitude > 300) n++;
            }
            return n;
        }
    }
}
