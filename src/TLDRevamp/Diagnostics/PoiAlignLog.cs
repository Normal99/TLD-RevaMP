using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Test (poicheck.py, bridge `poialign on|off|<gx> <gz> <r>`): every building aligned to a road, as the game decided it
    /// — the candidate spot, where it went, the roads RoadsNear offered (from the road index around the spot only, and
    /// with the game's fallbacks) and which main-line road chunks existed then. Off unless switched on.
    [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.AlignPosToRoad))]
    internal static class PoiAlignLog
    {
        public static bool On;
        private struct Row { public Vector3d From, To; public string Json; }
        private static readonly List<Row> Rows = new List<Row>();

        [HarmonyPostfix]
        private static void Post(poiGenScript __instance, Vector3d _gpos, ref Vector3d _pos, bool __result, poiGenScript.alignToRoadEmu align)
        {
            if (!On) return;
            try
            {
                var rg = __instance.roadgen;
                if (rg == null) return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string N(double v) => v.ToString("0.#", ci);
                string Roads(List<roadGenScript.roadClass> l)
                {
                    var s = new List<string>();
                    foreach (var r in l) s.Add("[" + N(r.start.x) + "," + N(r.start.z) + "," + N(r.end.x) + "," + N(r.end.z) + "]");
                    return "[" + string.Join(",", s) + "]";
                }
                var chunks = new List<string>();
                var c0 = mainscript.GetChunkPos(_gpos, 0, 0, rg.chunkSize);
                for (int dz = -1; dz <= 1; dz++)
                {
                    var c = mainscript.GetChunkPos(c0, 0, dz, rg.chunkSize);
                    chunks.Add("[" + N(c.z) + "," + (rg.chunks.ContainsKey(c) ? "true" : "false") + "," + (rg.chunksNeeded.Contains(c) ? "true" : "false") + "]");
                }
                lock (Rows)
                {
                    if (Rows.Count > 5000) Rows.RemoveRange(0, 1000);
                    Rows.Add(new Row { From = _gpos, To = _pos, Json = "{\"gx\":" + N(_gpos.x) + ",\"gz\":" + N(_gpos.z) + ",\"px\":" + N(_pos.x) + ",\"pz\":" + N(_pos.z) +
                        ",\"ok\":" + (__result ? "true" : "false") + ",\"align\":" + Json.Str(align.ToString()) + ",\"frame\":" + Time.frameCount +
                        ",\"indexed\":" + Roads(rg.RoadsNear(_gpos, false, align)) + ",\"offered\":" + Roads(rg.RoadsNear(_gpos, true, align)) +
                        ",\"chunks\":[" + string.Join(",", chunks) + "]}" });
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("poialign: " + e.Message); }
        }

        /// The decisions whose candidate OR result lies within r of (gx, gz).
        public static string Query(string arg)
        {
            var a = arg.Trim().Split(' ');
            if (a[0] == "on" || a[0] == "off") { On = a[0] == "on"; if (On) lock (Rows) Rows.Clear(); return "{\"on\":" + (On ? "true" : "false") + "}"; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double gx = double.Parse(a[0], ci), gz = double.Parse(a[1], ci), r = double.Parse(a[2], ci);
            var hits = new List<string>();
            lock (Rows)
                foreach (var row in Rows)
                {
                    double ax = row.From.x - gx, az = row.From.z - gz, bx = row.To.x - gx, bz = row.To.z - gz;
                    if (ax * ax + az * az <= r * r || bx * bx + bz * bz <= r * r) hits.Add(row.Json);
                }
            return "{\"on\":" + (On ? "true" : "false") + ",\"count\":" + Rows.Count + ",\"rows\":[" + string.Join(",", hits) + "]}";
        }
    }
}
