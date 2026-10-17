using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Hats, caps, helmets, glasses and masks (the game's wearable slots, wearableType; a lit cigarette goes in one too).
    /// The game wears an item by parenting it to the local player's head (fpscontroller.Wear: TWearablePos by
    /// wearable.tipus, its rigidbody destroyed, colliders off). Before v0.68.2 nothing told the other machines: the
    /// wearer's states drew the copy where the hat had been on its own (late) path — beside the head when walking, metres
    /// behind a passenger at speed — as a colliding body anyone could grab or a car could claim; and a record captured
    /// while worn (save_wear: wearable.player = the wearer's playerIndex, 1) put the hat on the receiving machine's own
    /// player when it loaded (save_wear.Load → fpscontroller.Wear).
    /// Now:
    ///  - the owner says when it is put on or taken off (Worn: slot, pose relative to its head target, anim.HHTarget — the
    ///    point the pose stream gives the remote body's head too, PlayerLook); the server keeps it for players the object
    ///    reaches later (join, interest), refuses claims and hand-offs while worn, and counts it in the wearer's pocket
    ///    (PlayerSave: it leaves with them and comes back on their head);
    ///  - copies ride the wearer's body: placed every frame after animation (LateUpdate) at that pose on the remote body's
    ///    head target, no collisions. Not parented: a seated body goes with the car model it sits in (RemotePlayers) and
    ///    would take the hat with it. No body shown here (far away, hidden with its car): the states place it as before;
    ///  - records from the network never carry a wearer (RecordCodec.StripWorn).
    public static partial class Entities
    {
        public const byte Worn = 85;   // c→s: u32 net, u32 epoch, u8 slot (255 = off), [3×f32 pos, rot].  s→c: u32 net, varuint wearer, u8 slot, [pos, rot]
        public static bool WornSync = true;   // A/B: false = before v0.68.2 (worn items drawn at their owner's states)
        public static long WornSent, WornRelayed, WornApplied, WornCleared, WornRefused, WornPlaced;
        private static float _wornCheckAt;
        private static readonly HashSet<Ent> _wornOwn = new HashSet<Ent>(), _wornCopies = new HashSet<Ent>();
        private static readonly List<Ent> _wornTmp = new List<Ent>();
        private static readonly Dictionary<tosaveitemscript, Ent> _wornEnt = new Dictionary<tosaveitemscript, Ent>();

        /// The slot this item is worn in by the local player, -1 if it isn't.
        internal static int LocalWornSlot(tosaveitemscript it)
        {
            var p = it != null ? it.P : null;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || p.wear == null || !p.wear.wearing || pl == null || pl.weared == null) return -1;
            int i = (int)p.wear.tipus;
            return i >= 0 && i < pl.weared.Length && pl.weared[i] == p ? i : -1;
        }

        private static Transform LocalHead()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return null;
            if (pl.anim != null && pl.anim.HHTarget != null) return pl.anim.HHTarget;
            return pl.Cam != null ? pl.Cam.transform : null;
        }

        /// Owner, 10 Hz: what the local player put on or took off since (any way the game does it: the wear key, a
        /// cigarette lit from its pack, death, a restored player save).
        private static void WornTick()
        {
            if (!WornSync || Time.unscaledTime < _wornCheckAt) return;
            _wornCheckAt = Time.unscaledTime + 0.1f;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.weared == null) return;
            _wornTmp.Clear(); _wornTmp.AddRange(_wornOwn);
            foreach (var e in _wornTmp)
            {
                int s = e.Proxy || e.Root == null ? -1 : LocalWornSlot(e.Root);
                if (s >= 0) continue;
                if (!e.Proxy && e.SentWornSlot >= 0 && ByNet.ContainsKey(e.NetId)) SendWorn(e, -1);
                e.SentWornSlot = -1;
                _wornOwn.Remove(e);
            }
            if (_wornEnt.Count > 64) _wornEnt.Clear();
            for (int i = 0; i < pl.weared.Length; i++)
            {
                var p = pl.weared[i];
                if (p == null) continue;
                var ts = p.tosave != null ? p.tosave : p.GetComponent<tosaveitemscript>();
                if (ts == null) continue;
                if (!_wornEnt.TryGetValue(ts, out var e) || e.Root != ts || !ByNet.ContainsKey(e.NetId))
                {
                    e = OwnEntity(ts);   // not shared yet (a cigarette just lit): next time
                    if (e == null) continue;
                    _wornEnt[ts] = e;
                }
                if (e.Proxy || e.SentWornSlot == i) continue;
                SendWorn(e, i);
                _wornOwn.Add(e);
            }
        }

        private static void SendWorn(Ent e, int slot)
        {
            W.Reset(); W.U8(Worn); W.U32(e.NetId); W.U32(e.Epoch); W.U8((byte)(slot < 0 ? 255 : slot));
            if (slot >= 0)
            {
                // metres in the head target's frame (unscaled: the two bodies' rigs need not share a scale)
                var h = LocalHead(); var t = e.Root.transform;
                var iq = h != null ? Quaternion.Inverse(h.rotation) : Quaternion.identity;
                Vector3 lp = h != null ? iq * (t.position - h.position) : Vector3.zero;
                Quaternion lq = iq * t.rotation;
                W.F32(lp.x); W.F32(lp.y); W.F32(lp.z); WriteRot(W, lq);
            }
            ToServer(W, true);
            e.SentWornSlot = slot;
            WornSent++;
        }

        // ------------------------------------------------------------------ server

        /// Worn by its owner right now (an owner change since ends it: the new owner's game has it loose).
        private static bool ServerWornNow(SEnt se) => se.WornBy >= 0 && se.WornBy == se.OwnerId;

        private static void ServerWorn(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32(); byte slot = r.U8();
            Vector3 lp = default; Quaternion lq = Quaternion.identity;
            if (slot != 255) { lp = new Vector3(r.F32(), r.F32(), r.F32()); lq = ReadRot(r); }
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from || se.Epoch != epoch) return;
            if (slot != 255 && (slot > 15 || !Finite(lp) || lp.sqrMagnitude > 4f)) return;   // on the head: within 2 m of it
            se.WornBy = slot == 255 ? -1 : from; se.WornSlot = slot == 255 ? -1 : slot; se.WornLp = lp; se.WornLq = lq;
            WriteWornOut(W, se);
            ServerSendAll(W, true, from);
            WornRelayed++;
        }

        private static void WriteWornOut(NetWriter w, SEnt se)
        {
            bool on = ServerWornNow(se);
            w.Reset(); w.U8(Worn); w.U32(se.NetId); w.VarU32((uint)(on ? se.WornBy : 0)); w.U8((byte)(on ? se.WornSlot : 255));
            if (on) { w.F32(se.WornLp.x); w.F32(se.WornLp.y); w.F32(se.WornLp.z); WriteRot(w, se.WornLq); }
        }

        /// A player the object reaches now (join, interest): it is on its wearer's head.
        private static void SendWornTo(int playerId, SEnt se)
        {
            if (!ServerWornNow(se)) return;
            WriteWornOut(W, se); ServerSendTo(playerId, W, true);
        }

        // ------------------------------------------------------------------ copies

        private static void ApplyWorn(NetReader r)
        {
            uint net = r.U32(); int wearer = (int)r.VarU32(); byte slot = r.U8();
            Vector3 lp = default; Quaternion lq = Quaternion.identity;
            if (slot != 255) { lp = new Vector3(r.F32(), r.F32(), r.F32()); lq = ReadRot(r); }
            if (r.Bad || !ByNet.TryGetValue(net, out var e)) return;
            WornApplied++;
            if (slot == 255 || !WornSync) { ClearWorn(e); return; }
            if (!e.Proxy || wearer == MyId || !Finite(lp)) return;   // ours: the game's own wear rules it
            e.WornBy = wearer; e.WornSlot = slot; e.WornLp = lp; e.WornLq = lq;
            _wornCopies.Add(e);
            if (e.MovedByBody) StopBodyMotion(e);
        }

        /// A copy on someone's head collides with nothing (as the worn item does on its wearer's machine).
        private static void WornBodies(Ent e, bool worn)
        {
            if (e.Root == null) return;
            foreach (var rb in e.Root.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.detectCollisions = !worn && !e.Held;
                if (worn) rb.interpolation = RigidbodyInterpolation.None;   // placed in LateUpdate: nothing to interpolate
            }
            var wr = e.Root.P != null ? e.Root.P.wear : null;
            if (wr != null && wr.c != null) foreach (var c in wr.c) if (c != null) c.enabled = !worn;
            e.WornOffFor = worn ? e.Root : null;
        }

        private static void ClearWorn(Ent e)
        {
            if (e.WornBy < 0) return;
            e.WornBy = -1; e.WornSlot = -1;
            _wornCopies.Remove(e);
            if (e.Root != null && e.WornOffFor == e.Root) WornBodies(e, false);
            e.WornOffFor = null;
            WornCleared++;
        }

        /// The remote body's head this copy rides on, null when there is none to show it on here.
        private static Transform WornAnchor(Ent e) => e.WornBy >= 0 && e.Proxy ? RemotePlayers.WearAnchor(e.WornBy) : null;

        /// On a body here: placed by WornLate, not by its states (Tick).
        private static bool WornRides(Ent e) => e.WornBy >= 0 && e.Proxy && e.Root != null && WornAnchor(e) != null;

        /// Every frame after animation (the runner's LateUpdate): worn copies onto their wearers' heads.
        public static void WornLate()
        {
            if (_wornCopies.Count == 0) return;
            _wornTmp.Clear(); _wornTmp.AddRange(_wornCopies);
            foreach (var e in _wornTmp)
            {
                if (!e.Proxy || !ByNet.ContainsKey(e.NetId)) { ClearWorn(e); continue; }
                if (e.Root == null) continue;   // in the far store here: placed again when it comes back
                var head = WornAnchor(e);
                if (head == null) continue;
                if (e.WornOffFor != e.Root) WornBodies(e, true);   // (placed again by the game's streaming: a new object)
                if (e.MovedByBody) StopBodyMotion(e);
                var q = head.rotation;
                e.Root.transform.SetPositionAndRotation(head.position + q * e.WornLp, q * e.WornLq);
                WornPlaced++;
            }
        }

        internal static void WornReset() { _wornOwn.Clear(); _wornCopies.Clear(); _wornEnt.Clear(); }

        // ------------------------------------------------------------------ tests (bridge)

        /// `mp wear <net>`: the local player puts on shared item `net` (taken first if another machine has it, as the
        /// pick-up before the wear key does).
        public static string WearNet(uint net)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.P == null) return "{\"error\":\"no such item here\"}";
            var p = e.Root.P;
            if (p.wear == null) return "{\"error\":\"not wearable\"}";
            int i = (int)p.wear.tipus;
            if (pl.weared[i] != null) return "{\"error\":\"slot " + p.wear.tipus + " taken by " + pl.weared[i].name + "\"}";
            if (e.Proxy && e.Root.car == null)
            {
                SetProxy(e, false); e.OwnerId = MyId;
                W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true); ClaimsSent++;
            }
            if (pl.pickedUp == p) pl.Drop();
            pl.Wear(p);
            return "{\"worn\":" + Json.Str(e.Root.name) + ",\"slot\":" + Json.Str(p.wear.tipus.ToString()) + "}";
        }

        /// `mp takeoff <net> [drop]`: off the local player's head — into the hands (the wear key), or `drop`: where it is.
        public static string TakeOffNet(uint net, bool drop)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || e.Root == null || LocalWornSlot(e.Root) < 0) return "{\"error\":\"not worn here\"}";
            pl.TakeOff(e.Root.P, drop);
            return "{\"off\":" + Json.Str(e.Root.name) + ",\"held\":" + (pl.pickedUp == e.Root.P ? "true" : "false") + "}";
        }

        /// `mp wearnear [name]`: the local player puts on the nearest wearable within 10 m whose name contains `name` (in a
        /// session or not: a host that wore it before hosting).
        public static string WearNear(string name)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            pickupable best = null; float bd = 100f;
            foreach (var p in Object.FindObjectsOfType<pickupable>())
            {
                if (p.wear == null || p.wear.wearing || (name != "" && p.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0)) continue;
                int i = (int)p.wear.tipus;
                if (i < 0 || i >= pl.weared.Length || pl.weared[i] != null || IsProxy(p)) continue;
                float d = (p.transform.position - pl.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = p; }
            }
            if (best == null) return "{\"error\":\"no free wearable within 10 m\"}";
            pl.Wear(best);
            return "{\"worn\":" + Json.Str(best.name) + ",\"slot\":" + Json.Str(best.wear.tipus.ToString()) + "}";
        }

        /// `mp wearables`: the spawnable items the game lets a player wear (itemdatabase index, name, slot).
        public static string Wearables()
        {
            var rows = new List<string>();
            var db = itemdatabase.s != null ? itemdatabase.s.items : null;
            if (db != null)
                for (int k = 0; k < db.Length; k++)
                {
                    var w = db[k] != null ? db[k].GetComponentInChildren<wearable>(true) : null;
                    if (w != null) rows.Add("[" + k + "," + Json.Str(db[k].name) + "," + Json.Str(w.tipus.ToString()) + "]");
                }
            return "{\"wearables\":[" + string.Join(",", rows) + "]}";
        }

        /// `mp worn`: every shared wearable this machine knows about: who wears it (here: the local slot; a copy: the
        /// wearer it rides), where it is shown and how far from the wearer's head.
        public static string WornStatus()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            var lh = LocalHead();
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.Root.P == null || e.Root.P.wear == null) continue;
                var t = e.Root.transform;
                int local = LocalWornSlot(e.Root);
                Transform head = e.Proxy ? (e.WornBy >= 0 ? RemotePlayers.WearAnchor(e.WornBy) : null) : (local >= 0 ? lh : null);
                var g = mainscript.GlobalFromUnityPos(t.position);
                var cols = 0; foreach (var c in e.Root.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) cols++;
                var rb = e.Root.GetComponent<Rigidbody>();
                // where it is on its wearer's body: height above the body's root, distance from its vertical axis (the same
                // numbers on every machine when it sits where its wearer has it)
                var body = local >= 0 && mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform : e.Proxy ? RemotePlayers.BodyOf(e.WornBy >= 0 ? e.WornBy : e.OwnerId) : null;
                string rel = "null";
                if (body != null)
                {
                    var d = t.position - body.position;
                    rel = "[" + d.y.ToString("F3", ic) + "," + new Vector2(d.x, d.z).magnitude.ToString("F3", ic) + "]";
                }
                rows.Add("{\"net\":" + e.NetId + ",\"name\":" + Json.Str(e.Root.name) + ",\"tipus\":" + Json.Str(e.Root.P.wear.tipus.ToString()) +
                         ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"owner\":" + e.OwnerId + ",\"localSlot\":" + local + ",\"wornBy\":" + e.WornBy +
                         ",\"wearing\":" + (e.Root.P.wear.wearing ? "true" : "false") + ",\"wearPlayer\":" + e.Root.P.wear.player +
                         ",\"rides\":" + (WornRides(e) ? "true" : "false") + ",\"headDist\":" + (head != null ? Vector3.Distance(head.position, t.position).ToString("F3", ic) : "-1") +
                         ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]" +
                         ",\"parent\":" + Json.Str(t.parent != null ? t.parent.name : "") + ",\"colliders\":" + cols + ",\"rb\":" + (rb == null ? "\"none\"" : rb.isKinematic ? "\"kinematic\"" : "\"dynamic\"") +
                         ",\"onBody\":" + rel + ",\"held\":" + (e.Held ? "true" : "false") + ",\"shown\":" + (e.Root.gameObject.activeInHierarchy ? "true" : "false") + "}");
            }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var slots = new List<string>();
            if (pl != null && pl.weared != null) foreach (var p in pl.weared) slots.Add(Json.Str(p != null ? p.name : ""));
            return "{\"enabled\":" + (WornSync ? "true" : "false") + ",\"slots\":[" + string.Join(",", slots) + "],\"sent\":" + WornSent + ",\"relayed\":" + WornRelayed +
                   ",\"applied\":" + WornApplied + ",\"cleared\":" + WornCleared + ",\"refused\":" + WornRefused + ",\"placed\":" + WornPlaced +
                   ",\"strippedRecords\":" + RecordCodec.WornStripped + ",\"items\":[" + string.Join(",", rows) + "]}";
        }
    }
}
