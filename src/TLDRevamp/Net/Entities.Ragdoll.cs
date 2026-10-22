using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Bodies made of limbs (tosaveitemscript.ragdollPos): a dead player's body (PlayerRagdol), the skeleton, and the
    /// creatures (the mansion's workers, the rabbits) once dead. The limbs are separate rigidbodies joined together; the
    /// item's own transform does not follow them (a dead player's body has no body of its own; a creature's limbs come off
    /// it when it dies, ragdollactivatescript.unparent). States carried that transform only, and a copy holds every body
    /// of an item still: on the other screens a dead player's body stayed in the pose it spawned in, wherever that was
    /// (in the air where the player died), and a dead creature fell its own way on every machine.
    /// The game's own MP sends every limb's pose with the item's position (tosaveitemscript.SyncRagdoll →
    /// syncScript.SendRagdoll). The same here: while limp, every state carries the limbs' poses (relative to the item);
    /// a copy keeps its limbs kinematic and follows them. A copy's creature still alive when the owner's is limp dies the
    /// game's way first (newAiScript / aiscript.Die). Grabbing a limb of a copy takes the body (pickup claim).
    public static partial class Entities
    {
        public static long RagdollStatesSent, RagdollStatesApplied, ModLimpErrors;
        private static readonly HashSet<Ent> RagdollCopies = new HashSet<Ent>();
        private static readonly Dictionary<GameObject, Ent> RagdollLimbOwner = new Dictionary<GameObject, Ent>();
        private const float RagdollFollowRate = 20f;   // 1/s: a copy's limbs close this share of the gap per second (~50 ms lag)

        public class Rag
        {
            public ragdollactivatescript Act; public bool ActLooked;
            public Vector3d[] SentPos; public Quaternion[] SentRot;   // owner: global for bodies, item-relative for vehicle joints
            public Vector3[] Pos, TPos; public Quaternion[] Rot, TRot;   // a copy: shown now / received
            public bool Has;
            // owner (RagdollDelta): the Key the copies have - its id, epoch, anchor, every limb's world pose then - and the
            // one this state carries, committed only if the state is sent
            public byte KeyId; public uint KeyEpoch; public int KeyN = -1; public float KeyAt;
            public Vector3d KeyAnchor; public Quaternion KeyAnchorRot; public Vector3d[] KeyWorld; public Quaternion[] KeyWorldRot;
            public bool Staged; public byte StagedId; public Vector3d StagedAnchor; public Quaternion StagedAnchorRot;
            public Vector3d[] StagedWorld; public Quaternion[] StagedWorldRot;
            public Transform[] HashedLimbs; public ushort Hash;
            public Vector3[] WPos; public Quaternion[] WRot;
            // a copy: the last Key (its epoch, id, anchor, every limb relative to it)
            public bool CHas; public uint CEpoch; public byte CId; public ushort CHash;
            public Vector3d CAnchor; public Quaternion CAnchorRot; public Vector3[] CRel; public Quaternion[] CRelRot;
        }

        /// A/B: a limp body's limbs go as a Key (every limb, millimetres from an anchor whose pose rides along) and then
        /// Deltas (only the limbs that moved since that Key; cumulative, so its Key and any one Delta make the whole pose
        /// and a lost Delta costs nothing). Before (false): every limb in every state whenever one moved 1 cm / 0.3 deg -
        /// a dead rabbit lying still sent 3.6 KB/s (14 limbs, 12.6 states a second) as an ear twitched (user, 2026-10-22).
        /// Vehicles' limb sets keep the frame-relative form (their frame is the moving car).
        public static bool RagdollDelta = true;
        public static float RagdollKeyEveryS = 2f;          // a fresh Key while streaming: bounds what a lost Key costs a copy
        public static float RagdollDeltaMaxShare = 0.75f;   // a Delta bigger than this share of a Key goes as a Key
        public static long RagdollKeysSent, RagdollDeltasSent, RagdollFramesSent, RagdollLimbsSent, RagdollBlockBytes;
        public static long RagdollDeltasNoKey, RagdollHashMismatch, RagdollKeysIn, RagdollDeltasIn;
        private static readonly RagdollWire.Block _ragBlock = new RagdollWire.Block();

        /// The limbs' names in order, hashed: both machines must mean the same limbs by the same index.
        private static ushort LimbHash(Rag g, Transform[] ls)
        {
            if (g.HashedLimbs == ls) return g.Hash;
            var names = new string[ls.Length];
            for (int i = 0; i < ls.Length; i++) names[i] = ls[i] != null ? ls[i].name : "";
            g.HashedLimbs = ls; g.Hash = RagdollWire.HierarchyHash(names);
            return g.Hash;
        }

        private static Transform[] Limbs(Ent e)
        {
            // a mod's own ragdoll (MpApi.RegisterRagdoll) first, then the game's limb list
            if (e.Root != null && e.PartIndex < 0 && TLDRevamp.Multiplayer.MpApi.TryGetRagdoll(e.Root.gameObject, out var modLimbs, out _)) return modLimbs;
            var rp = e.Root != null && e.PartIndex < 0 ? e.Root.ragdollPos : null;
            return rp != null && rp.childs != null && rp.childs.Length > 0 && rp.childs.Length < 64 ? rp.childs : null;
        }

        /// Limp: a body with no creature (dead player, skeleton), or a creature in its ragdoll.
        private static bool Limp(Ent e)
        {
            if (e.Rag == null) e.Rag = new Rag();
            if (e.Root != null && TLDRevamp.Multiplayer.MpApi.TryGetRagdoll(e.Root.gameObject, out _, out var isLimp))
            {
                if (isLimp == null) return true;
                try { return isLimp(); } catch (System.Exception ex) { if (ModLimpErrors++ < 5) Plugin.Log.LogWarning("mods: a ragdoll's limp test threw: " + ex.Message); return false; }
            }
            if (!e.Rag.ActLooked) { e.Rag.ActLooked = true; e.Rag.Act = e.Root.GetComponentInChildren<ragdollactivatescript>(true); }
            return e.Rag.Act == null || e.Rag.Act.ragdoll;
        }

        private static void LocalPose(Transform root, Transform limb, out Vector3 p, out Quaternion q)
        {
            var inv = Quaternion.Inverse(root.rotation);
            p = inv * (limb.position - root.position); q = inv * limb.rotation;
        }

        /// The owner: has a limb moved (1 cm / 0.3 deg) since the last state? - a body sent like any item: while it
        /// moves, one state at rest.
        private static bool RagdollMoved(Ent e)
        {
            var ls = Limbs(e);
            if (ls == null || !Limp(e)) return false;
            var r = e.Rag;
            if (r.SentPos == null || r.SentPos.Length != ls.Length) return true;
            var t = RagdollFrame(e);
            for (int i = 0; i < ls.Length; i++)
            {
                if (ls[i] == null || t != e.Root.transform && ls[i] == e.Root.transform) continue;
                Vector3d p; Quaternion q;
                if (e.Root.car == null) { p = mainscript.GlobalFromUnityPos(ls[i].position); q = ls[i].rotation; }
                else { LocalPose(t, ls[i], out var lp, out q); p = new Vector3d(lp.x, lp.y, lp.z); }
                if (BandwidthRules.PoseChanged((p - r.SentPos[i]).sqrMagnitude, Quaternion.Angle(q, r.SentRot[i]))) return true;
            }
            return false;
        }

        /// After the car signals in every state: the ragdoll block (RagdollWire): none, Frame (every limb relative to the
        /// transmitted frame: vehicles, a limb out of millimetre range, RagdollDelta off), Key or Delta. `key`: a Key is
        /// due whatever else (a first state, the final one). Returns true when it wrote a Key, which RagdollSent commits
        /// only if the state actually goes out (a Key no copy got must not be what the next Deltas refer to).
        private static bool WriteRagdoll(NetWriter w, Ent e, bool key)
        {
            var ls = Limbs(e);
            if (ls == null || !Limp(e)) { RagdollWire.WriteNone(w); return false; }
            var r = e.Rag; var t = RagdollFrame(e); int n = ls.Length;
            if (r.SentPos == null || r.SentPos.Length != n) { r.SentPos = new Vector3d[n]; r.SentRot = new Quaternion[n]; }
            if (r.WPos == null || r.WPos.Length != n) { r.WPos = new Vector3[n]; r.WRot = new Quaternion[n]; }
            var pos = r.WPos; var rot = r.WRot;   // the wire arrays: exactly one per limb, kept with the body
            int at = w.Len;
            r.Staged = false;
            if (!RagdollDelta || e.Root.car != null)
            {
                WriteFrameBlock(w, e, ls, t, pos, rot);
                RagdollFramesSent++; RagdollBlockBytes += w.Len - at;
                return false;
            }
            // the limbs' world poses now (also the baseline RagdollMoved compares against)
            for (int i = 0; i < n; i++)
            {
                if (ls[i] == null || t != e.Root.transform && ls[i] == e.Root.transform) { r.SentPos[i] = default; r.SentRot[i] = Quaternion.identity; continue; }
                r.SentPos[i] = mainscript.GlobalFromUnityPos(ls[i].position); r.SentRot[i] = ls[i].rotation;
            }
            float now = Time.realtimeSinceStartup;
            bool needKey = key || r.KeyEpoch != e.Epoch || r.KeyN != n || r.KeyWorld == null || now - r.KeyAt > RagdollKeyEveryS;
            ulong mask = 0;
            if (!needKey)
            {
                var inv = Quaternion.Inverse(r.KeyAnchorRot);
                for (int i = 0; i < n && !needKey; i++)
                {
                    if (ls[i] == null || t != e.Root.transform && ls[i] == e.Root.transform) continue;
                    if (!BandwidthRules.PoseChanged((r.SentPos[i] - r.KeyWorld[i]).sqrMagnitude, Quaternion.Angle(r.SentRot[i], r.KeyWorldRot[i]))) continue;
                    var d = r.SentPos[i] - r.KeyAnchor;
                    pos[i] = inv * new Vector3((float)d.x, (float)d.y, (float)d.z); rot[i] = inv * r.SentRot[i];
                    if (!RagdollWire.Fits(pos[i])) { needKey = true; break; }
                    mask |= 1UL << i;
                }
                if (!needKey && RagdollWire.DeltaSize(n, mask) > RagdollDeltaMaxShare * RagdollWire.KeySize(n)) needKey = true;
            }
            if (!needKey)
            {
                RagdollWire.WriteDelta(w, r.KeyId, mask, pos, rot);
                RagdollDeltasSent++; RagdollBlockBytes += w.Len - at;
                for (ulong m = mask; m != 0; m &= m - 1) RagdollLimbsSent++;
                return false;
            }
            // a Key: the anchor is the frame's pose now; every limb relative to it
            var anchor = mainscript.GlobalFromUnityPos(t.position); var anchorRot = t.rotation; var ainv = Quaternion.Inverse(anchorRot);
            for (int i = 0; i < n; i++)
            {
                if (ls[i] == null || t != e.Root.transform && ls[i] == e.Root.transform) { pos[i] = Vector3.zero; rot[i] = Quaternion.identity; continue; }
                var d = r.SentPos[i] - anchor;
                pos[i] = ainv * new Vector3((float)d.x, (float)d.y, (float)d.z); rot[i] = ainv * r.SentRot[i];
                if (!RagdollWire.Fits(pos[i])) { w.Len = at; WriteFrameBlock(w, e, ls, t, pos, rot); RagdollFramesSent++; RagdollBlockBytes += w.Len - at; return false; }
            }
            byte id = (byte)(r.KeyId + 1);
            RagdollWire.WriteKey(w, id, LimbHash(r, ls), pos, rot, anchor.x, anchor.y, anchor.z, anchorRot);
            RagdollKeysSent++; RagdollLimbsSent += n; RagdollBlockBytes += w.Len - at;
            r.Staged = true; r.StagedId = id; r.StagedAnchor = anchor; r.StagedAnchorRot = anchorRot;
            if (r.StagedWorld == null || r.StagedWorld.Length != n) { r.StagedWorld = new Vector3d[n]; r.StagedWorldRot = new Quaternion[n]; }
            System.Array.Copy(r.SentPos, r.StagedWorld, n); System.Array.Copy(r.SentRot, r.StagedWorldRot, n);
            return true;
        }

        /// Every limb relative to the transmitted frame, f32 (the form before protocol 54, now with its mode byte).
        private static void WriteFrameBlock(NetWriter w, Ent e, Transform[] ls, Transform t, Vector3[] pos, Quaternion[] rot)
        {
            var r = e.Rag; int n = ls.Length;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = Vector3.zero; Quaternion q = Quaternion.identity;
                if (ls[i] != null && (t == e.Root.transform || ls[i] != e.Root.transform)) LocalPose(t, ls[i], out p, out q);
                // A moving limb frame amplifies opposing jitter in every other limb. Compare what is visible in
                // world space; keep the wire poses relative and complete, including on the final state.
                r.SentPos[i] = e.Root.car == null && ls[i] != null ? mainscript.GlobalFromUnityPos(ls[i].position) : new Vector3d(p.x, p.y, p.z);
                r.SentRot[i] = e.Root.car == null && ls[i] != null ? ls[i].rotation : q;
                pos[i] = p; rot[i] = q;
            }
            RagdollWire.WriteFrame(w, pos, rot);
            RagdollLimbsSent += n;
        }
        private static T[] SubArray<T>(T[] a, int n) { var b = new T[n]; System.Array.Copy(a, b, n); return b; }

        /// The state carrying the block went out (or not): only a sent Key becomes the one the next Deltas refer to.
        private static void RagdollSent(Ent e, bool sent)
        {
            var r = e.Rag;
            if (r == null || !r.Staged) return;
            r.Staged = false;
            if (!sent) return;
            r.KeyId = r.StagedId; r.KeyEpoch = e.Epoch; r.KeyN = r.StagedWorld.Length; r.KeyAt = Time.realtimeSinceStartup;
            r.KeyAnchor = r.StagedAnchor; r.KeyAnchorRot = r.StagedAnchorRot;
            var kw = r.KeyWorld; var kr = r.KeyWorldRot;
            r.KeyWorld = r.StagedWorld; r.KeyWorldRot = r.StagedWorldRot;
            r.StagedWorld = kw; r.StagedWorldRot = kr;   // reuse the arrays
        }

        /// `framePos`/`frameRot`: the state's own frame (its header pose, global). A Key's and a Delta's limbs are relative to
        /// the Key's anchor; they are turned into frame-relative targets here, so the display (RagdollTick, MoveLimbs) is
        /// the same for every form. The Key is kept even while the copy isn't placed (streamed out) or isn't a copy.
        private static void ReadRagdoll(NetReader r, Ent e, Vector3d framePos, Quaternion frameRot)
        {
            if (r.Pos >= r.End) return;   // a sender without them (bots)
            var b = _ragBlock;
            if (!RagdollWire.Read(r, b)) return;   // none, or malformed (r.Bad)
            int n = b.N;
            if (e.Rag == null) e.Rag = new Rag();
            var g = e.Rag;
            Vector3[] tp; Quaternion[] tq;
            if (b.Mode == RagdollWire.Frame)
            {
                tp = SubArray(b.Pos, n); tq = SubArray(b.Rot, n);
            }
            else
            {
                if (b.Mode == RagdollWire.Key)
                {
                    RagdollKeysIn++;
                    g.CHas = true; g.CEpoch = e.Epoch; g.CId = b.Anchor; g.CHash = b.Hash;
                    g.CAnchor = new Vector3d(b.AX, b.AY, b.AZ); g.CAnchorRot = b.ARot;
                    g.CRel = SubArray(b.Pos, n); g.CRelRot = SubArray(b.Rot, n);
                }
                else
                {
                    RagdollDeltasIn++;
                    if (!g.CHas || g.CEpoch != e.Epoch || g.CId != b.Anchor || g.CRel == null || g.CRel.Length != n) { RagdollDeltasNoKey++; return; }
                }
                // every limb: the Delta's where it has one, the Key's otherwise; anchor-relative → world → frame-relative
                tp = new Vector3[n]; tq = new Quaternion[n];
                var finv = Quaternion.Inverse(frameRot);
                for (int i = 0; i < n; i++)
                {
                    bool d = b.Mode == RagdollWire.Delta && (b.Mask & (1UL << i)) != 0;
                    var rel = d ? b.Pos[i] : g.CRel[i]; var relRot = d ? b.Rot[i] : g.CRelRot[i];
                    var wp = g.CAnchorRot * rel; var wq = g.CAnchorRot * relRot;
                    var off = new Vector3((float)(g.CAnchor.x - framePos.x + wp.x), (float)(g.CAnchor.y - framePos.y + wp.y), (float)(g.CAnchor.z - framePos.z + wp.z));
                    tp[i] = finv * off; tq[i] = finv * wq;
                }
            }
            if (r.Bad || !e.Proxy || e.Root == null) return;
            // limbs in the root's frame (a vehicle's back section: ~10 m); a NaN went into the copy's kinematic bodies
            for (int i = 0; i < n; i++) if (!Finite(tp[i]) || tp[i].sqrMagnitude > 2500f) return;
            var ls = Limbs(e);
            if (ls == null || ls.Length != n) return;
            if (b.Mode != RagdollWire.Frame && g.CHash != LimbHash(g, ls))
            {
                if (RagdollHashMismatch++ < 5) Plugin.Log.LogWarning($"ragdoll {e.Root.name} net {e.NetId}: its owner's limbs aren't these (limb names differ) - not moved");
                return;
            }
            g.TPos = tp; g.TRot = tq;
            if (!g.Has || g.Pos == null || g.Pos.Length != n) { g.Pos = (Vector3[])tp.Clone(); g.Rot = (Quaternion[])tq.Clone(); }
            g.Has = true;
            RagdollCopies.Add(e);
            RagdollStatesApplied++;
        }

        /// Per frame: copies' limbs to the owner's poses.
        private static readonly List<Ent> _ragDrop = new List<Ent>();
        private static void RagdollTick(float dt)
        {
            if (RagdollCopies.Count == 0) return;
            float k = 1f - Mathf.Exp(-RagdollFollowRate * dt);
            _ragDrop.Clear();
            foreach (var e in RagdollCopies)
            {
                var ls = e.Proxy && e.Root != null && e.Rag != null && e.Rag.Has ? Limbs(e) : null;
                if (ls == null || ls.Length != e.Rag.TPos.Length) { _ragDrop.Add(e); continue; }
                if (!Limp(e)) DieLikeTheOwner(e);
                var g = e.Rag; var t = e.Root.transform; bool byBody = LimbsByBody(e);
                var framePos = t.position; var frameRot = t.rotation;
                for (int i = 0; i < ls.Length; i++)
                {
                    var l = ls[i];
                    if (l == null || e.Root.car == null && l == t) continue;
                    var rb = l.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) { rb.isKinematic = true; if (!e.MadeKinematic.Contains(rb)) e.MadeKinematic.Add(rb); }
                    if (rb != null) rb.detectCollisions = !e.Held && !e.Stored && !e.ReleaseGhost && !e.CargoGhost;
                    if (!RagdollLimbOwner.ContainsKey(l.gameObject)) { RagdollLimbOwner[l.gameObject] = e; ProxyItems.Add(l.gameObject); }
                    g.Pos[i] = Vector3.Lerp(g.Pos[i], g.TPos[i], k); g.Rot[i] = Quaternion.Slerp(g.Rot[i], g.TRot[i], k);
                    if ((g.Pos[i] - g.TPos[i]).sqrMagnitude < 1e-6f && Quaternion.Angle(g.Rot[i], g.TRot[i]) < 0.03f)
                    { g.Pos[i] = g.TPos[i]; g.Rot[i] = g.TRot[i]; }
                    if (rb != null && byBody) continue;   // moved as a body with the root (MoveLimbs)
                    l.SetPositionAndRotation(framePos + frameRot * g.Pos[i], frameRot * g.Rot[i]);
                }
            }
            foreach (var e in _ragDrop) RagdollRelease(e);
        }

        private static void DieLikeTheOwner(Ent e)
        {
            var ai = e.Root.GetComponentInChildren<newAiScript>(true);
            if (ai != null) { if (!ai.died) ai.Die(_playSound: false); }
            else
            {
                var rab = e.Root.GetComponentInChildren<aiscript>(true);
                if (rab != null) { if (rab.alive) rab.Die(_playSound: false); }
                else if (e.Rag.Act != null) e.Rag.Act.SetRagdol(true);
            }
        }

        /// No longer a copy here (taken over, session over): its limbs are the game's again (SetProxy restores the
        /// bodies it made kinematic).
        private static void RagdollRelease(Ent e)
        {
            RagdollCopies.Remove(e);
            if (e.Rag != null) e.Rag.Has = false;
            _ragKeys.Clear();
            foreach (var kv in RagdollLimbOwner) if (kv.Value == e) _ragKeys.Add(kv.Key);
            foreach (var go in _ragKeys) { RagdollLimbOwner.Remove(go); if (go != null) ProxyItems.Remove(go); }
        }
        private static readonly List<GameObject> _ragKeys = new List<GameObject>();

        /// Grabbing a limb of a copy: which body it belongs to (its limbs are not under the item once a creature died).
        private static Ent RagdollOf(pickupable p) =>
            p != null && RagdollLimbOwner.TryGetValue(p.gameObject, out var e) ? e : null;

        /// Test hooks (bridge `mp ragdoll net kill|grab`): the creature dies the game's way (newAiScript / aiscript.Die, as a
        /// shot does); a limb is picked up by the local player the game's way (pickupable.Pickup, as the E press does).
        public static string RagdollTest(uint net, string what)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no such entity\"}";
            if (what == "kill")
            {
                var ai = e.Root.GetComponentInChildren<newAiScript>(true);
                if (ai != null) { ai.Die(_playSound: true); return "{\"died\":\"newAi\"}"; }
                var rab = e.Root.GetComponentInChildren<aiscript>(true);
                if (rab != null) { rab.Die(_playSound: true); return "{\"died\":\"ai\"}"; }
                return "{\"error\":\"no creature\"}";
            }
            var ls = Limbs(e);
            var rp = e.Root.ragdollPos;
            if (ls == null || rp.ps == null || rp.ps.Length == 0) return "{\"error\":\"no limbs\"}";
            var pl = mainscript.s.player;
            pickupable best = null; float bd = float.MaxValue;
            foreach (var p in rp.ps)
                if (p != null) { float d = (p.transform.position - pl.transform.position).sqrMagnitude; if (d < bd) { bd = d; best = p; } }
            if (best == null) return "{\"error\":\"no limb\"}";
            best.Pickup();
            return "{\"picked\":" + Json.Str(best.name) + ",\"dist\":" + Mathf.Sqrt(bd).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"proxyNow\":" + (e.Proxy ? "true" : "false") + "}";
        }

        /// Diagnostics (bridge `mp ragdoll net`): the body's limbs here, world positions (global), and what was received.
        public static string RagdollStatus(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no such entity\"}";
            var ls = Limbs(e);
            if (ls == null) return "{\"error\":\"no limbs\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>(); var rest = new List<string>();
            var frame = RagdollFrame(e);
            foreach (var l in ls)
            {
                if (l == null) { rows.Add("null"); continue; }
                var g = mainscript.GlobalFromUnityPos(l.position);
                if (frame == e.Root.transform || l != e.Root.transform)
                {
                    var q = l.rotation;
                    rest.Add("[" + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) + "," +
                             q.x.ToString("F6", ic) + "," + q.y.ToString("F6", ic) + "," + q.z.ToString("F6", ic) + "," + q.w.ToString("F6", ic) + "]");
                }
                var rb = l.GetComponent<Rigidbody>();
                rows.Add("[" + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) + "," + (rb != null && rb.isKinematic ? "true" : "false") + "]");
            }
            var root = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            return "{\"name\":" + Json.Str(e.Root.name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"limp\":" + (Limp(e) ? "true" : "false") +
                   ",\"root\":[" + root.x.ToString("F3", ic) + "," + root.y.ToString("F3", ic) + "," + root.z.ToString("F3", ic) + "]" +
                   ",\"following\":" + (RagdollCopies.Contains(e) ? "true" : "false") + ",\"sent\":" + RagdollStatesSent + ",\"applied\":" + RagdollStatesApplied +
                   ",\"keys\":" + RagdollKeysSent + ",\"deltas\":" + RagdollDeltasSent + ",\"frames\":" + RagdollFramesSent + ",\"limbsSent\":" + RagdollLimbsSent +
                   ",\"blockBytes\":" + RagdollBlockBytes + ",\"keysIn\":" + RagdollKeysIn + ",\"deltasIn\":" + RagdollDeltasIn + ",\"deltasNoKey\":" + RagdollDeltasNoKey +
                   ",\"hashMismatch\":" + RagdollHashMismatch +
                   ",\"limbs\":[" + string.Join(",", rows) + "],\"restPose\":[" + string.Join(",", rest) + "]}";
        }

        /// Diagnostics (bridge `mp ragdolls`): every item prefab with limbs (tosaveitemscript.ragdollPos) or a creature
        /// ragdoll (ragdollactivatescript): [index, name, limbs, limb pickupables, creature bodies, kind].
        public static string RagdollPrefabs()
        {
            var rows = new List<string>();
            var db = itemdatabase.s != null ? itemdatabase.s.items : null;
            if (db == null) return "{\"error\":\"no itemdatabase\"}";
            for (int i = 0; i < db.Length; i++)
            {
                var go = db[i];
                if (go == null) continue;
                var ts = go.GetComponent<tosaveitemscript>();
                var rd = go.GetComponentInChildren<ragdollactivatescript>(true);
                if ((ts == null || ts.ragdollPos == null) && rd == null) continue;
                var rp = ts != null ? ts.ragdollPos : null;
                rows.Add("[" + i + "," + Json.Str(go.name) + "," + (rp != null && rp.childs != null ? rp.childs.Length : -1) + "," +
                         (rp != null && rp.ps != null ? rp.ps.Length : -1) + "," + (rd != null ? rd.RBs != null ? rd.RBs.Length : 0 : -1) + "," +
                         Json.Str((go.GetComponent<newAiScript>() != null ? "newAi " : "") + (go.GetComponentInChildren<nyulscript>(true) != null ? "nyul " : "") +
                                  (go.GetComponentInChildren<aiscript>(true) != null ? "ai " : "") + (go.GetComponent<Rigidbody>() != null ? "rootRB" : "")) + "]");
            }
            return "{\"ragdolls\":[" + string.Join(",", rows) + "]}";
        }
    }
}
