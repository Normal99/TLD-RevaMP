using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.VR.Native
{
    /// Native VR play, on top of the game's own VR code (docs/VR-NATIVE.md).
    ///
    /// The game's VR is a thin layer over flat play: the controllers feed the walk, the look and "use", the grip slides
    /// doors and levers, and every interaction ray comes from the head. This layer makes the hands the way you play:
    /// both hands grab, hold and throw items, the trigger uses what a hand points at or what it holds, A/X attaches and
    /// detaches parts, and the flat-screen camera effects are off while the headset is on.
    ///
    /// It never changes the player's saved settings: every flat-screen setting it turns off in VR is turned off only for
    /// the frame that reads it, and comes back the moment VR is off.
    internal static class VrNative
    {
        /// Settings → Revamp → VR.
        internal static bool Comfort = true;
        internal static bool Hands = true;

        private static int _frame = -1;
        private static bool _active;

        /// VR is on and the headset is really presenting (or a test drives the hands without one, see VrSim).
        internal static bool Active
        {
            get
            {
                int f = Time.frameCount;
                if (f != _frame) { _frame = f; _active = Compute(); }
                return _active;
            }
        }

        /// VR is on and a headset is presenting: the real thing, not a simulation.
        internal static bool Headset
        {
            get
            {
                try { return GameVr() && XRSettings.isDeviceActive; }
                catch { return false; }
            }
        }

        private static bool Compute()
        {
            if (VrSim.On) return true;
            return Headset;
        }

        internal static bool GameVr()
        {
            var s = settingsscript.s;
            return s != null && s.S != null && s.S.BVR;
        }

        internal static fpscontroller Player => mainscript.s != null ? mainscript.s.player : null;

        /// A menu or the pause screen owns the controllers (the game's own menu laser).
        internal static bool MenuOpen => mainscript.s == null || !mainscript.s.crsrLocked || mainscript.s.pauseMenuOpen;

        // ---- the debug bridge: `vrnative` (read) and `vrnative sim ...` (a test drives the hands)

        internal static string Command(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0 || arg == "state") return Json();
            if (arg.StartsWith("sim", StringComparison.Ordinal)) return VrSim.Command(arg.Substring(3).Trim());
            if (arg == "body") return VrBody.Json();
            if (arg.StartsWith("items", StringComparison.Ordinal)) return VrHands.ItemsJson(arg.Length > 5 ? float.Parse(arg.Substring(5).Trim(), CultureInfo.InvariantCulture) : 6f);
            if (arg == "comfort on" || arg == "comfort off") { Comfort = arg.EndsWith("on"); return Json(); }
            if (arg == "hands on" || arg == "hands off") { Hands = arg.EndsWith("on"); return Json(); }
            if (arg == "height on" || arg == "height off") { VrHeight.Enabled = arg.EndsWith("on"); return Json(); }
            if (arg == "mirror on" || arg == "mirror off") { VrPerf.DesktopMirror = arg.EndsWith("on"); return Json(); }
            if (arg == "sharedmirrors on" || arg == "sharedmirrors off") { VrPerf.SharedMirrors = arg.EndsWith("on"); return Json(); }
            if (arg == "post on" || arg == "post off") return VrPerf.PostEffects(arg.EndsWith("on"));
            if (arg == "post list") return VrPerf.PostList();
            if (arg == "singlepass on" || arg == "singlepass off") { VrPerf.SinglePass = arg.EndsWith("on"); return Json(); }
            if (arg == "taa on" || arg == "taa off") { VrPerf.NoTaa = arg.EndsWith("off"); return Json(); }
            if (arg.StartsWith("post layer ", StringComparison.Ordinal))
            {
                var pa = arg.Substring(11).Trim().Split(' ');
                return VrPerf.PostLayer(int.Parse(pa[0], CultureInfo.InvariantCulture), pa.Length > 1 ? pa[1] : "on");
            }
            if (arg.StartsWith("post fx ", StringComparison.Ordinal))
            {
                var pa = arg.Substring(8).Trim().Split(' ');
                return VrPerf.PostEffect(pa[0], pa.Length > 1 && pa[1] == "on");
            }
            if (arg.StartsWith("msaa ", StringComparison.Ordinal)) { VrPerf.MaxMsaa = int.Parse(arg.Substring(5).Trim(), CultureInfo.InvariantCulture); return Json(); }
            if (arg.StartsWith("scale ", StringComparison.Ordinal)) { VrPerf.RenderScale = float.Parse(arg.Substring(6).Trim(), CultureInfo.InvariantCulture); return Json(); }
            return "{\"error\":\"usage: vrnative [state] | body | items [radius] | comfort on|off | hands on|off | height on|off | mirror on|off | sharedmirrors on|off | scale 0.5..1 | msaa 0|2|4|8 | singlepass on|off | taa on|off | sim on|off | sim <left|right> at x y z [dir dx dy dz] | sim <left|right> <grip|trigger|primary> <down|up> | sim <left|right> to x y z\"}";
        }

        internal static string Json()
        {
            var b = new StringBuilder(512);
            b.Append("{\"active\":").Append(Active ? "true" : "false");
            b.Append(",\"headset\":").Append(Headset ? "true" : "false");
            b.Append(",\"gameVr\":").Append(GameVr() ? "true" : "false");
            b.Append(",\"comfort\":").Append(Comfort ? "true" : "false");
            b.Append(",\"hands\":").Append(Hands ? "true" : "false");
            b.Append(",\"menu\":").Append(MenuOpen ? "true" : "false");
            b.Append(",\"sim\":").Append(VrSim.On ? "true" : "false");
            b.Append(",\"comfortApplied\":").Append(VrComfort.AppliedFrames);
            b.Append(',');
            VrComfort.AppendJson(b);
            b.Append(',');
            VrHands.AppendJson(b);
            b.Append(',');
            VrPerf.AppendJson(b);
            b.Append(',');
            VrHeight.AppendJson(b);
            b.Append(',');
            VrSky.AppendJson(b);
            b.Append('}');
            return b.ToString();
        }

        internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        internal static string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
    }

    /// The hands without a headset, for tests: a test places a hand in the world and presses its buttons through the
    /// debug bridge, and everything downstream (grab, hold, throw, use, the interaction ray) runs as it does with real
    /// controllers. With the headset on, a simulated hand replaces only that hand.
    internal static class VrSim
    {
        internal static bool On;
        internal static readonly bool[] Hand = new bool[2];
        internal static readonly Vector3[] Pos = new Vector3[2];
        internal static readonly Quaternion[] Rot = { Quaternion.identity, Quaternion.identity };
        internal static readonly bool[] Trigger = new bool[2], Grip = new bool[2], Primary = new bool[2];
        // a hand moved by a test glides to its target over a few frames, so the throw sees a real speed
        internal static readonly Vector3[] Target = new Vector3[2];
        internal static readonly float[] GlideS = new float[2];

        internal static void Tick()
        {
            if (!On) return;
            for (int h = 0; h < 2; h++)
            {
                if (!Hand[h] || GlideS[h] <= 0f) continue;
                float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                float k = Mathf.Clamp01(dt / GlideS[h]);
                Pos[h] = Vector3.Lerp(Pos[h], Target[h], k);
                GlideS[h] -= dt;
                if (GlideS[h] <= 0f) Pos[h] = Target[h];
            }
        }

        internal static string Command(string arg)
        {
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) return VrNative.Json();
            if (a[0] == "on") { On = true; return VrNative.Json(); }
            if (a[0] == "off")
            {
                On = false;
                for (int i = 0; i < 2; i++) { Hand[i] = false; Trigger[i] = Grip[i] = Primary[i] = false; GlideS[i] = 0f; }
                return VrNative.Json();
            }
            int h = a[0] == "left" ? 0 : a[0] == "right" ? 1 : -1;
            if (h < 0 || a.Length < 2) return VrNative.Command("?");
            var ci = CultureInfo.InvariantCulture;
            if ((a[1] == "at" || a[1] == "to") && a.Length >= 5)
            {
                var p = new Vector3(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci));
                if (a.Length >= 9 && a[5] == "dir")
                {
                    var d = new Vector3(float.Parse(a[6], ci), float.Parse(a[7], ci), float.Parse(a[8], ci));
                    if (d.sqrMagnitude > 1e-6f) Rot[h] = Quaternion.LookRotation(d.normalized, Vector3.up);
                }
                Hand[h] = true;
                On = true;
                if (a[1] == "at") { Pos[h] = p; Target[h] = p; GlideS[h] = 0f; }
                else
                {
                    float s = 0.12f;
                    if (a.Length >= 7 && a[a.Length - 2] == "in") s = float.Parse(a[a.Length - 1], ci);
                    Target[h] = p; GlideS[h] = Mathf.Max(0.02f, s);
                }
                return VrNative.Json();
            }
            if (a.Length >= 3 && (a[2] == "down" || a[2] == "up"))
            {
                bool v = a[2] == "down";
                if (a[1] == "grip") Grip[h] = v;
                else if (a[1] == "trigger") Trigger[h] = v;
                else if (a[1] == "primary") Primary[h] = v;
                else return VrNative.Command("?");
                Hand[h] = true;
                On = true;
                return VrNative.Json();
            }
            return VrNative.Command("?");
        }
    }

    /// The per-frame parts that run after the game's own Update: the camera height, then the laser from the hand.
    internal sealed class VrNativeRunner : MonoBehaviour
    {
        private void Update() { try { VrSim.Tick(); } catch { } try { VrPerf.Tick(); } catch (Exception e) { VrHands.Fault("perf", e); } }
        private void LateUpdate()
        {
            try { VrHeight.LateTick(); } catch (Exception e) { VrHands.Fault("height", e); }
            try { VrSky.LateTick(); } catch (Exception e) { VrSky.Error = e.GetType().Name + ": " + e.Message; VrHands.Fault("sky", e); }
            try { VrHands.LateTick(); } catch (Exception e) { VrHands.Fault("late", e); }
        }
    }
}
