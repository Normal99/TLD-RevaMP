using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp.Replay
{
    /// What an object looks like, captured once: each renderer of the object's own (its nested items are objects of
    /// their own) with its mesh, its materials and where it sits relative to the object's root. A replay draws copies
    /// built from this, so playback never moves, reparents or re-enables anything of the game's.
    ///
    /// Poses are in the root's unscaled frame (position and rotation only); each piece keeps its own world scale.
    internal sealed class ReplayLook
    {
        internal sealed class Piece
        {
            public Renderer Source;   // the game's renderer (null when the look came from a prefab)
            public Transform SourceT;
            public Mesh Mesh;
            public Material[] Materials;
            public MaterialPropertyBlock Block;
            public int Layer;
            public ShadowCastingMode Shadows;
            public bool ReceiveShadows;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public bool Baked;   // a skinned mesh, frozen into a mesh of its own (destroyed with the look)
        }

        public readonly List<Piece> Pieces = new List<Piece>();
        public bool FromPrefab;

        /// Captures the look of `root`'s own renderers. `nested` says which child transforms start another object
        /// (their subtree belongs to that object, not this one).
        internal static ReplayLook Capture(Transform root, System.Func<Transform, bool> nested, bool fromPrefab = false)
        {
            var look = new ReplayLook { FromPrefab = fromPrefab };
            if (root == null) return look;
            var skip = new HashSet<Renderer>();
            // An LODGroup draws one level at a time; a copy of every level would draw them all on top of each other.
            foreach (var g in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = g.GetLODs();
                for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) skip.Add(r);
            }
            var inv = Quaternion.Inverse(root.rotation);
            Walk(root, root, inv, nested, skip, look);
            return look;
        }

        private static void Walk(Transform t, Transform root, Quaternion inv, System.Func<Transform, bool> nested, HashSet<Renderer> skip, ReplayLook look)
        {
            if (t != root && nested(t)) return;
            if (look.Pieces.Count >= ReplayLimits.MaxPiecesPerObject) return;
            foreach (var r in t.GetComponents<Renderer>())
            {
                if (r == null || !r.enabled || skip.Contains(r) || look.Pieces.Count >= ReplayLimits.MaxPiecesPerObject) continue;
                Mesh mesh = null; bool baked = false;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    try { mesh = new Mesh(); smr.BakeMesh(mesh); baked = true; } catch { mesh = null; }
                }
                if (mesh == null) continue;
                var p = new Piece
                {
                    Source = look.FromPrefab ? null : r, SourceT = look.FromPrefab ? null : t, Mesh = mesh, Materials = r.sharedMaterials,
                    Layer = r.gameObject.layer, Shadows = r.shadowCastingMode, ReceiveShadows = r.receiveShadows,
                    Pos = inv * (t.position - root.position), Rot = inv * t.rotation, Scale = baked ? Vector3.one : t.lossyScale, Baked = baked,
                };
                if (r.HasPropertyBlock()) { p.Block = new MaterialPropertyBlock(); r.GetPropertyBlock(p.Block); }
                look.Pieces.Add(p);
            }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), root, inv, nested, skip, look);
        }

        internal void Dispose()
        {
            foreach (var p in Pieces) if (p.Baked && p.Mesh != null) Object.Destroy(p.Mesh);
            Pieces.Clear();
        }
    }

    /// One drawn copy of a recorded object during playback.
    internal sealed class ReplayProxy
    {
        public GameObject Root;
        public Transform[] Pieces;
        public int Parent = int.MinValue;   // the parent the copy hangs under right now
        public bool Shown = true;

        internal static ReplayProxy Build(ReplayLook look, int index)
        {
            var proxy = new ReplayProxy { Root = new GameObject("TLDRevampReplayCopy" + index) };
            proxy.Root.hideFlags = HideFlags.HideAndDontSave;
            proxy.Pieces = new Transform[look.Pieces.Count];
            for (int i = 0; i < look.Pieces.Count; i++)
            {
                var p = look.Pieces[i];
                var go = new GameObject("piece") { layer = p.Layer, hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(proxy.Root.transform, false);
                go.transform.localPosition = p.Pos; go.transform.localRotation = p.Rot; go.transform.localScale = p.Scale;
                go.AddComponent<MeshFilter>().sharedMesh = p.Mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = p.Materials;
                mr.shadowCastingMode = p.Shadows; mr.receiveShadows = p.ReceiveShadows;
                if (p.Block != null) mr.SetPropertyBlock(p.Block);
                proxy.Pieces[i] = go.transform;
            }
            return proxy;
        }

        internal void Show(bool on)
        {
            if (Shown == on || Root == null) return;
            Shown = on; Root.SetActive(on);
        }

        internal void Dispose() { if (Root != null) Object.Destroy(Root); Root = null; }
    }

    /// Hides the game's own renderers while their copies are drawn, and gives back exactly what it took.
    ///
    /// It uses Renderer.forceRenderingOff, a flag separate from Renderer.enabled, so the game's own show/hide logic
    /// keeps working underneath. The tiny-part culler uses the same flag, so it is paused while a replay plays and
    /// re-evaluates every part when it resumes.
    internal static class ReplayHider
    {
        private static readonly Dictionary<Renderer, bool> Hidden = new Dictionary<Renderer, bool>();
        internal static int Count => Hidden.Count;

        internal static void Hide(Renderer r)
        {
            if (r == null || Hidden.ContainsKey(r)) return;
            Hidden[r] = r.forceRenderingOff;
            r.forceRenderingOff = true;
        }

        internal static void HideAll(Transform root)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) Hide(r);
        }

        /// Everything back as it was. Renderers the game destroyed in the meantime are skipped.
        internal static int Restore()
        {
            int restored = 0;
            foreach (var kv in Hidden)
            {
                if (kv.Key == null) continue;
                kv.Key.forceRenderingOff = kv.Value; restored++;
            }
            Hidden.Clear();
            return restored;
        }

        /// How many hidden renderers are still hidden (for the bridge's check that nothing was left behind).
        internal static int StillHidden()
        {
            int n = 0;
            foreach (var kv in Hidden) if (kv.Key != null && kv.Key.forceRenderingOff) n++;
            return n;
        }
    }
}
