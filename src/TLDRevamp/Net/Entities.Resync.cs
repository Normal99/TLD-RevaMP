using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// State that isn't a pose — fuel, doors, part conditions, lights, paint — travels as the object's save record
    /// (the same data the save file keeps). Every 3 s the owner captures each item of its group with the transform
    /// zeroed and sends the items whose record changed (driving changes only the body's: fuel, rpm, steer); receivers
    /// apply the state part of them with the game's own per-component loaders. Ids differ per machine and ownership
    /// moves, so records carry canonical group-index ids (0xF0000000 + index: no real id gets there); each machine maps
    /// them onto its own items by position in the group, like parts. The server keeps the latest record of every item
    /// for players who join later.
    public static partial class Entities
    {
        public const byte Resync = 31;
        private const uint CanonBase = 0xF0000000u;
        public static float ResyncInterval = 3f, ResyncBudgetMs = 0.5f;
        public static long ResyncsSent, ResyncsApplied, ResyncItemsLoaded, ResyncSkipped, PartStatesChanged, ResyncBytes, ResyncItemsSent, ResyncHeld;
        public static double ResyncCaptureMsMax, ResyncCaptureMsTotal;
        private static readonly NetWriter WR = new NetWriter();

        private static void ResyncTick(Ent e, float dt)
        {
            if (e.Proxy) { e.ResyncHashes = null; e.ResyncCursor = -1; return; }   // owning again later: everything goes out once
            if (e.Root == null || e.NetId == 0) return;
            if (!e.ResyncPhased) { e.ResyncPhased = true; e.ResyncAcc = (e.NetId * 0.618034f) % 1f * ResyncInterval; }   // spread over frames
            if (e.ResyncCursor < 0)
            {
                e.ResyncAcc += dt;
                if (e.ResyncAcc < ResyncInterval) return;
                e.ResyncAcc = 0f; e.ResyncCursor = 0;
            }
            // a pass over the group spreads over frames: at most ~0.5 ms of capturing per object per frame
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = e.Items.Count;
            if (e.ResyncHashes == null || e.ResyncHashes.Length != n) e.ResyncHashes = new ulong[n];
            var map = CanonMap(e.Items);
            WR.Reset(); WR.U8(Resync); WR.U32(e.NetId); WR.U32(e.Epoch);
            int countAt = WR.Len; WR.U16(0);
            int changed = 0;
            int done = 0;
            while (e.ResyncCursor < n && (done++ == 0 || sw.Elapsed.TotalMilliseconds < ResyncBudgetMs))
            {
                int i = e.ResyncCursor++;
                var it = e.Items[i];
                if (it == null || (e.Split != null && e.Split.Contains(i))) continue;   // came off: its own entity syncs it
                byte[] rec = StateRecord(ItemSnapshot.Capture(it), map, out ulong h);
                if (h == e.ResyncHashes[i]) continue;
                e.ResyncHashes[i] = h;
                WR.VarU32((uint)i); WR.VarU32((uint)rec.Length); WR.Bytes(rec, 0, rec.Length);
                changed++;
            }
            if (e.ResyncCursor >= n) e.ResyncCursor = -1;
            double ms = sw.Elapsed.TotalMilliseconds; ResyncCaptureMsTotal += ms; if (ms > ResyncCaptureMsMax) ResyncCaptureMsMax = ms;
            if (changed == 0) { if (e.ResyncCursor < 0) ResyncSkipped++; return; }
            WR.Buf[countAt] = (byte)changed; WR.Buf[countAt + 1] = (byte)(changed >> 8);
            ToServer(WR, true);
            ResyncsSent++; ResyncItemsSent += changed; ResyncBytes += WR.Len;
        }

        private static Dictionary<uint, uint> CanonMap(List<tosaveitemscript> items)
        {
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < items.Count; i++) if (items[i] != null) map[items[i].idInSave] = CanonBase + (uint)i;
            return map;
        }

        private static byte[] StateRecord(itemDataClass captured, Dictionary<uint, uint> map, out ulong hash, bool withCar = true)
        {
            var d = StateOnly(captured);
            if (!withCar) d.car = new List<save_carscript>();   // the driver's controls: never someone else's edit
            // attached to an item OUTSIDE this group (a part bolted into another entity's car): its parentid is this
            // machine's id and would name a stranger elsewhere — a re-bolted hubcap's own record re-attached it to the
            // TYRE on the other machine (buglist2m step 3). Where such a part sits travels in AttachSync (net + index).
            if (d.attachable != null)
                ResyncForeignAttachDropped += d.attachable.RemoveAll(a => a != null && a.attachType == (int)save_attachable.attachTypeEmu.toItem && !map.ContainsKey(a.parentid));
            ItemSnapshot.Remap(d, map);
            byte[] rec = RecordCodec.Encode(d);
            ulong h = 14695981039346656037UL;
            foreach (var bt in rec) { h ^= bt; h *= 1099511628211UL; }
            hash = h;
            return rec;
        }

        /// The owner's baseline at share time: the state everyone gets with the share itself (no resend 3 s later).
        private static ulong[] StateHashes(List<tosaveitemscript> items, List<itemDataClass> perItem)
        {
            var map = CanonMap(items);
            var hs = new ulong[items.Count];
            for (int i = 0; i < items.Count; i++) if (perItem[i] != null) StateRecord(perItem[i], map, out hs[i]);
            return hs;
        }

        /// Server: keep each item's latest record, pass the message on.
        private static void ServerResync(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32();
            if (!Server.TryGetValue(net, out var se) || se.OwnerId != from || se.Epoch != epoch) return;
            int count = r.U16();
            for (int k = 0; k < count && !r.Bad; k++)
            {
                int idx = (int)r.VarU32(), len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining || idx < 0 || idx > 4096) return;
                var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len); r.Pos += len;
                bool split = false;
                foreach (var pn in se.Parts) if (Server.TryGetValue(pn, out var sp) && sp.PartIndex == idx) { split = true; break; }
                if (!split) se.ItemState[idx] = rec;
            }
            if (r.Bad) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, true, from);
        }

        /// Server → a player who joins later: every item's latest record in one message.
        private static void WriteResyncAll(NetWriter w, SEnt se)
        {
            w.Reset(); w.U8(Resync); w.U32(se.NetId); w.U32(se.Epoch); w.U16((ushort)se.ItemState.Count);
            foreach (var kv in se.ItemState) { w.VarU32((uint)kv.Key); w.VarU32((uint)kv.Value.Length); w.Bytes(kv.Value, 0, kv.Value.Length); }
        }

        /// Ownership moves (a claim, a pickup, a car's cargo, a hand-off): the new owner goes on from its COPY's state —
        /// up to a resync interval (3 s) old. A jerry can poured from and set down by a car was claimed by the car's
        /// owner with the can's 3 s-old level: fluid made out of nothing (fluids run, v0.65.8: can 7.145 → 7.261 L after
        /// the pour). The old owner, told it lost the object, sends every item's state record at once (its last word,
        /// tagged with the new epoch); the server keeps them and passes them to the new owner, which takes them if they
        /// arrive while the object is freshly its own (2 s).
        public const byte HandoverState = 59;
        public static long HandoversSent, HandoversApplied, HandoversLate, HandoversCrashSkipped;
        public static float HandoverWindowS = 2f;
        public static string HandoverLateWhy = "";
        private static void SendHandover(Ent e, uint newEpoch)
        {
            if (e.Root == null || e.Items == null || e.NetId == 0) return;
            var map = CanonMap(e.Items);
            WR.Reset(); WR.U8(HandoverState); WR.U32(e.NetId); WR.U32(newEpoch);
            int countAt = WR.Len; WR.U16(0);
            int n = 0;
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i];
                if (it == null || (e.Split != null && e.Split.Contains(i))) continue;
                byte[] rec = StateRecord(ItemSnapshot.Capture(it), map, out _, withCar: false);
                WR.VarU32((uint)i); WR.VarU32((uint)rec.Length); WR.Bytes(rec, 0, rec.Length);
                n++;
            }
            if (n == 0) return;
            WR.Buf[countAt] = (byte)n; WR.Buf[countAt + 1] = (byte)(n >> 8);
            ToServer(WR, true);
            HandoversSent++;
        }
        /// Server: from the one who just lost it (not the owner), for the current epoch → kept, and to the new owner.
        private static void ServerHandover(int from, NetReader r)
        {
            uint net = r.U32(), epoch = r.U32();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from || se.Epoch != epoch) return;
            int count = r.U16();
            for (int k = 0; k < count && !r.Bad; k++)
            {
                int idx = (int)r.VarU32(), len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining || idx < 0 || idx > 4096) return;
                var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len); r.Pos += len;
                se.ItemState[idx] = rec;
            }
            if (r.Bad) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }
        /// New owner: the old owner's last state, while the object is freshly ours.
        private static void ApplyHandover(NetReader r)
        {
            uint net = r.U32(), epoch = r.U32();
            int count = r.U16();
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || e.Epoch != epoch || Time.realtimeSinceStartup - e.OwnerAt > HandoverWindowS)
            {
                HandoversLate++;
                ByNet.TryGetValue(net, out var le);
                HandoverLateWhy = "net " + net + " epoch " + epoch + (le == null ? " unknown" : " mine " + le.Epoch + " owner " + le.OwnerId + " proxy " + le.Proxy +
                                  " age " + (Time.realtimeSinceStartup - le.OwnerAt).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (!Resolve(e)) return;
            // taken over mid-crash (CrashAuthority): the crash happens here, the record is from before it — applied it would
            // wind back the damage just done (part conditions). When the car goes back, our record carries the crash.
            if (e.CrashTaking) { HandoversCrashSkipped++; return; }
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) map[CanonBase + (uint)i] = e.Items[i].idInSave;
            for (int k = 0; k < count; k++)
            {
                int idx = (int)r.VarU32(), len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining) return;
                var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len); r.Pos += len;
                if (idx < 0 || idx >= e.Items.Count || e.Items[idx] == null || (e.Split != null && e.Split.Contains(idx))) continue;
                var d = RecordCodec.Decode(rec);
                if (d == null) continue;
                DropUncanonAttach(d);
                ItemSnapshot.Remap(d, map);
                ApplyState(e.Items[idx], d);
            }
            HandoversApplied++;
        }

        public static long ResyncForeignAttachDropped;
        /// Receiving side of the same rule: an attachment to an item must name a group index, never a raw id.
        private static void DropUncanonAttach(itemDataClass d)
        {
            if (d.attachable != null)
                ResyncForeignAttachDropped += d.attachable.RemoveAll(a => a != null && a.attachType == (int)save_attachable.attachTypeEmu.toItem && a.parentid < CanonBase);
        }
        private static void ApplyResync(NetReader r)
        {
            uint net = r.U32(); r.U32();
            int count = r.U16();
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || !e.Proxy) return;
            if (!Resolve(e))
            {
                // stored here (far away): the records take the new state — what this machine saves and shows on arrival
                if (e.ItemIds == null) return;
                var fmap = new Dictionary<uint, uint>();
                for (int i = 0; i < e.ItemIds.Count; i++) if (e.ItemIds[i] != 0) fmap[CanonBase + (uint)i] = e.ItemIds[i];
                for (int k = 0; k < count; k++)
                {
                    int idx = (int)r.VarU32(), len = (int)r.VarU32();
                    if (r.Bad || len < 0 || len > r.Remaining) return;
                    var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len); r.Pos += len;
                    if (e.Split != null && e.Split.Contains(idx)) continue;
                    var fd = RecordCodec.Decode(rec);
                    if (fd == null) continue;
                    DropUncanonAttach(fd);
                    ItemSnapshot.Remap(fd, fmap);
                    FarResync(e, idx, fd);
                }
                FarResyncs++;
                return;
            }
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) map[CanonBase + (uint)i] = e.Items[i].idInSave;
            for (int k = 0; k < count; k++)
            {
                int idx = (int)r.VarU32(), len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining) return;
                var rec = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, len); r.Pos += len;
                if (idx < 0 || idx >= e.Items.Count || e.Items[idx] == null) continue;
                if (e.Split != null && e.Split.Contains(idx)) { ResyncSplitSkipped++; continue; }   // came off: never re-bolted from a car record
                if (e.EditHoldUntil != null && e.EditHoldUntil.TryGetValue(idx, out float hold) && Time.realtimeSinceStartup < hold) { ResyncHeld++; continue; }   // our edit is on its way to the owner
                var d = RecordCodec.Decode(rec);
                if (d == null) continue;
                DropUncanonAttach(d);
                ItemSnapshot.Remap(d, map);
                ApplyState(e.Items[idx], d);
                ResyncItemsLoaded++;
            }
            ResyncsApplied++;
        }

        /// Only the lists ApplyState uses. The rest would make every item look changed on every tick while its car
        /// moves — attached parts' local positions jitter by micrometres on their joints, dashboard needles move — and
        /// none of it is applied (poses and wheels stream on their own).
        private static itemDataClass StateOnly(itemDataClass d)
        {
            var o = new itemDataClass();
            o.partconditions = d.partconditions; o.tanks = d.tanks; o.usable = d.usable; o.colors = d.colors; o.door_rots = d.door_rots;
            o.car = d.car; o.ammo = d.ammo; o.ginlamp = d.ginlamp; o.busdoorscript = d.busdoorscript; o.painting = d.painting;
            o.food = d.food; o.rendszam = d.rendszam;
            // re-attaching a detached part must reach the other machines — but only WHERE it's attached: lpos jitters by
            // micrometres on joints and gpos (the attach point in the world) moves with the car, and carrying them made
            // every attached part "change" every pass while driving: the receivers re-attached the whole dashboard
            // every 3 s (visible re-insert + hitch). gpos dropped, lpos to the centimetre.
            o.attachable = new List<save_attachable>();
            if (d.attachable != null)
                foreach (var a in d.attachable)
                {
                    if (a == null) continue;
                    var c = new save_attachable(a);
                    c.gpos = new Vector3d(0, 0, 0);
                    // in a slot (indexType 2) the slot IS the position; stuck on a collider, the spot counts (to 5 cm)
                    c.lpos = a.indexType == 2 ? new Vector3d(0, 0, 0)
                        : new Vector3d(Math.Round(a.lpos.x * 20) / 20, Math.Round(a.lpos.y * 20) / 20, Math.Round(a.lpos.z * 20) / 20);
                    o.attachable.Add(c);
                }
            return o;
        }

        /// The part of the game's LoadStuff that is state, applied to a live object. Not the rest: physLocks (would
        /// place far items), engine rpm and wheels (they
        /// stream with the pose). Part conditions get the Refresh the game's own load leaves to Start().
        private static void ApplyState(tosaveitemscript it, itemDataClass d)
        {
            _applyingState = true;
            try { ApplyStateInner(it, d); }
            finally { _applyingState = false; }
        }

        /// Same records? (JSON of each element — the game's own serialisable save classes.)
        private static bool SameList(System.Collections.IList a, System.Collections.IList b)
        {
            int na = a != null ? a.Count : 0, nb = b != null ? b.Count : 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (JsonUtility.ToJson(a[i]) != JsonUtility.ToJson(b[i])) return false;
            return true;
        }

        public static long StateListsApplied, StateListsSame, AttachReapplied, ResyncSplitSkipped, PartsRepainted;
        public static bool RepaintOnApply = true;   // A/B: false = the colour applied without redrawing the part (before v0.65.20)

        /// Attached to the same thing in the same place? Only WHERE — never the jittering exact position.
        private static bool SameAttachment(List<save_attachable> remote, List<save_attachable> mine)
        {
            int nr = remote != null ? remote.Count : 0, nm = mine != null ? mine.Count : 0;
            if (nr != nm) return false;
            for (int i = 0; i < nr; i++)
            {
                var a = remote[i]; var b = mine[i];
                if (a.attachType != b.attachType || a.parentid != b.parentid || a.indexType != b.indexType || a.index != b.index || a.poigenid != b.poigenid) return false;
                if (a.indexType != 2 && (a.lpos - b.lpos).magnitude > 0.1) return false;   // moved on its collider
            }
            return true;
        }

        /// Only the parts of the record that differ from this copy are loaded: a fuel tick must not reload the body's
        /// colours, doors, knobs and attachment (each load does real work — materials, refreshes, re-attaching).
        private static void ApplyStateInner(tosaveitemscript it, itemDataClass d)
        {
            var local = StateOnly(ItemSnapshot.Capture(it));
            bool Diff(System.Collections.IList remote, System.Collections.IList mine)
            {
                if (SameList(remote, mine)) { StateListsSame++; return false; }
                StateListsApplied++; return true;
            }
            int[] before = null; Color[] colBefore = null;
            if (it.partconditions != null)
            {
                before = new int[it.partconditions.Count * 3]; colBefore = new Color[it.partconditions.Count];
                for (int i = 0; i < it.partconditions.Count; i++)
                    if (it.partconditions[i] != null) { before[i * 3] = it.partconditions[i].state; before[i * 3 + 1] = it.partconditions[i].state2; before[i * 3 + 2] = it.partconditions[i].state3; colBefore[i] = it.partconditions[i].color; }
            }
            if (Diff(d.partconditions, local.partconditions)) savedatascript.Load(it, it.partconditions, d.partconditions);
            if (Diff(d.tanks, local.tanks)) { savedatascript.Load(it, it.tanks, d.tanks); Fixes.EmptyTankLoad.EmptyUnrecorded(it, d.tanks); }
            if (Diff(d.usable, local.usable)) savedatascript.Load(it, it.usables, d.usable);
            if (Diff(d.colors, local.colors)) savedatascript.Load(it, it.colors, d.colors);
            if (Diff(d.door_rots, local.door_rots)) savedatascript.Load(it, it.door_rots, d.door_rots);
            if (it.car != null && Diff(d.car, local.car)) savedatascript.Load(it, d.car);
            if (it.ammo != null && Diff(d.ammo, local.ammo)) savedatascript.Load(it, d.ammo);
            if (it.ginlamp != null && Diff(d.ginlamp, local.ginlamp)) savedatascript.Load(it, d.ginlamp);
            if (it.busdoorscript != null && Diff(d.busdoorscript, local.busdoorscript)) savedatascript.Load(it, d.busdoorscript);
            if (it.painting != null && Diff(d.painting, local.painting)) savedatascript.Load(it, d.painting);
            if (it.food != null && Diff(d.food, local.food)) savedatascript.Load(it, d.food);
            if (it.rendszam != null && Diff(d.rendszam, local.rendszam)) savedatascript.Load(it, d.rendszam);
            if (d.attachable != null && d.attachable.Count > 0 && !SameAttachment(d.attachable, local.attachable)) { savedatascript.Load(it, d.attachable); AttachReapplied++; }   // (re-)attach only when WHERE differs
            if (before != null)
                for (int i = 0; i < it.partconditions.Count; i++)
                {
                    var pc = it.partconditions[i];
                    // a paint changes only the colour: the material is rebuilt by Refresh too (before, a part sprayed by
                    // another player kept its old colour on screen here until the object was next loaded — paintsync.py)
                    if (pc == null) continue;
                    bool st = before[i * 3] != pc.state || before[i * 3 + 1] != pc.state2 || before[i * 3 + 2] != pc.state3;
                    bool col = RepaintOnApply && colBefore[i] != pc.color;
                    if (st || col) { pc.Refresh(); if (st) PartStatesChanged++; else PartsRepainted++; }
                }
        }
    }
}
