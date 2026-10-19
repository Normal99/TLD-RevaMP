#pragma warning disable CS0649, CS8981
// Unity surfaces only: production receiver eligibility, ownership and donor selection run unchanged.
global using tosaveitemscript = PrefabSave;
global using itemdatabase = PrefabDatabase;
global using carscript = PrefabCar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrailerHitches;
using UnityEngine;

static class HitchPrefabTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static int Main()
    {
        var car = new GameObject("car"); var save = car.AddComponent<tosaveitemscript>();
        var drive = car.AddComponent<carscript>();
        car.AddComponent<seatscript>().driverSeat0 = true;
        for (int i = 0; i < 4; i++) drive.WHDriven.Add(new GameObject("wheel").AddComponent<WheelCollider>());
        Check(Plugin.DriveableCar(car), "four-wheel driver car eligible");
        drive.WHDriven.RemoveAt(3);
        Check(!Plugin.DriveableCar(car), "three-wheel vehicle excluded");
        drive.WHDriven.Add(new GameObject("wheel").AddComponent<WheelCollider>());
        var assembly = new GameObject("receiver"); assembly.transform.SetParent(car.transform, false);
        var slotObject = new GameObject("slot"); slotObject.transform.SetParent(assembly.transform, false);
        var slot = slotObject.AddComponent<partslotscript>(); slot.tipus = new[] { "trailer" };
        var target = new GameObject("target"); target.transform.SetParent(assembly.transform, false); slot.T = target.transform;
        var grip = new GameObject("grip").AddComponent<attachablescript>();
        Check(Plugin.CompatibleSlot(car, grip) == slot, "active donor discovered");
        car.Active = false;
        Check(slot.GetComponentInParent<tosaveitemscript>() == null, "inactive prefab ancestors require explicit lookup");
        Check(Plugin.CompatibleSlot(car, grip) == slot, "inactive stock donor remains compatible");
        var childSave = slotObject.AddComponent<tosaveitemscript>();
        Check(Plugin.CompatibleSlot(car, grip) == null, "a detachable child save cannot donate its slot to parent");
        slotObject.Components.Remove(childSave);
        var root = typeof(Plugin).GetMethod("AssemblyRoot", BindingFlags.Static | BindingFlags.NonPublic);
        Check(root.Invoke(null, new object[] { slot, car.transform }) == assembly.transform, "sibling snap target keeps whole receiver assembly");
        slot.DT = car.transform;
        Check(root.Invoke(null, new object[] { slot, car.transform }) == null, "external donor references rejected");
        slot.DT = null;
        car.AddComponent<attachgripscript>();
        Check(!Plugin.DriveableCar(car), "trailer excluded even with driver and wheels");
        Console.WriteLine("ALL PASS (" + checks + " prefab checks)"); return 0;
    }
}
class PrefabSave : Component { public List<partslotscript> partslotscripts = new(); }
class PrefabDatabase { public enum i { utanfutofull } public GameObject[] items; }
class PrefabCar : Component { public List<WheelCollider> WHDriven = new(), WHSteer, WHInvSteer, WHBrakeOnly, WHHandBrake; }
class seatscript : Component { public bool driverSeat0, driverSeat1; }
class attachgripscript : Component { public GameObject GripPrefab; }
class pickupable : Component { }
class attachablescript : Component { }
class partslotscript : Component
{
    public tosaveitemscript tosaveitem; public pickupable pick; public Transform T, DT; public int id;
    public string[] maintipus = Array.Empty<string>(), tipus = Array.Empty<string>();
    public bool CanCraft(attachablescript grip) => tipus.Length > 0;
}
namespace UnityEngine
{
    public class Object
    {
        public string name;
        protected static T Instantiate<T>(T original, Transform parent, bool world = false) where T : Object => original;
        protected static void DestroyImmediate(Object obj) { }
    }
    public class Component : Object
    {
        public GameObject gameObject; public Transform transform => this as Transform ?? gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T GetComponentInParent<T>(bool includeInactive = false) where T : Component
        {
            for (var at = transform; at != null; at = at.parent)
                if ((at == transform || includeInactive || at.gameObject.ActiveInHierarchy) && at.GetComponent<T>() is T found) return found;
            return null;
        }
        public T GetComponentInChildren<T>(bool inactive = false) where T : Component => GetComponentsInChildren<T>(inactive).FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : Component => gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class GameObject : Object
    {
        public bool Active = true; public bool ActiveInHierarchy => Active && (transform.parent == null || transform.parent.gameObject.ActiveInHierarchy);
        public Transform transform; public List<Component> Components = new();
        public GameObject(string name) { this.name = name; transform = new Transform { gameObject = this }; Components.Add(transform); }
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this, name = name }; Components.Add(c); return c; }
        public T GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
        public T GetComponentInChildren<T>(bool inactive = false) where T : Component => GetComponentsInChildren<T>(inactive).FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : Component =>
            (inactive || ActiveInHierarchy ? Components.OfType<T>() : Enumerable.Empty<T>()).Concat(transform.Children.SelectMany(t => t.gameObject.GetComponentsInChildren<T>(inactive))).ToArray();
        public static GameObject CreatePrimitive(PrimitiveType type) => new("cube");
    }
    public class Transform : Component
    {
        public Transform parent; public List<Transform> Children = new(); public Vector3 position, localPosition, localScale; public Quaternion localRotation;
        public void SetParent(Transform value, bool world) { parent?.Children.Remove(this); parent = value; parent?.Children.Add(this); }
        public bool IsChildOf(Transform value) { for (var at = this; at != null; at = at.parent) if (at == value) return true; return false; }
        public Transform Find(string name) => Children.FirstOrDefault(c => c.gameObject.name == name);
    }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 zero => default;
        public static Vector3 operator +(Vector3 a,Vector3 b) => default;
        public static Vector3 operator -(Vector3 a,Vector3 b) => default;
        public static Vector3 Scale(Vector3 a,Vector3 b) => default;
    }
    public struct Quaternion { public static Vector3 operator *(Quaternion q,Vector3 v) => v; }
    class Rigidbody : Component { } class Joint : Component { } class WheelCollider : Component { }
    class Material : Object { } class Renderer : Component { public Material sharedMaterial; } class Collider : Component { }
    public enum PrimitiveType { Cube }
}
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] class BepInPlugin : Attribute { public BepInPlugin(string id,string name,string version) { } }
    [AttributeUsage(AttributeTargets.Class)] class BepInDependency : Attribute
    { public enum DependencyFlags { SoftDependency } public BepInDependency(string id,DependencyFlags flags) { } }
    public class BaseUnityPlugin : Component { protected Logging.ManualLogSource Logger = new(); protected Config Config = new(); }
    public class Config { public Entry Bind(string section,string key,string value,string description) => new() { Value=value }; }
    public class Entry { public string Value; }
}
namespace BepInEx.Logging { public class ManualLogSource { public void LogWarning(object s) { } public void LogError(object s) { } public void LogInfo(object s) { } } }
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)] class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type t,string method) { } }
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] class HarmonyPriority : Attribute { public HarmonyPriority(int p) { } }
    static class Priority { public const int Last = 0; }
    class Harmony { public Harmony(string id) { } public void PatchAll(Assembly a) { } }
}
