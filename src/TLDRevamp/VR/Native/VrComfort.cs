using System;
using System.Reflection;
using HarmonyLib;

namespace TLDRevamp.VR.Native
{
    /// The flat-screen camera effects, off while the headset is on.
    ///
    /// In VR the game parents the camera rig to the head-lean transform, so everything that moves that transform for
    /// the flat camera moves the player's whole view: the walking head bob (TStandHeadPos), the driving head bob
    /// (THeadBob). The game only offers them as settings the player has to remember to switch off. Here they read as
    /// off for exactly the frames the game reads them while VR runs, so the saved settings are never touched and flat
    /// play keeps them.
    ///
    /// Motion blur and depth of field are screen-space effects made for a monitor: in a headset they smear the world
    /// when you turn your head and blur what you look at. They are off while VR runs and come back when it stops.
    internal static class VrComfort
    {
        internal static int AppliedFrames;

        private static bool _savedWalk, _savedDrive, _swapped;

        [HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class HeadBob
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                _swapped = false;
                if (!VrNative.Comfort || !VrNative.Headset) return;
                var s = settingsscript.s;
                if (s == null || s.S == null) return;
                _savedWalk = s.S.BWalkHeadBob;
                _savedDrive = s.S.BDriveHeadBob;
                s.S.BWalkHeadBob = false;
                s.S.BDriveHeadBob = false;
                _swapped = true;
                AppliedFrames++;
            }

            // a finalizer, so the player's settings come back even when the game's Update throws
            private static Exception Finalizer(Exception __exception)
            {
                if (_swapped)
                {
                    var s = settingsscript.s;
                    if (s != null && s.S != null) { s.S.BWalkHeadBob = _savedWalk; s.S.BDriveHeadBob = _savedDrive; }
                    _swapped = false;
                }
                return __exception;
            }
        }

        // ---- post-processing: the game's own effect objects (Unity's post-processing stack), read by reflection so the
        // build needs no reference to that assembly

        private static FieldInfo _dof, _mblur, _profileP;
        private static bool _looked, _blurOff;

        [HarmonyPatch(typeof(postprocesscontrol), "Update")]
        private static class PostProcessing
        {
            private static void Postfix(postprocesscontrol __instance)
            {
                if (__instance == null) return;
                bool vr = VrNative.Comfort && VrNative.Headset;
                if (!vr && !_blurOff) return;
                if (!_looked)
                {
                    _looked = true;
                    var t = typeof(postprocesscontrol);
                    _dof = AccessTools.Field(t, "dof");
                    _mblur = AccessTools.Field(t, "mblur");
                    _profileP = AccessTools.Field(t, "P");
                }
                if (vr)
                {
                    SetEnabled(_dof?.GetValue(__instance), false);    // the game sets it from the setting every frame
                    SetEnabled(_mblur?.GetValue(__instance), false);
                    SetEnabled(ProfileBlur(__instance), false);
                    _blurOff = true;
                }
                else
                {
                    // VR stopped: motion blur back on (the game keeps it on outside screenshots; depth of field it
                    // sets again from the player's setting on its own)
                    SetEnabled(_mblur?.GetValue(__instance), true);
                    SetEnabled(ProfileBlur(__instance), true);
                    _blurOff = false;
                }
            }
        }

        /// What the player sees now: the saved head bob settings, and whether the blur effects are on.
        internal static void AppendJson(System.Text.StringBuilder b)
        {
            var s = settingsscript.s;
            b.Append("\"walkHeadBobSetting\":").Append(s != null && s.S != null && s.S.BWalkHeadBob ? "true" : "false");
            b.Append(",\"driveHeadBobSetting\":").Append(s != null && s.S != null && s.S.BDriveHeadBob ? "true" : "false");
            var pl = VrNative.Player;
            var pc = pl != null ? pl.ppc : null;
            if (pc != null && _looked)
            {
                b.Append(",\"depthOfField\":").Append(Enabled(_dof?.GetValue(pc)));
                b.Append(",\"motionBlur\":").Append(Enabled(_mblur?.GetValue(pc)));
                b.Append(",\"motionBlurP\":").Append(Enabled(ProfileBlur(pc)));
            }
        }

        private static string Enabled(object effect)
        {
            if (effect == null) return "null";
            try
            {
                var p = effect.GetType().GetField("enabled")?.GetValue(effect);
                var v = p?.GetType().GetField("value")?.GetValue(p);
                return v is bool on ? (on ? "true" : "false") : "null";
            }
            catch { return "null"; }
        }

        private static object ProfileBlur(postprocesscontrol pc)
        {
            try
            {
                var profile = _profileP?.GetValue(pc);
                if (profile == null) return null;
                var settings = profile.GetType().GetField("settings")?.GetValue(profile) as System.Collections.IList;
                if (settings == null) return null;
                foreach (var s in settings)
                    if (s != null && s.GetType().Name == "MotionBlur") return s;
            }
            catch { }
            return null;
        }

        private static void SetEnabled(object effect, bool on)
        {
            if (effect == null) return;
            try
            {
                var p = effect.GetType().GetField("enabled")?.GetValue(effect);
                var f = p?.GetType().GetField("value");
                if (f != null && !Equals(f.GetValue(p), on)) f.SetValue(p, on);
            }
            catch { }
        }
    }
}
