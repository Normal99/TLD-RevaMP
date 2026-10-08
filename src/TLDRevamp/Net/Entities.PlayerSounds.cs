using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A player's own sounds: footsteps, burps, farts and shits. Each was played only on the machine of the player who
    /// made it - the others saw them walk in silence. The game's own MP sends the same thing: every step the player
    /// takes (stepSoundScript.Step → syncScript.SendStepSound: place, ground type, clip, volume) and every burp, fart
    /// and shit (fpscontroller → SendSound: place, sound, clip, volume, pitch), and the others play them at that place
    /// (RecStepSound: PlayClipAtPoint with a random pitch, as a step here; RecSound: soundMainScript.PlaySound).
    /// The same here. A player model's own steps (its animation's step events, if it has any) are not played for a
    /// remote player: the steps come from the player who takes them.
    /// Steps go unreliable (a step lost is a step not heard; a resent one arrives late, and holds up the messages behind it).
    public static partial class Entities
    {
        public const byte PlayerSound = 57;
        public static long PlayerSoundsSent, PlayerSoundsPlayed, ModelStepsMuted;
        public static long[] SoundsSentBy = new long[4], SoundsPlayedBy = new long[4];
        private static string _lastSound = "null";

        private enum Snd : byte { Step, Burp, Fart, Shit }

        private static void SendPlayerSound(Snd kind, Vector3 at, int type, int clip, float volume, float pitch, int priority)
        {
            if (!InSession) return;
            var g = mainscript.GlobalFromUnityPos(at);
            W.Reset(); W.U8(PlayerSound); W.U8((byte)kind);
            W.F64(g.x); W.F64(g.y); W.F64(g.z);
            W.U8((byte)type); W.U16((ushort)clip); W.F32(volume); W.F32(pitch); W.U8((byte)priority);
            ToServer(W, kind != Snd.Step);
            PlayerSoundsSent++; SoundsSentBy[(int)kind]++;
        }

        private static void ServerPlayerSound(int from, NetReader r)
        {
            int kind = r.U8();
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, kind != (int)Snd.Step, from);
        }

        private static void ApplyPlayerSound(NetReader r)
        {
            int kind = r.U8();
            var g = new Vector3d(r.F64(), r.F64(), r.F64());
            int type = r.U8(), clip = r.U16(); float volume = r.F32(), pitch = r.F32(); int priority = r.U8();
            if (r.Bad || mainscript.s == null || kind > 3 || !Finite(g) || !Finite(volume) || !Finite(pitch)) return;
            var at = mainscript.UnityPosFromGlobal(g);
            GameObject go = null;
            if (kind == (int)Snd.Step)
            {
                var clips = mainscript.GetWalkClip((mainscript.walkSoundType)type, clip);
                if (clips == null) return;
                go = mainscript.PlayClipAtPoint(clips, at, volume, true, mainscript.AudioPriorities[3]);   // RecStepSound
            }
            else
            {
                var e = (soundenum)(kind - 1);
                if (soundMainScript.s == null || clip >= soundMainScript.s.GetLength(e)) return;
                var c = soundMainScript.s.GetClip(e, clip);
                go = mainscript.PlayClipAtPoint(c, at, volume, pitch, (byte)priority);   // soundMainScript.PlaySound
            }
            PlayerSoundsPlayed++; SoundsPlayedBy[kind]++;
            var src = go != null ? go.GetComponent<AudioSource>() : null;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            _lastSound = "{\"kind\":" + kind + ",\"gx\":" + g.x.ToString("F2", ic) + ",\"gy\":" + g.y.ToString("F2", ic) + ",\"gz\":" + g.z.ToString("F2", ic) +
                         ",\"type\":" + type + ",\"clip\":" + Json.Str(src != null && src.clip != null ? src.clip.name : "") +
                         ",\"playing\":" + (src != null && src.isPlaying ? "true" : "false") + ",\"volume\":" + volume.ToString("F3", ic) + "}";
        }

        /// The local player's steps (fpscontroller.stepScript), as the game sends them.
        [HarmonyPatch(typeof(stepSoundScript), nameof(stepSoundScript.Step), typeof(float))]
        private static class StepSounds
        {
            private static readonly AccessTools.FieldRef<stepSoundScript, mainscript.walkSoundType> Type = AccessTools.FieldRefAccess<stepSoundScript, mainscript.walkSoundType>("currentType");
            private static readonly AccessTools.FieldRef<stepSoundScript, float> Vol = AccessTools.FieldRefAccess<stepSoundScript, float>("StepSoundVolume");
            private static readonly AccessTools.FieldRef<stepSoundScript, AudioClip> Clip = AccessTools.FieldRefAccess<stepSoundScript, AudioClip>("StepSoundClip");

            [HarmonyPrefix]
            private static bool Prefix(stepSoundScript __instance, out bool __state)
            {
                __state = false;
                if (!InSession) return true;
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl != null && __instance == pl.stepScript)
                {
                    __state = true;
                    Clip(__instance) = null;   // set again only when the step plays (no ground under the foot: Step returns first)
                    return true;
                }
                // a remote player's model: its steps come from that player
                if (__instance.GetComponentInParent<mpplayerscript>() != null) { ModelStepsMuted++; return false; }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(stepSoundScript __instance, float _modifier, bool __state)
            {
                if (!__state || Clip(__instance) == null) return;
                SendPlayerSound(Snd.Step, __instance.transform.position, (int)Type(__instance), __instance.currentClipIndex,
                                Vol(__instance) * _modifier * __instance.stepSound * __instance.syncMultiplier, 1f, 0);
            }
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Burp))]
        private static class BurpSound
        {
            [HarmonyPrefix] private static void Prefix(fpscontroller __instance, out bool __state) => __state = __instance.SBurp != null && __instance.SBurp.isPlaying;
            [HarmonyPostfix] private static void Postfix(fpscontroller __instance, bool __state) => Sent(__instance.SBurp, __state, Snd.Burp, soundenum.burp);
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Fart))]
        private static class FartSound
        {
            [HarmonyPrefix] private static void Prefix(fpscontroller __instance, out bool __state) => __state = __instance.SFart != null && __instance.SFart.isPlaying;
            [HarmonyPostfix] private static void Postfix(fpscontroller __instance, bool __state) => Sent(__instance.SFart, __state, Snd.Fart, soundenum.fart);
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Shit))]
        private static class ShitSound
        {
            [HarmonyPrefix] private static void Prefix(fpscontroller __instance, out bool __state) => __state = __instance.SFart != null && __instance.SFart.isPlaying;
            [HarmonyPostfix] private static void Postfix(fpscontroller __instance, bool __state) => Sent(__instance.SFart, __state, Snd.Shit, soundenum.shit);
        }

        /// Played just now (it was not already playing - the game plays nothing then): which clip of its list it was.
        private static void Sent(AudioSource src, bool wasPlaying, Snd kind, soundenum e)
        {
            if (!InSession || wasPlaying || src == null || src.clip == null || soundMainScript.s == null) return;
            if (mainscript.s == null || mainscript.s.player == null || src.GetComponentInParent<fpscontroller>() != mainscript.s.player) return;
            int n = soundMainScript.s.GetLength(e), clip = -1;
            for (int i = 0; i < n; i++) if (soundMainScript.s.GetClip(e, i) == src.clip) { clip = i; break; }
            if (clip < 0) return;
            SendPlayerSound(kind, src.transform.position, (int)e, clip, src.volume, src.pitch, src.priority);
        }

        // ---- diagnostics / test hooks (bridge `mp sounds`, `mp sounds burp|fart|shit|step`, `mp sounds walk secs`: the
        // player walks straight ahead, the forward key held)
        private static float _walkUntil;

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class WalkTest
        {
            [HarmonyPostfix]
            private static void Postfix(inputscript __instance) { if (Time.time < _walkUntil) __instance.forward = 1f; }
        }

        public static string PlayerSoundsTest(string[] a)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            string did = "null";
            if (a.Length > 1 && pl != null)
            {
                switch (a[1])
                {
                    case "burp": pl.Burp(); break;
                    case "fart": pl.Fart(); break;
                    case "shit": pl.Shit(); break;
                    case "step": pl.stepScript.Step(1f); break;
                    case "walk": _walkUntil = Time.time + float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); break;
                    default: return "{\"error\":\"burp|fart|shit|step|walk secs\"}";
                }
                did = Json.Str(a[1]);
            }
            int models = 0, modelSteps = 0, modelStepEvents = 0;
            foreach (var mp in Object.FindObjectsOfType<mpplayerscript>())
            {
                models++;
                modelSteps += mp.GetComponentsInChildren<stepSoundScript>(true).Length;
                modelStepEvents += mp.GetComponentsInChildren<animStepEventScript>(true).Length;
            }
            return "{\"did\":" + did + ",\"sent\":" + PlayerSoundsSent + ",\"played\":" + PlayerSoundsPlayed + ",\"sentBy\":[" + string.Join(",", SoundsSentBy) +
                   "],\"playedBy\":[" + string.Join(",", SoundsPlayedBy) + "],\"modelStepsMuted\":" + ModelStepsMuted + ",\"models\":" + models +
                   ",\"modelStepScripts\":" + modelSteps + ",\"modelStepEvents\":" + modelStepEvents + ",\"playerMadeShared\":" + PlayerMadeShared + ",\"last\":" + _lastSound + "}";
        }
    }
}
