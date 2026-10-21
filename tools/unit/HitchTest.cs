global using tosaveitemscript = HitchFakes.Item;
global using attachablescript = HitchFakes.Attachment;
global using partslotscript = HitchFakes.Slot;
global using attachgripscript = HitchFakes.Grip;
global using rearlights = HitchFakes.Lights;
global using itemdatabase = HitchFakes.Database;
global using mainscript = HitchFakes.Main;
global using fpscontroller = HitchFakes.Player;
using System;
using System.Collections.Generic;
using System.IO;
using TLDRevamp.Net;
using UnityEngine;

static class HitchTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Main() { Entities.Tests(); Console.WriteLine("ALL PASS (" + checks + " hitch checks)"); }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed class Ent
        {
            internal uint NetId, Epoch = 1; internal int OwnerId, HitchAuthorityOwner = -1; internal bool Proxy, HitchWaiting, SentAtRest, HitchHasVelocity;
            internal HashSet<int> Split;
            internal float HitchUntil = -100; internal Vector3 LastVel, HitchVelocity;
            internal tosaveitemscript Root; internal readonly List<tosaveitemscript> Items = new List<tosaveitemscript>();
            internal readonly List<Rigidbody> HitchBodies = new List<Rigidbody>(); internal readonly HashSet<uint> HitchAnnounced = new HashSet<uint>();
        }
        private sealed class SEnt
        {
            internal uint NetId, Epoch = 1, HitchCar, AttachParent; internal int OwnerId, HitchSlot, CrashReturnTo = -1;
            internal bool Hitch, Car, Stored, Held, HasState; internal Vector3d Pos, Where; internal Quaternion Rot;
            internal byte[] LastAttach; internal HashSet<uint> AttachKids;
        }
        private static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        private static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        private static readonly Dictionary<HitchFakes.Car, float> DamageGraceUntil = new Dictionary<HitchFakes.Car, float>();
        private static bool InSession = true, PlayerActing = true, _applyingAttach, _applyingDetach, EditRangeCheck = true, Near = true;
        private static int MyId;
        private static bool IsHost = true;
        private const byte Add = 22, Owner = 25, DetachSync = 42;
        private static readonly Dictionary<int, HashSet<uint>> Known = new Dictionary<int, HashSet<uint>>();
        private static HashSet<uint> KnownBy(int player)
        {
            if (!Known.TryGetValue(player, out var known)) Known[player] = known = new HashSet<uint> { 10, 20 };
            return known;
        }
        private static void SendObject(int player, SEnt e)
        {
            var w = new NetWriter(); w.U8(Add); w.U32(e.NetId); ServerSendTo(player, w, true); KnownBy(player).Add(e.NetId);
        }
        private static void DropAttach(SEnt e)
        {
            if (Server.TryGetValue(e.AttachParent, out var parent)) parent.AttachKids?.Remove(e.NetId);
            e.HitchCar = e.AttachParent = 0; e.LastAttach = null;
        }
        private static readonly List<(int target, byte[] data)> Packets = new List<(int, byte[])>();
        private static readonly List<byte[]> Requests = new List<byte[]>();
        private static Ent OwnEntity(tosaveitemscript it) { foreach (var e in ByNet.Values) if (e.Root == it) return e; return null; }
        private static Ent EditEntityOf(tosaveitemscript it) => OwnEntity(it);
        private static bool Resolve(Ent e) => e.Root != null;
        private static bool AtObject(int from, SEnt e) => Near;
        private static HitchFakes.Record EditItemRecord(SEnt e, int idx) => new HitchFakes.Record();
        private static bool Finite(Vector3d v) => double.IsFinite(v.x) && double.IsFinite(v.y) && double.IsFinite(v.z);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Finite(Quaternion q) => float.IsFinite(q.w);
        private static void WriteRot(NetWriter w, Quaternion q) { w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
        private static Quaternion ReadRot(NetReader r) => new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        private static byte[] Copy(NetWriter w) { var b = new byte[w.Len]; Array.Copy(w.Buf, b, b.Length); return b; }
        private static void ToServer(NetWriter w, bool reliable) => Requests.Add(Copy(w));
        private static void ServerSendTo(int target, NetWriter w, bool reliable) => Packets.Add((target, Copy(w)));
        private static void ServerSendAll(NetWriter w, bool reliable, int except)
        { ServerSendTo(0, w, reliable); ServerSendTo(1, w, reliable); if (Known.ContainsKey(2)) ServerSendTo(2, w, reliable); }
        private static void OwnerLog(SEnt e, int target, string reason) { }
        private static void CascadeOwner(SEnt e) => CascadeHitches(e);
        private static void ApplyProxyBodies(Ent e) { foreach (var rb in HitchBodies(e)) rb.isKinematic = true; }
        private static void SendDetachSync(Ent tr) { Server[tr.NetId].HitchCar = Server[tr.NetId].AttachParent = 0; }
        private static bool Unmount(attachablescript at, bool fallOff) { at.Detach(); return true; }
        private static void SendAttach(Ent tr, attachablescript at)
        {
            if (!Server.TryGetValue(tr.NetId, out var se)) return;
            var car = HitchCar(at); se.AttachParent = se.HitchCar = car != null ? car.NetId : 0;
        }
        public static void GameAttach(attachablescript at, partslotscript slot)
        {
            if (!(bool)typeof(HitchSlotHook).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { at, slot })) return;
            at.slot = slot; GameAttach(at, slot.T);
        }
        public static void GameAttach(attachablescript at, Transform point)
        {
            if (!(bool)typeof(HitchJointHook).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { at, point })) return;
            at.attached = true; at.point = point; at.J = new Joint(); at.slot.parts.Add(at);
            var tr = OwnEntity(at.tosave); if (!_applyingAttach && tr != null) SendAttach(tr, at);
        }
        private static NetReader Reader(byte[] b) => new NetReader { Buf = b, Pos = 1, End = b.Length };
        private static void ServerPacket(byte[] b, int from)
        {
            var r = Reader(b);
            switch (b[0])
            {
                case HitchRequest: ServerHitchRequest(from, r); break;
                case HitchFrozen: ServerHitchFrozen(from, r); break;
                case HitchResult: ServerHitchResult(from, r); break;
            }
        }
        private static Ent Make(uint net, int owner, bool trailer)
        {
            var it = new tosaveitemscript(); var e = new Ent { NetId = net, Root = it, OwnerId = owner, HitchAuthorityOwner = owner, Proxy = owner != MyId };
            e.Items.Add(it); ByNet[net] = e; it.RB.isKinematic = e.Proxy;
            if (trailer) { it.attachable = new attachablescript { tosave = it, utanfuto = new HitchFakes.Trailer() }; it.attachable.RB.isKinematic = e.Proxy; }
            else it.car = new HitchFakes.Car();
            return e;
        }
        private static void Grant(Ent tr, int target, uint epoch)
        {
            HitchBeforeOwner(tr, target, epoch); tr.OwnerId = tr.HitchAuthorityOwner = target; tr.Epoch = epoch; tr.Proxy = target != MyId;
            foreach (var rb in HitchBodies(tr)) rb.isKinematic = tr.Proxy;
            if (tr.HitchWaiting) HitchFreezeBodies(tr);
        }
        internal static void Tests()
        {
            var check = (Action<bool, string>)HitchTest.Check;
            for (int carOwner = 0; carOwner < 3; carOwner++)
            for (int trailerOwner = 0; trailerOwner < 3; trailerOwner++)
            for (int me = 0; me < 3; me++)
                check(HitchAuthority.CanJoin(carOwner, trailerOwner, me, false, carOwner != me, trailerOwner != me) == (carOwner == me && trailerOwner == me), "only the common owner can make the joint");
            check(!HitchAuthority.CanJoin(1, 1, 1, true, false, false), "frozen owner cannot join");
            check(!HitchAuthority.CanJoin(1, 1, 1, false, true, false), "provisional authority cannot join a proxy");
            var refusals = new[] { "entity absent", "not a trailer and car", "edit reach", "stale epoch", "held or stored", "slot taken", "already hitched" };
            for (int i = 0; i < refusals.Length; i++)
                check(HitchAuthority.Refusal(i != 0, i != 1, true, i != 2, i == 3, i == 4, i == 5, i == 6) == refusals[i], "specific refusal: " + refusals[i]);

            foreach (int old in new[] { 0, 1 })
            {
                MyId = 1 - old; ByNet.Clear(); Server.Clear(); Packets.Clear(); Requests.Clear();
                _hitchOps.Clear(); _hitchTransfers.Clear(); _hitchCommands.Clear();
                var car = Make(10, MyId, false); var tr = Make(20, old, true);
                var slot = new partslotscript { tosaveitem = car.Root }; car.Root.partslotscripts.Add(slot);
                Server[10] = new SEnt { NetId = 10, OwnerId = MyId, Car = true };
                Server[20] = new SEnt { NetId = 20, OwnerId = old, Hitch = true };
                // The requester invokes the game's slot entry point: no movement, metadata change or joint yet.
                var start = tr.Root.RB.position; tr.Root.attachable.Attach(slot);
                check(!tr.Root.attachable.attached && tr.Root.attachable.J == null && Requests.Count == 1, "non-owner attach is intercepted before any joint");
                check(tr.Root.RB.position.x == start.x, "request does not reposition the proxy");
                ServerPacket(Requests[0], MyId); Requests.Clear();
                check(Packets.Count == 1 && Packets[0].target == old && Packets[0].data[0] == HitchFreeze, "server routes freeze to current trailer owner");
                check(Server[20].OwnerId == old, "no ownership grant before old owner freezes");
                var freeze = Packets[0].data; Packets.Clear();
                MyId = old; tr.Proxy = false; tr.Root.RB.isKinematic = tr.Root.attachable.RB.isKinematic = false;
                ApplyHitchFreeze(Reader(freeze));
                check(tr.HitchWaiting && tr.Root.RB.isKinematic && tr.Root.attachable.RB.isKinematic, "body and separate drawbar freeze together");
                var ack = Requests[0]; Requests.Clear(); ServerPacket(ack, 1 - old);
                check(Server[20].OwnerId == old, "forged acknowledgement cannot transfer authority");
                ServerPacket(ack, old);
                check(Server[20].OwnerId == 1 - old && Server[20].Epoch == 2, "acknowledged transfer increments epoch");
                int grantPackets = Packets.Count; ServerPacket(ack, old);
                check(Packets.Count == grantPackets, "duplicate acknowledgement cannot grant twice");
                MyId = 1 - old; tr.Proxy = true; car.Proxy = false;
                foreach (var packet in Packets.ToArray())
                {
                    if (packet.target != MyId) continue;
                    var r = Reader(packet.data);
                    switch (packet.data[0])
                    {
                        case HitchFreeze: ApplyHitchFreeze(r); check(tr.Root.RB.isKinematic, "new owner prepares frozen"); break;
                        case Owner: r.U32(); int owner = (int)r.VarU32(); Grant(tr, owner, r.U32()); check(tr.HitchWaiting && tr.Root.RB.isKinematic, "grant alone does not unfreeze"); break;
                        case HitchFrozen: ApplyHitchFrozen(r); check(!tr.HitchWaiting && !tr.Root.RB.isKinematic && !tr.Root.attachable.RB.isKinematic, "reliable motion releases both bodies"); break;
                        case HitchCommand: ApplyHitchCommand(r); break;
                    }
                }
                Packets.Clear(); HitchCommandTick();
                check(tr.Root.attachable.attached && tr.Root.attachable.J != null, "common owner creates joint and broadcasts");
                check(DamageGraceUntil[car.Root.car] > Time.time, "coupler alignment guards the car's speed-change damage path");
                ServerPacket(Requests[0], MyId); Requests.Clear();
                check(Packets.Count == 2 && !_hitchOps.ContainsKey(20), "accepted result reaches both peers");

                // A driver takes the car. Its hitch remains registered and follows, without a general pickup claim.
                car.HitchUntil = -100; var joint = tr.Root.attachable.J;
                Server[10].OwnerId = old; Server[10].Epoch++; Packets.Clear(); CascadeHitches(Server[10]);
                check(Packets.Count == 1 && Packets[0].target == MyId && Packets[0].data[0] == HitchFreeze, "car takeover starts trailer transfer");
                HitchBeforeOwner(car, old, 2); car.Proxy = true; car.OwnerId = old;
                check(joint.Destroyed && tr.Root.attachable.J == null && tr.Root.RB.isKinematic, "old simulator removes constraint synchronously and freezes before car becomes a proxy");
                check(!HitchAuthority.CanJoin(car.OwnerId, tr.OwnerId, MyId, tr.HitchWaiting, car.Proxy, tr.Proxy), "no mixed-authority joint during takeover");

                // trailercheck reads hitchstate, then entities in a separate bridge job. A handback can land between them.
                car.Proxy = false; car.OwnerId = car.HitchAuthorityOwner = MyId;
                tr.HitchWaiting = false; tr.Proxy = false; tr.OwnerId = tr.HitchAuthorityOwner = MyId;
                HitchRestore(tr);
                var beforeHandback = TestHitchState(20);
                check(beforeHandback.Contains("\"joint\":true") && beforeHandback.Contains("\"carProxy\":false") && beforeHandback.Contains("\"trailerProxy\":false"), "joint and both roles share one bridge snapshot");
                HitchBeforeOwner(car, old, 3); car.OwnerId = car.HitchAuthorityOwner = old; car.Proxy = true;
                var afterHandback = TestHitchState(20);
                check(tr.Root.attachable.J == null && afterHandback.Contains("\"joint\":false") && afterHandback.Contains("\"carProxy\":true"), "handback destroys the joint before proxy conversion");
                check(beforeHandback.Contains("\"joint\":true") && car.Proxy, "old hitchstate plus new entities reproduces the script assertion despite synchronous joint removal");

                // Replaying the attach on a display copy keeps logical agreement without a constraint.
                MyId = old; car.Proxy = tr.Proxy = true; tr.OwnerId = 1 - old;
                _applyingAttach = true; tr.Root.attachable.Attach(slot); _applyingAttach = false;
                check(tr.Root.attachable.attached && tr.Root.attachable.J == null, "proxy replay keeps hitch metadata with no joint");
                Requests.Clear(); PlayerActing = true;
                check(!HitchDetach(tr.Root.attachable) && Requests.Count == 1 && tr.Root.attachable.attached, "non-owner disconnect requests owner and leaves copy unchanged");
                var disconnect = Reader(Requests[0]); var op = ReadHitch(disconnect);
                check(!op.Connect && op.Car == 10 && op.Trailer == 20, "disconnect addresses both entities");
                PlayerActing = false; check(!HitchDetach(tr.Root.attachable), "automatic proxy detach is suppressed"); PlayerActing = true;
            }
            var transfer = new HitchAuthority.Transfer { From = 2, To = 1, Epoch = 8, Token = 9 };
            check(!transfer.Confirm(2, 7, 9) && !transfer.Confirm(2, 8, 8) && !transfer.Confirm(1, 8, 9), "stale epochs, tokens and wrong senders refuse freeze acknowledgement");
            check(transfer.Confirm(2, 8, 9) && !transfer.Confirm(2, 8, 9), "only one valid freeze acknowledgement");

            foreach (int requester in new[] { 1, 2 })
            {
                MyId = requester; ByNet.Clear(); Server.Clear(); Requests.Clear(); Packets.Clear();
                _hitchOps.Clear(); _hitchTransfers.Clear();
                var car = Make(10, 0, false); var tr = Make(20, 1, true);
                var slot = new partslotscript { tosaveitem = car.Root }; car.Root.partslotscripts.Add(slot);
                Server[10] = new SEnt { NetId = 10, OwnerId = 0, Car = true };
                Server[20] = new SEnt { NetId = 20, OwnerId = 1, Hitch = true };
                tr.Root.attachable.Attach(slot);
                check(Requests.Count == 1 && !tr.Root.attachable.attached && tr.Root.attachable.J == null, "trailer owner or third party cannot join their body to someone else's car");
                var request = Requests[0]; ServerPacket(request, requester);
                check(Packets.Count == 1 && Packets[0].target == 1, "freeze always routes to trailer owner, independently of requester");
                Packets.Clear(); _hitchOps.Clear(); _hitchTransfers.Clear();
                Server[10].Epoch++;
                ServerPacket(request, requester);
                var refused = Reader(Packets[0].data); ReadHitch(refused);
                check(refused.Str() == "stale epoch" && !_hitchTransfers.ContainsKey(20), "production server refuses stale epochs with a reason before any freeze");
                Server[10].Epoch--; Packets.Clear(); Near = false;
                ServerPacket(request, requester); Near = true;
                refused = Reader(Packets[0].data); ReadHitch(refused);
                check(refused.Str() == "edit reach", "production server checks reach on both endpoints");
            }
            var player = new fpscontroller { RB = new Rigidbody { velocity = new Vector3(50, 0, 0) } };
            mainscript.s = new mainscript { player = player };
            ByNet[20].HitchUntil = 1;
            var fall = typeof(HitchFallGuard).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            check(!(bool)fall.Invoke(null, new object[] { player }) && player.lastVelocity == 50, "handover speed spike cannot invoke the game's fall-death check");
            player.lastVelocity = 0; Time.time = 2;
            check((bool)fall.Invoke(null, new object[] { player }), "fall-death guard expires after handover");
            Time.time = 0; mainscript.s = null;
            // An unhitch can reach the server after it requested a freeze but before the acknowledgement.
            Packets.Clear(); Requests.Clear(); _hitchOps.Clear(); _hitchTransfers.Clear();
            var detached = ByNet[20]; MyId = detached.OwnerId = 1; detached.Proxy = false;
            Server[20].HitchCar = 10; CascadeHitches(Server[10]);
            var pendingFreeze = Packets[0].data; Packets.Clear();
            Server[20].HitchCar = Server[20].AttachParent = 0;
            ApplyHitchFreeze(Reader(pendingFreeze)); ServerPacket(Requests[0], 1);
            check(Server[20].OwnerId == 1 && !_hitchTransfers.ContainsKey(20), "unhitch racing a freeze keeps the current owner and completes the transfer fence");
            foreach (var packet in Packets)
            {
                if (packet.target != 1) continue;
                var r = Reader(packet.data);
                if (packet.data[0] == HitchFreeze) ApplyHitchFreeze(r);
                else if (packet.data[0] == Owner) { r.U32(); int owner = (int)r.VarU32(); Grant(detached, owner, r.U32()); }
                else if (packet.data[0] == HitchFrozen) ApplyHitchFrozen(r);
            }
            check(!detached.HitchWaiting && !detached.Root.RB.isKinematic, "cancelled hitch transfer unfreezes the unchanged owner instead of stranding it");
            var loading = new attachablescript { tosave = new tosaveitemscript(), utanfuto = new HitchFakes.Trailer() };
            PlayerActing = false; loading.Attach(new partslotscript()); PlayerActing = true;
            check(loading.attached && loading.point != null && loading.J == null, "save load before entity binding preserves the hitch record without creating an unowned joint");
            ByNet.Clear(); MyId = 1;
            var provisionalCar = Make(10, 1, false); var provisionalTrailer = Make(20, 1, true);
            provisionalTrailer.HitchAuthorityOwner = 0;
            var provisionalSlot = new partslotscript { tosaveitem = provisionalCar.Root }; provisionalCar.Root.partslotscripts.Add(provisionalSlot);
            PlayerActing = false; provisionalTrailer.Root.attachable.Attach(provisionalSlot); PlayerActing = true;
            HitchRestore(provisionalTrailer);
            check(provisionalTrailer.Root.attachable.J == null, "provisional pickup authority cannot create a hitch even when both local bodies are dynamic");
            provisionalTrailer.HitchAuthorityOwner = 1; HitchRestore(provisionalTrailer);
            check(provisionalTrailer.Root.attachable.J != null, "server-confirmed authority permits restoration");
            RecoveryTests(check);
        }

        private static void RecoverySetup(int carOwner, int trailerOwner, bool hitched)
        {
            MyId = 0; Time.realtimeSinceStartup = 0;
            Server.Clear(); ByNet.Clear(); Packets.Clear(); Requests.Clear(); Known.Clear();
            _hitchOps.Clear(); _hitchTransfers.Clear(); _hitchCommands.Clear(); _hitchDeparted.Clear();
            var car = Make(10, carOwner, false); var tr = Make(20, trailerOwner, true);
            var slot = new partslotscript { tosaveitem = car.Root }; car.Root.partslotscripts.Add(slot);
            Server[10] = new SEnt { NetId = 10, OwnerId = carOwner, Car = true, AttachKids = new HashSet<uint>() };
            Server[20] = new SEnt { NetId = 20, OwnerId = trailerOwner, Hitch = true, HasState = true,
                Pos = new Vector3d(40, 0, 3), Rot = Quaternion.identity };
            if (hitched)
            {
                Server[20].HitchCar = Server[20].AttachParent = 10; Server[20].LastAttach = new byte[] { 1 };
                Server[10].AttachKids.Add(20);
                var at = tr.Root.attachable; at.slot = slot; at.point = slot.T; at.attached = true; slot.parts.Add(at);
            }
        }

        private static void RecoverPackets(int player)
        {
            MyId = player;
            foreach (var packet in Packets.ToArray())
            {
                if (packet.target != player) continue;
                var r = Reader(packet.data);
                switch (packet.data[0])
                {
                    case HitchFreeze: ApplyHitchFreeze(r); break;
                    case Owner: { uint net = r.U32(); int owner = (int)r.VarU32(); Grant(ByNet[net], owner, r.U32()); break; }
                    case HitchFrozen: ApplyHitchFrozen(r); break;
                    case HitchCommand: ApplyHitchCommand(r); break;
                    case DetachSync:
                        // The existing receiver unmounts both loaded and far-store copies (SyncControlsTest covers it).
                        var tr = ByNet[r.U32()];
                        if (tr.Root.attachable.attached) Unmount(tr.Root.attachable, true);
                        break;
                }
            }
        }

        private static void RecoveryTests(Action<bool, string> check)
        {
            string entities = File.ReadAllText("src/TLDRevamp/Net/Entities.cs");
            int silentStart = entities.IndexOf("internal static int ServerTakeSilent(", StringComparison.Ordinal);
            int leaveStart = entities.IndexOf("public static void ServerOnLeave(", silentStart, StringComparison.Ordinal);
            check(entities.Substring(silentStart, leaveStart - silentStart).Contains("CascadeOwner(se);"), "silent takeover already cascades trailer ownership");
            string lifecycle = File.ReadAllText("src/TLDRevamp/Net/Entities.Lifecycle.cs");
            int removeStart = lifecycle.IndexOf("internal static void ServerRemoveItem(", StringComparison.Ordinal);
            int releaseAt = lifecycle.IndexOf("RemoveHitches(net);", removeStart, StringComparison.Ordinal);
            check(releaseAt >= removeStart && releaseAt < lifecycle.IndexOf("Server.Remove(net);", removeStart, StringComparison.Ordinal), "production deletion releases hitches before removing the tow entity");

            // Silent car takeover already cascades, but the silent trailer owner never acknowledges its freeze.
            RecoverySetup(0, 1, true);
            CascadeOwner(Server[10]); var transfer = _hitchTransfers[20];
            Time.realtimeSinceStartup = HitchTimeoutS - 0.01f; ServerHitchTick();
            check(Server[20].OwnerId == 1 && _hitchTransfers.ContainsKey(20), "silent takeover waits for the freeze until its deadline");
            HitchTransferTimeout = false; Time.realtimeSinceStartup = HitchTimeoutS; ServerHitchTick();
            check(Server[20].OwnerId == 1, "timeout A/B switch preserves the old wait"); HitchTransferTimeout = true;
            ServerHitchTick();
            check(Server[20].OwnerId == 0 && Server[20].Epoch == 2 && !_hitchTransfers.ContainsKey(20), "silent trailer follows car after timeout with an epoch fence");
            var fallback = Reader(Packets.Find(p => p.target == 0 && p.data[0] == HitchFrozen).data);
            fallback.U32(); fallback.U32(); fallback.U32();
            check(fallback.Bool() && fallback.F64() == 40 && fallback.F64() == 0 && fallback.F64() == 3,
                "timeout delivers the last server pose reliably after the grant");
            ReadRot(fallback);
            check(fallback.F32() == 0 && fallback.F32() == 0 && fallback.F32() == 0, "timeout never replays stale motion");
            var ack = new NetWriter(); ack.U8(HitchFrozen); ack.U32(20); ack.U32(transfer.Epoch); ack.U32(transfer.Token); ack.Bool(false);
            int sent = Packets.Count; ServerPacket(Copy(ack), 1);
            check(Packets.Count == sent && Server[20].OwnerId == 0, "late silent-owner acknowledgement cannot grant twice");
            RecoverPackets(0);
            check(!ByNet[20].HitchWaiting && !ByNet[20].Root.RB.isKinematic && ByNet[20].Root.attachable.J != null, "fallback thaws trailer and restores the common-owner hitch");

            // The frozen source can acknowledge, but the destination has never been offered these objects at 3 km.
            RecoverySetup(2, 1, false); Known[2] = new HashSet<uint>();
            HitchEnsureKnown = false; Server[20].HitchCar = 10; HitchOwnerKnows(Server[20], 2); Server[20].HitchCar = 0;
            check(Packets.Count == 0 && KnownBy(2).Count == 0, "interest A/B switch leaves dependencies to ordinary offers"); HitchEnsureKnown = true;
            var op = new HitchOp { Trailer = 20, Car = 10, TrailerEpoch = 1, CarEpoch = 1, Slot = 0, Connect = true };
            var request = new NetWriter(); request.U8(HitchRequest); WriteHitch(request, op); ServerPacket(Copy(request), 0);
            var pending = _hitchTransfers[20]; Packets.Clear();
            ack = new NetWriter(); ack.U8(HitchFrozen); ack.U32(20); ack.U32(1); ack.U32(pending.Token); ack.Bool(false);
            ServerPacket(Copy(ack), 1);
            var toOwner = Packets.FindAll(p => p.target == 2);
            check(toOwner.Count >= 6 && toOwner[0].data[0] == Add && Reader(toOwner[0].data).U32() == 10 &&
                toOwner[1].data[0] == Add && Reader(toOwner[1].data).U32() == 20 && toOwner[2].data[0] == HitchFreeze,
                "out-of-interest owner receives both Add dependencies before the prepare/grant/pose/command");
            check(KnownBy(2).Contains(10) && KnownBy(2).Contains(20), "direct hitch dependencies become known outside the interest radius");
            Packets.Clear(); HitchOwnerKnows(Server[20], 2);
            check(Packets.Count == 0, "already-known hitch dependencies are not resent");
            // A distant destination can still have only stored copies and never run its command. Release on deadline.
            Server[20].AttachParent = 10; Server[20].LastAttach = new byte[] { 1 }; Server[10].AttachKids.Add(20);
            ApplyHitchCommand(Reader(toOwner[toOwner.Count - 1].data));
            ByNet.Remove(10); Time.realtimeSinceStartup = HitchTimeoutS; HitchCommandTick();
            check(_hitchCommands.Count == 0, "missing command dependencies cannot leave a permanent local queue");
            ServerHitchTick();
            check(!_hitchOps.ContainsKey(20) && Server[20].HitchCar == 0 && Server[20].AttachParent == 0 && Server[20].LastAttach == null && Server[20].Epoch == 3,
                "unanswered destination command releases the hitch and invalidates delayed commands");

            foreach (bool transferring in new[] { false, true })
            {
                RecoverySetup(transferring ? 0 : 1, 1, true);
                var tr = ByNet[20]; MyId = 1; tr.Proxy = false; tr.HitchWaiting = true; HitchFreezeBodies(tr);
                if (transferring) CascadeHitches(Server[10]);
                _hitchOps[20] = new HitchOp { Trailer = 20, Car = 10, Connect = true };
                Packets.Clear();
                HitchReleaseOnRemove = false; RemoveHitches(10);
                check(Server[20].HitchCar == 10, "deletion A/B switch preserves old hitch"); HitchReleaseOnRemove = true;
                RemoveHitches(10);
                check(Server[20].HitchCar == 0 && Server[20].AttachParent == 0 && Server[20].LastAttach == null && Server[10].AttachKids.Count == 0,
                    "deleted tow car drops live and replayed hitch metadata");
                check(_hitchTransfers.Count == 0 && _hitchOps.Count == 0 && Server[20].OwnerId == 1, "deletion cancels pending transfer and retains the live trailer owner");
                // A queued old-epoch hitch must not reattach after the deletion recovery.
                _hitchCommands[20] = new HitchOp { Trailer = 20, Car = 10, TrailerEpoch = 1, CarEpoch = 1, Connect = true, Until = 5 };
                RecoverPackets(1); HitchCommandTick();
                check(!tr.Root.attachable.attached && tr.Root.attachable.slot == null && tr.Root.attachable.J == null && !tr.HitchWaiting && !tr.Root.RB.isKinematic,
                    "deletion unmounts and thaws the trailer without executing the stale command");
                Server.Remove(10);
                // ServerOnLeave skips only HitchCar != 0: this released trailer now takes its ordinary owner-to-host path.
                check(Server[20].OwnerId == 1 && Server[20].HitchCar == 0 && entities.Substring(leaveStart).Contains("se.OwnerId == playerId && se.HitchCar == 0"),
                    "released trailer is eligible for ordinary leave recovery");
                check(File.ReadAllText("src/TLDRevamp/Net/Entities.Pickup.cs").Contains("if (se.HitchCar != 0)") && Server[20].HitchCar == 0 && !Server[20].Held && !Server[20].Stored,
                    "released trailer no longer trips the pickup hitch guard");
            }

            RecoverySetup(0, 1, true); _hitchDeparted.Add(1); RemoveHitches(10);
            check(Server[20].OwnerId == 0, "deletion with an already departed trailer owner falls back to host");
            RecoverPackets(0);
            check(!ByNet[20].HitchWaiting && !ByNet[20].Root.attachable.attached, "departed-owner fallback leaves a loose live host trailer");

            // Unity may defer Transform updates after Rigidbody.position. Exercise translation and rotation together.
            foreach (bool receivedGrip in new[] { false, true })
            foreach (bool deferred in new[] { false, true })
            foreach (bool enabled in new[] { false, true })
            {
                RecoverySetup(0, 0, false); HitchTransferredGripPose = enabled;
                var tr = ByNet[20]; tr.Root.transform.position = new Vector3(10, 2, -8);
                tr.Root.transform.rotation = new Quaternion(0, 0, 1, 0);
                tr.Root.RB.DeferredTransform = deferred; tr.HitchWaiting = true; HitchFreezeBodies(tr);
                var q = new Quaternion(0, 0, 0.70710677f, 0.70710677f); var lp = new Vector3(2, 1, -3);
                tr.Root.gameObject.Extra = new attachgripscript { GripPos = new Transform {
                    position = tr.Root.transform.TransformPoint(lp), rotation = tr.Root.transform.rotation } };
                var pose = new NetWriter(); pose.U8(HitchFrozen); pose.U32(20); pose.U32(1); pose.U32(0); pose.Bool(true);
                pose.F64(100); pose.F64(30); pose.F64(-40); WriteRot(pose, q); pose.F32(0); pose.F32(0); pose.F32(0);
                pose.Bool(receivedGrip); pose.F32(lp.x); pose.F32(lp.y); pose.F32(lp.z); WriteRot(pose, Quaternion.identity);
                ApplyHitchFrozen(Reader(Copy(pose)));
                var expected = new Vector3(100, 30, -40) + q * lp;
                bool correct = (tr.Root.attachable.transform.position - expected).sqrMagnitude < 0.0001f;
                check(enabled ? correct : correct == (receivedGrip && !deferred), "drawbar uses destination pose with rotation; received=" + receivedGrip + ", deferred=" + deferred + ", enabled=" + enabled);
            }
            HitchTransferredGripPose = true; Time.realtimeSinceStartup = 0;
        }
    }
}

namespace HitchFakes
{
    public class Item : Component
    {
        public Attachment attachable; public Car car; public Component P;
        public Rigidbody RB; public List<Slot> partslotscripts = new List<Slot>();
        public Item() { RB = new Rigidbody(); RB.gameObject = gameObject; gameObject.Body = RB; }
    }
    public class Attachment : Component
    {
        public Item tosave; public Trailer utanfuto; public Slot slot; public bool attached; public Transform point; public Joint J;
        public Rigidbody RB;
        public Attachment() { RB = new Rigidbody(); RB.gameObject = gameObject; gameObject.Body = RB; }
        public void Attach(Slot s) => Entities.GameAttach(this, s);
        public void Attach(Transform t) => Entities.GameAttach(this, t);
        public void Detach() { attached = false; slot.parts.Remove(this); slot = null; J = null; }
    }
    public class Slot : Component
    {
        public Item tosaveitem; public Transform T => transform; public List<Attachment> parts = new List<Attachment>();
        public bool hasPart() => parts.Count != 0; public bool CanCraft(Attachment at) => true;
        public void Craft(Attachment p) { }
    }
    public class Car : Component { }
    public class Grip : Component { public Transform GripPos; }
    public class Lights : Component { }
    public class Brake { public int currentTurnState; }
    public class Trailer { public Brake brake; public void RefreshCenterOffMass(bool attached, Lights lights) { } }
    public class Record { public int prefabID; }
    public class Database { public static Database s; public GameObject Item(int id) => null; }
    public class Player : Component
    {
        public Rigidbody RB; public Component pickedUp; public float lastVelocity, currentVelocity, deathVelocityChange = 30;
        public void Drop() { pickedUp = null; } public void FallDamage() { }
    }
    public class Main
    {
        public static Main s; public Player player;
        public static Vector3d GlobalFromUnityPos(Vector3 p) => new Vector3d(p.x, p.y, p.z);
        public static Vector3 UnityPosFromGlobal(Vector3d p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
    }
}
namespace TLDRevamp
{
    internal static class SessionLog { internal static void Event(string kind, uint net = 0, uint other = 0, int item = -1, int slot = -1, int from = -1, int to = -1, string result = null, uint epoch = 0, int count = 0, float value = 0) { } }
    internal static class Json { internal static string Str(string s) => "\"" + s + "\""; }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string s) { } public HarmonyPatch(Type t, string s, Type[] args) { } }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int n) { } } public static class Priority { public const int First = 0; }
}
namespace UnityEngine
{
    public class Object { public bool Destroyed; public static void DestroyImmediate(Object o) { o.Destroyed = true; } }
    public class Component : Object
    {
        public string name = "hitch"; public GameObject gameObject = new GameObject(); public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool all) where T : class => gameObject.Body is T body ? new[] { body } : Array.Empty<T>();
    }
    public class GameObject
    {
        public Rigidbody Body; public object Extra; public Transform transform = new Transform(); public T GetComponent<T>() where T : class => Body as T ?? Extra as T;
    }
    public class Transform
    {
        public Vector3 position; public Quaternion rotation = Quaternion.identity;
        public Vector3 TransformPoint(Vector3 p) => position + rotation * p; public Vector3 InverseTransformPoint(Vector3 p) => Quaternion.Inverse(rotation) * (p - position);
        public void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
        public T GetComponentInParent<T>() where T : class => null;
    }
    public class Rigidbody : Component
    {
        public bool isKinematic; public Vector3 velocity, angularVelocity;
        public bool DeferredTransform;
        public Vector3 position { get => transform.position; set { if (!DeferredTransform) transform.position = value; } }
        public Quaternion rotation { get => transform.rotation; set { if (!DeferredTransform) transform.rotation = value; } }
        public Vector3 GetPointVelocity(Vector3 p) => velocity;
    }
    public class Joint : Object { }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 zero => default; public float sqrMagnitude => x * x + y * y + z * z; public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    public struct Quaternion
    {
        public float x, y, z, w; public Quaternion(float a, float b, float c, float d) { x = a; y = b; z = c; w = d; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        private System.Numerics.Quaternion Value => new System.Numerics.Quaternion(x, y, z, w);
        private static Quaternion From(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static Quaternion Inverse(Quaternion q) => From(System.Numerics.Quaternion.Inverse(q.Value));
        public static Quaternion operator *(Quaternion a, Quaternion b) => From(a.Value * b.Value);
        public static Vector3 operator *(Quaternion q, Vector3 p)
        { var v = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(p.x, p.y, p.z), q.Value); return new Vector3(v.X, v.Y, v.Z); }
    }
    public static class Time { public static float time, realtimeSinceStartup; }
    public static class Mathf { public static float Abs(float f) => MathF.Abs(f); }
}
public struct Vector3d
{
    public double x, y, z; public Vector3d(double a, double b, double c) { x = a; y = b; z = c; }
}
