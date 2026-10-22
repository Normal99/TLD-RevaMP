using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CarTrailer
{
    /// A car trailer built like the Brakas transporter's back: the Brakas's own bed (meshes, colliders, hinged ramp) low
    /// between four wheels, on the game's trailer (its scripts: hitching, brake, lights, saving), with the transporter's
    /// way of carrying a car (a storage area, mountStuff, that lists the car and locks its wheels). An example of a mod
    /// that needs NO multiplayer code: it is a
    /// game item, so the game saves and loads it, and TLD Revamp shares it like the game's own trailer, the car on it
    /// included (a carried car follows the machine that simulates its carrier: Entities.CarCargoTick).
    ///
    /// What it can't get without the API: every player must install it by hand (the join checks only mods that
    /// register with TLDRevamp.Multiplayer), and anything it kept outside the game's save records would stay local.
    [BepInPlugin("tldrevamp.cartrailer", "Car Trailer", Version)]
    [HarmonyPatch]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.1.1";
        public const int ItemId = 9200;          // far above the game's own ids; never renumber (saves store it)
        public const int LootWeight = 8;         // against 100 for each of the game's two trailers
        // with the game's vehicles at bus stops and buildings (category 39): each of its ten weighs 1 (the game keeps the
        // chances written there in categorychances, which an item list ignores: itemdatabase.selectId)
        public const int VehicleLootWeight = 1;

        // the Brakas's bed in its own space (prefab dump, `mount prefab 397`): wheel tracks' tops at y 0.125, from
        // behind the cab (z 0.6) to the tail (z -4.75), frame rails down to y -0.33; sides kept low
        public static float BrakasFloor = 0.125f, BedFront = 0.6f, BedRear = -4.75f, BedBottom = -0.36f, BedTop = 0.45f, BedHalfWidth = 0.95f;
        // the trailer: wheels where the Brakas has its rear ones (so the floor is as high above them as on the Brakas)
        public static float DrawbarClear = 1.0f;
        public static float ExtraMass = 250f;

        internal static ManualLogSource Log;
        private static GameObject _holder, _prefab;
        internal static int HolderChildren => _holder != null ? _holder.transform.childCount : -1;

        private void Awake()
        {
            Log = Logger;
            new Harmony("tldrevamp.cartrailer").PatchAll(typeof(Plugin).Assembly);
        }

        [HarmonyPatch(typeof(itemdatabase), "Awake")]
        [HarmonyPostfix]
        private static void AfterDatabase(itemdatabase __instance)
        {
            try { Register(__instance); }
            catch (Exception e) { Log.LogError("registering the car trailer failed: " + e); }
        }

        private static void Register(itemdatabase db)
        {
            var src = db.items[(int)itemdatabase.i.utanfutofull];
            var brakas = db.items[(int)itemdatabase.i.car09Tfull];
            if (src == null) { Log.LogError("the game's trailer (utanfutofull) is missing: no car trailer"); return; }
            if (db.items.Length <= ItemId) Array.Resize(ref db.items, ItemId + 1);
            if (_holder == null) { _holder = new GameObject("CarTrailerPrefabs"); _holder.SetActive(false); DontDestroyOnLoad(_holder); }
            // The database is a scene object that fills its items again on every scene load (a new game, a load, the
            // menu): the trailer is built once and only put back after that. Built each time, another trailer and
            // its ~17 cut meshes piled up in the holder per load.
            if (_prefab != null) { db.items[ItemId] = _prefab; AddToLoot(db); return; }

            var prefab = Instantiate(src, _holder.transform);
            prefab.name = "cartrailer";
            var save = prefab.GetComponent<tosaveitemscript>();
            save.id = ItemId;
            var trailer = prefab.GetComponent<utanfutoscript>();
            var root = prefab.transform;
            var grip = prefab.GetComponent<attachgripscript>();
            if (trailer == null || grip == null || grip.GripPos == null || brakas == null) { Log.LogError("the game's trailer or the Brakas isn't as expected: no car trailer"); return; }

            // 1. the game's trailer keeps its scripts, wheels, drawbar coupler, rear tow ball, plate and lights; its tub
            //    (looks and colliders) and its load area go
            foreach (var m in prefab.GetComponentsInChildren<mountStuff>(true))
            {
                save.physLocks?.Remove(m);
                DestroyImmediate(m.gameObject);
            }
            Transform body = root.Find("utanfuto4");
            if (body != null)   // its tub and light bar (rearlights gets the Brakas's lamps instead, below)
                foreach (Transform c in body) { var r = c.GetComponent<MeshRenderer>(); if (r != null) r.enabled = false; }
            var tubLook = root.Find("Cube");
            if (tubLook != null && tubLook.GetComponent<MeshRenderer>() != null) tubLook.GetComponent<MeshRenderer>().enabled = false;   // keeps its keep-awake wheel
            var tub = new List<GameObject>();
            foreach (Transform c in root)
                if (c.name.StartsWith("Collider") && c.GetComponent<BoxCollider>() != null && c.GetComponent<partslotscript>() == null) tub.Add(c.gameObject);
            foreach (var go in tub) DestroyImmediate(go);

            // 2. the Brakas's bed, from just behind its cab to its tail, from under its frame to low sides; its floor
            //    (the tops of its wheel tracks) as high over the wheels as on the Brakas
            var region = new Bounds(new Vector3(0f, (BedBottom + BedTop) * 0.5f, (BedFront + BedRear) * 0.5f),
                                    new Vector3(BedHalfWidth * 2f, BedTop - BedBottom, BedFront - BedRear));
            // the Brakas's rear wheels (two each side) and where they are: the trailer's go to the same spots, and the
            // bed is placed so those axles sit around the trailer's origin
            var brakasAxles = new List<float>(); float brakasWheelY = 0f, brakasWheelX = 0.75f;
            foreach (var ps in brakas.GetComponentsInChildren<partslotscript>(true))
            {
                if (!ps.name.EndsWith("WheelSlotScript")) continue;
                var lp = brakas.transform.InverseTransformPoint(ps.transform.position);
                if (lp.z > -0.5f) continue;   // the cab's wheels
                brakasWheelY = lp.y; brakasWheelX = Mathf.Abs(lp.x);
                if (!brakasAxles.Exists(z => Mathf.Abs(z - lp.z) < 0.1f)) brakasAxles.Add(lp.z);
            }
            if (brakasAxles.Count == 0) { brakasAxles.Add(-2.08f); brakasAxles.Add(-2.85f); brakasWheelY = -0.32f; }
            float axleMid = 0f; foreach (var z in brakasAxles) axleMid += z; axleMid /= brakasAxles.Count;
            const float trailerWheelY = 0.35f;   // the game's trailer's wheel centre
            float floorY = trailerWheelY + (BrakasFloor - brakasWheelY);
            var shiftV = new Vector3(0f, floorY - BrakasFloor, -axleMid);
            var shift = Matrix4x4.Translate(shiftV);
            var bedHolder = new GameObject("BrakasBed").transform; bedHolder.SetParent(root, false);
            var ramp = brakas.transform.Find("car09t/RotRot");   // the tailgate ramp (the side mirrors have a RotRot too)
            var back = BrakasBed.CopyRegion(brakas, region, shift, bedHolder, ramp);
            // the ramp, hinged as on the Brakas: the object with its use script (the trailer's save record keeps its state)
            int usables = 0;
            if (ramp != null)
            {
                var rc = BrakasBed.CopyWhole(brakas, ramp, shift, bedHolder);
                if (save.usables == null) save.usables = new List<usablescript>();
                foreach (var u in rc.GetComponentsInChildren<usablescript>(true)) { save.usables.Add(u); usables++; }
            }
            float bedFront = BedFront + shiftV.z, bedRear = BedRear + shiftV.z;

            // 3. four wheels, two each side, where the Brakas has its rear wheels
            foreach (var side in new[] { ("LeftWheelRot", "WheelL", "dust", -1f), ("RightWheelRot", "WheelR", "dust (1)", 1f) })
            {
                var set = new List<Transform> { root.Find(side.Item1), root.Find(side.Item2), root.Find(side.Item3) };
                if (set.Contains(null)) { Log.LogWarning("trailer wheel " + side.Item1 + " not found"); continue; }
                var groups = new List<List<Transform>> { set };
                for (int k = 1; k < brakasAxles.Count; k++)
                {
                    var copy = BrakasBed.CloneTogether(root, set);
                    groups.Add(copy);
                    var wc = copy[1].GetComponent<WheelCollider>();
                    if (wc != null) trailer.wheels.Add(wc);
                    // the copy's wheel slot is saved with the trailer, as the original's
                    foreach (var ps in copy[0].GetComponentsInChildren<partslotscript>(true))
                        if (OwnerSave(ps.transform) == save) save.partslotscripts?.Add(ps);
                }
                for (int k = 0; k < groups.Count; k++)
                    foreach (var t in groups[k])
                        t.position += root.TransformVector(new Vector3(side.Item4 * (brakasWheelX - 0.75f), 0f, brakasAxles[k] + shiftV.z));   // the game's wheels sit at ±0.75, z 0
            }
            var mat = FirstMaterial(bedHolder.gameObject) ?? FirstMaterial(prefab);

            // 4. the coupler just ahead of the bed, an A-frame drawbar to it (looks only: it reaches the tow car), and
            //    the light bar, plate and rear tow ball at the bed's tail
            var gripAt = root.InverseTransformPoint(grip.GripPos.position);
            float ballZ = bedFront + DrawbarClear;
            var move = new Vector3(0f, 0f, ballZ - gripAt.z);
            grip.GripPos.position += root.TransformVector(move);
            if (grip.fixGripPos) grip.VgripPos += move;
            foreach (float sx in new[] { -0.45f, 0.45f })
            {
                var from = new Vector3(sx, floorY - 0.3f, bedFront - 0.3f); var to = new Vector3(gripAt.x, gripAt.y, ballZ);
                var dir = to - from;
                NoCollider(Box(root, "CarTrailerDrawbar", (from + to) * 0.5f, Quaternion.LookRotation(dir), new Vector3(0.09f, 0.09f, dir.magnitude + 0.05f), mat));
            }
            // the old trailer's tow ball and plate holder go (slots out of the save list first); its lamps' lights
            // move to the Brakas tail lights, which rearlights now lights (brake → the tail lights, turn → the indicators)
            foreach (var n in new[] { "VonoHorog", "RearLicensePlate" })
            {
                var t = root.Find(n);
                if (t == null) continue;
                foreach (var ps in t.GetComponentsInChildren<partslotscript>(true)) save.partslotscripts?.Remove(ps);
                DestroyImmediate(t.gameObject);
            }
            var tail = FindDeep(bedHolder, "tail light trailer"); var indicator = FindDeep(bedHolder, "index trailer rear");
            var lights = prefab.GetComponent<rearlights>();
            if (lights != null)
            {
                Renderer Swap(Renderer r)
                {
                    if (r == null) return null;
                    if (r.name == "BrakeLight") return tail != null ? tail.GetComponent<Renderer>() : null;
                    if (r.name == "Index") return indicator != null ? indicator.GetComponent<Renderer>() : null;
                    return null;
                }
                Renderer[] Map(Renderer[] a) { var l = new List<Renderer>(); if (a != null) foreach (var r in a) { var n = Swap(r); if (n != null && !l.Contains(n)) l.Add(n); } return l.ToArray(); }
                lights.FLamp = Map(lights.FLamp); lights.RLamp = Map(lights.RLamp); lights.HLamp = Map(lights.HLamp);
            }
            float tailZ = bedRear + 0.05f;
            foreach (var n in new[] { "FLamp (1)", "FLamp (2)" })
            {
                var t = root.Find(n);
                if (t != null) { var lp = root.InverseTransformPoint(t.position); t.position = root.TransformPoint(new Vector3(lp.x, floorY - 0.2f, tailZ)); }
            }

            // the trailer's handbrake: the IFA trailer's lever (its use script, two positions, and its post) on the
            // drawbar's left, just ahead of the bed; the trailer script brakes the wheels while it is up
            var ifa = db.items[(int)itemdatabase.i.bus02UtanfutoFull];
            var lever = ifa != null ? ifa.transform.Find("Osszes (1)/GameObject (1)") : null;
            if (lever != null)
            {
                var leverAt = ifa.transform.InverseTransformPoint(lever.position);
                var to = Matrix4x4.Translate(new Vector3(-0.35f, floorY - 0.15f, bedFront + 0.35f) - leverAt);
                var copy = BrakasBed.CopyWhole(ifa, lever, to, root);
                foreach (var n in new[] { "Osszes (1)/Cube", "Osszes (1)/Cube (1)" })
                {
                    var t = ifa.transform.Find(n);
                    if (t != null) BrakasBed.CopyWhole(ifa, t, to, root);
                }
                var u = copy.GetComponent<usablescript>();
                if (u != null)
                {
                    if (save.usables == null) save.usables = new List<usablescript>();
                    save.usables.Add(u);
                    trailer.brake = u;
                    var ifaTrailer = ifa.GetComponent<utanfutoscript>();
                    trailer.brakeForce = ifaTrailer != null && ifaTrailer.brakeForce > 0f ? ifaTrailer.brakeForce : 2000f;
                }
            }
            else Log.LogWarning("the IFA trailer's handbrake lever wasn't found: the car trailer has none");

            // 5. the weight low and a little ahead of the axles
            foreach (var cm in new[] { trailer.CenterOfMass, trailer.CenterOfMassAttached })
                if (cm != null) cm.position = root.TransformPoint(new Vector3(0f, floorY - 0.3f, cm == trailer.CenterOfMass ? 0.2f : 0.5f));

            // 6. the car area: the Brakas's, where its bed went (a trunk area that doesn't lock: it lists the car and,
            //    with the car's handbrake on, locks its wheels, as on the Brakas)
            var bedRef = FindBed(brakas);
            var areaGo = new GameObject("CarMount");
            areaGo.transform.SetParent(root, false);
            var col = areaGo.AddComponent<BoxCollider>();
            col.isTrigger = true;
            if (bedRef != null && bedRef.col != null)
            {
                var cm = brakas.transform.worldToLocalMatrix * bedRef.col.transform.localToWorldMatrix;
                areaGo.transform.localPosition = shift.MultiplyPoint3x4(cm.MultiplyPoint3x4(bedRef.col.center));
                col.size = Vector3.Scale(bedRef.col.size, cm.lossyScale);
                areaGo.layer = bedRef.col.gameObject.layer;
                foreach (var lc in bedRef.col.GetComponents<Component>())
                    if (lc.GetType().Name == "layerScript" && areaGo.GetComponent(lc.GetType()) == null) areaGo.AddComponent(lc.GetType());
            }
            else { areaGo.transform.localPosition = new Vector3(0f, floorY + 0.36f, 0.5f); col.size = new Vector3(1.7f, 0.72f, 4.9f); }
            var mount = areaGo.AddComponent<mountStuff>();
            mount.sType = mountStuff.storageType.trunk;
            mount.noPhysicsLock = true;
            mount.col = col;
            mount.parentTosave = save;
            mount.slot = new partslotscript[0]; mount.susable = new usablescript[0]; mount.invertsusable = new usablescript[0];
            mount.ignoreCars = new carscript[0];
            if (save.physLocks == null) save.physLocks = new List<mountStuff>();
            save.physLocks.Add(mount);

            var rb = prefab.GetComponent<Rigidbody>();
            if (rb != null) rb.mass += ExtraMass;
            db.items[ItemId] = prefab;
            _prefab = prefab;
            AddToLoot(db);
            Log.LogInfo($"car trailer: item {ItemId}, bed z {bedRear:F2}..{bedFront:F2} floor {floorY:F2}, axles {string.Join("/", brakasAxles.ConvertAll(z => (z + shiftV.z).ToString("F2")))}, coupler z {ballZ:F2}, wheels {trailer.wheels.Count}, "
                + $"Brakas bed: {back.Whole} meshes, {back.Cut} cut ({back.CutTris} triangles), {back.Colliders} colliders, ramp {(ramp != null ? usables + " use script(s)" : "not found")}, handbrake {(trailer.brake != null ? "yes, force " + trailer.brakeForce : "no")}"
                + (back.Skipped.Count > 0 ? ", skipped " + string.Join(", ", back.Skipped) : ""));
        }

        private static void AddToLoot(itemdatabase db)
        {
            // rare: with the game's trailers (category 38)
            var cat = db.categories != null && db.categories.Length > 38 ? db.categories[38] : null;
            if (cat != null && cat.items != null && Array.IndexOf(cat.items, ItemId) < 0)
            {
                var items = new List<int>(cat.items); var ch = new List<int>();
                for (int k = 0; k < cat.items.Length; k++) ch.Add(cat.itemchances != null && k < cat.itemchances.Length ? cat.itemchances[k] : 100);
                items.Add(ItemId); ch.Add(LootWeight);
                cat.items = items.ToArray(); cat.itemchances = ch.ToArray();
            }
            // also where the game puts its vehicles (category 39: the IFA and its trailer, the Brakas, the buses...)
            var veh = db.categories != null && db.categories.Length > 39 ? db.categories[39] : null;
            if (veh != null && veh.items != null && Array.IndexOf(veh.items, ItemId) < 0)
            {
                var items = new List<int>(veh.items); var ch = new List<int>();
                for (int k = 0; k < veh.items.Length; k++) ch.Add(veh.itemchances != null && k < veh.itemchances.Length ? veh.itemchances[k] : 1);
                items.Add(ItemId); ch.Add(VehicleLootWeight);
                veh.items = items.ToArray(); veh.itemchances = ch.ToArray();
            }
        }

        private static GameObject Box(Transform parent, string name, Vector3 localPos, Quaternion localRot, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos; go.transform.localRotation = localRot; go.transform.localScale = size;
            go.layer = parent.gameObject.layer;
            if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private static void NoCollider(GameObject go) { var c = go.GetComponent<Collider>(); if (c != null) DestroyImmediate(c); }

        /// The save record an object belongs to (the nearest one up; works on the inactive prefab).
        private static tosaveitemscript OwnerSave(Transform t)
        {
            for (var p = t; p != null; p = p.parent) { var s = p.GetComponent<tosaveitemscript>(); if (s != null) return s; }
            return null;
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = FindDeep(c, name); if (f != null) return f; }
            return null;
        }

        private static mountStuff FindBed(GameObject car)
        {
            if (car == null) return null;
            foreach (var m in car.GetComponentsInChildren<mountStuff>(true))
                if (m.sType == mountStuff.storageType.trunk && m.noPhysicsLock) return m;
            return null;
        }

        private static Material FirstMaterial(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) if (r.sharedMaterial != null) return r.sharedMaterial;
            return null;
        }
    }
}
