using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Doors, gates, hatches, switches of the world's buildings and sites (usablescript on a poiScript: setidPoi).
    /// The game marks a player's change for its official MP (usablescript.SyncMulti(true) → SendUsable with the
    /// building's address: generator, position id, usable index); Entities.Edits took only the usables of ITEMS
    /// (cars, loose objects) — a building's door opened by one player stayed shut for everyone else (user, 2026-10-01:
    /// "Door and gates are not synced still").
    /// Now the change goes out with the same address, at most 10 times a second per usable, and every machine applies
    /// it with the game's own UpdFromMulti (its sounds included). A machine whose building is unloaded when a change
    /// arrives can't apply it, and its own snapshot of the building (save_poi, taken by poiGenScript.RemovePoi at
    /// unload and loaded after poiScript.FStart on respawn) holds the door as it was when it left — so the session
    /// remembers every changed usable: the server keeps the last state for players who join later, and each machine
    /// re-applies the known state when the building spawns again, after the game's own snapshot (doors run 2: the
    /// snapshot closed a door the host had opened meanwhile) — everyone sees the same door.
    public static partial class Entities
    {
        public const byte PoiUsable = 52;
        public static long PoiUsablesSent, PoiUsablesApplied, PoiUsablesOnSpawn, PoiUsablesUnplaced;

        private struct PoiKey : System.IEquatable<PoiKey>
        {
            public int Gen, U; public double X, Y, Z;
            public bool Equals(PoiKey o) => Gen == o.Gen && U == o.U && X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object o) => o is PoiKey k && Equals(k);
            public override int GetHashCode() => Gen * 397 ^ U * 7919 ^ X.GetHashCode() ^ Y.GetHashCode() * 31 ^ Z.GetHashCode() * 17;
        }
        private struct PoiState { public float XRot, YRot, Slide, Tuned; public int Turn; }

        private static readonly Dictionary<PoiKey, byte[]> ServerPoiUsables = new Dictionary<PoiKey, byte[]>();   // server: for joiners
        private static readonly Dictionary<PoiKey, PoiState> KnownPoiUsables = new Dictionary<PoiKey, PoiState>(); // every machine: re-spawned buildings
        private static readonly HashSet<usablescript> PoiDirty = new HashSet<usablescript>();
        private static float _poiSendAt;
        private static bool _applyingPoi;

        [HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class PoiUsableChanged
        {
            [HarmonyPostfix]
            private static void Postfix(usablescript __instance, bool syncinmulti)
            {
                if (syncinmulti && InSession && !_applyingPoi && __instance.setidPoi) PoiDirty.Add(__instance);
            }
        }

        /// Per frame: changed building usables out, 10 Hz (a door swung by hand changes every frame).
        private static void PoiUsableTick()
        {
            if (PoiDirty.Count == 0 || Time.unscaledTime < _poiSendAt) return;
            _poiSendAt = Time.unscaledTime + 0.1f;
            foreach (var u in PoiDirty)
            {
                if (u == null) continue;
                var k = new PoiKey { Gen = u.poigenid, X = u.poiid.x, Y = u.poiid.y, Z = u.poiid.z, U = u.usableid };
                var st = new PoiState { XRot = u.xRot, YRot = u.yRot, Slide = u.slideValue, Turn = u.currentTurnState, Tuned = u.tunedFloat };
                KnownPoiUsables[k] = st;
                W.Reset(); W.U8(PoiUsable); WritePoi(W, k, st);
                ToServer(W, true); PoiUsablesSent++;
            }
            PoiDirty.Clear();
        }

        private static void WritePoi(NetWriter w, PoiKey k, PoiState st)
        {
            w.VarU32((uint)k.Gen); w.F64(k.X); w.F64(k.Y); w.F64(k.Z); w.VarU32((uint)k.U);
            w.F32(st.XRot); w.F32(st.YRot); w.F32(st.Slide); w.VarI32(st.Turn); w.F32(st.Tuned);
        }

        private static bool ReadPoi(NetReader r, out PoiKey k, out PoiState st)
        {
            k = new PoiKey { Gen = (int)r.VarU32(), X = r.F64(), Y = r.F64(), Z = r.F64(), U = (int)r.VarU32() };
            st = new PoiState { XRot = r.F32(), YRot = r.F32(), Slide = r.F32(), Turn = r.VarI32(), Tuned = r.F32() };
            return !r.Bad && Finite(st.XRot) && Finite(st.YRot) && Finite(st.Slide) && Finite(st.Tuned);
        }

        private static void ServerPoiUsable(int from, NetReader r)
        {
            if (!ReadPoi(r, out var k, out _)) return;
            var bytes = new byte[r.End];
            System.Buffer.BlockCopy(r.Buf, 0, bytes, 0, r.End);
            ServerPoiUsables[k] = bytes;
            WS.Reset(); WS.Bytes(bytes, 0, bytes.Length);
            ServerSendAll(WS, true, from);
        }

        /// A joiner: every building usable changed this session.
        private static void ServerPoiUsablesTo(int playerId)
        {
            foreach (var b in ServerPoiUsables.Values)
            {
                WS.Reset(); WS.Bytes(b, 0, b.Length);
                ServerSendTo(playerId, WS, true);
            }
        }

        private static void ApplyPoiUsable(NetReader r)
        {
            if (!ReadPoi(r, out var k, out var st)) return;
            KnownPoiUsables[k] = st;
            var u = PoiUsableOf(k);
            if (u == null) { PoiUsablesUnplaced++; return; }   // that building isn't spawned here: applied when it is
            _applyingPoi = true;
            try { u.UpdFromMulti(st.Slide, st.XRot, st.YRot, st.Turn, st.Tuned); PoiUsablesApplied++; }
            finally { _applyingPoi = false; }
        }

        /// The game's own lookup (syncScript.RecUsable): generator → chunk → poi → usable index.
        private static usablescript PoiUsableOf(PoiKey k)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null || k.Gen < 0 || k.Gen >= map.poiGens.Count || map.poiGens[k.Gen] == null) return null;
            var gen = map.poiGens[k.Gen];
            var id = new Vector3d(k.X, k.Y, k.Z);
            var chunkPos = mainscript.GetChunkPos(id, gen.chunkSize);
            if (gen.chunks == null || !gen.chunks.TryGetValue(chunkPos, out var chunk) || chunk.pois == null || !chunk.pois.TryGetValue(id, out var poi)) return null;
            var p = poi.pobj;
            if (p == null || p.usables == null || k.U < 0 || k.U >= p.usables.Count) return null;
            return p.usables[k.U];
        }

        /// A building spawning here: the session's known state of its usables (silently — no door sound on arrival).
        [HarmonyPatch(typeof(poiScript), nameof(poiScript.FStart))]
        private static class PoiSpawned
        {
            [HarmonyPostfix]
            private static void Postfix(poiScript __instance) => ApplyKnown(__instance);
        }

        private static void ApplyKnown(poiScript p)
        {
            if (!InSession || KnownPoiUsables.Count == 0 || p == null || p.usables == null) return;
            for (int i = 0; i < p.usables.Count; i++)
            {
                var u = p.usables[i];
                if (u == null) continue;
                var k = new PoiKey { Gen = p.poigenid, X = p.posID.x, Y = p.posID.y, Z = p.posID.z, U = i };
                if (!KnownPoiUsables.TryGetValue(k, out var st)) continue;
                _applyingPoi = true;
                try { u.tunedFloat = st.Tuned; u.Refresh(st.Slide, st.XRot, st.YRot, st.Turn); PoiUsablesOnSpawn++; }
                finally { _applyingPoi = false; }
            }
        }

        // ---------------------------------------------------------------- debug (bridge: mp poius / poiuse / poiu)
        private static string D(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        private static string F(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        private static string PoiRow(poiScript p, int i, usablescript u, float dist)
        {
            var g = mainscript.GlobalFromUnityPos(u.transform.position);
            string kind = (u.RT1 != null ? "rot " : "") + (u.slideAble ? "slide " : "") + (u.turnable ? "turn " : "");
            return "{\"gen\":" + p.poigenid + ",\"id\":[" + D(p.posID.x) + "," + D(p.posID.y) + "," + D(p.posID.z) + "],\"u\":" + i +
                   ",\"poi\":" + Json.Str(p.name) + ",\"name\":" + Json.Str(u.name) + ",\"kind\":" + Json.Str(kind.Trim()) + ",\"dist\":" + F(dist) +
                   ",\"at\":[" + F((float)g.x) + "," + F((float)g.y) + "," + F((float)g.z) + "]," + PoiStateJson(u) + "}";
        }

        private static string PoiStateJson(usablescript u) => "\"x\":" + F(u.xRot) + ",\"y\":" + F(u.yRot) + ",\"slide\":" + F(u.slideValue) +
                                                               ",\"turn\":" + u.currentTurnState + ",\"minX\":" + F(u.minX) + ",\"maxX\":" + F(u.maxX) +
                                                               ",\"minY\":" + F(u.minY) + ",\"maxY\":" + F(u.maxY);

        /// Building usables near the player (spawned here).
        public static string PoiList(float radius)
        {
            var me = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
            var rows = new List<string>();
            foreach (var p in Object.FindObjectsOfType<poiScript>())
            {
                if (p == null || p.usables == null || !p.started) continue;
                for (int i = 0; i < p.usables.Count; i++)
                {
                    var u = p.usables[i];
                    if (u == null) continue;
                    float d = (u.transform.position - me).magnitude;
                    if (d <= radius) rows.Add(PoiRow(p, i, u, d));
                }
            }
            return "{\"usables\":[" + string.Join(",", rows) + "]}";
        }

        private static usablescript PoiArg(string[] a, int at)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var k = new PoiKey { Gen = int.Parse(a[at]), X = double.Parse(a[at + 1], ci), Y = double.Parse(a[at + 2], ci), Z = double.Parse(a[at + 3], ci), U = int.Parse(a[at + 4]) };
            return PoiUsableOf(k);
        }

        /// One building usable's state here: `mp poiu gen x y z u`.
        public static string PoiGet(string[] a)
        {
            var u = PoiArg(a, 1);
            return u == null ? "{\"error\":\"not spawned here\"}" : "{" + PoiStateJson(u) + "}";
        }

        /// Operate it as a player does (the game's own input calls, synced like a player's): `mp poiuse gen x y z u
        /// rot dx dy | slide v | turn`.
        public static string PoiUse(string[] a)
        {
            var u = PoiArg(a, 1);
            if (u == null) return "{\"error\":\"not spawned here\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (a[6])
            {
                case "rot": u.Rot(float.Parse(a[7], ci), float.Parse(a[8], ci), true); break;
                case "slide": { float v = float.Parse(a[7], ci); u.Slide(v, v, v, true); break; }
                case "turn": u.Turn(); break;
                default: return "{\"error\":\"rot dx dy | slide v | turn\"}";
            }
            return "{" + PoiStateJson(u) + "}";
        }

        /// The building's own snapshot loaded on respawn (poiGenScript: FStart, then save.Load): the session's newer state wins.
        [HarmonyPatch(typeof(save_poi), nameof(save_poi.Load), new[] { typeof(poiScript) })]
        private static class PoiSnapshotLoaded
        {
            [HarmonyPostfix]
            private static void Postfix(poiScript _poi) => ApplyKnown(_poi);
        }

        private static void PoiUsablesReset() { ServerPoiUsables.Clear(); KnownPoiUsables.Clear(); PoiDirty.Clear(); }

        public static string PoiUsableStats() => "{\"sent\":" + PoiUsablesSent + ",\"applied\":" + PoiUsablesApplied + ",\"onSpawn\":" + PoiUsablesOnSpawn +
                                                 ",\"unplaced\":" + PoiUsablesUnplaced + ",\"known\":" + KnownPoiUsables.Count + ",\"serverKnown\":" + ServerPoiUsables.Count + ",\"removeDist\":[" + RemoveDists() + "]}";

        private static string RemoveDists()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            var r = new List<string>();
            if (map != null && map.poiGens != null) foreach (var gn in map.poiGens) r.Add(gn == null ? "null" : F(gn.removeDistance));
            return string.Join(",", r);
        }
    }
}
