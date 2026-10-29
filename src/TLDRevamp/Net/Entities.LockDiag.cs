using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static readonly System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;

        /// Bug report (lock.json): why the item under the reporter's crosshair (else the one in the hand) is not held by a
        /// physics-lock container ("some items never physlock until the save is loaded again"). Null when there is none.
        /// Main thread.
        internal static string LockDiagnostics()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return null;
            tosaveitemscript it = null;
            var camera = pl.Cam != null ? pl.Cam : pl.mainCam;
            if (camera != null && Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 8f, ~0, QueryTriggerInteraction.Ignore))
                it = hit.collider.GetComponentInParent<tosaveitemscript>();
            if (it == null && pl.inHandP != null) it = pl.inHandP.tosave != null ? pl.inHandP.tosave : pl.inHandP.GetComponentInParent<tosaveitemscript>();
            if (it == null && pl.pickedUp != null) it = pl.pickedUp.tosave != null ? pl.pickedUp.tosave : pl.pickedUp.GetComponentInParent<tosaveitemscript>();
            return it == null || it.P == null ? null : WhyNotLock(it);
        }

        /// Why the game's physics lock does or doesn't store this item: for every storage area within 4 m, whether its last two
        /// scans saw the item (both are needed), the game's CanStore and whether it started; the item's own conditions from
        /// mountStuff.FreezeUpd; and what this mod adds (who owns it, whether it is a copy, the attach component, the links
        /// from its colliders back to the item that the scan reads). Also the bridge's `mount whynot <net>`.
        internal static string WhyNotLock(tosaveitemscript it)
        {
                var p = it.P; var rows = new List<string>();
                foreach (var m in Object.FindObjectsOfType<mountStuff>())
                {
                    if (m == null || m.col == null || (m.col.transform.position - it.transform.position).sqrMagnitude > 16f) continue;
                    bool In(Collider[] cs) { if (cs != null) foreach (var c in cs) if (c != null && (c.transform.root == it.transform.root)) return true; return false; }
                    var lp = m.col.transform.InverseTransformPoint(it.transform.position) - m.col.center;
                    bool inBox = Mathf.Abs(lp.x) <= m.col.size.x * 0.5f && Mathf.Abs(lp.y) <= m.col.size.y * 0.5f && Mathf.Abs(lp.z) <= m.col.size.z * 0.5f;
                    bool started = HarmonyLib.Traverse.Create(m).Field("started").GetValue<bool>();
                    rows.Add("{\"name\":" + Json.Str(m.name + " of " + m.transform.root.name) + ",\"type\":" + Json.Str(m.sType.ToString()) + ",\"centreInBox\":" + (inBox ? "true" : "false") +
                             ",\"inScan\":" + (In(m.cols) ? "true" : "false") + ",\"inLastScan\":" + (In(m.lastcols) ? "true" : "false") + ",\"canStore\":" + (m.CanStore() ? "true" : "false") +
                             ",\"started\":" + (started ? "true" : "false") + ",\"manual\":" + (m.onlyManual ? "true" : "false") + ",\"noLock\":" + (m.noPhysicsLock ? "true" : "false") +
                             ",\"proxyCar\":" + (IsProxy(m) ? "true" : "false") + "}");
                }
                var lm = mainscript.s.mountLayerMask; var layers = new List<string>();
                foreach (var c in it.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) layers.Add(Json.Str(c.name + ":" + LayerMask.LayerToName(c.gameObject.layer) + ((lm & (1 << c.gameObject.layer)) != 0 ? "" : " (not in the mount mask)")));
                var tsv = it;
                return "{\"item\":" + Json.Str(it.name) + ",\"physlock\":" + Json.Str(p.physlock != null ? p.physlock.name : "") + ",\"dontMount\":" + (p.dontMount ? "true" : "false") +
                       ",\"pickedUp\":" + (p.pickedUp ? "true" : "false") + ",\"dontMountTillPickup\":" + (HarmonyLib.Traverse.Create(p).Field("dontMountTillPickup").GetValue<bool>() ? "true" : "false") + ",\"sinceDropped\":" + (Time.time - p.DroppedTime).ToString("F1", ic) +
                       ",\"mpConnected\":" + (syncScript.IsConnected() ? "true" : "false") + ",\"mpFlags\":" + Json.Str((tsv.mppickedup ? "pickedup " : "") + (tsv.mpequipped ? "equipped " : "") + (tsv.mpstored ? "stored " : "") + (tsv.mpweared ? "weared" : "")) +
                       ",\"setting\":" + settingsscript.s.S.IPhysicsLock + ",\"colliders\":[" + string.Join(",", layers) + "],\"areas\":[" + string.Join(",", rows) + "]" + LockFacts(it) + "}";
        }

        private static string LockFacts(tosaveitemscript it)
        {
            string B(bool v) => v ? "true" : "false";
            uint net = TelemetryNet(it);
            var p = it.P; var at = it.attachable;
            int links = 0, unlinked = 0;
            if (p != null && p.cols != null) foreach (var l in p.cols) { if (l == null) continue; links++; if (l.p == null) unlinked++; }
            string owner = "null";
            if (net != 0 && ByNet.TryGetValue(net, out var e)) owner = "{\"net\":" + net + ",\"owner\":" + e.OwnerId + ",\"mine\":" + B(e.OwnerId == MyId) + ",\"proxy\":" + B(e.Proxy) + ",\"held\":" + B(e.Held) + "}";
            return ",\"mod\":{\"entity\":" + owner + ",\"attachable\":" + (at == null ? "null" : "{\"attached\":" + B(at.attached) + ",\"crafted\":" + B(p != null && p.crafted) + ",\"slot\":" + B(at.slot != null) + "}") +
                   ",\"started\":" + B(it.started) + ",\"pickupStarted\":" + B(p != null && HarmonyLib.Traverse.Create(p).Field("started").GetValue<bool>()) + ",\"colliderLinks\":" + links + ",\"unlinked\":" + unlinked +
                   ",\"storesRefused\":" + ContainerStoresRefused + ",\"attachedStoresRefused\":" + AttachedStoresRefused + ",\"insideBox\":" + (it.transform.parent != null ? Json.Str(it.transform.parent.name) : "null") + "}";
        }
    }
}
