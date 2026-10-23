using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TLDRevamp.Net;
using UnityEngine;

/// Offline check of the player bot (src/TLDRevamp/Net/PlayerBotCore.cs) against a fake world: what it sends is what a
/// real client sends for the same thing, byte for byte where the format is shared (claims, the state head), and in the
/// order a real client sends it (seat, claim, then states only once the server made it the owner).
static class PlayerBotTest
{
    static int checks, fails;
    static void Check(bool ok, string name) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL " + name); } else Console.WriteLine("PASS " + name); }

    sealed class World : IPlayerBotWorld
    {
        public readonly Dictionary<uint, (Vector3d g, Quaternion q)> Poses = new Dictionary<uint, (Vector3d, Quaternion)>();
        public readonly HashSet<uint> InCar = new HashSet<uint>();
        public readonly Dictionary<uint, PlayerBotLock> Locks = new Dictionary<uint, PlayerBotLock>();
        public bool Pose(uint net, out Vector3d g, out Quaternion q) { bool ok = Poses.TryGetValue(net, out var p); g = p.g; q = ok ? p.q : Quaternion.identity; return ok; }
        public bool Ground(double gx, double gz, out double gy) { gy = 100; return true; }
        public bool Car(uint net, out PlayerBotCar car)
        {
            car = new PlayerBotCar { DriverSeat = -1 };
            if (net != 10) return false;
            car = new PlayerBotCar { DriverSeat = 2, Wheels = 4, SeatLocal = new Vector3(-0.4f, 0.8f, 0.2f), DoorLocal = new Vector3(-1.7f, 0.4f, 0.2f) };
            return true;
        }
        public void Cargo(uint carNet, List<uint> into) { if (carNet == 10) into.AddRange(InCar); }
        public PlayerBotLock Lock(uint net) => Locks.TryGetValue(net, out var l) ? l : default;
    }

    sealed class Sent { public byte[] Bytes; public bool Reliable; public byte Type => Bytes[0]; }
    static readonly List<Sent> Out = new List<Sent>();
    static void Send(NetWriter w, bool reliable) { var b = new byte[w.Len]; Array.Copy(w.Buf, b, w.Len); Out.Add(new Sent { Bytes = b, Reliable = reliable }); }
    static NetReader Reader(NetWriter w) { var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len); return r; }

    static void Deliver(PlayerBotCore bot, NetWriter w) { var r = Reader(w); byte t = r.U8(); bot.Receive(t, r, Send); }
    static NetWriter AddMsg(uint net, int owner, uint epoch) { var w = new NetWriter(); w.U8(Entities.Add); w.U32(net); w.VarU32((uint)owner); w.U32(epoch); w.Bool(false); w.VarU32(0); return w; }
    static NetWriter OwnerMsg(uint net, int owner, uint epoch) { var w = new NetWriter(); w.U8(Entities.Owner); w.U32(net); w.VarU32((uint)owner); w.U32(epoch); return w; }

    struct Head { public uint Net, Epoch; public Vector3d G; public Quaternion Q; public Vector3 V; public bool Driven, Stored, Held, Final; public int Wheels; }
    static Head Decode(Sent s)
    {
        var r = new NetReader(); r.Load(s.Bytes.Length); Buffer.BlockCopy(s.Bytes, 0, r.Buf, 0, s.Bytes.Length);
        r.U8();
        Entities.ReadStateHead(r, out uint net, out uint epoch, out _, out var g, out var q, out var v, out bool driven, out bool stored, out bool held, out _);
        var h = new Head { Net = net, Epoch = epoch, G = g, Q = q, V = v, Driven = driven, Stored = stored, Held = held, Final = Entities.TestFinal };
        h.Wheels = r.U8() & 0x7F;
        return h;
    }
    static List<Sent> Of(byte type) => Out.FindAll(s => s.Type == type);
    static Quaternion Yaw(float deg) { float h = deg * Mathf.Deg2Rad * 0.5f; return new Quaternion(0, Mathf.Sin(h), 0, Mathf.Cos(h)); }
    static double Dist(Vector3d a, Vector3d b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "src/TLDRevamp/Net")) && Directory.GetParent(root) != null) root = Directory.GetParent(root).FullName;
        SourceConstants(root);

        var w = new World();
        var carRot = Yaw(90);                                         // facing +x
        w.Poses[10] = (new Vector3d(1000, 101, 2000), carRot);
        w.Poses[20] = (new Vector3d(998.5, 101.6, 2000), carRot);      // in the bed, 1.5 m behind the centre
        w.Poses[21] = (new Vector3d(1010, 100.1, 2005), Quaternion.identity);
        w.InCar.Add(20);
        var bot = new PlayerBotCore("pbot1", new Vector3d(1003, 100, 1995)) { MyId = 3 };
        Deliver(bot, AddMsg(10, 1, 4)); Deliver(bot, AddMsg(20, 1, 2)); Deliver(bot, AddMsg(21, 0, 1));
        Check(bot.OwnerOf(10) == 1 && bot.EpochOf(10) == 4, "Add: the owner and epoch it learns");

        double t = 0; const float dt = 1f / 60f;
        void Run(double seconds) { for (double end = t + seconds; t < end; t += dt) { bot.Clock = t; bot.Tick(t, dt, w, Send); } }

        // ---- take the wheel: walk to the door, sit (the seat in its state), claim (Driver); nothing moved before the answer
        Check(bot.Enter(10, w) == null, "enter: a car with a driver seat");
        Run(3);
        var claims = Of(Entities.Claim);
        Check(claims.Count == 1 && claims[0].Reliable && claims[0].Bytes.Length == 6 && BitConverter.ToUInt32(claims[0].Bytes, 1) == 10 &&
              claims[0].Bytes[5] == (byte)Entities.ClaimReason.Driver, "enter: one reliable Driver claim, the real client's 6 bytes");
        Check(bot.SeatNet == 10 && bot.SeatIdx == 2, "enter: seated in the driver seat (the player state carries it)");
        Check(Of(Entities.State).Count == 0, "enter: no state for the car before the server says it is the bot's");
        Out.Clear(); Run(2.1);
        claims = Of(Entities.Claim);
        Check(claims.Count == 1 && claims[0].Bytes[5] == (byte)Entities.ClaimReason.DriverRetry, "no answer within 2 s: one DriverRetry claim");

        // ---- the server answers: the car is its own, what lies in it comes with it
        Out.Clear(); Deliver(bot, OwnerMsg(10, 3, 5)); Run(0.2);
        var states = Of(Entities.State).ConvertAll(Decode);
        var car0 = states.Find(h => h.Net == 10);
        Check(states.Count > 0 && car0.Epoch == 5 && car0.Driven && car0.Wheels == 4 && Dist(car0.G, w.Poses[10].g) < 0.01, "owner: car states at once, the new epoch, driven, its four wheels, where the car stands");
        claims = Of(Entities.Claim);
        Check(claims.Count == 1 && BitConverter.ToUInt32(claims[0].Bytes, 1) == 20 && claims[0].Bytes[5] == (byte)Entities.ClaimReason.Contents,
              "owner: what lies in the car is claimed (Contents), nothing else");
        Deliver(bot, OwnerMsg(20, 3, 3));
        var hand = new NetWriter(); hand.U8(Entities.HandoverState); hand.U32(10); hand.U32(5); hand.U16(1); hand.VarU32(0); hand.VarU32(3); hand.U8(7); hand.U8(8); hand.U8(9);
        Deliver(bot, hand);

        // ---- 8 s towards 54 km/h (2.5 m/s² up: 6 s, then 2 s at speed, 3 s braking): forward on the ground, the cargo in its place
        Out.Clear(); Check(bot.Drive(15f, 8f, t) == null, "drive: its own car");
        Run(12);
        states = Of(Entities.State).ConvertAll(Decode);
        var cars = states.FindAll(h => h.Net == 10); var items = states.FindAll(h => h.Net == 20);
        var last = cars[cars.Count - 1];
        double moved = last.G.x - w.Poses[10].g.x;
        Check(moved > 95 && moved < 100 && Math.Abs(last.G.z - 2000) < 0.05 && Math.Abs(last.G.y - 101) < 0.01, $"drive: forward along +x on the ground ({moved:F1} m, 97.5 expected)");
        Check(cars.Count >= 230 && cars.Count <= 245, $"drive: car states at the players' rate ({cars.Count} in 12 s)");
        double worst = 0;
        foreach (var it in items)
        {
            var c = cars.Find(h => h.Epoch == 5 && Math.Abs(h.G.x - (it.G.x + 1.5)) < 1.0);
            worst = Math.Max(worst, Math.Abs(it.G.z - 2000) + Math.Abs(it.G.y - 101.6));
        }
        Check(items.Count >= 200 && worst < 0.01, $"drive: the cargo's states keep it in the bed ({items.Count} states, worst {worst:F3} m off)");
        Check(cars.Exists(h => h.Final) && items.Exists(h => h.Final) && Out.Exists(s => s.Type == Entities.State && s.Reliable), "drive: stopped, a final (reliable) state for the car and its cargo");
        float top = 0; foreach (var h in cars) top = Math.Max(top, h.V.x);
        Check(top > 14.9f && top <= 15.01f, $"drive: up to the speed asked, never faster ({top:F2} m/s)");

        // ---- someone else takes the car: the bot hands its record on and is off the seat
        Out.Clear(); Deliver(bot, OwnerMsg(10, 1, 6)); Run(0.5);
        var hs = Of(Entities.HandoverState);
        Check(hs.Count == 1 && hs[0].Reliable && BitConverter.ToUInt32(hs[0].Bytes, 1) == 10 && BitConverter.ToUInt32(hs[0].Bytes, 5) == 6 && hs[0].Bytes.Length == 9 + 2 + 5,
              "lost the car: the HandoverState for the new epoch with the records it was handed");
        Check(bot.SeatNet == 0 && bot.Driving == 0 && !Of(Entities.State).ConvertAll(Decode).Exists(h => h.Net == 10), "lost the car: off the seat, no more states for it");

        // ---- a grab it loses, then one it wins; held states; let go of it on the car's roof
        Out.Clear(); Check(bot.Take(21, w) == null, "take: an item there");
        Run(22);   // 90 m back from where the car stopped
        claims = Of(Entities.Claim);
        Check(claims.Count == 1 && claims[0].Bytes[5] == (byte)Entities.ClaimReason.Pickup, "take: walks to it, one Pickup claim");
        Deliver(bot, OwnerMsg(21, 2, 2)); Run(0.3);
        Check(bot.Holding == 0 && bot.ClaimsLost == 1 && Of(Entities.State).Count == 0, "lost the grab: hands empty, nothing sent for it");
        Out.Clear(); bot.Take(21, w); Run(0.2); Deliver(bot, OwnerMsg(21, 3, 3)); Run(1);
        var held = Of(Entities.State).ConvertAll(Decode).FindAll(h => h.Net == 21);
        Check(bot.Holding == 21 && held.Count >= 18 && held.TrueForAll(h => h.Held && !h.Final && h.Epoch == 3), "won the grab: held states at the players' rate");
        Out.Clear(); Check(bot.Release(10, new Vector3(0, 1.8f, 0), w, Send) == null, "put: onto the car's roof");
        var put = Of(Entities.State).ConvertAll(Decode);
        Check(put.Count == 1 && !put[0].Held && put[0].Final && Dist(put[0].G, new Vector3d(1000, 102.8, 2000)) < 0.01, "put: one final state, not held, where the car's frame says");
        Out.Clear(); Run(1);
        Check(Of(Entities.State).Count == 0, "at rest: nothing more for it");

        // ---- an item stored in a container it can't describe, handed to it (the server's hand-off): it says nothing about it
        w.Poses[22] = (new Vector3d(1003, 101, 2003), Quaternion.identity);
        w.Locks[22] = new PlayerBotLock { Locked = true };
        Deliver(bot, OwnerMsg(22, 1, 1)); Deliver(bot, OwnerMsg(22, 3, 2)); Out.Clear(); Run(0.5);
        Check(!Of(Entities.State).ConvertAll(Decode).Exists(h => h.Net == 22), "stored, its lock unknown here, handed to it: no states (the copies keep riding the container)");

        // ---- an item locked in the car on this screen, picked up: held states and a loose put (a game unlocks what it
        // picks up; lobby3 runs 5-6: "locked" states while held, the car's owner took it out of its hands by Contents)
        w.Poses[23] = (new Vector3d(1000.5, 101.2, 2000.5), Quaternion.identity);
        w.Locks[23] = new PlayerBotLock { Locked = true, Known = true, Frame = 10, LocalPos = new Vector3(0.5f, 0.2f, 0.5f), LocalRot = Quaternion.identity };
        Out.Clear(); bot.Take(23, w); Run(3); Deliver(bot, OwnerMsg(23, 3, 2)); Run(1);
        var held23 = Of(Entities.State).ConvertAll(s => (h: Decode(s), locked: Entities.TestLocked)).FindAll(x => x.h.Net == 23);
        Check(bot.Holding == 23 && held23.Count >= 18 && held23.TrueForAll(x => x.h.Held && !x.locked), "picked out of a car's lock: held states, not locked ones");
        Out.Clear(); bot.Release(10, new Vector3(0, 1.8f, 0), w, Send);
        var put23 = Of(Entities.State).ConvertAll(s => (h: Decode(s), locked: Entities.TestLocked));
        Check(put23.Count == 1 && put23[0].h.Final && !put23[0].locked && Dist(put23[0].h.G, new Vector3d(1000, 102.8, 2000)) < 0.01, "put back: a loose final state where the car's frame says");

        // ---- a crash: nothing at all any more
        bot.Crash(t); Out.Clear(); Run(2);
        Check(Out.Count == 0 && bot.Silent, "crash: silent");
        bot.Reset();
        Check(bot.OwnerOf(10) == -1 && !bot.Silent && bot.SeatNet == 0, "rejoin: what it knew belonged to the old session");

        Console.WriteLine(fails == 0 ? "ALL PASS (" + checks + " checks)" : fails + " FAILED of " + checks);
        return fails == 0 ? 0 : 1;
    }

    /// The stubs below copy the game's message numbers: they must be the source's.
    static void SourceConstants(string root)
    {
        string net = Path.Combine(root, "src/TLDRevamp/Net");
        string ents = File.ReadAllText(Path.Combine(net, "Entities.cs")), resync = File.ReadAllText(Path.Combine(net, "Entities.Resync.cs"));
        string pickup = File.ReadAllText(Path.Combine(net, "Entities.Pickup.cs")), proto = File.ReadAllText(Path.Combine(net, "Protocol.cs"));
        int Const(string text, string name) { var m = Regex.Match(text, @"\b" + name + @"\s*=\s*(\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : -1; }
        Check(Const(ents, "Add") == Entities.Add && Const(ents, "State") == Entities.State && Const(ents, "Claim") == Entities.Claim && Const(ents, "Owner") == Entities.Owner &&
              Const(ents, "PartDetached") == Entities.PartDetached && Const(ents, "RemoveItem") == Entities.RemoveItem && Const(resync, "HandoverState") == Entities.HandoverState &&
              Const(proto, "StateHz") == Protocol.StateHz, "the test's message numbers are the source's");
        var reasons = Regex.Match(pickup, @"enum ClaimReason : byte\s*\{([^}]*)\}").Groups[1].Value;
        var names = Regex.Split(Regex.Replace(reasons, @"\s|=\s*1", ""), ",");
        Check(Array.IndexOf(names, "Driver") == 0 && Array.IndexOf(names, "DriverRetry") == 1 && Array.IndexOf(names, "Pickup") == 2 && Array.IndexOf(names, "Contents") == 6,
              "the claim reasons are the source's");
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public const byte Add = 22, State = 23, Claim = 24, Owner = 25, PartDetached = 27, RemoveItem = 35, HandoverState = 59;
        internal enum ClaimReason : byte { Driver = 1, DriverRetry, Pickup, CargoNear, CargoContact, CargoRelease, Contents }
        internal static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z);
        internal static bool TestFinal => ReadFinalState;
        internal static bool TestLocked => ReadLockedState;
    }
    public static class Protocol { public const int StateHz = 20; }
}

public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public float sqrMagnitude => x * x + y * y + z * z;
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
    public static class Mathf
    {
        public const float PI = (float)Math.PI, Deg2Rad = PI / 180f;
        public static float Abs(float n) => Math.Abs(n);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Sqrt(float n) => (float)Math.Sqrt(n);
        public static float Sin(float n) => (float)Math.Sin(n);
        public static float Cos(float n) => (float)Math.Cos(n);
        public static float Clamp(float n, float a, float b) => Math.Clamp(n, a, b);
        public static int RoundToInt(float n) => (int)Math.Round(n);
        public static float MoveTowards(float a, float b, float d) => Math.Abs(b - a) <= d ? b : a + Math.Sign(b - a) * d;
    }
}
