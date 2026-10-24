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
            /// A skinned mesh drawn with its bones (an animal, a body, a ragdoll): the look's bones it is skinned to, in the
            /// renderer's own order (-1: a bone that was missing; the copy's root stands in), and its root bone. Null for a
            /// mesh drawn as it is.
            public int[] Bones;
            public int RootBone = -1;
        }

        /// A bone a skinned piece is drawn with. Its pose is relative to the object's root like a piece's, and it is
        /// recorded like one (its track index follows the pieces': ReplayObject.PieceCount counts both), so an animal
        /// runs and a ragdoll falls in the replay instead of sliding along frozen in the pose it was first seen in.
        internal sealed class Bone
        {
            public Transform SourceT;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
        }

        public readonly List<Piece> Pieces = new List<Piece>();
        public readonly List<Bone> Bones = new List<Bone>();
        private Dictionary<Transform, int> _boneIndex;
        /// Pieces and bones together: the number of tracks the recording keeps for the object.
        public int Tracks => Pieces.Count + Bones.Count;
        /// The object's own lights (headlights, lamps), in a fixed order: ObjKey.Lights says which are on.
        public readonly List<Light> Lights = new List<Light>();
        public readonly List<Vector3> LightPos = new List<Vector3>();
        public readonly List<Quaternion> LightRot = new List<Quaternion>();
        public bool FromPrefab;

        /// Which of the lights are on right now (bit per light).
        internal uint LightMask()
        {
            uint mask = 0;
            for (int i = 0; i < Lights.Count; i++)
                if (Lights[i] != null && Lights[i].enabled && Lights[i].gameObject.activeInHierarchy) mask |= 1u << i;
            return mask;
        }

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
            look._boneIndex = null;
            return look;
        }

        private static void Walk(Transform t, Transform root, Quaternion inv, System.Func<Transform, bool> nested, HashSet<Renderer> skip, ReplayLook look)
        {
            if (t != root && (nested(t) || IsPlayer(t))) return;
            if (look.Tracks >= ReplayLimits.MaxPiecesPerObject) return;
            foreach (var l in t.GetComponents<Light>())
            {
                if (l == null || look.Lights.Count >= ReplayLimits.MaxLightsPerObject) continue;
                look.Lights.Add(l); look.LightPos.Add(inv * (t.position - root.position)); look.LightRot.Add(inv * t.rotation);
            }
            foreach (var r in t.GetComponents<Renderer>())
            {
                if (r == null || !r.enabled || skip.Contains(r) || look.Tracks >= ReplayLimits.MaxPiecesPerObject) continue;
                Mesh mesh = null; bool baked = false; int[] bones = null; int rootBone = -1;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    var sb = smr.bones;
                    if (!look.FromPrefab && sb != null && sb.Length > 0 && look.Tracks + 1 + sb.Length <= ReplayLimits.MaxPiecesPerObject)
                    {
                        mesh = smr.sharedMesh;
                        bones = new int[sb.Length];
                        for (int i = 0; i < sb.Length; i++) bones[i] = look.BoneOf(sb[i], root, inv);
                        rootBone = smr.rootBone != null ? look.BoneOf(smr.rootBone, root, inv) : -1;
                    }
                    else
                    {
                        try { mesh = new Mesh(); smr.BakeMesh(mesh); baked = true; } catch { mesh = null; }
                    }
                }
                if (mesh == null || ShowsLiveView(r)) continue;
                var p = new Piece
                {
                    Source = look.FromPrefab ? null : r, SourceT = look.FromPrefab ? null : t, Mesh = mesh, Materials = r.sharedMaterials,
                    Layer = r.gameObject.layer, Shadows = r.shadowCastingMode, ReceiveShadows = r.receiveShadows,
                    Pos = inv * (t.position - root.position), Rot = inv * t.rotation, Scale = baked ? Vector3.one : t.lossyScale, Baked = baked,
                    Bones = bones, RootBone = rootBone,
                };
                if (r.HasPropertyBlock()) { p.Block = new MaterialPropertyBlock(); r.GetPropertyBlock(p.Block); }
                look.Pieces.Add(p);
            }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), root, inv, nested, skip, look);
        }

        /// The look's bone for `t`, added the first time it is asked for (bones are shared by every skinned piece of the object).
        private int BoneOf(Transform t, Transform root, Quaternion inv)
        {
            if (t == null) return -1;
            if (_boneIndex == null) { _boneIndex = new Dictionary<Transform, int>(); for (int i = 0; i < Bones.Count; i++) _boneIndex[Bones[i].SourceT] = i; }
            if (_boneIndex.TryGetValue(t, out int index)) return index;
            index = Bones.Count;
            Bones.Add(new Bone { SourceT = t, Pos = inv * (t.position - root.position), Rot = inv * t.rotation, Scale = t.lossyScale });
            _boneIndex[t] = index;
            return index;
        }

        /// A player sitting in a car hangs under the car's transform. The player is drawn by the recorded body, so a
        /// car's look never takes the player's model along (a frozen driver, and the camera's own pieces in the eyes view).
        private static bool IsPlayer(Transform t) => t.GetComponent<fpscontroller>() != null || t.GetComponent<mpplayerscript>() != null;

        /// A mirror's glass shows what its own camera renders right now (a RenderTexture). A copy of it would show the
        /// live view, or a blank grey card, in the middle of the replay; the copy leaves the glass out.
        private static bool ShowsLiveView(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty("_MainTex") && m.mainTexture is RenderTexture) return true;
            return false;
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
        /// The bones skinned pieces are drawn with (ReplayLook.Bones); track index Pieces.Length + i.
        public Transform[] Bones = new Transform[0];
        public Light[] Lights = new Light[0];
        private uint _lights;
        public int Parent = int.MinValue;   // the parent the copy hangs under right now
        public bool Shown = true;
        /// How far the copy's meshes reach from its root, and each piece's mesh centre and size (the eyes view leaves
        /// out the small pieces sitting at the eyes).
        public float Reach;
        public Renderer[] Renderers;
        public Vector3[] Centers;
        public float[] Radii;

        internal static ReplayProxy Build(ReplayLook look, int index)
        {
            var proxy = new ReplayProxy { Root = new GameObject("TLDRevampReplayCopy" + index) };
            proxy.Root.hideFlags = HideFlags.HideAndDontSave;
            proxy.Pieces = new Transform[look.Pieces.Count];
            proxy.Renderers = new Renderer[look.Pieces.Count];
            proxy.Bones = new Transform[look.Bones.Count];
            for (int i = 0; i < look.Bones.Count; i++)
            {
                var bone = look.Bones[i];
                var go = new GameObject("bone") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(proxy.Root.transform, false);
                go.transform.localPosition = bone.Pos; go.transform.localRotation = bone.Rot; go.transform.localScale = bone.Scale;
                proxy.Bones[i] = go.transform;
            }
            proxy.Centers = new Vector3[look.Pieces.Count];
            proxy.Radii = new float[look.Pieces.Count];
            for (int i = 0; i < look.Pieces.Count; i++)
            {
                var p = look.Pieces[i];
                var go = new GameObject("piece") { layer = p.Layer, hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(proxy.Root.transform, false);
                go.transform.localPosition = p.Pos; go.transform.localRotation = p.Rot; go.transform.localScale = p.Scale;
                Renderer mr;
                if (p.Bones != null)
                {
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    var bones = new Transform[p.Bones.Length];
                    for (int b = 0; b < bones.Length; b++) bones[b] = p.Bones[b] >= 0 ? proxy.Bones[p.Bones[b]] : proxy.Root.transform;
                    smr.sharedMesh = p.Mesh;
                    smr.bones = bones;
                    if (p.RootBone >= 0) smr.rootBone = proxy.Bones[p.RootBone];
                    smr.updateWhenOffscreen = true;   // the bones move every frame; bounds from the bind pose would cull it
                    mr = smr;
                }
                else
                {
                    go.AddComponent<MeshFilter>().sharedMesh = p.Mesh;
                    mr = go.AddComponent<MeshRenderer>();
                }
                mr.sharedMaterials = p.Materials;
                mr.shadowCastingMode = p.Shadows; mr.receiveShadows = p.ReceiveShadows;
                if (p.Block != null) mr.SetPropertyBlock(p.Block);
                proxy.Pieces[i] = go.transform; proxy.Renderers[i] = mr;
                if (p.Mesh == null) continue;   // the game destroyed it with its object during the recording
                var b = p.Mesh.bounds;
                proxy.Centers[i] = b.center; proxy.Radii[i] = Vector3.Scale(b.extents, p.Scale).magnitude;
                proxy.Reach = Mathf.Max(proxy.Reach, (p.Pos + p.Rot * Vector3.Scale(b.center, p.Scale)).magnitude + proxy.Radii[i]);
            }
            // Lights are copied from the game's own (a look from a prefab has the prefab's), off until a key turns them on.
            proxy.Lights = new Light[look.Lights.Count];
            for (int i = 0; i < look.Lights.Count; i++)
            {
                var src = look.Lights[i];
                if (src == null) continue;
                var go = new GameObject("light") { layer = src.gameObject.layer, hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(proxy.Root.transform, false);
                go.transform.localPosition = look.LightPos[i]; go.transform.localRotation = look.LightRot[i];
                var l = go.AddComponent<Light>();
                l.type = src.type; l.color = src.color; l.intensity = src.intensity; l.range = src.range; l.spotAngle = src.spotAngle;
                l.shadows = src.shadows; l.cookie = src.cookie; l.renderMode = src.renderMode; l.cullingMask = ReplayHider.CullingMask(src);
                l.bounceIntensity = src.bounceIntensity;
                l.enabled = false;
                proxy.Lights[i] = l;
            }
            return proxy;
        }

        internal void SetLights(uint mask)
        {
            if (mask == _lights) return;
            _lights = mask;
            for (int i = 0; i < Lights.Length; i++) if (Lights[i] != null) Lights[i].enabled = (mask & (1u << i)) != 0;
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
        /// The game's lights stand in for nothing during a replay: they light nothing (culling mask 0) until the replay
        /// ends. The mask is a property the game never changes, unlike enabled, so giving it back is exact.
        private static readonly Dictionary<Light, int> Dark = new Dictionary<Light, int>();
        internal static int Count => Hidden.Count;
        internal static int DarkCount => Dark.Count;

        /// A light's own culling mask, even while it is darkened here.
        internal static int CullingMask(Light l) => Dark.TryGetValue(l, out int mask) ? mask : l.cullingMask;

        internal static void Darken(Light l)
        {
            if (l == null || Dark.ContainsKey(l)) return;
            Dark[l] = l.cullingMask;
            l.cullingMask = 0;
        }

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
            foreach (var kv in Dark) if (kv.Key != null) kv.Key.cullingMask = kv.Value;
            Dark.Clear();
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
