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
        internal static void RecordRefusal(string why) { Refusal = why; }
        static bool ServerWornNow(SEnt se) => false;
        static string DriverSeatClaimRefusal(SEnt se, int from) => null;
        static string LastOwnerReason;
        static void OwnerLog(SEnt se, int to, string why) { LastOwnerReason = why; }
        static bool OwnerCascade = true;
        static long OwnerCascaded;
        static void CascadeHitches(SEnt se) { }
        static readonly System.Collections.Generic.List<byte[]> OwnerPackets = new System.Collections.Generic.List<byte[]>();
        static readonly System.Collections.Generic.List<(int Target, byte[] Bytes)> OwnerReplies = new System.Collections.Generic.List<(int, byte[])>();
        static byte[] CopyPacket(NetWriter w) { var bytes = new byte[w.Len]; Array.Copy(w.Buf, bytes, w.Len); return bytes; }
        static void ServerSendAll(NetWriter w, bool reliable, int except) { OwnerPackets.Add(CopyPacket(w)); }
        static long ClaimsRefused, RefusedCrash, RefusedHeld, RefusedStored, RefusedDriven, WornRefused;
        static readonly System.Collections.Generic.Dictionary<uint, int> _refuseLogged = new System.Collections.Generic.Dictionary<uint, int>();
        internal static bool InSession = true;
        static tosaveitemscript ForceHeld = null;
        static long GrabsLost, OwnerChanges, ContentsOnOwner;
        static void HitchBeforeOwner(Ent e, int owner, uint epoch) { }
        static void SendHandover(Ent e, uint epoch) { }
        static void LiveUsablesChanged(tosaveitemscript it) { }
        static void HitchFreezeBodies(Ent e) { }
        static int ClaimContents(TestCar car, bool now) => 0;
        internal static int MyId = 1;
        static long ClaimsSent;
        static byte[] Uploaded;
        static void ToServer(NetWriter w, bool reliable) { Uploaded = new byte[w.Len]; Array.Copy(w.Buf, Uploaded, w.Len); }
        static bool HitchTrailer(Ent e) => false;
        static bool IsProxy(pickupable p) => p != null && ProxyItems.Count > 0 && (ProxyItems.Contains(p.gameObject) || ProxyItems.Contains(p.transform.root.gameObject));
        static NetReader ClaimReader(NetWriter w) => new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
        static void TestClaims()
        {
            Time.time = 10; Time.realtimeSinceStartup = 10; MyId = 1;
            var p = new TestPickup();
            var item = new Ent { NetId = 8, Proxy = true, OwnerId = 0 };
            item.Root.P = p; item.Root.gameObject = p.gameObject;
            ByNet[8] = item; ProxyItems.Add(p.gameObject); Uploaded = null;
            PickupPrefix(p);
            var packet = new NetWriter(); WriteClaim(packet, 8, ClaimReason.Pickup);
            IdleTrafficTest.Check(!item.Proxy && item.OwnerId == MyId && Uploaded.Length == 6 &&
                new NetReader { Buf = Uploaded, Pos = 1, End = Uploaded.Length }.U32() == 8,
                "proxy pickup takes provisional ownership with only net id and diagnostic producer");
            Uploaded = null; PickupPrefix(p);
            IdleTrafficTest.Check(Uploaded == null && !item.SentHeld && item.LastPoseStamp == 0,
                "already-owned pickup sends no claim or held/clock notification");
            SetProxy(item, true); item.Root.car = new TestCar(); Uploaded = null;
            PickupPrefix(p);
            IdleTrafficTest.Check(item.Proxy && Uploaded == null, "pickup interaction cannot claim a whole car");
            ByNet.Clear(); ProxyItems.Clear();

            foreach (ClaimReason reason in Enum.GetValues(typeof(ClaimReason)))
            {
                var se = new SEnt { NetId = 8, Epoch = 1, OwnerId = 0, ReceivedStateAt = 7, ReceivedStateEpoch = 1 };
                Server[8] = se; OwnerReplies.Clear(); Refusal = null;
                packet.Reset(); WriteClaim(packet, 8, reason);
                ServerClaim(1, ClaimReader(packet));
                IdleTrafficTest.Check(se.OwnerId == 1 && se.Epoch == 2 && !se.Held && se.ReceivedStateAt == 7 &&
                    LastOwnerReason == "claim: " + reason, "all producers use the same arbitration without pickup protection: " + reason);
                ServerClaim(1, ClaimReader(packet));
                IdleTrafficTest.Check(se.Epoch == 2 && OwnerReplies.Count == 0, "same-owner claims are ignored without confirmation: " + reason);
                se.Held = true; ServerClaim(0, ClaimReader(packet));
                IdleTrafficTest.Check(Refusal == "held" && se.OwnerId == 1, "ordinary held pose still prevents claims: " + reason);
                se.Held = false; ServerClaim(0, ClaimReader(packet));
                IdleTrafficTest.Check(se.OwnerId == 0 && se.Epoch == 3, "clearing held pose restores normal claims: " + reason);
            }
            // A reattached door follows the car's current owner during usable motion.
            var carOwner = new SEnt { NetId = 1, OwnerId = 0, Epoch = 1, AttachKids = new System.Collections.Generic.HashSet<uint> { 146 } };
            var door = new SEnt { NetId = 146, OwnerId = 0, Epoch = 1, AttachParent = 1 };
            Server[1] = carOwner; Server[146] = door;
            packet.Reset(); WriteClaim(packet, 1, ClaimReason.Driver); ServerClaim(1, ClaimReader(packet));
            IdleTrafficTest.Check(door.OwnerId == carOwner.OwnerId && door.OwnerId == 1 && door.Epoch == 2 && !door.Held,
                "reattached door follows current car owner when ownership changes during usable motion");
            IdleTrafficTest.Check(door.OwnerId != 0, "saved pre-motion owner is no longer a valid door expectation");
            Server.Clear();

            Mp.Server = new TestServer();
            Mp.Server.Positions[0] = default; Mp.Server.Positions[1] = new Vector3d(20, 0, 0);
            var rabbit = new SEnt { NetId = 9, Epoch = 1, OwnerId = 1, Rabbit = true, Held = true }; Server[9] = rabbit;
            ServerRabbitTick(.25f);
            IdleTrafficTest.Check(rabbit.OwnerId == 1, "ordinary held pose excludes rabbit from nearest-player transfer");
            rabbit.Held = false; ServerRabbitTick(.25f);
            IdleTrafficTest.Check(rabbit.OwnerId == 0, "nearest-player policy resumes when held pose clears");
            Mp.Server = null; Known.Clear(); Server.Clear();

            foreach (int length in new[] { 4, 5, 7, 14 })
            {
                var se = new SEnt { NetId = 8, Epoch = 1, OwnerId = 0 }; Server[8] = se;
                packet.Reset(); WriteClaim(packet, 8, ClaimReason.Pickup);
                while (packet.Len < length) packet.U8(0);
                ServerClaim(1, new NetReader { Buf = packet.Buf, Pos = 1, End = length });
                IdleTrafficTest.Check(se.OwnerId == 0 && se.Epoch == 1, "malformed or obsolete extended claim is ignored: " + length);
            }
            var held = new TestPickup { pickedUp = true };
            var loser = new TestPlayer { pickedUp = held }; mainscript.s = new TestMain { player = loser };
            item = new Ent { NetId = 8, Epoch = 1, OwnerId = MyId }; item.Root.P = held; ByNet[8] = item;
            packet.Reset(); packet.U8(Owner); packet.U32(8); packet.VarU32(0); packet.U32(2);
            Uploaded = null; ApplyOwner(ClaimReader(packet));
            IdleTrafficTest.Check(loser.Drops == 1 && item.Proxy && item.OwnerId == 0 && item.Epoch == 2 && Uploaded == null,
                "owner decision drops a lost grab immediately without pickup reassertion");
            ByNet.Clear(); Server.Clear(); mainscript.s = null;

            // a copy handed from one remote player to another: the new owner's clock may be behind the old one's
            // (lobby3 C: the host's copy froze 134 m behind while a third player drove)
            foreach (bool on in new[] { true, false })
            {
                NewOwnerNewClock = on;
                var copy = new Ent { NetId = 11, Epoch = 1, OwnerId = 2, Proxy = true }; copy.Root.P = new TestPickup(); ByNet[11] = copy;
                copy.Ip.Add(new PoseInterpolator.Sample { T = 900, Pos = new Vector3d(0, 0, 0), Rot = Quaternion.identity }, 10);
                packet.Reset(); packet.U8(Owner); packet.U32(11); packet.VarU32(3); packet.U32(2);
                ApplyOwner(ClaimReader(packet));
                copy.Ip.Add(new PoseInterpolator.Sample { T = 40, Pos = new Vector3d(1, 0, 0), Rot = Quaternion.identity }, 10.05);
                IdleTrafficTest.Check(copy.OwnerId == 3 && copy.Proxy && copy.Ip.TelemetryAccepted == on,
                    on ? "a copy handed to another remote player takes the new owner's states (its clock behind the old owner's)"
                       : "A/B off: the old owner's interpolator drops them (the bug)");
                ByNet.Clear();
            }
            NewOwnerNewClock = true;

            CargoClaims = true;
            var car = new Ent { NetId = 7, OwnerId = MyId }; ByNet[7] = car;
            item = new Ent { NetId = 8, Proxy = true, OwnerId = 0, CarriedBy = 7, Held = true }; item.Root.P = new TestPickup(); ByNet[8] = item;
            ReadCarriedBy = 0; ReadLockedState = true;
            var sample = new PoseInterpolator.Sample(); Uploaded = null;
            IdleTrafficTest.Check(!TakeCarried(item, ref sample, default, Quaternion.identity, false, false) && Uploaded == null,
                "physlocked state cannot be mistaken for loose cargo release");
            item.CarriedBy = 7; ReadLockedState = false;
            IdleTrafficTest.Check(TakeCarried(item, ref sample, default, Quaternion.identity, false, false) && !item.Proxy && Uploaded.Length == 6,
                "loose release in our car claims cargo without explicit release bit or central held guard");
            // A separate sled still follows the rabbit's simulator without changing the held rabbit.
            var heldRabbit = new SEnt { NetId = 9, Epoch = 1, OwnerId = MyId, Held = true }; Server[9] = heldRabbit;
            var sled = new Ent { NetId = 10, Proxy = true, OwnerId = 0, OwnerAt = 0 }; sled.Root.P = new TestPickup(); ByNet[10] = sled;
            var sledServer = new SEnt { NetId = 10, Epoch = 1, OwnerId = 0 }; Server[10] = sledServer;
            IdleTrafficTest.Check(ClaimCargo(sled, reason: ClaimReason.RabbitObjectNear), "rabbit simulator can claim separate loose sled");
            ServerClaim(MyId, new NetReader { Buf = Uploaded, Pos = 1, End = Uploaded.Length });
            IdleTrafficTest.Check(sledServer.OwnerId == MyId && sledServer.Epoch == 2 && !sledServer.Held && heldRabbit.OwnerId == MyId && heldRabbit.Held &&
                LastOwnerReason == "claim: RabbitObjectNear", "sled claim changes only the separate sled and names its producer");
            ByNet.Clear(); Server.Clear(); CargoClaims = false; OwnerReplies.Clear(); OwnerPackets.Clear();
        }
    }
}

namespace TLDRevamp.Net { static class DedicatedServer { internal static bool Enabled = false; } }
namespace TLDRevamp { static class Plugin { internal static readonly Logger Log = new Logger(); internal class Logger { internal void LogWarning(string text) { } internal void LogInfo(string text) { } } } }

namespace UnityEngine { public static partial class Time { internal static float realtimeSinceStartup; internal static float time; internal static double unscaledTimeAsDouble = 10; } }

namespace HarmonyLib
{
    class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    class HarmonyPrefix : Attribute { }
    class HarmonyPostfix : Attribute { }
}
