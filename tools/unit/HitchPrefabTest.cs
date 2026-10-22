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
        car.Components.Remove(car.GetComponent<attachgripscript>());
        slot.tosaveitem = save; save.partslotscripts.Add(slot);
        var metal = new GameObject("Cylinder"); metal.transform.SetParent(assembly.transform, false);
        metal.AddComponent<Renderer>().sharedMaterial = new Material();
        car.Asset = true;
        var db = new itemdatabase { items = new GameObject[400] };
        db.items[1] = car;
        var trailer = new GameObject("trailer");
        trailer.AddComponent<attachgripscript>().GripPrefab = grip.gameObject;
        db.items[0] = trailer;
        var originals = new Dictionary<int, GameObject>();
        foreach (var model in HitchPosition.Models)
        {
            Plugin.Positions[model.Key] = model.Position;
            foreach (int id in model.Items)
            {
                var body = new GameObject("car" + id) { Asset = true };
                body.AddComponent<carscript>().WHDriven = drive.WHDriven;
                body.AddComponent<seatscript>().driverSeat0 = true;
                var owner = body.AddComponent<tosaveitemscript>();
                owner.id = model.Items[0]; // Full variants keep their canonical base-model id.
                body.AddComponent<pickupable>();
                for (int i = 0; i < (id < 300 ? 21 : 31); i++)
                {
                    var native = new GameObject("native" + i); native.transform.SetParent(body.transform, false);
                    var nativeSlot = native.AddComponent<partslotscript>(); nativeSlot.tosaveitem = owner;
                    owner.partslotscripts.Add(nativeSlot);
                }
                originals[id] = db.items[id] = body;
            }
        }
        db.items[398] = db.items[243]; // One prefab may have several database aliases.
        Plugin.Log = new BepInEx.Logging.ManualLogSource();
        var register = typeof(Plugin).GetMethod("AddReceivers", BindingFlags.Static | BindingFlags.NonPublic);
        register.Invoke(null, new object[] { db });
        Check(db.items[398] == db.items[243], "database aliases share replacement template");
        GameObject.UnloadScene();
        foreach (var pair in originals)
        {
            int index = pair.Key < 300 ? 21 : 31;
            var template = db.items[pair.Key];
            Check(template != pair.Value && !template.Asset && !template.ActiveInHierarchy && template.Alive,
                "persistent inactive whole-car template " + pair.Key);
            Check(pair.Value.GetComponent<tosaveitemscript>().partslotscripts.Count == index,
                "original asset untouched " + pair.Key);
            // The double remaps serialized references when cloning, as Unity does for one hierarchy.
            var spawned = UnityEngine.Object.Instantiate(template, null, false);
            var owner = spawned.GetComponent<tosaveitemscript>();
            var receiver = owner.partslotscripts[index];
            Check(owner.id == pair.Value.GetComponent<tosaveitemscript>().id && receiver.pick == spawned.GetComponent<pickupable>(),
                "canonical item id and pickup owner preserved " + pair.Key);
            Check(receiver != null && receiver.gameObject.Alive && receiver != template.GetComponent<tosaveitemscript>().partslotscripts[index],
                "spawn has its own live receiver after setup scene unload " + pair.Key);
            Check(receiver.tosaveitem == owner && receiver.T.IsChildOf(spawned.transform) && receiver.id == index &&
                Plugin.CompatibleSlot(spawned, grip) == receiver && receiver.CanCraft(grip), "spawn owns target, list entry and compatible stable index " + pair.Key);
            Check(owner.partslotscripts.Take(index).All(s => s.tosaveitem == owner && s.transform.IsChildOf(spawned.transform)),
                "native slots retain order and remap ownership " + pair.Key);
        }
        register.Invoke(null, new object[] { db });
        Check(db.items[243].GetComponent<tosaveitemscript>().partslotscripts.Count == 22 &&
            db.items[371].GetComponent<tosaveitemscript>().partslotscripts.Count == 32, "repeat registration keeps saved indices and count");
        // A scene load: the game's database fills its items again from the original prefabs. The templates made the
        // first time come back; no new whole-car clone is made.
        var fresh = new itemdatabase { items = new GameObject[400] };
        fresh.items[0] = trailer; fresh.items[1] = car;
        foreach (var pair in originals) fresh.items[pair.Key] = pair.Value;
        fresh.items[398] = fresh.items[243];
        register.Invoke(null, new object[] { fresh });
        Check(originals.Keys.All(id => fresh.items[id] == db.items[id]) && fresh.items[398] == fresh.items[243],
            "a scene load puts the same templates back");
        Check(db.items[243].GetComponent<tosaveitemscript>().partslotscripts.Count == 22, "no second receiver on a template");
        Console.WriteLine("ALL PASS (" + checks + " prefab checks)"); return 0;
    }
}
class PrefabSave : Component { public int id; public List<partslotscript> partslotscripts = new(); }
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
        public virtual string name { get; set; }
        public static GameObject Instantiate(GameObject original, Transform parent, bool world = false)
        {
            var map = new Dictionary<Object, Object>();
            GameObject Copy(GameObject source, Transform at)
            {
                var result = new GameObject(source.name) { Active = source.Active };
                result.transform.SetParent(at, false);
                map[source] = result; map[source.transform] = result.transform;
                foreach (var c in source.Components.Where(c => c is not Transform))
                {
                    var copy = (Component)Activator.CreateInstance(c.GetType()); copy.gameObject = result;
                    result.Components.Add(copy); map[c] = copy;
                }
                foreach (var child in source.transform.Children) Copy(child.gameObject, result.transform);
                return result;
            }
            object Remap(object value)
            {
                if (value is Object obj) return map.TryGetValue(obj, out var copy) ? copy : obj;
                if (value is Array array)
                {
                    var copy = (Array)array.Clone();
                    for (int i = 0; i < copy.Length; i++) copy.SetValue(Remap(copy.GetValue(i)), i);
                    return copy;
                }
                if (value is System.Collections.IList list)
                {
                    var copy = (System.Collections.IList)Activator.CreateInstance(value.GetType());
                    foreach (var entry in list) copy.Add(Remap(entry));
                    return copy;
                }
                return value;
            }
            var root = Copy(original, parent);
            foreach (var pair in map.Where(p => p.Key is Component))
                foreach (var field in pair.Key.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (field.Name != "gameObject" && field.Name != "parent" && field.Name != "Children")
                        field.SetValue(pair.Value, Remap(field.GetValue(pair.Key)));
            return root;
        }
        protected static void DontDestroyOnLoad(GameObject obj) { obj.Persistent = true; }
        protected static void DestroyImmediate(Object obj) { }
    }
    public class Component : Object
    {
        public GameObject gameObject; public Transform transform => this as Transform ?? gameObject.transform;
        public override string name { get => gameObject.name; set => gameObject.name = value; }
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
        static readonly List<GameObject> All = new();
        public bool Asset, Persistent, Alive = true;
        public bool Active = true; public bool ActiveInHierarchy => Active && (transform.parent == null || transform.parent.gameObject.ActiveInHierarchy);
        public Transform transform; public List<Component> Components = new();
        public GameObject(string name) { this.name = name; transform = new Transform { gameObject = this }; Components.Add(transform); All.Add(this); }
        public void SetActive(bool active) { Active = active; }
        public static void UnloadScene()
        {
            foreach (var obj in All)
                if (!obj.Asset && !Ancestors(obj.transform).Any(t => t.gameObject.Persistent)) obj.Alive = false;
        }
        static IEnumerable<Transform> Ancestors(Transform t) { for (; t != null; t = t.parent) yield return t; }
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this, name = name }; Components.Add(c); return c; }
        public T GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
        public T GetComponentInChildren<T>(bool inactive = false) where T : Component => GetComponentsInChildren<T>(inactive).FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : Component =>
            (inactive || ActiveInHierarchy ? Components.OfType<T>() : Enumerable.Empty<T>()).Concat(transform.Children.SelectMany(t => t.gameObject.GetComponentsInChildren<T>(inactive))).ToArray();
        public static GameObject CreatePrimitive(PrimitiveType type)
        { var cube = new GameObject("cube"); cube.AddComponent<Collider>(); cube.AddComponent<Renderer>(); return cube; }
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
