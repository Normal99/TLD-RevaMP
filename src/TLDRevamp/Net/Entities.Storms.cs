using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Sandstorms in a session (the storms themselves: Fixes.Sandstorms). A storm is a local object (sandstormscript):
    /// it drifts, grows near players, darkens sky and fog for a player inside it, and its wind pushes loose bodies about.
    /// Each machine ran its own, so one player stood in a storm the other could not see, and its wind moved things on
    /// one machine only. Now the storms are the host's: its spawner alone runs (around any player) and a storm fades by
    /// the player nearest it (Fixes.Sandstorms); the host sends every storm (place, size, direction, speed, strength)
    /// when one comes up or goes and every StormResendS. A client makes, moves and removes its storms to match; how it
    /// looks and blows near a client is the game's own.
    public static partial class Entities
    {
        public const byte StormSync = 56;
        public static float StormResendS = 2f;
        public static long StormSyncsSent, StormSyncsApplied, StormsMade, StormsRemoved, StormSpawnsSkipped, StormSnaps;

        private static readonly Dictionary<sandstormscript, uint> StormIds = new Dictionary<sandstormscript, uint>();
        private static readonly Dictionary<uint, sandstormscript> StormById = new Dictionary<uint, sandstormscript>();
        private static uint _stormNext = 1;
        private static float _stormAcc;
        private static int _stormLastCount = -1;

        private static readonly AccessTools.FieldRef<sandstormscript, float> StormDist = AccessTools.FieldRefAccess<sandstormscript, float>("currentDistance");

        // ---- the host
        private static void StormTick(float dt)
        {
            if (napszakvaltakozas.s == null) return;
            var list = napszakvaltakozas.s.sandStorms;
            if (IsHost)
            {
                bool changed = list.Count != _stormLastCount;
                foreach (var st in list) if (st != null && !StormIds.ContainsKey(st)) { StormIds[st] = _stormNext++; changed = true; }
                _stormAcc += dt;
                if (!changed && _stormAcc < StormResendS) return;
                _stormAcc = 0f; _stormLastCount = list.Count;
                PruneStormIds();
                W.Reset(); W.U8(StormSync);
                int n = 0; foreach (var st in list) if (st != null) n++;
                W.U8((byte)Mathf.Min(n, 255));
                int k = 0;
                foreach (var st in list)
                {
                    if (st == null || k++ >= 255) continue;
                    var g = mainscript.GlobalFromUnityPos(st.transform.position);
                    W.U32(StormIds[st]); W.U8((byte)st.prefabid);
                    W.F64(g.x); W.F64(g.z); W.F32(st.transform.eulerAngles.y);
                    W.F32(st.size); W.F32(st.xdir); W.F32(st.ydir); W.F32(st.speed); W.F32(st.life);
                }
                ServerSendAll(W, true, 0);
                StormSyncsSent++;
            }
        }

        private static readonly List<sandstormscript> _stormGone = new List<sandstormscript>();
        private static void PruneStormIds()
        {
            _stormGone.Clear();
            foreach (var kv in StormIds) if (kv.Key == null) _stormGone.Add(kv.Key);
            foreach (var st in _stormGone) StormIds.Remove(st);
            Fixes.Sandstorms.Prune();
        }

        // ---- a client
        private static void ApplyStorms(NetReader r)
        {
            int n = r.U8();
            var seen = new HashSet<uint>();
            var rows = new List<(uint id, int pf, double x, double z, float yaw, float size, float xd, float yd, float sp, float life)>();
            for (int i = 0; i < n; i++)
                rows.Add((r.U32(), r.U8(), r.F64(), r.F64(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32()));
            if (r.Bad || IsHost || napszakvaltakozas.s == null || mainscript.s == null || itemdatabase.s == null) return;
            foreach (var row in rows)
            {
                seen.Add(row.id);
                StormById.TryGetValue(row.id, out var st);
                var pos = mainscript.UnityPosFromGlobal(new Vector3d(row.x, mainscript.GlobalFromUnityPos(Vector3.zero).y, row.z));
                pos.y = (float)mainscript.s.visszarakva.y;
                if (st == null)
                {
                    if (row.pf >= itemdatabase.s.storms.Length || itemdatabase.s.storms[row.pf] == null) continue;
                    var go = Object.Instantiate(itemdatabase.s.storms[row.pf], pos, Quaternion.Euler(0f, row.yaw, 0f));
                    st = go.GetComponent<sandstormscript>();
                    if (st == null) { Object.Destroy(go); continue; }
                    st.started = true;   // the host's size, direction, speed - not a new roll (Start: FStart only when not started)
                    StormById[row.id] = st;
                    StormsMade++;
                }
                st.size = row.size; st.xdir = row.xd; st.ydir = row.yd; st.speed = row.sp; st.life = row.life;
                var off = st.transform.position - pos; off.y = 0f;
                if (off.sqrMagnitude > 0.01f)
                {
                    if (off.sqrMagnitude > 25f) { st.transform.position = pos; StormSnaps++; }   // > 5 m: put it there
                    else st.transform.position = Vector3.Lerp(st.transform.position, pos, 0.5f);
                }
            }
            // storms the host no longer has, and this machine's own (loaded with its save before joining)
            _stormGone.Clear();
            foreach (var st in napszakvaltakozas.s.sandStorms)
            {
                if (st == null) continue;
                bool known = false;
                foreach (var kv in StormById) if (kv.Value == st) { known = seen.Contains(kv.Key); break; }
                if (!known) _stormGone.Add(st);
            }
            foreach (var st in _stormGone) { Object.Destroy(st.gameObject); StormsRemoved++; }
            var dead = new List<uint>();
            foreach (var kv in StormById) if (kv.Value == null || !seen.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var id in dead) StormById.Remove(id);
            StormSyncsApplied++;
        }

        private static void StormReset()
        {
            StormIds.Clear(); StormById.Clear(); _stormLastCount = -1; _stormAcc = 0f;
        }

        // ---- diagnostics / test hooks (bridge `mp storms`, `mp storms spawn gx gz`, `mp storms clear`, `mp storms timer`)
        public static string StormTest(string[] a)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (a.Length > 1 && a[1] == "spawn" && mainscript.s != null && itemdatabase.s != null && itemdatabase.s.storms.Length > 0)
            {
                var p = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(a[2], ic), mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position).y, double.Parse(a[3], ic)));
                // the beta has no spawner in its scene (stormprobe: mainscript.sandStormSpawn null, none loaded): the
                // storm is made as its SpawnAt makes one
                if (mainscript.s.sandStormSpawn != null) mainscript.s.sandStormSpawn.SpawnAt(p);
                else Object.Instantiate(itemdatabase.s.storms[0], p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                return "{\"spawned\":true,\"spawner\":" + (mainscript.s.sandStormSpawn != null ? "true" : "false") + "}";
            }
            if (a.Length > 1 && a[1] == "prefab" && itemdatabase.s != null)
            {
                var rows2 = new List<string>();
                foreach (var go in itemdatabase.s.storms)
                {
                    var x = go != null ? go.GetComponent<sandstormscript>() : null;
                    if (x == null) { rows2.Add("null"); continue; }
                    rows2.Add("{\"name\":" + Json.Str(go.name) + ",\"prefabid\":" + x.prefabid + ",\"biome\":" + x.biome + ",\"lifeGain\":" + x.lifeGain.ToString("F4", ic) +
                              ",\"lifeLoss\":" + x.lifeLossOnOtherBiome.ToString("F4", ic) + ",\"destroyDist\":" + x.destroyDist.ToString("F0", ic) +
                              ",\"radius\":" + x.radius.ToString("F0", ic) + ",\"windForce\":" + x.windForce.ToString("F1", ic) + ",\"life\":" + x.life.ToString("F2", ic) +
                              ",\"size\":" + x.size.ToString("F2", ic) + ",\"speed\":" + x.speed.ToString("F2", ic) + ",\"started\":" + (x.started ? "true" : "false") + "}");
                }
                return "{\"prefabs\":[" + string.Join(",", rows2) + "],\"currentBiome\":" + (napszakvaltakozas.s != null ? napszakvaltakozas.s.CurrentBiome : -1) + "}";
            }
            if (a.Length > 3 && a[1] == "biome")
                return "{\"biome\":" + Fixes.Sandstorms.BiomeAt(mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(a[2], ic), 0, double.Parse(a[3], ic)))) + "}";
            if (a.Length > 1 && a[1] == "sand" && mainscript.s != null)
            {
                // the nearest sand (biome 0) to the player: rings of 250 m out to 30 km
                var me = mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position);
                for (int ring = 0; ring <= 120; ring++)
                {
                    int steps = Mathf.Max(1, ring * 8);
                    for (int k = 0; k < steps; k++)
                    {
                        double ang = k * System.Math.PI * 2 / steps, rr = ring * 250.0;
                        var g = new Vector3d(me.x + System.Math.Cos(ang) * rr, me.y, me.z + System.Math.Sin(ang) * rr);
                        if (terrainGenSettings.GetFirstBiomeInt(g) != 0) continue;
                        // the middle of the sand there: well inside it (the next 2 km on from the edge all sand)
                        bool deep = true;
                        for (int m = 1; m <= 8 && deep; m++)
                            deep = terrainGenSettings.GetFirstBiomeInt(new Vector3d(me.x + System.Math.Cos(ang) * (rr + m * 250), me.y, me.z + System.Math.Sin(ang) * (rr + m * 250))) == 0;
                        if (!deep) continue;
                        var c = new Vector3d(me.x + System.Math.Cos(ang) * (rr + 1000), me.y, me.z + System.Math.Sin(ang) * (rr + 1000));
                        return "{\"gx\":" + c.x.ToString("F1", ic) + ",\"gz\":" + c.z.ToString("F1", ic) + ",\"edgeM\":" + rr.ToString("F0", ic) + "}";
                    }
                }
                return "{\"error\":\"no sand within 30 km\"}";
            }
            if (a.Length > 1 && a[1] == "clear" && napszakvaltakozas.s != null)
            {
                foreach (var st in napszakvaltakozas.s.sandStorms.ToArray()) if (st != null) Object.Destroy(st.gameObject);
                return "{\"cleared\":true}";
            }
            if (a.Length > 1 && a[1] == "timer" && (mainscript.s == null || mainscript.s.sandStormSpawn == null)) return "{\"error\":\"no spawner\"}";
            if (a.Length > 1 && a[1] == "timer")
            {
                mainscript.s.sandStormSpawn.nextTime = 0f;   // the spawner's time is up now (as if its random wait ran out)
                return "{\"due\":true}";
            }
            var rows = new List<string>();
            if (napszakvaltakozas.s != null)
                foreach (var st in napszakvaltakozas.s.sandStorms)
                {
                    if (st == null) continue;
                    var g = mainscript.GlobalFromUnityPos(st.transform.position);
                    uint id = 0;
                    if (!StormIds.TryGetValue(st, out id)) foreach (var kv in StormById) if (kv.Value == st) { id = kv.Key; break; }
                    rows.Add("[" + id + "," + st.prefabid + "," + g.x.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "," + st.size.ToString("F3", ic) + "," +
                             st.speed.ToString("F2", ic) + "," + st.xdir.ToString("F3", ic) + "," + st.ydir.ToString("F3", ic) + "," + st.life.ToString("F3", ic) + "," +
                             st.distanceLerp.ToString("F3", ic) + "," + (Fixes.Sandstorms.FadeDist.TryGetValue(st, out var fd) ? fd : StormDist(st)).ToString("F0", ic) + "," + st.destroyDist.ToString("F0", ic) + "]");
                }
            var sp = mainscript.s != null ? mainscript.s.sandStormSpawn : null;
            var all = Resources.FindObjectsOfTypeAll<sandStormSpawnScript>();
            var spawners = new List<string>();
            foreach (var x in all) spawners.Add(Json.Str(x.name + (x.gameObject.scene.IsValid() ? "" : "(asset)") + (x.gameObject.activeInHierarchy ? "" : "(inactive)") + (x.enabled ? "" : "(disabled)") + " under " + (x.transform.parent != null ? x.transform.parent.name : "-")));
            return "{\"sandstorms\":" + Fixes.Sandstorms.Stats + ",\"spawnerObjects\":[" + string.Join(",", spawners) + "],\"stormPrefabs\":" + (itemdatabase.s != null && itemdatabase.s.storms != null ? itemdatabase.s.storms.Length : -1) + ",\"storms\":[" + string.Join(",", rows) + "],\"sent\":" + StormSyncsSent + ",\"applied\":" + StormSyncsApplied + ",\"made\":" + StormsMade +
                   ",\"removed\":" + StormsRemoved + ",\"snaps\":" + StormSnaps +
                   (sp != null ? ",\"spawner\":{\"radius\":" + sp.radius.ToString("F0", ic) + ",\"min\":" + sp.minTime.ToString("F0", ic) + ",\"max\":" + sp.maxTime.ToString("F0", ic) +
                                 ",\"next\":" + (sp.lastTime + sp.nextTime - Time.time).ToString("F0", ic) + "}" : "") + "}";
        }
    }
}
