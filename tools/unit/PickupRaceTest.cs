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
        static void OwnerLog(SEnt se, int to, string why) { }
        static void CascadeOwner(SEnt se) { }
        static void ServerSendAll(NetWriter w, bool reliable, int except) { }
        static void RefuseClaim(int from, SEnt se, string why) { Refusal = why; }
        static void TestPickupRace()
        {
            var detached = new TestPickup();
            var body = new Ent(); body.Root.ragdollPos = new TestLimbList { ps = new[] { detached } };
            IdleTrafficTest.Check(PickupLimb(body, detached) && !PickupLimb(body, new TestPickup()), "already-owned detached corpse limb resolves to its root without the proxy-only limb map");
            foreach (bool rabbit in new[] { true, false }) foreach (bool alreadyOwner in new[] { true, false })
            {
                var se = new SEnt { NetId = 8, Epoch = 1, OwnerId = alreadyOwner ? 1 : 0, Rabbit = rabbit,
                    ReceivedStateEpoch = 1, ReceivedStateAt = 9 };
                Server[8] = se;
                var pickup = new NetWriter(); WritePickupClaim(pickup, 8, 1, 10);
                ServerClaim(1, new NetReader { Buf = pickup.Buf, Pos = 1, End = pickup.Len });
                IdleTrafficTest.Check(se.OwnerId == 1 && se.Held && se.PickupPending && se.Epoch == (alreadyOwner ? 1u : 2u), "pickup atomically protects existing/provisional owner of rabbit/corpse/sled");
                // A delayed pre-pickup unreliable state arrives before the next held pose.
                IdleTrafficTest.Check(!NewerState(9.9, se.Epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch) && se.Held,
                    "pre-pickup pose cannot clear Held during network latency");
                var touch = new NetWriter(); touch.U8(Claim); touch.U32(8); Refusal = null;
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
                if (end == 5) continue; // ordinary five-byte contact claim
                ServerClaim(2, new NetReader { Buf = stale.Buf, Pos = 1, End = end });
                IdleTrafficTest.Check(current.OwnerId == 1 && !current.Held, "truncated pickup cannot change ownership");
            }
            Server.Remove(8);
        }
    }
}

namespace TLDRevamp.Net { static class DedicatedServer { internal static bool Enabled = false; } }

namespace UnityEngine { static class Time { internal static float realtimeSinceStartup; } }
