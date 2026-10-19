using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace TrailerHitches
{
    /// Diagnostics through the existing bridge: call TrailerHitches.Probe.Catalog / Measure <item id>.
    public static class Probe
    {
        private static string Number(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
        private static string Vector(Vector3 v) => "[" + Number(v.x) + "," + Number(v.y) + "," + Number(v.z) + "]";
        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

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
                bool added = slot != null && slot.name == Plugin.ReceiverName;
                var save = prefab.GetComponent<tosaveitemscript>();
                rows.Add("{\"id\":" + id + ",\"name\":" + Quote(prefab.name) + ",\"added\":" + (added ? "true" : "false") +
                    ",\"slot\":" + (slot != null ? save.partslotscripts.IndexOf(slot) : -1) +
                    ",\"position\":" + (slot != null ? Vector(prefab.transform.InverseTransformPoint(slot.T.position)) : "null") +
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
            var root = slot.transform.parent;
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
            return slot.tosaveitem == root.GetComponentInParent<tosaveitemscript>();
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

        public static string Measure(int itemId)
        {
            tosaveitemscript car = null;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.id == itemId && it.car != null && (car == null || it.idInSave > car.idInSave)) car = it;
            if (car == null) throw new InvalidOperationException("no loaded car for item " + itemId);
            var grip = Plugin.Drawbar(itemdatabase.s);
            partslotscript slot = null;
            // An occupied receiver cannot CanCraft another drawbar. Its existing part identifies it after hitching.
            foreach (var s in car.partslotscripts)
                if (s != null && (s.hasPart() && s.part().utanfuto != null || !s.hasPart() && s.CanCraft(grip))) { slot = s; break; }
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
            return "{\"item\":" + itemId + ",\"save\":" + car.idInSave + ",\"slot\":" + car.partslotscripts.IndexOf(slot) +
                ",\"local\":" + Vector(local) + ",\"bodyRearZ\":" + Number(BodyRear(car, local.y, slot.transform.parent)) +
                ",\"hitchHeight\":" + Number(groundFound ? slot.T.position.y - ground.point.y : float.NaN) +
                ",\"drawbarHeight\":" + Number(drawbarHeight) + ",\"pitch\":" + Number(pitch) + ",\"roll\":" + Number(roll) +
                ",\"gap\":" + Number(gap) + ",\"attached\":" + (part != null && part.attached ? "true" : "false") + "}";
        }

        public static string RecordRoundTrip(int itemId, bool missingSlot)
        {
            tosaveitemscript car = null;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.id == itemId && it.car != null && (car == null || it.idInSave > car.idInSave)) car = it;
            if (car == null) throw new InvalidOperationException("car missing");
            var slot = car.GetComponentInChildren<partslotscript>(true);
            foreach (var candidate in car.partslotscripts)
                if (candidate != null && candidate.name == Plugin.ReceiverName) { slot = candidate; break; }
            if (slot == null || slot.name != Plugin.ReceiverName || !slot.hasPart()) throw new InvalidOperationException("added receiver is not occupied");
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
