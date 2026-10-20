using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static void TestPhysLock()
        {
            void Check(bool ok, string why) => IdleTrafficTest.Check(ok, "physlock: " + why);
            var mount = new TestMount();
            var baseline = new PhysLockPose();
            baseline.Sent(false, null, 0, 1, new Vector3d(10, 0, 0), Quaternion.identity);
            // The item moves less than the visible threshold after its last loose pose, then loses its body.
            var rest = new Vector3d(10.005, 0, 0);
            Check(!baseline.Moved(rest, Quaternion.identity) && baseline.Transition(true, mount, 0, 1), "locking while moving forces state even below pose threshold");
            Check(FinalState(true, 40, true, BandwidthRules.Quiet.None, true, false) && !FinalState(true, 0, true, BandwidthRules.Quiet.None, true, false) &&
                  !FinalState(true, 0, false, BandwidthRules.Quiet.None, true, false), "only a framed lock produces moving finals, including with waste fixes off");
            baseline.Sent(true, mount, 0, 1, rest, Quaternion.identity);
            Check(!baseline.Transition(true, mount, 0, 1) && !baseline.Moved(rest, Quaternion.identity), "confirmed world lock becomes quiet");
            Check(baseline.Moved(new Vector3d(11, 0, 0), Quaternion.identity), "unshared moving parent/push changes world pose");
            Check(baseline.Transition(false, null, 0, 1), "unlock forces state before the first physics step");
            baseline.Sent(false, null, 0, 1, rest, Quaternion.identity);
            var tail = new BandwidthRules.Tail();
            Check(BandwidthRules.StateQuiet(ref tail, 3, false, baseline.Moved(new Vector3d(12, 0, 0), Quaternion.identity), false, false, false, true, true, false, false, .05) == BandwidthRules.Quiet.None && tail.Active, "unlock and motion restart ordinary streaming");

            ByNet.Clear(); Server.Clear();
            var car = new Ent { NetId = 40, OwnerId = 1 };
            car.Items.Add(car.Root); ByNet.Add(car.NetId, car);
            mount.parentTosave = car.Root; mount.transform.parent = car.Root.transform;
            // A stored child's own containers must not shift the mount reference on another machine.
            car.Root.Add(new[] { new TestMount { parentTosave = new TestItem() }, mount });
            var cargo = new Ent { NetId = 41, OwnerId = 1, Proxy = true };
            cargo.Root.P = new TestPickup { transform = cargo.Root.transform };
            cargo.Items.Add(cargo.Root); ByNet.Add(cargo.NetId, cargo);
            cargo.Root.transform.position = new Vector3(1, 2, 3);
            Check(LockedFrame(cargo, mount, cargo.Root.transform, out var lp, out var lq, out int item, out int index) == 40 && item == 0 && index == 0, "exact shared parent and stable container reference");
            baseline.Sent(true, mount, 40, 1, new Vector3d(lp.x, lp.y, lp.z), lq);
            for (int tick = 1; tick <= 1200; tick++)
            {
                car.Root.transform.position = new Vector3(tick, 0, 0);
                car.Root.transform.rotation = Quaternion.Euler(0, tick % 360, 0);
                cargo.Root.transform.position = car.Root.transform.position + car.Root.transform.rotation * lp;
                cargo.Root.transform.rotation = car.Root.transform.rotation * lq;
                LockedFrame(cargo, mount, cargo.Root.transform, out var local, out var rotation, out _, out _);
                Check(!baseline.Moved(new Vector3d(local.x, local.y, local.z), rotation), "moving/turning car carries quiet locked pose");
            }
            Check(baseline.Moved(new Vector3d(lp.x + .1, lp.y, lp.z), lq), "local displacement sends while locked");
            Check(baseline.Transition(true, mount, 40, 2), "ownership epoch forces locked confirmation");
            Check(baseline.Transition(true, new TestMount(), 40, 1) && baseline.Transition(true, mount, 42, 1), "container/frame switches force confirmation");

            cargo.LockLocalPos = lp; cargo.LockLocalRot = lq;
            cargo.MovedByBody = true;
            ApplyPoseLock(cargo, true, 40, item, index);
            Check(cargo.Root.P.physlock == mount && cargo.Root.transform.parent == mount.transform && !cargo.MovedByBody, "receiver stores in owner's container and clears obsolete body motion");
            int mountScans = car.Root.MountScans;
            for (int tick = 0; tick < 100; tick++) TickPoseLock(cargo);
            Check(car.Root.MountScans == mountScans, "unchanged framed locks never rescan mount arrays");
            cargo.Root.P.UnPhysicsLock();
            var recreatedBody = new Rigidbody(); cargo.Root.Add(new[] { recreatedBody });
            TickPoseLock(cargo);
            Check(cargo.Root.P.physlock == mount && cargo.Root.transform.parent == mount.transform && recreatedBody.isKinematic,
                "next tick repairs game UnPhysicsLock and freezes any recreated proxy body, without another state");
            cargo.ReceivedStateEpoch = 0; cargo.Root.P.UnPhysicsLock(); TickPoseLock(cargo);
            RebindPoseLocks(cargo); RebindPoseLocks(car);
            Check(cargo.Root.P.physlock == null, "neither tick nor item/parent rebind enforces a stale epoch");
            cargo.ReceivedStateEpoch = cargo.Epoch;
            EnsurePoseLock(cargo);
            cargo.CarriedBy = 40;
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 1, Pos = new Vector3d(lp.x, lp.y, lp.z), Rot = lq }, 1);
            EvalShown(cargo, 1.1, .05f, out var rendered, out var renderedRot);
            var expected = car.Root.transform.position + car.Root.transform.rotation * lp;
            Check((rendered - new Vector3d(expected.x, expected.y, expected.z)).sqrMagnitude < .0001 && Quaternion.Angle(renderedRot, car.Root.transform.rotation * lq) < .01, "receiver draws a quiet item in its current car frame");
            TakeOverCarried(cargo);
            Check(cargo.CarriedBy == 0 && cargo.Root.P.physlock == mount, "ownership drops interpolation frame while retaining the actual game lock");
            cargo.Epoch++;
            SetProxy(cargo, false);
            Check(cargo.Root.P.physlock == mount && !cargo.PoseLocked && cargo.LockFrame == 0 && cargo.LockItem == -1 && cargo.LockMount == -1,
                "real takeover retains the game lock and discards receiver fields even after the epoch advances");
            cargo.Root.P.UnPhysicsLock(); cargo.Root.transform.position = new Vector3(50, 0, 0);
            SetProxy(cargo, true); RebindPoseLocks(cargo); RebindPoseLocks(car);
            Check(cargo.Root.P.physlock == null && cargo.Root.transform.position.x == 50, "returning proxy cannot reapply the previous owner's trunk pose");
            var stale = new Ent { PoseLocked = true, LockFrame = 40, LockItem = item, LockMount = index, ReceivedStateEpoch = 0 };
            stale.Root.P = new TestPickup { transform = stale.Root.transform }; stale.Items.Add(stale.Root);
            stale.Root.transform.position = new Vector3(50, 0, 0);
            SetProxy(stale, true);
            Check(stale.Root.P.physlock == null && stale.Root.transform.position.x == 50, "SetProxy itself rejects received lock fields from an older epoch");
            cargo.ReceivedStateEpoch = cargo.Epoch;
            ApplyPoseLock(cargo, true, 40, item, index);
            ApplyPoseLock(cargo, false, 0, -1, -1);
            Check(cargo.Root.P.physlock == null && cargo.Root.transform.parent == null && !cargo.PoseLocked, "unlock releases receiver immediately");

            // Lock state may arrive before its parent. A later placement resolves the retained reference.
            ByNet.Remove(40);
            ApplyPoseLock(cargo, true, 40, item, index);
            Check(cargo.Root.P.physlock == null, "missing parent keeps a world fallback");
            ByNet.Add(40, car); RebindPoseLocks(car);
            Check(cargo.Root.P.physlock == mount, "parent arriving later restores the lock");

            // A separately attached door owns this mount, but streams no shared frame for it.
            var door = new Ent { NetId = 42, OwnerId = 1 };
            door.Root.attachable = new TestAttach { attached = true, isPart = true, slot = new TestSlot(), point = new Transform() };
            door.Root.attachable.point.Add(new TestCar());
            door.Items.Add(door.Root); ByNet.Add(42, door);
            var glovebox = new TestMount { parentTosave = door.Root };
            glovebox.transform.parent = door.Root.transform; door.Root.transform.parent = car.Root.transform;
            door.Root.Add(new[] { glovebox }); glovebox.Store(cargo.Root.P, true);
            int reliable = 0; PoseInterpolator lockedIp = null;
            for (int tick = 0; tick < 200; tick++)
            {
                car.Root.transform.position = new Vector3(tick, 0, 0);
                car.Root.transform.rotation = Quaternion.Euler(0, tick * .5f, 0);
                door.Root.transform.position = car.Root.transform.position;
                door.Root.transform.rotation = car.Root.transform.rotation;
                cargo.Root.transform.position = car.Root.transform.position + car.Root.transform.rotation * new Vector3(1, 2, 0);
                cargo.Root.transform.rotation = car.Root.transform.rotation;
                uint frame = LockedFrame(cargo, glovebox, cargo.Root.transform, out _, out _, out int li, out int lm);
                Check(frame == 0, "attached-container uses world fallback");
                if (tick > 0) Check(baseline.Moved(cargo.Root.transform.position, cargo.Root.transform.rotation), "driving and turning the vehicle requires ordinary world pose samples");
                bool change = baseline.Transition(true, glovebox, frame, 1);
                bool final = FinalState(true, frame, true, BandwidthRules.Quiet.None, true, false);
                if (change || final) reliable++;
                baseline.Sent(true, glovebox, frame, 1, cargo.Root.transform.position, cargo.Root.transform.rotation);
                ApplyPoseLock(cargo, true, frame, li, lm);
                if (tick == 0) lockedIp = cargo.Ip;
                Check(ReferenceEquals(lockedIp, cargo.Ip) && cargo.Root.P.physlock == glovebox, "moving world lock keeps interpolator and local game lock across states");
            }
            Check(reliable == 1, "moving attached-container lock sends one reliable transition, then ordinary pose cadence");
            door.Root.attachable.attached = false;
            cargo.Root.transform.position = door.Root.transform.position + new Vector3(1, 0, 0);
            Check(LockedFrame(cargo, glovebox, cargo.Root.transform, out _, out _, out _, out _) == 42, "joint detach without a hierarchy change invalidates negative frame cache");
            door.Root.attachable.attached = true;
            // Different owners prevent the viewer's own container scan from pre-storing this copy.
            door.OwnerId = 2; cargo.Root.P.UnPhysicsLock();
            var hit = new TestHit(); hit.Add(new TestLayer { p = cargo.Root.P }); glovebox.cols = new[] { hit };
            UnityEngine.Object.Mounts = new[] { new TestMount { noPhysicsLock = true }, glovebox };
            Check(cargo.Root.P.physlock == null, "frame-zero takeover starts with no viewer game lock");
            int syncs = Physics.Syncs;
            cargo.Epoch++; SetProxy(cargo, false);
            Check(!cargo.Proxy && cargo.Root.P.physlock == glovebox && cargo.Root.transform.parent == glovebox.transform,
                "real SetProxy takeover resolves the game's layerScript pickup reference with different owners");
            Check(Physics.Syncs == syncs + 1, "takeover synchronizes the interpolated collider before its one-time overlap scan");
            SetProxy(cargo, true); cargo.ReceivedStateEpoch = cargo.Epoch; cargo.Root.P.UnPhysicsLock();
            uint worldFrame = LockedFrame(cargo, glovebox, cargo.Root.transform, out var worldLocal, out var worldRotation, out int worldItem, out int worldMount);
            Check(worldFrame == 0 && WorldLockContainer(cargo) == 42, "non-streaming attached container supplies an address without becoming a pose frame");
            var worldPacket = new NetWriter();
            WriteStateHead(worldPacket, 41, cargo.Epoch, 1, 3, default, Quaternion.identity, Vector3.zero, false,
                localPos: worldLocal, localRot: worldRotation, locked: true, lockItem: worldItem, lockMount: worldMount, lockContainer: 42);
            worldPacket.U8(77);
            var worldBytes = FinalSnapshot(new NetReader { Buf = worldPacket.Buf, End = worldPacket.Len }, true);
            ReplayLockedState(worldPacket, worldBytes, new Vector3d(500, 0, 0), Quaternion.identity);
            var worldReader = new NetReader { Buf = worldPacket.Buf, Pos = 1, End = worldPacket.Len };
            ReadEntityHead(worldReader, false, out _, out _, out _, out _, out _, out _, out _, out _, out bool worldHeld, out _);
            Check(!worldReader.Bad && !worldHeld && ReadCarriedBy == 0 && ReadLockContainer == 42 &&
                ReadLockItem == worldItem && ReadLockMount == worldMount && worldReader.U8() == 77, "world-lock address survives codec and cached replay without a held/carried flag");
            cargo.LockLocalPos = ReadLocalPos; cargo.LockLocalRot = ReadLocalRot;
            ApplyPoseLock(cargo, true, 0, ReadLockItem, ReadLockMount, ReadLockContainer);
            Check(cargo.Root.P.physlock == null, "address alone does not let the viewer's container store another owner's item");
            cargo.Root.transform.position += new Vector3(10, 0, 0); glovebox.cols = Array.Empty<TestHit>();
            syncs = Physics.Syncs; cargo.Epoch++; SetProxy(cargo, false);
            Check(cargo.Root.P.physlock == glovebox && Physics.Syncs == syncs && cargo.CarriedBy == 0 && cargo.LockContainer == 0 &&
                (cargo.Root.transform.position - (door.Root.transform.position + door.Root.transform.rotation * worldLocal)).sqrMagnitude < .0001,
                "takeover restores exact mount-local lock despite non-overlapping delayed world pose, then clears receiver address");
            for (int n = 1; n < worldBytes.Length - 1; n++)
            {
                var cut = new NetReader { Buf = worldBytes, Pos = 1, End = n };
                ReadEntityHead(cut, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                Check(cut.Bad, "truncated world-lock address is rejected");
            }
            SetProxy(cargo, true); cargo.ReceivedStateEpoch = cargo.Epoch;
            ApplyPoseLock(cargo, true, 0, -1, -1);
            var mounts = LockMounts(door.Root); var scans = door.Root.MountScans;
            Check(ReferenceEquals(mounts, LockMounts(door.Root)) && door.Root.MountScans == scans, "container mount arrays are cached");
            // The mount stays the same while its parent chain changes.
            glovebox.transform.parent = car.Root.transform; glovebox.parentTosave = car.Root;
            Check(LockedFrame(cargo, glovebox, cargo.Root.transform, out _, out _, out _, out _) == 0, "changed container cannot reuse an obsolete mount index");
            glovebox.transform.parent = door.Root.transform; glovebox.parentTosave = door.Root;
            ApplyPoseLock(cargo, false, 0, -1, -1);
            Check(cargo.Root.P.physlock == null, "explicit unlock still releases a frame-zero lock");
            // The role flip must clear locks even when the item is not loaded on this machine.
            var savedRoot = cargo.Root;
            ApplyPoseLock(cargo, true, 40, item, index); cargo.Root.P.UnPhysicsLock(); cargo.Root = null;
            SetProxy(cargo, false); SetProxy(cargo, true); cargo.Root = savedRoot;
            RebindPoseLocks(cargo); RebindPoseLocks(car);
            Check(!cargo.PoseLocked && cargo.Root.P.physlock == null, "dormant takeover cannot restore an earlier epoch's mount on placement");
            cargo.LockLocalPos = lp; cargo.LockLocalRot = lq;
            ApplyPoseLock(cargo, true, 40, item, index);

            var parent = new SEnt { NetId = 40, HasState = true, Pos = new Vector3d(5000, 0, 0), Rot = Quaternion.Euler(0, 90, 0) };
            var child = new SEnt { NetId = 41, LockFrame = 40, LockLocalPos = lp, LockLocalRot = lq };
            Server.Add(40, parent); Server.Add(41, child); ServerLockedPose(child);
            var offset = parent.Rot * lp;
            Check(Math.Abs(child.Pos.x - (5000 + offset.x)) < .001 && child.Where.x == child.Pos.x, "late join and interest use current parent world pose");
            car.FarPos = parent.Pos; car.FarRot = parent.Rot; car.Root = null;
            Check(LockedWorld(cargo, out var far, out _) && (far - child.Pos).sqrMagnitude < .0001, "far child follows stored-out parent in world space");
            UpdateFarRecord(cargo, far, child.Rot); cargo.FarDirty = false;
            for (int tick = 0; tick < 100; tick++) UpdateFarRecord(cargo, far, child.Rot);
            Check(!cargo.FarDirty, "parked dormant locked child does not repeatedly dirty far records");
            UpdateFarRecord(cargo, far + new Vector3d(.01, 0, 0), child.Rot);
            Check(cargo.FarDirty, "derived far movement dirties records");
            cargo.FarDirty = false;
            UpdateFarRecord(cargo, cargo.FarPos, Quaternion.Euler(0, 45, 0));
            Check(cargo.FarDirty, "derived far rotation dirties records");

            var w = new NetWriter();
            WriteStateHead(w, 41, 2, 4, 5, rest, Quaternion.identity, Vector3.zero, false,
                           carriedBy: 40, localPos: lp, localRot: lq, final: true, locked: true, lockItem: item, lockMount: index);
            w.U8(77); // opaque visual tail must survive replay
            var snapshot = FinalSnapshot(new NetReader { Buf = w.Buf, End = w.Len }, true);
            ReplayLockedState(w, snapshot, child.Pos, child.Rot);
            var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(r, false, out uint net, out uint epoch, out double stamp, out var world, out _, out var velocity, out _, out _, out bool lockHeld, out _);
            Check(!lockHeld, "physlock frame is not a held pickup");
            Check(net == 41 && epoch == 2 && stamp == 5 && ReadFinalState && ReadLockedState && ReadCarriedBy == 40 && ReadLockItem == item && ReadLockMount == index, "late join replays final lock, frame and container");
            Check((world - child.Pos).sqrMagnitude < .00001 && velocity.sqrMagnitude == 0 && r.U8() == 77 && !r.Bad, "replay refreshes stale world fallback and preserves visual state");
            var sample = new PoseInterpolator.Sample { T = stamp, Pos = world, Rot = child.Rot };
            Check(!TakeCarried(cargo, ref sample, world, child.Rot, false, false) && cargo.CarriedBy == 40 &&
                  (sample.Pos - new Vector3d(lp.x, lp.y, lp.z)).sqrMagnitude < .0001, "late-join sample enters parent coordinates before interpolation");
            for (int n = 1; n < snapshot.Length - 1; n++)
            {
                r = new NetReader { Buf = snapshot, Pos = 1, End = n };
                ReadEntityHead(r, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                Check(r.Bad, "truncated framed lock rejected");
            }
            WriteUnlock(w);
            r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(r, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            Check(!ReadLockedState && ReadCarriedBy == 0 && ReadLockContainer == 0 && ReadLockItem == -1, "unlock clears wire read context");
            double previous = 5; uint previousEpoch = 2;
            Check(NewerState(PoseStamp(5, 5, false), 2, ref previous, ref previousEpoch), "same physics-step lock/unlock has a newer millisecond stamp");
            Check(!NewerState(5, 2, ref previous, ref previousEpoch) && NewerState(PoseStamp(1, 5, true), 3, ref previous, ref previousEpoch), "delayed final cannot overwrite motion; new owner clock is accepted");

            // Batch 9: inventory transitions must bypass quiet pose/lock rules, and lock-reader context must
            // not leak from the preceding entity into a lamp's stored/held state.
            foreach (bool stored in new[] { true, false })
            {
                w.Reset();
                WriteStateHead(w, 41, 2, 6, 6, rest, Quaternion.identity, Vector3.zero, false,
                    carriedBy: 40, localPos: lp, localRot: lq, final: true, locked: true, lockItem: item, lockMount: index);
                r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadEntityHead(r, false, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                w.Reset(); WriteStateHead(w, 42, 1, 2, 6.05, rest, Quaternion.identity, Vector3.zero, false, stored: stored, held: !stored);
                r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadEntityHead(r, false, out _, out _, out _, out _, out _, out _, out _, out bool wireStored, out bool wireHeld, out _);
                Check(!r.Bad && wireStored == stored && wireHeld != stored && !ReadLockedState && ReadCarriedBy == 0 && ReadLockItem == -1,
                    "inventory state survives protocol 39 after another entity's framed lock");
                tail = default;
                Check(BandwidthRules.StateQuiet(ref tail, 6.05, false, false, false, false, true, false, true, true, false, .05, stored: stored) == BandwidthRules.Quiet.None,
                    "inventory flag change sends even with no visible pose and an inactive quiet tail");
            }
            var drivenCar = new Ent(); drivenCar.Root.car = new TestCar();
            baseline = default; tail = default;
            for (int tick = 0; tick < 100; tick++)
            {
                bool carVelocity = tick < 99;
                bool moving = StateMoving(drivenCar, tick < 99, false, tick < 99, ref carVelocity);
                Check(!baseline.Locked && !FinalState(false, 0, true, BandwidthRules.Quiet.None, moving, true),
                    "driven car is never finalized by item release/lock policy, including its last braking sample");
                Check(BandwidthRules.StateQuiet(ref tail, tick * .05, tick == 0, tick < 99, false, false, tick == 99,
                    moving, false, false, false, .05, driven: true) == BandwidthRules.Quiet.None,
                    "driven car's moving poses and braking control keep the physics cadence");
            }
            TestLockCost();
            ByNet.Clear(); Server.Clear();
        }

        private static void WriteUnlock(NetWriter w)
        {
            w.Reset(); WriteStateHead(w, 41, 2, 5, 5.001, default, Quaternion.identity, Vector3.zero, false);
        }
    }

    static class SessionLog
    {
        internal static void Event(string kind, uint net, uint other = 0, int from = -1, uint epoch = 0, int to = -1, string result = null, int count = 0) { if (kind == "claim-refused") Net.Entities.RecordRefusal(result); }
    }
}
