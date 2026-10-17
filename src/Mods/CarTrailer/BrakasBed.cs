using System.Collections.Generic;
using UnityEngine;

namespace CarTrailer
{
    /// Pieces of another item's prefab copied onto the trailer: the Brakas transporter's bed (its meshes, cut out of
    /// the body where the body is one mesh with the cab, and its own colliders: wheel tracks, rails, frame, wheel chocks)
    /// and whole objects with their scripts (the hinged ramp).
    public static class BrakasBed
    {
        public sealed class Result
        {
            public int Whole, Cut, Colliders, CutTris;
            public List<string> Skipped = new List<string>();
        }

        /// Copies everything of `source` inside `region` (source space) under `parent`, moved by `shift` (source space →
        /// the parent's). Objects under `skip` (copied whole elsewhere) and wheels/removable parts are left out.
        public static Result CopyRegion(GameObject source, Bounds region, Matrix4x4 shift, Transform parent, Transform skip)
        {
            var r = new Result();
            var toS = source.transform.worldToLocalMatrix;
            foreach (var mf in source.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh; var mr = mf.GetComponent<MeshRenderer>();
                if (mesh == null || mr == null || Excluded(mf.transform, source.transform, skip)) continue;
                var m = toS * mf.transform.localToWorldMatrix;
                int inside = CornersInside(mesh.bounds, m, region);
                if (inside == 0 && !Overlaps(mesh.bounds, m, region)) continue;
                if (inside == 8)
                {
                    var go = new GameObject(mf.name);
                    go.transform.SetParent(parent, false);
                    Place(go.transform, shift * m);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                    go.layer = mf.gameObject.layer;
                    r.Whole++;
                    continue;
                }
                if (!mesh.isReadable) { r.Skipped.Add(mf.name + " (" + mesh.name + ", not readable)"); continue; }
                var cut = CutMesh(mesh, m, region, out int tris, FrontFaceBand);
                if (cut == null) continue;
                var cg = new GameObject(mf.name + "_cut");
                cg.transform.SetParent(parent, false);
                Place(cg.transform, shift);
                cg.AddComponent<MeshFilter>().sharedMesh = cut;
                cg.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                cg.layer = mf.gameObject.layer;
                r.Cut++; r.CutTris += tris;
            }

            // its solid parts: box colliders wholly inside (not storage areas, triggers or the things that are used)
            foreach (var bc in source.GetComponentsInChildren<BoxCollider>(true))
            {
                if (bc.isTrigger || bc.GetComponent<mountStuff>() != null || bc.GetComponent<usablescript>() != null || Excluded(bc.transform, source.transform, skip)) continue;
                if (bc.GetComponentInParent<mountStuff>() != null) continue;
                var m = toS * bc.transform.localToWorldMatrix;
                if (CornersInside(new Bounds(bc.center, bc.size), m, region) < 8) continue;
                var go = new GameObject(bc.name + "_col");
                go.transform.SetParent(parent, false);
                Place(go.transform, shift * m);
                var c = go.AddComponent<BoxCollider>(); c.center = bc.center; c.size = bc.size; c.sharedMaterial = bc.sharedMaterial;
                go.layer = bc.gameObject.layer;
                r.Colliders++;
            }
            return r;
        }

        /// A whole object of `source` (scripts and all) under `parent`, where `shift` puts it.
        public static GameObject CopyWhole(GameObject source, Transform t, Matrix4x4 shift, Transform parent)
        {
            var m = source.transform.worldToLocalMatrix * t.localToWorldMatrix;
            var go = Object.Instantiate(t.gameObject, parent, false);
            go.name = t.name;
            Place(go.transform, shift * m);
            return go;
        }

        /// Copies of `set` (objects under `root` that refer to each other, e.g. a wheel's collider, its looks and its
        /// slot): cloned together, so the copies refer to each other as the originals do.
        public static List<Transform> CloneTogether(Transform root, IList<Transform> set)
        {
            var tmp = new GameObject("tmp").transform; tmp.SetParent(root, false);
            var parents = new List<Transform>();
            foreach (var t in set) { parents.Add(t.parent); t.SetParent(tmp, true); }
            var copy = Object.Instantiate(tmp.gameObject, root, false).transform;
            var res = new List<Transform>();
            for (int i = 0; i < set.Count; i++) res.Add(copy.GetChild(i));
            for (int i = 0; i < set.Count; i++) { set[i].SetParent(parents[i], true); res[i].SetParent(parents[i], true); }
            Object.DestroyImmediate(tmp.gameObject); Object.DestroyImmediate(copy.gameObject);
            return res;
        }

        /// Names left out wherever they are (the Brakas's exhaust runs under its bed).
        public static string[] SkipNames = { "muffler" };

        private static bool Excluded(Transform t, Transform root, Transform skip)
        {
            foreach (var n in SkipNames) if (t.name.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (var p = t; p != null && p != root; p = p.parent)
                if (p == skip || !p.gameObject.activeSelf || p.GetComponent<mountStuff>() != null || (p.parent == root && p.name == "Slots") || p.GetComponent<wheelgraphicsscript>() != null || p.GetComponent<WheelCollider>() != null || p.GetComponent<attachablescript>() != null
                    || p.GetComponent<partslotscript>() != null || p.GetComponent<seatscript>() != null || p.GetComponent<Camera>() != null)
                    return true;
            return false;
        }

        private static int CornersInside(Bounds b, Matrix4x4 m, Bounds region)
        {
            int n = 0;
            for (int i = 0; i < 8; i++) if (region.Contains(m.MultiplyPoint3x4(Corner(b, i)))) n++;
            return n;
        }

        private static bool Overlaps(Bounds b, Matrix4x4 m, Bounds region)
        {
            var w = new Bounds(m.MultiplyPoint3x4(Corner(b, 0)), Vector3.zero);
            for (int i = 1; i < 8; i++) w.Encapsulate(m.MultiplyPoint3x4(Corner(b, i)));
            return w.Intersects(region);
        }

        private static Vector3 Corner(Bounds b, int i) =>
            b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));

        /// The triangles of `mesh` (placed by m) wholly inside `region`, as a new mesh in the region's space.
        /// Cut triangles this close to the region's front (+z) also get a back face: the bed's front wall was the cab's
        /// back wall, whose front side is inside the cab and was never modelled (seen from the tow car: no face).
        public static float FrontFaceBand = 0.15f;

        private static Mesh CutMesh(Mesh mesh, Matrix4x4 m, Bounds region, out int tris, float frontBand)
        {
            tris = 0;
            var v = mesh.vertices; var n = mesh.normals; var uv = mesh.uv;
            var nm = m.inverse.transpose;
            var map = new Dictionary<int, int>();
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nuv = new List<Vector2>();
            var subs = new List<int[]>();
            var inside = new bool[v.Length]; var pv = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) { pv[i] = m.MultiplyPoint3x4(v[i]); inside[i] = region.Contains(pv[i]); }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var src = mesh.GetTriangles(s); var dst = new List<int>();
                for (int t = 0; t + 2 < src.Length; t += 3)
                {
                    if (!inside[src[t]] || !inside[src[t + 1]] || !inside[src[t + 2]]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int i = src[t + k];
                        if (!map.TryGetValue(i, out int j))
                        {
                            j = nv.Count; map[i] = j; nv.Add(pv[i]);
                            nn.Add(n != null && n.Length == v.Length ? nm.MultiplyVector(n[i]).normalized : Vector3.up);
                            nuv.Add(uv != null && uv.Length == v.Length ? uv[i] : Vector2.zero);
                        }
                        dst.Add(j);
                    }
                    tris++;
                    float zf = region.max.z - frontBand;
                    if (frontBand > 0f && pv[src[t]].z > zf && pv[src[t + 1]].z > zf && pv[src[t + 2]].z > zf)
                    {
                        // the back face: the same corners, reversed, normals flipped (own vertices)
                        for (int k = 2; k >= 0; k--)
                        {
                            int i = src[t + k];
                            dst.Add(nv.Count); nv.Add(pv[i]);
                            nn.Add(-(n != null && n.Length == v.Length ? nm.MultiplyVector(n[i]).normalized : Vector3.up));
                            nuv.Add(uv != null && uv.Length == v.Length ? uv[i] : Vector2.zero);
                        }
                    }
                }
                subs.Add(dst.ToArray());
            }
            if (tris == 0) return null;
            var o = new Mesh { name = mesh.name + "_cut" };
            if (nv.Count > 65000) o.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            o.SetVertices(nv); o.SetNormals(nn); o.SetUVs(0, nuv);
            o.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) o.SetTriangles(subs[s], s);
            o.RecalculateBounds();
            return o;
        }

        public static void Place(Transform t, Matrix4x4 m)
        {
            t.localPosition = m.GetColumn(3);
            t.localRotation = m.rotation;
            t.localScale = m.lossyScale;
        }
    }
}
