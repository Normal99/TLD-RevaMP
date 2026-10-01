using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Test diagnostic (off by default): who collides with what. Every car/item collision the game sees plays a sound
    /// through mainscript.PlayClipAtPoint — a new AudioSource object each. In the convoy load test (loadtest --radius 30)
    /// the host logged ~68,000 "ran out of virtual channels" for collision sounds, then FMOD "not enough resources" for
    /// every new AudioSource, and hung. Counts OnCollisionEnter of carscript / pickupable per (this root, other root),
    /// each tagged copy/owned and kinematic/physical. Bridge: `mp contacts [on|off|reset]`.
    public static partial class Entities
    {
        public static bool ContactCensus;
        private static readonly Dictionary<string, long> _census = new Dictionary<string, long>();
        public static long CensusTotal, SoundClips;

        private static string Tag(Transform root)
        {
            if (root == null) return "?";
            var rb = root.GetComponent<Rigidbody>();
            string body = rb == null ? "static" : rb.isKinematic ? "kin" : "dyn";
            NetOf(root, out uint net, out bool proxy);
            string who = net == 0 ? "local" : proxy ? "copy" + net : "own" + net;
            return root.name + "[" + who + "," + body + "]";
        }

        private static void Count(Component self, Collision c)
        {
            if (!ContactCensus || self == null || c == null) return;
            CensusTotal++;
            string k = Tag(self.transform.root) + " <- " + Tag(c.transform.root) + " (" + (c.collider != null ? c.collider.name : "?") + ")";
            _census.TryGetValue(k, out long n); _census[k] = n + 1;
        }

        [HarmonyLib.HarmonyPatch(typeof(carscript), "OnCollisionEnter")]
        private static class CensusCar { [HarmonyLib.HarmonyPrefix] private static void Prefix(carscript __instance, Collision c) => Count(__instance, c); }

        [HarmonyLib.HarmonyPatch(typeof(pickupable), "OnCollisionEnter")]
        private static class CensusItem { [HarmonyLib.HarmonyPrefix] private static void Prefix(pickupable __instance, Collision c) => Count(__instance, c); }

        [HarmonyLib.HarmonyPatch(typeof(mainscript), nameof(mainscript.PlayClipAtPoint), new[] { typeof(AudioClip), typeof(Vector3), typeof(float), typeof(float), typeof(UnityEngine.Audio.AudioMixerGroup), typeof(float), typeof(byte) })]   // every overload ends here: Instantiate(s.Audio)
        private static class CensusSound { [HarmonyLib.HarmonyPrefix] private static void Prefix() { if (ContactCensus) SoundClips++; } }

        public static string Contacts(string arg)
        {
            if (arg == "on") { ContactCensus = true; }
            else if (arg == "off") { ContactCensus = false; }
            else if (arg == "reset") { _census.Clear(); CensusTotal = 0; SoundClips = 0; }
            // audio sources alive: the hang ended in FMOD failing to create more of them
            var srcs = Object.FindObjectsOfType<AudioSource>(true);
            int playing = 0; var byRoot = new Dictionary<string, int>();
            foreach (var a in srcs)
            {
                if (a.isPlaying) playing++;
                string r = a.transform.root.name; byRoot.TryGetValue(r, out int n); byRoot[r] = n + 1;
            }
            var sb = new StringBuilder("{\"on\":" + (ContactCensus ? "true" : "false") + ",\"total\":" + CensusTotal + ",\"soundClips\":" + SoundClips +
                                       ",\"audioSources\":" + srcs.Length + ",\"audioPlaying\":" + playing + ",\"audioRoots\":" + Json.Str(string.Join(", ", byRoot.OrderByDescending(p => p.Value).Take(6).Select(p => p.Key + " " + p.Value))) + ",\"top\":[");
            bool first = true;
            foreach (var kv in _census.OrderByDescending(p => p.Value).Take(12))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("[").Append(Json.Str(kv.Key)).Append(',').Append(kv.Value).Append(']');
            }
            return sb.Append("]}").ToString();
        }
    }
}
