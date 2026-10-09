using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Always on, log only: the faults the scripted playtests caught once and never again (playround.py, 2026-10-09: the
    /// other player's car placed and gone again 35 m away; a door mirror on one machine's copy only) — written as
    /// "[anomaly]" lines (capped) for the next real playtest's logs. It never changes the game.
    /// Dev actions can't trip it: a deletion (any tool) leaves no far-store record, so it is not a store-out; a teleport
    /// streams by distance (the copy is far, and so is its owner's pose); a part changed by a tool reaches both machines —
    /// the census is asked twice, 6 s apart, and only a difference both times is logged. The last bridge command is noted
    /// on every line anyway.
    public static partial class Entities
    {
        public const byte CensusAsk = 65, CensusReply = 66;
        public static bool AnomalyWatch = true;
        public static int AnomalyLogCap = 40;
        public static float CensusDelayS = 8f, CensusRecheckS = 6f;
        public static long Anomalies, CensusAsks, CensusDiffers, StoreOutsSeen, StoreOutsAfterJump, CensusAsked, CensusRepliesIn;
        public static float JumpM = 50f, AfterJumpS = 20f;
        private static float _jumpAt = -1000f; private static Vector3d _lastGen; private static bool _hasGen;
        public static string LastDevCmd = ""; public static float LastDevAt = -1000f;   // DebugBridge.Execute
        private static int _anomLogged;
        private static readonly List<string> _anomLines = new List<string>();
        public static string AnomalyJson()
        {
            var rows = new List<string>(); foreach (var l in _anomLines) rows.Add(Json.Str(l));
            return "{\"count\":" + Anomalies + ",\"censusAsks\":" + CensusAsks + ",\"censusAsked\":" + CensusAsked + ",\"censusRepliesIn\":" + CensusRepliesIn + ",\"censusDiffers\":" + CensusDiffers +
                   ",\"storeOutsSeen\":" + StoreOutsSeen + ",\"storeOutsAfterJump\":" + StoreOutsAfterJump + ",\"lines\":[" + string.Join(",", rows) + "]}";
        }

        // ---- test hooks (bridge): a glitch made on purpose, to see the watch catch it
        /// `mp displace <net> <m>`: our copy put m metres off, and the game's removal pass at once (a displaced copy)
        public static string TestDisplace(uint net, float m)
        {
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || !Resolve(e) || e.Root == null || itemPlaceRemoveScript.s == null) return "{\"error\":\"no copy of it placed here\"}";
            e.Root.transform.root.position += new Vector3(m, 0f, 0f);
            double d = NearestPlayerM(mainscript.GlobalFromUnityPos(e.Root.transform.position));
            itemPlaceRemoveScript.s.RemoveStuff();
            return "{\"displaced\":" + net + ",\"fromPlayer\":" + d.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + ",\"ipReady\":" + (e.Ip != null && e.Ip.Ready ? "true" : "false") + "}";
        }
        /// `mp copypartgone <net> <name>`: a part of our copy gone here only (no message), then its census asked
        public static string TestCopyPartGone(uint net, string name)
        {
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || !Resolve(e) || e.Root == null) return "{\"error\":\"no copy of it placed here\"}";
            foreach (var it in e.Root.transform.root.GetComponentsInChildren<tosaveitemscript>(true))
            {
                if (it == null || it == e.Root || !it.name.Contains(name)) continue;
                string n = it.name;
                SuppressDestroyEcho = true;
                try { Object.DestroyImmediate(it.gameObject); } finally { SuppressDestroyEcho = false; }
                _censusDue[net] = new CensusDue { At = Time.realtimeSinceStartup, Attempt = 1 };
                return "{\"gone\":" + Json.Str(n) + "}";
            }
            return "{\"error\":\"no such part on it\"}";
        }
        private static readonly Dictionary<uint, float> _displacedAt = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, List<float>> _storeOuts = new Dictionary<uint, List<float>>();
        private struct CensusDue { public float At; public byte Attempt; }
        private static readonly Dictionary<uint, CensusDue> _censusDue = new Dictionary<uint, CensusDue>();

        private static void Anomaly(string what)
        {
            Anomalies++;
            if (_anomLogged >= AnomalyLogCap) return;
            _anomLogged++;
            float ago = Time.realtimeSinceStartup - LastDevAt;
            string line = what + (ago < 30f ? $" (dev command {ago:F0} s before: {LastDevCmd})" : "");
            _anomLines.Add(line);
            Plugin.Log.LogWarning("[anomaly] " + line);
        }

        private static double NearestPlayerM(Vector3d g)
        {
            double best = double.MaxValue;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.genArounds != null)
                foreach (var ga in map.genArounds) best = System.Math.Min(best, (mainscript.GlobalFromUnityPos(ga.upos) - g).magnitude);
            return best;
        }

        /// A copy streamed out (the game's distance streaming: its record kept). Wrong when its owner has it within a
        /// player's reach here — the copy was somewhere else than the owner's object (displaced); or when it happens
        /// again and again (the edge flicker).
        internal static void AnomalyStoreOut(Ent e, tosaveitemscript root)
        {
            if (!AnomalyWatch || !e.Proxy || root == null || itemPlaceRemoveScript.s == null) return;
            float now = Time.realtimeSinceStartup;
            StoreOutsSeen++;
            // right after our player jumped (a teleport, a respawn) everything left behind streams out: the game's own doing
            bool afterJump = now - _jumpAt < AfterJumpS;
            if (afterJump) StoreOutsAfterJump++;
            if (!_storeOuts.TryGetValue(e.NetId, out var times)) _storeOuts[e.NetId] = times = new List<float>();
            if (!afterJump) times.Add(now);
            times.RemoveAll(t => now - t > 60f);
            var copy = mainscript.GlobalFromUnityPos(root.transform.position);
            // the owner's pose: its last state, else where the copy was placed (a parked car sends nothing)
            var own = e.Ip != null && e.Ip.Ready ? e.Ip.LastPos : e.HasPlaced ? e.PlacedPos : copy;
            double dCopy = NearestPlayerM(copy), dOwn = NearestPlayerM(own);
            double lim = itemPlaceRemoveScript.s.itemRemoveDist;
            if (dOwn < lim - 25)
            {
                _displacedAt[e.NetId] = now;
                Anomaly($"copy stored out displaced: {root.name} net {e.NetId} owner {e.OwnerId} — the copy at {copy.x:F0},{copy.z:F0} ({dCopy:F0} m from the " +
                        $"player), its owner has it at {own.x:F0},{own.z:F0} ({dOwn:F0} m; removal beyond {lim:F0})");
            }
            else if (!afterJump && times.Count >= 3)
                Anomaly($"copy stored out {times.Count} times in 60 s: {root.name} net {e.NetId} owner {e.OwnerId}, at {copy.x:F0},{copy.z:F0} ({dCopy:F0} m from the player)");
        }

        /// A copy placed (Resolve bound it): placed again soon after a displaced store-out; a car's census asked later.
        internal static void AnomalyPlaced(Ent e)
        {
            if (!AnomalyWatch || !e.Proxy || e.Root == null) return;
            float now = Time.realtimeSinceStartup;
            e.PlacedPos = mainscript.GlobalFromUnityPos(e.Root.transform.position); e.HasPlaced = true;
            if (_displacedAt.TryGetValue(e.NetId, out float t) && now - t < 30f)
                Anomaly($"copy placed again {now - t:F1} s after a displaced store-out: {e.Root.name} net {e.NetId}");
            if (e.Root.car != null) _censusDue[e.NetId] = new CensusDue { At = now + CensusDelayS, Attempt = 1 };
        }

        /// What is bolted into the object, by model and slot (object names differ between an owner's spawn and a copy's):
        /// `mp carparts` and the census.
        internal static List<string> CensusRows(Ent e)
        {
            var rows = new List<string>();
            foreach (var it in e.Root.transform.root.GetComponentsInChildren<tosaveitemscript>(true))
            {
                if (it == null || it == e.Root || it.attachable == null || !it.attachable.attached || it.attachable.slot == null) continue;
                var sl = it.attachable.slot;
                var so = sl.tosaveitem;
                string owner = so != null ? PrefabName(so.id) + "#" + so.partslotscripts.IndexOf(sl) : "?" + sl.name;
                // its variant (whitewall or plain tyre, …): each random type selector's choice
                string tip = "";
                if (it.randoms != null) foreach (var rs in it.randoms) tip += rs != null ? "/" + rs.rtipus : "/-";
                rows.Add(owner + " = " + PrefabName(it.id) + tip);
            }
            string rtip = "";
            if (e.Root.randoms != null) foreach (var rs in e.Root.randoms) rtip += rs != null ? "/" + rs.rtipus : "/-";
            rows.Add("(car) = " + PrefabName(e.Root.id) + rtip);
            rows.Sort(System.StringComparer.Ordinal);
            return rows;
        }

        private static uint CensusHash(List<string> rows)
        {
            uint h = 2166136261;
            foreach (var row in rows) { foreach (char c in row) { h ^= c; h *= 16777619; } h ^= '\n'; h *= 16777619; }
            return h;
        }

        private static float _censusTick;
        private static readonly List<uint> _censusNow = new List<uint>();
        private static void AnomalyTick()
        {
            if (!AnomalyWatch) return;
            float now = Time.realtimeSinceStartup;
            // our player's jumps (genArounds[0] is the local player's generation centre)
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.genArounds != null && map.genArounds.Count > 0)
            {
                var g = mainscript.GlobalFromUnityPos(map.genArounds[0].upos);
                if (_hasGen && (g - _lastGen).magnitude > JumpM) _jumpAt = now;
                _lastGen = g; _hasGen = true;
            }
            if (_censusDue.Count == 0) return;
            if (now - _censusTick < 0.5f) return;
            _censusTick = now;
            _censusNow.Clear();
            foreach (var kv in _censusDue) if (now >= kv.Value.At) _censusNow.Add(kv.Key);
            foreach (var net in _censusNow)
            {
                byte attempt = _censusDue[net].Attempt;
                _censusDue.Remove(net);
                if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || e.Root == null) continue;
                W.Reset(); W.U8(CensusAsk); W.U32(net); W.U32(e.Epoch); W.U32(CensusHash(CensusRows(e))); W.U8(attempt);
                ToServer(W, true);
                CensusAsks++;
            }
        }

        /// Server: to the object's owner, with who asks.
        internal static void ServerCensusAsk(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(), hash = r.U32(); byte attempt = r.U8();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from || se.Epoch != epoch) return;
            WS.Reset(); WS.U8(CensusAsk); WS.U32(net); WS.U32(epoch); WS.U32(hash); WS.U8(attempt); WS.VarU32((uint)from);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Owner: silent when the copy has what we have; else our list, back to who asked.
        internal static void ApplyCensusAsk(NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(), hash = r.U32(); byte attempt = r.U8(); int asker = (int)r.VarU32();
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || e.Epoch != epoch || !Resolve(e) || e.Root == null) return;
            CensusAsked++;
            var rows = CensusRows(e);
            if (CensusHash(rows) == hash) return;
            W.Reset(); W.U8(CensusReply); W.U32(net); W.VarU32((uint)asker); W.U8(attempt); W.Str(string.Join("\n", rows));
            ToServer(W, true);
        }

        internal static void ServerCensusReply(int from, NetReader r)
        {
            uint net = r.U32(); int asker = (int)r.VarU32(); byte attempt = r.U8(); string rows = r.Str();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from) return;
            WS.Reset(); WS.U8(CensusReply); WS.U32(net); WS.VarU32((uint)asker); WS.U8(attempt); WS.Str(rows);
            ServerSendTo(asker, WS, true);
        }

        /// Asker: the owner's list differs from our copy's. Once: ask again later (a change on its way). Twice: logged.
        internal static void ApplyCensusReply(NetReader r)
        {
            uint net = r.U32(); r.VarU32(); byte attempt = r.U8(); string theirs = r.Str();
            CensusRepliesIn++;
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || !e.Proxy || e.Root == null) return;
            CensusDiffers++;
            if (attempt < 2) { _censusDue[net] = new CensusDue { At = Time.realtimeSinceStartup + CensusRecheckS, Attempt = 2 }; return; }
            var ours = CensusRows(e);
            var owner = new List<string>(theirs.Split('\n'));
            var missing = owner.FindAll(x => !ours.Contains(x));
            var extra = ours.FindAll(x => !owner.Contains(x));
            Anomaly($"copy's parts differ from the owner's: {e.Root.name} net {e.NetId} owner {e.OwnerId} — missing here [{string.Join("; ", missing)}], " +
                    $"extra here [{string.Join("; ", extra)}]");
        }
    }
}
