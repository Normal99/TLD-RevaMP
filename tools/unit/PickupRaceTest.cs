using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        static readonly NetWriter W = new NetWriter();
        static bool WornSync = false;
        static float RabbitHoldS = 2, RabbitNearM = 60, RabbitSwitchM = 5, RabbitCarMoveMps = 1.5f, _rabSrvAcc;
        static bool RabbitSync = true, RabbitOwners = true;
        static long RabbitOwnerMoves;
        static string RabbitLastMove;
        static System.Collections.Generic.HashSet<uint> KnownBy(int player)
        { if (!Known.TryGetValue(player, out var set)) Known[player] = set = new System.Collections.Generic.HashSet<uint>(); return set; }
        static long DriverSeatRefused;
        static string Refusal;
        static bool ServerWornNow(SEnt se) => false;
        static string DriverSeatClaimRefusal(SEnt se, int from) => null;
        static string LastOwnerReason;
        static void OwnerLog(SEnt se, int to, string why) { LastOwnerReason = why; }
        static bool OwnerCascade = true;
        static long OwnerCascaded;
        static void CascadeHitches(SEnt se) { }
        static void ServerSendAll(NetWriter w, bool reliable, int except) { }
        static void RefuseClaim(int from, SEnt se, string why, ClaimReason reason = 0) { Refusal = why; }
        static bool InSession = true;
        static int MyId = 1;
        static long ClaimsSent;
        static byte[] Uploaded;
        static void ToServer(NetWriter w, bool reliable) { Uploaded = new byte[w.Len]; Array.Copy(w.Buf, Uploaded, w.Len); }
        static bool HitchTrailer(Ent e) => false;
        static bool PickupPose(SEnt se, double at, bool held, bool released)
        {
            var pose = new NetWriter();
            WriteStateHead(pose, se.NetId, se.Epoch, 1, at, default, Quaternion.identity, Vector3.zero, false, held: held, releasedHeld: released);
            var r = new NetReader { Buf = pose.Buf, Pos = 1, End = pose.Len };
            ReadEntityHead(r, false, out _, out uint epoch, out double stamp, out _, out _, out _, out _, out _, out bool wireHeld, out _);
            if (r.Bad || !ServerPickupPose(se, wireHeld, ReadReleasedState) || !NewerState(stamp, epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch)) return false;
            ServerHeldState(se, wireHeld); return true;
        }
        static void TestPickupRace()
        {
            Time.time = 10;
            // Run the real Harmony prefix/postfix before the game unparents the mounted object.
            foreach (bool car in new[] { true, false }) foreach (bool proxy in new[] { true, false }) foreach (bool limb in new[] { true, false })
            {
                var container = new Ent { NetId = 7 }; if (car) container.Root.car = new TestCar();
                var item = new Ent { NetId = 8, Proxy = proxy };
                var p = new TestPickup(); p.transform.parent = container.Root.transform;
                if (limb) item.Root.ragdollPos = new TestLimbList { ps = new[] { p } };
                else item.Root.P = p;
                ByNet[7] = container; ByNet[8] = item;
                PickupClaim.Prefix(p, out var picked);
                IdleTrafficTest.Check(picked == item && !item.Proxy && !container.Held, "mounted item wins over earlier owned car/container, including detached limbs and own items");
                p.UnPhysicsLock(); p.pickedUp = true; Uploaded = null;
                PickupClaim.Postfix(p, picked);
                var claim = new NetReader { Buf = Uploaded, Pos = 1, End = Uploaded.Length };
                IdleTrafficTest.Check(claim.U32() == 8 && claim.U8() == (byte)ClaimReason.Pickup && claim.U32() == item.Epoch, "pickup notifies server about item, never its mount");
                ByNet.Clear();
            }
            var detached = new TestPickup();
            var body = new Ent(); body.Root.ragdollPos = new TestLimbList { ps = new[] { detached } };
            IdleTrafficTest.Check(PickupLimb(body, detached) && !PickupLimb(body, new TestPickup()), "already-owned detached corpse limb resolves to its root without the proxy-only limb map");
            // batch13: joiner already owns net 8/epoch 1, sends a pickup, then the host claims it.
            // Exercise a pose sharing the pickup's encoded millisecond, and an unreliable pose overtaking it.
            foreach (bool hostPickup in new[] { false, true }) foreach (double received in new[] { 57.596, 57.631 })
            {
                var se = new SEnt { NetId = 8, Epoch = 1, OwnerId = 1, Rabbit = true,
                    ReceivedStateEpoch = 1, ReceivedStateAt = received };
                Server[8] = se;
                var e = new Ent { NetId = 8, Epoch = 1, OwnerId = 1, LastPoseStamp = 57.531 };
                var p = new TestPickup(); e.Root.ragdollPos = new TestLimbList { ps = new[] { p } }; ByNet[8] = e;
                Time.unscaledTimeAsDouble = 57.596;
                PickupClaim.Prefix(p, out var picked); p.pickedUp = true; PickupClaim.Postfix(p, picked);
                IdleTrafficTest.Check(Uploaded.Length == 14, "owner's limb pickup sends the extended Claim");
                Time.realtimeSinceStartup = 78.651f;
                ServerClaim(1, new NetReader { Buf = Uploaded, Pos = 1, End = Uploaded.Length });
                var host = new NetWriter();
                if (hostPickup) WritePickupClaim(host, 8, 1, 78.724);
                else { WriteClaim(host, 8, ClaimReason.RabbitNear); }
                Refusal = null;
                Time.realtimeSinceStartup = 78.724f;
                ServerClaim(0, new NetReader { Buf = host.Buf, Pos = 1, End = host.Len });
                IdleTrafficTest.Check(Refusal == "held" && se.OwnerId == 1 && se.Epoch == 1 && se.Held && se.PickupPending,
                    "same-owner pickup cannot be vetoed by the pose fence then stolen by host's claim");
                IdleTrafficTest.Check(se.ReceivedStateAt == received && !NewerState(57.591, 1, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch),
                    "pickup preserves the newer pose fence and rejects delayed pre-pickup poses");
                IdleTrafficTest.Check(NewerState(57.671, 1, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch), "owner's subsequent release can pass the fence");
                se.Held = false;
                ServerClaim(0, new NetReader { Buf = host.Buf, Pos = 1, End = host.Len });
                IdleTrafficTest.Check(se.OwnerId == 0, "same sequence permits host claims after the owner's release");
                ByNet.Clear(); Server.Remove(8);
            }
            IdleTrafficTest.Check(PoseStamp(65.331, 65.332, false) > 65.332,
                "first held or immediate-release physics sample stays beyond the pickup frame-clock fence");
            var carried = new SEnt { NetId = 9, OwnerId = 1, Epoch = 1, Rabbit = true,
                ReceivedStateEpoch = 1, ReceivedStateAt = 65.271 };
            Server[9] = carried;
            var grab = new NetWriter(); WritePickupClaim(grab, 9, 1, 65.332);
            ServerClaim(1, new NetReader { Buf = grab.Buf, Pos = 1, End = grab.Len });
            foreach (bool confirmed in new[] { false, true })
            {
                if (confirmed) PickupPose(carried, 65.35, true, false);
                double fence = carried.ReceivedStateAt;
                IdleTrafficTest.Check(!PickupPose(carried, 65.371, false, false) && carried.Held && carried.ReceivedStateAt == fence,
                    "batch14 intervening unheld pose cannot revoke reliable pickup or advance release fence");
                foreach (ClaimReason reason in Enum.GetValues(typeof(ClaimReason)))
                {
                    var take = new NetWriter();
                    if (reason == ClaimReason.Pickup) WritePickupClaim(take, 9, 1, 86.135);
                    else WriteClaim(take, 9, reason);
                    Refusal = null; ServerClaim(0, new NetReader { Buf = take.Buf, Pos = 1, End = take.Len });
                    IdleTrafficTest.Check(Refusal == "held" && carried.OwnerId == 1 && carried.Epoch == 1,
                        "held net 9 refuses producer " + reason);
                }
            }
            IdleTrafficTest.Check(PickupPose(carried, 65.36, false, true) && !carried.Held && !carried.PickupHeld,
                "reliable release passes even when a newer ordinary unheld pose overtook it");
            var releasedClaim = new NetWriter(); WriteClaim(releasedClaim, 9, ClaimReason.RabbitNear);
            ServerClaim(0, new NetReader { Buf = releasedClaim.Buf, Pos = 1, End = releasedClaim.Len });
            IdleTrafficTest.Check(carried.OwnerId == 0 && LastOwnerReason == "claim: RabbitNear", "released body can be claimed and ownerlog names producer");
            ServerPickupHeld(carried, 86140); carried.OwnerId = 1; carried.Epoch++;
            IdleTrafficTest.Check(PickupPose(carried, 1, false, false) && !carried.PickupHeld,
                "forced ownership epoch after departure can clear the old holder's pickup");
            Server.Remove(9);

            // batch14 doors: reattachment completed at owner 0; both parent and door then changed to owner 1.
            var carOwner = new SEnt { NetId = 1, OwnerId = 0, Epoch = 1, AttachKids = new System.Collections.Generic.HashSet<uint> { 146 } };
            var door = new SEnt { NetId = 146, OwnerId = 0, Epoch = 1, AttachParent = 1 };
            Server[1] = carOwner; Server[146] = door;
            var drive = new NetWriter(); WriteClaim(drive, 1, ClaimReason.Driver);
            ServerClaim(1, new NetReader { Buf = drive.Buf, Pos = 1, End = drive.Len });
            IdleTrafficTest.Check(door.OwnerId == carOwner.OwnerId && door.OwnerId == 1 && door.Epoch == 2 && !door.Held,
                "reattached door follows current car owner when ownership changes during usable motion");
            IdleTrafficTest.Check(door.OwnerId != 0, "saved pre-motion owner is no longer a valid door expectation");
            Server.Remove(1); Server.Remove(146);
            Time.unscaledTimeAsDouble = 10;
            foreach (bool rabbit in new[] { true, false }) foreach (bool alreadyOwner in new[] { true, false })
            {
                var se = new SEnt { NetId = 8, Epoch = 1, OwnerId = alreadyOwner ? 1 : 0, Rabbit = rabbit,
                    ReceivedStateEpoch = 1, ReceivedStateAt = 9 };
                Server[8] = se;
                var pickup = new NetWriter(); WritePickupClaim(pickup, 8, 1, 10);
                ServerClaim(1, new NetReader { Buf = pickup.Buf, Pos = 1, End = pickup.Len });
                IdleTrafficTest.Check(se.OwnerId == 1 && se.Held && se.PickupPending && se.Epoch == (alreadyOwner ? 1u : 2u), "pickup atomically protects existing/provisional owner of rabbit/corpse/sled");
                if (!alreadyOwner)
                {
                    double first = PoseStamp(9.98, 10, true);
                    IdleTrafficTest.Check(NewerState(first, se.Epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch), "first granted held pose from the same physics frame clears the new epoch fence");
                }
                // A delayed pre-pickup unreliable state arrives before the next held pose.
                IdleTrafficTest.Check((alreadyOwner ? !NewerState(9.9, se.Epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch) : se.Epoch != 1) && se.Held,
                    "pre-pickup pose cannot clear Held during network latency");
                var touch = new NetWriter(); WriteClaim(touch, 8, ClaimReason.CargoContact); Refusal = null;
                Time.realtimeSinceStartup = 10.2f;
                ServerClaim(0, new NetReader { Buf = touch.Buf, Pos = 1, End = touch.Len });
                IdleTrafficTest.Check(Refusal == "held" && se.OwnerId == 1, "host car/contact claim at 0.2 seconds cannot steal held body");
                IdleTrafficTest.Check(NewerState(10.1, se.Epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch), "next held pose passes pickup fence");
                if (rabbit)
                {
                    Mp.Server = new TestServer();
                    Mp.Server.Positions[0] = default; Mp.Server.Positions[1] = new Vector3d(20, 0, 0);
                    Time.realtimeSinceStartup = Math.Max(13, se.RabbitHoldUntil + 1); // beyond the existing contact-claim hold
                    ServerRabbitTick(.25f);
                    IdleTrafficTest.Check(se.OwnerId == 1 && se.Held, "actual nearest-player tick cannot steal a picked-up rabbit before its held pose");
                    se.Held = false; ServerRabbitTick(.25f);
                    IdleTrafficTest.Check(se.OwnerId == 0, "actual nearest-player policy resumes on release");
                    Mp.Server = null; Known.Clear();
                }
                // Release restores the ordinary contact claim policy.
                se.Held = false;
                ServerClaim(0, new NetReader { Buf = touch.Buf, Pos = 1, End = touch.Len });
                IdleTrafficTest.Check(se.OwnerId == 0, "released body may return to car owner normally");
                Server.Remove(8);
            }
            var current = new SEnt { NetId = 8, Epoch = 3, OwnerId = 1 }; Server[8] = current;
            var stale = new NetWriter(); WritePickupClaim(stale, 8, 2, 100);
            ServerClaim(1, new NetReader { Buf = stale.Buf, Pos = 1, End = stale.Len });
            IdleTrafficTest.Check(!current.Held, "same-owner notification from old epoch is ignored");
            for (int end = 2; end < stale.Len; end++)
            {
                ServerClaim(2, new NetReader { Buf = stale.Buf, Pos = 1, End = end });
                IdleTrafficTest.Check(current.OwnerId == 1 && !current.Held, "truncated pickup cannot change ownership");
            }
            Server.Remove(8);
        }
    }
}

namespace TLDRevamp.Net { static class DedicatedServer { internal static bool Enabled = false; } }

namespace UnityEngine { static class Time { internal static float realtimeSinceStartup; internal static float time; internal static double unscaledTimeAsDouble = 10; } }

namespace HarmonyLib
{
    class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    class HarmonyPrefix : Attribute { }
    class HarmonyPostfix : Attribute { }
}
