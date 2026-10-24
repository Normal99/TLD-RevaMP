using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Stamped station anchors follow the host's broadcast through transit, pauses and clip changes.
    public static partial class Entities
    {
        public const byte RadioSync = 54;
        public static float RadioResendS = 5f;
        public static long RadioSent, RadioApplied;

        // ---- the host
        private static int[] _radioLast = new int[0];
        private static float[] _radioStartLast = new float[0];
        private static float _radioAcc;
        private sealed class RadioAnchor
        {
            public AudioClip Clip; public double Position, Stamp, Rate;
        }
        private static readonly Dictionary<mainscript.radioCH, RadioAnchor> _radioAnchors = new Dictionary<mainscript.radioCH, RadioAnchor>();
        private static double _radioStamp = -1;

        private static void RadioTick(float dt)
        {
            if (!IsHost || mainscript.s == null || mainscript.s.radioChs == null || mainscript.s.radioChsAM == null) return;
            var fm = mainscript.s.radioChs; var am = mainscript.s.radioChsAM;
            bool changed = _radioLast.Length != fm.Count + am.Count;
            if (changed) { _radioLast = new int[fm.Count + am.Count]; _radioStartLast = new float[_radioLast.Length]; }
            for (int i = 0; i < fm.Count + am.Count; i++)
            {
                var ch = i < fm.Count ? fm[i] : am[i - fm.Count];
                int c = ch != null && !ch.custom ? ch.GetCurrentIndex() : -1;
                if (c != _radioLast[i]) { _radioLast[i] = c; changed = true; }
                if (ch != null && !ch.custom && Fixes.SessionClocks.RadioEnabled && ch.startTime != _radioStartLast[i]) { _radioStartLast[i] = ch.startTime; changed = true; }
            }
            _radioAcc += dt;
            if (!changed && (Fixes.SessionClocks.RadioEnabled ? ModObjects.ClockTime - _radioSentAt < RadioResendS : _radioAcc < RadioResendS)) return;
            _radioAcc = 0f;
            _radioSentAt = ModObjects.ClockTime;
            WriteRadio();
            ServerSendAll(W, true, 0);
            RadioSent++;
        }

        private static void WriteRadio()
        {
            W.Reset(); W.U8(RadioSync); W.F64(ModObjects.ClockTime); W.F32(_hostRate);
            WriteStations(W, mainscript.s.radioChs); WriteStations(W, mainscript.s.radioChsAM);
        }

        private static void ServerRadioTo(int playerId)
        {
            if (!Fixes.SessionClocks.RadioEnabled || mainscript.s == null || mainscript.s.radioChs == null || mainscript.s.radioChsAM == null) return;
            WriteRadio(); ServerSendTo(playerId, W, true); RadioSent++;
        }

        private static void WriteStations(NetWriter w, List<mainscript.radioCH> chs)
        {
            w.U8((byte)Mathf.Min(chs.Count, 255));
            for (int i = 0; i < chs.Count && i < 255; i++)
            {
                var ch = chs[i];
                int c = ch != null && !ch.custom ? ch.GetCurrentIndex() : -1;
                w.U16((ushort)(c + 1));
                w.F32(c >= 0 ? Fixes.SessionClocks.RadioEnabled ? Mathf.Max(0f, Time.time - ch.startTime) : ch.cTime : 0f);
            }
        }

        // ---- a client
        private static void ApplyRadio(NetReader r)
        {
            double stamp = r.F64(); float rate = r.F32();
            ReadStations(r, out var fm, out var fmT); ReadStations(r, out var am, out var amT);
            if (r.Bad || IsHost || mainscript.s == null || double.IsNaN(stamp) || double.IsInfinity(stamp) || !Finite(rate) || rate < 0f) return;
            if (stamp <= _radioStamp) { Fixes.SessionClocks.RadioStale++; return; }
            _radioStamp = stamp;
            if (fm.Length != mainscript.s.radioChs.Count || am.Length != mainscript.s.radioChsAM.Count) return;
            mainscript.s.RadioChannelFromMultiplayerUpd(fm, fmT, am, amT);   // the game's own receiver (skips custom stations)
            AnchorStations(mainscript.s.radioChs, fmT, stamp, rate); AnchorStations(mainscript.s.radioChsAM, amT, stamp, rate);
            RadioApplied++;
        }

        private static void AnchorStations(List<mainscript.radioCH> stations, float[] times, double stamp, float rate)
        {
            for (int i = 0; i < stations.Count; i++)
            {
                var ch = stations[i];
                if (ch == null || ch.custom || ch.currentClip == null) continue;
                if (!_radioAnchors.TryGetValue(ch, out var anchor)) _radioAnchors[ch] = anchor = new RadioAnchor();
                anchor.Clip = ch.currentClip; anchor.Position = times[i]; anchor.Stamp = stamp; anchor.Rate = rate;
                if (Fixes.SessionClocks.RadioEnabled) FollowStation(ch);
            }
        }

        private static void FollowStation(mainscript.radioCH ch)
        {
            if (!_radioAnchors.TryGetValue(ch, out var anchor) || ch.currentClip != anchor.Clip) return;
            double now = ModObjects.ClockTime;
            ch.cTime = (float)ClockMath.RadioPosition(anchor.Position, anchor.Stamp, now, anchor.Rate, anchor.Clip.length);
            ch.startTime = Time.unscaledTime - ch.cTime;   // preserve the legacy clock when toggled off
            Fixes.SessionClocks.RadioProjected++;
            if (ClockMath.Project(anchor.Position, anchor.Stamp, now, anchor.Rate) >= anchor.Clip.length) Fixes.SessionClocks.RadioClamped++;
        }

        private static void ReadStations(NetReader r, out int[] clips, out float[] times)
        {
            int n = r.U8();
            clips = new int[n]; times = new float[n];
            for (int i = 0; i < n; i++) { clips[i] = r.U16() - 1; times[i] = r.F32(); if (!Finite(times[i]) || times[i] < 0f) times[i] = 0f; }
        }

        /// Vanilla sleep advances FM stations directly, bypassing RADIO; only the host may choose broadcasts.
        [HarmonyLib.HarmonyPatch(typeof(mainscript.radioCH), nameof(mainscript.radioCH.AdvanceRadio))]
        private static class RadioSleepFollowsHost
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(mainscript.radioCH __instance)
            {
                if (!InSession || IsHost || !Fixes.SessionClocks.RadioEnabled || __instance.custom) return true;
                Fixes.SessionClocks.RadioLocalAdvancesBlocked++;
                return false;
            }
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
                    else if (Fixes.SessionClocks.RadioEnabled) FollowStation(ch);
                    else ch.cTime = Time.unscaledTime - ch.startTime;   // A/B: mainscript.RADIO2
                }
                if (_radioCustom.Count > 0)
                {
                    _radioInner = true;
                    try { __instance.RADIO(_radioCustom); } finally { _radioInner = false; }
                }
                return false;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(mainscript), nameof(mainscript.RADIO2))]
        private static class RadioClockFollowsHost
        {
            [HarmonyLib.HarmonyPrefix]
            private static bool Prefix(List<mainscript.radioCH> _radio)
            {
                if (!InSession || IsHost || !Fixes.SessionClocks.RadioEnabled || _radio == null) return true;
                foreach (var ch in _radio)
                    if (ch != null) { if (ch.custom) ch.cTime = Time.unscaledTime - ch.startTime; else FollowStation(ch); }
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

        /// Diagnostics (bridge `mp radiocar <net>`): the radios of a shared object here — on, band, frequency, volume knob,
        /// the clip playing and how far into it.
        public static string RadioCar(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var r in e.Root.transform.root.GetComponentsInChildren<radioscript>(true))
                rows.Add("{\"on\":" + (r.ON ? "true" : "false") + ",\"am\":" + (r.amfm ? "true" : "false") + ",\"fr\":" + r.FR.ToString("F1", ic) +
                         ",\"volume\":" + (r.Bvolume != null ? r.Bvolume.tunedLerp : -1f).ToString("F2", ic) +
                         ",\"clip\":" + Json.Str(r.SC != null && r.SC.clip != null ? r.SC.clip.name : "") + ",\"time\":" + (r.SC != null ? r.SC.time : -1f).ToString("F1", ic) +
                         ",\"playing\":" + (r.SC != null && r.SC.isPlaying ? "true" : "false") + ",\"electronics\":" + (r.electronics != null ? r.electronics.output : -1f).ToString("F2", ic) +
                         ",\"tune\":" + DialAddress(r.Btune) + ",\"vol\":" + DialAddress(r.Bvolume) + "}");
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"radios\":[" + string.Join(",", rows) + "]}";
        }

        private static string DialAddress(usablescript u) =>
            UsableAddress(u, out uint net, out int item, out int usable) ? "[" + net + "," + item + "," + usable + "]" : "null";

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
