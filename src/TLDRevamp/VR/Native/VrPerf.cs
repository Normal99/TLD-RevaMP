using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.VR.Native
{
    /// The headset frame rate.
    ///
    /// Three things the game does in VR cost frames for nothing the player sees:
    ///   - the desktop mirror: Unity copies the left eye to the game window every frame, and the game has no option for
    ///     it. Off by default in the headset (Settings → Revamp → VR), and the window shows nothing while it is off;
    ///   - the eye resolution: the game renders at whatever the runtime recommends, so a 150% stream scale in WiVRn is
    ///     150% more pixels per eye for the game too. The render scale here sets the game's own share of that. It sizes
    ///     the eye textures when VR starts (StartSubsystems, before the first frame makes them): resizing them in a
    ///     running session recreates the runtime's swapchains, and under Proton and WiVRn that failed one time in five
    ///     (XR_ERROR_RUNTIME_FAILURE in xrReleaseSwapchainImage, and Unity quits). A change while VR runs renders a
    ///     smaller part of the same textures instead (renderViewportScale), down to the new scale, never above the
    ///     size VR started with;
    ///   - the car mirrors: camscript.OnPreRender re-renders every visible mirror once per eye, from that eye. The two
    ///     eyes are 6 cm apart, so the second render is the same picture. Shared, each mirror renders once a frame from
    ///     the middle of the head and both eyes see that image.
    ///   - anti-aliasing: the game's Ultra preset is 8x MSAA, and at a headset's eye size (3096×3244 twice at a
    ///     150% WiVRn scale) that alone took the frame from 25 to 35 ms on the GPU. 4x is the VR default (measured on
    ///     a Quest 3 over WiVRn: 28 → 39 fps); 2x and 0 measured the same as 4x there, so 4x keeps the edges.
    ///   - single-pass stereo: the game was built for multi-pass, which culls and draws every camera once per eye. In
    ///     the open that main-thread work alone held a Quest 3 at about 48 fps; single-pass instanced draws both eyes
    ///     at once (about 64 there). The game's sky shaders don't support it: VrSky gives both eyes the same sky;
    ///   - temporal anti-aliasing: the world camera's post-processing layer runs TAA, about 3 ms a frame at headset
    ///     size, and it smears whenever the head moves. In the headset it is off (MSAA does the edges): about 75 fps.
    internal static class VrPerf
    {
        /// Settings → Revamp → VR.
        internal static bool DesktopMirror;
        internal static float RenderScale = 1f;
        internal static bool SharedMirrors = true;
        /// The most MSAA samples in the headset (0: the game's own setting, uncapped).
        internal static int MaxMsaa = 4;
        /// Single-pass instanced stereo (from the next time VR starts).
        internal static bool SinglePass = true;
        /// No temporal anti-aliasing in the headset.
        internal static bool NoTaa = true;
        private static readonly List<Behaviour> _taaLayers = new List<Behaviour>();
        private static float _taaScanAt;

        internal static int MirrorPasses, MirrorPassesSkipped;
        private static int _mirrorFrame = -1;
        private static bool _applied;
        private static float _startScale = 1f;    // the eye texture scale this VR session started with
        private static GameViewRenderMode _savedMode = GameViewRenderMode.LeftEye;
        private static int _gameMsaa = -1, _ourMsaa = -1;   // the game's MSAA while ours is applied

        // frame pacing, measured over the last second the headset ran
        private static readonly Queue<float> Frames = new Queue<float>();
        private static float _framesSum;

        /// Every frame, from VrNativeRunner.Update.
        internal static void Tick()
        {
            bool vr = VrNative.Headset;
            if (vr)
            {
                var mode = DesktopMirror ? GameViewRenderMode.LeftEye : GameViewRenderMode.None;
                if (!_applied) { _savedMode = XRSettings.gameViewRenderMode; _applied = true; }
                if (XRSettings.gameViewRenderMode != mode) XRSettings.gameViewRenderMode = mode;
                CapMsaa();
                TickTaa(true);
                float viewport = Mathf.Clamp(Scale / Mathf.Max(_startScale, 0.1f), 0.5f, 1f);
                if (!Mathf.Approximately(XRSettings.renderViewportScale, viewport)) XRSettings.renderViewportScale = viewport;
                float dt = Time.unscaledDeltaTime;
                Frames.Enqueue(dt); _framesSum += dt;
                while (_framesSum > 1f && Frames.Count > 1) _framesSum -= Frames.Dequeue();
            }
            else if (_applied)
            {
                // the headset stopped: the window gets its picture back, and the eye scale its default
                XRSettings.gameViewRenderMode = _savedMode;
                RestoreMsaa();
                TickTaa(false);
                _applied = false;
                Frames.Clear(); _framesSum = 0f;
            }
        }

        private static float Scale => Mathf.Clamp(RenderScale, 0.5f, 1f);

        /// The game sets QualitySettings.antiAliasing from its own settings (and again whenever they apply): above the
        /// cap it is lowered, and the game's value comes back when VR stops or the cap is lifted.
        private static void CapMsaa()
        {
            int cur = QualitySettings.antiAliasing;
            if (MaxMsaa <= 0) { RestoreMsaa(); return; }
            if (_ourMsaa >= 0 && cur == _ourMsaa) return;
            if (cur <= MaxMsaa) { _gameMsaa = -1; _ourMsaa = -1; return; }
            _gameMsaa = cur;
            QualitySettings.antiAliasing = MaxMsaa;
            _ourMsaa = QualitySettings.antiAliasing;
        }

        /// The game's post-processing layers that run TAA: switched to no AA while the headset is on, back after.
        private static void TickTaa(bool vr)
        {
            if (!vr || !NoTaa)
            {
                foreach (var l in _taaLayers) if (l != null) SetAa(l, "TemporalAntialiasing");
                _taaLayers.Clear();
                return;
            }
            if (Time.unscaledTime < _taaScanAt) return;
            _taaScanAt = Time.unscaledTime + 2f;
            foreach (var c in Object.FindObjectsOfType<Behaviour>())
            {
                if (c == null || c.GetType().Name != "PostProcessLayer") continue;
                var f = c.GetType().GetField("antialiasingMode");
                if (f == null || f.GetValue(c).ToString() != "TemporalAntialiasing") continue;
                SetAa(c, "None");
                if (!_taaLayers.Contains(c)) _taaLayers.Add(c);
            }
        }

        private static void SetAa(Behaviour layer, string mode)
        {
            try
            {
                var f = layer.GetType().GetField("antialiasingMode");
                if (f != null) f.SetValue(layer, System.Enum.Parse(f.FieldType, mode));
            }
            catch (System.Exception e) { VrHands.Fault("taa", e); }
        }

        /// The stereo mode is read when the loader initializes (OpenXRSettings.ApplyRenderSettings).
        [HarmonyPatch(typeof(UnityEngine.XR.Management.XRManagerSettings), nameof(UnityEngine.XR.Management.XRManagerSettings.InitializeLoaderSync))]
        private static class Stereo
        {
            private static void Prefix()
            {
                try
                {
                    var s = UnityEngine.XR.OpenXR.OpenXRSettings.Instance;
                    if (s != null) s.renderMode = SinglePass ? UnityEngine.XR.OpenXR.OpenXRSettings.RenderMode.SinglePassInstanced : UnityEngine.XR.OpenXR.OpenXRSettings.RenderMode.MultiPass;
                }
                catch (System.Exception e) { VrHands.Fault("stereo", e); }
            }
        }

        private static void RestoreMsaa()
        {
            if (_gameMsaa >= 0 && QualitySettings.antiAliasing == _ourMsaa) QualitySettings.antiAliasing = _gameMsaa;
            _gameMsaa = -1; _ourMsaa = -1;
        }

        /// VR starts (the game's SetXR and the bridge both go through the manager): size the eye textures before the
        /// first frame creates them.
        [HarmonyPatch(typeof(UnityEngine.XR.Management.XRManagerSettings), nameof(UnityEngine.XR.Management.XRManagerSettings.StartSubsystems))]
        private static class Start
        {
            private static void Postfix()
            {
                try
                {
                    _startScale = Scale;
                    CapMsaa();   // before the first frame sizes the eye textures with it
                    XRSettings.eyeTextureResolutionScale = _startScale;
                    XRSettings.renderViewportScale = 1f;
                }
                catch (System.Exception e) { VrHands.Fault("scale", e); }
            }
        }

        [HarmonyPatch(typeof(mainscript), nameof(mainscript.MirrorsUpdate), typeof(Vector3), typeof(Camera), typeof(bool))]
        private static class Mirrors
        {
            private static bool Prefix(ref Vector3 _mirrorHeadPos, Camera _cam)
            {
                if (!SharedMirrors || !VrNative.Headset || _cam == null) return true;
                int f = Time.frameCount;
                if (_mirrorFrame == f) { MirrorPassesSkipped++; return false; }   // the second eye: the image is already there
                _mirrorFrame = f;
                _mirrorHeadPos = _cam.transform.position;                         // the middle of the head, not one eye
                MirrorPasses++;
                return true;
            }
        }

        /// Measurement: every post-processing layer (Unity's post-processing stack, found by type name) on or off.
        internal static string PostEffects(bool on)
        {
            int n = 0;
            foreach (var c in Object.FindObjectsOfType<Behaviour>())
            {
                if (c == null || c.GetType().Name != "PostProcessLayer") continue;
                c.enabled = on;
                n++;
            }
            return "{\"postProcessLayers\":" + n + ",\"on\":" + (on ? "true" : "false") + "}";
        }

        /// Measurement: the post-processing layers (their anti-aliasing) and every effect in every volume's profile.
        internal static string PostList()
        {
            var b = new StringBuilder("{\"layers\":[");
            bool first = true;
            foreach (var c in Object.FindObjectsOfType<Behaviour>())
            {
                if (c == null || c.GetType().Name != "PostProcessLayer") continue;
                if (!first) b.Append(',');
                first = false;
                var aa = c.GetType().GetField("antialiasingMode")?.GetValue(c);
                var cam = c.GetComponent<Camera>();
                b.Append("{\"go\":").Append(Json.Str(c.gameObject.name)).Append(",\"camDepth\":").Append(cam != null ? VrNative.F(cam.depth) : "null")
                 .Append(",\"camOn\":").Append(cam != null && cam.isActiveAndEnabled ? "true" : "false").Append(",\"on\":").Append(c.isActiveAndEnabled ? "true" : "false")
                 .Append(",\"aa\":").Append(Json.Str(aa != null ? aa.ToString() : "?")).Append('}');
            }
            b.Append("],\"effects\":[");
            first = true;
            foreach (var e in Effects())
            {
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"profile\":").Append(Json.Str(e.Profile)).Append(",\"type\":").Append(Json.Str(e.Setting.GetType().Name))
                 .Append(",\"on\":").Append(Enabled(e.Setting) ? "true" : "false").Append('}');
            }
            return b.Append("]}").ToString();
        }

        /// Measurement: the enabled cameras that draw into the headset and the layers each draws.
        internal static string CullList()
        {
            var b = new StringBuilder("{\"cameras\":[");
            bool first = true;
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.targetTexture != null || cam.stereoTargetEye == StereoTargetEyeMask.None) continue;
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"name\":").Append(Json.Str(cam.name)).Append(",\"depth\":").Append(VrNative.F(cam.depth))
                 .Append(",\"far\":").Append(VrNative.F(cam.farClipPlane)).Append(",\"mask\":").Append(cam.cullingMask).Append(",\"layers\":[");
                bool f2 = true;
                for (int i = 0; i < 32; i++)
                {
                    if ((cam.cullingMask & (1 << i)) == 0) continue;
                    if (!f2) b.Append(',');
                    f2 = false;
                    float d = cam.layerCullDistances != null && cam.layerCullDistances.Length == 32 ? cam.layerCullDistances[i] : 0f;
                    b.Append("{\"i\":").Append(i).Append(",\"name\":").Append(Json.Str(LayerMask.LayerToName(i))).Append(",\"cull\":").Append(VrNative.F(d)).Append('}');
                }
                b.Append("]}");
            }
            return b.Append("]}").ToString();
        }

        /// Measurement: one layer off or on for the headset cameras with that name ("all": every one).
        internal static string Cull(string camName, int layer, bool on)
        {
            int n = 0;
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.targetTexture != null || (camName != "all" && cam.name != camName)) continue;
                if (on) cam.cullingMask |= 1 << layer; else cam.cullingMask &= ~(1 << layer);
                n++;
            }
            return "{\"cameras\":" + n + "}";
        }

        /// Measurement: one effect type on or off in every profile.
        internal static string PostEffect(string type, bool on)
        {
            int n = 0;
            foreach (var e in Effects())
            {
                if (e.Setting.GetType().Name != type) continue;
                var en = e.Setting.GetType().GetField("enabled")?.GetValue(e.Setting);
                en?.GetType().GetField("value")?.SetValue(en, on);
                n++;
            }
            return "{\"type\":" + Json.Str(type) + ",\"changed\":" + n + "}";
        }

        /// Measurement: one post-processing layer (by its index in `post list`) on/off, or its anti-aliasing mode.
        internal static string PostLayer(int index, string what)
        {
            int i = 0;
            foreach (var c in Object.FindObjectsOfType<Behaviour>())
            {
                if (c == null || c.GetType().Name != "PostProcessLayer") continue;
                if (i++ != index) continue;
                if (what == "on" || what == "off") c.enabled = what == "on";
                else
                {
                    var f = c.GetType().GetField("antialiasingMode");
                    if (f != null) f.SetValue(c, System.Enum.Parse(f.FieldType, what));
                }
                return "{\"layer\":" + index + ",\"go\":" + Json.Str(c.gameObject.name) + ",\"set\":" + Json.Str(what) + "}";
            }
            return "{\"error\":\"no layer " + index + "\"}";
        }

        private struct Effect { public string Profile; public object Setting; }

        private static IEnumerable<Effect> Effects()
        {
            var seen = new HashSet<int>();
            foreach (var c in Object.FindObjectsOfType<Behaviour>())
            {
                if (c == null || c.GetType().Name != "PostProcessVolume") continue;
                foreach (var f in new[] { "sharedProfile", "profile" })
                {
                    var prof = c.GetType().GetField(f)?.GetValue(c) as Object;
                    if (prof == null || !seen.Add(prof.GetInstanceID())) continue;
                    if (!(prof.GetType().GetField("settings")?.GetValue(prof) is System.Collections.IList list)) continue;
                    foreach (var st in list) if (st != null) yield return new Effect { Profile = prof.name, Setting = st };
                }
            }
        }

        private static bool Enabled(object setting)
        {
            var en = setting.GetType().GetField("enabled")?.GetValue(setting);
            return en?.GetType().GetField("value")?.GetValue(en) is bool v && v;
        }

        internal static void AppendJson(StringBuilder b)
        {
            b.Append("\"perf\":{\"desktopMirror\":").Append(DesktopMirror ? "true" : "false");
            b.Append(",\"renderScale\":").Append(VrNative.F(RenderScale)).Append(",\"startScale\":").Append(VrNative.F(_startScale));
            b.Append(",\"sharedMirrors\":").Append(SharedMirrors ? "true" : "false");
            b.Append(",\"singlePass\":").Append(SinglePass ? "true" : "false").Append(",\"noTaa\":").Append(NoTaa ? "true" : "false").Append(",\"taaLayersOff\":").Append(_taaLayers.Count);
            b.Append(",\"maxMsaa\":").Append(MaxMsaa).Append(",\"msaa\":").Append(QualitySettings.antiAliasing).Append(",\"gameMsaa\":").Append(_gameMsaa);
            b.Append(",\"mirrorPasses\":").Append(MirrorPasses).Append(",\"mirrorPassesSkipped\":").Append(MirrorPassesSkipped);
            try
            {
                b.Append(",\"gameView\":\"").Append(XRSettings.gameViewRenderMode).Append('"');
                b.Append(",\"stereo\":\"").Append(XRSettings.stereoRenderingMode).Append('"');
                b.Append(",\"eye\":[").Append(XRSettings.eyeTextureWidth).Append(',').Append(XRSettings.eyeTextureHeight).Append(']');
                b.Append(",\"eyeScale\":").Append(VrNative.F(XRSettings.eyeTextureResolutionScale));
                b.Append(",\"viewportScale\":").Append(VrNative.F(XRSettings.renderViewportScale));
                b.Append(",\"occlusionMesh\":").Append(XRSettings.useOcclusionMesh ? "true" : "false").Append(",\"occlusionScale\":").Append(VrNative.F(XRSettings.occlusionMaskScale));
                var xs = UnityEngine.XR.OpenXR.OpenXRSettings.Instance;
                if (xs != null) b.Append(",\"depthSubmission\":\"").Append(xs.depthSubmissionMode).Append('"');
                var displays = new List<XRDisplaySubsystem>();
                SubsystemManager.GetInstances(displays);
                foreach (var d in displays)
                {
                    if (d == null || !d.running) continue;
                    if (d.TryGetDisplayRefreshRate(out float hz)) b.Append(",\"refreshHz\":").Append(VrNative.F(hz));
                    if (d.TryGetAppGPUTimeLastFrame(out float gpu)) b.Append(",\"appGpuMs\":").Append(VrNative.F(gpu * 1000f));
                    if (d.TryGetCompositorGPUTimeLastFrame(out float comp)) b.Append(",\"compositorGpuMs\":").Append(VrNative.F(comp * 1000f));
                    if (d.TryGetDroppedFrameCount(out int dropped)) b.Append(",\"droppedFrames\":").Append(dropped);
                    if (d.TryGetFramePresentCount(out int presents)) b.Append(",\"framePresents\":").Append(presents);
                    if (d.TryGetMotionToPhoton(out float m2p)) b.Append(",\"motionToPhotonMs\":").Append(VrNative.F(m2p * 1000f));
                    break;
                }
            }
            catch { }
            if (Frames.Count > 0) b.Append(",\"fps\":").Append(VrNative.F(Frames.Count / Mathf.Max(_framesSum, 1e-3f)));
            b.Append(",\"targetFps\":").Append(Application.targetFrameRate).Append(",\"vSync\":").Append(QualitySettings.vSyncCount);
            b.Append('}');
        }
    }
}
