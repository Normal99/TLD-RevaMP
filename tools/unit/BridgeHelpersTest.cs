global using pickupable = BridgeFakes.Pickup;
global using fpscontroller = BridgeFakes.Player;
global using inputscript = BridgeFakes.Input;
global using mainscript = BridgeFakes.Main;
global using carscript = BridgeFakes.Car;
using System;
using System.Collections.Generic;
using System.Reflection;
using TLDRevamp;
using TLDRevamp.Net;
using UnityEngine;

static class BridgeHelpersTest
{
    static int checks;
    internal static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main()
    {
        Entities.Tests(); DriveLab.Tests();
        Console.WriteLine("ALL PASS (" + checks + " bridge helper checks)"); return 0;
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        static bool InSession = true;
        static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        sealed class Ent
        {
            internal BridgeFakes.Item Root = new BridgeFakes.Item();
            internal bool Proxy, Held, Stored;
        }
        static bool Resolve(Ent e) => true;
        static void TelemetryTouch(uint net) { }
        static Transform[] Limbs(Ent e) => e.Root.ragdollPos?.childs;
        static bool Limp(Ent e) => true;
        static bool IsHeld(BridgeFakes.Item it) => it.P == mainscript.s.player.pickedUp ||
            it.ragdollPos != null && Array.IndexOf(it.ragdollPos.ps, mainscript.s.player.pickedUp) >= 0;
        static bool IsStored(BridgeFakes.Item it) => false;
        public static void Tests()
        {
            var player = new fpscontroller(); mainscript.s = new mainscript { player = player };
            var input = inputscript.i = new inputscript();
            // A seated camera is independent of the hand anchor. The old camera-relative placement fails the gap.
            player.mainCam.position = new Vector3(10, 2, 0);
            player.TpickedUpPos.position = new Vector3(0, 1, 0);
            var e = new Ent(); ByNet[8] = e;
            var branch = new Transform { position = new Vector3(3, 0, 0) };
            var limb = new pickupable(); limb.transform.position = new Vector3(4, 0, 0);
            limb.transform.parent = branch;
            var other = new Transform { position = new Vector3(5, 0, 0) };
            e.Root.ragdollPos = new BridgeFakes.Ragdoll { ps = new[] { limb }, childs = new[] { branch, limb.transform, other } };
            var relative = other.position - limb.transform.position;
            Check(ItemCheck(8, "hold").Contains("\"held\":true"), "production helper starts a native limb pickup");
            Check((limb.transform.position - player.TpickedUpPos.position).magnitude == 0, "pickup starts at the game's hold anchor despite a displaced camera");
            Check((other.position - limb.transform.position - relative).magnitude == 0, "detached ragdoll branches translate once without stretching joints");
            Check(input.pickup && !input.pickupUp && !input.lmb, "pickup input is sustained immediately without activating LMB uses");
            for (int frame = 0; frame < 60; frame++)
            {
                input.pickup = false; input.pickupUp = true;
                Invoke(typeof(ItemCheckInput), "Postfix", input);
                Check(input.pickup && !input.pickupUp, "input Update retains the pickup across frames");
            }
            input.pickup = false; input.pickupUp = true;
            Invoke(typeof(ItemCheckPlayerInput), "Prefix", player);
            Check(input.pickup && !input.pickupUp, "player Update sees held input even when inputscript runs later");
            player.TpickedUpParent.position = limb.transform.position + new Vector3(2, 0, 0);
            Invoke(typeof(ItemCheckDrop), "Prefix", player); player.Drop();
            Check(SessionLog.Kind == "itemcheck-drop" && SessionLog.Net == 8 && SessionLog.Gap == 2 && SessionLog.Limit == 1.25f, "native helper drop measurements survive in the mark event ring");
            Check(ItemCheck(8, "").Contains("\"dropGap\":2.000") && ItemCheck(8, "").Contains("\"dropLimit\":1.250"), "native release retains measured gap and native limit for the rerun");
            input.pickup = false; ItemCheckHoldInput(input);
            Check(!input.pickup && player.pickedUp == null, "native drops remain dropped and are never regrabbed");
            ItemCheck(8, "hold"); ItemCheck(8, "release"); input.pickup = false; ItemCheckHoldInput(input);
            Check(!input.pickup && _itemCheckHold == null && player.pickedUp == null, "explicit release ends sustained input");
            ItemCheck(8, "hold"); InSession = false; input.pickup = false; ItemCheckHoldInput(input);
            Check(!input.pickup, "session exit ends the helper's input"); InSession = true;
        }
        static void Check(bool ok, string why) => BridgeHelpersTest.Check(ok, why);
        static void Invoke(Type type, string method, object value) => type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { value });
    }
}

namespace TLDRevamp
{
    public static partial class DriveLab
    {
        public static bool Active;
        public static void Tests()
        {
            var player = mainscript.s.player; var input = inputscript.i;
            var car = player.Car = new carscript { isPlayerDriving = true };
            var trailer = new Rigidbody { velocity = new Vector3(0, 0, 4) };
            car.RB.velocity = trailer.velocity; car.RB.angularVelocity = new Vector3(0, 1, 0);
            Active = true;
            BridgeHelpersTest.Check(Brake().Contains("\"braked\":true") && !Active, "brakecar stops autodrive and starts braking");
            BridgeHelpersTest.Check(car.RB.velocity.z == 4 && trailer.velocity.z == 4 && car.RB.angularVelocity.y == 1, "braking never zeroes the car while its trailer retains momentum");
            BridgeHelpersTest.Check(input.brake == 1 && input.handbrake == 1 && input.throttle == 0, "braking feeds the game's real controls");
            input.brake = 0; input.throttle = 1; BrakeInput(input);
            BridgeHelpersTest.Check(input.brake == 1 && input.throttle == 0, "braking persists across input updates");
            car.RB.velocity = new Vector3(0, 0, -4); BrakeInput(input);
            BridgeHelpersTest.Check(input.brake == 0 && input.throttle == 1, "automatic reverse uses the opposite pedal to brake rather than accelerate backwards");
            car.Auto = false; BrakeInput(input);
            BridgeHelpersTest.Check(input.brake == 1 && input.throttle == 0, "manual reverse retains the service brake");
            car.RB.velocity = Vector3.zero; BrakeInput(input);
            BridgeHelpersTest.Check(input.brake == 0 && input.handbrake == 1, "parking holds at rest without the automatic gearbox selecting reverse");
            Active = true; input.handbrake = 0;
            BridgeHelpersTest.Check(!BrakeInput(input) && input.handbrake == 0 && _brakeCar == null, "new autodrive releases scripted braking");
            Active = false; Brake(); player.Car = new carscript();
            BridgeHelpersTest.Check(!BrakeInput(input) && _brakeCar == null, "braking cannot follow the player into another car");
            player.Car.isPlayerDriving = false;
            BridgeHelpersTest.Check(Brake().Contains("error"), "passenger cannot apply driver input");
        }
    }
}

namespace BridgeFakes
{
    public sealed class Input { public static Input i; public bool lmb, pickup, pickupUp, pickupDown, recenter; public float throttle, brake, handbrake; }
    public sealed class Main { public static Main s; public Player player; }
    public sealed class Car
    {
        public Rigidbody RB = new Rigidbody(); public Transform transform = new Transform();
        public bool isPlayerDriving, Auto = true; public bool IsAutoShift() => Auto;
    }
    public sealed class Pickup { public Transform transform = new Transform(); }
    public sealed class Ragdoll { public Pickup[] ps; public Transform[] childs; }
    public sealed class Item
    {
        public Transform transform = new Transform(); public Pickup P = new Pickup(); public Ragdoll ragdollPos;
        public T GetComponent<T>() where T : new() => transform.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive) => Array.Empty<T>();
    }
    public sealed class Player
    {
        public Pickup pickedUp, inHandP; public Car Car; public float dropDist = 1.25f;
        public Transform mainCam = new Transform(), TpickedUpPos = new Transform(), TpickedUpParent = new Transform();
        public void Pickup(Pickup p, Vector3 at) { pickedUp = p; TpickedUpParent.position = p.transform.position; }
        public void Drop() { pickedUp = null; }
        public void RightHandRelease(bool sync) { inHandP = null; }
    }
}

namespace UnityEngine
{
    public sealed class Transform
    {
        Vector3 local; public Transform parent; public Vector3 forward => new Vector3(0, 0, 1);
        public Vector3 position { get => parent == null ? local : parent.position + local; set => local = parent == null ? value : value - parent.position; }
        public T GetComponent<T>() where T : new() => new T();
    }
    public sealed class Rigidbody { public Vector3 velocity, angularVelocity; public bool detectCollisions = true, isKinematic; public void WakeUp() { } }
    public static class Physics { public static void SyncTransforms() { } }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default; public static Vector3 up => new Vector3(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z; public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    { public HarmonyPatch(Type t, string name) { } public HarmonyPatch(Type t, string name, Type[] args) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
}

namespace TLDRevamp
{
    static class SessionLog
    {
        internal static string Kind; internal static uint Net; internal static float Gap, Limit;
        internal static void Event(string kind, uint net, float value, float dx, int count)
        { Kind = kind; Net = net; Gap = value; Limit = dx; }
    }
}
