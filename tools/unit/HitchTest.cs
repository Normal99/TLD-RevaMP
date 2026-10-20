global using tosaveitemscript = HitchFakes.Item;
global using attachablescript = HitchFakes.Attachment;
global using partslotscript = HitchFakes.Slot;
global using attachgripscript = HitchFakes.Grip;
global using rearlights = HitchFakes.Lights;
global using itemdatabase = HitchFakes.Database;
global using mainscript = HitchFakes.Main;
global using fpscontroller = HitchFakes.Player;
using System;
using System.Collections.Generic;
using TLDRevamp.Net;
using UnityEngine;

static class HitchTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Main() { Entities.Tests(); Console.WriteLine("ALL PASS (" + checks + " hitch checks)"); }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private sealed class Ent
        {
            internal uint NetId, Epoch = 1; internal int OwnerId, HitchAuthorityOwner = -1; internal bool Proxy, HitchWaiting, SentAtRest, HitchHasVelocity;
            internal HashSet<int> Split;
            internal float HitchUntil = -100; internal Vector3 LastVel, HitchVelocity;
            internal tosaveitemscript Root; internal readonly List<tosaveitemscript> Items = new List<tosaveitemscript>();
            internal readonly List<Rigidbody> HitchBodies = new List<Rigidbody>(); internal readonly HashSet<uint> HitchAnnounced = new HashSet<uint>();
        }
        private sealed class SEnt
        {
            internal uint NetId, Epoch = 1, HitchCar, AttachParent; internal int OwnerId, HitchSlot, CrashReturnTo = -1;
            internal bool Hitch, Car, Stored, Held, HasState; internal Vector3d Pos; internal Quaternion Rot;
        }
        private static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        private static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        private static readonly Dictionary<HitchFakes.Car, float> DamageGraceUntil = new Dictionary<HitchFakes.Car, float>();
        private static bool InSession = true, PlayerActing = true, _applyingAttach, _applyingDetach, EditRangeCheck = true, Near = true;
        private static int MyId;
        private const byte Owner = 25;
        private static readonly List<(int target, byte[] data)> Packets = new List<(int, byte[])>();
        private static readonly List<byte[]> Requests = new List<byte[]>();
        private static Ent OwnEntity(tosaveitemscript it) { foreach (var e in ByNet.Values) if (e.Root == it) return e; return null; }
        private static Ent EditEntityOf(tosaveitemscript it) => OwnEntity(it);
        private static bool Resolve(Ent e) => e.Root != null;
        private static bool AtObject(int from, SEnt e) => Near;
        private static HitchFakes.Record EditItemRecord(SEnt e, int idx) => new HitchFakes.Record();
        private static bool Finite(Vector3d v) => double.IsFinite(v.x) && double.IsFinite(v.y) && double.IsFinite(v.z);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Finite(Quaternion q) => float.IsFinite(q.w);
        private static void WriteRot(NetWriter w, Quaternion q) { w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
        private static Quaternion ReadRot(NetReader r) => new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        private static byte[] Copy(NetWriter w) { var b = new byte[w.Len]; Array.Copy(w.Buf, b, b.Length); return b; }
        private static void ToServer(NetWriter w, bool reliable) => Requests.Add(Copy(w));
        private static void ServerSendTo(int target, NetWriter w, bool reliable) => Packets.Add((target, Copy(w)));
        private static void ServerSendAll(NetWriter w, bool reliable, int except) { ServerSendTo(0, w, reliable); ServerSendTo(1, w, reliable); }
        private static void OwnerLog(SEnt e, int target, string reason) { }
        private static void CascadeOwner(SEnt e) => CascadeHitches(e);
        private static void ApplyProxyBodies(Ent e) { foreach (var rb in HitchBodies(e)) rb.isKinematic = true; }
        private static void SendDetachSync(Ent tr) { Server[tr.NetId].HitchCar = Server[tr.NetId].AttachParent = 0; }
        private static bool Unmount(attachablescript at, bool fallOff) { at.Detach(); return true; }
        private static void SendAttach(Ent tr, attachablescript at)
        {
            if (!Server.TryGetValue(tr.NetId, out var se)) return;
            var car = HitchCar(at); se.AttachParent = se.HitchCar = car != null ? car.NetId : 0;
        }
        public static void GameAttach(attachablescript at, partslotscript slot)
        {
            if (!(bool)typeof(HitchSlotHook).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { at, slot })) return;
            at.slot = slot; GameAttach(at, slot.T);
        }
        public static void GameAttach(attachablescript at, Transform point)
        {
            if (!(bool)typeof(HitchJointHook).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { at, point })) return;
            at.attached = true; at.point = point; at.J = new Joint(); at.slot.parts.Add(at);
            var tr = OwnEntity(at.tosave); if (!_applyingAttach && tr != null) SendAttach(tr, at);
        }
        private static NetReader Reader(byte[] b) => new NetReader { Buf = b, Pos = 1, End = b.Length };
        private static void ServerPacket(byte[] b, int from)
        {
            var r = Reader(b);
            switch (b[0])
            {
                case HitchRequest: ServerHitchRequest(from, r); break;
                case HitchFrozen: ServerHitchFrozen(from, r); break;
                case HitchResult: ServerHitchResult(from, r); break;
            }
        }
        private static Ent Make(uint net, int owner, bool trailer)
        {
            var it = new tosaveitemscript(); var e = new Ent { NetId = net, Root = it, OwnerId = owner, HitchAuthorityOwner = owner, Proxy = owner != MyId };
            e.Items.Add(it); ByNet[net] = e; it.RB.isKinematic = e.Proxy;
            if (trailer) { it.attachable = new attachablescript { tosave = it, utanfuto = new HitchFakes.Trailer() }; it.attachable.RB.isKinematic = e.Proxy; }
            else it.car = new HitchFakes.Car();
            return e;
        }
        private static void Grant(Ent tr, int target, uint epoch)
        {
            HitchBeforeOwner(tr, target, epoch); tr.OwnerId = tr.HitchAuthorityOwner = target; tr.Epoch = epoch; tr.Proxy = target != MyId;
            foreach (var rb in HitchBodies(tr)) rb.isKinematic = tr.Proxy;
            if (tr.HitchWaiting) HitchFreezeBodies(tr);
        }
        internal static void Tests()
        {
            var check = (Action<bool, string>)HitchTest.Check;
            for (int carOwner = 0; carOwner < 3; carOwner++)
            for (int trailerOwner = 0; trailerOwner < 3; trailerOwner++)
            for (int me = 0; me < 3; me++)
                check(HitchAuthority.CanJoin(carOwner, trailerOwner, me, false, carOwner != me, trailerOwner != me) == (carOwner == me && trailerOwner == me), "only the common owner can make the joint");
            check(!HitchAuthority.CanJoin(1, 1, 1, true, false, false), "frozen owner cannot join");
            check(!HitchAuthority.CanJoin(1, 1, 1, false, true, false), "provisional authority cannot join a proxy");
            var refusals = new[] { "entity absent", "not a trailer and car", "edit reach", "stale epoch", "held or stored", "slot taken", "already hitched" };
            for (int i = 0; i < refusals.Length; i++)
                check(HitchAuthority.Refusal(i != 0, i != 1, true, i != 2, i == 3, i == 4, i == 5, i == 6) == refusals[i], "specific refusal: " + refusals[i]);

            foreach (int old in new[] { 0, 1 })
            {
                MyId = 1 - old; ByNet.Clear(); Server.Clear(); Packets.Clear(); Requests.Clear();
                _hitchOps.Clear(); _hitchTransfers.Clear(); _hitchCommands.Clear();
                var car = Make(10, MyId, false); var tr = Make(20, old, true);
                var slot = new partslotscript { tosaveitem = car.Root }; car.Root.partslotscripts.Add(slot);
                Server[10] = new SEnt { NetId = 10, OwnerId = MyId, Car = true };
                Server[20] = new SEnt { NetId = 20, OwnerId = old, Hitch = true };
                // The requester invokes the game's slot entry point: no movement, metadata change or joint yet.
                var start = tr.Root.RB.position; tr.Root.attachable.Attach(slot);
                check(!tr.Root.attachable.attached && tr.Root.attachable.J == null && Requests.Count == 1, "non-owner attach is intercepted before any joint");
                check(tr.Root.RB.position.x == start.x, "request does not reposition the proxy");
                ServerPacket(Requests[0], MyId); Requests.Clear();
                check(Packets.Count == 1 && Packets[0].target == old && Packets[0].data[0] == HitchFreeze, "server routes freeze to current trailer owner");
                check(Server[20].OwnerId == old, "no ownership grant before old owner freezes");
                var freeze = Packets[0].data; Packets.Clear();
                MyId = old; tr.Proxy = false; tr.Root.RB.isKinematic = tr.Root.attachable.RB.isKinematic = false;
                ApplyHitchFreeze(Reader(freeze));
                check(tr.HitchWaiting && tr.Root.RB.isKinematic && tr.Root.attachable.RB.isKinematic, "body and separate drawbar freeze together");
                var ack = Requests[0]; Requests.Clear(); ServerPacket(ack, 1 - old);
                check(Server[20].OwnerId == old, "forged acknowledgement cannot transfer authority");
                ServerPacket(ack, old);
                check(Server[20].OwnerId == 1 - old && Server[20].Epoch == 2, "acknowledged transfer increments epoch");
                int grantPackets = Packets.Count; ServerPacket(ack, old);
                check(Packets.Count == grantPackets, "duplicate acknowledgement cannot grant twice");
                MyId = 1 - old; tr.Proxy = true; car.Proxy = false;
                foreach (var packet in Packets.ToArray())
                {
                    if (packet.target != MyId) continue;
                    var r = Reader(packet.data);
                    switch (packet.data[0])
                    {
                        case HitchFreeze: ApplyHitchFreeze(r); check(tr.Root.RB.isKinematic, "new owner prepares frozen"); break;
                        case Owner: r.U32(); int owner = (int)r.VarU32(); Grant(tr, owner, r.U32()); check(tr.HitchWaiting && tr.Root.RB.isKinematic, "grant alone does not unfreeze"); break;
                        case HitchFrozen: ApplyHitchFrozen(r); check(!tr.HitchWaiting && !tr.Root.RB.isKinematic && !tr.Root.attachable.RB.isKinematic, "reliable motion releases both bodies"); break;
                        case HitchCommand: ApplyHitchCommand(r); break;
                    }
                }
                Packets.Clear(); HitchCommandTick();
                check(tr.Root.attachable.attached && tr.Root.attachable.J != null, "common owner creates joint and broadcasts");
                check(DamageGraceUntil[car.Root.car] > Time.time, "coupler alignment guards the car's speed-change damage path");
                ServerPacket(Requests[0], MyId); Requests.Clear();
                check(Packets.Count == 2 && !_hitchOps.ContainsKey(20), "accepted result reaches both peers");

                // A driver takes the car. Its hitch remains registered and follows, without a general pickup claim.
                car.HitchUntil = -100; var joint = tr.Root.attachable.J;
                Server[10].OwnerId = old; Server[10].Epoch++; Packets.Clear(); CascadeHitches(Server[10]);
                check(Packets.Count == 1 && Packets[0].target == MyId && Packets[0].data[0] == HitchFreeze, "car takeover starts trailer transfer");
                HitchBeforeOwner(car, old, 2); car.Proxy = true; car.OwnerId = old;
                check(joint.Destroyed && tr.Root.attachable.J == null && tr.Root.RB.isKinematic, "old simulator removes constraint synchronously and freezes before car becomes a proxy");
                check(!HitchAuthority.CanJoin(car.OwnerId, tr.OwnerId, MyId, tr.HitchWaiting, car.Proxy, tr.Proxy), "no mixed-authority joint during takeover");

                // trailercheck reads hitchstate, then entities in a separate bridge job. A handback can land between them.
                car.Proxy = false; car.OwnerId = car.HitchAuthorityOwner = MyId;
                tr.HitchWaiting = false; tr.Proxy = false; tr.OwnerId = tr.HitchAuthorityOwner = MyId;
                HitchRestore(tr);
                var beforeHandback = TestHitchState(20);
                check(beforeHandback.Contains("\"joint\":true") && beforeHandback.Contains("\"carProxy\":false") && beforeHandback.Contains("\"trailerProxy\":false"), "joint and both roles share one bridge snapshot");
                HitchBeforeOwner(car, old, 3); car.OwnerId = car.HitchAuthorityOwner = old; car.Proxy = true;
                var afterHandback = TestHitchState(20);
                check(tr.Root.attachable.J == null && afterHandback.Contains("\"joint\":false") && afterHandback.Contains("\"carProxy\":true"), "handback destroys the joint before proxy conversion");
                check(beforeHandback.Contains("\"joint\":true") && car.Proxy, "old hitchstate plus new entities reproduces the script assertion despite synchronous joint removal");

                // Replaying the attach on a display copy keeps logical agreement without a constraint.
                MyId = old; car.Proxy = tr.Proxy = true; tr.OwnerId = 1 - old;
                _applyingAttach = true; tr.Root.attachable.Attach(slot); _applyingAttach = false;
                check(tr.Root.attachable.attached && tr.Root.attachable.J == null, "proxy replay keeps hitch metadata with no joint");
                Requests.Clear(); PlayerActing = true;
                check(!HitchDetach(tr.Root.attachable) && Requests.Count == 1 && tr.Root.attachable.attached, "non-owner disconnect requests owner and leaves copy unchanged");
                var disconnect = Reader(Requests[0]); var op = ReadHitch(disconnect);
                check(!op.Connect && op.Car == 10 && op.Trailer == 20, "disconnect addresses both entities");
                PlayerActing = false; check(!HitchDetach(tr.Root.attachable), "automatic proxy detach is suppressed"); PlayerActing = true;
            }
            var transfer = new HitchAuthority.Transfer { From = 2, To = 1, Epoch = 8, Token = 9 };
            check(!transfer.Confirm(2, 7, 9) && !transfer.Confirm(2, 8, 8) && !transfer.Confirm(1, 8, 9), "stale epochs, tokens and wrong senders refuse freeze acknowledgement");
            check(transfer.Confirm(2, 8, 9) && !transfer.Confirm(2, 8, 9), "only one valid freeze acknowledgement");

            foreach (int requester in new[] { 1, 2 })
            {
                MyId = requester; ByNet.Clear(); Server.Clear(); Requests.Clear(); Packets.Clear();
                _hitchOps.Clear(); _hitchTransfers.Clear();
                var car = Make(10, 0, false); var tr = Make(20, 1, true);
                var slot = new partslotscript { tosaveitem = car.Root }; car.Root.partslotscripts.Add(slot);
                Server[10] = new SEnt { NetId = 10, OwnerId = 0, Car = true };
                Server[20] = new SEnt { NetId = 20, OwnerId = 1, Hitch = true };
                tr.Root.attachable.Attach(slot);
                check(Requests.Count == 1 && !tr.Root.attachable.attached && tr.Root.attachable.J == null, "trailer owner or third party cannot join their body to someone else's car");
                var request = Requests[0]; ServerPacket(request, requester);
                check(Packets.Count == 1 && Packets[0].target == 1, "freeze always routes to trailer owner, independently of requester");
                Packets.Clear(); _hitchOps.Clear(); _hitchTransfers.Clear();
                Server[10].Epoch++;
                ServerPacket(request, requester);
                var refused = Reader(Packets[0].data); ReadHitch(refused);
                check(refused.Str() == "stale epoch" && !_hitchTransfers.ContainsKey(20), "production server refuses stale epochs with a reason before any freeze");
                Server[10].Epoch--; Packets.Clear(); Near = false;
                ServerPacket(request, requester); Near = true;
                refused = Reader(Packets[0].data); ReadHitch(refused);
                check(refused.Str() == "edit reach", "production server checks reach on both endpoints");
            }
            var player = new fpscontroller { RB = new Rigidbody { velocity = new Vector3(50, 0, 0) } };
            mainscript.s = new mainscript { player = player };
            ByNet[20].HitchUntil = 1;
            var fall = typeof(HitchFallGuard).GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            check(!(bool)fall.Invoke(null, new object[] { player }) && player.lastVelocity == 50, "handover speed spike cannot invoke the game's fall-death check");
            player.lastVelocity = 0; Time.time = 2;
            check((bool)fall.Invoke(null, new object[] { player }), "fall-death guard expires after handover");
            Time.time = 0; mainscript.s = null;
            // An unhitch can reach the server after it requested a freeze but before the acknowledgement.
            Packets.Clear(); Requests.Clear(); _hitchOps.Clear(); _hitchTransfers.Clear();
            var detached = ByNet[20]; MyId = detached.OwnerId = 1; detached.Proxy = false;
            Server[20].HitchCar = 10; CascadeHitches(Server[10]);
            var pendingFreeze = Packets[0].data; Packets.Clear();
            Server[20].HitchCar = Server[20].AttachParent = 0;
            ApplyHitchFreeze(Reader(pendingFreeze)); ServerPacket(Requests[0], 1);
            check(Server[20].OwnerId == 1 && !_hitchTransfers.ContainsKey(20), "unhitch racing a freeze keeps the current owner and completes the transfer fence");
            foreach (var packet in Packets)
            {
                if (packet.target != 1) continue;
                var r = Reader(packet.data);
                if (packet.data[0] == HitchFreeze) ApplyHitchFreeze(r);
                else if (packet.data[0] == Owner) { r.U32(); int owner = (int)r.VarU32(); Grant(detached, owner, r.U32()); }
                else if (packet.data[0] == HitchFrozen) ApplyHitchFrozen(r);
            }
            check(!detached.HitchWaiting && !detached.Root.RB.isKinematic, "cancelled hitch transfer unfreezes the unchanged owner instead of stranding it");
            var loading = new attachablescript { tosave = new tosaveitemscript(), utanfuto = new HitchFakes.Trailer() };
            PlayerActing = false; loading.Attach(new partslotscript()); PlayerActing = true;
            check(loading.attached && loading.point != null && loading.J == null, "save load before entity binding preserves the hitch record without creating an unowned joint");
            ByNet.Clear(); MyId = 1;
            var provisionalCar = Make(10, 1, false); var provisionalTrailer = Make(20, 1, true);
            provisionalTrailer.HitchAuthorityOwner = 0;
            var provisionalSlot = new partslotscript { tosaveitem = provisionalCar.Root }; provisionalCar.Root.partslotscripts.Add(provisionalSlot);
            PlayerActing = false; provisionalTrailer.Root.attachable.Attach(provisionalSlot); PlayerActing = true;
            HitchRestore(provisionalTrailer);
            check(provisionalTrailer.Root.attachable.J == null, "provisional pickup authority cannot create a hitch even when both local bodies are dynamic");
            provisionalTrailer.HitchAuthorityOwner = 1; HitchRestore(provisionalTrailer);
            check(provisionalTrailer.Root.attachable.J != null, "server-confirmed authority permits restoration");
        }
    }
}

namespace HitchFakes
{
    public class Item : Component
    {
        public Attachment attachable; public Car car; public Component P;
        public Rigidbody RB; public List<Slot> partslotscripts = new List<Slot>();
        public Item() { RB = new Rigidbody(); RB.gameObject = gameObject; gameObject.Body = RB; }
    }
    public class Attachment : Component
    {
        public Item tosave; public Trailer utanfuto; public Slot slot; public bool attached; public Transform point; public Joint J;
        public Rigidbody RB;
        public Attachment() { RB = new Rigidbody(); RB.gameObject = gameObject; gameObject.Body = RB; }
        public void Attach(Slot s) => Entities.GameAttach(this, s);
        public void Attach(Transform t) => Entities.GameAttach(this, t);
        public void Detach() { attached = false; slot.parts.Remove(this); slot = null; J = null; }
    }
    public class Slot : Component
    {
        public Item tosaveitem; public Transform T => transform; public List<Attachment> parts = new List<Attachment>();
        public bool hasPart() => parts.Count != 0; public bool CanCraft(Attachment at) => true;
        public void Craft(Attachment p) { }
    }
    public class Car : Component { }
    public class Grip : Component { public Transform GripPos; }
    public class Lights : Component { }
    public class Brake { public int currentTurnState; }
    public class Trailer { public Brake brake; public void RefreshCenterOffMass(bool attached, Lights lights) { } }
    public class Record { public int prefabID; }
    public class Database { public static Database s; public GameObject Item(int id) => null; }
    public class Player : Component
    {
        public Rigidbody RB; public Component pickedUp; public float lastVelocity, currentVelocity, deathVelocityChange = 30;
        public void Drop() { pickedUp = null; } public void FallDamage() { }
    }
    public class Main
    {
        public static Main s; public Player player;
        public static Vector3d GlobalFromUnityPos(Vector3 p) => new Vector3d(p.x, p.y, p.z);
        public static Vector3 UnityPosFromGlobal(Vector3d p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
    }
}
namespace TLDRevamp
{
    internal static class SessionLog { internal static void Event(string kind, uint net = 0, uint other = 0, int item = -1, int slot = -1, int from = -1, int to = -1, string result = null, uint epoch = 0, int count = 0, float value = 0) { } }
    internal static class Json { internal static string Str(string s) => "\"" + s + "\""; }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string s) { } public HarmonyPatch(Type t, string s, Type[] args) { } }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int n) { } } public static class Priority { public const int First = 0; }
}
namespace UnityEngine
{
    public class Object { public bool Destroyed; public static void DestroyImmediate(Object o) { o.Destroyed = true; } }
    public class Component : Object
    {
        public string name = "hitch"; public GameObject gameObject = new GameObject(); public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : class => gameObject.Body as T;
        public T[] GetComponentsInChildren<T>(bool all) where T : class => gameObject.Body is T body ? new[] { body } : Array.Empty<T>();
    }
    public class GameObject
    {
        public Rigidbody Body; public Transform transform = new Transform(); public T GetComponent<T>() where T : class => Body as T;
    }
    public class Transform
    {
        public Vector3 position; public Quaternion rotation = Quaternion.identity;
        public Vector3 TransformPoint(Vector3 p) => position + p; public Vector3 InverseTransformPoint(Vector3 p) => p - position;
        public void SetPositionAndRotation(Vector3 p, Quaternion q) { position = p; rotation = q; }
        public T GetComponentInParent<T>() where T : class => null;
    }
    public class Rigidbody : Component
    {
        public bool isKinematic; public Vector3 velocity, angularVelocity;
        public Vector3 position { get => transform.position; set => transform.position = value; }
        public Quaternion rotation { get => transform.rotation; set => transform.rotation = value; }
        public Vector3 GetPointVelocity(Vector3 p) => velocity;
    }
    public class Joint : Object { }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 zero => default; public float sqrMagnitude => x * x + y * y + z * z; public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    public struct Quaternion
    {
        public float x, y, z, w; public Quaternion(float a, float b, float c, float d) { x = a; y = b; z = c; w = d; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1); public static Quaternion Inverse(Quaternion q) => q;
        public static Quaternion operator *(Quaternion a, Quaternion b) => b; public static Vector3 operator *(Quaternion q, Vector3 p) => p;
    }
    public static class Time { public static float time; }
    public static class Mathf { public static float Abs(float f) => MathF.Abs(f); }
}
public struct Vector3d
{
    public double x, y, z; public Vector3d(double a, double b, double c) { x = a; y = b; z = c; }
}
