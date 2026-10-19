using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Rabbits in a session (the spawner: Fixes.Rabbits). A rabbit (nyulscript + aiscript + movecreaturescript on one
    /// body, a ragdoll when it dies) is a shared item like any other: one machine simulates it, the others show a
    /// kinematic copy. Before, that was all:
    ///  - a copy thought on its own. aiscript.Update has no "am I a copy" test (newAiScript's has), so every copy ran its
    ///    own senses, fled, roared and attacked on its own timing - its playerDeathTrigger came on and hurt the local
    ///    player whenever the copy, not the rabbit, decided to bite; its animation was its own decision, not the owner's.
    ///  - a rabbit spawned in a session was shared before its Start rolled its size (scale, mass, speed, health, evil
    ///    above 0.75): the copies were made from size 0 - small, harmless rabbits where the owner had a big killer.
    ///  - a bite hurt the owner's own player only (the trigger only meets local bodies; remote bodies have no colliders).
    ///  - a rabbit and the car it ran into were simulated on two machines: on the rabbit's owner the car was a kinematic
    ///    copy (a wall: the rabbit stopped dead), on the car's owner the rabbit was one (infinite mass: a train). The
    ///    same crash two ways, neither the game's.
    /// Now:
    ///  - the owner thinks; a copy shows the owner's rabbit: its run animation, roar and attack (the same clip), evil eyes,
    ///    lost eyes and ears, its idle and death sounds. A copy never bites.
    ///  - size is rolled before the rabbit is shared, so every copy is the same rabbit.
    ///  - the owner's rabbit bites remote players its trigger reaches (the damage goes to them, checked by the host).
    ///  - one machine simulates a rabbit and what it touches, as in single player: the server gives a rabbit to the
    ///    machine whose player - or moving car - is nearest it (hysteresis); a car simulated here that comes at another
    ///    machine's rabbit takes it before the touch, and a rabbit simulated here takes the parked car or loose item it
    ///    is about to hit. Car flips, pushes and run-overs are then the game's own physics, on one machine, and every
    ///    other machine sees the result.
    /// A/B: RabbitSync false = before (copies think on their own, no events), RabbitOwners false = no ownership rules.
    public static partial class Entities
    {
        public const byte RabbitState = 90, RabbitFx = 91;
        public static bool RabbitSync = true, RabbitOwners = true;
        public static float RabbitNearM = 60f, RabbitSwitchM = 5f, RabbitHoldS = 2f, RabbitCarMoveMps = 1.5f;
        public static float RabbitClaimM = 0.5f, RabbitClaimSteps = 4f;   // claim before the touch: margin + closing speed × physics steps
        public static long RabbitStatesSent, RabbitStatesApplied, RabbitStatesStale, RabbitFxSent, RabbitFxApplied, RabbitDamageSent,
                           RabbitOwnerMoves, RabbitClaimedByCar, RabbitClaimedObjects, RabbitBreakHitsSent, RabbitBreakHitsSkipped, RabbitSizesRolled;
        public static string RabbitLastMove = "";
        public static float RabbitNextSize = -1f;   // test: the next shared rabbit's size (bridge `set`)

        private const byte RfRoar = 0, RfAttack = 1, RfIdle = 2, RfDie = 3, RfEyes = 4, RfEars = 5, RfEvil = 6;

        private static readonly AccessTools.FieldRef<nyulscript, float> NyLastRoar = AccessTools.FieldRefAccess<nyulscript, float>("lastRoarTime");
        private static readonly AccessTools.FieldRef<nyulscript, float> NyLastAttack = AccessTools.FieldRefAccess<nyulscript, float>("lastAttackTime");
        private static readonly AccessTools.FieldRef<nyulscript, float> NyLastSee = AccessTools.FieldRefAccess<nyulscript, float>("lastSeeTarget");
        private static readonly AccessTools.FieldRef<nyulscript, bool> NyEvil = AccessTools.FieldRefAccess<nyulscript, bool>("alreadyEvil");
        private static readonly AccessTools.FieldRef<nyulscript, GameObject> NyIdleGo = AccessTools.FieldRefAccess<nyulscript, GameObject>("GIdleSound");

        private sealed class RabView { public byte Flags; public float Run, ShownRun; public Vector3 Interest; public bool Has; }
        private sealed class RabSent { public byte Flags; public byte Run; public Vector3 Interest; public float At = -10f; }
        private static readonly Dictionary<nyulscript, RabView> RabViews = new Dictionary<nyulscript, RabView>();
        private static readonly Dictionary<nyulscript, RabSent> RabLastSent = new Dictionary<nyulscript, RabSent>();
        private static readonly Dictionary<nyulscript, bool> RabGreen = new Dictionary<nyulscript, bool>();   // evil from an EMP (green eyes), not by size
        private static readonly List<Ent> RabEnts = new List<Ent>();
        private static float _rabScanAt, _rabSendAt, _rabDmgFlushAt, _rabBreakFlushAt, _rabSrvAcc;
        private static bool _rabReplay;

        private static nyulscript NyOf(Ent e) => e != null && e.Root != null && e.PartIndex < 0 ? e.Root.nyul : null;
        private static Ent RabEnt(nyulscript ny)
        {
            if (ny == null) return null;
            var it = ny.GetComponentInParent<tosaveitemscript>();
            foreach (var e in RabEnts) if (e.Root == it) return e;
            foreach (var e in ByNet.Values) if (e.Root == it && e.PartIndex < 0) return e;
            return null;
        }
        private static bool RabCopy(Component c) => RabbitSync && InSession && !_rabReplay && IsProxy(c);

        // ================================================================ sharing: the size first
        /// From ShareItem: a rabbit not yet started rolls its size now, as its Start would (Start then leaves it): the
        /// record that goes out carries the rabbit the owner will have.
        internal static void RabbitBeforeShare(tosaveitemscript root)
        {
            if (!RabbitSync || root == null || root.nyul == null || root.nyul.started) return;
            root.nyul.SetBasedOnSize(RabbitNextSize >= 0f ? RabbitNextSize : Random.Range(0f, 1f));
            RabbitNextSize = -1f;
            RabbitSizesRolled++;
        }

        /// Fixes.Rabbits: the player's spawner made one.
        internal static void RabbitSpawned(GameObject go) { if (InSession && go != null) ShareSpawn(go); }

        // ================================================================ copies don't think
        [HarmonyPatch(typeof(aiscript), "Update")]
        private static class RabAiUpdate { [HarmonyPrefix] private static bool Prefix(aiscript __instance) => !RabCopy(__instance); }

        [HarmonyPatch(typeof(movecreaturescript), "Update")]
        private static class RabMoveUpdate
        {
            [HarmonyPrefix]
            private static bool Prefix(movecreaturescript __instance)
            {
                if (RabCopy(__instance)) return false;
                if (RabCharges.Count > 0) Charge(__instance);
                return true;
            }
        }

        // ---- test staging (bridge `mp rabbitdo <net> charge <gx> <gz> <m/s> <s>`): the rabbit runs at that point, at
        // that speed, on whichever machine simulates it - a hit that does not depend on what the rabbit feels like
        private sealed class RabCharge { public Vector3d At; public float Speed, Until, OwnSpeed; }
        private static readonly Dictionary<movecreaturescript, RabCharge> RabCharges = new Dictionary<movecreaturescript, RabCharge>();
        private static void Charge(movecreaturescript m)
        {
            if (!RabCharges.TryGetValue(m, out var c)) return;
            if (Time.time > c.Until) { m.speed = c.OwnSpeed; RabCharges.Remove(m); return; }
            var d = Vector3.ProjectOnPlane(mainscript.UnityPosFromGlobal(c.At) - m.transform.position, Vector3.up);
            m.speed = c.Speed;
            m.moveDir = d.normalized;
            m.moveRot = Vector3.SignedAngle(m.transform.forward, d, Vector3.up) * 10f;
        }

        /// The owner's rabbit: its idle sound also for a remote player close to it (the game asks only the local
        /// player); every sound it makes here is captured and goes to the copies.
        [HarmonyPatch(typeof(nyulscript), "Update")]
        private static class RabNyUpdate
        {
            [HarmonyPrefix]
            private static bool Prefix(nyulscript __instance)
            {
                if (RabCopy(__instance)) return false;
                if (InSession && RabbitSync) { _voiceOf = __instance; _voiceKind = RfIdle; _voiceClip = null; }
                return true;
            }
            [HarmonyPostfix]
            private static void Postfix(nyulscript __instance)
            {
                if (_voiceOf != __instance) return;
                if (_voiceClip == null) RemoteIdle(__instance);
                Voiced(__instance);
            }
            [HarmonyFinalizer] private static System.Exception Finalizer(System.Exception __exception) { _voiceOf = null; return __exception; }
        }

        /// A remote player within the rabbit's idle distance (sqr, as the game compares) while the local player is not.
        private static void RemoteIdle(nyulscript ny)
        {
            if (!ny.idleSound || ny.Sidle == null || ny.Sidle.Length == 0 || Time.time - ny.lastIdle <= ny.currentIdle) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl != null && (pl.transform.position - ny.transform.position).sqrMagnitude < ny.idleDistance) return;   // the game's own case
            bool near = false;
            foreach (var t in RemotePlayers.AiTargets()) if ((t.body.position - ny.transform.position).sqrMagnitude < ny.idleDistance) { near = true; break; }
            if (!near) return;
            ny.lastIdle = Time.time;
            ny.currentIdle = Random.Range(ny.minIdle, ny.maxIdle);
            NyIdleGo(ny) = mainscript.PlayClipAtPoint(ny.Sidle[Random.Range(0, ny.Sidle.Length)], ny.transform.position, 1f, mainscript.AudioPriorities[3]);
        }

        // ---- the owner's sounds: whatever clip the game picked is the one the copies play
        private static nyulscript _voiceOf; private static byte _voiceKind; private static AudioClip _voiceClip;

        [HarmonyPatch]
        private static class RabClipCapture
        {
            [HarmonyTargetMethods]
            private static IEnumerable<System.Reflection.MethodBase> Targets()
            {
                foreach (var m in typeof(mainscript).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (m.Name == nameof(mainscript.PlayClipAtPoint) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(AudioClip)) yield return m;
            }
            [HarmonyPostfix] private static void Postfix(AudioClip __0) { if (_voiceOf != null && _voiceClip == null) _voiceClip = __0; }
        }

        [HarmonyPatch(typeof(nyulscript), nameof(nyulscript.Roar))]
        private static class RabRoar
        {
            [HarmonyPrefix] private static bool Prefix(nyulscript __instance) { if (RabCopy(__instance)) return false; Voice(__instance, RfRoar); return true; }
            [HarmonyPostfix] private static void Postfix(nyulscript __instance) => Voiced(__instance);
            [HarmonyFinalizer] private static System.Exception Finalizer(System.Exception __exception) { _voiceOf = null; return __exception; }
        }

        [HarmonyPatch(typeof(nyulscript), nameof(nyulscript.Attack))]
        private static class RabAttack
        {
            [HarmonyPrefix] private static bool Prefix(nyulscript __instance) { if (RabCopy(__instance)) return false; Voice(__instance, RfAttack); return true; }
            [HarmonyPostfix] private static void Postfix(nyulscript __instance) => Voiced(__instance);
            [HarmonyFinalizer] private static System.Exception Finalizer(System.Exception __exception) { _voiceOf = null; return __exception; }
        }

        /// A copy dies silently (its body follows the owner's ragdoll, a shot's break is replayed): the owner's death
        /// sound comes as an event.
        [HarmonyPatch(typeof(nyulscript), nameof(nyulscript.Die))]
        private static class RabDie
        {
            [HarmonyPrefix]
            private static void Prefix(nyulscript __instance, ref bool _playSound)
            {
                if (!InSession || !RabbitSync || _rabReplay) return;
                if (IsProxy(__instance)) { _playSound = false; return; }
                Voice(__instance, RfDie);
            }
            [HarmonyPostfix] private static void Postfix(nyulscript __instance) => Voiced(__instance);
            [HarmonyFinalizer] private static System.Exception Finalizer(System.Exception __exception) { _voiceOf = null; return __exception; }
        }

        private static void Voice(nyulscript ny, byte kind)
        {
            if (!InSession || !RabbitSync || _rabReplay) return;
            _voiceOf = ny; _voiceKind = kind; _voiceClip = null;
        }

        private static void Voiced(nyulscript ny)
        {
            if (_voiceOf != ny) return;
            var clip = _voiceClip; byte kind = _voiceKind;
            _voiceOf = null; _voiceClip = null;
            if (clip == null && kind != RfRoar && kind != RfAttack) return;   // roar / attack: the animation goes out even without a sound
            var arr = kind == RfRoar ? ny.Sroar : kind == RfAttack ? ny.Sattack : kind == RfIdle ? ny.Sidle : ny.Sdie;
            int idx = clip != null && arr != null ? System.Array.IndexOf(arr, clip) : -1;
            SendRabbitFx(ny, kind, idx);
        }

        private static void SendRabbitFx(nyulscript ny, byte kind, int arg)
        {
            var e = RabEnt(ny);
            if (e == null || e.NetId == 0) return;
            W.Reset(); W.U8(RabbitFx); W.U32(e.NetId); W.U8(kind); W.VarU32((uint)(arg + 1));
            ToServer(W, true); RabbitFxSent++;
        }

        /// A copy: the owner's roar, bite, voice, death sound; anyone's shot that took its eyes or ears; the owner's EMP evil.
        private static void ApplyRabbitFx(NetReader r)
        {
            uint net = r.U32(); byte kind = r.U8(); int arg = (int)r.VarU32() - 1;
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || !Resolve(e)) return;
            var ny = NyOf(e);
            if (ny == null) return;
            bool copy = IsProxy(ny);
            _rabReplay = true;
            try
            {
                switch (kind)
                {
                    case RfEyes: if (ny.senses != null && ny.senses.hasEyes) ny.senses.LooseEyes(); break;
                    case RfEars: if (ny.senses != null && ny.senses.hasEars) ny.senses.LooseEars(); break;
                    case RfEvil: if (copy) { RabGreen[ny] = arg == 0; ny.GetEvil(natural: arg != 0); } break;
                    default:
                        if (!copy) return;
                        var arr = kind == RfRoar ? ny.Sroar : kind == RfAttack ? ny.Sattack : kind == RfIdle ? ny.Sidle : ny.Sdie;
                        var clip = arr != null && arg >= 0 && arg < arr.Length ? arr[arg] : null;
                        if (kind == RfRoar)
                        {
                            if (clip != null) mainscript.PlayClipAtPoint(clip, ny.transform.position, 1f, 0.985f, spati: true, mainscript.AudioPriorities[3]);
                            if (ny.anim != null) ny.anim.SetTrigger("Roar");
                        }
                        else if (kind == RfAttack)
                        {
                            if (clip != null) mainscript.PlayClipAtPoint(clip, ny.transform.position, 1f, mainscript.AudioPriorities[3]);
                            if (ny.anim != null) ny.anim.SetTrigger("Attack");
                        }
                        else if (kind == RfIdle) { if (clip != null) NyIdleGo(ny) = mainscript.PlayClipAtPoint(clip, ny.transform.position, 1f, mainscript.AudioPriorities[3]); }
                        else if (clip != null) mainscript.PlayClipAtPoint(clip, ny.transform.position, 1f, mainscript.AudioPriorities[3]);
                        break;
                }
                RabbitFxApplied++;
            }
            finally { _rabReplay = false; }
        }

        /// Eyes and ears: a shot rolls them (breakchilds.TryBreakShoot) on the shooter's screen, the copy's or the owner's
        /// - that roll is the one everybody gets.
        [HarmonyPatch(typeof(sensoryscript), nameof(sensoryscript.LooseEyes))]
        private static class RabEyes
        {
            [HarmonyPrefix] private static void Prefix(sensoryscript __instance, out bool __state) => __state = __instance.hasEyes;
            [HarmonyPostfix] private static void Postfix(sensoryscript __instance, bool __state) { if (__state) SenseLost(__instance, RfEyes); }
        }

        [HarmonyPatch(typeof(sensoryscript), nameof(sensoryscript.LooseEars))]
        private static class RabEars
        {
            [HarmonyPrefix] private static void Prefix(sensoryscript __instance, out bool __state) => __state = __instance.hasEars;
            [HarmonyPostfix] private static void Postfix(sensoryscript __instance, bool __state) { if (__state) SenseLost(__instance, RfEars); }
        }

        private static void SenseLost(sensoryscript s, byte kind)
        {
            if (!InSession || !RabbitSync || _rabReplay) return;
            var ny = s.GetComponentInParent<nyulscript>();
            if (ny != null) SendRabbitFx(ny, kind, 0);
        }

        /// Evil: by size (the record carries it), or an EMP near it (green eyes) - on the owner's machine; a copy turns
        /// with the owner's.
        [HarmonyPatch(typeof(nyulscript), nameof(nyulscript.GetEvil))]
        private static class RabEvil
        {
            [HarmonyPrefix]
            private static bool Prefix(nyulscript __instance, out bool __state)
            {
                __state = NyEvil(__instance);
                return !RabCopy(__instance);
            }
            [HarmonyPostfix]
            private static void Postfix(nyulscript __instance, bool natural, bool __state)
            {
                if (__state || !NyEvil(__instance)) return;
                RabGreen[__instance] = !natural;
                if (InSession && RabbitSync && !_rabReplay && !IsProxy(__instance)) SendRabbitFx(__instance, RfEvil, natural ? 1 : 0);
            }
        }

        // ================================================================ the owner's state out, copies shown
        private static byte RabFlags(nyulscript ny)
        {
            var a = ny.AI;
            bool alive = a != null && a.alive;
            return (byte)((a != null && a.fleeing ? 1 : 0) | (ny.roaring ? 2 : 0) | (ny.attacking ? 4 : 0) | (NyEvil(ny) ? 8 : 0) |
                          (RabGreen.TryGetValue(ny, out bool g) && g ? 16 : 0) | (ny.senses != null && !ny.senses.hasEyes ? 32 : 0) |
                          (ny.senses != null && !ny.senses.hasEars ? 64 : 0) | (alive ? 0 : 128));
        }

        /// Per frame (Entities.Tick).
        private static void RabbitTick()
        {
            float now = Time.unscaledTime;
            if (now >= _rabScanAt)
            {
                _rabScanAt = now + 1f;
                RabEnts.Clear();
                foreach (var e in ByNet.Values) if (NyOf(e) != null) RabEnts.Add(e);
                Prune(RabViews); Prune(RabLastSent); Prune(RabGreen);
            }
            if (!RabbitSync || RabEnts.Count == 0) return;
            bool send = RemotePlayers.Count > 0 && now >= _rabSendAt;
            if (send) _rabSendAt = now + 0.1f;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            foreach (var e in RabEnts)
            {
                var ny = NyOf(e);
                if (ny == null) continue;
                if (e.Proxy)
                {
                    // a copy never bites; it shows the owner's
                    if (ny.playerDeathTrigger != null && ny.playerDeathTrigger.activeSelf) ny.playerDeathTrigger.SetActive(false);
                    if (!RabViews.TryGetValue(ny, out var v) || !v.Has) continue;
                    ShowRabbit(ny, v, k);
                    continue;
                }
                if (ny.AI != null && ny.AI.alive && ny.attacking && ny.playerDeathTrigger != null && ny.playerDeathTrigger.activeSelf) RabbitRemoteBite(e, ny);
                if (!send || e.NetId == 0) continue;
                byte f = RabFlags(ny);
                byte run = (byte)Mathf.Clamp(Mathf.RoundToInt(ny.runValue * 200f), 0, 255);
                var interest = ny.AI != null ? ny.AI.interestPos - ny.transform.position : Vector3.zero;
                if (!Finite(interest) || interest.sqrMagnitude > 1e8f) interest = Vector3.zero;
                if (!RabLastSent.TryGetValue(ny, out var ls)) RabLastSent[ny] = ls = new RabSent();
                if (ls.Flags == f && Mathf.Abs(ls.Run - run) <= 2 && (ls.Interest - interest).sqrMagnitude < 0.25f && now - ls.At < 1f) continue;
                ls.Flags = f; ls.Run = run; ls.Interest = interest; ls.At = now;
                // "seen its target lately" (no second roar for a new owner): the nyulscript's own forget time
                bool seen = Time.time - NyLastSee(ny) < ny.targetForgetTime;
                W.Reset(); W.U8(RabbitState); W.U32(e.NetId); W.U8(f); W.U8(run); W.Bool(seen);
                W.F32(interest.x); W.F32(interest.y); W.F32(interest.z);
                ToServer(W, false); RabbitStatesSent++;
            }
            RabbitDamageFlush(now);
            RabbitBreakFlush(now);
        }

        private static void Prune<T>(Dictionary<nyulscript, T> d)
        {
            List<nyulscript> gone = null;
            foreach (var key in d.Keys) if (key == null) (gone ?? (gone = new List<nyulscript>())).Add(key);
            if (gone != null) foreach (var key in gone) d.Remove(key);
        }

        private static readonly Dictionary<nyulscript, bool> RabSeen = new Dictionary<nyulscript, bool>();
        private static void ApplyRabbitState(NetReader r)
        {
            uint net = r.U32(); byte f = r.U8(); byte run = r.U8(); bool seen = r.Bool();
            var interest = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Bad || !Finite(interest) || !ByNet.TryGetValue(net, out var e) || !e.Proxy || !Resolve(e)) return;
            var ny = NyOf(e);
            if (ny == null) return;
            if (!RabViews.TryGetValue(ny, out var v)) RabViews[ny] = v = new RabView { ShownRun = ny.runValue };
            v.Flags = f; v.Run = run / 200f; v.Interest = interest; v.Has = true;
            RabSeen[ny] = seen;
            RabbitStatesApplied++;
        }

        private static void ShowRabbit(nyulscript ny, RabView v, float k)
        {
            v.ShownRun = Mathf.Lerp(v.ShownRun, v.Run, k);
            ny.runValue = v.ShownRun;
            if (ny.anim != null && ny.anim.isActiveAndEnabled) ny.anim.SetFloat("RunValue", Mathf.Clamp(v.ShownRun * 2f, 0f, 1f));
            ny.roaring = (v.Flags & 2) != 0; ny.attacking = (v.Flags & 4) != 0;
            if (ny.AI != null) ny.AI.fleeing = (v.Flags & 1) != 0;
            _rabReplay = true;
            try
            {
                if ((v.Flags & 8) != 0 && !NyEvil(ny)) { RabGreen[ny] = (v.Flags & 16) != 0; ny.GetEvil(natural: (v.Flags & 16) == 0); }
                if ((v.Flags & 32) != 0 && ny.senses != null && ny.senses.hasEyes) ny.senses.LooseEyes();
                if ((v.Flags & 64) != 0 && ny.senses != null && ny.senses.hasEars) ny.senses.LooseEars();
            }
            finally { _rabReplay = false; }
        }

        /// SetProxy: a rabbit changing hands. Made a copy: it stops biting here. Made ours: it carries on from the
        /// owner's last word - where it was heading, roar and bite over, its last speed as "last frame's" (aiscript kills a
        /// rabbit whose speed jumps: accelerationDeath - the copy's stale one against the handed-over speed killed it).
        internal static void RabbitRole(Ent e, bool proxy)
        {
            var ny = NyOf(e);
            if (ny == null || !RabbitSync) return;
            var a = ny.AI;
            if (ny.playerDeathTrigger != null) ny.playerDeathTrigger.SetActive(false);
            if (ny.attacking || ny.roaring) { ny.attacking = false; ny.roaring = false; if (a != null) a.goToTarget = true; }
            if (ny.move != null) ny.move.modifier = false;
            if (proxy || a == null) return;
            if (a.move != null && a.move.RB != null) a.lastVelocity = a.move.RB.velocity;
            a.fleeing = false;   // the stopFleeing coroutine ran on the old owner: its next threat sets it again here
            if (RabViews.TryGetValue(ny, out var v) && v.Has) a.interestPos = ny.transform.position + v.Interest;
            if (RabSeen.TryGetValue(ny, out bool seen) && seen) NyLastSee(ny) = Time.time;
            RabViews.Remove(ny);
        }

        // ================================================================ bites on remote players, and on others' things
        private static readonly Dictionary<int, float> _rabDmg = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _rabDmgDir = new Dictionary<int, Vector3>(), _rabDmgAt = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, uint> _rabDmgNet = new Dictionary<int, uint>();

        /// The owner's rabbit biting (its playerDeathTrigger on): a remote body inside the trigger takes damagePlayer ×
        /// deltaTime, as OnTriggerStay gives the local player - sent to the victim ten times a second.
        private static void RabbitRemoteBite(Ent e, nyulscript ny)
        {
            var ts = ny.attackTriggerScript;
            var col = ts != null ? ts.selfCol : null;
            if (col == null || !col.enabled || RemotePlayers.Count == 0) return;
            foreach (var t in RemotePlayers.AiTargets())
            {
                Vector3 hit = default; bool touch = false;
                for (int i = 0; i <= 6 && !touch; i++)
                {
                    var p = Vector3.Lerp(t.feet, t.top, i / 6f);
                    var c = col.ClosestPoint(p);
                    if ((c - p).sqrMagnitude <= BodyR * BodyR) { touch = true; hit = c; }
                }
                if (!touch) continue;
                _rabDmg.TryGetValue(t.id, out float acc);
                _rabDmg[t.id] = acc + ts.damagePlayer * Time.deltaTime;
                _rabDmgDir[t.id] = t.body.position - ny.transform.position;
                _rabDmgAt[t.id] = hit; _rabDmgNet[t.id] = e.NetId;
            }
        }

        private static void RabbitDamageFlush(float now)
        {
            if (_rabDmg.Count == 0 || now < _rabDmgFlushAt) return;
            _rabDmgFlushAt = now + 0.1f;
            foreach (var kv in _rabDmg) { PlayerCombat.SendInstant(kv.Key, kv.Value, _rabDmgDir[kv.Key], _rabDmgAt[kv.Key], _rabDmgNet[kv.Key], 0); RabbitDamageSent++; }
            _rabDmg.Clear(); _rabDmgDir.Clear(); _rabDmgAt.Clear(); _rabDmgNet.Clear();
        }

        /// The bite also breaks what it is in (lamps, glass, other rabbits): breakablescript.TryBreak(damage × dt) every
        /// frame. On a copy that is a hit for its owner (Entities.Ai) - once a frame, reliable. A breakable that keeps no
        /// damage (decreaseAfterAttack off) breaks only from one frame's force, which the copy can tell itself; the rest is
        /// summed and goes ten times a second as so many equal calls.
        private static bool _rabTrigger;
        private static readonly Dictionary<playerdeathtriggerscript, bool> _rabTriggerOf = new Dictionary<playerdeathtriggerscript, bool>();

        [HarmonyPatch(typeof(playerdeathtriggerscript), "OnTriggerStay")]
        private static class RabTriggerStay
        {
            [HarmonyPrefix]
            private static void Prefix(playerdeathtriggerscript __instance)
            {
                if (!InSession || !RabbitSync) return;
                if (!_rabTriggerOf.TryGetValue(__instance, out bool rab)) _rabTriggerOf[__instance] = rab = __instance.GetComponentInParent<nyulscript>() != null;
                _rabTrigger = rab;
            }
            [HarmonyFinalizer] private static System.Exception Finalizer(System.Exception __exception) { _rabTrigger = false; return __exception; }
        }

        private sealed class RabHit { public float Sum; public int N; }
        private static readonly Dictionary<breakablescript, RabHit> _rabHits = new Dictionary<breakablescript, RabHit>();

        /// Entities.Ai CopyBreakForce: true = handled here.
        internal static bool RabbitBreakOnCopy(breakablescript br, float force)
        {
            if (!_rabTrigger) return false;
            if (!br.decreaseAfterAttack && force < br.health) { RabbitBreakHitsSkipped++; return true; }
            if (!_rabHits.TryGetValue(br, out var h)) _rabHits[br] = h = new RabHit();
            h.Sum += force; h.N++;
            return true;
        }

        private static void RabbitBreakFlush(float now)
        {
            if (_rabHits.Count == 0 || now < _rabBreakFlushAt) return;
            _rabBreakFlushAt = now + 0.1f;
            foreach (var kv in _rabHits)
            {
                if (kv.Key == null || !BreakRef(kv.Key, out uint net, out int bi)) continue;
                W.Reset(); W.U8(BreakHit); W.U32(net); W.VarU32((uint)bi); W.U8(3); W.F32(kv.Value.Sum / kv.Value.N); W.F32(0f); W.F32(0f); W.F32(0f); W.VarU32((uint)kv.Value.N);
                ToServer(W, true); RabbitBreakHitsSent++;
            }
            _rabHits.Clear();
        }

        // ================================================================ one machine simulates a rabbit and what it hits
        /// Server, four times a second: each rabbit to the machine whose player or moving car (driven, or faster than
        /// RabbitCarMoveMps) is nearest it, within RabbitNearM, by RabbitSwitchM more than its owner's - not within
        /// RabbitHoldS of a claim (a car about to hit it, it about to hit something).
        private static readonly List<KeyValuePair<int, Vector3d>> _rabAnchors = new List<KeyValuePair<int, Vector3d>>();
        private static void ServerRabbitTick(float dt)
        {
            if (Mp.Server == null || !RabbitSync || !RabbitOwners) return;
            _rabSrvAcc += dt;
            if (_rabSrvAcc < 0.25f) return;
            _rabSrvAcc = 0f;
            bool any = false;
            foreach (var se in Server.Values) if (se.Rabbit) { any = true; break; }
            if (!any) return;
            Mp.Server.ReadyPeers(_rabAnchors);
            if (!DedicatedServer.Enabled && mainscript.s != null && mainscript.s.player != null)
                _rabAnchors.Add(new KeyValuePair<int, Vector3d>(0, mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position)));
            float rt = Time.realtimeSinceStartup;
            foreach (var c in Server.Values)
                if (c.Car && c.PartIndex < 0 && c.HasState && ((c.Driven && rt - c.DrivenAt < 1f) || c.Speed > RabbitCarMoveMps))
                    _rabAnchors.Add(new KeyValuePair<int, Vector3d>(c.OwnerId, c.Where));
            if (_rabAnchors.Count < 2) return;   // one machine: nothing to decide
            double near2 = RabbitNearM * (double)RabbitNearM;
            foreach (var se in Server.Values)
            {
                if (!se.Rabbit || se.PartIndex >= 0 || se.CrashReturnTo >= 0 || se.Held || se.Stored || rt < se.RabbitHoldUntil) continue;
                int best = -1; double bestD = double.MaxValue, curD = double.MaxValue;
                foreach (var p in _rabAnchors)
                {
                    double d = (p.Value - se.Where).sqrMagnitude;
                    if (p.Key == se.OwnerId) { if (d < curD) curD = d; continue; }
                    if (d < bestD && (p.Key == 0 || KnownBy(p.Key).Contains(se.NetId))) { bestD = d; best = p.Key; }
                }
                if (best < 0 || bestD > near2) continue;
                double bd = System.Math.Sqrt(bestD), cd = curD == double.MaxValue ? double.MaxValue : System.Math.Sqrt(curD);
                if (cd <= bd + RabbitSwitchM) continue;
                RabbitLastMove = "net " + se.NetId + ": " + se.OwnerId + " -> " + best + " (" + bd.ToString("F1") + " m against " + (cd == double.MaxValue ? "none" : cd.ToString("F1")) + ")";
                OwnerLog(se, best, "rabbit: nearest player / moving car");
                se.OwnerId = best; se.Epoch++; RabbitOwnerMoves++;
                W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)best); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                CascadeOwner(se);
            }
        }

        /// A claim, at once here (as a pickup): ours from the next physics step; the server confirms or hands it back.
        private static bool ClaimNow(Ent e)
        {
            float now = Time.realtimeSinceStartup;
            if (now - e.OwnerAt < CargoClaimCooldownS) return false;
            e.OwnerAt = now;
            SetProxy(e, false);
            e.OwnerId = MyId;
            W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
            ClaimsSent++;
            return true;
        }

        private static readonly List<Ent> _rabMine = new List<Ent>();
        /// Runner.FixedUpdate (Entities.FixedTick), before the copies move: claims before the touch.
        ///  - another machine's rabbit coming at a car simulated here (or it at the rabbit): the rabbit is ours;
        ///  - a rabbit simulated here coming at a copy of a parked car or a loose item: that is ours (a driven car is its
        ///    driver's: the driver's machine takes the rabbit instead).
        private static void RabbitNearTick(float dt)
        {
            if (!RabbitSync || !RabbitOwners || RabEnts.Count == 0) return;
            _rabMine.Clear();
            foreach (var e in RabEnts)
            {
                if (e.Root == null || NyOf(e) == null) continue;
                if (!e.Proxy) { _rabMine.Add(e); continue; }
                if (e.Held || e.Stored || e.Physical || e.CrashTaking || _localCars.Count == 0) continue;
                var rb = e.Root.GetComponent<Rigidbody>();
                if (rb == null) continue;
                float rr = RabRadius(e);
                foreach (var car in _localCars)
                {
                    if (car == null || car.RB == null || car.RB.isKinematic) continue;
                    if (!NearBox(car.transform, CarLocalBounds(car), rb.position, rr, (car.RB.GetPointVelocity(rb.position) - e.LastVel).magnitude, dt)) continue;
                    if (ClaimNow(e)) { RabbitClaimedByCar++; if (IsHost) RabHold(e.NetId); }
                    break;
                }
            }
            if (_rabMine.Count == 0) return;
            foreach (var o in ByNet.Values)
            {
                if (!o.Proxy || o.Root == null || o.PartIndex >= 0 || o.Held || o.Stored || o.Physical || o.CrashTaking || NyOf(o) != null) continue;
                bool car = o.Root.car != null;
                if (car ? o.Driven : o.Root.P == null) continue;
                var orb = o.Root.GetComponent<Rigidbody>();
                if (orb == null || !orb.detectCollisions) continue;
                foreach (var m in _rabMine)
                {
                    var mrb = m.Root.GetComponent<Rigidbody>();
                    if (mrb == null || mrb.isKinematic) continue;
                    var p = mrb.position; float rr = RabRadius(m);
                    float closing = (mrb.velocity - o.LastVel).magnitude;
                    bool near = car ? NearBox(o.Root.car.transform, CarLocalBounds(o.Root.car), p, rr, closing, dt)
                                    : (orb.position - p).magnitude < rr + ItemRadius(o) + RabbitClaimM + closing * dt * RabbitClaimSteps;
                    if (!near) continue;
                    if (car ? ClaimNow(o) : ClaimCargo(o)) RabbitClaimedObjects++;
                    break;
                }
            }
        }

        private static bool NearBox(Transform t, Bounds b, Vector3 p, float r, float closing, float dt)
        {
            float m = r + RabbitClaimM + closing * dt * RabbitClaimSteps;
            if ((p - t.position).sqrMagnitude > (b.extents.magnitude + m + 2f) * (b.extents.magnitude + m + 2f)) return false;
            var lb = b; lb.Expand(2f * m / Mathf.Max(0.01f, t.lossyScale.x));
            return lb.Contains(t.InverseTransformPoint(p));
        }

        private static readonly Dictionary<Ent, float> _rabRadius = new Dictionary<Ent, float>();
        private static float RabRadius(Ent e)
        {
            if (_rabRadius.TryGetValue(e, out float r) && Time.frameCount % 300 != 0) return r;
            r = 0.3f;
            var at = e.Root.transform.position;
            foreach (var c in e.Root.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger) r = Mathf.Max(r, (c.bounds.center - at).magnitude + c.bounds.extents.magnitude);
            if (_rabRadius.Count > 200) _rabRadius.Clear();
            return _rabRadius[e] = r;
        }

        private static float ItemRadius(Ent o)
        {
            float r = 0.1f;
            foreach (var c in o.Root.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) r = Mathf.Max(r, c.bounds.extents.magnitude);
            return r;
        }

        /// ContactRelay (a root simulated here touched something): the claim before the touch missed - now.
        internal static void RabbitContact(uint net, Collision c)
        {
            if (!RabbitSync || !RabbitOwners || c.rigidbody == null || !ByNet.TryGetValue(net, out var mine) || mine.Proxy || mine.Root == null) return;
            var other = ProxyOf(c.rigidbody.transform.root);
            if (other == null && RagdollLimbOwner.TryGetValue(c.rigidbody.gameObject, out var lo)) other = lo;   // a dead rabbit's limb
            if (other == null || other.Root == null || !other.Proxy || other.Held || other.Stored || other.Physical || other.CrashTaking) return;
            bool mineRabbit = NyOf(mine) != null, otherRabbit = NyOf(other) != null;
            if (otherRabbit && mine.Root.car != null) { if (ClaimNow(other)) RabbitClaimedByCar++; return; }
            if (!mineRabbit || otherRabbit || other.PartIndex >= 0) return;
            if (other.Root.car != null) { if (!other.Driven && ClaimNow(other)) RabbitClaimedObjects++; }
            else if (other.Root.P != null && ClaimCargo(other)) RabbitClaimedObjects++;
        }

        /// Server: a rabbit just claimed stays with the claimer a moment (the nearest-player rule would take it back).
        private static void RabHold(uint net) { if (Server.TryGetValue(net, out var se) && se.Rabbit) se.RabbitHoldUntil = Time.realtimeSinceStartup + RabbitHoldS; }

        /// Server, at Share: what the record is (its root: a rabbit, a car).
        private static void ShareFacts(byte[] rec, SEnt se)
        {
            var d = RecordCodec.Decode(rec);
            if (d == null || d.items.Count == 0) return;
            se.Where = d.items[0].transform.pos;
            se.TelemetryCategory = TelemetryPrefabCategory(d.items[0].prefabID);
            uint id = d.items[0].id;
            foreach (var n in d.nyul) if (n != null && n.id == id) { se.Rabbit = true; break; }
            foreach (var c in d.car) if (c != null && c.id == id) { se.Car = true; break; }
        }

        private static void RabbitReset()
        {
            RabViews.Clear(); RabLastSent.Clear(); RabGreen.Clear(); RabEnts.Clear(); RabSeen.Clear(); _rabDmg.Clear(); _rabDmgDir.Clear(); _rabDmgAt.Clear(); _rabDmgNet.Clear();
            _rabHits.Clear(); _rabTriggerOf.Clear(); _rabRadius.Clear(); _rabMine.Clear(); RabCharges.Clear();
        }

        // ================================================================ diagnostics / test hooks
        /// Bridge `mp rabbits`: every rabbit here.
        public static string RabbitList()
        {
            var rows = new List<string>();
            foreach (var e in ByNet.Values) if (NyOf(e) != null) rows.Add(RabbitJson(e));
            return "{\"rabbits\":[" + string.Join(",", rows) + "],\"stats\":" + RabbitStats() + ",\"spawn\":" + Fixes.Rabbits.Stats() + "}";
        }

        internal static string RabbitJson(Ent e)
        {
            var ny = NyOf(e); var a = ny.AI;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var g = mainscript.GlobalFromUnityPos(ny.transform.position); var f = ny.transform.forward;
            var rb = e.Root.GetComponent<Rigidbody>();
            float mass = 0f; foreach (var b in e.Root.GetComponentsInChildren<Rigidbody>(true)) mass += b.mass;
            string anim = ny.anim != null && ny.anim.isActiveAndEnabled ? ny.anim.GetFloat("RunValue").ToString("F3", ci) : "null";
            return "{\"net\":" + e.NetId + ",\"owner\":" + e.OwnerId + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"alive\":" + (a != null && a.alive ? "true" : "false") +
                   ",\"size\":" + ny.size.ToString("F4", ci) + ",\"scale\":" + ny.transform.lossyScale.x.ToString("F4", ci) + ",\"mass\":" + mass.ToString("F2", ci) +
                   ",\"speed\":" + (ny.move != null ? ny.move.speed : 0f).ToString("F3", ci) + ",\"shootHp\":" + (ny.breakable != null ? ny.breakable.shootHealth : -1f).ToString("F2", ci) +
                   ",\"evil\":" + (NyEvil(ny) ? "true" : "false") + ",\"green\":" + (RabGreen.TryGetValue(ny, out bool gr) && gr ? "true" : "false") +
                   ",\"eyes\":" + (ny.senses != null && ny.senses.hasEyes ? "true" : "false") + ",\"ears\":" + (ny.senses != null && ny.senses.hasEars ? "true" : "false") +
                   ",\"run\":" + ny.runValue.ToString("F3", ci) + ",\"anim\":" + anim + ",\"roaring\":" + (ny.roaring ? "true" : "false") + ",\"attacking\":" + (ny.attacking ? "true" : "false") +
                   ",\"trigger\":" + (ny.playerDeathTrigger != null && ny.playerDeathTrigger.activeSelf ? "true" : "false") +
                   ",\"fleeing\":" + (a != null && a.fleeing ? "true" : "false") + ",\"ff\":" + (a != null ? a.flightFight : 0f).ToString("F2", ci) +
                   ",\"stimuli\":" + (a != null && a.senses != null ? a.senses.stimuls.Count : -1) +
                   ",\"kin\":" + (rb != null && rb.isKinematic ? "true" : "false") + ",\"vel\":" + (rb != null ? rb.velocity.magnitude : 0f).ToString("F2", ci) +
                   ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "],\"fwd\":[" + f.x.ToString("F2", ci) + "," + f.z.ToString("F2", ci) + "]}";
        }

        /// Bridge `mp rabbitdo <net> roar|attack|evil|emp|eyes|ears|size <s>|push <vx> <vy> <vz>|aim <gx> <gz>`: the owner's
        /// rabbit does it the game's way (test staging).
        public static string RabbitDo(string[] a)
        {
            if (a.Length < 3 || !uint.TryParse(a[1], out uint net) || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || NyOf(e) == null) return "{\"error\":\"no rabbit\"}";
            var ny = NyOf(e);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (a[2])
            {
                case "roar": ny.Roar(); break;
                case "attack": ny.Attack(); break;
                case "evil": ny.GetEvil(natural: true); break;
                case "emp": ny.GetEvil(natural: false); break;
                case "eyes": ny.senses.LooseEyes(); break;
                case "ears": ny.senses.LooseEars(); break;
                case "size": ny.started = false; ny.SetBasedOnSize(float.Parse(a[3], ci)); break;
                case "push": { var rb = e.Root.GetComponent<Rigidbody>(); if (rb != null) rb.velocity = new Vector3(float.Parse(a[3], ci), float.Parse(a[4], ci), float.Parse(a[5], ci)); break; }
                case "charge":
                {
                    var m = ny.move;
                    if (m == null) return "{\"error\":\"no move\"}";
                    RabCharges.TryGetValue(m, out var was);
                    RabCharges[m] = new RabCharge { At = new Vector3d(double.Parse(a[3], ci), 0, double.Parse(a[4], ci)), Speed = float.Parse(a[5], ci),
                                                    Until = Time.time + float.Parse(a[6], ci), OwnSpeed = was != null ? was.OwnSpeed : m.speed };
                    break;
                }
                case "aim": if (ny.AI != null) ny.AI.interestPos = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(a[3], ci), mainscript.GlobalFromUnityPos(ny.transform.position).y, double.Parse(a[4], ci))); break;
                default: return "{\"error\":\"what?\"}";
            }
            return RabbitJson(e);
        }

        /// Bridge `mp body <net>`: the object's root body here - for crash checks (velocity, spin, which way is up).
        public static string BodyState(uint net, string[] a = null)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            // `mp body <net> launch <m/s>`: the owner sends it off forwards at that speed (a car rolling into something)
            if (a != null && a.Length > 3 && a[2] == "launch")
            {
                var b = e.Root.GetComponent<Rigidbody>();
                if (e.Proxy || b == null || b.isKinematic) return "{\"error\":\"not simulated here\"}";
                var fw = Vector3.ProjectOnPlane(e.Root.transform.forward, Vector3.up).normalized;
                b.velocity = fw * float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rb = e.Root.GetComponent<Rigidbody>(); var t = e.Root.transform;
            var g = mainscript.GlobalFromUnityPos(t.position);
            Vector3 v = rb != null ? (rb.isKinematic ? e.LastVel : rb.velocity) : Vector3.zero, w = rb != null && !rb.isKinematic ? rb.angularVelocity : Vector3.zero;
            System.Func<Vector3, string> V = x => "[" + x.x.ToString("F3", ci) + "," + x.y.ToString("F3", ci) + "," + x.z.ToString("F3", ci) + "]";
            return "{\"net\":" + net + ",\"owner\":" + e.OwnerId + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"kin\":" + (rb != null && rb.isKinematic ? "true" : "false") +
                   ",\"mass\":" + (rb != null ? rb.mass : 0f).ToString("F1", ci) + ",\"pos\":[" + g.x.ToString("F3", ci) + "," + g.y.ToString("F3", ci) + "," + g.z.ToString("F3", ci) + "]" +
                   ",\"vel\":" + V(v) + ",\"spin\":" + V(w) + ",\"up\":" + V(t.up) + ",\"fwd\":" + V(t.forward) + ",\"t\":" + Time.realtimeSinceStartup.ToString("F3", ci) + "}";
        }

        public static string RabbitStats() =>
            "{\"sync\":" + (RabbitSync ? "true" : "false") + ",\"owners\":" + (RabbitOwners ? "true" : "false") + ",\"statesSent\":" + RabbitStatesSent + ",\"statesApplied\":" + RabbitStatesApplied +
            ",\"statesStale\":" + RabbitStatesStale + ",\"fxSent\":" + RabbitFxSent + ",\"fxApplied\":" + RabbitFxApplied + ",\"damageSent\":" + RabbitDamageSent +
            ",\"damageTaken\":" + PlayerCombat.DamageTaken + ",\"ownerMoves\":" + RabbitOwnerMoves + ",\"lastMove\":" + Json.Str(RabbitLastMove) +
            ",\"claimedByCar\":" + RabbitClaimedByCar + ",\"claimedObjects\":" + RabbitClaimedObjects + ",\"breakHitsSent\":" + RabbitBreakHitsSent +
            ",\"breakHitsSkipped\":" + RabbitBreakHitsSkipped + ",\"sizesRolled\":" + RabbitSizesRolled + "}";
    }
}
