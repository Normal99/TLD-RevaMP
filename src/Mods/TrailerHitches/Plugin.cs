using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TrailerHitches
{
    /// Adds the stock receiver assembly to cars missing a compatible drawbar slot. The trailer and its joints,
    /// snapping, breaking and attachment records remain the game's own.
    [BepInPlugin(Guid, "Trailer Hitches", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    [HarmonyPatch]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.trailerhitches";
        public const string Version = "0.2.0";
        internal const string ReceiverName = "TrailerReceiverHitch";
        internal static ManualLogSource Log;
        internal static readonly Dictionary<string, HitchPosition> Positions = new Dictionary<string, HitchPosition>();
        internal static partslotscript StockSlot;
        internal static Transform StockAssembly;
        internal static Material Metal;
        private static GameObject Prefabs;

        private void Awake()
        {
            Log = Logger;
            foreach (var model in HitchPosition.Models)
            {
                string raw = Config.Bind("Positions", model.Key, model.Position.ToString(),
                    "Ball target in car-local metres: x,y,z (+Z forward). Restart after editing; all players must match.").Value;
                if (!HitchPosition.TryParse(raw, out var position))
                {
                    Log.LogWarning("invalid position for " + model.Key + "; using the built-in position");
                    position = model.Position;
                }
                Positions.Add(model.Key, position);
            }
            string identity = Version + "+" + HitchPosition.Fingerprint(Positions);
            bool multiplayer = RegisterWithMultiplayerApi(identity);
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Trailer Hitches {identity}: multiplayer API {(multiplayer ? "registered" : "not found; local play only")}");
        }

        private static bool RegisterWithMultiplayerApi(string identity)
        {
            var api = Type.GetType("TLDRevamp.Multiplayer.MpApi, TLDRevamp.Multiplayer", false);
            var register = api?.GetMethod("RegisterMod", new[] { typeof(string), typeof(string), typeof(string), typeof(Assembly) });
            if (register == null) return false;
            register.Invoke(null, new object[] { "trailerhitches", "Trailer Hitches", identity, typeof(Plugin).Assembly });
            return true;
        }

        [HarmonyPatch(typeof(itemdatabase), "Awake")]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterDatabase(itemdatabase __instance)
        {
            try { AddReceivers(__instance); }
            catch (Exception e) { Log.LogError("adding trailer receiver hitches failed: " + e); }
        }

        internal static attachablescript Drawbar(itemdatabase db)
        {
            int id = (int)itemdatabase.i.utanfutofull;
            var trailer = db?.items != null && db.items.Length > id ? db.items[id] : null;
            var grip = trailer != null ? trailer.GetComponent<attachgripscript>() : null;
            return grip?.GripPrefab != null ? grip.GripPrefab.GetComponent<attachablescript>() : null;
        }

        internal static bool DriveableCar(GameObject prefab)
        {
            var car = prefab.GetComponent<carscript>();
            if (car == null || prefab.GetComponent<tosaveitemscript>() == null ||
                prefab.GetComponentInChildren<attachgripscript>(true) != null) return false;
            bool driver = false;
            foreach (var seat in prefab.GetComponentsInChildren<seatscript>(true))
                if (seat.driverSeat0 || seat.driverSeat1) { driver = true; break; }
            if (!driver) return false;
            // A car needs four road wheels. This excludes bicycles/motorcycles and the tiny keep-awake wheel.
            var wheels = new HashSet<WheelCollider>();
            foreach (var list in new[] { car.WHDriven, car.WHSteer, car.WHInvSteer, car.WHBrakeOnly, car.WHHandBrake })
                if (list != null) foreach (var wheel in list) if (wheel != null) wheels.Add(wheel);
            return wheels.Count >= 4;
        }

        internal static partslotscript CompatibleSlot(GameObject prefab, attachablescript grip)
        {
            var save = prefab.GetComponent<tosaveitemscript>();
            foreach (var slot in prefab.GetComponentsInChildren<partslotscript>(true))
            {
                // Prefab assets are inactive in the hierarchy even when activeSelf is set.
                if (slot.GetComponentInParent<tosaveitemscript>(true) != save || slot.maintipus == null || slot.tipus == null) continue;
                if (slot.CanCraft(grip)) return slot;
            }
            return null;
        }

        private static Transform AssemblyRoot(partslotscript slot, Transform car)
        {
            if (slot.T == null) return null;
            var root = slot.transform;
            while (root != null && root != car && !slot.T.IsChildOf(root)) root = root.parent;
            if (root == null || root == car || root.GetComponentInChildren<tosaveitemscript>(true) != null ||
                root.GetComponentsInChildren<partslotscript>(true).Length != 1 ||
                root.GetComponentInChildren<Joint>(true) != null || root.GetComponentInChildren<Rigidbody>(true) != null) return null;
            // Refuse a changed donor with references outside its assembly instead of retaining another car's targets.
            foreach (var field in typeof(partslotscript).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == nameof(partslotscript.tosaveitem) || field.Name == nameof(partslotscript.pick)) continue;
                var value = field.GetValue(slot);
                if (value is Component c && !c.transform.IsChildOf(root) || value is GameObject g && !g.transform.IsChildOf(root)) return null;
                if (value is System.Collections.ICollection list && list.Count > 0 && !(value is string[])) return null;
            }
            return root;
        }

        private static void AddReceivers(itemdatabase db)
        {
            var grip = Drawbar(db);
            if (grip == null) { Log.LogError("stock trailer drawbar missing; no receivers added"); return; }
            StockSlot = null;
            StockAssembly = null;
            Metal = null;
            var seen = new HashSet<GameObject>();
            // Item id order chooses the same stock donor on every machine, before any prefab is modified.
            foreach (var prefab in db.items)
            {
                if (prefab == null || !seen.Add(prefab) || !DriveableCar(prefab)) continue;
                var slot = CompatibleSlot(prefab, grip);
                if (slot == null || slot.name == ReceiverName) continue;
                var assembly = AssemblyRoot(slot, prefab.transform);
                if (assembly == null) continue;
                Metal = null;
                foreach (var renderer in assembly.GetComponentsInChildren<Renderer>(true))
                    if (renderer.name == "Cylinder" && renderer.sharedMaterial != null) { Metal = renderer.sharedMaterial; break; }
                if (Metal == null) continue;
                StockSlot = slot;
                StockAssembly = assembly;
                Log.LogInfo($"stock receiver: {prefab.name}/{assembly.name}, slot {slot.name}, material {Metal.name}");
                break;
            }
            if (StockAssembly == null) { Log.LogError("no self-contained stock receiver found; no receivers added"); return; }

            seen.Clear();
            for (int id = 0; id < db.items.Length; id++)
            {
                var prefab = db.items[id];
                if (prefab == null || !seen.Add(prefab) || !DriveableCar(prefab) || CompatibleSlot(prefab, grip) != null) continue;
                var model = HitchPosition.ForItem(id);
                if (model == null) { Log.LogWarning($"no measured receiver position for {prefab.name} (item {id}); skipped"); continue; }
                // A runtime child of a prefab asset still belongs to the current scene: unloading it
                // destroys the receiver and leaves a null serialized slot. Clone the WHOLE car into
                // an inactive persistent hierarchy first, so receiver and native slots clone together.
                if (Prefabs == null)
                {
                    Prefabs = new GameObject("TrailerHitchesPrefabs");
                    Prefabs.SetActive(false);
                    DontDestroyOnLoad(Prefabs);
                }
                var original = prefab;
                prefab = Instantiate(original, Prefabs.transform, false);
                prefab.name = original.name;
                seen.Add(prefab);
                var save = prefab.GetComponent<tosaveitemscript>();
                if (prefab.transform.Find(ReceiverName) != null) continue;
                if (save.partslotscripts == null) save.partslotscripts = new List<partslotscript>();
                // Instantiate remaps internal references and preserves serialized fields, layers, transforms, snap
                // trigger and physical colliders. Only ownership and the assembly's placement change.
                var assembly = Instantiate(StockAssembly.gameObject, prefab.transform, false).transform;
                assembly.name = ReceiverName;
                var receiver = assembly.GetComponentInChildren<partslotscript>(true);
                receiver.name = ReceiverName;
                receiver.tosaveitem = save;
                receiver.pick = save.GetComponent<pickupable>();
                var position = Positions[model.Key];
                var target = new Vector3(position.X, position.Y, position.Z);
                assembly.localPosition = target - assembly.localRotation * Vector3.Scale(assembly.localScale, LocalTarget(receiver.T, assembly));
                AddReceiverTube(assembly, receiver.T);
                foreach (var renderer in assembly.GetComponentsInChildren<Renderer>(true)) renderer.sharedMaterial = Metal;
                // Never sort/rebuild the native list: existing saves store these indices. One append, once per prefab.
                receiver.id = save.partslotscripts.Count;
                save.partslotscripts.Add(receiver);
                // Preserve database aliases as well as each variant's native serialized item id.
                for (int alias = id; alias < db.items.Length; alias++)
                    if (db.items[alias] == original) db.items[alias] = prefab;
                Log.LogInfo($"added {prefab.name} (item {id}), slot {receiver.id}, ball {position}");
            }
        }

        private static Vector3 LocalTarget(Transform target, Transform assembly)
        {
            var position = Vector3.zero;
            for (var t = target; t != assembly; t = t.parent)
                position = t.localPosition + t.localRotation * Vector3.Scale(t.localScale, position);
            return position;
        }

        private static void AddReceiverTube(Transform assembly, Transform target)
        {
            // The cloned stock bar, neck and ball retain their colliders. The open receiver tube is visual only.
            var tube = new GameObject("ReceiverTube").transform;
            tube.SetParent(assembly, false);
            tube.position = target.position;
            MakeWall(tube, new Vector3(-0.032f, -0.035f, 0.22f), new Vector3(0.008f, 0.072f, 0.20f));
            MakeWall(tube, new Vector3(0.032f, -0.035f, 0.22f), new Vector3(0.008f, 0.072f, 0.20f));
            MakeWall(tube, new Vector3(0f, -0.067f, 0.22f), new Vector3(0.056f, 0.008f, 0.20f));
            MakeWall(tube, new Vector3(0f, -0.003f, 0.22f), new Vector3(0.056f, 0.008f, 0.20f));
        }

        private static void MakeWall(Transform parent, Vector3 position, Vector3 scale)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "TubeWall";
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = position;
            wall.transform.localScale = scale;
            DestroyImmediate(wall.GetComponent<Collider>());
            wall.GetComponent<Renderer>().sharedMaterial = Metal;
        }
    }
}
