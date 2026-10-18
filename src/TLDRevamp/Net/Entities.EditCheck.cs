using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Server: what a player's edit of someone else's object (Entities.Edits) and a building usable change
    /// (Entities.PoiUsables) may do. Before v0.69.2 the server checked only that the sender stood at the object: the
    /// record itself went to the owner unread — an edit naming every kind (KindAll) loaded the car's controls and
    /// re-attached parts, a "tank fill" could put 10,000 L into a jerry can or NaN into a fuel tank, a colour edit
    /// could repair every part, and a door change could come from anywhere in the world, as often as the sender liked.
    /// Now the server decodes each edit and refuses it when it names kinds edits never carry, when it changes something
    /// its kinds don't cover, or when a value is out of range (non-finite, a tank over capacity, a part look index
    /// nobody has); and each player gets a rate limit for edits and for building usables. Refusals count as anomalies
    /// (Entities.Anomaly), the first 5 of each reason are logged. The host's own edits are trusted (its world is the save).
    public static partial class Entities
    {
        public static bool EditValidation = true;   // A/B: false = before v0.69.2 (only "is the sender at the object")
        public const int KindsKnown = KindUsable | KindTank | KindPart | KindColor | KindFood | KindAmmo | KindPlate | KindBusDoor;
        /// An item's edits leave at most every 0.15 s (EditTick): a door dragged is ~7 a second, pouring the same.
        public static float EditRatePerS = 20f, EditBurst = 40f;
        /// A building usable goes out at most 10 times a second (PoiUsableTick); a few at once (a double gate).
        public static float PoiRatePerS = 30f, PoiBurst = 60f;
        /// How far from a building's position (its poiid: the building's own spot) a player may be to move one of its
        /// usables — buildings are big; their usables are within ~60 m of it (gas station, the big sites).
        public static float PoiReachM = 150f;
        public static float UnknownTankL = 1000f;   // a tank whose capacity the server can't look up (no copy, no prefab)
        public static long EditRefusedRate, EditRefusedKinds, EditRefusedValues, PoiRefusedReach, PoiRefusedRate, PoiRefusedValues;
        public static string EditRefusedLast = "";

        private struct RateBucket { public float Tokens, At; }
        private static readonly Dictionary<int, RateBucket> _editBuckets = new Dictionary<int, RateBucket>(), _poiBuckets = new Dictionary<int, RateBucket>();
        private static readonly Dictionary<string, int> _refusalsLogged = new Dictionary<string, int>();
        private static readonly Dictionary<uint, KeyValuePair<byte[], itemDataClass>> _groupRecords = new Dictionary<uint, KeyValuePair<byte[], itemDataClass>>();
        private static readonly int FluidKinds = Enum.GetValues(typeof(mainscript.fluidenum)).Length;

        private static bool TakeToken(Dictionary<int, RateBucket> d, int player, float rate, float burst)
        {
            float now = Time.realtimeSinceStartup;
            if (!d.TryGetValue(player, out var b)) b = new RateBucket { Tokens = burst, At = now };
            b.Tokens = Mathf.Min(burst, b.Tokens + (now - b.At) * rate); b.At = now;
            bool ok = b.Tokens >= 1f;
            if (ok) b.Tokens -= 1f;
            d[player] = b;
            return ok;
        }

        /// One refusal: its own counter and the anomaly count; the first 5 of each reason logged as [anomaly] lines.
        private static void Refuse(ref long counter, string reason, string what)
        {
            counter++;
            EditRefusedLast = what;
            _refusalsLogged.TryGetValue(reason, out int n);
            _refusalsLogged[reason] = n + 1;
            if (n < 5) Anomaly(what + " (refused)"); else Anomalies++;
        }

        private static void EditCheckForget(int player) { _editBuckets.Remove(player); _liveBuckets.Remove(player); _liveServerIn.Remove(player); _poiBuckets.Remove(player); }
        private static void EditCheckReset() { _editBuckets.Clear(); _poiBuckets.Clear(); _refusalsLogged.Clear(); _groupRecords.Clear(); }

        /// The object's record as shared (decoded once per object and record).
        private static itemDataClass GroupRecord(SEnt se)
        {
            if (se.Record == null) return null;
            if (_groupRecords.TryGetValue(se.NetId, out var c) && ReferenceEquals(c.Key, se.Record)) return c.Value;
            if (_groupRecords.Count > 512) _groupRecords.Clear();
            var d = RecordCodec.Decode(se.Record);
            _groupRecords[se.NetId] = new KeyValuePair<byte[], itemDataClass>(se.Record, d);
            return d;
        }

        /// The owner's latest state of group member idx: its last resync record, else its part of the shared record
        /// (an item unchanged since the share never resyncs). Group members are in the record's items in group order.
        private static itemDataClass Baseline(SEnt se, int idx)
        {
            if (se.ItemState.TryGetValue(idx, out var b)) return RecordCodec.Decode(b);
            var g = GroupRecord(se);
            if (g == null || idx >= g.items.Count || g.items[idx] == null) return null;
            uint id = g.items[idx].id;
            var o = new itemDataClass();
            foreach (var f in ItemSnapshot.ListFields)
            {
                if (!(f.GetValue(g) is IList src) || !(f.GetValue(o) is IList dst)) continue;
                foreach (var x in src)
                {
                    var idf = x != null ? _idField(x) : null;
                    if (idf != null && idf.FieldType == typeof(uint) && (uint)idf.GetValue(x) == id) dst.Add(x);
                }
            }
            return o;
        }

        /// Litres tank `ind` of group member idx holds: the host's copy if it has one placed, else the item's prefab.
        private static float TankCapacity(SEnt se, int idx, int ind)
        {
            if (ByNet.TryGetValue(se.NetId, out var e) && e.Items != null && idx < e.Items.Count && e.Items[idx] != null)
            {
                var c = MaxC(e.Items[idx], ind);
                if (c > 0) return c;
            }
            var g = GroupRecord(se);
            if (g != null && idx < g.items.Count && g.items[idx] != null && itemdatabase.s != null)
            {
                var go = itemdatabase.s.Item(g.items[idx].prefabID);
                var c = go != null ? MaxC(go.GetComponent<tosaveitemscript>(), ind) : 0f;
                if (c > 0) return c;
            }
            return UnknownTankL;
        }

        private static float MaxC(tosaveitemscript it, int ind)
        {
            if (it == null || it.tanks == null || ind < 0 || ind >= it.tanks.Count || it.tanks[ind] == null || it.tanks[ind].F == null) return 0f;
            return it.tanks[ind].F.maxC;
        }

        private static bool Sane(float v) => Finite(v) && Math.Abs(v) <= 1e5f;
        private static bool SaneColor(save_color c) => c == null || (Sane(c.r) && Sane(c.g) && Sane(c.b) && Sane(c.a) && Math.Abs(c.r) <= 100 && Math.Abs(c.g) <= 100 && Math.Abs(c.b) <= 100 && Math.Abs(c.a) <= 100);
        private static bool Ind(int i) => i >= 0 && i < 1024;

        /// Server: is this edit's record something its kinds may do? null = yes, else why not.
        private static string CheckEdit(SEnt se, int idx, int kinds, byte[] rec)
        {
            if (kinds == 0 || (kinds & ~KindsKnown) != 0) return "kinds " + kinds;   // KindAll: the car's controls and its attachments — never another player's edit
            if (idx > 4096) return "index " + idx;
            var d = RecordCodec.Decode(rec);
            if (d == null) return "record unreadable";
            bool K(int bit) => (kinds & bit) != 0;
            if (K(KindUsable))
            {
                foreach (var u in d.usable)
                    if (u == null || !Ind(u.ind) || !Sane(u.xRot) || !Sane(u.yRot) || !Sane(u.slideValue) || !Sane(u.tunedFloat) || u.currentTurnState < -1 || u.currentTurnState > 1024) return "usable value";
                foreach (var x in d.door_rots) if (x == null || !Ind(x.ind) || !Sane(x.state)) return "door value";
                foreach (var x in d.ginlamp) if (x == null || !Sane(x.value)) return "lamp value";
            }
            if (K(KindTank))
                foreach (var t in d.tanks)
                {
                    if (t == null || !Ind(t.ind)) return "tank";
                    int n = t.amount != null ? t.amount.Length : 0;
                    if ((t.type != null ? t.type.Length : 0) != n || n > 16) return "tank fluids";
                    float sum = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        // a tank poured dry keeps a float leftover just under 0: refused, the pour was lost on the owner
                        if (!Finite(t.amount[i]) || t.amount[i] < -0.01f) return "tank amount " + t.amount[i];
                        if (t.type[i] < 0 || t.type[i] >= FluidKinds) return "fluid type " + t.type[i];
                        sum += t.amount[i];
                    }
                    float cap = TankCapacity(se, idx, t.ind);
                    if (sum > cap * 1.01f + 0.01f) return $"tank over capacity: {sum:F2} of {cap:F2} L";
                }
            if (K(KindPart | KindColor))
            {
                foreach (var p in d.partconditions)
                    if (p == null || !Ind(p.ind) || p.state < 0 || p.state > 16 || p.state2 < 0 || p.state2 > 4096 || p.state3 < 0 || p.state3 > 4096 ||
                        p.slectedRandomTipus < 0 || p.slectedRandomTipus > 256 || !SaneColor(p.color))
                        return p == null ? "part value: null" : $"part value: part {p.ind} state {p.state}/{p.state2}/{p.state3} look {p.slectedRandomTipus}" + (SaneColor(p.color) ? "" : " colour");
                // a paint job carries the part conditions too (one list): it may change their colour, never their condition
                if (!K(KindPart) && d.partconditions.Count > 0)
                {
                    var b = Baseline(se, idx);
                    if (b == null) return "colour edit, the item's part conditions unknown";
                    foreach (var p in d.partconditions)
                    {
                        var q = b.partconditions.Find(x => x != null && x.ind == p.ind);
                        if (q == null || q.state != p.state || q.state2 != p.state2 || q.state3 != p.state3 || q.slectedRandomTipus != p.slectedRandomTipus)
                            return "colour edit changes part " + p.ind + "'s condition";
                    }
                }
            }
            if (K(KindColor))
            {
                foreach (var c in d.colors) if (c == null || !Ind(c.ind) || !SaneColor(c)) return "colour value";
                foreach (var p in d.painting) if (p == null || p.index < 0 || p.index > 4096) return "painting";
            }
            if (K(KindFood)) foreach (var f in d.food) if (f == null || !Sane(f.hp) || f.hp < 0f || f.index < 0 || f.index > 4096) return "food value";
            if (K(KindAmmo)) foreach (var a in d.ammo) if (a == null || a.count < 0 || a.count > 10000 || !Sane(a.fammo) || a.fammo < 0f) return "ammo value";
            if (K(KindPlate))
                foreach (var p in d.rendszam)
                {
                    if (p == null || (p.S != null && p.S.Length > 32)) return "plate";
                    if (p.S != null) foreach (char ch in p.S) if (ch < ' ') return "plate";
                }
            if (K(KindBusDoor))
                foreach (var b in d.busdoorscript)
                    if (b == null || (b.Bopened != null && b.Bopened.Length > 64) || (b.Bsigned != null && b.Bsigned.Length > 64)) return "bus doors";
            return null;
        }

        /// Server: a building usable change — from a player at that building, within the rate, with sane values.
        private static bool CheckPoiUsable(int from, PoiKey k, PoiState st)
        {
            if (!EditValidation || from == 0) return true;
            if (!TakeToken(_poiBuckets, from, PoiRatePerS, PoiBurst)) { Refuse(ref PoiRefusedRate, "poi rate", $"building usable from player {from}: over {PoiRatePerS:F0} a second"); return false; }
            if (k.Gen < 0 || k.Gen > 255 || k.U < 0 || k.U > 4096 || !Finite(k.X) || !Finite(k.Y) || !Finite(k.Z) ||
                !Sane(st.XRot) || !Sane(st.YRot) || !Sane(st.Slide) || !Sane(st.Tuned) || st.Turn < -1 || st.Turn > 1024)
            { Refuse(ref PoiRefusedValues, "poi value", $"building usable from player {from}: values out of range (gen {k.Gen} u {k.U})"); return false; }
            if (Mp.Server == null || !Mp.Server.PeerPos(from, out var p)) { Refuse(ref PoiRefusedReach, "poi reach", $"building usable from player {from}: their position isn't known"); return false; }
            double dx = p.x - k.X, dz = p.z - k.Z;
            if (dx * dx + dz * dz > (double)PoiReachM * PoiReachM)
            {
                Refuse(ref PoiRefusedReach, "poi reach", $"building usable from player {from}: {Math.Sqrt(dx * dx + dz * dz):F0} m from the building (gen {k.Gen} at {k.X:F0},{k.Z:F0})");
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- test hooks (bridge: mp forgeedit / forgepoi)
        /// `mp forgeedit <net> <what> [n]`: n edits of our copy of someone else's object, made up the way a cheating
        /// player could: all (KindAll), tank (99,999 L), nan (a usable's rotation NaN), colourstate (a colour edit that
        /// repairs part 0), ok (the copy's own usable state: passes), rate (ok, n at once).
        public static string TestForgeEdit(uint net, string what, int n)
        {
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy || !Resolve(e)) return "{\"error\":\"no copy of someone else's object with that net here\"}";
            int idx = -1;
            for (int i = 0; i < e.Items.Count && idx < 0; i++)
            {
                var it = e.Items[i];
                if (it == null) continue;
                if (what == "tank" ? it.tanks != null && it.tanks.Count > 0 : what == "colourstate" ? it.partconditions != null && it.partconditions.Count > 0 : it.usables != null && it.usables.Count > 0) idx = i;
            }
            if (idx < 0) return "{\"error\":\"no item of it has what that needs\"}";
            var map = CanonMap(e.Items);
            var d = StateOnly(ItemSnapshot.Capture(e.Items[idx]));
            d.car = new List<save_carscript>();
            int kinds = KindUsable;
            switch (what)
            {
                case "all": kinds = KindAll; break;
                case "tank": kinds = KindTank; if (d.tanks.Count > 0) { d.tanks[0].amount = new[] { 99999f }; d.tanks[0].type = new[] { 0 }; } break;
                case "nan": if (d.usable.Count > 0) d.usable[0].xRot = float.NaN; break;
                case "colourstate": kinds = KindColor; if (d.partconditions.Count > 0) d.partconditions[0].state = d.partconditions[0].state == 0 ? 1 : 0; break;
                case "ok": case "rate": break;
                default: return "{\"error\":\"all | tank | nan | colourstate | ok | rate\"}";
            }
            ItemSnapshot.Remap(d, map);
            byte[] rec = RecordCodec.Encode(d);
            for (int k = 0; k < n; k++)
            {
                W.Reset(); W.U8(Edit); W.U32(e.NetId); W.F64(SessionLog.St); W.VarU32((uint)idx); W.VarU32((uint)kinds); W.VarU32((uint)rec.Length); W.Bytes(rec, 0, rec.Length);
                ToServer(W, true);
            }
            return "{\"sent\":" + n + ",\"item\":" + idx + ",\"kinds\":" + kinds + ",\"bytes\":" + rec.Length + "}";
        }

        /// `mp forgepoi gen x y z u slide [n]`: a building usable change sent as is (the building needn't be here).
        public static string TestForgePoi(string[] a)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var k = new PoiKey { Gen = int.Parse(a[1]), X = double.Parse(a[2], ci), Y = double.Parse(a[3], ci), Z = double.Parse(a[4], ci), U = int.Parse(a[5]) };
            var u = PoiUsableOf(k);
            var st = u != null ? new PoiState { XRot = u.xRot, YRot = u.yRot, Slide = u.slideValue, Turn = u.currentTurnState, Tuned = u.tunedFloat } : new PoiState();
            st.Slide = float.Parse(a[6], ci);
            int n = a.Length > 7 ? int.Parse(a[7]) : 1;
            for (int i = 0; i < n; i++) { W.Reset(); W.U8(PoiUsable); WritePoi(W, k, st); ToServer(W, true); }
            return "{\"sent\":" + n + ",\"spawnedHere\":" + (u != null ? "true" : "false") + "}";
        }

        public static string EditCheckJson() =>
            "{\"validation\":" + (EditValidation ? "true" : "false") + ",\"refused\":{\"outOfReach\":" + EditsOutOfReach + ",\"rate\":" + EditRefusedRate + ",\"kinds\":" + EditRefusedKinds +
            ",\"values\":" + EditRefusedValues + ",\"poiReach\":" + PoiRefusedReach + ",\"poiRate\":" + PoiRefusedRate + ",\"poiValues\":" + PoiRefusedValues + "},\"last\":" + Json.Str(EditRefusedLast) +
            ",\"anomalies\":" + Anomalies + "}";
    }
}
