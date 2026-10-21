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
    ///     150% more pixels per eye for the game too. The render scale here sets the game's own share of that;
    ///   - the car mirrors: camscript.OnPreRender re-renders every visible mirror once per eye, from that eye. The two
    ///     eyes are 6 cm apart, so the second render is the same picture. Shared, each mirror renders once a frame from
    ///     the middle of the head and both eyes see that image.
    internal static class VrPerf
    {
        /// Settings → Revamp → VR.
        internal static bool DesktopMirror;
        internal static float RenderScale = 1f;
        internal static bool SharedMirrors = true;

        internal static int MirrorPasses, MirrorPassesSkipped;
        private static int _mirrorFrame = -1;
        private static bool _applied;
        private static float _appliedScale = -1f;
        private static GameViewRenderMode _savedMode = GameViewRenderMode.LeftEye;

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
                float scale = Mathf.Clamp(RenderScale, 0.5f, 1.5f);
                if (!Mathf.Approximately(_appliedScale, scale))
                {
                    XRSettings.eyeTextureResolutionScale = scale;
                    _appliedScale = scale;
                }
                float dt = Time.unscaledDeltaTime;
                Frames.Enqueue(dt); _framesSum += dt;
                while (_framesSum > 1f && Frames.Count > 1) _framesSum -= Frames.Dequeue();
            }
            else if (_applied)
            {
                // the headset stopped: the window gets its picture back, and the eye scale its default
                XRSettings.gameViewRenderMode = _savedMode;
                if (_appliedScale > 0f) XRSettings.eyeTextureResolutionScale = 1f;
                _appliedScale = -1f;
                _applied = false;
                Frames.Clear(); _framesSum = 0f;
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

        internal static void AppendJson(StringBuilder b)
        {
            b.Append("\"perf\":{\"desktopMirror\":").Append(DesktopMirror ? "true" : "false");
            b.Append(",\"renderScale\":").Append(VrNative.F(RenderScale));
            b.Append(",\"sharedMirrors\":").Append(SharedMirrors ? "true" : "false");
            b.Append(",\"mirrorPasses\":").Append(MirrorPasses).Append(",\"mirrorPassesSkipped\":").Append(MirrorPassesSkipped);
            try
            {
                b.Append(",\"gameView\":\"").Append(XRSettings.gameViewRenderMode).Append('"');
                b.Append(",\"stereo\":\"").Append(XRSettings.stereoRenderingMode).Append('"');
                b.Append(",\"eye\":[").Append(XRSettings.eyeTextureWidth).Append(',').Append(XRSettings.eyeTextureHeight).Append(']');
                b.Append(",\"eyeScale\":").Append(VrNative.F(XRSettings.eyeTextureResolutionScale));
                b.Append(",\"viewportScale\":").Append(VrNative.F(XRSettings.renderViewportScale));
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
