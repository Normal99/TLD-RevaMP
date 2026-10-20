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
            Check(FinalState(true, true, BandwidthRules.Quiet.None, true, false) && FinalState(true, false, BandwidthRules.Quiet.None, true, false), "lock always produces a final, including with waste fixes off");
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
            var cargo = new Ent { NetId = 41, OwnerId = 1 };
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
            EnsurePoseLock(cargo);
            cargo.CarriedBy = 40;
            cargo.Ip.Add(new PoseInterpolator.Sample { T = 1, Pos = new Vector3d(lp.x, lp.y, lp.z), Rot = lq }, 1);
            EvalShown(cargo, 1.1, .05f, out var rendered, out var renderedRot);
            var expected = car.Root.transform.position + car.Root.transform.rotation * lp;
            Check((rendered - new Vector3d(expected.x, expected.y, expected.z)).sqrMagnitude < .0001 && Quaternion.Angle(renderedRot, car.Root.transform.rotation * lq) < .01, "receiver draws a quiet item in its current car frame");
            TakeOverCarried(cargo);
            Check(cargo.CarriedBy == 0 && cargo.Root.P.physlock == mount, "ownership drops interpolation frame while retaining the actual game lock");
            Check(cargo.Root.P.physlock == mount, "lock survives ownership takeover/rebind");
            ApplyPoseLock(cargo, false, 0, -1, -1);
            Check(cargo.Root.P.physlock == null && cargo.Root.transform.parent == null && !cargo.PoseLocked, "unlock releases receiver immediately");

            // Lock state may arrive before its parent. A later placement resolves the retained reference.
            ByNet.Remove(40);
            ApplyPoseLock(cargo, true, 40, item, index);
            Check(cargo.Root.P.physlock == null, "missing parent keeps a world fallback");
            ByNet.Add(40, car); EnsurePoseLock(cargo);
            Check(cargo.Root.P.physlock == mount, "parent arriving later restores the lock");

            var parent = new SEnt { NetId = 40, HasState = true, Pos = new Vector3d(5000, 0, 0), Rot = Quaternion.Euler(0, 90, 0) };
            var child = new SEnt { NetId = 41, LockFrame = 40, LockLocalPos = lp, LockLocalRot = lq };
            Server.Add(40, parent); Server.Add(41, child); ServerLockedPose(child);
            var offset = parent.Rot * lp;
            Check(Math.Abs(child.Pos.x - (5000 + offset.x)) < .001 && child.Where.x == child.Pos.x, "late join and interest use current parent world pose");
            car.FarPos = parent.Pos; car.FarRot = parent.Rot; car.Root = null;
            Check(LockedWorld(cargo, out var far, out _) && (far - child.Pos).sqrMagnitude < .0001, "far child follows stored-out parent in world space");

            var w = new NetWriter();
            WriteStateHead(w, 41, 2, 4, 5, rest, Quaternion.identity, Vector3.zero, false,
                           carriedBy: 40, localPos: lp, localRot: lq, final: true, locked: true, lockItem: item, lockMount: index);
            w.U8(77); // opaque visual tail must survive replay
            var snapshot = FinalSnapshot(new NetReader { Buf = w.Buf, End = w.Len }, true);
            ReplayLockedState(w, snapshot, child.Pos, child.Rot);
            var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
            ReadEntityHead(r, false, out uint net, out uint epoch, out double stamp, out var world, out _, out var velocity, out _, out _, out _, out _);
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
            Check(!ReadLockedState && ReadCarriedBy == 0 && ReadLockItem == -1, "unlock clears wire read context");
            double previous = 5; uint previousEpoch = 2;
            Check(NewerState(PoseStamp(5, 5, false), 2, ref previous, ref previousEpoch), "same physics-step lock/unlock has a newer millisecond stamp");
            Check(!NewerState(5, 2, ref previous, ref previousEpoch) && NewerState(PoseStamp(1, 5, true), 3, ref previous, ref previousEpoch), "delayed final cannot overwrite motion; new owner clock is accepted");
            ByNet.Clear(); Server.Clear();
        }

        private static void WriteUnlock(NetWriter w)
        {
            w.Reset(); WriteStateHead(w, 41, 2, 5, 5.001, default, Quaternion.identity, Vector3.zero, false);
        }
    }

    static class SessionLog
    {
        internal static void Event(string kind, uint net, uint other = 0, int from = -1, uint epoch = 0) { }
    }
}
