using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Tumbleweeds. An item (thumbleweedScript) that gusts of wind push about while its machine applies the wind (bwind).
    /// The map's spawner (thumbleweedMain.Upd, a random wait) makes one around the player on sand, up to its limit; once
    /// far from the player the world's streaming drops it (PreDestroy: not stored). The game's MP: the host spawns and
    /// blows them, the others get them as items. Here every machine spawned its own around its own player, unshared:
    /// each player had tumbleweeds the other could not see.
    /// Now every machine still spawns around its own player - it has the ground there for them to roll on - and the new
    /// one is shared (the others get it as an item; its owner moves it). The limit counts every tumbleweed around the
    /// player, the others' too: players together do not get twice as many. The wind blows on the owner's machine only
    /// (a copy is moved by its owner).
    public static partial class Entities
    {
        public static long TumbleShared, TumbleLimited;
        public static float TumbleNearM = 300f;   // the others' tumbleweeds this near the player count for its limit

        [HarmonyPatch(typeof(thumbleweedMain), nameof(thumbleweedMain.Spawn))]
        private static class TumbleSpawn
        {
            [HarmonyPrefix]
            private static bool Prefix(thumbleweedMain __instance, bool ignoreLimit, out tosaveitemscript __state)
            {
                __state = __instance.nowSpawned;
                if (!InSession || ignoreLimit) return true;
                if (TumbleweedsNear(__instance) >= __instance.thumbleweedLimit) { TumbleLimited++; return false; }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(thumbleweedMain __instance, tosaveitemscript __state)
            {
                var it = __instance.nowSpawned;
                if (!InSession || it == null || it == __state) return;
                long before = SpawnShared;
                ShareSpawn(it.gameObject);
                if (SpawnShared > before) TumbleShared++;
            }
        }

        private static readonly HashSet<thumbleweedScript> _tumbles = new HashSet<thumbleweedScript>();
        /// This machine's own tumbleweeds count wherever they are (the game's count), the others' (copies: in the
        /// game's list too, and every shared one this machine knows of) only within TumbleNearM: a player 2 km off with
        /// a full set of its own must not stop them coming up here.
        private static int TumbleweedsNear(thumbleweedMain m)
        {
            _tumbles.Clear();
            var pl = mainscript.s != null ? mainscript.s.player : null;
            foreach (var t in m.thumbleweeds) if (t != null) Count(t, pl);
            foreach (var e in ByNet.Values) if (e.Root != null && e.Root.thumbleweed != null) Count(e.Root.thumbleweed, pl);
            return _tumbles.Count;
        }

        private static void Count(thumbleweedScript t, fpscontroller pl)
        {
            if (!IsProxy(t)) { _tumbles.Add(t); return; }
            if (pl != null && (t.transform.position - pl.transform.position).sqrMagnitude <= TumbleNearM * TumbleNearM) _tumbles.Add(t);
        }

        /// The wind blows where the tumbleweed is simulated: its owner's machine.
        [HarmonyPatch(typeof(thumbleweedScript), "FixedUpdate")]
        private static class TumbleWind
        {
            [HarmonyPrefix]
            private static void Prefix(thumbleweedScript __instance) { if (InSession) __instance.bwind = !IsProxy(__instance); }
        }

        // ---- diagnostics / test hooks (bridge `mp tumble`, `mp tumble spawn` (the limit applies), `mp tumble due`)
        public static string TumbleTest(string[] a)
        {
            var m = thumbleweedMain.s;
            if (m == null) return "{\"error\":\"no tumbleweed spawner on this map\"}";
            string did = "null";
            if (a.Length > 1 && a[1] == "spawn") { var was = m.nowSpawned; m.Spawn(false); did = m.nowSpawned != was ? "\"spawned\"" : "\"none\""; }
            if (a.Length > 1 && a[1] == "due") { m.spawnTime = 0f; did = "\"due\""; }
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            var all = new HashSet<thumbleweedScript>(Object.FindObjectsOfType<thumbleweedScript>());
            foreach (var e in ByNet.Values) if (e.Root != null && e.Root.thumbleweed != null) all.Add(e.Root.thumbleweed);
            foreach (var t in all)
            {
                var it = t.GetComponent<tosaveitemscript>();
                uint net = 0; int owner = -1;
                foreach (var e in ByNet.Values) if (e.Root == it) { net = e.NetId; owner = e.OwnerId; break; }
                var g = mainscript.GlobalFromUnityPos(t.transform.position);
                var v = t.rb != null ? t.rb.velocity : Vector3.zero;
                rows.Add("[" + net + "," + owner + "," + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "," +
                         v.magnitude.ToString("F2", ic) + "," + (t.bwind ? "true" : "false") + "," + (IsProxy(t) ? "true" : "false") + "," +
                         (t.gameObject.activeInHierarchy ? "true" : "false") + "]");
            }
            var srv = new List<string>();
            if (IsHost)
                foreach (var e0 in ByNet.Values)
                {
                    if (e0.Root == null || e0.Root.thumbleweed == null || !Server.TryGetValue(e0.NetId, out var se)) continue;
                    var d = RecordCodec.Decode(se.Record ?? new byte[0]);
                    var known = new List<string>();
                    if (Mp.Server != null) { Mp.Server.ReadyPeers(_peerPos); foreach (var kv in _peerPos) known.Add("[" + kv.Key + "," + (KnownBy(kv.Key).Contains(se.NetId) ? 1 : 0) + "," + kv.Value.x.ToString("F0", ic) + "," + kv.Value.z.ToString("F0", ic) + "]"); }
                    srv.Add("{\"net\":" + se.NetId + ",\"owner\":" + se.OwnerId + ",\"where\":[" + se.Where.x.ToString("F0", ic) + "," + se.Where.z.ToString("F0", ic) + "],\"hasState\":" + (se.HasState ? "true" : "false") +
                            ",\"records\":" + (d != null ? d.items.Count : -1) + ",\"recId\":" + (d != null && d.items.Count > 0 ? d.items[0].id : -1) + ",\"recBytes\":" + (se.Record != null ? se.Record.Length : -1) + ",\"peers\":[" + string.Join(",", known) + "]}");
                }
            return "{\"server\":[" + string.Join(",", srv) + "],\"did\":" + did + ",\"tumbleweeds\":[" + string.Join(",", rows) + "],\"listed\":" + m.thumbleweeds.Count + ",\"near\":" + TumbleweedsNear(m) +
                   ",\"limit\":" + m.thumbleweedLimit + ",\"dist\":[" + m.spawnMinDist.ToString("F0", ic) + "," + m.spawnMaxDist.ToString("F0", ic) + "]" +
                   ",\"next\":" + (m.spawnTime - Time.time).ToString("F0", ic) + ",\"shared\":" + TumbleShared + ",\"limited\":" + TumbleLimited +
                   ",\"biome\":" + (mainscript.s != null ? mainscript.s.GetCurrentBiome() : -1) + "}";
        }
    }
}
