using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Test hooks for bug 3 (2026-10-03, "wheel attachment is buggy on cars if you are not the owner"): the game's own
    /// mount and dismount on a named slot, and where a part sits against its slot afterwards.
    public static partial class Entities
    {
        internal static tosaveitemscript RootOfNet(uint net) => MountRootOf(net);
        private static tosaveitemscript MountRootOf(uint net) => ByNet.TryGetValue(net, out var e) && Resolve(e) ? e.Root : null;

        /// A slot by name — cars have several of one name (four hubcap slots): the one holding `part` if given, else the
        /// first filled one (wantPart true), the first empty one (false), or simply the first (null).
        private static partslotscript SlotNamed(tosaveitemscript car, string slot, bool? wantPart = null, tosaveitemscript part = null)
        {
            partslotscript first = null, pick = null;
            foreach (var s in car.GetComponentsInChildren<partslotscript>(true))
            {
                if (s.name != slot) continue;
                if (first == null) first = s;
                if (part != null && s.hasPart() && s.part().tosave == part) return s;
                if (pick == null && wantPart.HasValue && s.hasPart() == wantPart.Value) pick = s;
            }
            return pick ?? first;
        }

        /// Diagnostics (`mount watch <net>` / `mount watchlog`): who unparents a part. The game's unparenting paths
        /// (attachablescript.Detach, tosaveitemscript.StartUnparent, fpscontroller.Drop) log when they touch it; a per-frame
        /// watcher logs every change of its parent with the attached flag and our entity state at that frame.
        private static tosaveitemscript _watch; private static Transform _watchParent; private static string _watchLog = "";
        private static void WatchNote(string what)
        {
            if (_watchLog.Length < 20000) _watchLog += "[f" + Time.frameCount + " " + what + "] ";
        }
        internal static void WatchTick()
        {
            if (_watch == null) return;
            var p = _watch.transform.parent;
            if (p == _watchParent) return;
            ByNet.TryGetValue(_watchNet, out var e);
            WatchNote("parent " + (_watchParent != null ? _watchParent.name : "null") + " -> " + (p != null ? p.name : "null") +
                      " attached " + (_watch.attachable != null && _watch.attachable.attached) + " proxy " + (e != null && e.Proxy) + " owner " + (e != null ? e.OwnerId : -1) +
                      " picked " + (mainscript.s != null && mainscript.s.player != null && mainscript.s.player.pickedUp == _watch.P));
            _watchParent = p;
        }
        private static uint _watchNet;

        /// Diagnostics (`mount deathwatch [reset]`): the game kills the player when its body's speed changes by more than
        /// deathVelocityChange in a frame (fpscontroller.FallDamage; god mode only skips Death). Measured here, god mode or
        /// not: the largest change, how many frames were deadly, and the nearest copy when it happened (bug 7: players killed
        /// standing next to a friend).
        public static float DeathDvMax; public static long DeathFrames; public static string DeathNear = "";
        public static bool DeathWatchOn; public static float DeathCopyMin = 999f; public static string DeathCopyMinName = "";
        public static float DeathVMax; public static long DeathCalls; public static string DeathAtReset = "", DeathPush = "";
        public static float DeathStepMax, DeathTurnMax, DeathStateMax, DeathStepSum; public static long DeathExtrapCalls, DeathStepN;
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.FallDamage))]
        private static class DeathWatch
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Pre(fpscontroller __instance)
            {
                if (__instance.RB == null || __instance != (mainscript.s != null ? mainscript.s.player : null)) return;
                if (DeathWatchOn)
                    foreach (var e in ByNet.Values)
                    {
                        if (!e.Proxy || e.Root == null || e.Root.car != null) continue;
                        float d = (e.Root.transform.position - (__instance.transform.position + Vector3.up)).magnitude;
                        if (d < DeathCopyMin) { DeathCopyMin = d; DeathCopyMinName = e.Root.name; }
                    }
                float dv = Mathf.Abs(__instance.RB.velocity.magnitude - __instance.lastVelocity);
                if (dv > DeathDvMax) DeathDvMax = dv;
                DeathCalls++; if (__instance.RB.velocity.magnitude > DeathVMax) DeathVMax = __instance.RB.velocity.magnitude;
                if (DeathWatchOn)   // the nearest car copy's own motion while near the player: steps vs the speed in its states
                {
                    var pp0 = __instance.transform.position;
                    foreach (var e in ByNet.Values)
                    {
                        if (!e.Proxy || e.Root == null || e.Root.car == null || (e.Root.transform.position - pp0).sqrMagnitude > 64f) continue;
                        DeathStepMax = Mathf.Max(DeathStepMax, e.StepSpeed); DeathTurnMax = Mathf.Max(DeathTurnMax, e.StepTurn);
                        DeathStateMax = Mathf.Max(DeathStateMax, e.LastVel.magnitude); if (e.StepExtrap) DeathExtrapCalls++;
                        DeathStepSum += e.StepSpeed; DeathStepN++;
                    }
                }
                // on foot and fast: what moved the player — the nearest car copy's own step speed vs its state speed
                if (DeathWatchOn && !__instance.Bsitting && __instance.RB.velocity.magnitude > 6f && DeathPush.Length < 1500)
                {
                    var pp = __instance.transform.position; Ent nc = null; float nd = 8f;
                    foreach (var e in ByNet.Values)
                    {
                        if (!e.Proxy || e.Root == null || e.Root.car == null) continue;
                        float d0 = (e.Root.transform.position - pp).magnitude;
                        if (d0 < nd) { nd = d0; nc = e; }
                    }
                    DeathPush += "[f" + Time.frameCount + " v " + __instance.RB.velocity.magnitude.ToString("F1") + " last " + __instance.lastVelocity.ToString("F1") +
                        (nc != null ? " copy " + nc.Root.name + " " + nd.ToString("F1") + " m step " + nc.StepSpeed.ToString("F1") + " state " + nc.LastVel.magnitude.ToString("F1") +
                                      (nc.StepExtrap ? " extrap" : "") + (nc.Physical ? " physical" : "") : " no car copy within 8 m") + "] ";
                }
                if (dv <= __instance.deathVelocityChange) return;
                DeathFrames++;
                var p = __instance.transform.position; string best = "nothing within 6 m"; float bd = 36f;
                foreach (var e in ByNet.Values)
                {
                    if (e.Root == null) continue;
                    float d = (e.Root.transform.position - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = e.Root.name + (e.Proxy ? " (copy" + (e.Physical ? ", physical" : "") + ")" : " (ours)") + " at " + Mathf.Sqrt(d).ToString("F1") + " m"; }
                }
                // what touches the player (any collider, shared or not) and which side of the jump is odd: a stale lastVelocity
                // (FallDamage runs only on foot) or a real one-frame push
                var touch = new List<string>();
                foreach (var c in Physics.OverlapSphere(p + Vector3.up, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || c.transform.root == __instance.transform.root) continue;
                    var crb = c.attachedRigidbody;
                    string n = c.transform.root.name + "/" + c.name + (crb != null ? (crb.isKinematic ? " kin" : " v" + crb.velocity.magnitude.ToString("F1")) : "");
                    if (!touch.Contains(n) && touch.Count < 6) touch.Add(n);
                }
                var vv = __instance.RB.velocity;
                if (DeathNear.Length < 1500) DeathNear += "[t" + Time.time.ToString("F2") + " f" + Time.frameCount + " dv " + dv.ToString("F1") + " v " + vv.magnitude.ToString("F1") +
                    " (" + vv.x.ToString("F1") + "," + vv.y.ToString("F1") + "," + vv.z.ToString("F1") + ") last " + __instance.lastVelocity.ToString("F1") +
                    " sit " + __instance.Bsitting + " near " + best + " touching " + string.Join("; ", touch) + "] ";
            }
        }

        /// Diagnostics (`mount trace <car> <item> <s>` / `mount tracelog`): per physics step, the car's spin and tilt with
        /// the item's mode (kinematic, colliding, proxy, owner) and where it is in the car — rows when anything changes or
        /// the car turns faster than 30 deg/s.
        private static tosaveitemscript _trCar, _trItem; private static uint _trCarNet, _trItemNet; private static float _trUntil;
        private static string _trLast = ""; private static readonly System.Text.StringBuilder _trLog = new System.Text.StringBuilder();
        private static tosaveitemscript _rideCar; private static uint _rideNet; private static float _rideUntil; private static int _rideN;
        private static Vector3 _ridePrev; private static readonly System.Text.StringBuilder _rideLog = new System.Text.StringBuilder();
        /// `mount ridetrace <car> <s>`: every 5th physics step, the local player on/near a car — where (car space), its
        /// body's velocity, the car's real motion (shown position delta) and how its body is driven.
        private static void RideTick()
        {
            if (_rideCar == null || Time.realtimeSinceStartup > _rideUntil) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var crb = _rideCar.GetComponent<Rigidbody>();
            if (pl == null || crb == null) return;
            var cp = crb.position; var cv = (cp - _ridePrev) / Time.fixedDeltaTime; _ridePrev = cp;
            if (_rideN++ % 5 != 0 || _rideLog.Length > 20000) return;
            ByNet.TryGetValue(_rideNet, out var e);
            var lp = _rideCar.transform.InverseTransformPoint(pl.transform.position);
            _rideLog.Append("t").Append(Time.time.ToString("F2")).Append(" lp ").Append(lp.ToString("F2"))
                    .Append(" plv ").Append(pl.RB != null ? pl.RB.velocity.ToString("F1") : "-")
                    .Append(" carv ").Append(cv.ToString("F1")).Append(" rbv ").Append(crb.velocity.ToString("F1"))
                    .Append(crb.isKinematic ? " kin" : " dyn").Append(e != null && e.MovedByBody ? " body" : "").Append(e != null && e.Physical ? " phys" : "")
                    .Append(" interp ").Append(crb.interpolation).Append(" par ").Append(pl.transform.parent != null ? pl.transform.parent.name : "-").Append("\n");
        }
        private static void TraceTick()
        {
            RideTick();
            if (_trCar == null || Time.realtimeSinceStartup > _trUntil) return;
            if (_trItem == null) _trItem = MountRootOf(_trItemNet);
            var crb = _trCar.GetComponent<Rigidbody>();
            if (crb == null) return;
            float spin = crb.angularVelocity.magnitude * Mathf.Rad2Deg;
            string mode = "-";
            if (_trItem != null)
            {
                var irb = _trItem.GetComponent<Rigidbody>();
                ByNet.TryGetValue(_trItemNet, out var ie);
                var lp = _trCar.transform.InverseTransformPoint(_trItem.transform.position);
                mode = (irb == null ? "norb" : (irb.isKinematic ? "kin" : "dyn") + (irb.detectCollisions ? "" : " nocol") + " m" + irb.mass.ToString("F1")) +
                       (ie != null ? (ie.Proxy ? " proxy" : " own") + " o" + ie.OwnerId + (ie.Physical ? " phys" : "") : " noent") +
                       (_trItem.transform.parent != null ? " par " + _trItem.transform.parent.name : "");
                if (spin > 30f || mode != _trLast)
                    _trLog.Append("t").Append(Time.time.ToString("F2")).Append(" spin ").Append(spin.ToString("F0")).Append(" up ").Append(_trCar.transform.up.y.ToString("F2"))
                          .Append(" carv ").Append(crb.velocity.magnitude.ToString("F1")).Append(" | ").Append(mode).Append(" lp ").Append(lp.ToString("F2"))
                          .Append(irb != null ? " v " + irb.velocity.magnitude.ToString("F1") : "").Append("\n");
            }
            _trLast = mode;
            if (_trLog.Length > 30000) _trUntil = 0f;
        }
        private static HarmonyLib.Harmony _watchHarmony;
        /// Any reparent of the watched part, with who did it (patched only while a watch runs: SetParent is hot).
        private static void WatchSetParent(Transform __instance, Transform parent)
        {
            if (_watch == null || __instance != _watch.transform || parent == __instance.parent) return;
            WatchNote("SetParent(" + (parent != null ? parent.name : "null") + ") < " + Short(StackTraceUtility.ExtractStackTrace()));
        }
        private static string Short(string st)
        {
            var o = new System.Text.StringBuilder();
            foreach (var l in st.Split('\n'))
            {
                if (l.Contains("StackTraceUtility") || l.Contains("WatchSetParent") || l.Contains("NativeDetour") || l.Contains("set_parentInternal") || l.Contains("Transform:SetParent")) continue;
                int i = l.IndexOf(" ("); o.Append(i > 0 ? l.Substring(0, i) : l).Append(" < ");
                if (o.Length > 260) break;
            }
            return o.ToString().Replace("(wrapper dynamic-method) ", "");
        }
        private static void WatchLoad(attachablescript _script, int _indexType, int _attachType, uint _parentid, int _index)
        {
            if (_watch == null || _script == null || _script.tosave != _watch) return;
            itemPlaceRemoveScript.s.GetOne(_parentid, out var item);
            WatchNote("save_attachable.Load type " + _attachType + " indexType " + _indexType + " index " + _index + " parent " + _parentid + " = " + (item != null ? item.name + " slots " + item.partslotscripts.Count : "none"));
        }
        private static System.Exception WatchAttachEx(attachablescript __instance, System.Exception __exception)
        {
            if (__exception != null && _watch != null && __instance.tosave == _watch) WatchNote("Attach threw " + __exception.GetType().Name + ": " + __exception.Message);
            return __exception;
        }
        private static void WatchSetParentProp(Transform __instance, Transform value) => WatchSetParent(__instance, value);
        private static void WatchPatch()
        {
            if (_watchHarmony != null) return;
            _watchHarmony = new HarmonyLib.Harmony("tldrevamp.mountwatch");
            var pre = new HarmonyLib.HarmonyMethod(typeof(Entities).GetMethod(nameof(WatchSetParent), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            _watchHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(Transform), nameof(Transform.SetParent), new[] { typeof(Transform), typeof(bool) }), pre);
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            _watchHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(save_attachable), nameof(save_attachable.Load), new[] { typeof(attachablescript), typeof(int), typeof(int), typeof(uint), typeof(int), typeof(Vector3d), typeof(int), typeof(Vector3d), typeof(Vector3d) }),
                new HarmonyLib.HarmonyMethod(typeof(Entities).GetMethod(nameof(WatchLoad), bf)));
            _watchHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(attachablescript), nameof(attachablescript.Attach), new[] { typeof(Transform) }),
                finalizer: new HarmonyLib.HarmonyMethod(typeof(Entities).GetMethod(nameof(WatchAttachEx), bf)));
            _watchHarmony.Patch(HarmonyLib.AccessTools.PropertySetter(typeof(Transform), nameof(Transform.parent)),
                new HarmonyLib.HarmonyMethod(typeof(Entities).GetMethod(nameof(WatchSetParentProp), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        }
        [HarmonyLib.HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class WatchDetach { [HarmonyLib.HarmonyPrefix] private static void Pre(attachablescript __instance) { if (_watch != null && __instance.tosave == _watch) WatchNote("Detach called (applying " + _applyingDetach + ")\n" + StackTraceUtility.ExtractStackTrace().Replace("\n", " < ").Substring(0, 400)); } }
        [HarmonyLib.HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.StartUnparent))]
        private static class WatchUnparent { [HarmonyLib.HarmonyPrefix] private static void Pre(tosaveitemscript __instance) { if (_watch != null && __instance == _watch) WatchNote("StartUnparent attached " + (__instance.attachable != null && __instance.attachable.attached)); } }
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Drop), new[] { typeof(bool) })]
        private static class WatchDrop { [HarmonyLib.HarmonyPrefix] private static void Pre(fpscontroller __instance) { if (_watch != null && __instance.pickedUp == _watch.P) WatchNote("player Drop"); } }

        /// Test: what an AttachSync for `slot` on `carNet` carries (the slot's item in the car's group, its index there).
        internal static bool SlotRef(uint carNet, string slot, out int parentIdx, out int index, out Vector3d lpos, out Vector3d gpos)
        {
            parentIdx = 0; index = -1; lpos = gpos = new Vector3d(0, 0, 0);
            if (!ByNet.TryGetValue(carNet, out var e) || !Resolve(e) || e.Root == null) return false;
            var s = SlotNamed(e.Root, slot);
            if (s == null) return false;
            var pt = s.GetComponentInParent<tosaveitemscript>();
            if (pt == null) return false;
            parentIdx = pt == e.Root ? 0 : e.Items.IndexOf(pt) + 1;
            if (pt != e.Root && parentIdx == 0) return false;
            index = pt.partslotscripts.IndexOf(s);
            var lp = pt.transform.InverseTransformPoint(s.T.position);
            lpos = new Vector3d(lp.x, lp.y, lp.z);
            gpos = mainscript.GlobalFromUnityPos(s.T.position);
            return index >= 0;
        }

        /// `mount off <carNet> <slot>`: the slot's part comes off as a dismount does (partslotscript.UnCraft).
        /// `mount on <partNet> <carNet> <slot>`: the loose part goes onto the slot as the attach key does (Craft).
        /// `mount slots <carNet>`: the car's slots, with what is in them.
        /// `mount pose <partNet> <carNet> <slot>`: the part against the slot (distance, attached, parent, bodies).
        private static int CountActive(List<BoxCollider> l) { int n = 0; if (l != null) foreach (var c in l) if (c != null && c.gameObject.activeInHierarchy && c.enabled) n++; return n; }

        public static string MountLab(string arg)
        {
            var a = arg.Split(' ');
            var ic = CultureInfo.InvariantCulture;
            if (a[0] == "slots" && a.Length > 1)
            {
                var car = MountRootOf(uint.Parse(a[1], ic));
                if (car == null) return "{\"error\":\"no car\"}";
                // the car's own slot list too: Bus01's rear wheel slots sit on BusBack, outside its hierarchy (ItemSnapshot.Group)
                var rows = new List<string>(); var seenS = new HashSet<partslotscript>();
                var all = new List<partslotscript>(car.GetComponentsInChildren<partslotscript>(true));
                if (car.partslotscripts != null) all.AddRange(car.partslotscripts);
                foreach (var s in all)
                {
                    if (s == null || !seenS.Add(s)) continue;
                    var pt = s.hasPart() ? s.part() : null;
                    // [slot, part, attached, the body it sits on] — the slot's name first, as other commands take it
                    rows.Add("[" + Json.Str(s.name) + "," + Json.Str(pt != null ? pt.name : "") + "," +
                             (pt != null && pt.attached ? 1 : 0) + "," + Json.Str(s.transform.root.name) + "]");
                }
                return "{\"slots\":[" + string.Join(",", rows) + "]}";
            }
            if (a[0] == "off" && a.Length > 2)
            {
                var car = MountRootOf(uint.Parse(a[1], ic));
                var s = car != null ? SlotNamed(car, a[2], true) : null;
                if (s == null || !s.hasPart()) return "{\"error\":\"no such slot with a part\"}";
                var p = s.part();
                bool ok = s.UnCraft();
                return "{\"off\":" + (ok ? "true" : "false") + ",\"part\":" + Json.Str(p.name) + ",\"id\":" + p.tosave.idInSave + "}";
            }
            if (a[0] == "on" && a.Length > 3)
            {
                var part = MountRootOf(uint.Parse(a[1], ic));
                var car = MountRootOf(uint.Parse(a[2], ic));
                var s = car != null ? SlotNamed(car, a[3], false) : null;
                if (part == null || part.attachable == null || s == null) return "{\"error\":\"no part / car / slot\"}";
                if (s.hasPart()) return "{\"error\":\"slot taken\"}";
                if (!s.CanCraft(part.attachable)) return "{\"error\":\"the part doesn't fit\"}";
                s.Craft(part.attachable);
                return "{\"on\":true,\"proxyPart\":" + (IsProxy(part) ? "true" : "false") + ",\"proxyCar\":" + (IsProxy(car) ? "true" : "false") + "}";
            }
            if (a[0] == "pose" && a.Length > 3)
            {
                var part = MountRootOf(uint.Parse(a[1], ic));
                var car = MountRootOf(uint.Parse(a[2], ic));
                var s = car != null && part != null ? SlotNamed(car, a[3], null, part) : null;
                if (part == null || s == null) return "{\"error\":\"no part / slot\"}";
                var at = part.attachable;
                var rb = part.GetComponent<Rigidbody>();
                int cols = 0, colsOn = 0;
                foreach (var c in part.GetComponentsInChildren<Collider>(true)) { cols++; if (c.enabled && c.gameObject.activeInHierarchy) colsOn++; }
                ByNet.TryGetValue(uint.Parse(a[1], ic), out var pe);
                return "{\"dist\":" + (part.transform.position - s.T.position).magnitude.ToString("F3", ic) +
                       ",\"attached\":" + (at != null && at.attached ? "true" : "false") + ",\"inSlot\":" + (s.hasPart() && s.part() == at ? "true" : "false") +
                       ",\"crafted\":" + (part.P != null && part.P.crafted ? "true" : "false") +
                       ",\"parent\":" + Json.Str(part.transform.parent != null ? part.transform.parent.name : "") + ",\"root\":" + Json.Str(part.transform.root.name) +
                       ",\"rb\":" + (rb == null ? "null" : "{\"kin\":" + (rb.isKinematic ? "true" : "false") + ",\"det\":" + (rb.detectCollisions ? "true" : "false") + "}") +
                       ",\"cols\":" + cols + ",\"colsOn\":" + colsOn +
                       ",\"owner\":" + (pe != null ? pe.OwnerId : -1) + ",\"epoch\":" + (pe != null ? (long)pe.Epoch : -1) + ",\"proxy\":" + (pe != null && pe.Proxy ? "true" : "false") +
                       ",\"ridesParent\":" + (pe != null && pe.Root != null && RidesParent(pe) ? "true" : "false") + "}";
            }
            if (a[0] == "watch" && a.Length > 1)
            {
                WatchPatch();
                _watchNet = uint.Parse(a[1], ic); _watch = MountRootOf(_watchNet); _watchLog = "";
                _watchParent = _watch != null ? _watch.transform.parent : null;
                return "{\"watching\":" + Json.Str(_watch != null ? _watch.name : "nothing") + "}";
            }
            if (a[0] == "watchlog") { WatchTick(); return "{\"log\":" + Json.Str(_watchLog) + "}"; }
            if (a[0] == "items" && a.Length > 1)
            {
                // the group's members by index (state records address them by it): name, and what each is attached to
                if (!ByNet.TryGetValue(uint.Parse(a[1], ic), out var ge)) return "{\"error\":\"no entity\"}";
                var rows = new List<string>();
                for (int i = 0; i < ge.Items.Count; i++)
                {
                    var it = ge.Items[i];
                    string par = "";
                    if (it != null && it.attachable != null && it.attachable.attached && it.attachable.point != null)
                    {
                        var pt = it.attachable.point.GetComponentInParent<tosaveitemscript>();
                        int pi = pt != null ? ge.Items.IndexOf(pt) : -2;
                        par = " @" + pi + (it.attachable.slot != null ? " in " + it.attachable.slot.transform.root.name + "/" + it.attachable.slot.name : " (no slot)");
                    }
                    else if (it != null) par = " LOOSE";
                    if (it != null && ge.Root != null) par += " " + (it.transform.position - ge.Root.transform.position).magnitude.ToString("F1", ic) + "m";
                    rows.Add(Json.Str(i + " " + (it != null ? it.name.Replace("(Clone)", "") : "-") + par + (ge.Split != null && ge.Split.Contains(i) ? " split" : "")));
                }
                return "{\"n\":" + ge.Items.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
            }
            if (a[0] == "probe" && a.Length > 1)
            {
                // a loose item's body: what holds it where it is
                var it = MountRootOf(uint.Parse(a[1], ic));
                if (it == null) return "{\"error\":\"no item\"}";
                var rb = it.GetComponent<Rigidbody>();
                string under = "nothing within 3 m";
                var hits = Physics.RaycastAll(it.transform.position + Vector3.up * 0.05f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                foreach (var hh in hits)
                {
                    if (hh.collider.transform.root == it.transform.root) continue;
                    under = hh.collider.name + " of " + hh.collider.transform.root.name + " at " + hh.distance.ToString("F2", ic) + " m";
                    break;
                }
                ByNet.TryGetValue(uint.Parse(a[1], ic), out var ie);
                // where it is in the nearest car's frame, and whether that's inside the car's body (renderer bounds, local)
                tosaveitemscript nc = null; float nd = float.MaxValue;
                foreach (var ci in savedatascript.s.items.Values)
                    if (ci != null && ci.car != null && ci.transform.parent == null) { float d = (ci.transform.position - it.transform.position).sqrMagnitude; if (d < nd) { nd = d; nc = ci; } }
                string inCar = "null";
                if (nc != null)
                {
                    var lp = nc.transform.InverseTransformPoint(it.transform.position);
                    var bb = new Bounds(); bool any = false;
                    foreach (var col in nc.GetComponentsInChildren<Collider>())
                    {
                        if (col.isTrigger || !col.enabled) continue;
                        var cb = col.bounds; var c0 = nc.transform.InverseTransformPoint(cb.min); var c1 = nc.transform.InverseTransformPoint(cb.max);
                        var lb = new Bounds((c0 + c1) * 0.5f, Vector3.zero); lb.Encapsulate(c0); lb.Encapsulate(c1);
                        if (any) bb.Encapsulate(lb); else { bb = lb; any = true; }
                    }
                    inCar = "{\"car\":" + Json.Str(nc.name) + ",\"lp\":[" + lp.x.ToString("F2", ic) + "," + lp.y.ToString("F2", ic) + "," + lp.z.ToString("F2", ic) + "]" +
                            ",\"bmin\":[" + bb.min.x.ToString("F2", ic) + "," + bb.min.y.ToString("F2", ic) + "," + bb.min.z.ToString("F2", ic) + "]" +
                            ",\"bmax\":[" + bb.max.x.ToString("F2", ic) + "," + bb.max.y.ToString("F2", ic) + "," + bb.max.z.ToString("F2", ic) + "]" +
                            ",\"inside\":" + (any && bb.Contains(lp) ? "true" : "false") + "}";
                }
                return "{\"inCar\":" + inCar + ",\"rb\":" + (rb == null ? "null" : "{\"kin\":" + (rb.isKinematic ? "true" : "false") + ",\"sleep\":" + (rb.IsSleeping() ? "true" : "false") + ",\"det\":" + (rb.detectCollisions ? "true" : "false") + ",\"grav\":" + (rb.useGravity ? "true" : "false") + ",\"v\":" + rb.velocity.magnitude.ToString("F2", ic) + ",\"cons\":" + Json.Str(rb.constraints.ToString()) + "}") +
                       ",\"parent\":" + Json.Str(it.transform.parent != null ? it.transform.parent.name + " of " + it.transform.root.name : "") +
                       ",\"physlock\":" + Json.Str(it.P != null && it.P.physlock != null ? it.P.physlock.name + " " + it.P.physlock.sType : "") +
                       ",\"layer\":" + Json.Str(LayerMask.LayerToName(it.gameObject.layer)) + ",\"under\":" + Json.Str(under) +
                       ",\"proxy\":" + (ie != null && ie.Proxy ? "true" : "false") + ",\"moved\":" + (ie != null && ie.MovedByBody ? "true" : "false") + "}";
            }
            if (a[0] == "trace" && a.Length > 3)
            {
                _trCarNet = uint.Parse(a[1], ic); _trItemNet = uint.Parse(a[2], ic);
                _trCar = MountRootOf(_trCarNet); _trItem = MountRootOf(_trItemNet);
                _trUntil = Time.realtimeSinceStartup + float.Parse(a[3], ic); _trLog.Length = 0; _trLast = "";
                return "{\"tracing\":" + Json.Str(_trCar != null ? _trCar.name : "no car") + "}";
            }
            if (a[0] == "tracelog") return "{\"log\":" + Json.Str(_trLog.ToString()) + "}";
            if (a[0] == "front" && a.Length > 2)
            {
                // test: the player stands `d` m in front of a car's nose, facing it
                var car = MountRootOf(uint.Parse(a[1], ic)); var pl = mainscript.s != null ? mainscript.s.player : null;
                if (car == null || pl == null) return "{\"error\":\"no car / player\"}";
                var f = car.transform.forward; f.y = 0f; f.Normalize();
                var at = car.transform.position + f * float.Parse(a[2], ic);
                // where tpground stands a player (the lowest walkable surface at or above the terrain), the player's pivot 1.1 m
                // over it, the body calmed. It took the first hit of a ray from only 5 m up and stood the pivot 0.3 m over it:
                // on a slope the ray started inside the terrain and the capsule sank into it — the host fell through the world
                // for seconds and landed at 28.6 m/s, read as a car copy killing them (buglist2m 7a, 3 full runs of 4)
                var hit = DebugBridge.StandHit(at.x, at.z, null);
                if (hit == null) return "{\"error\":\"no ground there\"}";
                pl.godModeTillGrounded = true;
                pl.Teleport(new Vector3(at.x, hit.Value.point.y + 1.1f, at.z));
                pl.transform.rotation = Quaternion.LookRotation(-f);
                DebugBridge.TpSettle(pl);
                var g = mainscript.GlobalFromUnityPos(car.transform.position);
                return "{\"at\":" + Json.Str(at.ToString("F1")) + ",\"carGx\":" + g.x.ToString("F2", ic) + ",\"carGz\":" + g.z.ToString("F2", ic) + "}";
            }
            if (a[0] == "bodies" && a.Length > 1)
            {
                // test (busdrive.py): every rigidbody jointed to a vehicle from outside its hierarchy (Bus01's BusBack: its own
                // body on a ConfigurableJoint to BusFront, no tosaveitemscript) — its pose in the vehicle's frame on THIS machine
                var car = MountRootOf(uint.Parse(a[1], ic));
                if (car == null) return "{\"error\":\"no car\"}";
                var root = car.transform.root; var rows = new List<string>();
                foreach (var j in UnityEngine.Object.FindObjectsOfType<Joint>())
                {
                    if (j == null || j.connectedBody == null || j.transform.root == root || j.connectedBody.transform.root != root) continue;
                    var rb = j.GetComponent<Rigidbody>(); var t = j.transform;
                    var lp = car.transform.InverseTransformPoint(t.position);
                    var lr = (Quaternion.Inverse(car.transform.rotation) * t.rotation).eulerAngles;
                    var g = mainscript.GlobalFromUnityPos(t.position);
                    rows.Add("{\"name\":" + Json.Str(t.name) + ",\"seats\":" + t.root.GetComponentsInChildren<seatscript>(true).Length + ",\"kin\":" + (rb != null && rb.isKinematic ? "true" : "false") +
                             ",\"lp\":[" + lp.x.ToString("F3", ic) + "," + lp.y.ToString("F3", ic) + "," + lp.z.ToString("F3", ic) + "]" +
                             ",\"lr\":[" + lr.x.ToString("F1", ic) + "," + lr.y.ToString("F1", ic) + "," + lr.z.ToString("F1", ic) + "]" +
                             ",\"g\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]" +
                             ",\"v\":" + (rb != null ? rb.velocity.magnitude.ToString("F2", ic) : "0") + "}");
                }
                // every wheel on the vehicle and on those bodies: why a bus that steers doesn't move (busdrive B)
                var wrows = new List<string>(); var seenW = new HashSet<WheelCollider>();
                var hosts = new List<Transform> { root };
                foreach (var j in UnityEngine.Object.FindObjectsOfType<Joint>())
                    if (j != null && j.connectedBody != null && j.transform.root != root && j.connectedBody.transform.root == root) hosts.Add(j.transform.root);
                foreach (var hr in hosts)
                    foreach (var w in hr.GetComponentsInChildren<WheelCollider>(true))
                    {
                        if (!seenW.Add(w)) continue;
                        var wrb = w.attachedRigidbody;
                        wrows.Add("[" + Json.Str(hr.name + "/" + w.name) + "," + (w.enabled ? 1 : 0) + "," + (w.gameObject.activeInHierarchy ? 1 : 0) + "," +
                                  w.motorTorque.ToString("F0", ic) + "," + w.brakeTorque.ToString("F0", ic) + "," + w.rpm.ToString("F0", ic) + "," +
                                  (w.isGrounded ? 1 : 0) + "," + Json.Str(wrb != null ? wrb.name + (wrb.isKinematic ? ":kin" : ":dyn") : "none") + "]");
                    }
                var cs = car.car;
                string eng = cs == null ? "{}" : "{\"throttle\":" + cs.throttle.ToString("F2", ic) + ",\"brake\":" + cs.brake.ToString("F2", ic) + ",\"handbrake\":" + cs.handbrake.ToString("F2", ic) + ",\"clutch\":" + cs.clutch.ToString("F2", ic) + ",\"gear\":" + cs.gear + ",\"running\":" + (cs.Engine != null && cs.Engine.running ? "true" : "false") +
                    ",\"whlocked\":" + (cs.whlocked ? "true" : "false") + ",\"lockBoxesOn\":\"" + CountActive(cs.whColliders) + "/" + (cs.whColliders != null ? cs.whColliders.Count : 0) + "\"" +
                    ",\"speed\":" + cs.speed.ToString("F2", ic) + ",\"rbVel\":" + (cs.RB != null ? cs.RB.velocity.magnitude.ToString("F2", ic) : "-1") +
                    ",\"rbSleeping\":" + (cs.RB != null && cs.RB.IsSleeping() ? "true" : "false") + ",\"constraints\":" + Json.Str(cs.RB != null ? cs.RB.constraints.ToString() : "") + "}";
                return "{\"rootSeats\":" + root.GetComponentsInChildren<seatscript>(true).Length + ",\"bodies\":[" + string.Join(",", rows) + "],\"wheels(name,en,active,motor,brake,rpm,grounded,rb)\":[" + string.Join(",", wrows) + "],\"car\":" + eng + "}";
            }
            if (a[0] == "carto" && a.Length > 4)
            {
                // test: the car (ours) stands at a global (gx, gz) facing yaw, on the ground there, every body still — the
                // site's own car was parked by a ramp (the user saw it climb it in step 7)
                if (!ByNet.TryGetValue(uint.Parse(a[1], ic), out var ce) || !Resolve(ce) || ce.Root == null || ce.Root.car == null) return "{\"error\":\"no car\"}";
                if (ce.Proxy) return "{\"error\":\"that car is another machine's copy\"}";
                var to = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(a[2], ic), 0, double.Parse(a[3], ic)));
                var gh = DebugBridge.GroundHit(to.x, to.z);
                if (gh == null) return "{\"error\":\"no ground there\"}";
                var cb = CarLocalBounds(ce.Root.car);
                var rot = Quaternion.Euler(0f, float.Parse(a[4], ic), 0f);
                var at = new Vector3(to.x, gh.Value.point.y - cb.min.y * ce.Root.transform.lossyScale.y + 0.15f, to.z);
                var root = ce.Root.transform;
                foreach (var rb in root.GetComponentsInChildren<Rigidbody>()) { if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } }
                root.SetPositionAndRotation(at, rot);
                foreach (var rb in root.GetComponentsInChildren<Rigidbody>()) { rb.position = rb.transform.position; rb.rotation = rb.transform.rotation; }
                Physics.SyncTransforms();
                ce.SentAtRest = false;
                return "{\"placed\":" + Json.Str(at.ToString("F2")) + ",\"on\":" + Json.Str(gh.Value.collider.name) + "}";
            }
            if (a[0] == "roof" && a.Length > 1)
            {
                // test: the player stands on top of a car (its box's top, over its middle)
                var car = MountRootOf(uint.Parse(a[1], ic)); var pl = mainscript.s != null ? mainscript.s.player : null;
                if (car == null || car.car == null || pl == null) return "{\"error\":\"no car / player\"}";
                var b = CarLocalBounds(car.car);
                var top = car.transform.TransformPoint(new Vector3(b.center.x, b.max.y, b.center.z));
                // the roof itself: the highest of the car's own colliders straight down over its middle (the cached box of a
                // copy whose part colliders were off at range stood the host on the hood)
                var mid = car.transform.TransformPoint(b.center); float bestY = float.NegativeInfinity;
                foreach (var rh in Physics.RaycastAll(mid + Vector3.up * 10f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
                    if (rh.collider.transform.root == car.transform.root && rh.point.y > bestY) { bestY = rh.point.y; top = rh.point; }
                pl.godModeTillGrounded = true;
                pl.Teleport(top + Vector3.up * 1.1f);   // the pivot over the roof like tpground (0.4: the capsule was in the roof)
                DebugBridge.TpSettle(pl);
                var g = mainscript.GlobalFromUnityPos(car.transform.position);
                return "{\"onTop\":" + Json.Str(top.ToString("F1")) + ",\"carGx\":" + g.x.ToString("F2", ic) + ",\"carGz\":" + g.z.ToString("F2", ic) + "}";
            }
            if (a[0] == "ridetrace" && a.Length > 2)
            {
                _rideNet = uint.Parse(a[1], ic); _rideCar = MountRootOf(_rideNet); _rideUntil = Time.realtimeSinceStartup + float.Parse(a[2], ic);
                _rideLog.Length = 0; _rideN = 0; if (_rideCar != null) _ridePrev = _rideCar.transform.position;
                return "{\"riding\":" + Json.Str(_rideCar != null ? _rideCar.name : "no car") + "}";
            }
            if (a[0] == "ridelog") return "{\"log\":" + Json.Str(_rideLog.ToString()) + "}";
            if (a[0] == "census" && a.Length > 3)
            {
                // every saved object (not only shared ones) within r m of a global point: name → count
                var at = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(a[1], ic), 0, double.Parse(a[2], ic)));
                float r = float.Parse(a[3], ic);
                var counts = new SortedDictionary<string, int>();
                if (a.Length > 5 && a[5].StartsWith("#"))   // one prefab id, every object (also parented): where, under what, shared as
                {
                    var rowsL = new List<string>();
                    if (savedatascript.s != null)
                        foreach (var it in savedatascript.s.items.Values)
                        {
                            if (it == null || "#" + it.id != a[5]) continue;
                            var d = it.transform.position - at; d.y = 0f;
                            if (d.magnitude > r) continue;
                            var en = ProxyOf(it.transform.root); var rb = it.GetComponent<Rigidbody>();
                            rowsL.Add("{\"name\":" + Json.Str(it.name) + ",\"pos\":" + V3(mainscript.GlobalFromUnityPos(it.transform.position)) +
                                      ",\"parent\":" + Json.Str(it.transform.parent != null ? it.transform.parent.name + " of " + it.transform.root.name : "") +
                                      ",\"net\":" + (en != null && en.Root == it ? en.NetId : 0) + ",\"kin\":" + (rb != null && rb.isKinematic ? "true" : "false") + "}");
                        }
                    return "{\"rows\":[" + string.Join(",", rowsL) + "]}";
                }
                if (savedatascript.s != null)
                    foreach (var it in savedatascript.s.items.Values)
                    {
                        if (it == null || it.transform.parent != null) continue;
                        var d = it.transform.position - at; d.y = 0f;
                        if (d.magnitude > r) continue;
                        string n = a.Length > 4 && a[4] == "byid" ? "#" + it.id : it.name.Replace("(Clone)", "");
                        counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
                    }
                var rows = new List<string>(); foreach (var kv in counts) rows.Add(Json.Str(kv.Key) + ":" + kv.Value);
                return "{" + string.Join(",", rows) + "}";
            }
            if (a[0] == "deathwatch")
            {
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (a.Length > 1 && a[1] == "reset")
                {
                    DeathDvMax = 0f; DeathFrames = 0; DeathNear = ""; DeathWatchOn = true; DeathCopyMin = 999f; DeathCopyMinName = ""; DeathVMax = 0f; DeathCalls = 0; DeathPush = ""; DeathStepMax = DeathTurnMax = DeathStateMax = DeathStepSum = 0f; DeathExtrapCalls = DeathStepN = 0;
                    DeathAtReset = pl != null && pl.RB != null ? "f" + Time.frameCount + " last " + pl.lastVelocity.ToString("F1", ic) + " v " + pl.RB.velocity.magnitude.ToString("F1", ic) + " sit " + pl.Bsitting : "";
                }
                return "{\"threshold\":" + (pl != null ? pl.deathVelocityChange : 0f).ToString("F1", ic) + ",\"dvMax\":" + DeathDvMax.ToString("F2", ic) + ",\"deadlyFrames\":" + DeathFrames + ",\"vMax\":" + DeathVMax.ToString("F1", ic) + ",\"calls\":" + DeathCalls + ",\"atReset\":" + Json.Str(DeathAtReset) + ",\"push\":" + Json.Str(DeathPush) + ",\"copyStepMax\":" + DeathStepMax.ToString("F1", ic) + ",\"copyStepMean\":" + (DeathStepN > 0 ? DeathStepSum / DeathStepN : 0f).ToString("F2", ic) + ",\"copyTurnMax\":" + DeathTurnMax.ToString("F0", ic) + ",\"copyStateMax\":" + DeathStateMax.ToString("F1", ic) + ",\"copyExtrap\":" + Json.Str(DeathExtrapCalls + "/" + DeathStepN) + ",\"near\":" + Json.Str(DeathNear) + ",\"closestCopy\":" + Json.Str(DeathCopyMinName + " " + DeathCopyMin.ToString("F2", ic) + " m from the chest") + "}";
            }
            if (a[0] == "putin" && a.Length > 2)
            {
                // test: an item this machine owns dropped onto a car's passenger seat (half a metre above it, at rest)
                var item = MountRootOf(uint.Parse(a[1], ic));
                var car = MountRootOf(uint.Parse(a[2], ic));
                if (item == null || car == null) return "{\"error\":\"no item / car\"}";
                if (IsProxy(item)) return "{\"error\":\"not the owner\"}";
                seatscript seat = null;
                foreach (var st in car.GetComponentsInChildren<seatscript>(true)) if (!st.driverSeat0 && st.mainseat == null) { seat = st; break; }
                var at = (seat != null ? seat.transform.position : car.transform.position) + car.transform.up * 0.6f;
                item.transform.SetPositionAndRotation(at, car.transform.rotation);
                var rb = item.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.position = at; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.WakeUp(); }
                return "{\"put\":true,\"seat\":" + Json.Str(seat != null ? seat.name : "none") + "}";
            }
            return "{\"error\":\"mount slots <car> | off <car> <slot> | on <part> <car> <slot> | pose <part> <car> <slot> | putin <item> <car>\"}";
        }
    }
}
