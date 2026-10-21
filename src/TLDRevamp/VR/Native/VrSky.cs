using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.VR.Native
{
    /// The sky in single-pass stereo.
    ///
    /// The game draws its sky (the sky dome, clouds, sun and moon on the "skystuff" layer) with its own camera before
    /// the world. Its cloud shaders were never built for single-pass instanced stereo, so in single-pass they draw into
    /// the left eye only. The sky is infinitely far away, so both eyes see the same sky anyway: here a mono camera with
    /// the sky camera's settings renders the sky into a cube map (two faces a frame, a whole cube every third frame),
    /// and the sky camera draws that cube map as its skybox in both eyes instead of the sky objects.
    /// Nothing changes in multi-pass, or without the headset.
    internal static class VrSky
    {
        internal const int Size = 1024;

        private static Camera _capture;
        private static RenderTexture _cube;
        private static Material _mat;
        private static Camera _sky;
        private static int _savedMask;
        private static CameraClearFlags _savedClear;
        private static Skybox _skybox;
        private static Material _savedSkyboxMat;
        private static bool _skyboxAdded, _savedSkyboxOn, _applied, _noShader;
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

            // the capture camera follows the sky camera's settings the game changes (its colour) and stands where it does
            _capture.backgroundColor = sky.backgroundColor;
            _capture.transform.SetPositionAndRotation(sky.transform.position, Quaternion.identity);
            int mask = (Time.frameCount % 3) switch { 0 => 3, 1 => 12, _ => 48 };
            if (_capture.RenderToCubemap(_cube, mask)) Faces += 2;
            // the game turns its sky camera on and off with its sky setting: the cube map stays its only content
            if (sky.cullingMask != 0) { _savedMask = sky.cullingMask; sky.cullingMask = 0; }
            sky.clearFlags = CameraClearFlags.Skybox;
        }

        private static bool Apply(Camera sky)
        {
            if (_mat == null)
            {
                var shader = Shader.Find("Skybox/Cubemap");
                if (shader == null) { _noShader = true; Error = "no Skybox/Cubemap shader in this build"; return false; }
                _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_cube == null)
            {
                _cube = new RenderTexture(Size, Size, 16, sky.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default)
                { dimension = UnityEngine.Rendering.TextureDimension.Cube, hideFlags = HideFlags.HideAndDontSave, name = "TLDRevamp VR sky" };
                _cube.Create();
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
            _capture.targetTexture = null;
            _capture.RenderToCubemap(_cube, 63);
            _mat.SetTexture("_Tex", _cube);

            _sky = sky;
            _savedMask = sky.cullingMask;
            _savedClear = sky.clearFlags;
            _skybox = sky.GetComponent<Skybox>();
            _skyboxAdded = _skybox == null;
            if (_skyboxAdded) _skybox = sky.gameObject.AddComponent<Skybox>();
            else { _savedSkyboxMat = _skybox.material; _savedSkyboxOn = _skybox.enabled; }
            _skybox.material = _mat;
            _skybox.enabled = true;
            sky.cullingMask = 0;
            sky.clearFlags = CameraClearFlags.Skybox;
            _applied = true;
            return true;
        }

        internal static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            if (_sky != null)
            {
                _sky.cullingMask = _savedMask;
                _sky.clearFlags = _savedClear;
                if (_skybox != null)
                {
                    if (_skyboxAdded) Object.Destroy(_skybox);
                    else { _skybox.material = _savedSkyboxMat; _skybox.enabled = _savedSkyboxOn; }
                }
            }
            _sky = null;
            _skybox = null;
        }

        internal static void AppendJson(System.Text.StringBuilder b)
        {
            b.Append("\"sky\":{\"wanted\":").Append(Wanted ? "true" : "false").Append(",\"applied\":").Append(_applied ? "true" : "false")
             .Append(",\"faces\":").Append(Faces).Append(",\"error\":").Append(Json.Str(Error)).Append('}');
        }
    }
}
