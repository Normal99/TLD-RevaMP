using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace RoadEnd
{
    /// Test hooks for TLD Revamp's debug bridge (`call RoadEnd.Probe.State`), read by tools/roadend.py.
    public static class Probe
    {
        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        static string V(Vector3d v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
        static string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";

        /// Where the end is, the grave and the note, and what the generators hold past the end.
        public static string State()
        {
            var sb = new StringBuilder("{");
            var end = EndPlan.Current;
            sb.Append("\"enabled\":").Append(Plugin.Enabled ? "true" : "false");
            sb.Append(",\"active\":").Append(World.Active ? "true" : "false").Append(",\"source\":\"").Append(World.Source).Append('"');
            sb.Append(",\"chosen\":").Append(World.ChosenForNewGame ? "true" : "false");
            sb.Append(",\"sign\":").Append(World.Sign);
            sb.Append(",\"configuredKm\":").Append(F(World.Km));
            sb.Append(",\"cut\":").Append(F(end.Cut)).Append(",\"cutKnown\":").Append(end.Known ? "true" : "false");
            sb.Append(",\"roadEnd\":").Append(end.HasRoadEnd ? V(end.RoadEnd) : "null");
            sb.Append(",\"trimmedRoads\":").Append(Roads.Trimmed).Append(",\"droppedRoads\":").Append(Roads.Dropped);
            var p = mainscript.s != null && mainscript.s.player != null ? mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position) : Vector3d.zero;
            sb.Append(",\"player\":").Append(V(p));
            sb.Append(",\"grave\":").Append(Grave.Instance != null ? V(mainscript.GlobalFromUnityPos(Grave.Instance.transform.position)) : "null");
            sb.Append(",\"note\":").Append(Grave.NoteObject != null ? V(mainscript.GlobalFromUnityPos(Grave.NoteObject.transform.position)) : "null");
            // past the end line: main road pieces (must be none), side road pieces and terrain tiles (must go on)
            int main = 0, side = 0, tiles = 0, near = 0;
            foreach (var r in Object.FindObjectsOfType<roadscript>())
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool past = EndPlan.U(mainscript.GlobalFromUnityPos(r.transform.position)) > end.Cut + 5;
                if (!past) continue;
                var rg = r.roadgen;
                bool isMain = rg != null && rg.chunks.TryGetValue(r.chunk, out var ch) && r.roadID < ch.roads.Count && ch.roads[r.roadID].roadType == roadGenScript.roadTypeEmu.mainRoad;
                if (isMain) main++; else side++;
            }
            foreach (var t in Object.FindObjectsOfType<terrainGenScript>())
                if (t.terrains != null) foreach (var k in t.terrains.Keys) if (EndPlan.U(k) > end.Cut + t.tileSize) tiles++;
            foreach (var poi in Object.FindObjectsOfType<poiScript>())
                if (end.HasRoadEnd && poi.gameObject.activeInHierarchy && poi.gameObject != Grave.Instance && (mainscript.GlobalFromUnityPos(poi.transform.position) - end.RoadEnd).magnitude < Plugin.BuildingClearance) near++;
            sb.Append(",\"past\":{\"mainRoad\":").Append(main).Append(",\"sideRoad\":").Append(side).Append(",\"tiles\":").Append(tiles).Append("},\"buildingsNearGrave\":").Append(near);
            return sb.Append("}").ToString();
        }

        /// Pick the end of the road for the next new game, as the new-game screen does (tests).
        public static string Choose(bool on) { NewGameUi.Choose(on); return World.ChosenForNewGame ? "on" : "off"; }

        /// Would a save of the world with this seed load with an end (the list next to the saves)?
        public static bool SavedWithEnd(int seed) => World.Has(seed);

        /// The new-game screen's row: is it there, what does it show.
        public static string NewGameRow() => NewGameUi.Status();

        /// The game's generators and their sizes (exploration).
        public static string Generators()
        {
            var sb = new StringBuilder();
            foreach (var t in Object.FindObjectsOfType<terrainGenScript>())
                sb.Append($"terrain {t.name} distant {t.isDistantTerrain} tile {t.tileSize} draw {t.drawDistance} tiles {(t.terrains != null ? t.terrains.Count : -1)} | ");
            foreach (var r in Object.FindObjectsOfType<roadGenScript>())
                sb.Append($"road {r.name} chunk {r.chunkSize} inner {r.innerChunkSize} main {r.hasMainRoad} side {r.hasSideRoad} margin {r.marginPercent} dots {r.minDotCount}-{r.maxDotCount} chunks {r.chunks.Count} | ");
            foreach (var g in Object.FindObjectsOfType<poiGenScript>()) sb.Append($"poi {g.name} chunk {g.chunkSize} | ");
            foreach (var g in Object.FindObjectsOfType<objGenScript>()) sb.Append($"obj {g.name} | ");
            foreach (var m in Object.FindObjectsOfType<MonoBehaviour>())
            {
                string n = m.GetType().Name;
                if (n.Contains("RandomMapGen") || n.Contains("FarTerrain") || n.Contains("dungeon")) sb.Append($"{n} on {m.name} | ");
            }
            return sb.ToString();
        }

        /// A prefab's hierarchy: names, components, renderer bounds (exploration). which: poi id or "item:N".
        public static string Prefab(string which)
        {
            GameObject go = which.StartsWith("item:") ? itemdatabase.s.items[int.Parse(which.Substring(5))] : itemdatabase.s.Building(int.Parse(which));
            if (go == null) return "null";
            var sb = new StringBuilder();
            Walk(go.transform, 0, sb);
            return sb.ToString();
        }

        static void Walk(Transform t, int depth, StringBuilder sb)
        {
            if (sb.Length > 6000) return;
            sb.Append(depth).Append(':').Append(t.name).Append(" lp ").Append(V(t.localPosition)).Append(" le ").Append(V(t.localEulerAngles)).Append(" ls ").Append(V(t.localScale)).Append(" {");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c is Transform) continue;
                sb.Append(c.GetType().Name);
                if (c is MeshFilter mf && mf.sharedMesh != null) sb.Append("=").Append(mf.sharedMesh.name).Append(V(mf.sharedMesh.bounds.size));
                if (c is Renderer rr && rr.sharedMaterial != null) sb.Append("=").Append(rr.sharedMaterial.name).Append("/").Append(rr.sharedMaterial.shader.name);
                sb.Append(' ');
            }
            sb.Append("} | ");
            foreach (Transform c in t) Walk(c, depth + 1, sb);
        }

        /// Where the player starts and faces, and where the main road is around a global z (exploration).
        public static string Start()
        {
            var ms = Object.FindObjectOfType<mapSettingScript>();
            var sb = new StringBuilder();
            if (ms != null && ms.singlePlayerStart != null)
                sb.Append("start ").Append(V(mainscript.GlobalFromUnityPos(ms.singlePlayerStart.position))).Append(" fwd ").Append(V(ms.singlePlayerStart.forward)).Append(' ');
            if (mainscript.s.player != null) sb.Append("player fwd ").Append(V(mainscript.s.player.transform.forward)).Append(" cam fwd ").Append(V(Camera.main != null ? Camera.main.transform.forward : Vector3.zero));
            return sb.ToString();
        }
    }
}
