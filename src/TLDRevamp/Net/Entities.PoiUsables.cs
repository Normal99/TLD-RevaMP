using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Building addresses, game lookup, respawn restoration and bridge controls.
    public static partial class Entities
    {
        public const byte PoiUsable = 52;
        public static long PoiUsablesSent, PoiUsablesApplied, PoiUsablesOnSpawn, PoiUsablesUnplaced;

        private static bool _applyingPoi;

        [HarmonyPatch(typeof(usablescript), nameof(usablescript.SyncMulti), new[] { typeof(bool) })]
        private static class PoiUsableChanged
        {
            [HarmonyPostfix]
            private static void Postfix(usablescript __instance, bool syncinmulti)
            {
                if (syncinmulti && InSession && !_applyingPoi && __instance.setidPoi) MarkLiveUsable(__instance);
            }
        }

        /// A joiner: every building usable changed this session (with InterestRelay: as they come near each building).
        private static void ServerPoiUsablesTo(int playerId)
        {
            if (InterestRelay) { PoiAllStale(playerId); return; }
            foreach (var b in ServerPoiUsables.Values)
            {
                WS.Reset(); WS.Bytes(b, 0, b.Length);
                ServerSendTo(playerId, WS, true);
            }
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
            if (u == null) return "{\"error\":\"not spawned here\"}";
            var k = PoiLiveKey(u); _poiObserved.TryGetValue(k.Poi, out var s);
            float now = Time.realtimeSinceStartup;
            return "{" + PoiStateJson(u) + ",\"value\":" + UsableValuesJson(UsableState(u)) +
                ",\"sendHz\":" + (s != null ? s.Sent.Hz(now) : 0) + ",\"receiveHz\":" + (s != null ? s.Received.Hz(now) : 0) +
                ",\"received\":" + (s != null ? s.ReceivedTotal : 0) + ",\"interpolating\":" + (_remoteUsables.ContainsKey(k) ? "true" : "false") +
                ",\"changeAge\":" + (s != null ? UN(now - s.ChangedAt) : "null") + "}";
        }

        public static string PoiDrag(string[] a) => StartUsableDrag(PoiArg(a, 1), float.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture),
            a.Length > 7 ? float.Parse(a[7], System.Globalization.CultureInfo.InvariantCulture) : 60f);
        public static string PoiProbe(string[] a) => a.Length > 1 ? StartUsableProbe(PoiArg(a, 1), float.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture)) : UsableProbeJson();

        private static usablescript PoiFocusUsable()
        {
            if (!InSession || _telemetryLook == null) return null;
            var u = _telemetryLook.GetComponentInParent<usablescript>();
            return u != null && u.setidPoi ? u : null;
        }
        // ObjectPanels calls this only at its half-second text refresh, never from GUI repaint.
        internal static string PoiFocusDescription(double now)
        {
            var u = PoiFocusUsable(); if (u == null) return null;
            var k = PoiLiveKey(u); _poiObserved.TryGetValue(k.Poi, out var s);
            string name = TelemetryPrivacy.ObjectName(u.gameObject.name);
            return name + " / building part\nPoiKey: generator " + k.Poi.Gen + " / position " + D(k.Poi.X) + ", " + D(k.Poi.Y) + ", " + D(k.Poi.Z) +
                " / usable " + k.Poi.U + "\nxRot " + F(u.xRot) + " / yRot " + F(u.yRot) + " / slide " + F(u.slideValue) + " / turn " + u.currentTurnState +
                "\nlast change " + (s != null ? F((float)now - s.ChangedAt) + " s ago" : "unknown") +
                "\nlive send " + (s != null ? s.Sent.Hz((float)now) : 0) + " Hz / receive " + (s != null ? s.Received.Hz((float)now) : 0) +
                " Hz / interpolating " + (_remoteUsables.ContainsKey(k) ? "yes" : "no");
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

        private static void PoiUsablesReset() { ServerPoiUsables.Clear(); KnownPoiUsables.Clear(); _poiObserved.Clear(); _poiServerOrder.Clear(); }

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
