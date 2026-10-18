using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Motion takes the short path to nearby viewers; the owner's reliable record still settles and saves it.
    public static partial class Entities
    {
        public static bool LiveUsableSync = true;
        public static long LiveUsablesSent, LiveUsablesRelayed, LiveUsablesApplied, LiveUsablesStale, LiveUsablesRefused;
        public static float LiveUsableRatePerS = 60f, LiveUsableBurst = 90f;
        private const float LiveQuietS = 0.3f, LiveStepS = 1f / 30f;
        private static bool _applyingLive;

        private struct UsableKey : IEquatable<UsableKey>
        {
            public uint Net, Epoch; public int Item, Usable;
            public bool Equals(UsableKey k) => Net == k.Net && Epoch == k.Epoch && Item == k.Item && Usable == k.Usable;
            public override bool Equals(object o) => o is UsableKey k && Equals(k);
            public override int GetHashCode() => (int)(Net * 397 ^ Epoch) ^ Item * 7919 ^ Usable;
        }
        private struct UsableValue : IEquatable<UsableValue>
        {
            public ushort X, Y, Slide, Tuned; public int Turn;
            public bool Equals(UsableValue v) => X == v.X && Y == v.Y && Slide == v.Slide && Tuned == v.Tuned && Turn == v.Turn;
        }
        private sealed class LocalUsable
        {
            public UsableKey Key; public Ent Entity; public tosaveitemscript Item; public usablescript U;
            public UsableValue Value, Sent; public PoiState Raw; public float ChangedAt, HookAt, SendAt; public ushort Seq; public bool Dirty, Final, HasSent;
        }
        private sealed class RemoteUsable
        {
            public usablescript U; public Ent Entity; public float At; public bool Final, Blending;
            public PoiState Start, Target;
        }
        private struct LiveOrder { public ushort Seq; public double Stamp; }
        private sealed class UsableHierarchy { public usablescript[] Usables; }
        private sealed class UsableBefore { public UsableValue Value; public PoiState Raw; }
        private static ConditionalWeakTable<tosaveitemscript, UsableHierarchy> _usableHierarchy = new ConditionalWeakTable<tosaveitemscript, UsableHierarchy>();
        private static ConditionalWeakTable<usablescript, UsableBefore> _usableBefore = new ConditionalWeakTable<usablescript, UsableBefore>();
        private static readonly Dictionary<usablescript, LocalUsable> _localUsables = new Dictionary<usablescript, LocalUsable>();
        private static readonly Dictionary<UsableKey, RemoteUsable> _remoteUsables = new Dictionary<UsableKey, RemoteUsable>();
        private static readonly Dictionary<UsableKey, LiveOrder> _liveIn = new Dictionary<UsableKey, LiveOrder>();
        private static readonly Dictionary<UsableKey, ushort> _liveOut = new Dictionary<UsableKey, ushort>();
        private static readonly Dictionary<int, Dictionary<UsableKey, LiveOrder>> _liveServerIn = new Dictionary<int, Dictionary<UsableKey, LiveOrder>>();
        private static readonly Dictionary<UsableKey, ushort> _liveRelaySeq = new Dictionary<UsableKey, ushort>();
        private static readonly Dictionary<UsableKey, double> _liveRecordFloor = new Dictionary<UsableKey, double>();
        private static readonly Dictionary<int, RateBucket> _liveBuckets = new Dictionary<int, RateBucket>();
        private static readonly List<usablescript> _deadUsables = new List<usablescript>();
        private static readonly List<UsableKey> _doneUsables = new List<UsableKey>();
        private static readonly NetWriter WL = new NetWriter();

        /// Exclude attached child items: their own item index gives them a stable address even after detachment.
        private static usablescript[] ItemUsables(tosaveitemscript it)
        {
            if (_usableHierarchy.TryGetValue(it, out var h)) return h.Usables;
            var list = new List<usablescript>();
            foreach (var u in it.GetComponentsInChildren<usablescript>(true))
                if (it.usables != null && it.usables.Contains(u)) list.Add(u);
            h = new UsableHierarchy { Usables = list.ToArray() }; _usableHierarchy.Add(it, h);
            return h.Usables;
        }
        private static usablescript LiveUsableOf(UsableKey k, out Ent e)
        {
            if (!ByNet.TryGetValue(k.Net, out e) || e.Epoch != k.Epoch || !Resolve(e) || k.Item < 0 || k.Item >= e.Items.Count ||
                (e.Split != null && e.Split.Contains(k.Item)) || e.Items[k.Item] == null) return null;
            var us = ItemUsables(e.Items[k.Item]);
            return k.Usable >= 0 && k.Usable < us.Length ? us[k.Usable] : null;
        }
        private static ushort QuantizeUsable(float v, float min, float max) => (ushort)Mathf.RoundToInt(Mathf.InverseLerp(min, max, v) * 65535f);
        private static UsableValue UsableBits(usablescript u) => new UsableValue {
            X = QuantizeUsable(u.xRot, u.minX, u.maxX), Y = QuantizeUsable(u.yRot, u.minY, u.maxY),
            Slide = QuantizeUsable(u.slideValue, 0f, 1f), Tuned = QuantizeUsable(u.tunedFloat, u.tunedFloatMin, u.tunedFloatMax), Turn = u.currentTurnState };
        private static PoiState UsableState(usablescript u) => new PoiState { XRot = u.xRot, YRot = u.yRot, Slide = u.slideValue, Tuned = u.tunedFloat, Turn = u.currentTurnState };
        private static bool SameUsable(PoiState a, PoiState b) => a.XRot == b.XRot && a.YRot == b.YRot && a.Slide == b.Slide && a.Tuned == b.Tuned && a.Turn == b.Turn;
        private static PoiState UsableState(usablescript u, UsableValue v) => new PoiState {
            XRot = Mathf.Lerp(u.minX, u.maxX, v.X / 65535f), YRot = Mathf.Lerp(u.minY, u.maxY, v.Y / 65535f),
            Slide = v.Slide / 65535f, Tuned = Mathf.Lerp(u.tunedFloatMin, u.tunedFloatMax, v.Tuned / 65535f), Turn = v.Turn };

        /// Remember the first input's starting value so even its first zero-delta call stays silent.
        [HarmonyLib.HarmonyPatch]
        private static class LiveUsableInput
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return HarmonyLib.AccessTools.Method(typeof(usablescript), nameof(usablescript.Rot), new[] { typeof(float), typeof(float), typeof(bool), typeof(bool) });
                yield return HarmonyLib.AccessTools.Method(typeof(usablescript), nameof(usablescript.Slide), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
                yield return HarmonyLib.AccessTools.Method(typeof(usablescript), nameof(usablescript.Tuned), new[] { typeof(float), typeof(bool) });
                yield return HarmonyLib.AccessTools.Method(typeof(usablescript), nameof(usablescript.UpdTurnState), new[] { typeof(int), typeof(bool) });
            }
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(usablescript __instance, bool syncinmulti)
            {
                if (syncinmulti && LiveUsableSync && InSession && !_applyingState && !_applyingLive && !__instance.setidPoi &&
                    !_localUsables.ContainsKey(__instance) && !_usableBefore.TryGetValue(__instance, out _))
                    _usableBefore.Add(__instance, new UsableBefore { Value = UsableBits(__instance), Raw = UsableState(__instance) });
            }
        }

        /// Zero input stays silent; sub-quantum motion still postpones the exact final record until it settles.
        private static bool MarkLiveUsable(usablescript u)
        {
            if (!LiveUsableSync || !InSession || u.setidPoi) return false;
            if (_applyingState || _applyingLive) return true;
            var it = ItemOf(u, u.tosaveid, u.setid);
            if (it == null) return false;
            var e = SharedGroupOf(it);
            if (e == null || e.NetId == 0) return false;
            int idx = e.Items.IndexOf(it), ui = Array.IndexOf(ItemUsables(it), u);
            if (ui < 0 || (e.Split != null && e.Split.Contains(idx))) return false;
            if (!_localUsables.TryGetValue(u, out var s) || s.Key.Net != e.NetId || s.Key.Epoch != e.Epoch || s.Key.Item != idx || s.Key.Usable != ui)
            {
                s = new LocalUsable { Key = new UsableKey { Net = e.NetId, Epoch = e.Epoch, Item = idx, Usable = ui }, Entity = e, Item = it, U = u };
                _liveOut.TryGetValue(s.Key, out s.Seq);
                _localUsables[u] = s;
                if (_usableBefore.TryGetValue(u, out var before))
                { s.Value = s.Sent = before.Value; s.Raw = before.Raw; s.HasSent = true; _usableBefore.Remove(u); }
                else s.Dirty = true;
            }
            s.HookAt = Time.realtimeSinceStartup;
            var v = UsableBits(u); var raw = UsableState(u);
            if (!s.Dirty && SameUsable(s.Raw, raw)) return true;
            if (!SameUsable(s.Raw, raw) || !s.Final) s.ChangedAt = s.HookAt;
            s.Value = v; s.Raw = raw; s.Final = true; s.Dirty = !s.HasSent || !s.Sent.Equals(v);
            if (e.Proxy)
            {
                MarkEdit(it, kind: "usable");
                if (e.EditHoldUntil == null) e.EditHoldUntil = new Dictionary<int, float>();
                e.EditHoldUntil[idx] = s.HookAt + EditHold;
            }
            return true;
        }
        private static bool LocalUsableHeld(usablescript u) => (mainscript.s != null && mainscript.s.player != null && mainscript.s.player.rotatings == u) ||
            (_dragUsable != null && _dragUsable.U == u) || (_localUsables.TryGetValue(u, out var s) && Time.realtimeSinceStartup - s.HookAt < LiveQuietS);
        private static bool LiveItemMoving(tosaveitemscript it)
        {
            if (!LiveUsableSync) return false;
            float now = Time.realtimeSinceStartup;
            foreach (var s in _localUsables.Values) if (s.Item == it && s.Final && now - s.ChangedAt < LiveQuietS) return true;
            foreach (var s in _remoteUsables.Values) if (s.U != null && s.Entity.Items.Contains(it) && ItemOf(s.U, s.U.tosaveid, s.U.setid) == it && now - s.At < LiveQuietS) return true;
            return false;
        }
        private static void WriteLiveUsable(NetWriter w, UsableKey k, ushort seq, double stamp, UsableValue v)
        {
            w.Reset(); w.U8(Protocol.LiveUsable); w.U32(k.Net); w.U32(k.Epoch); w.VarU32((uint)k.Item); w.VarU32((uint)k.Usable);
            w.U16(seq); w.F64(stamp); w.U16(v.X); w.U16(v.Y); w.U16(v.Slide); w.VarI32(v.Turn); w.U16(v.Tuned);
        }
        private static void ReadLiveUsable(NetReader r, out UsableKey k, out ushort seq, out double stamp, out UsableValue v)
        {
            k = new UsableKey { Net = r.U32(), Epoch = r.U32(), Item = (int)r.VarU32(), Usable = (int)r.VarU32() };
            seq = r.U16(); stamp = r.F64(); v = new UsableValue { X = r.U16(), Y = r.U16(), Slide = r.U16(), Turn = r.VarI32(), Tuned = r.U16() };
        }
        private static bool LiveNewer(Dictionary<UsableKey, LiveOrder> orders, UsableKey k, ushort seq, double stamp)
        {
            if (orders.TryGetValue(k, out var last) && (stamp < last.Stamp || (short)(seq - last.Seq) <= 0))
            { LiveUsablesStale++; return false; }
            orders[k] = new LiveOrder { Seq = seq, Stamp = stamp }; return true;
        }
        private static UsableKey ItemKey(UsableKey k) { k.Usable = -1; return k; }
        private static bool LiveAfterRecord(UsableKey k, double stamp) => !_liveRecordFloor.TryGetValue(ItemKey(k), out double floor) || stamp > floor;
        private static void SetLiveRecordFloor(UsableKey k, double stamp)
        {
            k = ItemKey(k);
            if (!_liveRecordFloor.TryGetValue(k, out double was) || stamp > was) _liveRecordFloor[k] = stamp;
        }

        /// A prefab supplies the bounds even when the host has streamed the object out.
        private static usablescript ServerUsable(SEnt se, UsableKey k)
        {
            var g = GroupRecord(se);
            if (k.Item < 0 || g == null || k.Item >= g.items.Count || g.items[k.Item] == null || itemdatabase.s == null ||
                (se.Gone != null && se.Gone.Contains(k.Item))) return null;
            foreach (var net in se.Parts) if (Server.TryGetValue(net, out var part) && part.PartIndex == k.Item) return null;
            var go = itemdatabase.s.Item(g.items[k.Item].prefabID);
            var it = go != null ? go.GetComponent<tosaveitemscript>() : null;
            if (it == null) return null;
            var us = ItemUsables(it);
            return k.Usable >= 0 && k.Usable < us.Length ? us[k.Usable] : null;
        }
        private static void RefuseLive(ref long counter, string reason, int from, uint net)
        { LiveUsablesRefused++; Refuse(ref counter, reason, $"live usable from player {from}, net {net}: {reason}"); }
        private static void ServerLiveUsable(int from, NetReader r)
        {
            ReadLiveUsable(r, out var k, out ushort seq, out double stamp, out var v);
            if (r.Bad || r.Remaining != 0 || !Finite(stamp) || Math.Abs(SessionLog.St - stamp) > 10 || !Server.TryGetValue(k.Net, out var se) || k.Epoch != se.Epoch)
            { RefuseLive(ref EditRefusedValues, "live address/clock", from, k.Net); return; }
            if (!TakeToken(_liveBuckets, from, LiveUsableRatePerS, LiveUsableBurst))
            { RefuseLive(ref EditRefusedRate, "live rate", from, k.Net); return; }
            if (from != 0 && EditRangeCheck && !AtObject(from, se))
            { RefuseLive(ref EditsOutOfReach, "live reach", from, k.Net); return; }
            var u = ServerUsable(se, k);
            if (u == null || v.Turn < 0 || v.Turn > u.maxTurnState || !Sane(u.minX) || !Sane(u.maxX) || !Sane(u.minY) || !Sane(u.maxY) || !Sane(u.tunedFloatMin) || !Sane(u.tunedFloatMax))
            { RefuseLive(ref EditRefusedValues, "live values", from, k.Net); return; }
            if (!LiveAfterRecord(k, stamp)) { LiveUsablesStale++; return; }
            if (!_liveServerIn.TryGetValue(from, out var orders)) _liveServerIn[from] = orders = new Dictionary<UsableKey, LiveOrder>();
            if (!LiveNewer(orders, k, seq, stamp)) return;
            _liveRelaySeq.TryGetValue(k, out ushort relaySeq); _liveRelaySeq[k] = ++relaySeq;
            WriteLiveUsable(WL, k, relaySeq, stamp, v);
            Mp.Server.ReadySpots(_spots);
            foreach (var s in _spots)
                if (s.Id != from && (s.Id == se.OwnerId || Near(s, se.Where.x, se.Where.z)))
                { Mp.Server.SendToPlayer(s.Id, WL, false); LiveUsablesRelayed++; }
            if (from != 0 && (se.OwnerId == 0 || HostNearUsable(se))) { Deliver(WL); LiveUsablesRelayed++; }
        }
        private static bool HostNearUsable(SEnt se)
        {
            var p = Mp.LocalGlobal(); double dx = p.x - se.Where.x, dz = p.z - se.Where.z;
            return dx * dx + dz * dz <= StateInterestM * StateInterestM;
        }
        private static void ApplyLiveUsable(NetReader r)
        {
            ReadLiveUsable(r, out var k, out ushort seq, out double stamp, out var v);
            if (!LiveUsableSync || r.Bad || !Finite(stamp) || !LiveAfterRecord(k, stamp)) { LiveUsablesStale++; return; }
            if (!LiveNewer(_liveIn, k, seq, stamp)) return;
            var u = LiveUsableOf(k, out var e);
            if (u == null || LocalUsableHeld(u)) return;
            u.FStart();   // veryInvert changes the runtime ranges during initialization
            if (!_remoteUsables.TryGetValue(k, out var s))
            {
                s = new RemoteUsable(); _remoteUsables[k] = s;
                ApplyUsable(u, UsableState(u, v), true);
            }
            s.U = u; s.Entity = e; s.Start = UsableState(u); s.Target = UsableState(u, v); s.At = Time.realtimeSinceStartup; s.Final = true;
            s.Blending = s.Start.XRot != s.Target.XRot || s.Start.YRot != s.Target.YRot || s.Start.Slide != s.Target.Slide || s.Start.Tuned != s.Target.Tuned;
            // Switches are discrete; their input events must happen immediately, once per incoming state.
            if (s.Start.Turn != s.Target.Turn) { s.Start.Turn = s.Target.Turn; ApplyUsable(u, s.Start, true); }
            LiveUsablesApplied++;
            SessionLog.Event("live-applied", k.Net, item: k.Item, slot: k.Usable, to: MyId, epoch: k.Epoch, sample: true);
        }
        private static void ApplyUsable(usablescript u, PoiState v, bool inputEvent = false)
        {
            _applyingLive = true;
            try
            {
                if (inputEvent) u.UpdFromMulti(v.Slide, v.XRot, v.YRot, v.Turn, v.Tuned);
                else { u.tempTunedFloat = u.tunedFloat = v.Tuned; u.Refresh(v.Slide, v.XRot, v.YRot, v.Turn); }
                if (_localUsables.TryGetValue(u, out var local)) ResetUsableBaseline(local);
            }
            finally { _applyingLive = false; }
        }
        private static bool LiveRecordUsables(Ent e, int item, double stamp)
        {
            if (!LiveUsableSync) return true;
            foreach (var s in _localUsables.Values) if (s.Entity == e && s.Key.Item == item && LocalUsableHeld(s.U)) return false;
            foreach (var kv in _liveIn) if (kv.Key.Net == e.NetId && kv.Key.Epoch == e.Epoch && kv.Key.Item == item && kv.Value.Stamp > stamp) return false;
            return true;
        }
        private static void LiveRecordApplied(Ent e, int item, double stamp)
        {
            var floor = new UsableKey { Net = e.NetId, Epoch = e.Epoch, Item = item, Usable = -1 };
            SetLiveRecordFloor(floor, stamp);
            _doneUsables.Clear();
            foreach (var kv in _remoteUsables)
                if (ItemKey(kv.Key).Equals(floor) && (!_liveIn.TryGetValue(kv.Key, out var order) || order.Stamp <= stamp)) _doneUsables.Add(kv.Key);
            foreach (var k in _doneUsables) _remoteUsables.Remove(k);
            _doneUsables.Clear();
            foreach (var kv in _liveIn) if (ItemKey(kv.Key).Equals(floor) && kv.Value.Stamp <= stamp) _doneUsables.Add(kv.Key);
            foreach (var k in _doneUsables) _liveIn.Remove(k);
        }
        private static void LiveLocalBaseline(tosaveitemscript it)
        {
            foreach (var s in _localUsables.Values)
                if (s.Item == it && s.U != null && !s.Final && !LocalUsableHeld(s.U)) ResetUsableBaseline(s);
        }
        private static void ResetUsableBaseline(LocalUsable s)
        { s.Value = s.Sent = UsableBits(s.U); s.Raw = UsableState(s.U); s.HasSent = true; s.Dirty = false; }
        private static void LiveUsableTick()
        {
            DragUsableTick();
            float now = Time.realtimeSinceStartup;
            _deadUsables.Clear();
            foreach (var kv in _localUsables)
            {
                var s = kv.Value;
                if (s.U == null || s.Item == null || !ByNet.TryGetValue(s.Key.Net, out var e) || e != s.Entity || e.Epoch != s.Key.Epoch)
                { _deadUsables.Add(kv.Key); continue; }
                if (s.Dirty && LiveUsableSync && now >= s.SendAt)
                {
                    s.SendAt = now + LiveStepS; s.Dirty = false;
                    s.Sent = s.Value; s.HasSent = true;
                    _liveOut[s.Key] = ++s.Seq;
                    WriteLiveUsable(WL, s.Key, s.Seq, SessionLog.St, s.Value); ToServer(WL, false); LiveUsablesSent++;
                    SessionLog.Event("live-sent", s.Key.Net, item: s.Key.Item, slot: s.Key.Usable, from: MyId, epoch: s.Key.Epoch, sample: true);
                }
                if (s.Final && (!LiveUsableSync || now - s.ChangedAt >= LiveQuietS))
                { s.Final = false; ForgetResyncHash(s.Entity, s.Key.Item); MarkEdit(s.Item, kind: "usable"); }
            }
            foreach (var u in _deadUsables) _localUsables.Remove(u);
            _doneUsables.Clear();
            foreach (var kv in _remoteUsables)
            {
                var s = kv.Value;
                if (!LiveUsableSync || s.U == null || !ByNet.TryGetValue(kv.Key.Net, out var e) || e.Epoch != kv.Key.Epoch || LocalUsableHeld(s.U))
                { _doneUsables.Add(kv.Key); continue; }
                float t = Mathf.Clamp01((now - s.At) / LiveStepS);
                if (s.Blending)
                {
                    var v = s.Target;
                    v.XRot = Mathf.Lerp(s.Start.XRot, v.XRot, t); v.YRot = Mathf.Lerp(s.Start.YRot, v.YRot, t);
                    v.Slide = Mathf.Lerp(s.Start.Slide, v.Slide, t); v.Tuned = Mathf.Lerp(s.Start.Tuned, v.Tuned, t);
                    ApplyUsable(s.U, v);
                    if (t >= 1f) s.Blending = false;
                }
                if (now - s.At >= LiveQuietS)
                {
                    if (s.Final && !s.Entity.Proxy) { ForgetResyncHash(s.Entity, kv.Key.Item); MarkEdit(s.Entity.Items[kv.Key.Item], kind: "usable"); }
                    s.Final = false; _doneUsables.Add(kv.Key);
                }
            }
            foreach (var k in _doneUsables) _remoteUsables.Remove(k);
            UsableProbeTick();
        }
        /// The viewers last saw a quantized live value: the owner's settling pass must send the exact record even when the
        /// drag ended where it began (its hash would otherwise match and the record be skipped).
        private static void ForgetResyncHash(Ent e, int item)
        {
            if (e != null && !e.Proxy && e.ResyncHashes != null && item >= 0 && item < e.ResyncHashes.Length) e.ResyncHashes[item] = 0;
        }
        private static void LiveUsablesReset()
        {
            _localUsables.Clear(); _remoteUsables.Clear(); _liveIn.Clear(); _liveOut.Clear(); _liveServerIn.Clear(); _liveRelaySeq.Clear(); _liveRecordFloor.Clear(); _liveBuckets.Clear();
            _usableHierarchy = new ConditionalWeakTable<tosaveitemscript, UsableHierarchy>(); _dragUsable = null; _usableProbe = null;
            _usableBefore = new ConditionalWeakTable<usablescript, UsableBefore>();
            EditDirty.Clear(); EditKinds.Clear(); _editFlushAt = 0; _resyncFirst = 0;
        }
        public static string LiveUsableStats() => "{\"on\":" + (LiveUsableSync ? "true" : "false") + ",\"sent\":" + LiveUsablesSent + ",\"relayed\":" + LiveUsablesRelayed +
            ",\"applied\":" + LiveUsablesApplied + ",\"stale\":" + LiveUsablesStale + ",\"refused\":" + LiveUsablesRefused + "}";

        // ---------------------------------------------------------------- bridge: real input calls, per-frame observations in the session clock
        private sealed class UsableDrag { public usablescript U; public double Start, End, Next; public float Hz; }
        private static UsableDrag _dragUsable;
        private struct UsableSample { public double St; public PoiState Value; }
        private sealed class UsableProbe
        {
            public usablescript U; public double Start, End; public int Frames, Updates; public bool Truncated;
            public UsableValue Last; public readonly List<UsableSample> Samples = new List<UsableSample>();
        }
        private static UsableProbe _usableProbe;
        private static string UN(double n) => n.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        private static string UsableValuesJson(PoiState v) => "[" + UN(v.XRot) + "," + UN(v.YRot) + "," + UN(v.Slide) + "," + v.Turn + "," + UN(v.Tuned) + "]";
        private static bool UsableArgs(uint net, int item, int usable, out UsableKey k, out usablescript u)
        {
            k = new UsableKey { Net = net, Item = item, Usable = usable };
            u = null;
            if (!ByNet.TryGetValue(net, out var e)) return false;
            k.Epoch = e.Epoch; u = LiveUsableOf(k, out _); return u != null;
        }
        public static string Usables(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"entity is not loaded here\"}";
            var rows = new List<string>();
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i];
                if (it == null || (e.Split != null && e.Split.Contains(i))) continue;
                var us = ItemUsables(it);
                for (int j = 0; j < us.Length; j++)
                {
                    var u = us[j]; if (u == null) continue;
                    string kind = (u.rotateAble ? "rot " : "") + (u.slideAble ? "slide " : "") + (u.turnable ? "turn " : "") + (u.tuneAble ? "tune " : "");
                    string path = u.name;
                    for (var t = u.transform.parent; t != null && t != it.transform; t = t.parent) path = t.name + "/" + path;
                    var g = mainscript.GlobalFromUnityPos(u.transform.position);
                    rows.Add("{\"item\":" + i + ",\"usable\":" + j + ",\"itemName\":" + Json.Str(it.name) + ",\"name\":" + Json.Str(path) +
                        ",\"kind\":" + Json.Str(kind.Trim()) + ",\"value\":" + UsableValuesJson(UsableState(u)) + ",\"at\":[" + UN(g.x) + "," + UN(g.y) + "," + UN(g.z) + "]}");
                }
            }
            return "{\"net\":" + net + ",\"owner\":" + e.OwnerId + ",\"st\":" + UN(SessionLog.St) + ",\"usables\":[" + string.Join(",", rows) + "]}";
        }
        public static string DragUsable(uint net, int item, int usable, float seconds, float hz)
        {
            if (!Finite(seconds) || !Finite(hz) || seconds <= 0 || seconds > 60 || hz < 1 || hz > 240 || !UsableArgs(net, item, usable, out _, out var u))
                return "{\"error\":\"loaded net/item/usable, seconds (0,60], hz [1,240] required\"}";
            if (!(u.rotateAble || u.slideAble || u.turnable || u.tuneAble)) return "{\"error\":\"usable has no input action\"}";
            u.FStart();
            double start = SessionLog.St;
            _dragUsable = new UsableDrag { U = u, Start = start, End = start + seconds, Next = start, Hz = hz };
            return "{\"start\":" + UN(start) + ",\"end\":" + UN(start + seconds) + ",\"hz\":" + UN(hz) + "}";
        }
        /// Monotonic motion makes the sender/receiver traces unambiguous when measuring delay offline.
        private static void DragUsableTick()
        {
            var d = _dragUsable;
            if (d == null) return;
            if (d.U == null) { _dragUsable = null; return; }
            double now = SessionLog.St;
            if (now < d.Next && now < d.End) return;
            var u = d.U; float t = Mathf.Clamp01((float)((now - d.Start) / (d.End - d.Start)));
            float value = 0.15f + 0.7f * t;
            if (u.rotateAble && u.RT1 != null)
            {
                float x = Mathf.Lerp(u.minX, u.maxX, value), y = Mathf.Lerp(u.minY, u.maxY, value);
                float sens = mainscript.s.rotSens;
                if (Mathf.Abs(sens) > 1e-6f) u.Rot((x - u.xRot) / (u.invX ? sens : -sens), (y - u.yRot) / (u.invY ? -sens : sens), true);
            }
            else if (u.slideAble)
            {
                float speed = u.slideSpeed * mainscript.s.slideSens, delta = value - u.slideValue;
                float mx = u.slideMultiplier.x == 0 ? 1 : u.slideMultiplier.x, my = u.slideMultiplier.y == 0 ? 1 : u.slideMultiplier.y, mz = u.slideMultiplier.z == 0 ? 1 : u.slideMultiplier.z;
                if (Mathf.Abs(speed) > 1e-6f)
                {
                    if (u.slideX != u.slideXi) u.Slide(delta / (speed * mx * (u.slideX ? 1 : -1)), 0, 0, true);
                    else if (u.slideY != u.slideYi) u.Slide(0, delta / (speed * my * (u.slideY ? 1 : -1)), 0, true);
                    else if (u.slideZ != u.slideZi) u.Slide(0, 0, delta / (speed * mz * (u.slideZ ? 1 : -1)), true);
                }
            }
            else if (u.tuneAble) u.Tuned((Mathf.Lerp(u.tunedFloatMin, u.tunedFloatMax, value) - u.tunedFloat) / (u.tuneSpeed == 0 ? 1 : u.tuneSpeed), true);
            else if (u.turnable) u.Turn();
            d.Next = now + 1.0 / d.Hz;
            if (now >= d.End) _dragUsable = null;
        }
        /// Sampling runs in Entities.Tick, independent of bridge polling, for both the old and live paths.
        public static string ProbeUsable(uint net, int item, int usable, float seconds)
        {
            if (!Finite(seconds) || seconds <= 0 || seconds > 90 || !UsableArgs(net, item, usable, out _, out var u))
                return "{\"error\":\"loaded net/item/usable and seconds (0,90] required\"}";
            _usableProbe = new UsableProbe { U = u, Start = SessionLog.St, End = SessionLog.St + seconds, Last = UsableBits(u) };
            return "{\"start\":" + UN(_usableProbe.Start) + ",\"end\":" + UN(_usableProbe.End) + "}";
        }
        private static void UsableProbeTick()
        {
            var p = _usableProbe;
            if (p == null || p.U == null || SessionLog.St > p.End) return;
            p.Frames++; var v = UsableBits(p.U);
            if (!v.Equals(p.Last)) { p.Updates++; p.Last = v; }
            if (p.Samples.Count < 8192) p.Samples.Add(new UsableSample { St = SessionLog.St, Value = UsableState(p.U) });
            else p.Truncated = true;
        }
        public static string UsableProbeJson()
        {
            var p = _usableProbe;
            if (p == null) return "{\"error\":\"start mp usableprobe net item usable seconds first\"}";
            var rows = new List<string>();
            foreach (var s in p.Samples) rows.Add("[" + UN(s.St) + "," + UsableValuesJson(s.Value) + "]");
            double elapsed = Math.Max(1e-3, Math.Min(SessionLog.St, p.End) - p.Start);
            return "{\"start\":" + UN(p.Start) + ",\"end\":" + UN(p.End) + ",\"frames\":" + p.Frames + ",\"updates\":" + p.Updates + ",\"updatesPerS\":" + UN(p.Updates / elapsed) +
                ",\"truncated\":" + (p.Truncated ? "true" : "false") + ",\"samples\":[" + string.Join(",", rows) + "]}";
        }
    }
}
