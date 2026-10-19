using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace TrailerHitches
{
    /// Diagnostics through the existing bridge: Catalog, staged Spawn, Measure/RecordRoundTrip by save id.
    public static class Probe
    {
        private sealed class TestSpawn
        {
            internal GameObject Root;
            internal readonly HashSet<tosaveitemscript> Members = new HashSet<tosaveitemscript>();
            internal readonly HashSet<uint> SaveIds = new HashSet<uint>();
            internal int Frame;
            internal bool Finished;
            internal string Error;
        }
        private static readonly Dictionary<int, TestSpawn> Spawns = new Dictionary<int, TestSpawn>();
        private static int nextSpawn;
        private static string Number(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
        private static string Vector(Vector3 v) => "[" + Number(v.x) + "," + Number(v.y) + "," + Number(v.z) + "]";
        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

        // The mod names the assembly, whose slot may retain the donor collider's name.
        private static Transform ReceiverAssembly(partslotscript slot, Transform car)
        {
            var assembly = car.Find(Plugin.ReceiverName);
            return assembly != null && slot != null && slot.transform.IsChildOf(assembly) &&
                slot.GetComponentInParent<tosaveitemscript>(true)?.transform == car ? assembly : null;
        }

        private static bool WorldReady() => mainscript.s != null && mainscript.s.player != null &&
                mainscript.s.player.gameObject.activeInHierarchy && itemdatabase.s?.items != null &&
                savedatascript.s != null && itemPlaceRemoveScript.s != null &&
                menuhandler.s != null && menuhandler.s.currentMainMap != null && Plugin.StockSlot != null;

        public static string Ready() => "{\"ready\":" + (WorldReady() ? "true" : "false") + "}";

        /// Stage the clone at its final, nearby location. Poll SpawnState across frames before PostSpawn:
        /// the synchronous dev spawn calls FStart while occupied slots can still have uninitialized part owners.
        public static string Spawn(int itemId, double gx, double gz, float yaw)
        {
            if (!WorldReady()) throw new InvalidOperationException("world not ready");
            if (itemId < 0 || itemId >= itemdatabase.s.items.Length || itemdatabase.s.items[itemId] == null)
                throw new ArgumentException("no prefab for item " + itemId);
            var player = mainscript.s.player.transform.position;
            var at = mainscript.UnityPosFromGlobal(new Vector3d(gx, 0, gz));
            at.y = player.y;
            if (Vector3.Distance(at, player) > 60f) throw new InvalidOperationException("spawn must be within 60m of player");
            if (!Ground(at, out var ground)) throw new InvalidOperationException("no nearby ground generated");
            var entry = new TestSpawn { Frame = Time.frameCount };
            int ticket = ++nextSpawn;
            Spawns.Add(ticket, entry);
            try
            {
                entry.Root = UnityEngine.Object.Instantiate(itemdatabase.s.items[itemId],
                    ground.point + Vector3.up * 0.6f, Quaternion.Euler(0f, yaw, 0f));
                Remember(entry);
            }
            catch (Exception e) { entry.Error = e.ToString(); }
            return "{\"ticket\":" + ticket + "}";
        }

        private static void Remember(TestSpawn entry)
        {
            if (entry.Root != null)
                foreach (var member in entry.Root.GetComponentsInChildren<tosaveitemscript>(true)) entry.Members.Add(member);
            foreach (var member in entry.Members)
                if (member != null && member.started) entry.SaveIds.Add(member.idInSave);
        }

        public static string SpawnState(int ticket)
        {
            var entry = Spawns[ticket];
            if (entry.Error != null) return "{\"error\":" + Quote(entry.Error) + "}";
            if (entry.Root == null) return "{\"error\":\"spawned object disappeared\"}";
            Remember(entry);
            var save = entry.Root.GetComponent<tosaveitemscript>();
            if (save == null) return "{\"error\":\"spawned root has no save component\"}";
            if (!entry.Finished)
            {
                if (Time.frameCount <= entry.Frame + 2) return "{\"ready\":false}";
                // Parent FStart recursively dereferences part.tosave BEFORE the child's IDForMulti calls
                // attachablescript.FStart (which caches that component). Initialize that cache on our clones only.
                foreach (var member in entry.Members)
                    if (member != null && member.partslotscripts != null)
                        for (int index = 0; index < member.partslotscripts.Count; index++)
                        {
                            var slot = member.partslotscripts[index];
                            if (slot == null) return "{\"error\":" + Quote("null slot in spawned " + member.name + " at index " + index) +
                                ",\"receiver\":" + ReceiverState(save) + "}";
                            if (slot.hasPart()) foreach (var part in slot.parts)
                                if (part != null && part.tosave == null)
                                {
                                    var owner = part.GetComponent<tosaveitemscript>();
                                    if (owner == null || !entry.Members.Contains(owner))
                                        return "{\"error\":\"occupied slot part has no owned save component\"}";
                                    part.tosave = owner;
                                }
                        }
                try
                {
                    var condition = entry.Root.GetComponent<partconditionscript>();
                    if (condition != null) condition.StartPaint(0, Color.white);
                    mainscript.s.PostSpawn(entry.Root);
                    Remember(entry);
                    Physics.SyncTransforms();
                    // Diagnostic-only soft dependency: share the initialized exact root through the existing API.
                    var entities = Type.GetType("TLDRevamp.Net.Entities, TLDRevamp", true);
                    string shared = (string)entities.GetMethod("ShareItem").Invoke(null, new object[] { save, null });
                    if (shared.Contains("\"error\"")) throw new InvalidOperationException(shared);
                    entry.Finished = true;
                }
                catch (Exception e) { entry.Error = e.ToString(); return "{\"error\":" + Quote(entry.Error) + "}"; }
            }
            bool loaded = Visible(save) &&
                savedatascript.s.items.TryGetValue(save.idInSave, out var current) && current == save;
            return "{\"ready\":" + (loaded ? "true" : "false") + ",\"save\":" + save.idInSave + ",\"runtimeItem\":" + save.id +
                ",\"receiver\":" + ReceiverState(save) + "}";
        }

        // Observe the actual instance without repairing it or consulting the prefab's slot list.
        private static string ReceiverState(tosaveitemscript save)
        {
            var assembly = save.transform.Find(Plugin.ReceiverName);
            var grip = Plugin.Drawbar(itemdatabase.s);
            var slot = assembly != null ? assembly.GetComponentInChildren<partslotscript>(true) : Plugin.CompatibleSlot(save.gameObject, grip);
            int index = slot != null && save.partslotscripts != null ? save.partslotscripts.IndexOf(slot) : -1;
            bool owned = slot != null && slot.tosaveitem == save && slot.GetComponentInParent<tosaveitemscript>(true) == save;
            return "{\"assembly\":" + (assembly != null ? "true" : "false") +
                ",\"component\":" + (slot != null ? "true" : "false") + ",\"slot\":" + index +
                ",\"listEntry\":" + (index >= 0 && save.partslotscripts[index] == slot ? "true" : "false") +
                ",\"owned\":" + (owned ? "true" : "false") +
                ",\"canCraft\":" + (slot != null && grip != null && slot.CanCraft(grip) ? "true" : "false") + "}";
        }

        public static string Cleanup(int ticket)
        {
            if (!Spawns.TryGetValue(ticket, out var entry)) return "{\"cleaned\":true}";
            Remember(entry);
            // Include parts that unparented themselves, and records/copies streamed out during a failed test.
            if (savedatascript.s != null)
                foreach (uint id in entry.SaveIds)
                {
                    if (savedatascript.s.items.TryGetValue(id, out var live) && live != null) entry.Members.Add(live);
                    savedatascript.s.items.Remove(id);
                    var data = savedatascript.s.data?.itemData;
                    if (data != null && savedatascript.IndexOfID(data.items, id, out int index)) data.items.RemoveAt(index);
                }
            foreach (var member in entry.Members)
                if (member != null)
                {
                    if (member.started) member.JustDestroy();
                    else UnityEngine.Object.Destroy(member.gameObject);
                }
            if (entry.Root != null) UnityEngine.Object.Destroy(entry.Root);
            Spawns.Remove(ticket);
            return "{\"cleaned\":true}";
        }

        private static tosaveitemscript LoadedCar(uint saveId)
        {
            if (savedatascript.s == null || !savedatascript.s.items.TryGetValue(saveId, out var car) ||
                car == null || car.car == null || !Visible(car))
                throw new InvalidOperationException("no loaded car for save " + saveId);
            return car;
        }

        private static bool Visible(tosaveitemscript save) => save.started && save.gameObject.activeInHierarchy &&
            (save.P == null || save.P.disableThisWhenStored == null || save.P.disableThisWhenStored.gameObject.activeSelf);

        public static string Catalog()
        {
            var rows = new List<string>();
            var seen = new HashSet<GameObject>();
            var grip = Plugin.Drawbar(itemdatabase.s);
            if (grip == null) throw new InvalidOperationException("stock drawbar missing");
            for (int id = 0; id < itemdatabase.s.items.Length; id++)
            {
                var prefab = itemdatabase.s.items[id];
                if (prefab == null || !seen.Add(prefab) || !Plugin.DriveableCar(prefab)) continue;
                var slot = Plugin.CompatibleSlot(prefab, grip);
                bool added = ReceiverAssembly(slot, prefab.transform) != null;
                var save = prefab.GetComponent<tosaveitemscript>();
                rows.Add("{\"id\":" + id + ",\"name\":" + Quote(prefab.name) + ",\"added\":" + (added ? "true" : "false") +
                    ",\"slot\":" + (slot != null ? save.partslotscripts.IndexOf(slot) : -1) +
                    ",\"position\":" + (slot != null ? Vector(prefab.transform.InverseTransformPoint(slot.T.position)) : "null") +
                    ",\"runtimeItem\":" + save.id + ",\"slotName\":" + (slot != null ? Quote(slot.name) : "null") +
                    ",\"structure\":" + (added && SameStructure(slot) ? "true" : "false") + "}");
            }
            return "{\"cars\":[" + string.Join(",", rows) + "]}";
        }

        private static string Path(Transform t, Transform root)
        {
            if (t == root) return "";
            if (t == null || !t.IsChildOf(root)) return "external";
            return Path(t.parent, root) + "/" + t.GetSiblingIndex();
        }

        private static bool SameStructure(partslotscript slot)
        {
            var root = ReceiverAssembly(slot, slot.tosaveitem.transform);
            var stock = Plugin.StockSlot;
            // Unity clones serialized fields, including object references. Check all public slot fields, not a handpicked
            // subset; ownership/index are deliberately different. Empty lists and the compatibility arrays must match.
            foreach (var f in typeof(partslotscript).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name == "tosaveitem" || f.Name == "pick" || f.Name == "id") continue;
                object a = f.GetValue(stock), b = f.GetValue(slot);
                if (a is Transform at && b is Transform bt)
                {
                    if (Path(at, Plugin.StockAssembly) != Path(bt, root)) return false;
                }
                else if (a is System.Collections.IList al && b is System.Collections.IList bl)
                {
                    if (al.Count != bl.Count) return false;
                    for (int i = 0; i < al.Count; i++) if (!Equals(al[i], bl[i])) return false;
                }
                else if (!Equals(a, b)) return false;
            }
            var original = Plugin.StockAssembly.GetComponentsInChildren<Collider>(true);
            var copies = root.GetComponentsInChildren<Collider>(true);
            if (original.Length != copies.Length || slot.enabled != stock.enabled) return false;
            var originalTransforms = Plugin.StockAssembly.GetComponentsInChildren<Transform>(true);
            var copyTransforms = root.GetComponentsInChildren<Transform>(true);
            if (copyTransforms.Length != originalTransforms.Length + 5) return false;
            for (int i = 1; i < originalTransforms.Length; i++)
                if (originalTransforms[i].localPosition != copyTransforms[i].localPosition ||
                    originalTransforms[i].localRotation != copyTransforms[i].localRotation ||
                    originalTransforms[i].localScale != copyTransforms[i].localScale) return false;
            for (int i = 0; i < copies.Length; i++)
            {
                if (copies[i].GetType() != original[i].GetType() || copies[i].isTrigger != original[i].isTrigger ||
                    copies[i].enabled != original[i].enabled || copies[i].gameObject.layer != original[i].gameObject.layer ||
                    copies[i].sharedMaterial != original[i].sharedMaterial || Path(copies[i].transform, root) != Path(original[i].transform, Plugin.StockAssembly)) return false;
                if (copies[i] is SphereCollider sc && original[i] is SphereCollider os && (sc.radius != os.radius || sc.center != os.center)) return false;
                if (copies[i] is BoxCollider bc && original[i] is BoxCollider ob && (bc.size != ob.size || bc.center != ob.center)) return false;
                if (copies[i] is CapsuleCollider cc && original[i] is CapsuleCollider oc &&
                    (cc.radius != oc.radius || cc.height != oc.height || cc.center != oc.center || cc.direction != oc.direction)) return false;
                if (copies[i] is MeshCollider mc && original[i] is MeshCollider om && (mc.sharedMesh != om.sharedMesh || mc.convex != om.convex)) return false;
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) if (renderer.sharedMaterial != Plugin.Metal) return false;
            return slot.tosaveitem == root.GetComponentInParent<tosaveitemscript>(true);
        }

        private static bool Ground(Vector3 point, out RaycastHit ground)
        {
            ground = default;
            float distance = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(point + Vector3.up * 3f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<tosaveitemscript>() != null || hit.distance >= distance) continue;
                ground = hit;
                distance = hit.distance;
            }
            return !float.IsPositiveInfinity(distance);
        }

        private static float BodyRear(tosaveitemscript car, float height, Transform receiverAssembly)
        {
            float rear = float.PositiveInfinity;
            // A centre-line slice of the main save owner's solid body, above the ball at frame/bumper height. Slot
            // hitboxes, storage volumes and detachable parts are excluded. This is a measurement, never runtime placement.
            foreach (var col in car.GetComponentsInChildren<Collider>(true))
            {
                if (!col.enabled || col.isTrigger || col is WheelCollider || col.GetComponentInParent<tosaveitemscript>() != car ||
                    col.transform.IsChildOf(receiverAssembly) || col.GetComponentInParent<partslotscript>() != null ||
                    col.GetComponent<mountStuff>() != null) continue;
                for (int i = 0; i < 4; i++)
                {
                    var origin = car.transform.TransformPoint(new Vector3(0f, height + i * 0.05f, -20f));
                    if (col.Raycast(new Ray(origin, car.transform.forward), out var hit, 40f))
                        rear = Mathf.Min(rear, car.transform.InverseTransformPoint(hit.point).z);
                }
            }
            return rear;
        }

        public static string Measure(uint saveId)
        {
            // Full/assembled prefabs can serialize the base model's item id. The spawn's save id is unique.
            var car = LoadedCar(saveId);
            var grip = Plugin.Drawbar(itemdatabase.s);
            partslotscript slot = null;
            // An occupied receiver cannot CanCraft another drawbar. Its existing part identifies it after hitching.
            foreach (var s in car.GetComponentsInChildren<partslotscript>(true))
                if (s.GetComponentInParent<tosaveitemscript>(true) == car && s.maintipus != null && s.tipus != null &&
                    (s.hasPart() && s.part().utanfuto != null || !s.hasPart() && s.CanCraft(grip))) { slot = s; break; }
            if (slot == null) throw new InvalidOperationException("no receiver");
            var local = car.transform.InverseTransformPoint(slot.T.position);
            bool groundFound = Ground(slot.T.position, out var ground);
            var part = slot.hasPart() ? slot.part() : null;
            var trailer = part != null ? part.tosave : null;
            float drawbarHeight = float.NaN, pitch = float.NaN, roll = float.NaN, gap = float.NaN;
            if (trailer != null && Ground(trailer.transform.position, out var trailerGround))
            {
                drawbarHeight = Ground(part.T.position, out var drawbarGround) ? part.T.position.y - drawbarGround.point.y : float.NaN;
                pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(trailer.transform.forward, trailerGround.normal), -1f, 1f)) * Mathf.Rad2Deg;
                roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(trailer.transform.right, trailerGround.normal), -1f, 1f)) * Mathf.Rad2Deg;
                gap = Vector3.Distance(slot.T.position, part.T.position);
            }
            return "{\"item\":" + car.id + ",\"save\":" + car.idInSave + ",\"slot\":" + car.partslotscripts.IndexOf(slot) +
                ",\"local\":" + Vector(local) + ",\"bodyRearZ\":" + Number(BodyRear(car, local.y, ReceiverAssembly(slot, car.transform) ?? slot.transform.parent)) +
                ",\"hitchHeight\":" + Number(groundFound ? slot.T.position.y - ground.point.y : float.NaN) +
                ",\"drawbarHeight\":" + Number(drawbarHeight) + ",\"pitch\":" + Number(pitch) + ",\"roll\":" + Number(roll) +
                ",\"gap\":" + Number(gap) + ",\"attached\":" + (part != null && part.attached ? "true" : "false") + "}";
        }

        public static string RecordRoundTrip(uint saveId, bool missingSlot)
        {
            var car = LoadedCar(saveId);
            partslotscript slot = null;
            foreach (var candidate in car.partslotscripts)
                if (ReceiverAssembly(candidate, car.transform) != null) { slot = candidate; break; }
            if (slot == null || !slot.hasPart()) throw new InvalidOperationException("added receiver is not occupied");
            int index = car.partslotscripts.IndexOf(slot);
            if (index != car.partslotscripts.Count - 1) throw new InvalidOperationException("receiver is not the last slot");
            var part = slot.part();
            var record = new save_attachable(part, part.tosave.idInSave);
            if (record.index != index || record.indexType != 2 || record.parentid != car.idInSave)
                throw new InvalidOperationException("record did not identify the receiver's owner/index");
            bool removed = false;
            try
            {
                if (!slot.UnCraft()) throw new InvalidOperationException("uncraft refused");
                if (missingSlot) { car.partslotscripts.RemoveAt(index); removed = true; }
                record.Load(part);
                return "{\"index\":" + record.index + ",\"parent\":" + record.parentid + ",\"trailer\":" + part.tosave.idInSave +
                    ",\"attached\":" + (part.attached ? "true" : "false") + ",\"inSlot\":" + (part.slot == slot ? "true" : "false") + "}";
            }
            finally
            {
                if (removed)
                {
                    if (part.attached) part.Detach();
                    car.partslotscripts.Add(slot);
                    record.Load(part);
                }
            }
        }
    }
}
