using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.VR.Native
{
    /// The sky in single-pass stereo.
    ///
    /// The game draws its sky (the sky dome, clouds, sun and moon on the "skystuff" layer) with its own camera before
    /// the world. Its cloud shaders were never built for single-pass instanced stereo, so in single-pass they draw into
    /// the left eye only. The sky is infinitely far away, so both eyes see the same sky anyway: here a mono camera with
    /// the sky camera's settings renders the sky into the six faces of a cube around it (two faces a frame), and the
    /// sky camera draws those six pictures on a cube around itself, in both eyes, instead of the sky objects.
    /// The build has no cube-map sky shader, so the faces are six textures drawn with the stock unlit shader.
    /// Nothing changes in multi-pass, or without the headset.
    internal static class VrSky
    {
        internal const int Size = 1536;
        private const int Layer = 31;                 // unnamed in the game; drawn for the sky camera alone

        private static readonly Quaternion[] FaceRot =
        {
            Quaternion.LookRotation(Vector3.right, Vector3.up), Quaternion.LookRotation(Vector3.left, Vector3.up),
            Quaternion.LookRotation(Vector3.up, Vector3.back), Quaternion.LookRotation(Vector3.down, Vector3.forward),
            Quaternion.LookRotation(Vector3.forward, Vector3.up), Quaternion.LookRotation(Vector3.back, Vector3.up),
        };

        private static Camera _capture;
        private static readonly RenderTexture[] _faces = new RenderTexture[6];
        private static readonly MaterialPropertyBlock[] _blocks = new MaterialPropertyBlock[6];
        private static Material _mat;
        private static Mesh _quad;
        private static Camera _sky;
        private static int _savedMask, _next;
        private static bool _applied, _noShader;
        internal static int Faces;
        internal static string Error = "";

        internal static bool Wanted =>
            VrNative.Headset && XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced;

        /// Every frame, from VrNativeRunner.LateUpdate (after the game set the sky camera's colour and FOV).
        internal static void LateTick()
        {
            var pl = VrNative.Player;
            var sky = pl != null ? pl.skyCamera : null;
            if (!Wanted || sky == null || _noShader) { Restore(); return; }
            if (_applied && sky != _sky) Restore();
            if (!_applied && !Apply(sky)) return;

            // the game turns its sky camera's layers on and off with its sky setting: what it wants is what the capture draws
            if (sky.cullingMask != 1 << Layer) { _savedMask = sky.cullingMask; sky.cullingMask = 1 << Layer; }
            _capture.cullingMask = _savedMask;
            _capture.backgroundColor = sky.backgroundColor;
            _capture.clearFlags = sky.clearFlags;
            Vector3 at = sky.transform.position;
            Render(at, _next); Render(at, (_next + 1) % 6);
            _next = (_next + 2) % 6;

            // the cube stands well inside the sky camera's far plane, around where the sky camera is
            float h = Mathf.Max(sky.nearClipPlane * 4f, sky.farClipPlane * 0.4f);
            for (int i = 0; i < 6; i++)
                Graphics.DrawMesh(_quad, Matrix4x4.TRS(at, FaceRot[i], Vector3.one * h), _mat, Layer, sky, 0, _blocks[i], false, false, false);
        }

        private static void Render(Vector3 at, int face)
        {
            _capture.transform.SetPositionAndRotation(at, FaceRot[face]);
            _capture.targetTexture = _faces[face];
            _capture.Render();
            Faces++;
        }

        private static bool Apply(Camera sky)
        {
            if (_mat == null)
            {
                var shader = Shader.Find("Unlit/Texture");
                if (shader == null) { _noShader = true; Error = "no Unlit/Texture shader in this build"; return false; }
                _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_quad == null)
            {
                // a square on z = 1, x and y from -1 to 1, seen from the centre: a 90° camera's whole picture
                _quad = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "TLDRevamp VR sky face" };
                _quad.vertices = new[] { new Vector3(-1, -1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1), new Vector3(1, -1, 1) };
                _quad.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
                _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            }
            var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.DefaultHDR;
            for (int i = 0; i < 6; i++)
            {
                if (_faces[i] == null)
                {
                    _faces[i] = new RenderTexture(Size, Size, 16, sky.allowHDR ? format : RenderTextureFormat.Default)
                    { hideFlags = HideFlags.HideAndDontSave, name = "TLDRevamp VR sky " + i, wrapMode = TextureWrapMode.Clamp };
                    _faces[i].Create();
                }
                if (_blocks[i] == null) _blocks[i] = new MaterialPropertyBlock();
                _blocks[i].SetTexture("_MainTex", _faces[i]);
            }
            if (_capture == null)
            {
                var go = new GameObject("TLDRevamp VR sky capture") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                _capture = go.AddComponent<Camera>();
            }
            _capture.CopyFrom(sky);
            _capture.enabled = false;
            _capture.stereoTargetEye = StereoTargetEyeMask.None;
            _capture.fieldOfView = 90f;
            _capture.aspect = 1f;

            _sky = sky;
            _savedMask = sky.cullingMask;
            for (int i = 0; i < 6; i++) Render(sky.transform.position, i);
            sky.cullingMask = 1 << Layer;
            _applied = true;
            return true;
        }

        internal static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            if (_sky != null && _sky.cullingMask == 1 << Layer) _sky.cullingMask = _savedMask;
            _sky = null;
        }

        internal static void AppendJson(System.Text.StringBuilder b)
        {
            b.Append("\"sky\":{\"wanted\":").Append(Wanted ? "true" : "false").Append(",\"applied\":").Append(_applied ? "true" : "false")
             .Append(",\"faces\":").Append(Faces).Append(",\"error\":").Append(Json.Str(Error)).Append('}');
        }
    }
}
