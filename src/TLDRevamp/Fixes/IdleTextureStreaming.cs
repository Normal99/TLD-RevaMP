using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's quality settings switch Unity's mipmap streaming on, but none of its textures stream (spawn: 0 streaming,
    /// 1,088 not). The streaming manager still runs every frame (EarlyUpdate.UpdateTextureStreamingManager 0.157 ms at
    /// spawn, 0.001 ms with it off). Off while no loaded texture streams, checked once a second; on again (the game's own
    /// value) as soon as one does, so a streaming texture is always handled as the game would. Non-streaming textures
    /// are always loaded in full either way.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.IdleTextureStreaming.Enabled; `call ...Scan` counts the loaded
    /// textures that have streaming mipmaps.
    public static class IdleTextureStreaming
    {
        /// On by default: spawn, in-place A/B 6 alternating rounds × 10 s: 119.5 → 122.0 fps, p50 8.37 → 8.20 ms.
        public static bool Enabled = true;
        public static long Offs, Ons;
        private static bool _offByUs;
        private static float _checkAt;

        public static void Tick()
        {
            if (!Enabled)
            {
                if (_offByUs) { QualitySettings.streamingMipmapsActive = true; _offByUs = false; Ons++; }
                return;
            }
            if (Time.unscaledTime < _checkAt) return;
            _checkAt = Time.unscaledTime + 1f;
            bool streaming = Texture.streamingTextureCount > 0;
            if (_offByUs)
            {
                if (QualitySettings.streamingMipmapsActive) _offByUs = false;   // the game switched it on again (a quality change)
                else if (streaming) { QualitySettings.streamingMipmapsActive = true; _offByUs = false; Ons++; return; }
            }
            if (!_offByUs && QualitySettings.streamingMipmapsActive && !streaming)
            {
                QualitySettings.streamingMipmapsActive = false;
                _offByUs = true;
                Offs++;
            }
        }

        public static string Scan()
        {
            int all = 0, streams = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) { all++; if (t.streamingMipmaps) streams++; }
            return "{\"texture2d\":" + all + ",\"withStreamingMipmaps\":" + streams + ",\"streamingTextureCount\":" + Texture.streamingTextureCount +
                   ",\"active\":" + (QualitySettings.streamingMipmapsActive ? "true" : "false") + ",\"offByUs\":" + (_offByUs ? "true" : "false") +
                   ",\"offs\":" + Offs + ",\"ons\":" + Ons + "}";
        }
    }
}
