using UnityEngine;

namespace TLDRevamp.Net
{
    /// What a player does with their own body that others should hear and see: humming/yelling (the game's hum key —
    /// a voice steered in volume and pitch with the mouse) and peeing (fpscontroller.piss, a pour of the bladder's fluid).
    /// The game played both only on the player's own machine (its own MP doesn't send them either): the other player
    /// heard no scream and saw no stream (bugs 1 and 2, 2026-10-03).
    ///
    /// Both ride the pose message (sent ~StateHz to everyone within PlayerLook.PoseRangeM) as an optional tail: what the
    /// player's own hum source plays (start or loop clip, volume, pitch) and what its own pour emits (rate, speed,
    /// colour, sound level, where and which way, relative to the body). The receiving model plays the same clip on a
    /// source set up like the player's own, and shows the stream with a copy of the player's own pour particles — the
    /// copy has no behaviours (no tankcapscript draining this machine's bladder, no particle detection filling tanks
    /// here): it only shows.
    public static class PlayerActs
    {
        public static long Sent, Applied;
        /// Test: the hum / pee key held (inputscript.i.hum / .piss, set where the keyboard's input lands).
        public static bool TestHum, TestPiss, TestSleep;

        [HarmonyLib.HarmonyPatch(typeof(inputscript), "Update")]
        private static class TestKeys
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(inputscript __instance)
            {
                if (TestHum) __instance.hum = true;
                if (TestPiss) __instance.piss = true;
                if (TestSleep) __instance.sleep = true;
            }
        }

        /// Bridge `mp acts`: what the local player does, and what every remote model shows.
        public static string Probe(string cmd = "")
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            // test: something to pee (a fresh world's bladder can be empty)
            if (cmd == "fill" && pl != null && pl.piss != null && pl.piss.Tank != null) pl.piss.Tank.F.ChangeOne(pl.piss.Tank.F.maxC * 0.8f, mainscript.fluidenum.water);
            string local = "null";
            if (pl != null)
            {
                var pc = pl.piss;
                local = "{\"humPlaying\":" + (pl.humSource != null && pl.humSource.isPlaying ? "true" : "false") +
                        ",\"humVol\":" + (pl.humSource != null ? pl.humSource.volume.ToString("F2", ic) : "0") +
                        ",\"pissEmit\":" + (pc != null && pc.ps != null && pc.ps.emission.enabled ? "true" : "false") +
                        ",\"pissParticles\":" + (pc != null && pc.ps != null ? pc.ps.particleCount : 0) +
                        ",\"pissSound\":" + (pc != null && pc.SSource != null && pc.SSource.isPlaying ? "true" : "false") +
                        ",\"bladder\":" + (pc != null && pc.Tank != null && pc.Tank.F != null ? pc.Tank.F.GetAmount().ToString("F2", ic) : "-1") + "}";
            }
            var rows = new System.Collections.Generic.List<string>();
            foreach (var s in Object.FindObjectsOfType<Show>())
                rows.Add("{\"model\":" + Json.Str(s.name) + ",\"fresh\":" + (s.Last != null && Time.time - s.At < 0.5f ? "true" : "false") +
                         ",\"humPlaying\":" + (s.HumSrc != null && s.HumSrc.isPlaying ? "true" : "false") + ",\"humVol\":" + (s.HumSrc != null ? s.HumSrc.volume.ToString("F2", ic) : "0") +
                         ",\"humClip\":" + Json.Str(s.HumSrc != null && s.HumSrc.clip != null ? s.HumSrc.clip.name : "") +
                         ",\"pissEmit\":" + (s.Ps != null && s.Ps.emission.enabled ? "true" : "false") + ",\"pissParticles\":" + (s.Ps != null ? s.Ps.particleCount : 0) +
                         ",\"pourPlaying\":" + (s.PourSrc != null && s.PourSrc.isPlaying ? "true" : "false") + "}");
            return "{\"sent\":" + Sent + ",\"applied\":" + Applied + ",\"local\":" + local + ",\"models\":[" + string.Join(",", rows) + "]}";
        }
        private const byte FHum = 1, FHumStart = 2, FPiss = 4, FPissSound = 8;

        public sealed class Acts
        {
            public byte Flags, HumSel; public float HumVol, HumPitch;
            public Vector3 PissPos, PissDir; public float PissRate, PissSpeed, PissVol; public Color32 PissColor;
        }

        public static void Write(NetWriter w, fpscontroller pl, Transform T)
        {
            byte f = 0;
            var hs = pl.humSource;
            bool hum = hs != null && hs.isPlaying && hs.clip != null && pl.selectedHum >= 0 && pl.selectedHum < 256;
            if (hum) { f |= FHum; if (pl.humStartClips != null && pl.selectedHum < pl.humStartClips.Length && hs.clip == pl.humStartClips[pl.selectedHum]) f |= FHumStart; }
            var pc = pl.piss;
            bool emit = false, snd = false;
            if (pc != null && pc.started && pc.ps != null)
            {
                emit = pc.ps.emission.enabled;
                snd = pc.SSource != null && pc.SSource.isPlaying;
            }
            if (emit) f |= FPiss;
            if (snd) f |= FPissSound;
            w.U8(f);
            if (hum) { w.U8((byte)pl.selectedHum); w.F32(hs.volume); w.F32(hs.pitch); }
            if (emit || snd)
            {
                var p = T.InverseTransformPoint(pc.transform.position);
                var d = T.InverseTransformDirection(pc.transform.forward);
                w.F32(p.x); w.F32(p.y); w.F32(p.z);
                w.U16((ushort)(short)Mathf.RoundToInt(d.x * 30000f)); w.U16((ushort)(short)Mathf.RoundToInt(d.y * 30000f)); w.U16((ushort)(short)Mathf.RoundToInt(d.z * 30000f));
                var main = pc.ps.main;
                w.F32(pc.ps.emission.rateOverTime.constant); w.F32(main.startSpeed.constant);
                Color32 c = main.startColor.color; w.U8(c.r); w.U8(c.g); w.U8(c.b); w.U8(c.a);
                w.F32(snd ? pc.SSource.volume : 0f);
            }
            if (f != 0) Sent++;
        }

        public static Acts Read(NetReader r)
        {
            if (r.Remaining <= 0) return null;
            var a = new Acts { Flags = r.U8() };
            if ((a.Flags & FHum) != 0) { a.HumSel = r.U8(); a.HumVol = r.F32(); a.HumPitch = r.F32(); }
            if ((a.Flags & (FPiss | FPissSound)) != 0)
            {
                a.PissPos = new Vector3(r.F32(), r.F32(), r.F32());
                a.PissDir = new Vector3((short)r.U16() / 30000f, (short)r.U16() / 30000f, (short)r.U16() / 30000f);
                a.PissRate = r.F32(); a.PissSpeed = r.F32();
                a.PissColor = new Color32(r.U8(), r.U8(), r.U8(), r.U8());
                a.PissVol = r.F32();
            }
            if (r.Bad) return null;
            // sound and a particle effect on the remote player: a non-finite value (a broken sender) plays nothing
            if (!Entities.Finite(a.HumVol) || !Entities.Finite(a.HumPitch)) a.Flags &= unchecked((byte)~FHum);
            if (!Entities.Finite(a.PissPos) || a.PissPos.sqrMagnitude > 1e4f || !Entities.Finite(a.PissRate) || !Entities.Finite(a.PissSpeed) || !Entities.Finite(a.PissVol))
                a.Flags &= unchecked((byte)~(FPiss | FPissSound));
            return a;
        }

        public static void Apply(mpplayerscript mp, Acts a)
        {
            if (mp == null || a == null) return;
            var o = mp.GetComponent<Show>();
            if (o == null) { if (a.Flags == 0) return; o = mp.gameObject.AddComponent<Show>(); o.Mp = mp; }
            o.Last = a; o.At = Time.time;
            Applied++;
        }

        /// On a remote player's model: plays what the last pose said, fades out when they stop (or poses stop coming).
        public sealed class Show : MonoBehaviour
        {
            public mpplayerscript Mp; public Acts Last; public float At;
            private AudioSource _hum, _pour; private ParticleSystem _ps; private AudioClip _clip; private bool _psFailed;
            private float _humVol;
            internal AudioSource HumSrc => _hum; internal AudioSource PourSrc => _pour; internal ParticleSystem Ps => _ps;

            private void Update()
            {
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl == null || Mp == null) return;
                bool fresh = Last != null && Time.time - At < 0.5f;
                if (_hum != null && Mp.THead != null) _hum.transform.position = Mp.THead.position;
                var a = fresh ? Last : null;
                Hum(pl, a);
                Piss(pl, a);
            }

            private static AudioSource Like(GameObject on, AudioSource src)
            {
                var s = on.AddComponent<AudioSource>();
                s.playOnAwake = false; s.loop = false;
                if (src == null) { s.spatialBlend = 1f; s.maxDistance = 100f; return s; }
                s.outputAudioMixerGroup = src.outputAudioMixerGroup;
                s.spatialBlend = src.spatialBlend; s.rolloffMode = src.rolloffMode; s.minDistance = src.minDistance; s.maxDistance = src.maxDistance;
                s.dopplerLevel = src.dopplerLevel; s.spread = src.spread; s.priority = src.priority; s.reverbZoneMix = src.reverbZoneMix;
                if (src.rolloffMode == AudioRolloffMode.Custom) s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, src.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
                return s;
            }

            private void Hum(fpscontroller pl, Acts a)
            {
                bool on = a != null && (a.Flags & FHum) != 0;
                if (on)
                {
                    var clips = (a.Flags & FHumStart) != 0 ? pl.humStartClips : pl.humClips;
                    if (clips == null || a.HumSel >= clips.Length) on = false;
                    else
                    {
                        if (_hum == null)
                        {
                            // its own object under the model (the model's head transform can sit in an inactive part of the
                            // prefab: a source there never played — actstest run 1), kept at the head
                            var voice = new GameObject("TLDRevampVoice");
                            voice.transform.SetParent(Mp.transform, false);
                            _hum = Like(voice, pl.humSource);
                        }
                        var c = clips[a.HumSel];
                        if (_hum.clip != c) { _hum.clip = c; _hum.Play(); }
                        else if (!_hum.isPlaying) _hum.Play();   // as the game: the loop clip restarts while the key is held
                        _humVol = a.HumVol;
                        _hum.pitch = a.HumPitch;
                    }
                }
                if (_hum == null) return;
                if (!on) _humVol = Mathf.Lerp(_hum.volume, 0f, Time.deltaTime * (pl.humLerp > 0f ? pl.humLerp : 5f));
                _hum.volume = _humVol;
                if (!on && _hum.volume < 0.01f && _hum.isPlaying) _hum.Stop();
            }

            private void Piss(fpscontroller pl, Acts a)
            {
                bool emit = a != null && (a.Flags & FPiss) != 0, snd = a != null && (a.Flags & FPissSound) != 0;
                if ((emit || snd) && _ps == null && !_psFailed) Build(pl);
                if (_ps == null) return;
                var em = _ps.emission;
                if (emit)
                {
                    var T = Mp.anim != null && Mp.anim.T != null ? Mp.anim.T : Mp.transform;
                    _ps.transform.SetPositionAndRotation(T.TransformPoint(a.PissPos), Quaternion.LookRotation(T.TransformDirection(a.PissDir.sqrMagnitude > 1e-4f ? a.PissDir : Vector3.forward)));
                    var main = _ps.main;
                    main.startSpeed = a.PissSpeed;
                    main.startColor = (Color)a.PissColor;
                    em.rateOverTime = a.PissRate;
                    em.rateOverDistance = a.PissRate * 2f;
                    em.enabled = true;
                }
                else em.enabled = false;
                if (_pour != null)
                {
                    if (snd) { _pour.volume = a.PissVol; if (!_pour.isPlaying) _pour.Play(); }
                    else if (_pour.isPlaying) _pour.Stop();
                }
            }

            /// A copy of the local player's own pour particles (same prefab for every player), stripped to a display.
            private void Build(fpscontroller pl)
            {
                _psFailed = true;
                var pc = pl.piss;
                if (pc == null || pc.ps == null) return;
                var holder = new GameObject("TLDRevampActsHolder");
                holder.SetActive(false);   // no Awake/OnEnable on the copy before its behaviours are gone
                var go = Object.Instantiate(pc.ps.gameObject, holder.transform);
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
                foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
                foreach (var src in go.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(src);
                go.transform.SetParent(Mp.transform, false);
                Object.Destroy(holder);
                go.name = "TLDRevampRemotePour";
                _ps = go.GetComponent<ParticleSystem>();
                if (_ps == null) { Object.Destroy(go); return; }
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var c = ps.collision; c.sendCollisionMessages = false;   // shows the splash, fills nothing here
                }
                var e = _ps.emission; e.enabled = false;
                go.SetActive(true);
                if (!_ps.isPlaying) _ps.Play();
                if (pc.SSource != null && pc.SSource.clip != null)
                {
                    _pour = Like(go, pc.SSource);
                    _pour.clip = pc.SSource.clip; _pour.loop = pc.SSource.loop;
                }
                _psFailed = false;
            }
        }
    }
}
