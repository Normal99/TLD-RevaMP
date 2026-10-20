using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using TLDRevamp.Net;
using UnityEngine;

static class ItemTrafficTest
{
    static int checks;
    static readonly List<ItemTraffic.Row> Rows = new List<ItemTraffic.Row>();
    static void Check(string name, bool ok) { checks++; if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;
    static void Fresh() { Time.realtimeSinceStartup = 0; Mp.Server = null; NetMeter.Reset(); }
    static void Sample(float t = 1f) { Time.realtimeSinceStartup = t; ItemTraffic.Current(Rows); }
    static ItemTraffic.Row Row(uint net, bool mod = false)
    {
        foreach (var r in Rows) if (r.Net == net && r.Mod == mod) return r;
        return default;
    }
    static NetWriter Head(byte type, uint id) { var w = new NetWriter(); w.U8(type); w.U32(id); return w; }
    static void Z(NetWriter w, int n) { for (int i = 0; i < n; i++) w.U8(0); }
    static void Send(NetWriter w, bool server = false) => NetMeter.Sent(w.Buf, w.Len, 1, NetMeter.Now(), server);
    static void Receive(NetWriter w, bool server = false) => NetMeter.Received(w.Buf[0], w.Buf, 0, w.Len, w.Len, 1, NetMeter.Now(), server);
    static NetWriter State(uint id, bool extras = false)
    {
        var w = new NetWriter();
        Entities.WriteStateHead(w, id, 300, 7, 1.5, new Vector3d(1, 2, 3), new Quaternion(0, 0, 0, 1), new Vector3(4, 5, 6), true,
                                carriedBy: extras ? 999u : 0u);
        w.U8(extras ? (byte)0x82 : (byte)0); if (extras) Z(w, 12);   // wheel travel/steer/rpm and torques
        w.U8(extras ? (byte)7 : (byte)0); if (extras) w.U16(2500);
        w.U8(extras ? (byte)1 : (byte)0); if (extras) Z(w, 2);
        w.U8(extras ? (byte)2 : (byte)0); if (extras) Z(w, 38);
        return w;
    }
    static NetWriter Live(uint id)
    {
        // Entities.WriteLiveUsable / ReadLiveUsable: u32 ids, two indices, seq, f64 clock and quantized values.
        var w = Head(Protocol.LiveUsable, id); w.U32(1); w.VarU32(140); w.VarU32(2); w.U16(3); w.F64(4);
        w.U16(5); w.U16(6); w.U16(7); w.VarI32(-150); w.U16(8); return w;
    }
    static NetWriter Edit(uint id, int n = 9)
    {
        // Entities.EditTick / ApplyEdit: net, session clock, item, kind mask, length and record.
        var w = Head(Entities.Edit, id); w.F64(7); w.VarU32(130); w.VarU32(256); w.VarU32((uint)n); Z(w, n); return w;
    }
    static NetWriter ModPose(uint id)
    {
        var w = Head(ModObjects.ModPose, id); w.U32(2); Protocol.WriteStamp(w, 3); Z(w, 24 + 16 + 12); return w;
    }
    static NetWriter Batch(params NetWriter[] messages)
    {
        var w = new NetWriter(); w.U8(Protocol.Batch);
        foreach (var m in messages) { w.VarU32((uint)m.Len); w.Bytes(m.Buf, 0, m.Len); } return w;
    }
    static void Main()
    {
        Fresh(); var state = State(300); var live = Live(12); var edit = Edit(300); var pose = ModPose(300);
        Send(state); Receive(live); Receive(edit); Send(pose); ItemTraffic.Current(Rows);
        Check("wait for a full second", Rows.Count == 0);
        Sample(); Check("State uses its varint net, Edit its u32 net", Near(Row(300).UpBps, state.Len) && Near(Row(300).DownBps, edit.Len));
        Check("LiveUsable receive bytes and messages", Near(Row(12).DownBps, live.Len) && Near(Row(12).DownMsgs, 1));
        Check("ModPose has its own id namespace", Rows.Count == 3 && Near(Row(300, true).UpBps, pose.Len));
        Check("busiest first", Rows[0].Net == 300 && !Rows[0].Mod);
        ItemTraffic.Current(Rows); Check("same-second query keeps snapshot", Near(Row(300).UpBps, state.Len));
        Sample(2.5f); Check("idle next interval is empty", Rows.Count == 0);
        Fresh(); var compact = new NetWriter(); CompactPoseCodec.Write(compact, 595, 1, 4, 2, 10, 20, 30, 90, true);
        Send(compact); Receive(compact); Sample();
        Check("CompactState bytes and message rates", Near(Row(595).UpBps, 25) && Near(Row(595).DownBps, 25) && Near(Row(595).UpMsgs, 1));
        Fresh(); var rabbit = new NetWriter(); RabbitStateCodec.Write(rabbit, new RabbitStateCodec.State { Net = 595, Epoch = 2, Run = 90, X = 50 });
        Send(rabbit); Receive(rabbit); Sample();
        Check("protocol 39 RabbitState epoch/global target attribution", Near(Row(595).UpBps, 24) && Near(Row(595).DownBps, 24) && Near(Row(595).UpMsgs, 1));
        Fresh(); ItemTraffic.Packet(rabbit.Buf, 0, rabbit.Len - 1, rabbit.Len - 1, true, false); Sample();
        Check("truncated protocol 39 RabbitState skipped", Rows.Count == 0);
        Fresh(); var detailed = State(500, true); Send(detailed); Sample();
        Check("State wheels, torques, engine, signals, carried pose and limbs", Near(Row(500).UpBps, detailed.Len) && Rows.Count == 1);
        Fresh(); Mp.Server = new MpServer(); var batch = Batch(state, live, Edit(7, 200), pose); Send(batch, true); Sample();
        Check("Batch attributes each sub-message and one-byte prefix", Near(Row(300).UpBps, state.Len + 1) && Near(Row(12).UpBps, live.Len + 1));
        Check("Batch two-byte prefix belongs to its record", Near(Row(7).UpBps, Edit(7, 200).Len + 2));
        Check("Batch excludes only its type byte", Near(Row(300).UpBps + Row(12).UpBps + Row(7).UpBps + Row(300, true).UpBps, batch.Len - 1));
        Fresh(); var br = new NetReader(); br.Load(batch.Len); Array.Copy(batch.Buf, br.Buf, batch.Len); br.U8();
        NetMeter.Received(Protocol.Batch, br.Buf, 0, br.End, 1, 1, NetMeter.Now(), false);
        while (br.Remaining > 0)
        {
            int at = br.Pos, n = (int)br.VarU32(), off = br.Pos; br.Pos += n;
            NetMeter.Received(br.Buf[off], br.Buf, off, n, br.Pos - at, 1, NetMeter.Now(), false);
        }
        Sample(); Check("receive batch uses offsets without copies or double counts", Near(Row(300).DownBps, state.Len + 1) && Near(Row(7).DownBps, Edit(7, 200).Len + 2));
        foreach (bool server in new[] { false, true }) foreach (bool up in new[] { false, true })
        {
            Fresh(); bool fromServer = server == up;
            var me = Head(ModObjects.ModEdit, 30); if (fromServer) me.VarU32(300); me.VarU32(3); Z(me, 3);
            var worn = Head(Entities.Worn, 31); if (fromServer) worn.VarU32(300); else worn.U32(300); worn.U8(4); Z(worn, 19);
            ItemTraffic.Packet(me.Buf, 0, me.Len, me.Len, up, server); ItemTraffic.Packet(worn.Buf, 0, worn.Len, worn.Len, up, server); Sample();
            Check("direction layouts server=" + server + " up=" + up, Near(up ? Row(30, true).UpBps : Row(30, true).DownBps, me.Len) && Near(up ? Row(31).UpBps : Row(31).DownBps, worn.Len));
        }
        Fresh(); Mp.Server = new MpServer();
        var serverWorn = Head(Entities.Worn, 31); serverWorn.VarU32(300); serverWorn.U8(4); Z(serverWorn, 19);
        Send(serverWorn, true); Receive(serverWorn, false); Sample();
        Check("server Worn send and host bot receive use transport roles", Near(Row(31).UpBps, serverWorn.Len) && Near(Row(31).DownBps, serverWorn.Len));
        Fresh(); Mp.Server = new MpServer(); var wornBatch = Batch(serverWorn); Send(wornBatch, true);
        NetMeter.Received(Protocol.Batch, wornBatch.Buf, 0, wornBatch.Len, 1, 1, NetMeter.Now(), false);
        NetMeter.Received(Entities.Worn, wornBatch.Buf, 2, serverWorn.Len, serverWorn.Len + 1, 1, NetMeter.Now(), false); Sample();
        Check("batched Worn keeps server send and bot receive roles", Near(Row(31).UpBps, serverWorn.Len + 1) && Near(Row(31).DownBps, serverWorn.Len + 1));
        Fresh(); var clientWorn = Head(Entities.Worn, 32); clientWorn.U32(300); clientWorn.U8(4); Z(clientWorn, 19);
        Send(clientWorn, false); Receive(clientWorn, true); Sample();
        Check("client Worn send and server receive ignore global role", Near(Row(32).UpBps, clientWorn.Len) && Near(Row(32).DownBps, clientWorn.Len));
        Fresh(); var resync = Head(Entities.Resync, 55); resync.U32(8); resync.F64(1); resync.U16(2);
        resync.VarU32(0); resync.VarU32(2); Z(resync, 2); resync.VarU32(7); resync.VarU32(6); Z(resync, 6);
        Send(resync); Sample(); Check("Resync item records share one group net id", Rows.Count == 1 && Near(Row(55).UpBps, resync.Len) && Near(Row(55).UpMsgs, 1));
        Fresh(); var attach = Head(Entities.AttachSync, 60); attach.U8(1); attach.U8(2); attach.VarU32(300); attach.U32(61); attach.VarU32(0); attach.VarU32(1); Z(attach, 72); attach.U8(1); attach.VarI32(-20); attach.VarI32(3); attach.VarI32(4); Z(attach, 7);
        Send(attach); Sample(); Check("Attach splits relationship bytes once", Near(Row(60).UpBps + Row(61).UpBps, attach.Len) && Math.Abs(Row(60).UpBps - Row(61).UpBps) <= 1);
        Fresh(); var shot = new NetWriter(); shot.U8(Entities.ShotFx); shot.U8(4); shot.U32(80); shot.VarU32(0); Z(shot, 37); shot.U8(2); int header = shot.Len;
        shot.U8(1); shot.U8(0); shot.U8(255); shot.U32(81); shot.Str("body"); Z(shot, 24); 
        shot.U8(4); shot.U8(0); shot.U8(255); shot.U32(81); shot.VarU32(0); Z(shot, 24);
        Send(shot); Sample();
        Check("ShotFx shares header and charges actual hit records", Near(Row(80).UpBps, header / 3 + (header % 3 > 0 ? 1 : 0)) && Near(Row(80).UpBps + Row(81).UpBps, shot.Len));
        Check("several records for one object are one message", Near(Row(81).UpMsgs, 1));
        Fresh(); var save = new NetWriter(); save.U8(PlayerSave.Record); save.Str("{}"); Z(save, 13); save.VarU32(2); int saveHeader = save.Len;
        save.U32(1000); save.U32(101); save.VarU32(2); Z(save, 2); int rec1 = save.Len - saveHeader;
        save.U32(1001); save.U32(102); save.VarU32(9); Z(save, 9); save.VarU32(1); save.U32(1000);
        Send(save); Sample();
        Check("inventory splits record bytes and shared header", Near(Row(101).UpBps, rec1 + (saveHeader + 5) / 2 + (saveHeader + 5) % 2) && Near(Row(101).UpBps + Row(102).UpBps, save.Len));
        Fresh(); var damage = new NetWriter(); damage.U8(PlayerCombat.PlayerDamage); damage.VarU32(5); Z(damage, 16); damage.U8(HitCheck.KCreature); Z(damage, 12); damage.U32(110); damage.VarU32(1);
        Send(damage); Sample(); Check("creature damage attributes its source object", Near(Row(110).UpBps, damage.Len));
        Fresh(); var bad = new List<NetWriter> { state, live, edit, pose, detailed, resync, attach, shot, compact };
        foreach (var w in bad) for (int n = 0; n < w.Len; n++)
        {
            // State signals/limbs are optional to support bots; a boundary before those tails is a valid state.
            if (w == state && (n == w.Len - 1 || n == w.Len - 2)) continue;
            if (w == detailed && (n == w.Len - 39 || n == w.Len - 42)) continue;
            ItemTraffic.Packet(w.Buf, 0, n, n, true, false);
        }
        Sample(); Check("truncated messages never throw or partially attribute", Rows.Count == 0);
        Fresh(); ItemTraffic.Packet(null, 0, 1, 1, true, false); ItemTraffic.Packet(state.Buf, -1, 4, 4, true, false);
        ItemTraffic.Packet(state.Buf, int.MaxValue, 3, 3, true, false);
        var huge = Head(Entities.Edit, 90); huge.F64(0); huge.VarU32(0); huge.VarU32(1); huge.VarU32(uint.MaxValue); Send(huge);
        var overflow = new byte[] { Entities.State, 255, 255, 255, 255, 255, 255 }; ItemTraffic.Packet(overflow, 0, overflow.Length, overflow.Length, false, false);
        Send(Head(Entities.Share, 70)); Send(Head(Entities.PoiUsable, 71)); Send(Head(ModObjects.ModShare, 72)); Send(Head(Protocol.State, 73)); Sample();
        Check("invalid ranges, lengths, varints and non-object traffic skipped", Rows.Count == 0);
        Fresh(); Send(live); Sample(2); Check("rates use elapsed time >=1 s", Near(Row(12).UpBps, live.Len / 2f) && Near(Row(12).UpMsgs, 0.5f));
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Entities.Objects[12] = new GameObject { name = "a\"b\n" };
        using (var j = JsonDocument.Parse(ItemTraffic.ListJson(1))) Check("JSON names escaped and rates invariant", j.RootElement[0].GetProperty("name").GetString() == "a\"b\n" && j.RootElement[0].GetProperty("up").GetSingle() > 0);
        foreach (var pair in new[] { new[] { "Car10k(Clone)", "Car10k" }, new[] { "Kolbasz (1)", "Kolbasz" }, new[] { "Car(Clone) (12)(Clone)", "Car" }, new[] { "Thing (x)", "Thing (x)" }, new[] { "Thing ()", "Thing ()" }, new[] { "Thing(1)", "Thing(1)" } })
        {
            Entities.Objects[12].name = pair[0];
            using (var j = JsonDocument.Parse(ItemTraffic.ListJson(1))) Check("object name " + pair[0], j.RootElement[0].GetProperty("name").GetString() == pair[1]);
        }
        Check("JSON limit", ItemTraffic.ListJson(0) == "[]");
        Entities.Objects.Clear(); using (var j = JsonDocument.Parse(ItemTraffic.ListJson(1))) Check("unresolved name is null", j.RootElement[0].GetProperty("name").ValueKind == JsonValueKind.Null);
        Sample(11); Check("inactive counters pruned", Rows.Count == 0);
        Time.realtimeSinceStartup = 11; Send(live); Sample(12); Check("pruned slot reused without old totals", Near(Row(12).UpBps, live.Len));
        NetMeter.Reset(); ItemTraffic.Current(Rows); Check("NetMeter reset clears object rates", Rows.Count == 0);
        Fresh(); Send(live); Sample(); ItemTraffic.Reset(); Time.realtimeSinceStartup = 2; Receive(live); Sample(3);
        Check("session reset drops old rates before ids are reused", Rows.Count == 1 && Near(Row(12).DownBps, live.Len) && Near(Row(12).UpBps, 0));
        Fresh(); NetMeter.ResetTelemetry(); TLDRevamp.SessionLog.History.Clear();
        var telemetryBatch = Batch(state, live); Send(telemetryBatch); Receive(live);
        using (var j = JsonDocument.Parse(NetMeter.TelemetryTick(1, 1)))
        {
            long sent = 0, received = 0;
            foreach (var type in j.RootElement.GetProperty("types").EnumerateArray())
            { sent += type.GetProperty("outBytes").GetInt64(); received += type.GetProperty("inBytes").GetInt64(); }
            Check("telemetry counts full transport bytes alongside object traffic", sent == telemetryBatch.Len && received == live.Len);
        }
        var history = TLDRevamp.SessionLog.History;
        Check("telemetry history keeps batch prefixes and sub-message counts", history.NetworkCount == 1 &&
            history.Network[0].Types[Protocol.Batch].OutBytes == 1 && history.Network[0].Types[Entities.State].OutBytes == state.Len + 1 &&
            history.Network[0].Types[Protocol.LiveUsable].Sent == 1 && history.Network[0].Types[Protocol.LiveUsable].Received == 1);
        Sample(); Check("telemetry tick preserves object rates", Near(Row(300).UpBps, state.Len + 1) && Near(Row(12).UpBps, live.Len + 1) && Near(Row(12).DownBps, live.Len));
        using (var j = JsonDocument.Parse(NetMeter.CategoryJson())) Check("telemetry tick clears its interval", j.RootElement.GetProperty("types").GetArrayLength() == 0);
        Fresh(); ItemTraffic.Packet(live.Buf, 0, live.Len, live.Len, true, false);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) ItemTraffic.Packet(live.Buf, 0, live.Len, live.Len, true, false);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check("hot path allocates zero bytes", delta == 0);
        var claim = Head(Entities.Claim, 1); allocated = GC.GetAllocatedBytesForCurrentThread();
        for (uint i = 100; i < 1000; i++) { claim.Len = 1; claim.U32(i); ItemTraffic.Packet(claim.Buf, 0, claim.Len, claim.Len, true, false); }
        delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check("first-seen objects allocate zero bytes", delta == 0);
        Console.WriteLine("ALL PASS (" + checks + " checks)");
    }
}

// Game/session surfaces only; the meter, cursor, writers, codec, protocol and JSON helper above are production code.
namespace TLDRevamp
{
    static class SessionLog
    {
        internal static double St => 0;
        internal static readonly TelemetryHistory History = new TelemetryHistory();
    }
}
namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte CompactState = 98;
        public const byte Share = 20, Assigned = 21, Add = 22, State = 23, Claim = 24, Owner = 25, PartOff = 26, PartDetached = 27;
        public const byte Resync = 31, Edit = 32, RemoveItem = 35, ContactImpulse = 36, DetachReq = 37, ShotgunIn = 39;
        public const byte AttachSync = 41, DetachSync = 42, CrashClaim = 43, CrashRelease = 44, ShotFx = 45, ExplodeReq = 46;
        public const byte BreakHit = 48, BreakFx = 49, AiState = 50, AiSound = 51, PoiUsable = 52, PushIn = 53;
        public const byte Refetch = 58, HandoverState = 59, AttachReq = 62, CensusAsk = 65, CensusReply = 66, Worn = 85, MemberGone = 86, RabbitState = 90, RabbitFx = 91, IgnitionKey = 96;
        public static readonly Dictionary<uint, GameObject> Objects = new Dictionary<uint, GameObject>();
        internal static GameObject GameObjectOf(uint id) => Objects.TryGetValue(id, out var go) ? go : null;
        internal static bool Finite(Vector3 p) => true;
        internal static int TelemetryPacketCategory(byte type, byte[] bytes, int at, int length) => -1;
        internal static void TelemetryOwned(int[] into) => Array.Clear(into, 0, into.Length);
        internal static void BandwidthSnapshot(long[] into) => Array.Clear(into, 0, into.Length);
    }
    public static class ModObjects
    {
        public const byte ModShare = 70, ModAssigned = 71, ModAdd = 72, ModRecord = 73, ModEdit = 74, ModOwner = 75, ModRemove = 76, ModPose = 77, ModEvent = 78, ModClaim = 79, ModState = 87, ModStore = 88;
    }
    public static class Mp { public static MpServer Server; }
    public sealed class MpServer { public int PlayerOfConn(uint id) => 0; }
    public static class ModTransfer { }
    public static class PlayerSave { public const byte Record = 67; }
    public static class PlayerCombat { public const byte PlayerDamage = 40; }
    public static class HitCheck { public const byte KCreature = 1; }
    public static class Reports { }
    public static class Tpa { }
    public static class TelemetryMarks { }
}
namespace TLDRevamp.Multiplayer
{
    public sealed class SharedObject { public GameObject GameObject; }
    public static class MpApi { public static SharedObject Find(uint id) => null; }
}
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
}
namespace UnityEngine
{
    public sealed class GameObject { public string name; }
    public static class Time
    {
        public static float realtimeSinceStartup;
        public static double realtimeSinceStartupAsDouble => realtimeSinceStartup;
        public static int frameCount => 0;
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude => x * x + y * y + z * z;
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
    public static class Mathf
    {
        public static float Abs(float n) => Math.Abs(n);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Sqrt(float n) => (float)Math.Sqrt(n);
        public static float Clamp(float n, float a, float b) => Math.Clamp(n, a, b);
        public static int RoundToInt(float n) => (int)Math.Round(n);
        public static float Repeat(float n, float m) => n - (float)Math.Floor(n / m) * m;
    }
}
