using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The world as this machine shows it, for the player bots (PlayerBotCore): the copies' poses, the ground, a car's
    /// seats, wheels and box, and how an item's copy is locked into a container.
    public static partial class Entities
    {
        internal static readonly IPlayerBotWorld BotWorld = new LocalBotWorld();

        private sealed class LocalBotWorld : IPlayerBotWorld
        {
            public bool Pose(uint net, out Vector3d g, out Quaternion q)
            {
                g = default; q = Quaternion.identity;
                if (ByNet.TryGetValue(net, out var e) && e.Root != null) { g = mainscript.GlobalFromUnityPos(e.Root.transform.position); q = e.Root.transform.rotation; return true; }
                // stored out here (far from this machine's own player): on the host, the last state the server has of it.
                // A player's game keeps what is near its player loaded; a bot made in the host's game may stand where the
                // host's player isn't.
                if (Mp.Server != null && Server.TryGetValue(net, out var se) && se.HasState) { g = se.Pos; q = se.Rot; return true; }
                return false;
            }

            public bool Ground(double gx, double gz, out double gy)
            {
                var u = mainscript.UnityPosFromGlobal(new Vector3d(gx, 0, gz));
                var hit = DebugBridge.GroundHit(u.x, u.z);
                gy = hit != null ? mainscript.GlobalFromUnityPos(hit.Value.point).y : 0;
                return hit != null;
            }

            private readonly Dictionary<uint, PlayerBotCar> _cars = new Dictionary<uint, PlayerBotCar>();   // its seats and wheels don't change
            public bool Car(uint net, out PlayerBotCar car)
            {
                car = new PlayerBotCar { DriverSeat = -1 };
                if (!ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.car == null || e.PartIndex >= 0)
                    return _cars.TryGetValue(net, out car) && (Mp.Server == null || Server.ContainsKey(net));
                var t = e.Root.transform;
                var seats = VehicleSeats(e.Root);
                int d = System.Array.FindIndex(seats, s => s != null && s.driverSeat0);
                if (d < 0) return false;
                // in the car's frame without its scale (the bot rotates and adds, as a world pose does)
                var sp = seats[d].sitPos != null ? seats[d].sitPos.position : seats[d].transform.position;
                car.DriverSeat = d; car.Wheels = WheelsOf(e).Length;
                car.SeatLocal = Quaternion.Inverse(t.rotation) * (sp - t.position);
                // the door: beside the seat, outward (a player gets in and out there)
                car.DoorLocal = car.SeatLocal + new Vector3(car.SeatLocal.x <= 0f ? -1.3f : 1.3f, -0.4f, 0f);
                if (_cars.Count > 256) _cars.Clear();
                _cars[net] = car;
                return true;
            }

            public void Cargo(uint carNet, List<uint> into)
            {
                if (!ByNet.TryGetValue(carNet, out var ce) || ce.Root == null || ce.Root.car == null) return;
                var car = ce.Root.car;
                foreach (var e in ByNet.Values)
                    if (e.Root != null && e.Root.car == null && LooseItem(e) && CargoBody(e) && !e.Held && !e.Stored && e.WornBy < 0 && !e.CrashTaking && InCar(car, e, out _))
                        into.Add(e.NetId);
            }

            public PlayerBotLock Lock(uint net)
            {
                var lk = new PlayerBotLock();
                if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return lk;
                if (e.Proxy && e.PoseLocked && (e.LockContainer != 0 || e.LockFrame != 0))
                {
                    lk.Locked = lk.Known = true; lk.Container = e.LockContainer; lk.Frame = e.LockFrame;
                    lk.Item = e.LockItem; lk.Mount = e.LockMount; lk.LocalPos = e.LockLocalPos; lk.LocalRot = e.LockLocalRot;
                }
                else if (e.Root.P != null && e.Root.P.physlock != null) lk.Locked = true;   // stored here by the game: how is this machine's to say
                return lk;
            }
        }
    }
}
