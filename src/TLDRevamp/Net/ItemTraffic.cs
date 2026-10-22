using System;
using System.Collections.Generic;
using UnityEngine;
using Scan = TLDRevamp.Net.StateTail.Scan;

namespace TLDRevamp.Net
{
    /// What this machine sends and receives about each shared object; rates are sampled lazily over at least a second.
    public static class ItemTraffic
    {
        public static bool Show;
        private static int _viewRevision = -1;   // the debug view this panel switched on itself (DebugView.Revision then)
        /// Off again ends the debug view it started — unless the player changed the view since (scan: Insert twice left
        /// the view on, and saved, across restarts).
        internal static void SetVisible(bool value)
        {
            if (value && !DebugView.Active) { DebugView.EnableTool(DebugViewMode.Everything); _viewRevision = DebugView.Revision; }
            else if (!value && Show && _viewRevision == DebugView.Revision) { DebugView.Mode = DebugViewMode.Off; _viewRevision = -1; }
            Show = value;
        }
        /// Mod ids have their own namespace; up and down are always relative to this machine.
        public struct Row { public uint Net; public bool Mod; public float UpBps, DownBps, UpMsgs, DownMsgs; }
        private struct Counter
        {
            public uint Net; public bool Mod; public int Next; public float Seen;
            public long Up, Down, UpN, DownN, PrevUp, PrevDown, PrevUpN, PrevDownN, Packet;
            public Row Rate;
        }
        // A bounded pool keeps first-seen objects allocation-free and peer-supplied ids from growing memory forever.
        private const int Capacity = 65536;
        private static readonly Counter[] Counts = new Counter[Capacity];
        private static readonly Dictionary<ulong, int> Slots = new Dictionary<ulong, int>(Capacity);
        private static int _used, _evict, _free = -1;
        private static float _rateAt = -1f; private static long _packet;
        private static readonly List<Row> Rows = new List<Row>();
        private sealed class Busiest : IComparer<Row>
        {
            public int Compare(Row a, Row b)
            {
                int n = (b.UpBps + b.DownBps).CompareTo(a.UpBps + a.DownBps);
                if (n != 0) return n;
                n = a.Mod.CompareTo(b.Mod); return n != 0 ? n : a.Net.CompareTo(b.Net);
            }
        }
        private static readonly Busiest Order = new Busiest();
        private static ulong Key(uint net, bool mod) => net | (mod ? 1UL << 32 : 0);
        private static void Note(uint net, bool mod, bool up, int bytes)
        {
            if (net == 0 || bytes <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (_rateAt < 0f) _rateAt = now;
            ulong key = Key(net, mod);
            if (!Slots.TryGetValue(key, out int i))
            {
                if (_free >= 0) { i = _free; _free = Counts[i].Next; }
                else if (_used < Capacity) i = _used++;
                else { i = _evict; _evict = (_evict + 1) % Capacity; Slots.Remove(Key(Counts[i].Net, Counts[i].Mod)); }
                Counts[i] = new Counter { Net = net, Mod = mod };
                Slots.Add(key, i);
            }
            ref var c = ref Counts[i]; c.Seen = now;
            if (up) { c.Up += bytes; if (c.Packet != _packet) c.UpN++; } else { c.Down += bytes; if (c.Packet != _packet) c.DownN++; }
            c.Packet = _packet;
        }
        /// A complete message, plus its batch length prefix in bytes. The batch type itself has no object.
        internal static void Packet(byte[] buf, int offset, int length, int bytes, bool up, bool server)
        {
            if (buf == null || offset < 0 || length <= 0 || offset > buf.Length - length || bytes < length) return;
            _packet++;
            var r = new Scan { Buf = buf, Pos = offset, End = offset + length };
            byte type = r.U8(); uint net = 0, other = 0; bool mod = false;
            bool fromServer = up == server;
            switch (type)
            {
                // Shares name sender-local tags; POI usables name coordinates, never an entity net id.
                case Entities.Share: case ModObjects.ModShare: case Entities.PoiUsable: return;
                case Entities.Assigned: case ModObjects.ModAssigned:
                    mod = type == ModObjects.ModAssigned; r.Skip(4); net = r.U32(); r.Skip(4); break;
                case Entities.Add:
                    net = r.U32(); r.Var(); r.Skip(4); if (r.U8() != 0) r.Skip(40); r.Bytes(); break;
                case Entities.CompactState:
                    net = r.Var(); r.Var(); r.Skip(21); break;
                case Entities.State:
                    if (!StateTail.TryRead(buf, offset, length, out var tail)) return;
                    net = tail.Net; r.Pos = r.End;
                    break;
                case Entities.Claim: case Entities.RemoveItem: case Entities.Refetch: case Entities.CrashRelease:
                    net = r.U32(); break;
                case Entities.Owner:
                    net = r.U32(); r.Var(); r.Skip(4); break;
                case Entities.PartOff:
                    net = r.U32(); r.Skip(4); r.Var(); r.Skip(40); break;
                case Entities.PartDetached:
                    net = r.U32(); r.Var(); other = r.U32(); r.Var(); r.Skip(44); break;
                case Entities.Resync: case Entities.HandoverState:
                    net = r.U32(); r.Skip(type == Entities.Resync ? 12 : 4); r.Records(); break;
                case Entities.Edit:
                    net = r.U32(); r.Skip(8); r.Var(); r.Var(); r.Bytes(); break;
                case Entities.ContactImpulse:
                    net = r.U32(); r.Skip(36); break;
                case Entities.DetachReq: case Entities.MemberGone: case Entities.BreakFx:
                    net = r.U32(); r.Var(); break;
                case Entities.PushIn:
                    net = r.U32(); r.Var(); r.Skip(29); break;
                case Protocol.LiveUsable:
                    net = r.U32(); if (net == 0) return; // building address, never an item entity
                    r.Skip(4); r.Var(); r.Var(); r.Skip(16); r.Var(); r.Skip(2); break;
                case Entities.IgnitionKey:
                    net = r.U32(); r.Skip(3); break;
                case Entities.Worn:
                    net = r.U32(); if (fromServer) r.Var(); else r.Skip(4);
                    if (r.U8() != 255) r.Skip(19); break;
                case Entities.AttachSync: case Entities.AttachReq:
                    net = r.U32(); byte attach = r.U8(); r.Skip(1); r.Var(); uint parent = r.U32();
                    if (attach == 1) other = parent;
                    r.Var(); r.Var(); r.Skip(72);
                    if (r.U8() == 1) { r.Var(); r.Var(); r.Var(); r.Skip(7); }
                    break;
                case Entities.DetachSync:
                    net = r.U32(); r.Skip(44); break;
                case Entities.CrashClaim:
                    net = r.U32(); other = r.U32(); break;
                case Entities.CensusAsk:
                    net = r.U32(); r.Skip(9); if (fromServer) r.Var(); break;
                case Entities.CensusReply:
                    net = r.U32(); r.Var(); r.Skip(1); r.Str(); break;
                case Entities.ShotgunIn:
                    net = r.U32(); r.Skip(6); break;
                case Entities.ExplodeReq:
                    net = r.U32(); r.Skip(1); r.Var(); r.Skip(5); break;
                case Entities.BreakHit:
                    net = r.U32(); r.Var(); byte kind = r.U8(); r.Skip(16); if (kind == 3) r.Var(); break;
                case Entities.AiState:
                    net = r.U32(); r.Var(); r.Skip(15); break;
                case Entities.AiSound:
                    net = r.U32(); r.Var(); r.Skip(1); r.Var(); r.Skip(4); break;
                case Entities.RabbitState:
                    net = r.U32(); r.Skip(19); break;
                case Entities.RabbitFx:
                    net = r.U32(); r.Skip(1); r.Var(); break;
                case PlayerCombat.PlayerDamage:
                    r.Var(); r.Skip(16); byte damage = r.U8(); r.Skip(12);
                    if (damage == HitCheck.KCreature) { net = r.U32(); r.Var(); }
                    break;
                case PlayerSave.Record:
                    Saved(r, bytes, up); return;
                case Entities.ShotFx:
                    Shot(r, bytes, up); return;
                case ModObjects.ModAdd:
                    mod = true; net = r.U32(); r.Var(); r.Skip(4); r.Str(); r.Skip(42); r.Bytes(); r.Bytes(); break;
                case ModObjects.ModRecord:
                    mod = true; net = r.U32(); r.Skip(44); r.Bytes(); break;
                case ModObjects.ModEdit:
                    mod = true; net = r.U32(); if (fromServer) r.Var(); r.Bytes(); break;
                case ModObjects.ModOwner:
                    mod = true; net = r.U32(); r.Var(); r.Skip(4); break;
                case ModObjects.ModRemove: case ModObjects.ModStore: case ModObjects.ModClaim:
                    mod = true; net = r.U32(); break;
                case ModObjects.ModPose:
                    mod = true; net = r.U32(); r.Skip(60); break;
                case ModObjects.ModState:
                    mod = true; net = r.U32(); r.Skip(4); r.Bytes(); break;
                case ModObjects.ModEvent:
                    mod = true; r.Str(); if (fromServer) r.Var(); else r.Skip(1); net = r.U32(); r.Bytes(); break;
                default: return;
            }
            if (r.Bad || r.Pos != r.End) return;
            if (other == 0 || other == net) Note(net, mod, up, bytes);
            else
            {
                // Relationship messages have no independent records: share their bytes without double counting.
                Note(net, mod, up, bytes / 2 + bytes % 2); Note(other, mod, up, bytes / 2);
            }
        }
        /// Shot hits are variable records. Validate first, then charge each hit its bytes and an equal header share.
        private static void Shot(Scan r, int bytes, bool up)
        {
            uint gun = 0;
            if ((r.U8() & 4) != 0) { gun = r.U32(); r.Var(); }
            r.Skip(37); int n = r.U8(), start = r.Pos;
            var hits = r;
            for (int i = 0; i < n && !r.Bad; i++) ShotHit(ref r);
            if (r.Bad || r.Pos != r.End) return;
            int header = bytes - (r.End - start), shares = n + (gun != 0 ? 1 : 0);
            if (shares == 0) return;
            int each = header / shares, extra = header % shares;
            if (gun != 0) { Note(gun, false, up, each + (extra > 0 ? 1 : 0)); if (extra > 0) extra--; }
            for (int i = 0; i < n; i++)
            {
                int at = hits.Pos; uint net = ShotHit(ref hits);
                Note(net, false, up, hits.Pos - at + each + (extra > 0 ? 1 : 0)); if (extra > 0) extra--;
            }
        }
        private static uint ShotHit(ref Scan r)
        {
            byte kind = r.U8(); r.Skip(1); byte decal = r.U8(); uint net = 0;
            switch (kind)
            {
                case 1: net = r.U32(); r.Str(); r.Skip(24); break;
                case 2: r.Skip(36); break;
                case 3: r.Var(); r.Var(); r.Skip(24); break;
                case 4: net = r.U32(); r.Var(); r.Skip(24); return net;
                default: r.Bad = true; return 0;
            }
            if (decal != 255) r.Skip(20);
            return net;
        }
        /// Inventory uploads carry several shared groups; local ids and worn ids are not entity addresses.
        private static void Saved(Scan r, int bytes, bool up)
        {
            r.Str(); r.Skip(13); uint count = r.Var();
            if (r.Bad || count > int.MaxValue) return;
            int n = (int)count, start = r.Pos;
            var records = r;
            for (int i = 0; i < n && !r.Bad; i++) { r.Skip(8); r.Bytes(); }
            int end = r.Pos;
            if (r.Pos < r.End)
            {
                uint worn = r.Var();
                if (worn > int.MaxValue / 4) return;
                r.Skip((int)worn * 4);
            }
            if (r.Bad || r.Pos != r.End || n == 0) return;
            int header = bytes - (end - start), each = header / n, extra = header % n;
            for (int i = 0; i < n; i++)
            {
                int at = records.Pos; records.Skip(4); uint net = records.U32(); records.Bytes();
                Note(net, false, up, records.Pos - at + each + (i < extra ? 1 : 0));
            }
        }
        private static void Rates()
        {
            float now = Time.realtimeSinceStartup, dt = now - _rateAt;
            if (_rateAt < 0f || dt < 1f) return;
            for (int i = 0; i < _used; i++)
            {
                ref var c = ref Counts[i];
                if (c.Net == 0) continue;
                if (now - c.Seen >= 10f)
                {
                    Slots.Remove(Key(c.Net, c.Mod)); c = default; c.Next = _free; _free = i; continue;
                }
                c.Rate = new Row { Net = c.Net, Mod = c.Mod, UpBps = (c.Up - c.PrevUp) / dt, DownBps = (c.Down - c.PrevDown) / dt,
                                   UpMsgs = (c.UpN - c.PrevUpN) / dt, DownMsgs = (c.DownN - c.PrevDownN) / dt };
                c.PrevUp = c.Up; c.PrevDown = c.Down; c.PrevUpN = c.UpN; c.PrevDownN = c.DownN;
            }
            _rateAt = now;
        }
        /// The objects with traffic over the last >=1 s, busiest first; no traffic leaves an empty list.
        public static void Current(List<Row> into)
        {
            Rates(); into.Clear();
            for (int i = 0; i < _used; i++)
                if (Counts[i].Net != 0 && Counts[i].Rate.UpBps + Counts[i].Rate.DownBps > 0f) into.Add(Counts[i].Rate);
            into.Sort(Order);
        }
        public static void Reset()
        {
            Slots.Clear(); Array.Clear(Counts, 0, _used); _used = _evict = 0; _free = -1; _rateAt = -1f; _packet = 0; Rows.Clear();
        }
        /// Unity clone / duplicate suffixes obscure the object's name; labels cache this and bridge rows resolve it on demand.
        internal static string ObjectName(string name)
        {
            if (name == null) return null;
            int end = name.Length;
            while (end > 0)
            {
                if (end >= 7 && string.CompareOrdinal(name, end - 7, "(Clone)", 0, 7) == 0) { end -= 7; continue; }
                if (name[end - 1] != ')') break;
                int at = end - 2;
                while (at >= 0 && name[at] >= '0' && name[at] <= '9') at--;
                if (at == end - 2 || at < 1 || name[at] != '(' || name[at - 1] != ' ') break;
                end = at - 1;
            }
            return end == name.Length ? name : name.Substring(0, end);
        }
        /// Bridge rows resolve names only on demand; packet accounting never touches Unity objects.
        public static string ListJson(int n)
        {
            Current(Rows); var sb = new System.Text.StringBuilder("[");
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Rows.Count && i < n; i++)
            {
                var r = Rows[i];
                var go = r.Mod ? TLDRevamp.Multiplayer.MpApi.Find(r.Net)?.GameObject : Entities.GameObjectOf(r.Net);
                if (i > 0) sb.Append(',');
                sb.Append("{\"net\":").Append(r.Net).Append(",\"mod\":").Append(r.Mod ? "true" : "false")
                  .Append(",\"up\":").Append(r.UpBps.ToString("F2", ic)).Append(",\"down\":").Append(r.DownBps.ToString("F2", ic))
                  .Append(",\"upMsgs\":").Append(r.UpMsgs.ToString("F2", ic)).Append(",\"downMsgs\":").Append(r.DownMsgs.ToString("F2", ic))
                  .Append(",\"name\":").Append((go != null ? TLDRevamp.Json.Str(ObjectName(go.name)) : "null")).Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
