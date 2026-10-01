using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// An item's network snapshot = its save record (docs/MULTIPLAYER-ARCHITECTURE.md §4b): savedatascript.SaveToDictionary
    /// into a fresh itemDataClass, sent as JSON (JsonUtility: data only — never BinaryFormatter, which the save files use,
    /// because deserialising it from another player's bytes can run their code). Spawned on the other side through the
    /// game's own path (SpawnItem + InitAfterSpawn + LoadStuff), the same one that places saved items near the player.
    ///
    /// Bridge `itemsnap test [n]`: round trip of the n items nearest to the player (capture → JSON → spawn a copy with a
    /// new id 3 m away → capture the copy → compare the records with id/position aligned). Differences mean state the
    /// save record doesn't carry, or JSON loses.
    public static class ItemSnapshot
    {
        public static itemDataClass Capture(tosaveitemscript item)
        {
            var d = new itemDataClass();
            savedatascript.SaveToDictionary(item, d);
            return d;
        }

        public static string ToJson(itemDataClass d) => JsonUtility.ToJson(d);
        public static itemDataClass FromJson(string s) => JsonUtility.FromJson<itemDataClass>(s);

        internal static readonly FieldInfo[] ListFields = typeof(itemDataClass).GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// Every record in the snapshot that belongs to `from` gets id `to` (records of other items are left alone),
        /// including id-carrying objects nested inside records (e.g. a part condition's colour).
        public static void Rekey(itemDataClass d, uint from, uint to)
        {
            foreach (var f in ListFields)
            {
                if (!(f.GetValue(d) is IList list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e == null) continue;
                    var boxed = RekeyObj(e, from, to, 0);
                    if (e.GetType().IsValueType) list[i] = boxed;
                }
            }
        }

        private static object RekeyObj(object o, uint from, uint to, int depth)
        {
            if (o is iid a && a.id == from) { a.id = to; o = a; }
            else if (o is iid2 b && b.id == from) { b.id = to; o = b; }
            if (depth > 3) return o;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var v = f.GetValue(o);
                if (v == null || v is string || f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                if (v is iid || v is iid2)
                {
                    var nv = RekeyObj(v, from, to, depth + 1);
                    if (f.FieldType.IsValueType) f.SetValue(o, nv);
                }
                else if (v is IList inner)
                    for (int i = 0; i < inner.Count; i++)
                    {
                        var e = inner[i];
                        if (e == null || e is string || e.GetType().IsPrimitive) continue;
                        var ne = RekeyObj(e, from, to, depth + 1);
                        if (e.GetType().IsValueType) inner[i] = ne;
                    }
            }
            return o;
        }

        public static void Move(itemDataClass d, Vector3d delta)
        {
            foreach (var it in d.items) it.transform = new posRotd(it.transform.pos + delta, it.transform.rot.Load());
        }

        public static tosaveitemscript Spawn(itemDataClass d, uint id)
        {
            var sd = new savedata { itemData = d };
            var go = savedatascript.s.SpawnItem(d.items[0]);
            if (go == null) return null;
            savedatascript.s.InitAfterSpawn(go, id);
            savedatascript.s.LoadStuff(sd, id, true);
            return savedatascript.s.items.TryGetValue(id, out var t) ? t : null;
        }

        // ------------------------------------------------------------------ groups (a car + its parts + locked items)

        /// The item, everything attached/parented under it, the parts in its part slots, and items locked in it
        /// (physLocks), closed over those links.
        /// Part slots count on their own: a slot can sit on a body outside the item's hierarchy. Bus01's rear section
        /// (BusBack: its own Rigidbody, a ConfigurableJoint to BusFront, no tosaveitemscript) carries the rear wheel slots,
        /// listed in BusFront's partslotscripts — the wheels there are in no transform under BusFront. Before v0.64.3 a
        /// shared bus went out with 6 of its 8 wheel sets (busprobe --mp: host record 24 items, spawner 28; groupdiag).
        public static List<tosaveitemscript> Group(tosaveitemscript root)
        {
            var list = new List<tosaveitemscript>();
            var seen = new HashSet<uint>();
            var queue = new Queue<tosaveitemscript>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var it = queue.Dequeue();
                if (it == null || !seen.Add(it.idInSave)) continue;
                list.Add(it);
                foreach (var ch in it.GetComponentsInChildren<tosaveitemscript>(true)) if (!seen.Contains(ch.idInSave)) queue.Enqueue(ch);
                if (it.partslotscripts != null)
                    foreach (var slot in it.partslotscripts)
                        if (slot != null && slot.parts != null)
                            foreach (var part in slot.parts)
                                if (part != null && part.tosave != null && !seen.Contains(part.tosave.idInSave)) queue.Enqueue(part.tosave);
                var d = Capture(it);
                foreach (var pl in d.physLocks)
                    if (pl.IDs != null)
                        foreach (var lid in pl.IDs)
                            if (savedatascript.s.items.TryGetValue(lid, out var locked) && !seen.Contains(lid)) queue.Enqueue(locked);
            }
            return list;
        }

        public static itemDataClass CaptureGroup(List<tosaveitemscript> items)
        {
            var d = new itemDataClass();
            foreach (var it in items) savedatascript.SaveToDictionary(it, d);
            return d;
        }

        /// CaptureGroup, keeping each item's own record too (same lists, same order: SaveToDictionary only appends).
        public static itemDataClass CaptureGroup(List<tosaveitemscript> items, List<itemDataClass> perItem)
        {
            var d = new itemDataClass();
            foreach (var it in items)
            {
                var di = it != null ? Capture(it) : null;
                perItem.Add(di);
                if (di == null) continue;
                foreach (var f in ListFields)
                    if (f.GetValue(di) is IList src && f.GetValue(d) is IList dst)
                        foreach (var x in src) dst.Add(x);
            }
            return d;
        }

        /// Every id-valued field (uint, or uint in a list) found in `map` is replaced — the records' own ids and their
        /// references to each other (attachable parentid, physLock IDs, …) move together.
        public static void Remap(itemDataClass d, Dictionary<uint, uint> map)
        {
            foreach (var f in ListFields)
                if (f.GetValue(d) is IList list)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null) { var o = RemapObj(list[i], map, 0); if (list[i].GetType().IsValueType) list[i] = o; }
        }

        private static object RemapObj(object o, Dictionary<uint, uint> map, int depth)
        {
            if (depth > 4) return o;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.IsNotSerialized) continue;
                var v = f.GetValue(o);
                if (v == null) continue;
                if (f.FieldType == typeof(uint)) { if (map.TryGetValue((uint)v, out var nv)) f.SetValue(o, nv); }
                else if (v is List<uint> lu) { for (int i = 0; i < lu.Count; i++) if (map.TryGetValue(lu[i], out var nv)) lu[i] = nv; }
                else if (v is uint[] au) { for (int i = 0; i < au.Length; i++) if (map.TryGetValue(au[i], out var nv)) au[i] = nv; }
                else if (v is IList inner) { for (int i = 0; i < inner.Count; i++) if (inner[i] != null && !(inner[i] is string) && !inner[i].GetType().IsPrimitive) { var ne = RemapObj(inner[i], map, depth + 1); if (inner[i].GetType().IsValueType) inner[i] = ne; } }
                else if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !(v is string) && (v is iid || v is iid2 || f.FieldType.IsDefined(typeof(System.SerializableAttribute), false)))
                { var nv2 = RemapObj(v, map, depth + 1); if (f.FieldType.IsValueType) f.SetValue(o, nv2); }
            }
            return o;
        }

        public static long ForeignRefsDropped;

        /// A record set's references to items OUTSIDE it (an attachment's parent, a container's contents) are the
        /// sender's ids: here they would name whatever local item has that number — a box pulled a car's wheels into
        /// itself (farworld run 15). Before the remap, such references are dropped: the item loads loose / the box
        /// without that content, never on or in a stranger. Counted and logged (first 30).
        public static int DropForeignRefs(itemDataClass d, Dictionary<uint, uint> group, string what)
        {
            int n = 0;
            if (d.attachable != null)
                n += d.attachable.RemoveAll(a =>
                {
                    if (a == null || a.attachType != (int)save_attachable.attachTypeEmu.toItem || group.ContainsKey(a.parentid)) return false;
                    if (ForeignRefsDropped + 1 <= 30) Plugin.Log.LogWarning($"{what}: attachment #{a.id} → #{a.parentid} outside its group dropped");
                    ForeignRefsDropped++;
                    return true;
                });
            if (d.physLocks != null)
                foreach (var pl in d.physLocks)
                {
                    if (pl == null || pl.IDs == null) continue;
                    var keep = new List<int>();
                    for (int i = 0; i < pl.IDs.Length; i++) if (group.ContainsKey(pl.IDs[i])) keep.Add(i);
                    if (keep.Count == pl.IDs.Length) continue;
                    int lost = pl.IDs.Length - keep.Count;
                    if (ForeignRefsDropped + 1 <= 30) Plugin.Log.LogWarning($"{what}: container #{pl.id} lock {pl.ind}: {lost} of {pl.IDs.Length} contents outside its group dropped");
                    ForeignRefsDropped += lost; n += lost;
                    var ids = new uint[keep.Count]; var ps = pl.poss != null ? new vector3[keep.Count] : null; var rs = pl.rots != null ? new vector3[keep.Count] : null;
                    for (int j = 0; j < keep.Count; j++)
                    {
                        ids[j] = pl.IDs[keep[j]];
                        if (ps != null && keep[j] < pl.poss.Length) ps[j] = pl.poss[keep[j]];
                        if (rs != null && keep[j] < pl.rots.Length) rs[j] = pl.rots[keep[j]];
                    }
                    pl.IDs = ids; pl.poss = ps; pl.rots = rs;
                }
            return n;
        }

        /// Spawn a group through the game's own path: all items first, then all state (so parts find their parents).
        /// Returns old id → new local id.
        public static Dictionary<uint, uint> SpawnGroup(itemDataClass d)
        {
            var map = new Dictionary<uint, uint>();
            foreach (var it in d.items) map[it.id] = savedatascript.s.GetNewKey();
            DropForeignRefs(d, map, "spawn group");
            Remap(d, map);
            var sd = new savedata { itemData = d };
            var records = new List<save_item>(d.items); // LoadStuff edits the lists it reads
            foreach (var it in records)
            {
                var go = savedatascript.s.SpawnItem(it);
                if (go != null) savedatascript.s.InitAfterSpawn(go, it.id);
            }
            foreach (var it in records)
                if (savedatascript.s.items.ContainsKey(it.id)) savedatascript.s.LoadStuff(sd, it.id, true);
            return map;
        }

        /// Round trip of the group of the nearest car (bridge `itemsnap group`).
        /// Bridge `itemsnap groupdiag [r]`: the nearest car's group, and every item within r m that isn't in it — with
        /// how it's held (transform parent, attachable.attached/point/slot, the point's tosave parent, joint body).
        public static string GroupDiag(float r)
        {
            if (savedatascript.s == null || mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
            var p = mainscript.s.player.transform.position;
            tosaveitemscript car = null; float best = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null) { float dd = (it.transform.position - p).sqrMagnitude; if (dd < best) { best = dd; car = it; } }
            if (car == null) return "{\"error\":\"no car\"}";
            var group = Group(car);
            var inG = new HashSet<tosaveitemscript>(group);
            var outRows = new List<string>();
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || inG.Contains(it) || (it.transform.position - car.transform.position).magnitude > r) continue;
                var a = it.attachable;
                string Path(Transform t) { var parts = new List<string>(); for (int n = 0; t != null && n < 6; n++, t = t.parent) parts.Add(t.name); return string.Join("<", parts); }
                var pt = a != null ? a.point : null;
                var ptHost = pt != null ? pt.GetComponentInParent<tosaveitemscript>() : null;
                var j = it.GetComponent<Joint>();
                outRows.Add("{\"name\":" + Json.Str(it.name) + ",\"id\":" + it.idInSave + ",\"parent\":" + Json.Str(Path(it.transform.parent)) +
                            ",\"attachable\":" + (a != null ? "true" : "false") + ",\"attached\":" + (a != null && a.attached ? "true" : "false") +
                            ",\"point\":" + Json.Str(pt != null ? Path(pt) : "") + ",\"pointHost\":" + Json.Str(ptHost != null ? ptHost.name + "#" + ptHost.idInSave : "") +
                            ",\"slot\":" + Json.Str(a != null && a.slot != null ? Path(a.slot.transform) : "") +
                            ",\"lastRoot\":" + Json.Str(a != null && a.lastRoot != null ? a.lastRoot.name : "") +
                            ",\"joint\":" + Json.Str(j != null && j.connectedBody != null ? j.connectedBody.name : "") +
                            ",\"active\":" + (it.gameObject.activeInHierarchy ? "true" : "false") + "}");
            }
            // the roots the outside items hang under: their components, joints, and which fields of the car point at them
            var foreignRoots = new HashSet<Transform>();
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && !inG.Contains(it) && (it.transform.position - car.transform.position).magnitude <= r) foreignRoots.Add(it.transform.root);
            var rootRows = new List<string>();
            foreach (var fr in foreignRoots)
            {
                var comps = new List<string>(); foreach (var c in fr.GetComponents<Component>()) if (c != null) comps.Add(c.GetType().Name);
                var joints = new List<string>(); foreach (var jj in fr.GetComponentsInChildren<Joint>(true)) joints.Add(jj.name + "->" + (jj.connectedBody != null ? jj.connectedBody.name : "null"));
                var refs = new List<string>();
                foreach (var mb in car.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    foreach (var f in mb.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        object v; try { v = f.GetValue(mb); } catch { continue; }
                        Transform tt = v is Component cc && cc != null ? cc.transform : v is GameObject go && go != null ? go.transform : null;
                        if (tt != null && tt.root == fr && tt.root != car.transform.root) refs.Add(mb.GetType().Name + "(" + mb.name + ")." + f.Name + "=" + tt.name);
                        if (refs.Count > 40) break;
                    }
                }
                rootRows.Add("{\"root\":" + Json.Str(fr.name) + ",\"comps\":" + Json.Str(string.Join(" ", comps)) + ",\"joints\":" + Json.Str(string.Join(" ", joints)) +
                             ",\"refsFromCar\":" + Json.Str(string.Join(" | ", refs)) + "}");
            }
            var names = new List<string>(); foreach (var g in group) names.Add(g.name + "#" + g.idInSave);
            return "{\"car\":" + Json.Str(car.name + "#" + car.idInSave) + ",\"group\":" + group.Count + ",\"members\":" + Json.Str(string.Join(" ", names)) +
                   ",\"outside\":[" + string.Join(",", outRows) + "],\"roots\":[" + string.Join(",", rootRows) + "]}";
        }

        public static string TestGroup()
        {
            if (savedatascript.s == null || mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
            var p = mainscript.s.player.transform.position;
            tosaveitemscript car = null; float best = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null) { float dd = (it.transform.position - p).sqrMagnitude; if (dd < best) { best = dd; car = it; } }
            if (car == null) return "{\"error\":\"no car\"}";
            var group = Group(car);
            byte[] b0 = RecordCodec.Encode(CaptureGroup(group));
            var d = RecordCodec.Decode(b0);
            Move(d, new Vector3d(0, 0, 6));
            var map = SpawnGroup(d);
            var back = new Dictionary<uint, uint>();
            foreach (var kv in map) back[kv.Value] = kv.Key;
            var copies = new List<tosaveitemscript>();
            foreach (var kv in map) if (savedatascript.s.items.TryGetValue(kv.Value, out var c)) copies.Add(c);
            // compare right away, records normalised: ids mapped back, positions zeroed, lists sorted by id
            var dc = CaptureGroup(copies); Remap(dc, back);
            var d0 = RecordCodec.Decode(b0);
            string a = Normalised(d0), bb = Normalised(dc);
            int k = 0; while (k < a.Length && k < bb.Length && a[k] == bb[k]) k++;
            string res = "{\"car\":" + Json.Str(car.name) + ",\"items\":" + group.Count + ",\"spawned\":" + copies.Count + ",\"bytes\":" + b0.Length +
                         ",\"identical\":" + (a == bb ? "true" : "false") +
                         (a == bb ? "" : ",\"diffAt\":" + k + ",\"orig\":" + Json.Str(a.Substring(Math.Max(0, k - 80), Math.Min(200, a.Length - Math.Max(0, k - 80)))) +
                                         ",\"copy\":" + Json.Str(bb.Substring(Math.Max(0, k - 80), Math.Min(200, bb.Length - Math.Max(0, k - 80))))) + "}";
            KeepCopy = copies;
            if (Full) res = res.Substring(0, res.Length - 1) + ",\"origJson\":" + a + ",\"copyJson\":" + bb + "}";
            return res;
        }

        public static List<tosaveitemscript> KeepCopy;
        public static bool Full;

        private static string Normalised(itemDataClass d)
        {
            foreach (var it in d.items) it.transform = new posRotd(new Vector3d(0, 0, 0), Vector3.zero);
            foreach (var f in ListFields)
                if (f.GetValue(d) is IList list && list.Count > 1)
                {
                    var arr = new List<object>(); foreach (var o in list) arr.Add(o);
                    arr.Sort((x, y) => IdOf(x).CompareTo(IdOf(y)));
                    list.Clear(); foreach (var o in arr) list.Add(o);
                }
            return JsonUtility.ToJson(d);
        }

        private static long IdOf(object o) => o is iid2 b ? ((long)b.id << 16) + b.ind : o is iid a ? (long)a.id << 16 : 0;

        public static string Test(int n)
        {
            if (savedatascript.s == null || mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
            var p = mainscript.s.player.transform.position;
            var near = new List<tosaveitemscript>();
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.gameObject.activeInHierarchy && it.transform.parent == null) near.Add(it);
            near.Sort((a, b) => (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));
            var rows = new List<string>();
            int same = 0, diff = 0, failed = 0;
            for (int i = 0; i < near.Count && i < n; i++)
            {
                var orig = near[i];
                string name = orig.name.Replace("(Clone)", "");
                try
                {
                    byte[] b0 = RecordCodec.Encode(Capture(orig));
                    var d = RecordCodec.Decode(b0);
                    if (d == null) { failed++; rows.Add(Json.Str(name + ": decode failed")); continue; }
                    uint nid = savedatascript.s.GetNewKey();
                    Rekey(d, orig.idInSave, nid);
                    Move(d, new Vector3d(3, 0.5, 0));
                    var copy = Spawn(d, nid);
                    if (copy == null) { failed++; rows.Add(Json.Str(name + ": spawn failed")); continue; }
                    // compare right away (before physics moves the copy): records with id/position aligned
                    var dc = Capture(copy);
                    Rekey(dc, nid, orig.idInSave);
                    var d0 = RecordCodec.Decode(b0);
                    foreach (var it in dc.items) it.transform = new posRotd(new Vector3d(0, 0, 0), Vector3.zero);
                    foreach (var it in d0.items) it.transform = new posRotd(new Vector3d(0, 0, 0), Vector3.zero);
                    var ba = RecordCodec.Encode(d0); var bb = RecordCodec.Encode(dc);
                    bool eq = ba.Length == bb.Length;
                    for (int q = 0; eq && q < ba.Length; q++) if (ba[q] != bb[q]) eq = false;
                    if (eq) { same++; rows.Add(Json.Str(name + ": identical (" + b0.Length + " B)")); }
                    else
                    {
                        diff++;
                        string a = ToJson(d0), bj = ToJson(dc);
                        int k = 0; while (k < a.Length && k < bj.Length && a[k] == bj[k]) k++;
                        int s0 = Math.Max(0, k - 60);
                        rows.Add(Json.Str(name + ": DIFF (json view at " + k + ") orig …" + a.Substring(s0, Math.Min(140, a.Length - s0)) + " | copy …" + bj.Substring(s0, Math.Min(140, bj.Length - s0))));
                    }
                    UnityEngine.Object.Destroy(copy.gameObject);
                    savedatascript.s.items.Remove(nid);
                }
                catch (Exception e) { failed++; rows.Add(Json.Str(name + ": " + e.GetType().Name + " " + e.Message)); }
            }
            return "{\"schema\":\"" + RecordCodec.SchemaHash(typeof(itemDataClass)).ToString("X16") + "\",\"unsupported\":" + Json.Str(RecordCodec.Unsupported) + ",\"tested\":" + (same + diff + failed) + ",\"identical\":" + same + ",\"different\":" + diff + ",\"failed\":" + failed + ",\"rows\":[" + string.Join(",", rows) + "]}";
        }
    }
}
