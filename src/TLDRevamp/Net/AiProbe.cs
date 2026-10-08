using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Test readout (bridge `mp ai`): every AI character (newAiScript — the mansion's workers) on this machine — shared
    /// or not, owner or copy, where, kinematic (a copy doesn't think: Update returns on rb.isKinematic), what it's doing
    /// and whom it hunts, the animation input it gets.
    public static class AiProbe
    {
        /// Why does (or doesn't) the AI nearest the local player see it? The game's own vision test, step by step
        /// (newAiScript.Update): angle between the head's forward and the player vs fov, then RaycastAll(head → player,
        /// range, lm) sorted — the first hit decides (a player = seen; anything but layer 23 glass blocks).
        public static string Vision()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null) return "{\"error\":\"not in game\"}";
            newAiScript best = null; float bd = float.MaxValue;
            foreach (var a in Object.FindObjectsOfType<newAiScript>())
            {
                float d = (a.transform.position - p.transform.position).magnitude;
                if (d < bd) { bd = d; best = a; }
            }
            if (best == null || best.head == null) return "{\"error\":\"no AI\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var v = p.transform.position - best.head.position;
            float ang = Vector3.Angle(best.head.forward, v);
            var hits = Physics.RaycastAll(best.head.position, v, best.range, best.lm);
            System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            var names = new List<string>();
            foreach (var h in hits) if (names.Count < 6) names.Add(h.collider.transform.root.name + "/" + h.collider.name + " L" + h.collider.gameObject.layer + " @" + h.distance.ToString("F2", ci));
            return "{\"ai\":" + Json.Str(best.name) + ",\"dist\":" + v.magnitude.ToString("F2", ci) + ",\"angle\":" + ang.ToString("F0", ci) +
                   ",\"fov\":" + best.fov.ToString("F0", ci) + ",\"headY\":" + best.head.position.y.ToString("F2", ci) + ",\"playerY\":" + p.transform.position.y.ToString("F2", ci) +
                   ",\"sleeping\":" + (best.sleeping ? "true" : "false") + ",\"peaceful\":" + gamemodesettingsscript.s.S.peacefulEnemies +
                   ",\"hits\":[" + string.Join(",", names.ConvertAll(Json.Str)) + "]}";
        }

        /// Its breakables' health (shoot health where the game counts shots separately), summed — the copy's never
        /// changes (hits go to the owner), the owner's goes down with every hit.
        private static string Health(newAiScript a)
        {
            float h = 0f; int n = 0; string rule = "";
            foreach (var br in a.transform.root.GetComponentsInChildren<breakablescript>(true))
            {
                h += br.shootDifferent ? br.shootHealth : br.health; n++;
                rule = (br.shootDifferent ? "shootDiff " : "") + (br.decreaseAfterAttack ? "decreases" : "one-hit") + (br.noShoot ? " noShoot" : "");
            }
            return "[" + n + "," + h.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," + Json.Str(rule) + "]";
        }

        /// The item database's creatures (bridge `mp aiids`): index (for devspawn) and name.
        /// Where each creature's parts are (bridge `mp aihead`): its root (the script's transform: vanilla's sight aims from it),
        /// its body (the rigidbody it moves by), its head (the eye the sight ray starts from) — names, paths, offsets.
        public static string Heads()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture; var rows = new List<string>();
            foreach (var a in Object.FindObjectsOfType<newAiScript>())
            {
                if (a == null) continue;
                string Path(Transform t) { var s = ""; for (var x = t; x != null; x = x.parent) s = x.name + (s.Length > 0 ? "/" + s : ""); return s; }
                var root = a.transform.position; var rbp = a.rb != null ? a.rb.position : root; var hp = a.head != null ? a.head.position : root;
                var hf = hp - root; hf.y = 0f; var bf = rbp - root; bf.y = 0f;
                // the bone the offset starts at: the topmost of the head's ancestors more than 2 m from the root
                string far = "";
                if (a.head != null)
                    for (var x = a.head; x != null && x != a.transform; x = x.parent)
                        if ((x.position - root).magnitude > 2f)
                            far = x.name + " world " + (x.position - root).magnitude.ToString("F1", ci) + " m off, local " + x.localPosition.ToString("F2") +
                                  (x.parent != null ? " (parent " + x.parent.name + " " + (x.parent.position - root).magnitude.ToString("F1", ci) + " m off)" : "");
                rows.Add("{\"ai\":" + Json.Str(Entities.DescribeRoot(a.transform.root)) + ",\"script\":" + Json.Str(Path(a.transform)) +
                         ",\"rbOn\":" + Json.Str(a.rb != null ? Path(a.rb.transform) : "") + ",\"headIs\":" + Json.Str(a.head != null ? Path(a.head) : "") +
                         ",\"bodyOff\":" + bf.magnitude.ToString("F2", ci) + ",\"headOff\":" + hf.magnitude.ToString("F2", ci) + ",\"headUp\":" + (hp.y - root.y).ToString("F2", ci) +
                         ",\"farBone\":" + Json.Str(far) + ",\"bodies\":" + Json.Str(Bodies(a)) +
                         ",\"kin\":" + (a.rb != null && a.rb.isKinematic ? "true" : "false") + ",\"died\":" + (a.died ? "true" : "false") + "}");
            }
            return "{\"ai\":[" + string.Join(",", rows) + "]}";
        }

        private static string Bodies(newAiScript a)
        {
            var parts = new List<string>();
            foreach (var rb in a.transform.root.GetComponentsInChildren<Rigidbody>(true))
                parts.Add(rb.name + (rb.isKinematic ? " kin" : " DYN") + (rb.interpolation != RigidbodyInterpolation.None ? " interp" : "") + (rb.detectCollisions ? "" : " nocol") +
                          (rb.gameObject.activeInHierarchy ? "" : " off") + " " + (rb.position - a.transform.position).magnitude.ToString("F1") + "m");
            return string.Join("; ", parts);
        }

        public static string Ids()
        {
            var rows = new List<string>();
            var items = itemdatabase.s != null ? itemdatabase.s.items : null;
            if (items != null)
                for (int i = 0; i < items.Length; i++)
                    if (items[i] != null && items[i].GetComponentInChildren<newAiScript>(true) != null) rows.Add("[" + i + "," + Json.Str(items[i].name) + "]");
            return "{\"ai\":[" + string.Join(",", rows) + "]}";
        }

        /// A creature's colliders (bridge `mp aicols <net>`): what a shot can meet on it.
        public static string Cols(string net)
        {
            // by net id, else the one nearest the player
            newAiScript pick = null; float best = float.MaxValue;
            var me = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
            foreach (var x in Object.FindObjectsOfType<newAiScript>())
            {
                if (x == null) continue;
                if (net != "") { if (Entities.DescribeRoot(x.transform.root).StartsWith("net " + net + " ")) { pick = x; break; } continue; }
                float d = (x.transform.position - me).sqrMagnitude;
                if (d < best) { best = d; pick = x; }
            }
            foreach (var a in new[] { pick })
            {
                if (a == null) continue;
                var rows = new List<string>();
                var rd = a.GetComponentInChildren<ragdollactivatescript>(true);
                foreach (var c in a.transform.root.GetComponentsInChildren<Collider>(true))
                {
                    string kind = rd != null && System.Array.IndexOf(rd.animColliders, c) >= 0 ? "anim" : rd != null && System.Array.IndexOf(rd.ragdollColliders, c) >= 0 ? "ragdoll" : "other";
                    var gp = mainscript.GlobalFromUnityPos(c.bounds.center);
                    rows.Add(Json.Str(c.name + " " + kind + " L" + c.gameObject.layer + (c.enabled && c.gameObject.activeInHierarchy ? "" : " OFF") + (c.isTrigger ? " trig" : "")
                                      + " at " + gp.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," + gp.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," + gp.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
                }
                return "{\"who\":" + Json.Str(Entities.DescribeRoot(a.transform.root)) + ",\"cols\":[" + string.Join(",", rows) + "]}";
            }
            return "{\"error\":\"no creature\"}";
        }

        private static Vector3 Chest(newAiScript a)
        {
            var rd = a.GetComponentInChildren<ragdollactivatescript>(true);
            if (rd != null && rd.ragdollColliders != null)
            {
                foreach (var c in rd.ragdollColliders) if (c != null && c.name == "spine02") return c.bounds.center;
                foreach (var c in rd.ragdollColliders) if (c != null && c.name.StartsWith("spine")) return c.bounds.center;
            }
            return a.head != null ? a.head.position - Vector3.up * 0.4f : a.transform.position;
        }

        public static string List()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var a in Object.FindObjectsOfType<newAiScript>())
            {
                var g = mainscript.GlobalFromUnityPos(a.transform.position);
                // where a shot hits it: the body collider's centre (the root is at the hips — root + 1.2 m is over its head)
                // shots hurt it only through its Flesh hitboxes (weaponscript.Shot: the body capsule passes them through); aim
                // at the chest's (spine02) — the capsule's centre is at the hips, between the hitboxes
                var body = Chest(a);
                var ag = mainscript.GlobalFromUnityPos(body);
                string tgt = a.target != null && a.target.parent != null ? a.target.parent.root.name + "/" + a.target.parent.name : "";
                rows.Add("{\"name\":" + Json.Str(a.name) + ",\"who\":" + Json.Str(Entities.DescribeRoot(a.transform.root)) +
                         ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "]" +
                         ",\"aim\":[" + ag.x.ToString("F2", ci) + "," + ag.y.ToString("F2", ci) + "," + ag.z.ToString("F2", ci) + "]" +
                         ",\"kin\":" + (a.rb == null ? "null" : a.rb.isKinematic ? "true" : "false") +
                         ",\"died\":" + (a.died ? "true" : "false") + ",\"sleeping\":" + (a.sleeping ? "true" : "false") +
                         ",\"chasing\":" + (a.isChasing ? "true" : "false") + ",\"seeing\":" + (a.isSeeing ? "true" : "false") +
                         ",\"attacking\":" + (a.isAttacking ? "true" : "false") + ",\"target\":" + Json.Str(tgt) +
                         ",\"fwd\":" + (a.anim != null ? a.anim.targetForward.ToString("F2", ci) : "0") +
                         ",\"speed\":" + (a.rb != null ? a.rb.velocity.magnitude.ToString("F2", ci) : "0") +
                         ",\"look\":[" + (a.head != null ? a.head.forward.x.ToString("F2", ci) + "," + a.head.forward.z.ToString("F2", ci) : "0,0") + "]" +
                         ",\"fov\":" + a.fov.ToString("F0", ci) + ",\"range\":" + a.range.ToString("F0", ci) + ",\"health\":" + Health(a) + "}");
            }
            return "{\"ai\":[" + string.Join(",", rows) + "]}";
        }
    }
}
