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
        public static long RagdollStatesSent, RagdollStatesApplied;
        private static readonly HashSet<Ent> RagdollCopies = new HashSet<Ent>();
        private static readonly Dictionary<GameObject, Ent> RagdollLimbOwner = new Dictionary<GameObject, Ent>();
        private const float RagdollFollowRate = 20f;   // 1/s: a copy's limbs close this share of the gap per second (~50 ms lag)

        public class Rag
        {
            public ragdollactivatescript Act; public bool ActLooked;
            public Vector3[] SentPos; public Quaternion[] SentRot;   // the owner: what was last sent (item-relative)
            public Vector3[] Pos, TPos; public Quaternion[] Rot, TRot;   // a copy: shown now / received
            public bool Has;
        }

        private static Transform[] Limbs(Ent e)
        {
            var rp = e.Root != null && e.PartIndex < 0 ? e.Root.ragdollPos : null;
            return rp != null && rp.childs != null && rp.childs.Length > 0 && rp.childs.Length < 64 ? rp.childs : null;
        }

        /// Limp: a body with no creature (dead player, skeleton), or a creature in its ragdoll.
        private static bool Limp(Ent e)
        {
            if (e.Rag == null) e.Rag = new Rag();
            if (!e.Rag.ActLooked) { e.Rag.ActLooked = true; e.Rag.Act = e.Root.GetComponentInChildren<ragdollactivatescript>(true); }
            return e.Rag.Act == null || e.Rag.Act.ragdoll;
        }

        private static void LocalPose(Transform root, Transform limb, out Vector3 p, out Quaternion q)
        {
            var inv = Quaternion.Inverse(root.rotation);
            p = inv * (limb.position - root.position); q = inv * limb.rotation;
        }

        /// The owner: has a limb moved (1 mm / 0.1 deg) since the last state? - a body sent like any item: while it
        /// moves, one state at rest.
        private static bool RagdollMoved(Ent e)
        {
            var ls = Limbs(e);
            if (ls == null || !Limp(e)) return false;
            var r = e.Rag;
            if (r.SentPos == null || r.SentPos.Length != ls.Length) return true;
            var t = e.Root.transform;
            for (int i = 0; i < ls.Length; i++)
            {
                if (ls[i] == null) continue;
                LocalPose(t, ls[i], out var p, out var q);
                if ((p - r.SentPos[i]).sqrMagnitude > 1e-6f || Quaternion.Angle(q, r.SentRot[i]) > 0.1f) return true;
            }
            return false;
        }

        /// After the car signals in every state: u8 limb count (0: not limp), per limb 3 x f32 position and the rotation,
        /// relative to the item.
        private static void WriteRagdoll(NetWriter w, Ent e)
        {
            var ls = Limbs(e);
            if (ls == null || !Limp(e)) { w.U8(0); return; }
            var r = e.Rag; var t = e.Root.transform;
            if (r.SentPos == null || r.SentPos.Length != ls.Length) { r.SentPos = new Vector3[ls.Length]; r.SentRot = new Quaternion[ls.Length]; }
            w.U8((byte)ls.Length);
            for (int i = 0; i < ls.Length; i++)
            {
                Vector3 p = Vector3.zero; Quaternion q = Quaternion.identity;
                if (ls[i] != null) LocalPose(t, ls[i], out p, out q);
                r.SentPos[i] = p; r.SentRot[i] = q;
                w.F32(p.x); w.F32(p.y); w.F32(p.z); WriteRot(w, q);
            }
            RagdollStatesSent++;
        }

        private static void ReadRagdoll(NetReader r, Ent e)
        {
            if (r.Pos >= r.End) return;   // a sender without them (bots)
            int n = r.U8();
            if (n == 0) return;
            var tp = new Vector3[n]; var tq = new Quaternion[n];
            for (int i = 0; i < n; i++) { tp[i] = new Vector3(r.F32(), r.F32(), r.F32()); tq[i] = ReadRot(r); }
            if (r.Bad || !e.Proxy || e.Root == null) return;
            // limbs in the root's frame (a vehicle's back section: ~10 m); a NaN went into the copy's kinematic bodies
            for (int i = 0; i < n; i++) if (!Finite(tp[i]) || tp[i].sqrMagnitude > 2500f) return;
            var ls = Limbs(e);
            if (ls == null || ls.Length != n) return;
            if (e.Rag == null) e.Rag = new Rag();
            var g = e.Rag;
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
                for (int i = 0; i < ls.Length; i++)
                {
                    var l = ls[i];
                    if (l == null) continue;
                    var rb = l.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) { rb.isKinematic = true; if (!e.MadeKinematic.Contains(rb)) e.MadeKinematic.Add(rb); }
                    if (!RagdollLimbOwner.ContainsKey(l.gameObject)) { RagdollLimbOwner[l.gameObject] = e; ProxyItems.Add(l.gameObject); }
                    g.Pos[i] = Vector3.Lerp(g.Pos[i], g.TPos[i], k); g.Rot[i] = Quaternion.Slerp(g.Rot[i], g.TRot[i], k);
                    if (rb != null && byBody) continue;   // moved as a body with the root (MoveLimbs)
                    l.SetPositionAndRotation(t.position + t.rotation * g.Pos[i], t.rotation * g.Rot[i]);
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
            var rows = new List<string>();
            foreach (var l in ls)
            {
                if (l == null) { rows.Add("null"); continue; }
                var g = mainscript.GlobalFromUnityPos(l.position);
                var rb = l.GetComponent<Rigidbody>();
                rows.Add("[" + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) + "," + (rb != null && rb.isKinematic ? "true" : "false") + "]");
            }
            var root = mainscript.GlobalFromUnityPos(e.Root.transform.position);
            return "{\"name\":" + Json.Str(e.Root.name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"limp\":" + (Limp(e) ? "true" : "false") +
                   ",\"root\":[" + root.x.ToString("F3", ic) + "," + root.y.ToString("F3", ic) + "," + root.z.ToString("F3", ic) + "]" +
                   ",\"following\":" + (RagdollCopies.Contains(e) ? "true" : "false") + ",\"sent\":" + RagdollStatesSent + ",\"applied\":" + RagdollStatesApplied +
                   ",\"limbs\":[" + string.Join(",", rows) + "]}";
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
