using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The radio stations. Every station is a simulated broadcast in mainscript (radioChs FM, radioChsAM): a current
    /// clip (music or talk, picked at random when the last one ends) and how far into it the station is (cTime). Radios
    /// play the station's clip from that time. Each machine ran its own broadcast, so two players in one car heard
    /// different songs. The game's own MP: only the host runs the broadcast (mainscript.RADIO picks the clips); clients
    /// run RADIO2 (the clock only) and take the host's clips and times (RadioChannelMultiplayerUpd → syncScript.
    /// SendRadioChannel → RadioChannelFromMultiplayerUpd → radioCH.UpdFromMulti). The same here: the host sends when a
    /// clip changes and every RadioResendS (a player who just joined, a lost message); clients follow it.
    /// A station playing the player's own music folder (custom) stays that player's own, as in the game.
    public static partial class Entities
    {
        public const byte RadioSync = 54;
        public static float RadioResendS = 5f;
        public static long RadioSent, RadioApplied;

        // ---- the host
        private static int[] _radioLast = new int[0];
        private static float _radioAcc;

        private static void RadioTick(float dt)
        {
            if (!IsHost || mainscript.s == null || mainscript.s.radioChs == null || mainscript.s.radioChsAM == null) return;
            var fm = mainscript.s.radioChs; var am = mainscript.s.radioChsAM;
            bool changed = _radioLast.Length != fm.Count + am.Count;
            if (changed) _radioLast = new int[fm.Count + am.Count];
            for (int i = 0; i < fm.Count + am.Count; i++)
            {
                var ch = i < fm.Count ? fm[i] : am[i - fm.Count];
                int c = ch != null && !ch.custom ? ch.GetCurrentIndex() : -1;
                if (c != _radioLast[i]) { _radioLast[i] = c; changed = true; }
            }
            _radioAcc += dt;
            if (!changed && _radioAcc < RadioResendS) return;
            _radioAcc = 0f;
            W.Reset(); W.U8(RadioSync);
            WriteStations(W, fm); WriteStations(W, am);
            ServerSendAll(W, true, 0);
            RadioSent++;
        }

        private static void WriteStations(NetWriter w, List<mainscript.radioCH> chs)
        {
            w.U8((byte)Mathf.Min(chs.Count, 255));
            for (int i = 0; i < chs.Count && i < 255; i++)
            {
                var ch = chs[i];
                int c = ch != null && !ch.custom ? ch.GetCurrentIndex() : -1;
                w.U16((ushort)(c + 1));
                w.F32(c >= 0 ? ch.cTime : 0f);
            }
        }

        // ---- a client
        private static void ApplyRadio(NetReader r)
        {
            ReadStations(r, out var fm, out var fmT); ReadStations(r, out var am, out var amT);
            if (r.Bad || IsHost || mainscript.s == null) return;
            mainscript.s.RadioChannelFromMultiplayerUpd(fm, fmT, am, amT);   // the game's own receiver (skips custom stations)
            RadioApplied++;
        }

        private static void ReadStations(NetReader r, out int[] clips, out float[] times)
        {
            int n = r.U8();
            clips = new int[n]; times = new float[n];
            for (int i = 0; i < n; i++) { clips[i] = r.U16() - 1; times[i] = r.F32(); }
        }

        /// A client does not run the broadcast: its stations follow the host (RADIO2, as in the game's MP). Its custom
        /// station (its own music folder) still runs here.
        private static bool _radioInner;
        private static readonly List<mainscript.radioCH> _radioCustom = new List<mainscript.radioCH>();

        [HarmonyLib.HarmonyPatch(typeof(mainscript), nameof(mainscript.RADIO))]
        private static class RadioFollowsHost
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(mainscript __instance, List<mainscript.radioCH> _radio)
            {
                if (_radioInner || !InSession || IsHost || _radio == null) return true;
                _radioCustom.Clear();
                foreach (var ch in _radio)
                {
                    if (ch == null) continue;
                    if (ch.custom) _radioCustom.Add(ch);
                    else ch.cTime = Time.unscaledTime - ch.startTime;   // mainscript.RADIO2
                }
                if (_radioCustom.Count > 0)
                {
                    _radioInner = true;
                    try { __instance.RADIO(_radioCustom); } finally { _radioInner = false; }
                }
                return false;
            }
        }

        /// Diagnostics (bridge `mp radio`): every station's clip and time here.
        public static string RadioStatus()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            if (mainscript.s != null)
                foreach (var list in new[] { mainscript.s.radioChs, mainscript.s.radioChsAM })
                    if (list != null)
                        foreach (var ch in list)
                            rows.Add("[" + Json.Str(ch != null ? ch.name : "") + "," + (ch != null && ch.custom ? "true" : "false") + "," +
                                     Json.Str(ch != null && ch.currentClip != null ? ch.currentClip.name : "") + "," +
                                     (ch != null ? ch.cTime : 0f).ToString("F2", ic) + "]");
            return "{\"t\":" + Time.unscaledTime.ToString("F2", ic) + ",\"sent\":" + RadioSent + ",\"applied\":" + RadioApplied +
                   ",\"stations\":[" + string.Join(",", rows) + "]}";
        }

        /// Test hook (bridge `mp radio advance i`): station i (FM first, then AM) moves to its next clip, as when a clip ends.
        public static string RadioAdvance(int i)
        {
            if (mainscript.s == null) return "{\"error\":\"no game\"}";
            var fm = mainscript.s.radioChs; var am = mainscript.s.radioChsAM;
            var ch = i < fm.Count ? fm[i] : i - fm.Count < am.Count ? am[i - fm.Count] : null;
            if (ch == null) return "{\"error\":\"no station " + i + "\"}";
            ch.AdvanceRadio();
            return "{\"station\":" + Json.Str(ch.name) + ",\"clip\":" + Json.Str(ch.currentClip != null ? ch.currentClip.name : "") + "}";
        }
    }
}
