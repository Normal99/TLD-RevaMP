using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The game's creatures (newAiScript — the haunted mansion's workers) and everything breakable, in MP.
    ///
    /// A creature is an item like any other: the owner's machine simulates it, the others show a kinematic copy. The game's
    /// creature only ever hunts the LOCAL player plus the official MP's player list (empty in our session), and a copy
    /// doesn't think at all (Update returns on a kinematic body) — so a creature ignored every player but its owner's
    /// (aiprobe run 5: the laptop in its face for 20 s, nothing; the host on the same spot, attacked at once), and the
    /// others saw a statue sliding around: no walk, no attack, no voice.
    /// Now the owner's creature sees remote players by the game's own rules (field of view, range, the first thing its
    /// eye ray meets), takes the closest seen, chases and attacks them the game's way; the damage goes to the victim's
    /// machine (DamageInstant there). The copies get the owner's state (asleep, chasing, attacking, the animation's
    /// walk/turn input, where it looks, its hands) and its voice.
    ///
    /// Breakables (bottles, windows, lamps, the creatures' bodies): a hit on a COPY — a shot, a push by a body this
    /// machine simulates — goes to the owner, which runs the game's own TryBreak there; the owner's Break is replayed on
    /// every copy (sound, slice, the creature's death) without a second gib — the gib is the owner's.
    public static partial class Entities
    {
        public const byte BreakHit = 48, BreakFx = 49, AiState = 50, AiSound = 51;
        public static long BreakHitsSent, BreakHitsRun, BreakFxSent, BreakFxApplied, AiStatesSent, AiStatesApplied, AiSoundsSent, AiSoundsApplied,
                           AiRemoteSeen, AiRemoteAttackFrames, AiDamageSent;

        private static bool _breakFxReplay;   // a copy replaying the owner's Break: no forwarding, no BreakFx back
        private static Vector3 _hitDir;       // the direction a forwarded force hit came from (wakes a creature)
        private static bool _aiSoundReplay;   // a copy playing the owner's creature voice

        // ================================================================ breakables
        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreakShoot))]
        private static class CopyBreakShoot
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, float force, Vector3 dir)
            {
                if (!InSession || _breakFxReplay || !IsProxy(__instance)) return true;
                if (!__instance.noShoot) ForwardHit(__instance, 1, force, dir);
                return false;
            }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreak), new[] { typeof(float) })]
        private static class CopyBreakForce
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, float force)
            {
                if (!InSession || _breakFxReplay || !IsProxy(__instance)) return true;
                if (_blastDepth == 0) ForwardHit(__instance, 2, force, _hitDir);   // inside a blast the owner replays the blast
                return false;
            }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreak), new[] { typeof(Rigidbody) })]
        private static class CopyBreakBody
        {
            [HarmonyPrefix]
            private static void Prefix(breakablescript __instance, Rigidbody RB) { if (RB != null) _hitDir = RB.position - __instance.transform.position; }
            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception) { _hitDir = Vector3.zero; return __exception; }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreakExplosion))]
        private static class CopyBreakBlast
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance) => !InSession || _breakFxReplay || !IsProxy(__instance);
        }

        /// A copy bumped: only a body THIS machine simulates counts (the owner sees its own bodies and the static world).
        [HarmonyPatch(typeof(breakablescript), "OnCollisionEnter")]
        private static class CopyBreakCollision
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, Collision c)
            {
                if (!InSession || !IsProxy(__instance)) return true;
                if (c.rigidbody == null || IsProxy(c.rigidbody)) return false;
                _hitDir = c.contactCount > 0 ? c.GetContact(0).point - __instance.transform.position : Vector3.zero;
                return true;
            }
            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception) { _hitDir = Vector3.zero; return __exception; }
        }

        /// The owner's (or a lone machine's) break goes out to the copies.
        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.Break))]
        private static class BreakOut
        {
            [HarmonyPrefix]
            private static void Prefix(breakablescript __instance, out bool __state) { __state = __instance.destroyed; }
            [HarmonyPostfix]
            private static void Postfix(breakablescript __instance, bool __state)
            {
                if (__state || !InSession || _breakFxReplay || IsProxy(__instance)) return;
                if (!BreakRef(__instance, out uint net, out int bi)) return;
                W.Reset(); W.U8(BreakFx); W.U32(net); W.VarU32((uint)bi);
                ToServer(W, true); BreakFxSent++;
            }
        }

        private static void ForwardHit(breakablescript br, byte kind, float force, Vector3 dir)
        {
            if (!BreakRef(br, out uint net, out int bi)) return;
            W.Reset(); W.U8(BreakHit); W.U32(net); W.VarU32((uint)bi); W.U8(kind); W.F32(force); W.F32(dir.x); W.F32(dir.y); W.F32(dir.z);
            ToServer(W, true); BreakHitsSent++;
        }

        private static bool BreakRef(breakablescript br, out uint net, out int bi)
        {
            net = 0; bi = -1;
            var e = EntOfRoot(br.transform.root);
            if (e == null) return false;
            bi = System.Array.IndexOf(br.transform.root.GetComponentsInChildren<breakablescript>(true), br);
            net = e.NetId;
            return bi >= 0;
        }

        private static breakablescript BreakableOf(uint net, int bi)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return null;
            var all = e.Root.transform.root.GetComponentsInChildren<breakablescript>(true);
            return bi >= 0 && bi < all.Length ? all[bi] : null;
        }

        private static readonly System.Reflection.MethodInfo DamageFromM = AccessTools.Method(typeof(breakablescript), "DamageFrom");

        private static void ServerBreakHit(int from, NetReader r)
        {
            uint net = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Owner: someone hit our breakable — the game's own call here.
        private static void ApplyBreakHit(NetReader r)
        {
            uint net = r.U32(); int bi = (int)r.VarU32(); byte kind = r.U8(); float force = r.F32();
            var dir = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Bad) return;
            var br = BreakableOf(net, bi);
            if (br == null || IsProxy(br) || br.destroyed) return;
            BreakHitsRun++;
            if (kind == 1) br.TryBreakShoot(force, dir);
            else
            {
                if (dir != Vector3.zero && DamageFromM != null) DamageFromM.Invoke(br, new object[] { dir });
                br.TryBreak(force);
            }
        }

        private static void ServerFwdAll(int from, NetReader r, bool reliable)
        {
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, reliable, from);
        }

        /// A copy: the owner's break, replayed with the game's own Break (its sound, slice, a creature's death) — not its gib.
        private static void ApplyBreakFx(NetReader r)
        {
            uint net = r.U32(); int bi = (int)r.VarU32();
            if (r.Bad) return;
            var br = BreakableOf(net, bi);
            if (br == null || br.destroyed || !IsProxy(br)) return;
            var gib = br.Gib; var rgib = br.reducedGib;
            _breakFxReplay = true;
            try { br.Gib = null; br.reducedGib = null; br.Break(); BreakFxApplied++; }
            finally { _breakFxReplay = false; if (br != null) { br.Gib = gib; br.reducedGib = rgib; } }
        }

        // ================================================================ creatures: the owner sees and attacks remote players
        private static readonly AccessTools.FieldRef<newAiScript, float> ChaseStartF = AccessTools.FieldRefAccess<newAiScript, float>("chaseStart");
        private static readonly AccessTools.FieldRef<newAiScript, float> CurChaseF = AccessTools.FieldRefAccess<newAiScript, float>("currentChaseTime");
        private static readonly RaycastHit[] AiHits = new RaycastHit[32];
        private static readonly Dictionary<int, float> _aiDmgAcc = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _aiDmgDir = new Dictionary<int, Vector3>();
        private static float _aiDmgFlushAt;
        private const float BodyR = 0.35f;
        private static newAiScript _aiSawRemote;

        /// Distance along the ray at which it enters remote body (feet..top), or -1; then whether anything the game's
        /// eye ray stops at (everything but glass, layer 23 — and not the creature itself if `skipSelf`) is in front of it.
        private static float SeenAt(newAiScript a, Vector3 from, Vector3 dir, float maxDist, Vector3 feet, Vector3 top, bool skipSelf)
        {
            float t = PlayerCombat.RayCapsule(new Ray(from, dir), feet, top, BodyR);
            if (t < 0f || t > maxDist) return -1f;
            int n = Physics.RaycastNonAlloc(from, dir, AiHits, t, a.lm);
            for (int i = 0; i < n; i++)
            {
                var h = AiHits[i];
                if (h.distance >= t) continue;
                if (skipSelf && h.collider.transform.IsChildOf(a.transform)) continue;
                if (h.collider.gameObject.layer != 23) return -1f;
            }
            return t;
        }

        /// The game's own test for the local player (newAiScript.Update): would it see it, and how far is it?
        private static float LocalSeen(newAiScript a)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.died) return -1f;
            var pv = pl.transform.position - a.transform.position;
            if (!(Vector3.Angle(a.head.forward, pv) < a.fov)) return -1f;
            int n = Physics.RaycastNonAlloc(a.head.position, pv, AiHits, a.range, a.lm);
            float best = float.MaxValue; bool seen = false;
            for (int i = 0; i < n; i++)
            {
                if (AiHits[i].distance >= best) continue;
                var h = AiHits[i];
                // the first non-glass hit decides
                bool glass = h.collider.gameObject.layer == 23;
                if (!glass) { best = h.distance; seen = h.collider.GetComponentInParent<fpscontroller>() != null; }
            }
            return seen ? pv.magnitude : -1f;
        }

        [HarmonyPatch(typeof(newAiScript), "Update")]
        private static class AiUpdate
        {
            [HarmonyPrefix]
            private static void Prefix(newAiScript __instance, out float __state)
            {
                __state = float.NaN;
                _aiSawRemote = null;
                var a = __instance;
                if (!InSession || RemotePlayers.Count == 0 || a.died || a.rb == null || a.rb.isKinematic || a.head == null) return;
                if (gamemodesettingsscript.s == null || gamemodesettingsscript.s.S.peacefulEnemies == 2) return;
                if (a.target == null) { a.TargetNull(); a.TargetReset(); }   // its remote target's body went away
                // the closest remote player the game's rules let it see
                Transform bestHead = null; float bestD = float.MaxValue; ulong bestSteam = 0;
                foreach (var t in RemotePlayers.AiTargets())
                {
                    var pv = t.body.position - a.transform.position;
                    float d = pv.magnitude;
                    if (d >= bestD || !(Vector3.Angle(a.head.forward, pv) < a.fov)) continue;
                    if (SeenAt(a, a.head.position, pv, a.range, t.feet, t.top, false) < 0f) continue;
                    bestD = d; bestHead = t.head; bestSteam = t.steam;
                }
                if (bestHead == null) return;
                float local = LocalSeen(a);
                if (local >= 0f && local < bestD) return;   // the game takes the local player itself
                var tg = a.target;
                tg.position = bestHead.position;
                tg.SetParent(bestHead);
                a.first = true;
                if (!a.isChasing) { CurChaseF(a) = Random.Range(a.chaseTimeMin, a.chaseTimeMax); a.SoundNotice(); }
                a.isSeeing = true;
                ChaseStartF(a) = Time.time;
                a.isChasing = true;
                a.targetSteamID = bestSteam; a.targetIsSteamID = true;
                _aiSawRemote = a;
                AiRemoteSeen++;
                __state = a.fov;
                a.fov = -1f;   // the game's own sight pass this frame finds nobody: the remote player stays its target
            }

            [HarmonyPostfix]
            private static void Postfix(newAiScript __instance)
            {
                var a = __instance;
                if (!InSession) return;
                if (a.rb != null && a.rb.isKinematic) { if (IsProxy(a)) ShowCopy(a); return; }
                if (_aiSawRemote == a) a.isSeeing = true;   // the game clears it before its sight pass
                if (RemotePlayers.Count == 0 || a.died || a.rb == null || a.currentPlayer != null) return;
                if (gamemodesettingsscript.s == null || gamemodesettingsscript.s.S.peacefulEnemies == 2) return;
                RemoteAttack(a);
            }

            [HarmonyFinalizer]
            private static System.Exception Finalizer(newAiScript __instance, float __state, System.Exception __exception)
            {
                if (!float.IsNaN(__state)) __instance.fov = __state;
                return __exception;
            }
        }

        private static bool SensTouches(sens s, Vector3 feet, Vector3 top)
        {
            if (s == null || s.selfCol == null || !s.selfCol.enabled) return false;
            for (int i = 0; i <= 6; i++)
            {
                var p = Vector3.Lerp(feet, top, i / 6f);
                if ((s.selfCol.ClosestPoint(p) - p).sqrMagnitude <= BodyR * BodyR) return true;
            }
            return false;
        }

        /// The game's attack (newAiScript.Attack) on a remote player its grab sensors reach: a clear line to the body,
        /// both hands on them, damage × deltaTime — sent to the victim ten times a second.
        private static void RemoteAttack(newAiScript a)
        {
            foreach (var t in RemotePlayers.AiTargets())
            {
                if (!SensTouches(a.sensU, t.feet, t.top) && !SensTouches(a.sensD, t.feet, t.top)) continue;
                var dir = t.body.position - a.transform.position;
                if (SeenAt(a, a.transform.position, dir, a.rayAttackDistRange * 2f, t.feet, t.top, true) < 0f) continue;
                a.target.position = t.head.position;
                a.isAttacking = true;
                var hold = (t.head.position + (a.transform.position + a.transform.forward)) * 0.5f;
                a.anim.Hold(hold, leftHand: true);
                a.anim.Hold(hold, leftHand: false);
                _aiDmgAcc.TryGetValue(t.id, out float acc);
                _aiDmgAcc[t.id] = acc + a.damage * Time.deltaTime;
                _aiDmgDir[t.id] = dir;
                AiRemoteAttackFrames++;
                break;
            }
            if (_aiDmgAcc.Count > 0 && Time.unscaledTime >= _aiDmgFlushAt)
            {
                _aiDmgFlushAt = Time.unscaledTime + 0.1f;
                foreach (var kv in _aiDmgAcc) { PlayerCombat.SendInstant(kv.Key, kv.Value, _aiDmgDir[kv.Key]); AiDamageSent++; }
                _aiDmgAcc.Clear(); _aiDmgDir.Clear();
            }
        }

        // ================================================================ creatures: copies show the owner's
        private sealed class AiView { public byte Flags; public float Fwd, Turn; public Vector3 Look; }
        private static readonly Dictionary<newAiScript, AiView> AiViews = new Dictionary<newAiScript, AiView>();
        private sealed class AiSent { public byte Flags; public sbyte Fwd, Turn; public Vector3 Look; public float At; }
        private static readonly Dictionary<newAiScript, AiSent> AiLastSent = new Dictionary<newAiScript, AiSent>();
        private static readonly List<newAiScript> AiAll = new List<newAiScript>();
        private static float _aiScanAt, _aiSendAt;

        private static void ShowCopy(newAiScript a)
        {
            if (!AiViews.TryGetValue(a, out var v)) return;
            a.sleeping = (v.Flags & 1) != 0; a.isChasing = (v.Flags & 2) != 0; a.isSeeing = (v.Flags & 4) != 0; a.isAttacking = (v.Flags & 8) != 0;
            if (a.anim != null)
            {
                a.anim.targetForward = v.Fwd; a.anim.targetTurn = v.Turn;
                a.anim.onGround = (v.Flags & 16) != 0; a.anim.duck = (v.Flags & 32) != 0;
            }
            a.TargetNull();
            if (a.target.parent != null) a.target.SetParent(null);
            a.target.position = a.transform.position + a.transform.rotation * v.Look;
            if (a.isAttacking && a.anim != null)
            {
                var hold = (a.target.position + (a.transform.position + a.transform.forward)) * 0.5f;
                a.anim.Hold(hold, leftHand: true);
                a.anim.Hold(hold, leftHand: false);
            }
            if ((v.Flags & 64) != 0 && !a.died) a.Die(_playSound: false);
        }

        private static byte AiFlags(newAiScript a) =>
            (byte)((a.sleeping ? 1 : 0) | (a.isChasing ? 2 : 0) | (a.isSeeing ? 4 : 0) | (a.isAttacking ? 8 : 0) |
                   (a.anim != null && a.anim.onGround ? 16 : 0) | (a.anim != null && a.anim.duck ? 32 : 0) | (a.died ? 64 : 0));

        /// Per frame: the owner's creatures' state out (10 Hz, on change; once a second regardless — it's unreliable).
        private static void AiTick()
        {
            if (RemotePlayers.Count == 0) return;   // nobody to show them to
            float now = Time.unscaledTime;
            if (now >= _aiScanAt)
            {
                _aiScanAt = now + 2f;
                AiAll.Clear(); AiAll.AddRange(Object.FindObjectsOfType<newAiScript>());
                var gone = new List<newAiScript>();
                foreach (var k in AiViews.Keys) if (k == null) gone.Add(k);
                foreach (var k in AiLastSent.Keys) if (k == null) gone.Add(k);
                foreach (var k in gone) { AiViews.Remove(k); AiLastSent.Remove(k); }
            }
            if (now < _aiSendAt) return;
            _aiSendAt = now + 0.1f;
            foreach (var a in AiAll)
            {
                if (a == null || IsProxy(a)) continue;
                if (!ItemRef(a.tosave != null ? a.tosave : a.GetComponentInParent<tosaveitemscript>(), out uint net, out int idx)) continue;
                byte f = AiFlags(a);
                sbyte fw = (sbyte)Mathf.Clamp(Mathf.RoundToInt((a.anim != null ? a.anim.targetForward : 0f) * 127f), -127, 127);
                sbyte tu = (sbyte)Mathf.Clamp(Mathf.RoundToInt((a.anim != null ? a.anim.targetTurn : 0f) * 127f), -127, 127);
                a.TargetNull();
                var look = Quaternion.Inverse(a.transform.rotation) * (a.target.position - a.transform.position);
                if (!AiLastSent.TryGetValue(a, out var ls)) AiLastSent[a] = ls = new AiSent { At = -10f };
                if (ls.Flags == f && ls.Fwd == fw && ls.Turn == tu && (ls.Look - look).sqrMagnitude < 0.04f && now - ls.At < 1f) continue;
                ls.Flags = f; ls.Fwd = fw; ls.Turn = tu; ls.Look = look; ls.At = now;
                W.Reset(); W.U8(AiState); W.U32(net); W.VarU32((uint)idx); W.U8(f); W.U8((byte)fw); W.U8((byte)tu);
                W.F32(look.x); W.F32(look.y); W.F32(look.z);
                ToServer(W, false); AiStatesSent++;
            }
        }

        private static newAiScript AiOf(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Items == null || idx < 0 || idx >= e.Items.Count || e.Items[idx] == null) return null;
            return e.Items[idx].GetComponentInChildren<newAiScript>(true);
        }

        private static void ApplyAiState(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32(); byte f = r.U8(); sbyte fw = (sbyte)r.U8(), tu = (sbyte)r.U8();
            var look = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Bad) return;
            var a = AiOf(net, idx);
            if (a == null || !IsProxy(a)) return;
            if (!AiViews.TryGetValue(a, out var v)) AiViews[a] = v = new AiView();
            v.Flags = f; v.Fwd = fw / 127f; v.Turn = tu / 127f; v.Look = look;
            AiStatesApplied++;
        }

        /// The owner's creature speaks: the same clip at the same pitch on every copy; a copy never speaks on its own.
        [HarmonyPatch(typeof(newAiScript), nameof(newAiScript.SoundSound), new[] { typeof(newAiScript.aiSoundType), typeof(int), typeof(float), typeof(bool) })]
        private static class AiVoice
        {
            [HarmonyPrefix]
            private static bool Prefix(newAiScript __instance) => !InSession || _aiSoundReplay || !IsProxy(__instance);

            [HarmonyPostfix]
            private static void Postfix(newAiScript __instance, newAiScript.aiSoundType _soundType, int _index, float _pitch)
            {
                if (!InSession || _aiSoundReplay || IsProxy(__instance)) return;
                if (!ItemRef(__instance.tosave != null ? __instance.tosave : __instance.GetComponentInParent<tosaveitemscript>(), out uint net, out int idx)) return;
                W.Reset(); W.U8(AiSound); W.U32(net); W.VarU32((uint)idx); W.U8((byte)_soundType); W.VarU32((uint)Mathf.Max(0, _index)); W.F32(_pitch);
                ToServer(W, true); AiSoundsSent++;
            }
        }

        private static void ApplyAiSound(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32(); int type = r.U8(); int clip = (int)r.VarU32(); float pitch = r.F32();
            if (r.Bad) return;
            var a = AiOf(net, idx);
            if (a == null || !IsProxy(a)) return;
            _aiSoundReplay = true;
            try { a.SoundSound((newAiScript.aiSoundType)Mathf.Clamp(type, 0, 4), clip, pitch, false); AiSoundsApplied++; }
            finally { _aiSoundReplay = false; }
        }

        /// Test readout (bridge `mp aisee`): every creature's view of every remote player, step by step as the owner
        /// tests it — angle vs fov, where its eye ray (vanilla's direction: root to root, from the head) meets the body,
        /// the first thing in front of it.
        public static string AiSee()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var a in Object.FindObjectsOfType<newAiScript>())
            {
                if (a.head == null) continue;
                foreach (var t in RemotePlayers.AiTargets())
                {
                    var pv = t.body.position - a.transform.position;
                    float ang = Vector3.Angle(a.head.forward, pv);
                    float tc = PlayerCombat.RayCapsule(new Ray(a.head.position, pv), t.feet, t.top, BodyR);
                    string block = "";
                    int n = Physics.RaycastNonAlloc(a.head.position, pv, AiHits, tc > 0 ? tc : a.range, a.lm);
                    float bd = float.MaxValue;
                    for (int i = 0; i < n; i++)
                        if (AiHits[i].distance < bd && AiHits[i].collider.gameObject.layer != 23) { bd = AiHits[i].distance; block = AiHits[i].collider.transform.root.name + "/" + AiHits[i].collider.name + " L" + AiHits[i].collider.gameObject.layer + " @" + bd.ToString("F2", ci); }
                    rows.Add("{\"ai\":" + Json.Str(DescribeRoot(a.transform.root)) + ",\"player\":" + t.id + ",\"dist\":" + pv.magnitude.ToString("F2", ci) +
                             ",\"angle\":" + ang.ToString("F0", ci) + ",\"fov\":" + a.fov.ToString("F0", ci) + ",\"rayAtBody\":" + tc.ToString("F2", ci) +
                             ",\"headY\":" + a.head.position.y.ToString("F2", ci) + ",\"feetY\":" + t.feet.y.ToString("F2", ci) + ",\"topY\":" + t.top.y.ToString("F2", ci) +
                             ",\"rootDy\":" + pv.y.ToString("F2", ci) + ",\"blockedBy\":" + Json.Str(block) + ",\"seenAt\":" +
                             SeenAt(a, a.head.position, pv, a.range, t.feet, t.top, false).ToString("F2", ci) + ",\"kin\":" + (a.rb != null && a.rb.isKinematic ? "true" : "false") + "}");
                }
            }
            return "{\"see\":[" + string.Join(",", rows) + "]}";
        }

        private static void AiReset() { AiViews.Clear(); AiLastSent.Clear(); AiAll.Clear(); _aiDmgAcc.Clear(); _aiDmgDir.Clear(); }

        public static string AiStats() => "{\"breakHitsSent\":" + BreakHitsSent + ",\"breakHitsRun\":" + BreakHitsRun + ",\"breakFxSent\":" + BreakFxSent +
                                          ",\"breakFxApplied\":" + BreakFxApplied + ",\"statesSent\":" + AiStatesSent + ",\"statesApplied\":" + AiStatesApplied +
                                          ",\"soundsSent\":" + AiSoundsSent + ",\"soundsApplied\":" + AiSoundsApplied + ",\"remoteSeen\":" + AiRemoteSeen +
                                          ",\"remoteAttackFrames\":" + AiRemoteAttackFrames + ",\"damageSent\":" + AiDamageSent +
                                          ",\"damageTaken\":" + PlayerCombat.DamageTaken + "}";
    }
}
