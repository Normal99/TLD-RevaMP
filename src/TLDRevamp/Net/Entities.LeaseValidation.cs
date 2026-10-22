using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private const float LeaseTimeoutS = 60f;
        private const double LeaseFallbackReachM = 5000;
        private static readonly Dictionary<string, float> LeaseUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, long> LeaseRefusedWhy = new Dictionary<string, long>();

        private static double LeaseReachM()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            double chunkSize = 0;
            if (map != null && map.poiGens != null)
                foreach (var gen in map.poiGens)
                    if (gen != null && Finite(gen.chunkSize) && gen.chunkSize > chunkSize) chunkSize = gen.chunkSize;
            // Farthest corner of the fixed 3x3 POI candidate area, plus one chunk for
            // requester movement within its chunk and POI placement/alignment offsets.
            return chunkSize > 0 ? chunkSize * (1.5 * Math.Sqrt(2) + 1) : LeaseFallbackReachM;
        }

        private static void RefuseLease(int from, string why, bool done = false)
        {
            string reason;
            switch (why)
            {
                case "malformed": reason = done ? "doneMalformed" : "malformed"; break;
                case "requester not ready": reason = "requesterNotReady"; break;
                case "building key": reason = "buildingKey"; break;
                case "building reach": reason = "buildingReach"; break;
                case "building type": reason = "buildingType"; break;
                case "building type mismatch": reason = "buildingTypeMismatch"; break;
                default: reason = "doneHolder"; break;
            }
            LeaseRefusedWhy.TryGetValue(reason, out long count);
            LeaseRefusedWhy[reason] = count + 1;
            SessionLog.Event(done ? "lease-done-refused" : "lease-refused", 0, from: from, result: why);
        }

        private static string LeaseRefusalsJson()
        {
            var rows = new List<string>();
            foreach (var kv in LeaseRefusedWhy)
                rows.Add("\"" + kv.Key + "\":" + kv.Value.ToString(CultureInfo.InvariantCulture));
            return "{" + string.Join(",", rows) + "}";
        }

        private static void ServerLeaseRequest(int from, NetReader r)
        {
            string key = r.Str();
            int type = int.MinValue;
            string typeName = "";
            if (r.Remaining > 0) { type = r.I32(); typeName = r.Str(); }
            if (r.Bad || r.Remaining != 0) { RefuseLease(from, "malformed"); return; }
            ServerLease(from, key, type, typeName);
        }

        private static void ServerLeaseDoneRequest(int from, NetReader r)
        {
            string key = r.Str();
            if (r.Bad || r.Remaining != 0) { RefuseLease(from, "malformed", done: true); return; }
            ServerLeaseDone(from, key);
        }

        private static bool LeasePosition(string key, out Vector3d pos)
        {
            pos = default;
            if (string.IsNullOrEmpty(key) || key.Length > 64) return false;
            var xy = key.Split(',');
            if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ||
                !Finite(x) || !Finite(z) || Math.Abs(x) > 1e8 || Math.Abs(z) > 1e8) return false;
            if (key != x.ToString("R", CultureInfo.InvariantCulture) + "," + z.ToString("R", CultureInfo.InvariantCulture)) return false;
            pos = new Vector3d(x, 0, z);
            return true;
        }

        private static bool LeasePlayerPosition(int from, out Vector3d pos)
        {
            pos = default;
            if (Mp.Server == null || from < 0) return false;
            if (from != 0) return Mp.Server.PeerPos(from, out pos) && Finite(pos);
            var player = mainscript.s != null ? mainscript.s.player : null;
            if (player == null) return false;
            pos = mainscript.GlobalFromUnityPos(player.transform.position);
            return Finite(pos);
        }

        private static string CheckLease(int from, string key, ref int type, out string typeName)
        {
            typeName = "";
            if (!LeasePlayerPosition(from, out var player)) return "requester not ready";
            if (!LeasePosition(key, out var at)) return "building key";
            double dx = player.x - at.x, dz = player.z - at.z;
            double reach = LeaseReachM();
            if (dx * dx + dz * dz > reach * reach) return "building reach";
            var poi = FindPoi(key);
            if (type == int.MinValue && poi != null) type = poi.id;
            if (type < 0 || itemdatabase.s == null) return "building type";
            GameObject prefab;
            try { prefab = itemdatabase.s.Building(type); }
            catch { return "building type"; }
            if (prefab == null) return "building type";
            typeName = prefab.name;
            // another building type there than the host's: nobody spawns contents for a building the host doesn't have.
            // With a holder, the asker only takes the holder's contents: ServerLease logs the difference (the missing
            // diner) and hands them over — refused, the asker had been told the lease was free, asked again and was refused
            // again, and neither machine logged the different building (leasetype.py, full tier 2026-10-22)
            if (!Leases.ContainsKey(key) && poi != null && poi.id != type) return "building type mismatch";
            return null;
        }

        private static void ServerLease(int from, string key, int type = int.MinValue, string typeName = "")
        {
            ServerLeaseExpiry();
            string why = CheckLease(from, key, ref type, out typeName);
            if (why != null)
            {
                if (why == "building type mismatch" && ++LeaseTypeMismatches <= 20)
                {
                    var hp = FindPoi(key);
                    Anomaly($"building differs at {key}: player {from} has type {type}, the host has {(hp != null ? hp.poiName + " (type " + hp.id + ")" : "none")} — its contents are not spawned there");
                }
                RefuseLease(from, why);
                // LeaseGate set the local spawn flags before asking. Let it retry when validation can succeed.
                if (from >= 0 && LeasePosition(key, out _))
                { W.Reset(); W.U8(LeaseFreed); W.Str(key); ServerSendTo(from, W, true); }
                return;
            }
            if (Leases.TryGetValue(key, out int holder))
            {
                // someone else spawned it: the asker drops its own built-in copies and takes theirs. The type stays the
                // HOLDER's (it was overwritten with the asker's here: the asker compared its building with itself)
                if (holder != from)
                {
                    bool known = LeaseTypes.TryGetValue(key, out var ht);
                    // the host's own buildings from before it hosted (HostStarted) have no type recorded: its world's
                    var hp = holder == 0 && !known ? FindPoi(key) : null;
                    if (hp != null) { ht = new KeyValuePair<int, string>(hp.id, hp.poiName ?? ""); known = true; }
                    Plugin.Log.LogInfo($"lease {key}: taken by {from} (holder {holder}{(_leasesDone.Contains(key) ? ", done" : "")})");
                    if (known && ht.Key != type)
                    {
                        LeaseTypeMismatches++;
                        Anomaly($"building differs at {key}: player {from} has {typeName} (type {type}), the lease holder {holder} has {ht.Value} (type {ht.Key}) — the holder's contents are put where the other has another building");
                    }
                    W.Reset(); W.U8(LeaseTaken); W.Str(key); W.I32(known ? ht.Key : int.MinValue); W.Str(known ? ht.Value ?? "" : ""); ServerSendTo(from, W, true);
                }
                return;
            }
            LeaseTypes[key] = new KeyValuePair<int, string>(type, typeName);
            Leases[key] = from;
            LeaseUntil[key] = Time.realtimeSinceStartup + LeaseTimeoutS;
            Plugin.Log.LogInfo($"lease {key}: granted to {from}");
            W.Reset(); W.U8(LeaseGrant); W.Str(key);
            ServerSendTo(from, W, true);
            LeasesGranted++;
        }

        /// Server: this lease's holder finished spawning and sharing its building.
        private static void ServerLeaseDone(int from, string key)
        {
            ServerLeaseExpiry();
            if (!LeasePlayerPosition(from, out _) || !LeasePosition(key, out _) || !Leases.TryGetValue(key, out int holder) || holder != from)
            { RefuseLease(from, "missing lease or wrong holder", done: true); return; }
            _leasesDone.Add(key); LeaseUntil.Remove(key);
            Plugin.Log.LogInfo($"lease {key}: done ({from})");
        }

        private static void FreeLease(string key)
        {
            Leases.Remove(key); LeaseTypes.Remove(key); LeaseUntil.Remove(key); _leasesDone.Remove(key); LeasesFreed++;
            W.Reset(); W.U8(LeaseFreed); W.Str(key);
            ServerSendAll(W, true, -1);
        }

        private static void ServerLeaseExpiry()
        {
            foreach (var kv in new List<KeyValuePair<string, float>>(LeaseUntil))
                if (Time.realtimeSinceStartup >= kv.Value || (Leases.TryGetValue(kv.Key, out int holder) && !LeasePlayerPosition(holder, out _))) FreeLease(kv.Key);
        }

        private static void ServerLeaseLeave(int playerId)
        {
            foreach (string key in new List<string>(Leases.Keys))
            {
                if (Leases[key] != playerId) continue;
                if (_leasesDone.Contains(key)) { Leases[key] = 0; LeaseUntil.Remove(key); }
                else FreeLease(key);
            }
        }
    }
}
