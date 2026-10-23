using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Player bots (test only: none exist unless the bridge's `mp pbot add` makes one). The load-test bots (Mp.AddBots,
    /// item bots) share their own objects and circle; they never touch anyone else's. A player bot is a stand-in for a
    /// THIRD PLAYER in ownership tests (tools/lobby3.py): it walks, takes the wheel of an existing car, drives it, gets
    /// out, picks an item up, holds it, drops it or puts it into a car, grabs at the same moment as someone else, leaves,
    /// crashes and rejoins — and on the wire it does that the way a real client does:
    ///   - the player state (Protocol.State) at the players' rate, with the seat it sits in;
    ///   - a car or item becomes its own only through the server: a Claim with the reason a real client gives (Driver,
    ///     DriverRetry 2 s later, Pickup, Contents for what lies in the car it just took), then the Owner answer;
    ///   - what it owns, it moves with State messages (Entities.WriteStateHead, the same tail as an owner's: wheels,
    ///     engine, car signals, limbs) at Protocol.StateHz while it moves, a final (reliable) state when it comes to rest,
    ///     held items flagged held, a seated driver's car flagged driven;
    ///   - told it lost something (Owner to another player), it sends the HandoverState for the new epoch — the records
    ///     it was handed when it got the object (a bot has no game object to capture new ones from);
    ///   - leaving closes the connection the way Mp.Stop does (linger, "left"); a crash sends nothing more at all (no
    ///     player state, no poll) for CrashSilentS — what the host sees of a dead process until Steam's timeout — then
    ///     drops the connection without a goodbye; a rejoin connects again under the same name.
    /// What it leaves out: the pose, outfit and voice streams and the player-save record (bots join without a player
    /// save: the host's own account, or an account already in the game), and the object's own physics: a bot's car
    /// follows the ground under a straight line, its cargo keeps the pose it had in the car when the bot took it.
    /// What the world looks like comes from the bot's own machine (IPlayerBotWorld: the copies there, the ground, the
    /// car's seats and box), the way a real client knows the world from its own copies. This file has no Unity calls,
    /// so tools/unit/PlayerBotTest.cs runs it against a fake world.
    internal interface IPlayerBotWorld
    {
        bool Pose(uint net, out Vector3d g, out Quaternion q);            // the copy's pose on this machine
        bool Ground(double gx, double gz, out double gy);                 // the ground under a point
        bool Car(uint net, out PlayerBotCar car);                         // a car's seats and wheels
        void Cargo(uint carNet, List<uint> into);                         // what lies in the car (Entities.ClaimContents' rule)
        PlayerBotLock Lock(uint net);                                     // how the item's copy is locked into a container
    }

    internal struct PlayerBotCar { public int DriverSeat, Wheels; public Vector3 SeatLocal, DoorLocal; }

    /// Locked: stored in a container (a trunk, a crate). Known: the lock as its owner sent it (container or frame, slot,
    /// local pose) — a bot moving it says the same; unknown, it sends nothing for it (the copies keep riding the lock).
    internal struct PlayerBotLock { public bool Locked, Known; public uint Container, Frame; public int Item, Mount; public Vector3 LocalPos; public Quaternion LocalRot; }

    internal sealed class PlayerBotCore
    {
        public static float WalkMps = 5f, ReachM = 2.5f, AccelMps2 = 2.5f, BrakeMps2 = 5f, ClaimRetryS = 2f, CrashSilentS = 12f;
        public string Name;
        public int MyId;
        public Vector3d Pos; public float Yaw;                 // the player: what Protocol.State carries
        public uint SeatNet; public int SeatIdx;
        public string Doing = "idle", Last = "";
        public bool Silent; public double SilentSince;
        public uint Holding;                                   // in its hands (held states)
        public long ClaimsSent, StatesSent, FinalsSent, HandoversSent, ClaimsLost;

        private sealed class Known { public int Owner = -1; public uint Epoch; public byte[] Handover; }
        private readonly Dictionary<uint, Known> _known = new Dictionary<uint, Known>();
        private readonly NetWriter _w = new NetWriter();

        // what it is doing: one action at a time, as a player does
        private enum Act { Idle, Walk, Board, Take }
        private Act _act; private Vector3d _target; private uint _actNet;
        private double _claimAt; private int _claimTries;
        private uint _wantNet; private Entities.ClaimReason _wantReason;   // the claim waiting for its Owner answer

        // the car it drives: its own model of it once it is the owner
        private uint _car; private PlayerBotCar _carInfo; private bool _carOwned, _carSeeded;
        private Vector3d _carPos; private Quaternion _carRot; private double _carGroundOff;
        private float _speed, _targetSpeed; private double _driveUntil; private bool _carMoving;
        private readonly Dictionary<uint, Vector3> _cargoLocal = new Dictionary<uint, Vector3>();   // net → pose in the car's frame
        private readonly Dictionary<uint, Quaternion> _cargoRot = new Dictionary<uint, Quaternion>();
        private readonly List<uint> _scratch = new List<uint>();
        private ushort _seq; private double _sendAcc;

        public PlayerBotCore(string name, Vector3d at) { Name = name; Pos = at; }

        public bool Owns(uint net) => _known.TryGetValue(net, out var k) && k.Owner == MyId && MyId > 0;
        public int OwnerOf(uint net) => _known.TryGetValue(net, out var k) ? k.Owner : -1;
        public uint EpochOf(uint net) => _known.TryGetValue(net, out var k) ? k.Epoch : 0;
        public uint Driving => _carOwned && SeatNet == _car ? _car : 0;
        public float Speed => _speed;
        public IEnumerable<uint> Cargo => _cargoLocal.Keys;
        public IEnumerable<uint> Owned { get { foreach (var kv in _known) if (kv.Value.Owner == MyId && MyId > 0) yield return kv.Key; } }

        /// Session over (left, crashed): what it knew belongs to that session.
        public void Reset()
        {
            _known.Clear(); _cargoLocal.Clear(); _cargoRot.Clear(); _fresh.Clear(); _handled.Clear();
            _act = Act.Idle; _wantNet = 0; Holding = 0; _car = 0; _carOwned = false; SeatNet = 0; SeatIdx = 0;
            _speed = _targetSpeed = 0f; Silent = false; MyId = 0; Doing = "idle";
        }

        // ------------------------------------------------------------------ commands (the bridge)

        public string Goto(double gx, double gz) { _act = Act.Walk; _target = new Vector3d(gx, Pos.y, gz); Doing = "walking"; return null; }

        public string Enter(uint carNet, IPlayerBotWorld w)
        {
            if (Holding != 0) return "hands full";
            if (!w.Car(carNet, out var info) || info.DriverSeat < 0) return "no such car here";
            _act = Act.Board; _actNet = carNet; _carInfo = info; Doing = "walking to car " + carNet;
            return null;
        }

        public string Drive(float mps, float seconds, double now)
        {
            if (Driving == 0) return "not driving a car of its own";
            _targetSpeed = Mathf.Max(0f, mps); _driveUntil = seconds > 0 ? now + seconds : double.MaxValue;
            Doing = "driving at " + mps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
            return null;
        }

        public string Exit(IPlayerBotWorld w, Action<NetWriter, bool> send)
        {
            _send = send;
            if (SeatNet == 0) return "not seated";
            uint car = SeatNet;
            if (_carOwned && car == _car)
            {
                _speed = _targetSpeed = 0f;
                SendCar(true, false);                                  // at rest, nobody at the wheel: the final word
                foreach (var n in _cargoLocal.Keys) SendCargo(n, true);
            }
            Pos = CarPoint(car, _carInfo.DoorLocal, w); SeatNet = 0; SeatIdx = 0; _car = 0; _carOwned = false; _cargoLocal.Clear(); _cargoRot.Clear();
            Doing = "idle";
            return null;
        }

        public string Take(uint net, IPlayerBotWorld w)
        {
            if (Holding != 0) return "hands full";
            if (SeatNet != 0) return "seated";
            if (!w.Pose(net, out _, out _)) return "no such item here";
            _act = Act.Take; _actNet = net; Doing = "walking to item " + net;
            return null;
        }

        /// Lets go of it on the ground under its hands (local = null) or at `local` in car `carNet`'s frame.
        public string Release(uint carNet, Vector3? local, IPlayerBotWorld w, Action<NetWriter, bool> send)
        {
            _send = send; World = w;
            if (Holding == 0) return "hands empty";
            uint net = Holding; Holding = 0;
            Vector3d g; Quaternion q = Quaternion.identity;
            if (local.HasValue)
            {
                if (!w.Pose(carNet, out var cp, out var cq)) return "no such car here";
                g = Add(cp, Rotate(cq, local.Value)); q = cq;
            }
            else
            {
                g = Hand(); w.Ground(g.x, g.z, out double gy); g = new Vector3d(g.x, gy + 0.15, g.z);
            }
            if (Owns(net)) { WriteItem(net, g, q, Vector3.zero, false, true); StatesSent++; FinalsSent++; }
            Doing = "idle"; Last = "let go of " + net + (local.HasValue ? " in car " + carNet : "");
            return null;
        }

        /// A crash: from now on nothing at all — the wrapper drops the connection after CrashSilentS.
        public void Crash(double now) { Silent = true; SilentSince = now; Doing = "crashed"; }

        // ------------------------------------------------------------------ from the server

        public static bool Wants(byte type) => type == Entities.Add || type == Entities.Owner || type == Entities.HandoverState ||
                                               type == Entities.RemoveItem || type == Entities.PartDetached;

        public void Receive(byte type, NetReader r, Action<NetWriter, bool> send)
        {
            if (type == Entities.Add)
            {
                uint net = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                if (r.Bad) return;
                var k = Get(net); if (epoch >= k.Epoch) { k.Owner = owner; k.Epoch = epoch; }
            }
            else if (type == Entities.PartDetached)
            {
                r.U32(); r.VarU32(); uint part = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                if (r.Bad) return;
                var k = Get(part); if (epoch >= k.Epoch) { k.Owner = owner; k.Epoch = epoch; }
            }
            else if (type == Entities.RemoveItem)
            {
                uint net = r.U32();
                if (r.Bad) return;
                _known.Remove(net); _cargoLocal.Remove(net); _cargoRot.Remove(net);
                if (Holding == net) Holding = 0;
            }
            else if (type == Entities.Owner)
            {
                uint net = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                if (r.Bad) return;
                var k = Get(net);
                if (epoch < k.Epoch) return;
                bool was = k.Owner == MyId && MyId > 0, now = owner == MyId && MyId > 0;
                bool changed = epoch != k.Epoch;
                k.Owner = owner; k.Epoch = epoch;
                // ours until now, someone else's from this epoch on: our last word on it goes to them (Entities: SendHandover)
                if (was && !now && changed) { SendHandover(net, epoch, k.Handover, send); k.Handover = null; Lost(net); }
                if (net == _wantNet)
                {
                    _wantNet = 0;
                    if (now) Won(net);
                    else { ClaimsLost++; Last = "claim on " + net + " (" + _wantReason + ") went to player " + owner; Refused(net); }
                }
                else if (now && !was && _car == net && !_carOwned) Won(net);
                else if (now && !was && changed && !_cargoLocal.ContainsKey(net)) _fresh.Add(net);   // handed to it (the server's hand-off): its word once
            }
            else if (type == Entities.HandoverState)
            {
                // the old owner's records, passed on by the server: kept, and handed back on when it goes again
                int start = r.Pos - 1;
                uint net = r.U32(), epoch = r.U32();
                if (r.Bad || !_known.TryGetValue(net, out var k) || k.Epoch != epoch || k.Owner != MyId) return;
                k.Handover = new byte[r.End - start - 9];
                Buffer.BlockCopy(r.Buf, start + 9, k.Handover, 0, k.Handover.Length);
            }
        }

        private Known Get(uint net) { if (!_known.TryGetValue(net, out var k)) _known[net] = k = new Known(); return k; }

        private void SendHandover(uint net, uint epoch, byte[] records, Action<NetWriter, bool> send)
        {
            if (records == null || records.Length < 2) return;   // nothing it was handed: nothing to pass on (a real client always has a record)
            _w.Reset(); _w.U8(Entities.HandoverState); _w.U32(net); _w.U32(epoch); _w.Bytes(records, 0, records.Length);
            send(_w, true); HandoversSent++;
        }

        /// It lost what it had: the car out from under it (it gets out, as a real driver is moved off the wheel), an item
        /// out of its hands (it lets go), cargo (it stops moving it).
        private void Lost(uint net)
        {
            _cargoLocal.Remove(net); _cargoRot.Remove(net);
            if (Holding == net) { Holding = 0; Last = "lost " + net + " out of its hands"; }
            if (_car == net && _carOwned) { _carOwned = false; _speed = _targetSpeed = 0f; SeatNet = 0; _car = 0; Last = "lost the car " + net; Doing = "idle"; }
        }

        private void Won(uint net)
        {
            if (_car == net)
            {
                _carOwned = true; _contentsTaken = false; _carSeeded = false; Doing = "at the wheel of " + net; Last = "took car " + net;
                _carMoving = false; _sendAcc = 1.0;   // the first state at once
                return;
            }
            if (_act == Act.Take && _actNet == net) { Holding = net; _handled.Add(net); _act = Act.Idle; Doing = "holding " + net; Last = "took " + net; }
        }

        private void Refused(uint net)
        {
            if (_car == net) { SeatNet = 0; _car = 0; _carOwned = false; Doing = "idle"; }   // someone else drives it: off the seat
            if (_act == Act.Take && _actNet == net) { _act = Act.Idle; Doing = "idle"; }       // lost the grab: hands empty
        }

        // ------------------------------------------------------------------ every frame

        public void Tick(double now, float dt, IPlayerBotWorld w, Action<NetWriter, bool> send)
        {
            _send = send; World = w;
            if (Silent || MyId <= 0) return;
            switch (_act)
            {
                case Act.Walk:
                    if (WalkTo(_target, dt, w)) { _act = Act.Idle; Doing = "idle"; }
                    break;
                case Act.Board:
                    if (!WalkTo(CarPoint(_actNet, _carInfo.DoorLocal, w), dt, w)) break;
                    _act = Act.Idle; _car = _actNet; SeatNet = _actNet; SeatIdx = _carInfo.DriverSeat;   // sits down: the seat goes out with the next state
                    if (Owns(_actNet)) Won(_actNet);
                    else Claim(_actNet, Entities.ClaimReason.Driver, now, send);
                    Doing = "claiming car " + _actNet;
                    break;
                case Act.Take:
                    if (!w.Pose(_actNet, out var ip, out _)) { _act = Act.Idle; Doing = "idle"; Last = "item " + _actNet + " gone"; break; }
                    if (_wantNet == _actNet) break;
                    if (!WalkTo(ip, dt, w, ReachM)) break;
                    if (Owns(_actNet)) Won(_actNet); else Claim(_actNet, Entities.ClaimReason.Pickup, now, send);
                    break;
            }
            // no answer to the driver's claim: once more, as the game does (DriverRetry)
            if (_wantNet != 0 && _wantNet == _car && now - _claimAt > ClaimRetryS && _claimTries < 3) Claim(_wantNet, Entities.ClaimReason.DriverRetry, now, send);
            if (SeatNet != 0) Pos = CarPoint(SeatNet, _carInfo.SeatLocal, w);
            if (_carOwned) Steer(now, dt, w, send);
            _sendAcc += dt;
            if (_sendAcc < 1.0 / Protocol.StateHz) return;
            _sendAcc = Math.Min(_sendAcc - 1.0 / Protocol.StateHz, 1.0 / Protocol.StateHz);
            if (_carOwned) SendCar(false, true);
            if (_carOwned && (_carMoving || _speed > 0.05f)) foreach (var n in _cargoLocal.Keys) SendCargo(n, false);
            if (Holding != 0 && Owns(Holding)) { WriteItem(Holding, Hand(), YawRot(), Vector3.zero, true, false); StatesSent++; }
            // what the server handed it without asking (Entities.Handoff: the nearest player simulates it): a real client's
            // physics would hold it where it lies and send one fresh state; the bot has no physics, so that one state only
            foreach (var n in _fresh)
            {
                if (!Owns(n) || n == Driving || n == Holding || !w.Pose(n, out var fg, out var fq)) continue;
                if (w.Car(n, out var fc)) WriteRest(n, fg, fq, fc.Wheels); else WriteItem(n, fg, fq, Vector3.zero, false, true);
                StatesSent++; FinalsSent++; Handed++;
            }
            _fresh.Clear();
        }

        private readonly List<uint> _fresh = new List<uint>();
        public long Handed;
        private void WriteRest(uint net, Vector3d g, Quaternion q, int wheels)
        {
            _w.Reset();
            Entities.WriteStateHead(_w, net, EpochOf(net), ++_seq, Clock, g, q, Vector3.zero, false, final: true);
            int n = Math.Min(wheels, 16); _w.U8((byte)n);
            for (int i = 0; i < n; i++) { _w.U8(0); _w.U8(0); _w.U16(0); }
            _w.U8(1); _w.U16(0);                                            // engine off
            _w.U8(0); _w.U8(0);
            _send?.Invoke(_w, true);
        }

        private void Claim(uint net, Entities.ClaimReason reason, double now, Action<NetWriter, bool> send)
        {
            _w.Reset(); _w.U8(Entities.Claim); _w.U32(net); _w.U8((byte)reason);
            send(_w, true); ClaimsSent++;
            if (reason != Entities.ClaimReason.Contents) { _wantNet = net; _wantReason = reason; _claimAt = now; _claimTries = reason == Entities.ClaimReason.DriverRetry ? _claimTries + 1 : 1; }
        }

        private bool WalkTo(Vector3d to, float dt, IPlayerBotWorld w, double within = 0.3)
        {
            double dx = to.x - Pos.x, dz = to.z - Pos.z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d <= within) return true;
            double step = Math.Min(d - within * 0.5, WalkMps * dt);
            double nx = Pos.x + dx / d * step, nz = Pos.z + dz / d * step;
            Pos = new Vector3d(nx, w.Ground(nx, nz, out double gy) ? gy : Pos.y, nz);
            Yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            return d - step <= within;
        }

        // ------------------------------------------------------------------ its car

        private Action<NetWriter, bool> _send;
        private void Steer(double now, float dt, IPlayerBotWorld w, Action<NetWriter, bool> send)
        {
            if (!_carSeeded)
            {
                // just taken: the car where this machine shows it (the last owner's state), and from then on its own model
                if (!w.Pose(_car, out var p, out var q)) return;
                _carPos = p; _carRot = q; _carSeeded = true;
                if (w.Ground(_carPos.x, _carPos.z, out double gy)) _carGroundOff = _carPos.y - gy;
            }
            if (!_carMoving && _speed <= 0f) TakeContents(now, w, send);
            if (now >= _driveUntil) _targetSpeed = 0f;
            float a = _targetSpeed > _speed ? AccelMps2 : BrakeMps2;
            _speed = Mathf.MoveTowards(_speed, _targetSpeed, a * dt);
            if (_speed <= 0f)
            {
                if (_carMoving) { _carMoving = false; SendCar(true, true); foreach (var n in _cargoLocal.Keys) SendCargo(n, true); Doing = "at the wheel of " + _car; }
                return;
            }
            _carMoving = true;
            var f = Rotate(_carRot, new Vector3(0, 0, 1));
            double fl = Math.Sqrt(f.x * f.x + f.z * f.z); if (fl < 1e-3) fl = 1;
            double nx = _carPos.x + f.x / fl * _speed * dt, nz = _carPos.z + f.z / fl * _speed * dt;
            double ny = w.Ground(nx, nz, out double g2) ? g2 + _carGroundOff : _carPos.y;
            _carPos = new Vector3d(nx, ny, nz);
        }

        private bool _contentsTaken;
        /// The car is its own now: what lies in it comes with it (Entities.ClaimContents, reason Contents) — once.
        private void TakeContents(double now, IPlayerBotWorld w, Action<NetWriter, bool> send)
        {
            if (_contentsTaken) return;
            _contentsTaken = true;
            _scratch.Clear(); w.Cargo(_car, _scratch);
            foreach (var n in _scratch)
            {
                if (!w.Pose(n, out var g, out var q)) continue;
                _cargoLocal[n] = Rotate(Conj(_carRot), Sub(g, _carPos)); _cargoRot[n] = Mul(Conj(_carRot), q);
                if (!Owns(n)) Claim(n, Entities.ClaimReason.Contents, now, send);
            }
        }

        private Vector3 CarVelocity()
        {
            var f = Rotate(_carRot, new Vector3(0, 0, 1));
            float fl = Mathf.Sqrt(f.x * f.x + f.z * f.z); if (fl < 1e-3f) fl = 1f;
            return new Vector3(f.x / fl * _speed, 0f, f.z / fl * _speed);
        }

        private void SendCar(bool final, bool driven)
        {
            if (_send == null || !Owns(_car) || !_carSeeded) return;
            _w.Reset();
            Entities.WriteStateHead(_w, _car, EpochOf(_car), ++_seq, Clock, _carPos, _carRot, CarVelocity(), driven && SeatNet == _car, final: final);
            int n = Math.Min(_carInfo.Wheels, 16);
            _w.U8((byte)n);                                                 // the owner's wheels: hanging mid-travel, no steer
            short rpm = (short)Mathf.Clamp(_speed / 0.35f * 60f / (2f * Mathf.PI), 0f, 32767f);
            for (int i = 0; i < n; i++) { _w.U8(0); _w.U8(0); _w.U16((ushort)rpm); }
            _w.U8(1 | 2); _w.U16((ushort)(800 + _speed * 90f));            // engine running, its rpm with the speed
            _w.U8(0);                                                       // no car signals (Entities.CarSignals)
            _w.U8(0);                                                       // no limbs (Entities.Ragdoll)
            _send(_w, final); StatesSent++; if (final) FinalsSent++;
        }

        private void SendCargo(uint net, bool final)
        {
            if (_send == null || !Owns(net) || !_cargoLocal.TryGetValue(net, out var lp)) return;
            WriteItem(net, Add(_carPos, Rotate(_carRot, lp)), Mul(_carRot, _cargoRot[net]), final ? Vector3.zero : CarVelocity(), false, final);
            StatesSent++; if (final) FinalsSent++;
        }

        public IPlayerBotWorld World;   // for the lock of an item it moves (set by the wrapper)
        /// Items it picked up: a player's game unlocks what it picks up (out of a car's lock, a box) and lets it go loose,
        /// so the lock its copy here still shows is no longer theirs to send. Before (lobby3 runs 5-6): the bot held an item
        /// taken out of a car and went on sending "locked in the car" states, the server never saw it held and gave it to
        /// the car's owner 2.7 s later (Contents); put on a roof, it stayed at the bot's hand on every screen.
        private readonly HashSet<uint> _handled = new HashSet<uint>();
        private void WriteItem(uint net, Vector3d g, Quaternion q, Vector3 v, bool held, bool final)
        {
            var lk = World != null && !held && !_handled.Contains(net) ? World.Lock(net) : default;
            if (lk.Locked && !lk.Known) return;   // stored where it can't say how: the copies keep riding the container
            _w.Reset();
            if (lk.Locked && lk.Frame != 0)
                Entities.WriteStateHead(_w, net, EpochOf(net), ++_seq, Clock, g, q, v, false, carriedBy: lk.Frame, localPos: lk.LocalPos, localRot: lk.LocalRot,
                                        final: final, locked: true, lockItem: lk.Item, lockMount: lk.Mount);
            else if (lk.Locked)
                Entities.WriteStateHead(_w, net, EpochOf(net), ++_seq, Clock, g, q, v, false, localPos: lk.LocalPos, localRot: lk.LocalRot,
                                        final: final, locked: true, lockItem: lk.Item, lockMount: lk.Mount, lockContainer: lk.Container);
            else Entities.WriteStateHead(_w, net, EpochOf(net), ++_seq, Clock, g, q, v, false, held: held, final: final);
            _w.U8(0); _w.U8(0); _w.U8(0); _w.U8(0);                          // no wheels, no engine, no car signals, no limbs
            _send?.Invoke(_w, final);
        }

        public double Clock;                                                // the sender's clock for state stamps (set each frame)

        private Vector3d Hand() => Add(Pos, Rotate(YawRot(), new Vector3(0.25f, 1.2f, 0.5f)));
        private Quaternion YawRot() { float h = Yaw * Mathf.Deg2Rad * 0.5f; return new Quaternion(0f, Mathf.Sin(h), 0f, Mathf.Cos(h)); }

        private Vector3d CarPoint(uint car, Vector3 local, IPlayerBotWorld w)
        {
            if (_carOwned && car == _car) return Add(_carPos, Rotate(_carRot, local));
            return w.Pose(car, out var p, out var q) ? Add(p, Rotate(q, local)) : Pos;
        }

        // ------------------------------------------------------------------ small math (no Unity operators: unit-testable)

        internal static Vector3d Add(Vector3d a, Vector3 b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
        internal static Vector3 Sub(Vector3d a, Vector3d b) => new Vector3((float)(a.x - b.x), (float)(a.y - b.y), (float)(a.z - b.z));
        internal static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
        internal static Quaternion Mul(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        internal static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            // v' = v + 2w(u×v) + 2u×(u×v), u = (x, y, z)
            float cx = q.y * v.z - q.z * v.y, cy = q.z * v.x - q.x * v.z, cz = q.x * v.y - q.y * v.x;
            float dx = q.y * cz - q.z * cy, dy = q.z * cx - q.x * cz, dz = q.x * cy - q.y * cx;
            return new Vector3(v.x + 2f * (q.w * cx + dx), v.y + 2f * (q.w * cy + dy), v.z + 2f * (q.w * cz + dz));
        }
    }
}
