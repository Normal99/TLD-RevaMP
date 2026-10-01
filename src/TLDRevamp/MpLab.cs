using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp
{
    /// Multiplayer host load test without other players: in multiplayer the host adds every remote player's transform as
    /// a world-generation centre (syncScript → mapSettingScript.AddGenAround). This adds N fake players the same way,
    /// spread in a circle `radius` metres around the real player, each driving straight at `speed` m/s (tangentially,
    /// so they stay at that distance band). Bridge: `fakeplayers <n> [speed] [radius]`, `fakeplayers 0` removes them
    /// (the game drops generation centres whose transform is gone).
    public static class MpLab
    {
        private static readonly List<Transform> Fakes = new List<Transform>();
        private static readonly List<Vector3> Dirs = new List<Vector3>();
        private static float _speed;

        public static string Set(int n, float speed, float radius)
        {
            foreach (var t in Fakes) if (t != null) Object.Destroy(t.gameObject);
            Fakes.Clear(); Dirs.Clear();
            _speed = speed;
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
            var center = mainscript.s.player.transform.position;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / Mathf.Max(1, n);
                var go = new GameObject("TLDRevampFakePlayer" + i);
                go.transform.position = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                Fakes.Add(go.transform);
                Dirs.Add(new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a))); // tangential: circles around at that distance
                map.AddGenAround(go.transform);
            }
            return "{\"fakes\":" + Fakes.Count + ",\"genArounds\":" + map.genArounds.Count + "}";
        }

        /// Runner, once per frame.
        public static void Tick()
        {
            if (Fakes.Count == 0 || _speed == 0f) return;
            float dt = Time.deltaTime;
            for (int i = 0; i < Fakes.Count; i++)
                if (Fakes[i] != null) Fakes[i].position += Dirs[i] * _speed * dt;
        }
    }
}
