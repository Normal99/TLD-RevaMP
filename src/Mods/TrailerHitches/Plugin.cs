using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TrailerHitches
{
    /// Adds a receiver slot to driveable cars that cannot accept the game's trailer drawbar. The trailer keeps its
    /// normal attachgripscript and attachablescript; this adds the matching receiver at the back of the car.
    [BepInPlugin(Guid, "Trailer Hitches", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    [HarmonyPatch]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.trailerhitches";
        public const string Version = "0.1.0";
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            bool multiplayer = RegisterWithMultiplayerApi();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Trailer Hitches {Version}: multiplayer API {(multiplayer ? "registered" : "not found; local play only")}");
        }

        private static bool RegisterWithMultiplayerApi()
        {
            var api = Type.GetType("TLDRevamp.Multiplayer.MpApi, TLDRevamp.Multiplayer", false);
            var register = api?.GetMethod("RegisterMod", new[] { typeof(string), typeof(string), typeof(string), typeof(Assembly) });
            if (register == null) return false;
            register.Invoke(null, new object[] { "trailerhitches", "Trailer Hitches", Version, typeof(Plugin).Assembly });
            return true;
        }

        [HarmonyPatch(typeof(itemdatabase), "Awake")]
        [HarmonyPostfix]
        private static void AfterDatabase(itemdatabase __instance)
        {
            try { AddReceivers(__instance); }
            catch (Exception e) { Log.LogError("adding trailer receiver hitches failed: " + e); }
        }

        private static void AddReceivers(itemdatabase db)
        {
            if (db?.items == null) return;
            var trailer = db.items[(int)itemdatabase.i.utanfutofull];
            var drawbar = trailer != null ? trailer.GetComponent<attachgripscript>() : null;
            var grip = drawbar != null && drawbar.GripPrefab != null
                ? drawbar.GripPrefab.GetComponent<attachablescript>()
                : null;
            if (grip == null)
            {
                Log.LogError("the stock trailer drawbar prefab is missing; no receiver hitches were added");
                return;
            }

            var seen = new HashSet<GameObject>();
            int added = 0, alreadySupported = 0;
            for (int id = 0; id < db.items.Length; id++)
            {
                var prefab = db.items[id];
                if (prefab == null || !seen.Add(prefab) || !prefab.name.StartsWith("Car", StringComparison.OrdinalIgnoreCase) ||
                    prefab.GetComponent<carscript>() == null ||
                    prefab.GetComponent<tosaveitemscript>() == null || prefab.GetComponentInChildren<attachgripscript>(true) != null)
                    continue;

                var save = prefab.GetComponent<tosaveitemscript>();
                bool hasNativeReceiver = false;
                foreach (var slot in prefab.GetComponentsInChildren<partslotscript>(true))
                {
                    try
                    {
                        if (slot.CanCraft(grip)) { hasNativeReceiver = true; break; }
                    }
                    catch { /* Some unrelated slots are only fully initialized on spawned objects. */ }
                }
                if (hasNativeReceiver)
                {
                    alreadySupported++;
                    continue;
                }

                if (save.partslotscripts == null) save.partslotscripts = new List<partslotscript>();
                var anchor = new GameObject("TrailerReceiverHitch").transform;
                anchor.SetParent(prefab.transform, false);
                anchor.localPosition = RearHitchPosition(prefab);
                anchor.localRotation = Quaternion.identity;

                var receiver = anchor.gameObject.AddComponent<partslotscript>();
                receiver.tosaveitem = save;
                receiver.pick = save.GetComponent<pickupable>();
                receiver.T = anchor;
                receiver.parts = new List<attachablescript>();
                receiver.maintipus = grip.maintipus != null ? (string[])grip.maintipus.Clone() : Array.Empty<string>();
                receiver.tipus = grip.tipus != null ? (string[])grip.tipus.Clone() : Array.Empty<string>();
                save.partslotscripts.Add(receiver);

                AddHitchVisual(anchor);
                added++;
                Log.LogInfo($"added trailer receiver hitch to {prefab.name} (item {id})");
            }
            Log.LogInfo($"added trailer receiver hitches to {added} car prefab(s); left {alreadySupported} stock-compatible prefab(s) unchanged");
        }

        // TLD vehicle forward is +Z, so the hitch sits just behind the rear-most body bounds at -Z.
        private static Vector3 RearHitchPosition(GameObject prefab)
        {
            float minZ = float.PositiveInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            bool found = false;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var b = renderer.bounds;
                for (int mask = 0; mask < 8; mask++)
                {
                    var world = new Vector3((mask & 1) == 0 ? b.min.x : b.max.x,
                        (mask & 2) == 0 ? b.min.y : b.max.y, (mask & 4) == 0 ? b.min.z : b.max.z);
                    var local = prefab.transform.InverseTransformPoint(world);
                    minZ = Mathf.Min(minZ, local.z);
                    minY = Mathf.Min(minY, local.y);
                    maxY = Mathf.Max(maxY, local.y);
                    found = true;
                }
            }
            if (!found) return new Vector3(0f, 0.25f, -2f);
            return new Vector3(0f, Mathf.Clamp(minY + (maxY - minY) * 0.30f, minY + 0.12f, maxY - 0.1f), minZ - 0.4f);
        }

        private static void AddHitchVisual(Transform anchor)
        {
            var material = new Material(Shader.Find("Standard")) { color = new Color(0.12f, 0.12f, 0.12f) };
            MakePart(anchor, "ReceiverTube", PrimitiveType.Cube, new Vector3(0f, -0.02f, 0.24f), new Vector3(0.16f, 0.13f, 0.48f), material);
            MakePart(anchor, "HitchNeck", PrimitiveType.Cube, new Vector3(0f, 0.01f, 0.07f), new Vector3(0.09f, 0.12f, 0.20f), material);
            MakePart(anchor, "HitchBall", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.13f, 0.13f, 0.13f), material);
        }

        private static void MakePart(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);
            var renderer = part.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
        }
    }
}
