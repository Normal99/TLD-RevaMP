using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp.Net
{
    /// What other players' cars cost here (architecture §9a, 10+ players): every player's car is a full copy on every
    /// machine (~1,000 objects, no LOD groups, its lamps casting shadows). CopyCounts (bridge `mp copycost`) counts what
    /// the copies have live; CopyFarShadowsM (0 = off) switches shadows off on car copies farther than that from this
    /// machine's camera, their renderers' and their lamps' (restored when they come nearer), to measure the lever
    /// (tools/carcopies.py). Only copies: the player's own car and cars nobody else owns stay as the game made them.
    public static partial class Entities
    {
        public static float CopyFarShadowsM = 0f;
        public static bool CopyLodAllCars;   // test only: also cars that aren't copies (one machine's measurement)
        public static long CopyLodOff, CopyLodOn;
        private static float _copyLodAt;

        private sealed class LodState
        {
            public bool Off;
            public readonly List<KeyValuePair<Renderer, ShadowCastingMode>> Renderers = new List<KeyValuePair<Renderer, ShadowCastingMode>>();
            public readonly List<KeyValuePair<Light, LightShadows>> Lights = new List<KeyValuePair<Light, LightShadows>>();
        }
        private static readonly Dictionary<carscript, LodState> _lod = new Dictionary<carscript, LodState>();

        private static IEnumerable<carscript> CopyCars()
        {
            if (CopyLodAllCars) { foreach (var c in Object.FindObjectsOfType<carscript>()) yield return c; yield break; }
            foreach (var e in ByNet.Values)
                if (e.Proxy && e.Root != null && e.Root.car != null) yield return e.Root.car;
        }

        /// Other players' cars shown here (telemetry's slow-frame scene counts).
        internal static int CopyCarCount()
        {
            int n = 0;
            foreach (var e in ByNet.Values) if (e.Proxy && e.Root != null && e.Root.car != null) n++;
            return n;
        }

        /// Once a second (Mp tick): far copies without shadows, near ones as the game made them.
        internal static void CopyLodTick()
        {
            if (Time.realtimeSinceStartup - _copyLodAt < 1f) return;
            _copyLodAt = Time.realtimeSinceStartup;
            if (CopyFarShadowsM <= 0f) { if (_lod.Count > 0) CopyLodRestoreAll(); return; }
            var cam = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Cam : null;
            if (cam == null) return;
            var seen = new HashSet<carscript>();
            float far2 = CopyFarShadowsM * CopyFarShadowsM, near2 = CopyFarShadowsM * 0.8f * CopyFarShadowsM * 0.8f;
            foreach (var c in CopyCars())
            {
                if (c == null) continue;
                seen.Add(c);
                float d2 = (c.transform.position - cam.transform.position).sqrMagnitude;
                _lod.TryGetValue(c, out var st);
                if (d2 > far2 && (st == null || !st.Off)) { if (st == null) _lod[c] = st = new LodState(); ShadowsOff(c, st); }
                else if (d2 < near2 && st != null && st.Off) Restore(st);
            }
            foreach (var c in new List<carscript>(_lod.Keys)) if (c == null || !seen.Contains(c)) { if (_lod[c].Off) Restore(_lod[c]); _lod.Remove(c); }
        }

        private static void ShadowsOff(carscript c, LodState st)
        {
            st.Renderers.Clear(); st.Lights.Clear();
            foreach (var r in c.GetComponentsInChildren<Renderer>(true))
                if (r.shadowCastingMode != ShadowCastingMode.Off) { st.Renderers.Add(new KeyValuePair<Renderer, ShadowCastingMode>(r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.Off; }
            foreach (var l in c.GetComponentsInChildren<Light>(true))
                if (l.shadows != LightShadows.None) { st.Lights.Add(new KeyValuePair<Light, LightShadows>(l, l.shadows)); l.shadows = LightShadows.None; }
            st.Off = true; CopyLodOff++;
        }

        private static void Restore(LodState st)
        {
            foreach (var kv in st.Renderers) if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
            foreach (var kv in st.Lights) if (kv.Key != null) kv.Key.shadows = kv.Value;
            st.Renderers.Clear(); st.Lights.Clear(); st.Off = false; CopyLodOn++;
        }

        internal static void CopyLodRestoreAll()
        {
            foreach (var st in _lod.Values) if (st.Off) Restore(st);
            _lod.Clear();
        }

        /// Bridge `mp copycost`: what the copies (or every car, with CopyLodAllCars) have live, summed and per car.
        public static string CopyCounts()
        {
            int cars = 0, tf = 0, rend = 0, rendOn = 0, casters = 0, cols = 0, colsOn = 0, lights = 0, lightsOn = 0, shadowLightsOn = 0, rbs = 0, anim = 0, audio = 0, ps = 0;
            foreach (var c in CopyCars())
            {
                if (c == null) continue;
                cars++;
                tf += c.GetComponentsInChildren<Transform>(true).Length;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) { rend++; if (r.enabled && r.gameObject.activeInHierarchy) { rendOn++; if (r.shadowCastingMode != ShadowCastingMode.Off) casters++; } }
                foreach (var co in c.GetComponentsInChildren<Collider>(true)) { cols++; if (co.enabled && co.gameObject.activeInHierarchy) colsOn++; }
                foreach (var l in c.GetComponentsInChildren<Light>(true)) { lights++; if (l.enabled && l.gameObject.activeInHierarchy) { lightsOn++; if (l.shadows != LightShadows.None) shadowLightsOn++; } }
                rbs += c.GetComponentsInChildren<Rigidbody>(true).Length;
                anim += c.GetComponentsInChildren<Animator>(true).Length;
                audio += c.GetComponentsInChildren<AudioSource>(true).Length;
                ps += c.GetComponentsInChildren<ParticleSystem>(true).Length;
            }
            int allShadowLights = 0;
            foreach (var l in Object.FindObjectsOfType<Light>()) if (l.enabled && l.shadows != LightShadows.None) allShadowLights++;
            string per(int n) => cars == 0 ? "0" : (n / (float)cars).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            return "{\"cars\":" + cars + ",\"transforms\":" + tf + ",\"renderers\":" + rend + ",\"renderersOn\":" + rendOn + ",\"shadowCasters\":" + casters +
                   ",\"colliders\":" + cols + ",\"collidersOn\":" + colsOn + ",\"lights\":" + lights + ",\"lightsOn\":" + lightsOn + ",\"shadowLightsOn\":" + shadowLightsOn +
                   ",\"rigidbodies\":" + rbs + ",\"animators\":" + anim + ",\"audio\":" + audio + ",\"particles\":" + ps + ",\"worldShadowLightsOn\":" + allShadowLights +
                   ",\"perCar\":{\"transforms\":" + per(tf) + ",\"renderersOn\":" + per(rendOn) + ",\"collidersOn\":" + per(colsOn) + ",\"shadowCasters\":" + per(casters) + "}" +
                   ",\"farShadowsM\":" + CopyFarShadowsM.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"lodOff\":" + CopyLodOff + ",\"lodOn\":" + CopyLodOn + ",\"lodNow\":" + _lod.Count + "}";
        }
    }
}
