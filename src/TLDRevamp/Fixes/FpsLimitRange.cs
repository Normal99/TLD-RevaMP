using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The game's FPS limit slider (Settings → Graphics; settingsscript tabGraphics.sliders[2], read into S.FFPSLimit and
    /// used as Application.targetFrameRate) stops well below what high-refresh monitors run at. Widened to 0-1000 before
    /// the menu loads the saved value into it (RefreshMenu, from settingsscript.FStart), so a saved value above the old
    /// maximum is kept, not clamped. 0 is no limit: Unity treats a target of 0 or less as the platform default.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FpsLimitRange.Enabled
    [HarmonyPatch(typeof(settingsscript), nameof(settingsscript.RefreshMenu))]
    public static class FpsLimitRange
    {
        public static bool Enabled = true;
        public static float Min = 0f, Max = 1000f;
        private static bool _logged;

        [HarmonyPrefix]
        private static void Prefix(settingsscript __instance)
        {
            if (!Enabled) return;
            var sliders = __instance.tabGraphics != null ? __instance.tabGraphics.sliders : null;
            if (sliders == null || sliders.Length <= 2 || sliders[2] == null) return;
            var s = sliders[2];
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"FPS limit slider '{s.name}': slider {s.sliderMinValue}..{s.sliderMaxValue} (half {s.sliderHalfValue}, useHalf {s.useHalf}), " +
                                   $"value {s.minValue}..{s.maxValue}, default {s.defValue}, x{s.valueMultipler} -> {Min}..{Max}");
            }
            if (s.valueMultipler != 0f && s.valueMultipler != 1f) return;   // a scaled slider: not the one this was written for
            if (s.minValue > Min) s.minValue = Min;
            if (s.maxValue < Max) s.maxValue = Max;
            if (s.sliderMinValue > Min || s.sliderMaxValue < Max)
            {
                s.sliderMinValue = System.Math.Min(s.sliderMinValue, Min);
                s.sliderMaxValue = System.Math.Max(s.sliderMaxValue, Max);
                if (s.slider != null) { s.slider.minValue = s.sliderMinValue; s.slider.maxValue = s.sliderMaxValue; }
            }
        }
    }
}
