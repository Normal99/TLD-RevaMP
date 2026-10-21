using System;
using System.Globalization;
using System.Text;
using TLDRevamp.Net;

namespace TLDRevamp
{
    // Numeric observations only. No scene labels, save ids or user text enter this wire format.
    internal sealed class ObjectPanelState
    {
        internal const int UsableLimit = 64, SlotLimit = 64, EventLimit = 20;
        [Flags] internal enum Bits : uint { Found = 1, Live = 2, Proxy = 4, Held = 8, Stored = 16, Locked = 32, Attached = 64, Car = 128, Engine = 256, Ignition = 512, RadioOn = 1024, RadioAm = 2048, EventsNotShared = 4096 }
        internal uint Net, Epoch, Parent, LockedTo;
        internal int Owner = -1, Part = -1, UsableCount, SlotCount, EventCount, Truncated;
        internal Bits Flags;
        internal double X, Y, Z;
        internal float Qx, Qy, Qz, Qw = 1, Vx, Vy, Vz, Fuel = float.NaN, Rpm = float.NaN, Key = float.NaN, Radio = float.NaN;
        internal readonly Usable[] Usables = new Usable[UsableLimit];
        internal readonly Slot[] Slots = new Slot[SlotLimit];
        internal readonly Recent[] Events = new Recent[EventLimit];
        internal struct Usable { internal int Item, Index; internal bool Bus; internal float X, Y, Slide, Turn, Tuned; }
        internal struct Slot { internal int Item, Index, Member; internal uint Content; internal bool Attached, Wheel, DrivenWheel; }
        internal struct Recent { internal float Age, Value, Dx, Dy, Dz; internal ushort Kind, Reason; internal uint Net, Other, Epoch; internal int Item, Slot, From, To, Count; }
        internal bool Has(Bits b) => (Flags & b) != 0;
        internal static uint Select(uint crosshair, uint car, uint held) => crosshair != 0 ? crosshair : car != 0 ? car : held;
        internal static int Severity(double metres, double degrees) => metres >= 2 || degrees >= 15 ? 2 : metres >= .2 || degrees >= 3 ? 1 : 0;
        internal double Distance(ObjectPanelState mine) => Math.Sqrt((X - mine.X) * (X - mine.X) + (Y - mine.Y) * (Y - mine.Y) + (Z - mine.Z) * (Z - mine.Z));
        internal double Angle(ObjectPanelState mine)
        {
            double norm = Math.Sqrt((Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw) * (mine.Qx * mine.Qx + mine.Qy * mine.Qy + mine.Qz * mine.Qz + mine.Qw * mine.Qw));
            return norm > 0 ? Math.Acos(Math.Min(1, Math.Abs((Qx * mine.Qx + Qy * mine.Qy + Qz * mine.Qz + Qw * mine.Qw) / norm))) * 360 / Math.PI : double.NaN;
        }
        internal void Write(NetWriter w)
        {
            w.U32(Net); w.U32(Epoch); w.U32((uint)Flags); w.I32(Owner); w.U32(Parent); w.I32(Part); w.U32(LockedTo); w.I32(Truncated);
            w.F64(X); w.F64(Y); w.F64(Z);
            w.F32(Qx); w.F32(Qy); w.F32(Qz); w.F32(Qw); w.F32(Vx); w.F32(Vy); w.F32(Vz);
            w.F32(Fuel); w.F32(Rpm); w.F32(Key); w.F32(Radio);
            w.U8((byte)UsableCount);
            for (int i = 0; i < UsableCount; i++) { var u = Usables[i]; w.I32(u.Item); w.I32(u.Index); w.Bool(u.Bus); w.F32(u.X); w.F32(u.Y); w.F32(u.Slide); w.F32(u.Turn); w.F32(u.Tuned); }
            w.U8((byte)SlotCount);
            for (int i = 0; i < SlotCount; i++) { var s = Slots[i]; w.I32(s.Item); w.I32(s.Index); w.U32(s.Content); w.I32(s.Member); w.Bool(s.Attached); w.U8((byte)((s.DrivenWheel ? 1 : 0) | (s.Wheel ? 2 : 0))); }
            w.U8((byte)EventCount);
            for (int i = 0; i < EventCount; i++)
            {
                var e = Events[i]; w.F32(e.Age); w.U16(e.Kind); w.U16(e.Reason); w.U32(e.Net); w.U32(e.Other); w.U32(e.Epoch);
                w.I32(e.Item); w.I32(e.Slot); w.I32(e.From); w.I32(e.To); w.I32(e.Count); w.F32(e.Value); w.F32(e.Dx); w.F32(e.Dy); w.F32(e.Dz);
            }
        }
        internal static ObjectPanelState Read(NetReader r)
        {
            var s = new ObjectPanelState { Net = r.U32(), Epoch = r.U32(), Flags = (Bits)r.U32(), Owner = r.I32(), Parent = r.U32(), Part = r.I32(), LockedTo = r.U32(), Truncated = r.I32(),
                X = r.F64(), Y = r.F64(), Z = r.F64(), Qx = r.F32(), Qy = r.F32(), Qz = r.F32(), Qw = r.F32(), Vx = r.F32(), Vy = r.F32(), Vz = r.F32(), Fuel = r.F32(), Rpm = r.F32(), Key = r.F32(), Radio = r.F32() };
            if (s.Net == 0 || ((uint)s.Flags & ~8191u) != 0 || s.Truncated < 0 || !Finite(s.X) || !Finite(s.Y) || !Finite(s.Z) ||
                !Finite(s.Qx) || !Finite(s.Qy) || !Finite(s.Qz) || !Finite(s.Qw) || !Finite(s.Vx) || !Finite(s.Vy) || !Finite(s.Vz)) r.Bad = true;
            s.UsableCount = r.U8(); if (s.UsableCount > UsableLimit) return null;
            for (int i = 0; i < s.UsableCount; i++) s.Usables[i] = new Usable { Item = r.I32(), Index = r.I32(), Bus = r.Bool(), X = r.F32(), Y = r.F32(), Slide = r.F32(), Turn = r.F32(), Tuned = r.F32() };
            s.SlotCount = r.U8(); if (s.SlotCount > SlotLimit) return null;
            for (int i = 0; i < s.SlotCount; i++)
            {
                var slot = new Slot { Item = r.I32(), Index = r.I32(), Content = r.U32(), Member = r.I32(), Attached = r.Bool() };
                byte flags = r.U8(); if (flags > 3) r.Bad = true;
                slot.DrivenWheel = (flags & 1) != 0; slot.Wheel = (flags & 2) != 0; s.Slots[i] = slot;
            }
            s.EventCount = r.U8(); if (s.EventCount > EventLimit) return null;
            for (int i = 0; i < s.EventCount; i++)
            {
                var e = new Recent { Age = r.F32(), Kind = r.U16(), Reason = r.U16(), Net = r.U32(), Other = r.U32(), Epoch = r.U32(), Item = r.I32(), Slot = r.I32(), From = r.I32(), To = r.I32(), Count = r.I32(), Value = r.F32(), Dx = r.F32(), Dy = r.F32(), Dz = r.F32() };
                if (!Finite(e.Age) || e.Age < 0 || e.Age > 35 || e.Kind >= ObjectPanelEvents.Kinds.Length || e.Reason >= ObjectPanelEvents.Reasons.Length || e.Net != s.Net && e.Other != s.Net) r.Bad = true;
                s.Events[i] = e;
            }
            return r.Bad || r.Remaining != 0 ? null : s;
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        internal string Json(ObjectPanelState mine, double age)
        {
            var b = new StringBuilder("{\"net\":").Append(Net).Append(",\"epoch\":").Append(Epoch).Append(",\"owner\":").Append(Owner).Append(",\"flags\":").Append((uint)Flags)
                .Append(",\"found\":").Append(Has(Bits.Found) ? "true" : "false").Append(",\"proxy\":").Append(Has(Bits.Proxy) ? "true" : "false")
                .Append(",\"held\":").Append(Has(Bits.Held) ? "true" : "false").Append(",\"stored\":").Append(Has(Bits.Stored) ? "true" : "false")
                .Append(",\"physlock\":").Append(Has(Bits.Locked) ? "true" : "false").Append(",\"lockedTo\":").Append(LockedTo)
                .Append(",\"parent\":").Append(Parent).Append(",\"part\":").Append(Part).Append(",\"truncated\":").Append(Truncated)
                .Append(",\"live\":").Append(Has(Bits.Live) ? "true" : "false").Append(",\"attached\":").Append(Has(Bits.Attached) ? "true" : "false")
                .Append(",\"car\":").Append(Has(Bits.Car) ? "true" : "false").Append(",\"engine\":").Append(Has(Bits.Engine) ? "true" : "false").Append(",\"ignition\":").Append(Has(Bits.Ignition) ? "true" : "false")
                .Append(",\"radioOn\":").Append(Has(Bits.RadioOn) ? "true" : "false").Append(",\"radioAm\":").Append(Has(Bits.RadioAm) ? "true" : "false")
                .Append(",\"pos\":[").Append(TLDRevamp.Json.Num(X)).Append(',').Append(TLDRevamp.Json.Num(Y)).Append(',').Append(TLDRevamp.Json.Num(Z))
                .Append("],\"rotation\":[").Append(TLDRevamp.Json.Num(Qx)).Append(',').Append(TLDRevamp.Json.Num(Qy)).Append(',').Append(TLDRevamp.Json.Num(Qz)).Append(',').Append(TLDRevamp.Json.Num(Qw))
                .Append("],\"velocity\":[").Append(TLDRevamp.Json.Num(Vx)).Append(',').Append(TLDRevamp.Json.Num(Vy)).Append(',').Append(TLDRevamp.Json.Num(Vz))
                .Append("],\"fuel\":").Append(TLDRevamp.Json.Num(Fuel)).Append(",\"rpm\":").Append(TLDRevamp.Json.Num(Rpm)).Append(",\"ignitionKey\":").Append(TLDRevamp.Json.Num(Key)).Append(",\"radio\":").Append(TLDRevamp.Json.Num(Radio))
                .Append(",\"distanceM\":").Append(TLDRevamp.Json.Num(mine != null && Has(Bits.Found) && mine.Has(Bits.Found) ? Distance(mine) : double.NaN))
                .Append(",\"angleDeg\":").Append(TLDRevamp.Json.Num(mine != null && Has(Bits.Found) && mine.Has(Bits.Found) ? Angle(mine) : double.NaN)).Append(",\"usables\":[");
            for (int i = 0; i < UsableCount; i++) { var u = Usables[i]; if (i > 0) b.Append(','); b.Append("{\"item\":").Append(u.Item).Append(",\"index\":").Append(u.Index).Append(",\"bus\":").Append(u.Bus ? "true" : "false").Append(",\"values\":[").Append(TLDRevamp.Json.Num(u.X)).Append(',').Append(TLDRevamp.Json.Num(u.Y)).Append(',').Append(TLDRevamp.Json.Num(u.Slide)).Append(',').Append(TLDRevamp.Json.Num(u.Turn)).Append(',').Append(TLDRevamp.Json.Num(u.Tuned)).Append("]}"); }
            b.Append("],\"slots\":[");
            for (int i = 0; i < SlotCount; i++) { var s = Slots[i]; if (i > 0) b.Append(','); b.Append("{\"item\":").Append(s.Item).Append(",\"index\":").Append(s.Index).Append(",\"content\":").Append(s.Content).Append(",\"member\":").Append(s.Member).Append(",\"attached\":").Append(s.Attached ? "true" : "false").Append(",\"wheel\":").Append(s.Wheel ? "true" : "false").Append(",\"drivenWheel\":").Append(s.DrivenWheel ? "true" : "false").Append('}'); }
            b.Append("],\"eventsShared\":").Append(Has(Bits.EventsNotShared) ? "false" : "true").Append(",\"events\":[");
            for (int i = 0; i < EventCount; i++) { if (i > 0) b.Append(','); b.Append(EventJson(Events[i], age)); }
            return b.Append("]}").ToString();
        }
        internal static string EventJson(Recent e, double age) => "{\"ageS\":" + TLDRevamp.Json.Num(e.Age + age) + ",\"kind\":" + TLDRevamp.Json.Str(ObjectPanelEvents.Kinds[e.Kind]) + ",\"reason\":" + TLDRevamp.Json.Str(ObjectPanelEvents.Reasons[e.Reason]) + ",\"net\":" + e.Net + ",\"other\":" + e.Other + ",\"epoch\":" + e.Epoch + ",\"item\":" + e.Item + ",\"slot\":" + e.Slot + ",\"from\":" + e.From + ",\"to\":" + e.To + ",\"count\":" + e.Count + ",\"value\":" + TLDRevamp.Json.Num(e.Value) + ",\"delta\":[" + TLDRevamp.Json.Num(e.Dx) + "," + TLDRevamp.Json.Num(e.Dy) + "," + TLDRevamp.Json.Num(e.Dz) + "]}";
        internal string Text(ObjectPanelState mine)
        {
            if (!Has(Bits.Found)) return "Object absent";
            var b = new StringBuilder("owner ").Append(Owner).Append("  epoch ").Append(Epoch).Append("  proxy ").Append(Has(Bits.Proxy));
            b.Append("\npos ").Append(N(X)).Append(' ').Append(N(Y)).Append(' ').Append(N(Z));
            b.Append("\nquat ").Append(N(Qx)).Append(' ').Append(N(Qy)).Append(' ').Append(N(Qz)).Append(' ').Append(N(Qw));
            if (mine != null && mine.Has(Bits.Found)) b.Append("\ndelta ").Append(N(Distance(mine))).Append(" m  ").Append(N(Angle(mine))).Append(" deg");
            b.Append("\nvel ").Append(N(Vx)).Append(' ').Append(N(Vy)).Append(' ').Append(N(Vz));
            b.Append("\nheld ").Append(Has(Bits.Held)).Append("  stored ").Append(Has(Bits.Stored)).Append("  live ").Append(Has(Bits.Live));
            b.Append("\nphyslock ").Append(Has(Bits.Locked)).Append(" -> ").Append(LockedTo).Append("\nattached ").Append(Has(Bits.Attached)).Append(" -> ").Append(Parent).Append(" part ").Append(Part);
            if (Has(Bits.Car)) b.Append("\nengine ").Append(Has(Bits.Engine)).Append(" rpm ").Append(N(Rpm)).Append("\nignition ").Append(Has(Bits.Ignition)).Append(" key ").Append(N(Key)).Append(" fuel ").Append(N(Fuel)).Append("\nradio ").Append(N(Radio)).Append(" on ").Append(Has(Bits.RadioOn)).Append(" AM ").Append(Has(Bits.RadioAm));
            for (int i = 0; i < UsableCount; i++) { var u = Usables[i]; b.Append(u.Bus ? "\nbus " : "\nuse ").Append(u.Item).Append('/').Append(u.Index).Append(' ').Append(N(u.X)).Append(' ').Append(N(u.Y)).Append(' ').Append(N(u.Slide)).Append(' ').Append(N(u.Turn)).Append(' ').Append(N(u.Tuned)); }
            for (int i = 0; i < SlotCount; i++) { var s = Slots[i]; b.Append(s.Wheel || s.DrivenWheel ? "\nwheel " : "\nslot ").Append(s.Item).Append('/').Append(s.Index).Append(" -> ").Append(s.Content).Append('/').Append(s.Member).Append(" attached ").Append(s.Attached); }
            if (Truncated > 0) b.Append("\ntruncated ").Append(Truncated);
            return b.ToString();
        }
        internal string Differences(ObjectPanelState mine)
        {
            if (!Has(Bits.Found)) return "Object absent";
            if (mine == null || !mine.Has(Bits.Found)) return "Local object absent";
            var b = new StringBuilder();
            void Field(string key, string local, string remote) { b.Append(key).Append(' ').Append(local).Append(" -> ").Append(remote).Append('\n'); }
            void Number(string key, double local, double remote, double tolerance = .01)
            {
                if (double.IsNaN(local) != double.IsNaN(remote) || Math.Abs(local - remote) > tolerance) Field(key, N(local), N(remote));
            }
            if (Owner != mine.Owner) Field("owner", mine.Owner.ToString(), Owner.ToString());
            if (Epoch != mine.Epoch) Field("epoch", mine.Epoch.ToString(), Epoch.ToString());
            double distance = Distance(mine), angle = Angle(mine);
            if (distance >= .01 || angle >= .1) b.Append("delta ").Append(N(distance)).Append(" m  ").Append(N(angle)).Append(" deg\n");
            Number("velocity x", mine.Vx, Vx); Number("velocity y", mine.Vy, Vy); Number("velocity z", mine.Vz, Vz);
            var mask = ~(Bits.Proxy | Bits.EventsNotShared);
            if ((Flags & mask) != (mine.Flags & mask)) Field("state", (mine.Flags & mask).ToString(), (Flags & mask).ToString());
            if (Parent != mine.Parent || Part != mine.Part) Field("attachment", mine.Parent + "/" + mine.Part, Parent + "/" + Part);
            if (LockedTo != mine.LockedTo) Field("locked to", mine.LockedTo.ToString(), LockedTo.ToString());
            Number("fuel", mine.Fuel, Fuel); Number("rpm", mine.Rpm, Rpm, 1); Number("key", mine.Key, Key); Number("radio", mine.Radio, Radio);
            if (UsableCount != mine.UsableCount) Field("usable count", mine.UsableCount.ToString(), UsableCount.ToString());
            for (int i = 0; i < UsableCount; i++)
            {
                var u = Usables[i];
                if (i >= mine.UsableCount) { b.Append("extra usable ").Append(u.Item).Append('/').Append(u.Index).Append('\n'); continue; }
                var m = mine.Usables[i];
                if (u.Item != m.Item || u.Index != m.Index || u.Bus != m.Bus || Different(u.X, m.X) || Different(u.Y, m.Y) || Different(u.Slide, m.Slide) || Different(u.Turn, m.Turn) || Different(u.Tuned, m.Tuned))
                    b.Append("usable ").Append(u.Item).Append('/').Append(u.Index).Append(": ").Append(N(m.X)).Append('/').Append(N(m.Y)).Append('/').Append(N(m.Slide)).Append('/').Append(N(m.Turn)).Append('/').Append(N(m.Tuned))
                        .Append(" -> ").Append(N(u.X)).Append('/').Append(N(u.Y)).Append('/').Append(N(u.Slide)).Append('/').Append(N(u.Turn)).Append('/').Append(N(u.Tuned)).Append('\n');
            }
            if (SlotCount != mine.SlotCount) Field("slot count", mine.SlotCount.ToString(), SlotCount.ToString());
            for (int i = 0; i < SlotCount; i++)
            {
                var s = Slots[i];
                if (i >= mine.SlotCount) { b.Append("extra slot ").Append(s.Item).Append('/').Append(s.Index).Append('\n'); continue; }
                var m = mine.Slots[i];
                if (s.Item != m.Item || s.Index != m.Index || s.Content != m.Content || s.Member != m.Member || s.Attached != m.Attached || s.Wheel != m.Wheel || s.DrivenWheel != m.DrivenWheel)
                    b.Append("slot ").Append(s.Item).Append('/').Append(s.Index).Append(": ").Append(m.Content).Append('/').Append(m.Member).Append(" attached ").Append(m.Attached).Append(" wheel ").Append(m.Wheel).Append('/').Append(m.DrivenWheel)
                        .Append(" -> ").Append(s.Content).Append('/').Append(s.Member).Append(" attached ").Append(s.Attached).Append(" wheel ").Append(s.Wheel).Append('/').Append(s.DrivenWheel).Append('\n');
            }
            if (Truncated > 0 || mine.Truncated > 0) b.Append("truncated ").Append(mine.Truncated).Append(" / ").Append(Truncated).Append('\n');
            return b.Length == 0 ? "No differences" : b.ToString().TrimEnd();
        }
        private static bool Different(float a, float b) => float.IsNaN(a) != float.IsNaN(b) || Math.Abs(a - b) > .01f;
        internal static string N(double n) => double.IsNaN(n) || double.IsInfinity(n) ? "?" : n.ToString("F2", CultureInfo.InvariantCulture);
    }
}
