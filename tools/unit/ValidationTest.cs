#pragma warning disable CS0649, CS8981
global using tosaveitemscript = ValidationFakes.Item;
global using mainscript = ValidationFakes.Main;
global using menuhandler = ValidationFakes.Menu;
using System;
using System.Collections.Generic;
using System.Globalization;
using TLDRevamp.Net;
using UnityEngine;

static class ValidationTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main()
    {
        Entities.ValidationTests(Check);
        Console.WriteLine("ALL PASS (" + checks + " validation checks)");
        return 0;
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        sealed class SEnt
        {
            internal uint NetId, Epoch, ParentNet, AttachParent;
            internal int OwnerId, PartIndex = -1;
            internal bool HasState;
            internal Vector3d Pos, Where;
            internal Quaternion Rot;
            internal itemDataClass Record;
            internal readonly List<uint> Parts = new List<uint>();
            internal readonly HashSet<int> SplitOff = new HashSet<int>();
            internal HashSet<int> Gone;
            internal readonly Dictionary<int, byte[]> ItemState = new Dictionary<int, byte[]>();
        }
        static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        static readonly Dictionary<int, HashSet<uint>> Known = new Dictionary<int, HashSet<uint>>();
        static readonly Dictionary<string, int> Leases = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<int, string>> LeaseTypes = new Dictionary<string, KeyValuePair<int, string>>();
        static readonly HashSet<string> _leasesDone = new HashSet<string>();
        static readonly Dictionary<string, Poi> Pois = new Dictionary<string, Poi>();
        static readonly NetWriter W = new NetWriter();
        const byte LeaseGrant = 29, LeaseTaken = 30, LeaseFreed = 34;
        const uint CanonBase = 0xF0000000u;
        static uint _nextNet = 100;
        static long PartOffRejected, LeasesFreed, LeasesGranted, LeaseTypeMismatches;
        static string PartRejects = "";
        static int Broadcasts, Sent, LastTarget;
        static byte LastMessage;
        static itemDataClass GroupRecord(SEnt e) => e.Record;
        static Poi FindPoi(string key) => Pois.TryGetValue(key, out var poi) ? poi : null;
        static void WritePartDetached(NetWriter w, SEnt e) { w.Reset(); w.U8(27); }
        static void ServerSendAll(NetWriter w, bool reliable, int except) { Broadcasts++; LastMessage = w.Buf[0]; }
        static void ServerSendTo(int from, NetWriter w, bool reliable) { Sent++; LastTarget = from; LastMessage = w.Buf[0]; }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        static bool Finite(Vector3d p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
        static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);

        static SEnt Car()
        {
            Server.Clear(); Known.Clear(); RecordCodec.Records.Clear(); _nextNet = 100; Broadcasts = 0;
            itemdatabase.s = new itemdatabase();
            var prefab = new tosaveitemscript(); prefab.partslotscripts.Add(new object());
            itemdatabase.s.Items[10] = new GameObject { Item = prefab };
            var group = new itemDataClass();
            group.items.Add(new save_item { id = 1000, prefabID = 10 });
            group.items.Add(new save_item { id = 1001, prefabID = 11 });
            group.attachable.Add(new save_attachable { id = 1001, parentid = 1000, attachType = 1, indexType = 2, index = 0 });
            var car = new SEnt { NetId = 1, OwnerId = 2, Epoch = 3, Record = group, Where = new Vector3d(20, 5, 40) };
            Server[1] = car; Known[2] = new HashSet<uint> { 1 };
            return car;
        }

        static NetReader Off(int idx = 1, uint epoch = 3, Vector3d? pos = null, Quaternion? rot = null)
        {
            var w = new NetWriter(); var p = pos ?? new Vector3d(21, 6, 41); var q = rot ?? new Quaternion(0, 0, 0, 1);
            w.U32(1); w.U32(epoch); w.VarU32(unchecked((uint)idx));
            w.F64(p.x); w.F64(p.y); w.F64(p.z); w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w);
            return new NetReader { Buf = w.Buf, End = w.Len };
        }

        static void LeaseSetup()
        {
            Leases.Clear(); LeaseTypes.Clear(); LeaseUntil.Clear(); _leasesDone.Clear(); Pois.Clear();
            LeaseRefusedWhy.Clear();
            menuhandler.s = new menuhandler { currentMainMap = new ValidationFakes.Map() };
            menuhandler.s.currentMainMap.poiGens.Add(new ValidationFakes.PoiGenerator { chunkSize = 1000 });
            Mp.Server = new TestServer(); Time.realtimeSinceStartup = 10;
            Mp.Server.Players[1] = new Vector3d(20, 0, 40); Mp.Server.Players[2] = new Vector3d(20, 0, 40);
            itemdatabase.s = new itemdatabase(); itemdatabase.s.Buildings[7] = new GameObject { name = "site" };
            itemdatabase.s.Buildings[8] = new GameObject { name = "other" };
            Pois["20,40"] = new Poi { id = 7 }; Sent = Broadcasts = 0;
        }

        internal static void ValidationTests(Action<bool, string> check)
        {
            foreach (int idx in new[] { -1, int.MinValue, 0, 2, 4097 })
            {
                Car(); ServerPartOff(2, Off(idx));
                check(Server.Count == 1 && _nextNet == 100 && SessionLog.Reason == "item index", "invalid member " + idx);
            }
            var car = Car(); ServerPartOff(1, Off());
            check(Server.Count == 1 && SessionLog.Reason == "wrong owner", "non-owner refused");
            ServerPartOff(2, Off(epoch: 2)); check(Server.Count == 1 && SessionLog.Reason == "epoch", "old epoch refused");
            Server.Clear(); ServerPartOff(2, Off()); check(SessionLog.Reason == "missing car", "missing car refused");
            foreach (double n in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                foreach (int axis in new[] { 0, 1, 2 })
                {
                    Car(); ServerPartOff(2, Off(pos: new Vector3d(axis == 0 ? n : 20, axis == 1 ? n : 5, axis == 2 ? n : 40)));
                    check(Server.Count == 1 && SessionLog.Reason == "non-finite pose", "nonfinite position " + axis);
                }
            foreach (float n in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                foreach (int axis in new[] { 0, 1, 2, 3 })
                {
                    Car(); ServerPartOff(2, Off(rot: new Quaternion(axis == 0 ? n : 0, axis == 1 ? n : 0, axis == 2 ? n : 0, axis == 3 ? n : 1)));
                    check(Server.Count == 1 && SessionLog.Reason == "non-finite pose", "nonfinite rotation " + axis);
                }
            Car(); ServerPartOff(2, Off(rot: new Quaternion())); check(Server.Count == 1, "zero rotation refused");
            Car(); ServerPartOff(2, Off(pos: new Vector3d(20, 56, 40))); check(Server.Count == 1 && SessionLog.Reason == "pose too far", "vertical distance checked");
            Car(); ServerPartOff(2, Off(pos: new Vector3d(71, 5, 40))); check(Server.Count == 1, "horizontal distance checked");
            Car(); var bad = Off(); bad.End--; ServerPartOff(2, bad); check(Server.Count == 1 && SessionLog.Reason == "malformed", "truncated packet");
            bad = Off(); bad.End++; ServerPartOff(2, bad); check(Server.Count == 1 && SessionLog.Reason == "malformed", "trailing packet bytes");
            car = Car(); car.Record = null; ServerPartOff(2, Off()); check(Server.Count == 1, "no group record");
            car = Car(); car.Record.items = null; ServerPartOff(2, Off()); check(Server.Count == 1, "null member list refused without throwing");
            car = Car(); car.Record.attachable = null; ServerPartOff(2, Off()); check(Server.Count == 1, "null attachment list refused without throwing");
            car = Car(); car.Record.items[1] = null; ServerPartOff(2, Off()); check(Server.Count == 1, "empty member");
            car = Car(); car.Record.attachable.Clear(); ServerPartOff(2, Off()); check(Server.Count == 1 && SessionLog.Reason == "not attached", "loose group member refused");
            car = Car(); car.Record.attachable[0].parentid = 999; ServerPartOff(2, Off()); check(Server.Count == 1 && SessionLog.Reason == "slot parent", "unknown parent refused");
            car = Car(); car.Record.attachable[0].index = 1; ServerPartOff(2, Off()); check(Server.Count == 1 && SessionLog.Reason == "prefab slot", "prefab slot range");
            car = Car(); itemdatabase.s.Items[10].Item = null; ServerPartOff(2, Off()); check(Server.Count == 1, "known prefab must have part slots");
            car = Car(); car.Record.attachable[0].index = -1; ServerPartOff(2, Off()); check(Server.Count == 1, "negative slot refused");
            car = Car(); car.Record.attachable[0].indexType = 1; ServerPartOff(2, Off()); check(Server.Count == 1, "non-slot attachment refused");
            car = Car(); car.Gone = new HashSet<int> { 1 }; ServerPartOff(2, Off()); check(Server.Count == 1, "destroyed part refused");
            car = Car(); car.ItemState[1] = new byte[] { 1 }; RecordCodec.Records[1] = new itemDataClass();
            ServerPartOff(2, Off()); check(Server.Count == 1 && SessionLog.Reason == "not attached", "latest detached record wins over original attached record");
            RecordCodec.Records[1].attachable.Add(new save_attachable { id = CanonBase + 1, parentid = CanonBase, attachType = 1, indexType = 2, index = 0 }); ServerPartOff(2, Off());
            check(Server.Count == 2 && car.ItemState.Count == 0 && Known[2].Contains(100), "latest attached record accepted and consumed");
            car = Car(); car.ItemState[1] = new byte[] { 1 }; RecordCodec.Records[1] = new itemDataClass();
            RecordCodec.Records[1].attachable.Add(car.Record.attachable[0]); ServerPartOff(2, Off());
            check(Server.Count == 1 && SessionLog.Reason == "not attached", "latest record must address canonical member IDs");
            car = Car(); car.ItemState[1] = new byte[] { 99 }; ServerPartOff(2, Off()); check(Server.Count == 1 && SessionLog.Reason == "item record", "unreadable latest record refused");
            car = Car(); itemdatabase.s.Items.Clear(); ServerPartOff(2, Off()); check(Server.Count == 2, "record validates slot when prefab unavailable");
            car = Car(); ServerPartOff(2, Off());
            check(Server.Count == 2 && car.Parts.Count == 1 && Broadcasts == 1 && Server[100].ParentNet == 1 && Server[100].PartIndex == 1 && Server[100].OwnerId == 2, "valid detach creates exactly one entity");
            ServerPartOff(2, Off()); check(Server.Count == 2 && Broadcasts == 1 && _nextNet == 101, "same-epoch replay is idempotent");
            car.OwnerId = 1; car.Epoch++;
            ServerPartOff(2, Off()); ServerPartOff(1, Off(epoch: 4));
            check(Server.Count == 2 && Broadcasts == 1, "ownership change cannot duplicate accepted detach");
            Server.Remove(100); ServerPartOff(1, Off(epoch: 4));
            check(Server.Count == 1 && _nextNet == 101 && SessionLog.Reason == "already detached or gone", "removed entity leaves consumed membership");
            car = Car(); car.OwnerId = 1; car.Epoch = 4; ServerPartOff(2, Off()); ServerPartOff(1, Off(epoch: 4));
            check(Server.Count == 2 && Server[100].OwnerId == 1, "late old-owner detach rejected before current-owner detach");

            // Car03's mirror remains in the car group after its door becomes an independent entity.
            foreach (bool canonical in new[] { false, true })
                foreach (uint attachedTo in new uint[] { 0, 1, 9 })
                {
                    car = Car();
                    car.Record.items.Add(new save_item { id = 1002, prefabID = 12 });
                    car.Record.attachable.Add(new save_attachable { id = 1002, parentid = 1001, attachType = 1, indexType = 2, index = 0 });
                    itemdatabase.s.Items[11] = itemdatabase.s.Items[10];
                    ServerPartOff(2, Off());
                    Server[100].AttachParent = attachedTo;
                    if (canonical)
                    {
                        car.ItemState[2] = new byte[] { 1 };
                        RecordCodec.Records[1] = new itemDataClass();
                        RecordCodec.Records[1].attachable.Add(new save_attachable { id = CanonBase + 2, parentid = CanonBase + 1, attachType = 1, indexType = 2, index = 0 });
                    }
                    if (attachedTo == 1)
                    {
                        car.Gone = new HashSet<int> { 1 };
                        ServerPartOff(2, Off(2));
                        check(Server.Count == 2 && SessionLog.Reason == "slot parent", "destroyed reattached door cannot supply mirror slot");
                        car.Gone.Clear();
                    }
                    ServerPartOff(2, Off(2));
                    check(Server.Count == (attachedTo == 1 ? 3 : 2), "mirror slot accepts only door reattached to its original car, canonical=" + canonical + " parent=" + attachedTo);
                    if (attachedTo == 1)
                    {
                        check(Server[101].PartIndex == 2 && car.SplitOff.Contains(2), "mirror becomes its own entity exactly once");
                        ServerPartOff(2, Off(2));
                        check(Server.Count == 3 && _nextNet == 102, "mirror duplicate remains refused");
                    }
                    else
                    {
                        Server.Remove(100); ServerPartOff(2, Off(2));
                        check(Server.Count == 1 && SessionLog.Reason == "slot parent", "removed split door cannot supply mirror slot");
                    }
                }

            LeaseSetup();
            foreach (string key in new[] { null, "", "building", "20", "20,40,0", "20, 40", "020,40", "20.0,40", "NaN,40", "Infinity,40", "100000001,40", "2e1,40", "20\n,40" })
            {
                ServerLease(1, key, 7); check(Leases.Count == 0 && SessionLog.Reason == "building key", "bad key " + key);
            }
            var oldCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                check(LeasePosition("20.5,-40.25", out var p) && p.x == 20.5 && p.z == -40.25, "keys are invariant");
            }
            finally { CultureInfo.CurrentCulture = oldCulture; }
            ServerLease(-1, "20,40", 7); ServerLease(99, "20,40", 7);
            check(Leases.Count == 0 && SessionLog.Reason == "requester not ready", "unknown/disconnected requester refused");
            Mp.Server.Players[1] = new Vector3d(10020, 0, 40); ServerLease(1, "20,40", 7);
            check(Leases.Count == 0 && SessionLog.Reason == "building reach", "far requester refused");
            Mp.Server.Players[1] = new Vector3d(double.NaN, 0, 40); ServerLease(1, "20,40", 7);
            check(Leases.Count == 0, "nonfinite player refused");
            Mp.Server.Players[1] = new Vector3d(20, 0, 40);
            ServerLease(1, "20,40", 999); ServerLease(1, "20,40", -1);
            check(Leases.Count == 0 && SessionLog.Reason == "building type", "unknown catalog type refused");
            ServerLease(1, "20,40", 8); check(Leases.Count == 0 && SessionLog.Reason == "building type mismatch", "known POI identity checked");
            check(LastMessage == LeaseFreed && LastTarget == 1, "refusal releases requester's local spawn gate for retry");
            Sent = 0;
            ServerLease(1, "20,40", 7, "untrusted name");
            check(Leases["20,40"] == 1 && LastMessage == LeaseGrant && LeaseTypes["20,40"].Value == "site", "valid grant uses catalog name");
            float deadline = LeaseUntil["20,40"];
            Time.realtimeSinceStartup = 20; ServerLease(1, "20,40", 7);
            check(Sent == 1 && LeaseUntil["20,40"] == deadline, "repeat request neither grants twice nor extends timeout");
            ServerLease(2, "20,40", 7); check(Leases.Count == 1 && Leases["20,40"] == 1 && LastMessage == LeaseTaken && LastTarget == 2, "second requester takes existing lease");
            ServerLeaseDone(2, "20,40"); check(!_leasesDone.Contains("20,40"), "wrong holder cannot finish");
            ServerLeaseDone(1, "arbitrary"); check(!_leasesDone.Contains("20,40"), "invalid done key cannot finish");
            Time.realtimeSinceStartup = deadline; ServerLeaseDone(1, "20,40");
            check(Leases.Count == 0 && LeaseTypes.Count == 0 && LeaseUntil.Count == 0 && LastMessage == LeaseFreed, "timeout frees before late done");
            ServerLease(2, "20,40", 7); ServerLeaseDone(1, "20,40");
            check(Leases["20,40"] == 2 && !_leasesDone.Contains("20,40"), "previous holder cannot finish new holder's lease");
            ServerLeaseDone(2, "20,40"); Time.realtimeSinceStartup += 100; ServerLeaseExpiry();
            check(_leasesDone.Contains("20,40") && Leases.Count == 1 && LeaseUntil.Count == 0, "completed lease does not expire");
            ServerLeaseLeave(2); check(Leases["20,40"] == 0 && _leasesDone.Contains("20,40"), "finished lease survives disconnect");
            LeaseSetup(); ServerLease(1, "20,40", 7); ServerLeaseLeave(1);
            check(Leases.Count == 0 && LeaseUntil.Count == 0 && LeaseTypes.Count == 0 && LastMessage == LeaseFreed, "unfinished lease freed on leave");
            ServerLease(2, "20,40", 7); check(Leases["20,40"] == 2, "lease available after disconnect");
            Mp.Server.Players.Remove(2); ServerLeaseExpiry(); check(Leases.Count == 0, "expiry also detects disconnected holder");
            LeaseSetup(); Pois.Clear(); ServerLease(1, "20,40", 7); ServerLease(2, "20,40", 8);
            check(Leases["20,40"] == 1 && LeaseTypes["20,40"].Key == 7 && SessionLog.Reason == "building type mismatch", "far POI retains first validated type without simulation");
            LeaseSetup(); ServerLease(1, "20,40"); check(Leases.Count == 1, "legacy request can resolve known POI type");
            LeaseSetup(); Pois.Clear(); ServerLease(1, "20,40"); check(Leases.Count == 0, "unknown POI requires catalog type");
            LeaseSetup(); ServerLease(0, "20,40", 7); check(Leases["20,40"] == 0, "host uses real player position too");
            LeaseSetup(); Mp.Server = null; ServerLease(1, "21,40", 7);
            check(Leases.Count == 0 && SessionLog.Reason == "requester not ready", "no session refused");
            LeaseSetup(); var leasePacket = new NetWriter(); leasePacket.Str("20,40"); leasePacket.I32(7); leasePacket.Str("untrusted name");
            int keyLength = 6;
            for (int cut = 0; cut < leasePacket.Len; cut++)
            {
                if (cut == keyLength) continue; // the supported legacy key-only request
                ServerLeaseRequest(1, new NetReader { Buf = leasePacket.Buf, End = cut });
                check(Leases.Count == 0 && SessionLog.Reason == "malformed", "lease packet truncation " + cut);
            }
            ServerLeaseRequest(1, new NetReader { Buf = leasePacket.Buf, End = leasePacket.Len + 1 });
            check(Leases.Count == 0 && SessionLog.Reason == "malformed", "lease trailing bytes refused");
            ServerLeaseRequest(1, new NetReader { Buf = leasePacket.Buf, End = leasePacket.Len });
            check(Leases.Count == 1 && LeaseTypes["20,40"].Value == "site", "valid lease packet reaches validator");
            var donePacket = new NetWriter(); donePacket.Str("20,40");
            ServerLeaseDoneRequest(1, new NetReader { Buf = donePacket.Buf, End = donePacket.Len - 1 });
            ServerLeaseDoneRequest(1, new NetReader { Buf = donePacket.Buf, End = donePacket.Len + 1 });
            check(!_leasesDone.Contains("20,40") && SessionLog.Reason == "malformed", "malformed done never completes lease");
            ServerLeaseDoneRequest(2, new NetReader { Buf = donePacket.Buf, End = donePacket.Len });
            check(!_leasesDone.Contains("20,40"), "wire done checks holder");
            ServerLeaseDoneRequest(1, new NetReader { Buf = donePacket.Buf, End = donePacket.Len });
            check(_leasesDone.Contains("20,40"), "valid wire done completes lease");

            LeaseSetup();
            // Center of the requester's chunk is (0,0); the far corner of its 3x3 area is (1500,1500).
            Mp.Server.Players[1] = new Vector3d(-499, 0, -499);
            ServerLease(1, "1500,1500", 7);
            check(Leases.ContainsKey("1500,1500") && LeaseRefusedWhy.Count == 0, "far 3x3 corner accepted even from opposite chunk edge");
            ServerLease(1, "9500,9500", 7);
            check(!Leases.ContainsKey("9500,9500") && LeaseRefusedWhy["buildingReach"] == 1, "far outside POI generation area refused and counted");
            double reach = 1000 * (1.5 * Math.Sqrt(2) + 1);
            check(Math.Abs(LeaseReachM() - reach) < 1e-6, "runtime reach includes exactly one chunk margin");
            Mp.Server.Players[1] = new Vector3d(20 - reach, 0, 40);
            ServerLease(1, "20,40", 7);
            check(Leases.ContainsKey("20,40"), "reach boundary accepted");
            Mp.Server.Players[1] = new Vector3d(22 + reach, 0, 41);
            ServerLease(1, "21,41", 7);
            check(!Leases.ContainsKey("21,41") && LeaseRefusedWhy["buildingReach"] == 2, "beyond reach boundary refused and accumulated");
            menuhandler.s.currentMainMap.poiGens.Insert(0, new ValidationFakes.PoiGenerator { chunkSize = 100 });
            check(Math.Abs(LeaseReachM() - reach) < 1e-6, "largest generator sets reach");
            menuhandler.s.currentMainMap.poiGens[1].chunkSize = 2000;
            check(Math.Abs(LeaseReachM() - 2 * reach) < 1e-6, "generator size is read at runtime");

            foreach (float size in new[] { 0f, -100f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                LeaseSetup(); menuhandler.s.currentMainMap.poiGens[0].chunkSize = size;
                ServerLease(1, "3020,3040", 7);
                check(Leases.ContainsKey("3020,3040") && LeaseReachM() == 5000, "unknown/invalid chunk size uses generous fallback " + size);
                ServerLease(1, "5021,40", 7);
                check(!Leases.ContainsKey("5021,40") && LeaseRefusedWhy["buildingReach"] == 1, "fallback still bounds reach " + size);
            }
            for (int missing = 0; missing < 5; missing++)
            {
                LeaseSetup();
                if (missing == 0) menuhandler.s = null;
                else if (missing == 1) menuhandler.s.currentMainMap = null;
                else if (missing == 2) menuhandler.s.currentMainMap.poiGens = null;
                else if (missing == 3) menuhandler.s.currentMainMap.poiGens.Clear();
                else menuhandler.s.currentMainMap.poiGens[0] = null;
                ServerLease(1, "5020,40", 7);
                check(Leases.ContainsKey("5020,40") && LeaseReachM() == 5000, "unavailable generator uses fallback " + missing);
            }

            LeaseSetup();
            ServerLease(1, "invalid", 7); ServerLease(99, "20,40", 7);
            ServerLease(1, "20,40", 999); ServerLease(1, "20,40", 8);
            ServerLeaseRequest(1, new NetReader { Buf = Array.Empty<byte>(), End = 0 });
            ServerLeaseDoneRequest(1, new NetReader { Buf = Array.Empty<byte>(), End = 0 });
            ServerLeaseDone(2, "20,40");
            var refusals = System.Text.Json.JsonDocument.Parse(LeaseRefusalsJson()).RootElement;
            foreach (string reason in new[] { "buildingKey", "requesterNotReady", "buildingType", "buildingTypeMismatch", "malformed", "doneMalformed", "doneHolder" })
                check(refusals.GetProperty(reason).GetInt64() == 1, "refusal reason counted and exported: " + reason);
        }
    }
    static class Mp { internal static TestServer Server; }
    sealed class TestServer
    {
        internal readonly Dictionary<int, Vector3d> Players = new Dictionary<int, Vector3d>();
        internal bool PeerPos(int id, out Vector3d pos) => Players.TryGetValue(id, out pos);
    }
    static class RecordCodec
    {
        internal static readonly Dictionary<byte, itemDataClass> Records = new Dictionary<byte, itemDataClass>();
        internal static itemDataClass Decode(byte[] b) => Records.TryGetValue(b[0], out var value) ? value : null;
    }
}

sealed class itemDataClass
{
    internal List<save_item> items = new List<save_item>();
    internal List<save_attachable> attachable = new List<save_attachable>();
}
sealed class save_item { internal uint id; internal int prefabID; }
sealed class save_attachable { internal uint id, parentid; internal int attachType, indexType, index; }
sealed class Poi { internal int id; }
sealed class itemdatabase
{
    internal static itemdatabase s;
    internal readonly Dictionary<int, GameObject> Items = new Dictionary<int, GameObject>(), Buildings = new Dictionary<int, GameObject>();
    internal GameObject Item(int id) => Items.TryGetValue(id, out var go) ? go : null;
    internal GameObject Building(int id) => Buildings[id];
}
namespace ValidationFakes
{
    sealed class Menu { internal static Menu s; internal Map currentMainMap; }
    sealed class Map { internal List<PoiGenerator> poiGens = new List<PoiGenerator>(); }
    sealed class PoiGenerator { internal float chunkSize; }
    sealed class Item { internal readonly List<object> partslotscripts = new List<object>(); }
    sealed class Main
    {
        internal static Main s = new Main();
        internal Player player = new Player();
        internal static Vector3d GlobalFromUnityPos(Vector3d pos) => pos;
    }
}
sealed class Player { internal Transform transform = new Transform(); }
sealed class Transform { internal Vector3d position = new Vector3d(20, 0, 40); }
struct Vector3d { internal double x, y, z; internal Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; } }
namespace UnityEngine
{
    struct Quaternion { internal float x, y, z, w; internal Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
    static class Time { internal static float realtimeSinceStartup; }
    sealed class GameObject
    {
        internal string name;
        internal tosaveitemscript Item;
        internal T GetComponent<T>() where T : class => Item as T;
    }
}
static class SessionLog
{
    internal static string Reason;
    internal static void Event(string kind, uint net, int item = -1, int from = -1, uint epoch = 0, string result = "ok") => Reason = result;
}
static class Plugin
{
    internal static readonly Logger Log = new Logger();
    internal sealed class Logger { internal void LogInfo(string message) { } }
}
