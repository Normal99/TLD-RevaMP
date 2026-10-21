using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static Transform AttachFrameRoot(attachablescript at)
        {
            var it = at.AttachedToTosave();
            if (it != null) return it.transform;
            var poi = at.AttachedToPOI();
            return poi != null ? poi.transform : null;
        }

        private static int[] AttachFramePath(attachablescript at, Transform frame)
        {
            var root = AttachFrameRoot(at);
            if (root == null || !frame.IsChildOf(root)) return null;
            var path = new List<int>();
            for (var t = frame; t != root; t = t.parent) path.Add(t.GetSiblingIndex());
            path.Reverse();
            return path.ToArray();
        }

        private static bool DoorAttachFrame(Transform frame)
        {
            if (frame == null) return false;
            var it = frame.GetComponentInParent<tosaveitemscript>();
            if (it == null) return false;
            var e = SharedGroupOf(it);
            if (e == null) return HasFrame(it);
            foreach (var member in e.Items) if (member != null && HasFrame(member)) return true;
            return false;
            bool Under(Transform t) => t != null && frame.IsChildOf(t);
            bool HasFrame(tosaveitemscript member)
            {
                foreach (var u in ItemUsables(member))
                    if (u != null && (Under(u.RT1) || Under(u.RT2) || Under(u.TDoor))) return true;
                var bus = member.busdoorscript;
                if (bus != null && bus.doors != null)
                    foreach (var d in bus.doors) if (d != null && d.doors != null)
                        foreach (var leaf in d.doors) if (leaf != null && Under(leaf.transform)) return true;
                return false;
            }
        }

        /// Collider lookup can fall back to the item root. Rebuild the point in the sender's actual moving frame.
        private static bool RestoreAttachFrame(attachablescript at, int[] path, Vector3 point)
        {
            if (path == null) return true;
            if (at.slot != null || at.point == null) return false;
            var frame = AttachFrameRoot(at);
            foreach (int index in path)
            {
                if (frame == null || index < 0 || index >= frame.childCount) { AttachPosesBad++; return false; }
                frame = frame.GetChild(index);
            }
            if (frame == null || frame == at.transform || frame.IsChildOf(at.transform)) { AttachPosesBad++; return false; }
            at.point.SetParent(frame, true); at.point.localPosition = point;
            if (!at.parentAtAttach && DoorAttachFrame(frame))
            {
                bool was = _applyingAttach; _applyingAttach = true;
                try { at.parentAtAttach = true; at.Attach(at.point); }
                finally { _applyingAttach = was; }
            }
            return true;
        }

        private sealed class DoorAttachment
        {
            public int[] Path; public Vector3 Point, Position; public Quaternion Rotation; public Transform BoundPoint;
        }
        private static readonly Dictionary<uint, DoorAttachment> _storedDoorAttaches = new Dictionary<uint, DoorAttachment>();
        private static readonly List<uint> _restoredDoorAttaches = new List<uint>();

        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class ForgetDoorAttachment
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(attachablescript __instance)
            {
                if (_applyingDetach || _applyingAttach || __instance.tosave == null) return;
                var e = OwnEntity(__instance.tosave);
                if (e != null) _storedDoorAttaches.Remove(e.NetId);
            }
        }

        private static void KeepDoorAttach(uint net, int[] path, Vector3 point, Vector3 position, Quaternion rotation, Transform boundPoint = null)
        {
            _storedDoorAttaches[net] = new DoorAttachment { Path = path, Point = point, Position = position, Rotation = rotation, BoundPoint = boundPoint };
        }

        private static void RestoreStoredDoorAttaches()
        {
            _restoredDoorAttaches.Clear();
            foreach (var kv in _storedDoorAttaches)
            {
                if (!ByNet.TryGetValue(kv.Key, out var e)) { _restoredDoorAttaches.Add(kv.Key); continue; }
                if (HandManaged(e)) continue;   // LateSample runs after final hand placement; retry after its release glide
                var at = e.Root != null ? e.Root.attachable : null;
                if (at == null) continue;
                if (!at.attached) { _restoredDoorAttaches.Add(kv.Key); continue; }
                if (at.point == null || at.point == kv.Value.BoundPoint) continue;
                var s = kv.Value;
                if (!RestoreAttachFrame(at, s.Path, s.Point)) continue;
                ApplyAttachPose(e, at, s.Position, s.Rotation);
                s.BoundPoint = at.point;
            }
            foreach (uint net in _restoredDoorAttaches) _storedDoorAttaches.Remove(net);
        }

        public static string GlueToDoor(uint itemNet, uint carNet, int item, int usable)
        {
            if (!ByNet.TryGetValue(itemNet, out var e) || !Resolve(e) || e.Proxy || e.Root == null || e.Root.attachable == null || e.Root.attachable.attached)
                return "{\"error\":\"an owned loose attachable is required\"}";
            if (!UsableArgs(carNet, item, usable, out _, out var u)) return "{\"error\":\"no usable\"}";
            var frame = u.TDoor != null ? u.TDoor : u.RT1;
            if (frame == null) return "{\"error\":\"no moving door frame\"}";
            var col = frame.GetComponentInChildren<Collider>();
            if (col == null) return "{\"error\":\"no door collider\"}";
            var at = e.Root.attachable;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl != null && pl.pickedUp == e.Root.P) pl.Drop();
            var position = col.bounds.center;
            e.Root.transform.SetPositionAndRotation(position, frame.rotation);
            at.Attach(at.NewPoint(col.transform, position, Quaternion.identity), col);
            return DoorAttachmentPose(itemNet);
        }

        public static string DoorAttachmentPose(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"entity is not loaded\"}";
            var at = e.Root.attachable;
            if (at == null || !at.attached || at.point == null) return "{\"attached\":false}";
            var f = AttachFrame(at); var t = at.transform; var iq = Quaternion.Inverse(f.rotation);
            var q = iq * t.rotation; var g = mainscript.GlobalFromUnityPos(t.position);
            return "{\"attached\":true,\"parented\":" + (RidesParent(e) ? "true" : "false") + ",\"frame\":" + Json.Str(f.name) +
                ",\"local\":" + V3f(iq * (t.position - f.position)) + ",\"rotation\":[" + UN(q.x) + "," + UN(q.y) + "," + UN(q.z) + "," + UN(q.w) +
                "],\"pos\":[" + UN(g.x) + "," + UN(g.y) + "," + UN(g.z) + "]}";
        }
    }
}
