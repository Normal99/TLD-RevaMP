using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static SyncFakes.Attach Mounted(SyncFakes.Item it, bool inheritedProxy)
        {
            var at = new SyncFakes.Attach { tosave = it, InheritedProxy = inheritedProxy };
            at.slot = new SyncFakes.Slot { Part = at };
            it.attachable = at;
            it.transform.SetPositionAndRotation(new Vector3(1, 2, 3), new Quaternion(0, 0, 0, 1));
            return at;
        }

        private static NetReader Packet()
        {
            var buf = new byte[W.Len]; Array.Copy(W.Buf, buf, W.Len);
            return new NetReader { Buf = buf, Pos = 1, End = buf.Length };
        }

        private static void RemovalTests(Action<bool, string> check)
        {
            // Exercise the production UnCraft/Detach hooks, owner request handler, sender and receiver.
            // Only the game's mechanical unbolting and Unity objects are faked.
            foreach (int frameOwner in new[] { 1, 2 })
            foreach (int partOwner in new[] { 0, 1, 2, 3 })
            {
                ByNet.Clear(); Server.Clear(); Sent = 0; _handFor = Handed = null;
                string label = "frame p" + frameOwner + ", headlight p" + partOwner;
                var frame = new SyncFakes.Item(); var lamp = new SyncFakes.Item();
                var fe = new Ent { NetId = 10, Root = frame, OwnerId = frameOwner, Proxy = frameOwner != MyId };
                fe.Items.AddRange(new[] { frame, lamp }); fe.Split = new System.Collections.Generic.HashSet<int> { 1 };
                var pe = new Ent { NetId = 20, Epoch = 4, Root = lamp, OwnerId = partOwner, Proxy = partOwner != MyId };
                pe.Items.Add(lamp); ByNet[10] = fe; ByNet[20] = pe;
                Server[10] = new SEnt { OwnerId = frameOwner };
                Server[20] = new SEnt { OwnerId = partOwner, AttachParent = 10 };
                var at = Mounted(lamp, fe.Proxy);
                var slot = at.slot;
                bool local = slot.UnCraft(false);
                if (partOwner == MyId)
                {
                    check(local && !at.attached && !lamp.Crafted && slot.Part == null, label + ": owner fully unbolts locally");
                    check(Sent == 1 && W.Buf[0] == DetachSync, label + ": owner broadcasts one detach, no request");
                }
                else
                {
                    check(!local && at.attached && lamp.Crafted && slot.Part == at, label + ": proxy remains bolted until owner answers");
                    check(Sent == 1 && W.Buf[0] == DetachReq && _handFor == lamp, label + ": player request and handoff recorded");
                    var req = Packet(); uint net = req.U32(); int idx = (int)req.VarU32();
                    check(net == 20 && idx == 0, label + ": request addresses headlight, not frame or stale membership");
                    ServerDetachReq(MyId, net, idx);
                    check(Target == partOwner && W.Buf[0] == DetachReq, label + ": forwarded to actual part owner (p0 is host)");
                    // Switch to the part owner's copy; the frame may still be a proxy on that machine.
                    pe.Proxy = false; fe.Proxy = frameOwner != partOwner; at.InheritedProxy = fe.Proxy;
                    PlayerActing = false;
                    ApplyDetachReq(0, net, idx);
                    PlayerActing = true;
                    check(!at.attached && !lamp.Crafted && slot.Part == null && W.Buf[0] == DetachSync,
                        label + ": network request runs full unbolt and broadcasts outside player scope");
                }
                var sync = Packet();
                check(sync.U32() == 20 && sync.U32() == 4, label + ": detach identifies the existing headlight entity and epoch");
                sync.Pos = 1;
                // Switch to the other machine's still-bolted copy, replay the production receiver.
                var copy = new SyncFakes.Item(); var ca = Mounted(copy, true); var cs = ca.slot;
                pe.Root = copy; pe.Items[0] = copy; pe.Proxy = true;
                if (partOwner != MyId) _handFor = copy;
                Sent = 0; ApplyDetachSync(sync);
                check(!ca.attached && !copy.Crafted && cs.Part == null && Sent == 0 && !_applyingDetach,
                    label + ": peer converges through full unbolt without echo/request");
                check(copy.transform.position.x == 1 && copy.transform.position.y == 2 && copy.transform.position.z == 3,
                    label + ": peer gets owner's detached pose");
                if (partOwner != MyId) check(Handed == copy, label + ": requested part reaches handoff");
            }

            // An ownership message can overtake a request from the new owner's still-proxy copy.
            ByNet.Clear(); Server.Clear(); Sent = 0;
            var raceLamp = new SyncFakes.Item(); var raceAt = Mounted(raceLamp, false);
            var race = new Ent { NetId = 30, Root = raceLamp, Proxy = true, OwnerId = 2 };
            race.Items.Add(raceLamp); ByNet[30] = race;
            Server[30] = new SEnt { OwnerId = MyId };
            check(!raceAt.slot.UnCraft(false) && raceAt.attached, "new frame owner's proxy copy initially suppresses dismount");
            var raceReq = Packet(); uint raceNet = raceReq.U32(); int raceIdx = (int)raceReq.VarU32();
            int sentBefore = Sent; ServerDetachReq(MyId, raceNet, raceIdx);
            check(Sent == sentBefore + 1 && Target == MyId, "server must not drop request when requester became owner");
            race.Proxy = false; race.OwnerId = MyId; // reliable Owner arrives before forwarded request
            PlayerActing = false; ApplyDetachReq(0, raceNet, raceIdx); PlayerActing = true;
            check(!raceAt.attached && !raceLamp.Crafted && W.Buf[0] == DetachSync, "new owner applies and broadcasts previously suppressed dismount");

            foreach (bool proxy in new[] { false, true })
            {
                ByNet.Clear(); Sent = 0;
                var frame = new SyncFakes.Item(); var lamp = new SyncFakes.Item();
                var group = new Ent { NetId = 40, Epoch = 3, Root = frame, Proxy = proxy, OwnerId = proxy ? 2 : MyId };
                group.Items.AddRange(new[] { frame, lamp }); ByNet[40] = group;
                var at = Mounted(lamp, proxy);
                bool result = at.slot.UnCraft(false);
                check(result != proxy && W.Buf[0] == (proxy ? DetachReq : PartOff), "nested unsplit group uses frame authority for proxy=" + proxy);
                var r = Packet(); check(r.U32() == 40, "nested member reports its containing group");
                if (!proxy) check(r.U32() == 3, "nested owner PartOff carries group epoch");
                check(r.VarU32() == 1, "nested member keeps group member index");
            }
            ByNet.Clear(); Server.Clear();
        }
    }
}
