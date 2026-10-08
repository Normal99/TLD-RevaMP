using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Proximity voice chat. Capture and codec are Steam's own voice (ISteamUser: the microphone chosen in Steam's
    /// settings, Opus-compressed, voice detection), about 2 KB/s while someone talks and nothing while silent. The game's
    /// own MP voice sends raw 8 kHz float samples of a whole second at a time — ~32 KB/s per talker.
    ///   talking    the game's own "Voice chat" key (rebindable in its controls; V when unbound) held, or open mic
    ///   relay      the host sends a voice only to players within RangeM of the speaker (beyond it nobody could hear it)
    ///   playback   3D from the speaker's head, through a jitter buffer (starts after PrimeS of audio, catches up if
    ///              it lags more than MaxLagS), lips moving with it
    public static class Voice
    {
        public enum Mode { PushToTalk, OpenMic, Off }
        public static Mode Setting = Mode.PushToTalk;
        public static float Volume = 1f;
        public static float RangeM = 60f;
        public const int Rate = 24000;   // what Steam decodes to (GetVoiceOptimalSampleRate on both machines: 24000)
        public const float PrimeS = 0.08f, MaxLagS = 0.35f, PollS = 0.04f, TailS = 0.5f;
        public const byte CodecSteam = 0, CodecPcm16 = 1;   // PCM16: test tone only (the bridge's `mp voice tone`)
        public static readonly HashSet<ulong> Muted = new HashSet<ulong>();

        // ---- sending
        private static bool _recording;
        private static float _tailUntil, _nextPoll, _lastSent = -10f;
        private static ushort _seq;
        private static readonly byte[] _enc = new byte[8192];
        private static readonly NetWriter W = new NetWriter();
        public static long PacketsSent, BytesSent, PacketsIn, PacketsPlayed, DecodeErrors, Late;
        public static EVoiceResult LastResult = EVoiceResult.k_EVoiceResultOK;
        public static bool Sending => Time.unscaledTime - _lastSent < 0.3f;

        /// Runner, every frame.
        public static void Tick()
        {
            float now = Time.unscaledTime;
            bool want = Session.InGame && Setting != Mode.Off && SteamTransport.SteamReady && (Setting == Mode.OpenMic || TalkKeyHeld());
            try
            {
                if (want && !_recording) { SteamUser.StartVoiceRecording(); _recording = true; }
                else if (!want && _recording) { SteamUser.StopVoiceRecording(); _recording = false; _tailUntil = now + TailS; }   // Steam hands out the last words for a moment after Stop
                if ((_recording || now < _tailUntil) && now >= _nextPoll)
                {
                    _nextPoll = now + PollS;
                    LastResult = SteamUser.GetAvailableVoice(out uint avail);
                    if (LastResult == EVoiceResult.k_EVoiceResultOK && avail > 0)
                    {
                        LastResult = SteamUser.GetVoice(true, _enc, (uint)_enc.Length, out uint n);
                        if (LastResult == EVoiceResult.k_EVoiceResultOK && n > 0) Send(CodecSteam, _enc, 0, (int)n);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("voice capture: " + e.Message); Setting = Mode.Off; }
            ToneTick(now);
            Speakers(now);
        }

        /// The game's own "Voice chat" key (inputscript zeroes it while typing); K when the player never bound one — not V:
        /// that's the game's sleep key (holding it to talk filled the sleep bar).
        private static bool TalkKeyHeld()
        {
            if (inputscript.i == null || mainscript.s == null || mainscript.s.SomeInputSelected() || Chat.Typing) return false;
            if (Keys.Bound(inputscript.IN.voicechat)) return inputscript.i.voiceChat;
            return Input.GetKey(KeyCode.K);
        }

        private static void Send(byte codec, byte[] data, int off, int len)
        {
            _seq++; _lastSent = Time.unscaledTime;
            PacketsSent++; BytesSent += len;
            if (Mp.Server != null) { Mp.Server.VoiceRelay(0, Mp.LocalGlobal(), codec, _seq, data, off, len); return; }
            var c = Mp.Client;
            if (c == null || !c.Ready) return;
            W.Reset(); W.U8(Protocol.Voice); W.U8(codec); W.U16(_seq); W.Bytes(data, off, len);
            c.SendVoice(W);
        }

        // ---- receiving (a client: from the host; the host: Play straight from the relay)
        internal static void In(NetReader r)
        {
            int id = (int)r.VarU32(); byte codec = r.U8(); ushort seq = r.U16();
            int len = r.Remaining;
            if (r.Bad || len <= 0) return;
            Play(id, codec, seq, r.Buf, r.Pos, len);
        }

        private sealed class Speaker
        {
            public int Id; public AudioSource Src; public Transform Head;
            public readonly float[] Ring = new float[Rate * 2];
            public int Wp, Rp, Count; public bool Primed;
            public ushort LastSeq; public bool HasSeq;
            public float LastAt = -10f; public volatile float Level;
            public long Underruns, Dropped, Samples, Fills;
            public float Step, Frac, Prev, Cur;   // resampling Rate → the output rate (linear)
            public volatile float Prime = PrimeS;  // this talker's buffer before playing (AdaptiveJitter)
            public readonly float[] Gaps = new float[30]; public int GapN;   // recent arrival gaps within an utterance
        }

        /// A fixed 80 ms buffer ran dry whenever packets came in bursts — a laptop on power-saving Wi-Fi gets everything
        /// in ~300 ms bursts (twocars, 10-08): 3 gaps in 3 s of speech (tools/voicesync.py), heard as crackle. Each
        /// talker's buffer now covers the longest arrival gap of its last 30 packets (+30 ms), 80-400 ms; the lag cap
        /// sits 250 ms above it. Smooth over a bursty link at the cost of that much more delay, only for that talker.
        public static bool AdaptiveJitter = true;
        private static void NoteArrival(Speaker sp, float now)
        {
            if (!AdaptiveJitter) { sp.Prime = PrimeS; return; }
            float gap = now - sp.LastAt;
            if (gap > 1f) return;   // a new utterance: the silence before it is no gap
            sp.Gaps[sp.GapN++ % sp.Gaps.Length] = gap;
            float mx = 0f; int n = Math.Min(sp.GapN, sp.Gaps.Length);
            for (int i = 0; i < n; i++) if (sp.Gaps[i] > mx) mx = sp.Gaps[i];
            sp.Prime = Mathf.Clamp(mx + 0.03f, PrimeS, 0.4f);
        }

        /// The voice is written by a DSP filter on the source, not a streamed clip: Unity reads a streamed clip ahead in
        /// bursts of thousands of samples with ~300 ms gaps (each burst emptied the jitter buffer, then the lag cap threw
        /// half the voice away — measured, v0.65.1). A filter is called every DSP block (~20 ms), evenly. The source plays
        /// a looping clip of constant 1.0 and the filter multiplies it by the voice, so the game's rolloff, 3D panning
        /// and mixer group apply to the voice as to any sound.
        private sealed class VoiceOut : MonoBehaviour
        {
            internal Speaker Sp;
            private void OnAudioFilterRead(float[] data, int channels)
            {
                var sp = Sp;
                if (sp == null) { Array.Clear(data, 0, data.Length); return; }
                Fill(sp, data, channels);
            }
        }
        private static AudioClip _one;
        private static readonly Dictionary<int, Speaker> _speakers = new Dictionary<int, Speaker>();
        private static readonly byte[] _pcm = new byte[Rate * 2 * 2];   // 2 s of 16-bit mono
        private static readonly float[] _f = new float[Rate * 2];
        private static GameObject _sourceTemplate;

        internal static void Play(int id, byte codec, ushort seq, byte[] data, int off, int len)
        {
            PacketsIn++;
            ulong sid = RemotePlayers.SteamIdOf(id);
            if (sid != 0 && Muted.Contains(sid)) return;
            var sp = Get(id);
            if (sp == null) return;
            if (sp.HasSeq && (short)(seq - sp.LastSeq) <= 0) { Late++; return; }   // out of order: too late to play
            sp.HasSeq = true; sp.LastSeq = seq;
            int samples = Decode(codec, data, off, len);
            if (samples <= 0) return;
            lock (sp)
            {
                for (int i = 0; i < samples; i++)
                {
                    sp.Ring[sp.Wp] = _f[i];
                    sp.Wp = (sp.Wp + 1) % sp.Ring.Length;
                    if (sp.Count < sp.Ring.Length) sp.Count++; else sp.Rp = (sp.Rp + 1) % sp.Ring.Length;
                }
                int max = (int)(Rate * (MaxLagS - PrimeS + sp.Prime));
                if (sp.Count > max) { int drop = sp.Count - (int)(Rate * sp.Prime); sp.Rp = (sp.Rp + drop) % sp.Ring.Length; sp.Count -= drop; sp.Dropped += drop; }
            }
            sp.Samples += samples;
            NoteArrival(sp, Time.unscaledTime);
            sp.LastAt = Time.unscaledTime;
            PacketsPlayed++;
        }

        private static int Decode(byte codec, byte[] data, int off, int len)
        {
            byte[] src = data;
            int n;
            if (codec == CodecSteam)
            {
                if (off != 0) { src = new byte[len]; Buffer.BlockCopy(data, off, src, 0, len); }
                var res = SteamUser.DecompressVoice(src, (uint)len, _pcm, (uint)_pcm.Length, out uint written, Rate);
                if (res != EVoiceResult.k_EVoiceResultOK) { DecodeErrors++; return 0; }
                n = (int)written / 2;
                for (int i = 0; i < n; i++) _f[i] = (short)(_pcm[2 * i] | (_pcm[2 * i + 1] << 8)) / 32768f;
                return n;
            }
            if (codec == CodecPcm16)
            {
                n = Math.Min(len / 2, _f.Length);
                for (int i = 0; i < n; i++) _f[i] = (short)(data[off + 2 * i] | (data[off + 2 * i + 1] << 8)) / 32768f;
                return n;
            }
            DecodeErrors++;
            return 0;
        }

        /// The speaker's voice source: on its body's head (re-made when the body is re-made).
        private static Speaker Get(int id)
        {
            var head = RemotePlayers.Head(id);
            if (head == null) return null;
            if (!_speakers.TryGetValue(id, out var sp)) { sp = new Speaker { Id = id }; _speakers[id] = sp; }
            if (sp.Src != null && sp.Head == head) return sp;
            if (sp.Src != null) UnityEngine.Object.Destroy(sp.Src.gameObject);
            sp.Head = head;
            var go = new GameObject("TLDRevampVoice" + id);
            go.transform.SetParent(head, false);
            var src = go.AddComponent<AudioSource>();
            var tpl = Template();
            if (tpl != null)
            {
                var t = tpl.GetComponent<AudioSource>();
                src.outputAudioMixerGroup = t.outputAudioMixerGroup;
                src.rolloffMode = t.rolloffMode; src.minDistance = t.minDistance;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, t.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
                src.dopplerLevel = 0f;
            }
            else
            {
                src.outputAudioMixerGroup = mainscript.s != null ? mainscript.s.defGroup : null;
                src.rolloffMode = AudioRolloffMode.Logarithmic; src.minDistance = 2f;
            }
            src.maxDistance = RangeM;
            src.spatialBlend = 1f; src.dopplerLevel = 0f; src.priority = 16;
            src.volume = Volume;
            src.loop = true;
            int outRate = AudioSettings.outputSampleRate;
            if (_one == null)
            {
                _one = AudioClip.Create("TLDRevampVoiceCarrier", outRate, 1, outRate, false);
                var ones = new float[outRate]; for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
                _one.SetData(ones, 0);
            }
            lock (sp) { sp.Step = (float)Rate / Mathf.Max(8000, outRate); sp.Frac = 0f; sp.Prev = sp.Cur = 0f; }
            src.clip = _one;
            go.AddComponent<VoiceOut>().Sp = sp;
            src.Play();
            sp.Src = src;
            return sp;
        }

        /// The game's own voice source (its MP prefab): the mixer group and rolloff the game meant for voices.
        private static GameObject Template()
        {
            if (_sourceTemplate != null) return _sourceTemplate;
            try
            {
                var ns = UnityEngine.Object.FindObjectOfType<GeneszSteamP2PBase.networkingScript>(true);
                if (ns != null && ns.voiceSourcePrefab != null) _sourceTemplate = ns.voiceSourcePrefab.gameObject;
            }
            catch { }
            return _sourceTemplate;
        }

        /// The audio thread pulls samples: silence until PrimeS are buffered, silence (and prime again) when it runs dry.
        /// The audio thread, every DSP block: data is the carrier (1.0, already attenuated and panned), interleaved.
        private static void Fill(Speaker sp, float[] data, int channels)
        {
            float sum = 0f;
            int frames = data.Length / Math.Max(1, channels);
            sp.Fills++;
            lock (sp)
            {
                if (!sp.Primed && sp.Count >= (int)(Rate * sp.Prime)) sp.Primed = true;
                for (int f = 0; f < frames; f++)
                {
                    float v = 0f;
                    if (sp.Primed)
                    {
                        sp.Frac += sp.Step;
                        while (sp.Frac >= 1f)
                        {
                            if (sp.Count == 0) { sp.Primed = false; sp.Underruns++; sp.Prev = sp.Cur = 0f; sp.Frac = 0f; break; }
                            sp.Prev = sp.Cur; sp.Cur = sp.Ring[sp.Rp];
                            sp.Rp = (sp.Rp + 1) % sp.Ring.Length; sp.Count--;
                            sp.Frac -= 1f;
                        }
                        v = sp.Prev + (sp.Cur - sp.Prev) * sp.Frac;
                    }
                    sum += v * v;
                    int b = f * channels;
                    for (int c = 0; c < channels; c++) data[b + c] *= v;
                }
            }
            sp.Level = frames > 0 ? Mathf.Sqrt(sum / frames) : 0f;
        }

        private static void Speakers(float now)
        {
            if (_speakers.Count == 0) return;
            List<int> gone = null;
            foreach (var sp in _speakers.Values)
            {
                if (RemotePlayers.Head(sp.Id) == null && now - sp.LastAt > 2f) (gone ?? (gone = new List<int>())).Add(sp.Id);
                else if (sp.Src != null) sp.Src.volume = Volume;
            }
            if (gone != null) foreach (int id in gone) Drop(id);
        }

        private static void Drop(int id)
        {
            if (!_speakers.TryGetValue(id, out var sp)) return;
            if (sp.Src != null) { var vo = sp.Src.GetComponent<VoiceOut>(); if (vo != null) vo.Sp = null; UnityEngine.Object.Destroy(sp.Src.gameObject); }
            _speakers.Remove(id);
        }

        /// Who is talking right now (the HUD's list), and how loud (lips).
        public static bool Talking(int id) => _speakers.TryGetValue(id, out var sp) && Time.unscaledTime - sp.LastAt < Mathf.Max(0.3f, sp.Prime + 0.15f);   // bursty links: no lip flicker

        internal static void Lips(int id, mpplayerscript mp)
        {
            if (mp == null || mp.lipT == null || mp.lipC == null || mp.lipO == null) return;
            if (!_speakers.TryGetValue(id, out var sp)) return;
            float target = Mathf.InverseLerp(mp.lipMin, mp.lipMax, sp.Level * 3f);
            mp.lipCurrent = Mathf.Lerp(mp.lipCurrent, target, Time.deltaTime * mp.lipSpeed);
            mp.lipT.SetPositionAndRotation(Vector3.Lerp(mp.lipC.position, mp.lipO.position, mp.lipCurrent),
                                           Quaternion.Lerp(mp.lipC.rotation, mp.lipO.rotation, mp.lipCurrent));
        }

        public static void Reset()
        {
            foreach (var id in new List<int>(_speakers.Keys)) Drop(id);
            if (_recording) { try { SteamUser.StopVoiceRecording(); } catch { } _recording = false; }
            _toneUntil = 0f;
        }

        // ---- test: a 440 Hz tone sent as voice (PCM16, no Steam codec), and a capture probe
        private static float _toneUntil, _toneNext; private static double _tonePhase;
        private static readonly byte[] _tone = new byte[Rate / 25 * 2];   // 40 ms

        public static string Tone(float seconds, int feed = -1) { _toneUntil = Time.unscaledTime + seconds; _toneNext = 0f; _feed = feed; return Status(); }
        private static int _feed = -1;   // the bridge's `mp voice feed id s`: the tone straight into this machine's speaker for player id (no network)

        private static void ToneTick(float now)
        {
            if (now >= _toneUntil) return;
            if (_toneNext <= 0f || now - _toneNext > 0.2f) _toneNext = now;   // start (or after a stall): from now
            for (int k = 0; k < 4 && now >= _toneNext; k++) { _toneNext += 0.04f; ToneBlock(); }   // real time: 25 blocks/s at any frame rate
        }

        private static void ToneBlock()
        {
            int n = _tone.Length / 2;
            for (int i = 0; i < n; i++)
            {
                short v = (short)(Math.Sin(_tonePhase) * 12000);
                _tonePhase += 2 * Math.PI * 440 / Rate;
                _tone[2 * i] = (byte)v; _tone[2 * i + 1] = (byte)(v >> 8);
            }
            if (_feed >= 0) Play(_feed, CodecPcm16, ++_seq, _tone, 0, _tone.Length);
            else Send(CodecPcm16, _tone, 0, _tone.Length);
        }

        /// Bridge `mp voice probe`: does Steam's capture work on this machine? (records 1 s, reports what came back)
        public static string Probe()
        {
            if (!SteamTransport.SteamReady) return "{\"error\":\"steam not ready\"}";
            uint rate = SteamUser.GetVoiceOptimalSampleRate();
            SteamUser.StartVoiceRecording();
            var first = SteamUser.GetAvailableVoice(out uint a0);
            _probeUntil = Time.unscaledTime + 1f;
            return "{\"optimalRate\":" + rate + ",\"first\":" + Json.Str(first.ToString()) + ",\"avail\":" + a0 + "}";
        }
        private static float _probeUntil;

        public static string Status()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var sp in _speakers.Values)
            {
                int buffered; lock (sp) buffered = sp.Count;
                rows.Add("{\"id\":" + sp.Id + ",\"samples\":" + sp.Samples + ",\"buffered\":" + buffered + ",\"fills\":" + sp.Fills + ",\"virtual\":" + (sp.Src != null && sp.Src.isVirtual ? "true" : "false") + ",\"timeSamples\":" + (sp.Src != null ? sp.Src.timeSamples : -1) + ",\"underruns\":" + sp.Underruns + ",\"dropped\":" + sp.Dropped +
                         ",\"level\":" + sp.Level.ToString("F4", ic) + ",\"talking\":" + (Talking(sp.Id) ? "true" : "false") + ",\"primeMs\":" + (sp.Prime * 1000f).ToString("F0", ic) +
                         ",\"playing\":" + (sp.Src != null && sp.Src.isPlaying && sp.Src.isActiveAndEnabled ? "true" : "false") +
                         ",\"maxDistance\":" + (sp.Src != null ? sp.Src.maxDistance : 0f).ToString("F0", ic) +
                         ",\"mixer\":" + Json.Str(sp.Src != null && sp.Src.outputAudioMixerGroup != null ? sp.Src.outputAudioMixerGroup.name : null) + "}");
            }
            if (_probeUntil > 0f && Time.unscaledTime > _probeUntil && !_recording) { try { SteamUser.StopVoiceRecording(); } catch { } _probeUntil = 0f; }
            uint avail = 0; EVoiceResult now = EVoiceResult.k_EVoiceResultNotRecording;
            try { now = SteamUser.GetAvailableVoice(out avail); } catch { }
            return "{\"mode\":" + Json.Str(Setting.ToString()) + ",\"recording\":" + (_recording ? "true" : "false") + ",\"sending\":" + (Sending ? "true" : "false") +
                   ",\"last\":" + Json.Str(LastResult.ToString()) + ",\"availNow\":" + Json.Str(now + " " + avail) +
                   ",\"sent\":" + PacketsSent + ",\"bytesSent\":" + BytesSent + ",\"in\":" + PacketsIn + ",\"played\":" + PacketsPlayed +
                   ",\"late\":" + Late + ",\"decodeErrors\":" + DecodeErrors + ",\"range\":" + RangeM.ToString("F0", ic) +
                   ",\"template\":" + (Template() != null ? "true" : "false") + ",\"speakers\":[" + string.Join(",", rows) + "]}";
        }
    }

    public sealed partial class MpServer
    {
        public long VoiceRelayed;

        private void VoiceFrom(Peer p, NetReader r)
        {
            byte codec = r.U8(); ushort seq = r.U16();
            int len = r.Remaining;
            if (r.Bad || len <= 0 || len > 8192 || !p.HasPos) return;
            VoiceRelay(p.Id, p.Pos, codec, seq, r.Buf, r.Pos, len);
        }

        /// A voice goes to everyone within hearing of the speaker, at once (not with the next tick's batch).
        internal void VoiceRelay(int from, Vector3d at, byte codec, ushort seq, byte[] data, int off, int len)
        {
            double range = Voice.RangeM + 10.0, r2 = range * range;
            _w.Reset(); _w.U8(Protocol.Voice); _w.VarU32((uint)from); _w.U8(codec); _w.U16(seq); _w.Bytes(data, off, len);
            foreach (var o in _peers.Values)
            {
                if (o.Id == from || !o.Ready || !o.HasPos) continue;
                double dx = o.Pos.x - at.x, dy = o.Pos.y - at.y, dz = o.Pos.z - at.z;
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                T.Send(o.Conn, _w, SteamTransport.SendUnreliable);
                VoiceRelayed++;
            }
            if (from != 0 && !DedicatedServer.Enabled)
            {
                var h = Mp.LocalGlobal();
                double dx = h.x - at.x, dy = h.y - at.y, dz = h.z - at.z;
                if (dx * dx + dy * dy + dz * dz <= r2) Voice.Play(from, codec, seq, data, off, len);
            }
        }
    }

    public sealed partial class MpClient
    {
        internal void SendVoice(NetWriter w) => T.Send(_conn, w, SteamTransport.SendUnreliable);
    }

    /// The game's own key bindings (Settings → Controls).
    internal static class Keys
    {
        internal static bool Bound(inputscript.IN which)
        {
            try
            {
                var inp = settingsscript.s.S.inputs[(int)which];
                foreach (var k in inp.keys) if (k != null && !k.IsNone()) return true;
            }
            catch { }
            return false;
        }

        internal static string Name(inputscript.IN which)
        {
            try { return settingsscript.s.S.inputs[(int)which].GetName(); } catch { return "?"; }
        }
    }
}
