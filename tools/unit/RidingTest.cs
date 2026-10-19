#pragma warning disable CS8981   // Aliases for the game types, implemented by test doubles below.
global using mainscript = TestMain;
global using fpscontroller = TestPlayer;
global using mpplayerscript = TestRemotePlayer;
global using playermodeloutfitscript = TestOutfit;
global using playeranimationscript = TestAnimation;
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;


static class RidingTest
{
    static int checks;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static bool Near(Vector3 a, Vector3 b, float tolerance = .003f) => (a - b).magnitude < tolerance;
    static PlayerLook.Pose Pose(double stamp, double clock, uint net = 1, uint epoch = 1, int seat = -1)
    {
        var p = new PlayerLook.Pose { Stamp = stamp, Clock = clock, Net = net, Epoch = epoch, Seat = seat, Body = new Vector3((float)clock, 1, 0) };
        p.V[5] = new Vector3(0, .5f, 0);
        return p;
    }
    static void Main()
    {
        var samples = new RidingPose(); var shown = new PlayerLook.Pose();
        samples.Add(Pose(1001, 1)); samples.Add(Pose(1002, 2));
        samples.Sample(1.5, shown);
        Check(Near(shown.Body, new Vector3(1.5f, 1, 0)), "passenger sender clock differs by 1000 s; playback uses vehicle clock");
        Check(shown.Clock == 1.5 && shown.V[5].y == .5f, "body and limbs share a sample");
        samples.Add(Pose(1001.5, 50)); samples.Sample(1.5, shown);
        Check(shown.Body.x == 1.5f, "reordered pose cannot rewind or poison frame");
        samples.Sample(20, shown); Check(shown.Body.x == 2, "loss holds passenger local pose without extrapolating off vehicle");
        samples.Add(Pose(1003, 3, seat: 8)); samples.Sample(0, shown);
        Check(shown.Seat == 8 && shown.Body.x == 3, "seat entry never blends through roof pose");
        samples.Add(Pose(1004, 0, epoch: 2, seat: 8)); samples.Sample(0, shown);
        Check(shown.Epoch == 2 && shown.Body.x == 0, "owner epoch resets carrier clock");
        samples.Add(Pose(1005, 4, net: 2)); samples.Sample(2, shown);
        Check(shown.Net == 2 && shown.Body.x == 4, "different vehicles cannot mix local coordinates");
        samples.Add(Pose(1006, 0, net: 0)); Check(samples.Latest.Net == 0, "exit clears riding frame");
        samples.Add(Pose(1007, 5)); samples.Add(Pose(1008, 6));
        samples.Sample(5.5, shown);
        var frame = new Transform { position = new Vector3(100, 4, 200), rotation = Quaternion.Euler(12, 90, 8) };
        var mp = new mpplayerscript();
        PlayerLook.ApplyRiding(mp, shown, frame);
        Check(Near(mp.anim.Pelvis.position, frame.TransformPoint(shown.Body + shown.V[5])), "pelvis composed in tilted, turning vehicle frame");
        frame.position += new Vector3(2, 0, 0);
        PlayerLook.ApplyRiding(mp, shown, frame);
        Check(Near(mp.anim.Pelvis.position, frame.TransformPoint(shown.Body + shown.V[5])), "same local sample follows vehicle on a frame without a packet");
        Check(!mp.anim.gotUpd, "arrival-time game interpolation cannot overwrite sampled targets");
        Wire(); Clock(); Timeline();
        Console.WriteLine("ALL PASS (" + checks + " riding checks)");
    }
    static void Wire()
    {
        var pl = mainscript.s.player; var a = pl.anim;
        a.T.position = new Vector3(1, 2, 3); a.Pelvis.position = new Vector3(1, 2.5f, 3);
        var w = new NetWriter();
        foreach (int seat in new[] { -1, 0, 18 })
        {
            Entities.Seat = seat; w.Reset(); Check(PlayerLook.WriteLocalPose(w), "local pose writes");
            var r = new NetReader { Buf = w.Buf, End = w.Len }; var p = PlayerLook.ReadPose(r);
            Check(p != null && !r.Bad && r.Pos == r.End && p.Net == 1 && p.Epoch == 3 && p.Seat == seat && p.Clock == 12, "riding wire roundtrip includes frame/epoch/owner clock");
            Check(Near(p.Body, a.T.position) && Near(p.V[5], new Vector3(0, .5f, 0)), "root and pelvis belong to same wire snapshot");
            r = new NetReader { Buf = w.Buf, End = w.Len - 1 }; Check(PlayerLook.ReadPose(r) == null, "truncated pose rejected");
        }
        Entities.Net = 0; w.Reset(); PlayerLook.WriteLocalPose(w);
        Check(PlayerLook.ReadPose(new NetReader { Buf = w.Buf, End = w.Len }).Net == 0, "walking pose has no vehicle");
        Entities.Net = 1; a.T.position = new Vector3(float.NaN, 0, 0); w.Reset(); PlayerLook.WriteLocalPose(w);
        Check(PlayerLook.ReadPose(new NetReader { Buf = w.Buf, End = w.Len }) == null, "nonfinite local body rejected");
    }
    static void Timeline()
    {
        var ip = new PoseInterpolator { MaxCoast = 1 }; var tail = new BandwidthRules.Tail();
        var packets = new Queue<(double arrival, PoseInterpolator.Sample sample)>();
        double sentAt = -1, lastX = -1; int sent = 0, held = 0, interpolated = 0, extrapolated = 0; double maxCorrection = 0;
        var rider = new RidingPose(); var p = Pose(1, 0); p.Body = new Vector3(0, 1, 0); rider.Add(p);
        var shown = new PlayerLook.Pose();
        for (int frame = 0; frame < 1200; frame++)
        {
            double now = frame / 120.0;
            if (frame % 6 == 0)
            {
                // 20 m/s bus, identical wheels/engine/signals: actual root movement must retain the full cadence.
                double x = now * 20;
                bool moved = BandwidthRules.PoseChanged((x - lastX) * (x - lastX), 0);
                var quiet = BandwidthRules.StateQuiet(ref tail, now, sent == 0, moved, false, false, false, true, false, false, false, now - sentAt, driven: true);
                Check(quiet == BandwidthRules.Quiet.None, "visible-change rule cannot starve translating bus");
                sent++; sentAt = now; lastX = x;
                // 150 ms one-way plus deterministic jitter and a 400 ms outage.
                if (!(now >= 4 && now < 4.4)) packets.Enqueue((now + .15 + (frame % 18) * .001, new PoseInterpolator.Sample { T = now, Pos = new Vector3d(x, 0, 0), Rot = Quaternion.identity, Vel = new Vector3(20, 0, 0) }));
            }
            while (packets.Count > 0 && packets.Peek().arrival <= now)
            { var packet = packets.Dequeue(); ip.Add(packet.sample, packet.arrival); maxCorrection = Math.Max(maxCorrection, ip.TelemetryCorrection); }
            if (!ip.Ready) continue;
            ip.Evaluate(now, 1f / 120, out var car, out _);
            if (now < 1) continue;
            if (ip.TelemetryMode == 2) held++; else if (ip.Extrapolating) extrapolated++; else interpolated++;
            rider.Sample(ip.RenderTime, shown);
            var world = (Vector3)car + shown.Body;
            Check(Near(world - (Vector3)car, p.Body), "passenger remains fixed through jitter and outage");
        }
        Check(sent == 200 && held == 0 && interpolated > 800, "bus stays mostly interpolated and never holds during 400 ms outage");
        Check(maxCorrection < .01, "constant-velocity outage needs no large corrections");
        // These are deterministic replay observations, not internet-playtest measurements.
        Console.WriteLine("TRACE replay: sentHz=20 renderedHz=120 interpolate=" + interpolated + " extrapolate=" + extrapolated + " hold=" + held + " correctionM=" + maxCorrection.ToString("F6"));
        Console.WriteLine("TRACE before: 20 m/s * 1/60 s stale world targets = 0.333 m; after: vehicle-frame residual <0.003 m; seated physics-only 50 Hz restored to render cadence");
    }
    static void Clock()
    {
        var e = Entities.ByNet[1];
        e.Proxy = true; e.MovedByBody = true; e.Ip = new PoseInterpolator();
        e.Ip.Add(new PoseInterpolator.Sample { T = 5, Rot = Quaternion.identity }, 10);
        e.Ip.Evaluate(10.2, .02f, out _, out _);
        double current = e.Ip.RenderTime; e.RidingPreviousClock = current - .02;
        Time.timeAsDouble = 10.01; Time.fixedTimeAsDouble = 10;
        e.Root.RB.interpolation = RigidbodyInterpolation.Interpolate;
        Entities.RidingClock(1, out uint epoch, out double clock);
        Check(epoch == 3 && Math.Abs(clock - (current - .01)) < 1e-6, "rider uses same blend of physics samples as drawn Rigidbody");
        Check(e.Ip.RenderTime == current, "reading rider clock never evaluates or advances vehicle twice");
        e.Root.RB.interpolation = RigidbodyInterpolation.None;
        Entities.RidingClock(1, out _, out clock);
        Check(clock == current, "physics-only A/B uses current physics clock");
        e.Proxy = false; Entities.RidingClock(1, out _, out clock);
        Check(Math.Abs(clock - (Time.unscaledTimeAsDouble - .01)) < 1e-6, "driver uses vehicle state stamp clock");
        Check(!Entities.RidingClock(999, out _, out _), "unloaded carrier cannot supply invented clock");
    }
}

// Only game/Unity surfaces are faked. Wire, riding sample selection, application, traffic and interpolation are production code.
namespace TLDRevamp.Net
{
    public static class Protocol { public const int StateHz = 20; }
    public static partial class Entities
    {
        public class Ent
        {
            public uint Epoch = 3; public bool Proxy, MovedByBody; public PoseInterpolator Ip;
            public double RidingPreviousClock; public TestRoot Root = new TestRoot();
        }
        public class TestRoot { public Rigidbody RB = new Rigidbody(); }
        internal static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent> { [1] = new Ent() };
        static void TelemetryPose(Ent e, int kind, double clock, Vector3d p, Quaternion q, Vector3 v, int player, int seat, int mode) { }
        public static uint Net = 1; public static int Seat;
        public struct Footing { public uint Net; }
        public static void LocalSeat(out uint net, out int seat) { seat = Seat; net = Seat >= 0 ? Net : 0; }
        public static void LocalFooting(out Footing f) { f = new Footing { Net = Net }; }
        public static Transform FootRootOf(uint net) => new Transform();
        public static Transform SeatTransform(uint net, int seat) => new Transform();
        public static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
    public static class PlayerActs
    {
        public class Acts { }
        public static void Write(NetWriter w, fpscontroller p, Transform t) { w.U8(0); }
        public static Acts Read(NetReader r) { r.U8(); return new Acts(); }
        public static void Apply(mpplayerscript mp, Acts a) { }
    }
}
public class TestMain
{
    public static mainscript s = new mainscript(); public fpscontroller player = new fpscontroller();
    public static Vector3d GlobalFromUnityPos(Vector3 p) => p;
    public byte WhichMenusOpen() => 0;
    public void SetMenus(byte b, object m) { }
}
public class TestPlayer
{
    public playeranimationscript anim = new playeranimationscript(); public Transform Th = new Transform(); public bool zooming;
    public playermodeloutfitscript outfit = new playermodeloutfitscript();
}
public class TestRemotePlayer
{
    public playeranimationscript anim = new playeranimationscript(); public bool zooming; public object GMenus;
}
public class TestOutfit
{
    public void GetData(out int selected, out bool[] enabled, out Color[] colors) { selected = 0; enabled = null; colors = null; }
}
public class TestAnimation
{
    public Transform T = new Transform(), transform = new Transform(), HHTarget = new Transform(), LHTarget = new Transform(), RHTarget = new Transform(), LLTarget = new Transform(), RLTarget = new Transform(), Pelvis = new Transform(), Th = new Transform();
    public bool gotUpd = true;
    public void setPose(params Vector3[] v) { }
    public void SetThRotX(float x) { }
}
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public double sqrMagnitude => x * x + y * y + z * z;
    public double magnitude => Math.Sqrt(sqrMagnitude);
    public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3d operator *(Vector3d a, double n) => new Vector3d(a.x * n, a.y * n, a.z * n);
    public static implicit operator Vector3d(Vector3 p) => new Vector3d(p.x, p.y, p.z);
    public static explicit operator Vector3(Vector3d p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
}
namespace UnityEngine
{
    public static class Time { public static double realtimeSinceStartupAsDouble = 123, unscaledTimeAsDouble = 12, timeAsDouble = 12, fixedTimeAsDouble = 12; public static float fixedDeltaTime = .02f; }
    public enum RigidbodyInterpolation { None, Interpolate }
    public class Rigidbody { public RigidbodyInterpolation interpolation; }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public class Transform
    {
        public Vector3 position; public Quaternion rotation = Quaternion.identity;
        public Vector3 eulerAngles => rotation.eulerAngles;
        public Vector3 localEulerAngles => eulerAngles;
        public Quaternion localRotation;
        public Vector3 TransformPoint(Vector3 p) => position + rotation * p;
        public Vector3 InverseTransformPoint(Vector3 p) => Quaternion.Inverse(rotation) * (p - position);
        public void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => this / magnitude;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float n) => new Vector3(a.x * n, a.y * n, a.z * n);
        public static Vector3 operator /(Vector3 a, float n) => a * (1 / n);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float n) => a + (b - a) * n;
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        static System.Numerics.Quaternion N(Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
        static Quaternion U(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public static Quaternion Euler(float x, float y, float z) => U(System.Numerics.Quaternion.CreateFromYawPitchRoll(y * MathF.PI / 180, x * MathF.PI / 180, z * MathF.PI / 180));
        public static Quaternion Euler(Vector3 v) => Euler(v.x, v.y, v.z);
        public Vector3 eulerAngles
        {
            get
            {
                var m = System.Numerics.Matrix4x4.CreateFromQuaternion(N(this));
                return new Vector3(MathF.Asin(Math.Clamp(-m.M32, -1, 1)), MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22)) * (180 / MathF.PI);
            }
        }
        public static Quaternion Inverse(Quaternion q) => U(System.Numerics.Quaternion.Inverse(N(q)));
        public static Quaternion operator *(Quaternion a, Quaternion b) => U(N(a) * N(b));
        public static Vector3 operator *(Quaternion a, Vector3 b) { var v = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(b.x, b.y, b.z), N(a)); return new Vector3(v.X, v.Y, v.Z); }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => U(System.Numerics.Quaternion.Slerp(N(a), N(b), t));
    }
    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static int RoundToInt(float v) => (int)MathF.Round(v);
        public static float Repeat(float v, float length) => v - MathF.Floor(v / length) * length;
        public static float LerpAngle(float a, float b, float t) => a + (Repeat(b - a + 180, 360) - 180) * t;
        public static float MoveTowards(float a, float b, float d) => a + Math.Clamp(b - a, -d, d);
    }
}
