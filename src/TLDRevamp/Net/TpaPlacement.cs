using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class TpaPlacement
    {
        internal static bool HigherFloor(Vector3d target, Vector3 body)
        {
            var u = mainscript.UnityPosFromGlobal(target);
            Transform playerRoot = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            for (int ring = 0; ring <= 3; ring++)
                for (int i = 0; i < (ring == 0 ? 1 : 8); i++)
                {
                    float angle = i * Mathf.PI / 4f;
                    var hit = StandHit(u.x + ring * Mathf.Cos(angle), u.z + ring * Mathf.Sin(angle), u.y - .4f, true, false);
                    if (hit.HasValue && TpaArrival.Floor(hit.Value.point.y, u.y) && Room(hit.Value.point, playerRoot))
                        return TpaArrival.Improves(hit.Value.point.y, body.y, u.y);
                }
            return false;
        }

        /// The surface to stand on at (x, z) — see above. Null while nothing is generated there.
        internal static RaycastHit? StandHit(float x, float z, float? belowY, bool vehicles = false, bool requireRoom = true)
        {
            Transform playerRoot = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.root : null;
            var hits = Physics.RaycastAll(new Vector3(x, 5000f, z), Vector3.down, 10000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var list = new System.Collections.Generic.List<RaycastHit>();
            double terrainY = double.NegativeInfinity;
            foreach (var h in hits)
            {
                if (Carried(h.collider.transform.root, playerRoot)) continue;   // the player, held items
                var rb = h.collider.attachedRigidbody;
                var root = h.collider.transform.root;
                var item = root.GetComponent<tosaveitemscript>();
                bool vehicle = item != null && (item.car != null || (item.attachable != null && item.attachable.utanfuto != null));
                bool atRest = Entities.TpaFloorReady(item);
                atRest &= rb == null || (rb.velocity.sqrMagnitude < .04f && rb.angularVelocity.sqrMagnitude < .04f);
                if (!TpaArrival.Surface(rb != null, vehicle, atRest, Tpa.MovingRoot(root), vehicles)) continue;
                TpaArrival.TerrainHeight(h.collider.GetType().Name == "TerrainCollider", h.point.y, ref terrainY);
                if (h.normal.y < .65f) continue;
                list.Add(h);
            }
            if (list.Count == 0) return null;
            if (belowY.HasValue)
            {
                list.Sort((a, b) => b.point.y.CompareTo(a.point.y));   // highest first: the nearest below the height
                foreach (var h in list) if (h.point.y <= belowY.Value && (!requireRoom || Room(h.point, playerRoot))) return h;
                return null;
            }
            list.Sort((a, b) => a.point.y.CompareTo(b.point.y));       // lowest first
            foreach (var h in list) if (TpaArrival.AboveTerrain(h.point.y, terrainY) && (!requireRoom || Room(h.point, playerRoot))) return h;
            return null;
        }

        internal static RaycastHit? Landing(Vector3 body, Transform playerRoot, out bool room)
        {
            var floor = StandHit(body.x, body.z, body.y - .4f, true, false);
            // A settled body is about .9 m above its floor, below the initial 1.1 m teleport offset.
            // Anchoring to that offset made the probe overlap the supporting floor itself.
            room = floor.HasValue && Room(floor.Value.point, playerRoot);
            return floor;
        }

        private static readonly Collider[] RoomBuf = new Collider[16];
        internal static bool Room(Vector3 p, Transform playerRoot)
        {
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.45f, p + Vector3.up * 1.7f, 0.3f, RoomBuf, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = RoomBuf[i];
                if (Carried(c.transform.root, playerRoot)) continue;
                return false;
            }
            return n < RoomBuf.Length;
        }

        /// The player's own body and what is in their hands: a held item is not under the player's root. Counted as
        /// something in the way, a joiner who came back with a lamp in the hand (PlayerSave) found no room where they
        /// stood, and the join screen placed them again 1 m over, and again, every 0.2 s: they slid away across the map
        /// (tools/rejoin.py 2026-10-22: 43, 122 and 211 m from where they left).
        private static bool Carried(Transform root, Transform playerRoot)
        {
            if (root == null) return false;
            if (playerRoot != null && root == playerRoot) return true;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return false;
            return (pl.pickedUp != null && pl.pickedUp.transform.root == root) || (pl.inHandP != null && pl.inHandP.transform.root == root);
        }
    }
}
