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

        private static string HitchHealth(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no car\"}";
            var rows = new List<string>(); int attached = 0;
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i]; if (it == null) continue;
                var at = it.attachable; if (at != null && at.attached) attached++;
                var d = ItemSnapshot.Capture(it);
                foreach (var pc in d.partconditions)
                    if (pc != null) rows.Add("[" + i + "," + pc.ind + "," + pc.state + "," + pc.state2 + "," + pc.state3 + "]");
            }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return "{\"dead\":" + (pl != null && pl.died ? "true" : "false") + ",\"attached\":" + attached + ",\"conditions\":[" + string.Join(",", rows) + "]}";
        }

        /// The attach press with something held (fpscontroller.RayStuff, mountUp on a slot that takes it): the player
        /// drops it and the slot crafts it, or it attaches to the slot when it isn't slot-only - all as the player's own
        /// action. The slot is the nearest within `reach` metres of the held part that takes it. `mount playermount [reach]`
        private static string PlayerMount(float reach)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var held = pl != null ? pl.pickedUp : null;
            var at = held != null && held.tosave != null ? held.tosave.attachable : null;
            if (at == null) return "{\"error\":\"holding nothing attachable\"}";
            partslotscript best = null; float bd = reach;
            foreach (var s in Object.FindObjectsOfType<partslotscript>())
            {
                if (s == null || s.T == null) continue;
                bool can = false;
                try { can = s.CanCraft(at); } catch (System.Exception) { }
                float d = Vector3.Distance(s.T.position, at.transform.position);
                if (can && d < bd) { bd = d; best = s; }
            }
            if (best == null) return "{\"error\":\"no slot within reach takes it\"}";
            bool was = TestAsPlayer; TestAsPlayer = true;
            try
            {
                pl.Drop();
                if (at.onlyToSlot) best.Craft(at);
                else { at.tempDisableColliderAttachPoint = true; at.Attach(best); at.tempDisableColliderAttachPoint = false; }
            }
            catch (System.Exception ex) { return "{\"error\":" + Json.Str("mount threw: " + ex.GetType().Name + " " + ex.Message) + "}"; }
            finally { TestAsPlayer = was; }
            return "{\"slot\":" + Json.Str(best.name) + ",\"dist\":" + bd.ToString("F2", CultureInfo.InvariantCulture) + ",\"onlyToSlot\":" +
                (at.onlyToSlot ? "true" : "false") + ",\"attachedNow\":" + (at.attached ? "true" : "false") + "}";
        }

        /// A slot by name — cars have several of one name (four hubcap slots): the one holding `part` if given, else the
        /// first filled one (wantPart true), the first empty one (false), or simply the first (null).
        private static partslotscript SlotNamed(tosaveitemscript car, string slot, bool? wantPart = null, tosaveitemscript part = null)
        {
            partslotscript first = null, pick = null;
            // the object's own slot list too: Bus01's rear slots sit on BusBack, outside its hierarchy (as `mount slots`)
            var all = new List<partslotscript>(car.GetComponentsInChildren<partslotscript>(true));
            if (car.partslotscripts != null) all.AddRange(car.partslotscripts);
            foreach (var s in all)
            {
                if (s == null || s.name != slot) continue;
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
        private static uint _gtNet; private static float _gtUntil; private static readonly System.Text.StringBuilder _gtLog = new System.Text.StringBuilder();
        /// `mount griptrace <trailer> <s>`: every physics step, the trailer's body and its drawbar's on THIS machine (where,
        /// kinematic or not, in the player's hands) and the entity's role - a drawbar that leaves the hand at once.
        private static void GripTick()
        {
            if (_gtNet == 0 || Time.realtimeSinceStartup > _gtUntil || _gtLog.Length > 60000) return;
            if (!ByNet.TryGetValue(_gtNet, out var e) || !Resolve(e) || e.Root == null || e.Root.attachable == null) return;
            var rrb = e.Root.GetComponent<Rigidbody>(); var at = e.Root.attachable; var grb = at.GetComponent<Rigidbody>();
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var ic = CultureInfo.InvariantCulture;
            string P(Vector3 v) { var g = mainscript.GlobalFromUnityPos(v); return g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic); }
            _gtLog.Append("t").Append(Time.time.ToString("F3", ic)).Append(" f").Append(Time.frameCount)
                .Append(" root ").Append(P(e.Root.transform.position)).Append(rrb != null ? (rrb.isKinematic ? " kin" : " dyn") + " v" + rrb.velocity.magnitude.ToString("F1", ic) : " norb")
                .Append(" grip ").Append(P(at.transform.position)).Append(grb != null ? (grb.isKinematic ? " kin" : " dyn") + " v" + grb.velocity.magnitude.ToString("F1", ic) : " norb")
                .Append(" held ").Append(pl != null && pl.pickedUp == e.Root.P ? 1 : 0)
                .Append(e.Proxy ? " proxy" : " own").Append(" o").Append(e.OwnerId).Append(e.HitchWaiting ? " wait" : "").Append("\n");
        }
        private static void TraceTick()
        {
            RideTick();
            GripTick();
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
            if (a[0] == "held" && a.Length > 1)
            {
                // `mount held <net>`: how each attached member is held on — parented to its point (rides its parent) or on
                // a joint — and whether the sender treats it as an attached car part (Entities.Tick: no states)
                var root = MountRootOf(uint.Parse(a[1], ic));
                if (root == null) return "{\"error\":\"no such object\"}";
                var rows = new List<string>();
                foreach (var at in root.GetComponentsInChildren<attachablescript>(true))
                {
                    if (at == null || !at.attached) continue;
                    bool carPoint = at.point != null && at.point.GetComponentInParent<carscript>() != null;
                    rows.Add("{\"name\":" + Json.Str(at.name) + ",\"parented\":" + (at.parentAtAttach ? "true" : "false") + ",\"parent\":" + Json.Str(at.transform.parent != null ? at.transform.parent.name : "") +
                             ",\"part\":" + (at.isPart ? "true" : "false") + ",\"slot\":" + (at.slot != null ? "true" : "false") + ",\"carPoint\":" + (carPoint ? "true" : "false") +
                             ",\"joint\":" + (at.J != null ? "true" : "false") + "}");
                }
                return "{\"root\":" + Json.Str(root.name) + ",\"held\":[" + string.Join(",", rows) + "]}";
            }
            if (a[0] == "whynot" && a.Length > 1)
            {
                // `mount whynot <item net>`: why the game's physics lock does or doesn't store this item — for every storage
                // area within 4 m: its last two scans saw the item (both needed), the game's CanStore, started; and the
                // item's own conditions from mountStuff.FreezeUpd
                var it = MountRootOf(uint.Parse(a[1], ic));
                if (it == null || it.P == null) return "{\"error\":\"no item\"}";
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
                       ",\"setting\":" + settingsscript.s.S.IPhysicsLock + ",\"colliders\":[" + string.Join(",", layers) + "],\"areas\":[" + string.Join(",", rows) + "]}";
            }
            if (a[0] == "areas" && a.Length > 1)
            {
                // the storage areas of a shared object (mountStuff): a car transporter's bed, trunks, racks - and what each
                // holds (transporter.py)
                var root = MountRootOf(uint.Parse(a[1], ic));
                if (root == null) return "{\"error\":\"no such object\"}";
                var rows = new List<string>(); int i = 0;
                foreach (var m in root.GetComponentsInChildren<mountStuff>(true))
                {
                    var c = m.col; var t = c != null ? c.transform : m.transform;
                    var center = c != null ? t.TransformPoint(c.center) : t.position;
                    var size = c != null ? Vector3.Scale(c.size, t.lossyScale) : Vector3.zero;
                    var g = mainscript.GlobalFromUnityPos(center);
                    var cars = new List<string>();
                    foreach (var sc in m.storedcars) if (sc != null) cars.Add(NetIdOf(sc.gameObject).ToString(ic));
                    rows.Add("{\"i\":" + i++ + ",\"name\":" + Json.Str(m.name) + ",\"type\":" + Json.Str(m.sType.ToString()) + ",\"manual\":" + (m.onlyManual ? "true" : "false") +
                             ",\"noLock\":" + (m.noPhysicsLock ? "true" : "false") + ",\"store\":" + (m.cstore ? "true" : "false") +
                             ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "],\"size\":[" +
                             size.x.ToString("F2", ic) + "," + size.y.ToString("F2", ic) + "," + size.z.ToString("F2", ic) + "],\"yaw\":" + t.eulerAngles.y.ToString("F1", ic) +
                             ",\"local\":[" + root.transform.InverseTransformPoint(center).x.ToString("F2", ic) + "," + root.transform.InverseTransformPoint(center).y.ToString("F2", ic) + "," + root.transform.InverseTransformPoint(center).z.ToString("F2", ic) + "]" +
                             ",\"items\":" + m.stored.Count + ",\"cars\":[" + string.Join(",", cars) + "]}");
                }
                return "{\"root\":" + Json.Str(root.name) + ",\"areas\":[" + string.Join(",", rows) + "]}";
            }
            if (a[0] == "prefab" && a.Length > 1)
            {
                // an item's prefab, object by object, to a text file next to the game (designing mods from the game's
                // own parts): `mount prefab <item id>` → {"file": ...}
                int id = int.Parse(a[1], ic);
                var pf = itemdatabase.s.items[id];
                if (pf == null) return "{\"error\":\"no such item\"}";
                var sb = new System.Text.StringBuilder();
                var toRoot = pf.transform.worldToLocalMatrix;
                void Dump(Transform t, int depth)
                {
                    var m = toRoot * t.localToWorldMatrix; var p0 = (Vector3)m.GetColumn(3);
                    sb.Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [off]")
                      .Append(" at ").Append(p0.ToString("F2")).Append(" rot ").Append(m.rotation.eulerAngles.ToString("F0")).Append(" scale ").Append(m.lossyScale.ToString("F2"))
                      .Append(" layer ").Append(t.gameObject.layer).Append(" |");
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c == null || c is Transform) continue;
                        sb.Append(' ').Append(c.GetType().Name);
                        if (c is MeshFilter mf && mf.sharedMesh != null) sb.Append('(').Append(mf.sharedMesh.name).Append(' ').Append(mf.sharedMesh.bounds.size.ToString("F2")).Append(mf.sharedMesh.isReadable ? " r" : "").Append(')');
                        else if (c is BoxCollider bc) sb.Append('(').Append(bc.center.ToString("F2")).Append(' ').Append(bc.size.ToString("F2")).Append(bc.isTrigger ? " trigger" : "").Append(')');
                        else if (c is MeshCollider mc) sb.Append('(').Append(mc.sharedMesh != null ? mc.sharedMesh.name : "none").Append(mc.convex ? " convex" : "").Append(mc.isTrigger ? " trigger" : "").Append(')');
                        else if (c is WheelCollider wc) sb.Append("(r ").Append(wc.radius.ToString("F2", ic)).Append(')');
                        else if (c is HingeJoint hj) sb.Append("(axis ").Append(hj.axis.ToString("F1")).Append(" body ").Append(hj.connectedBody != null ? hj.connectedBody.name : "none").Append(')');
                        else if (c is mountStuff ms) sb.Append('(').Append(ms.sType).Append(ms.noPhysicsLock ? " noLock" : "").Append(')');
                    }
                    sb.Append('\n');
                    for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1);
                }
                Dump(pf.transform, 0);
                var file = System.IO.Path.Combine(Application.dataPath, "prefab_" + id + ".txt");
                System.IO.File.WriteAllText(file, sb.ToString());
                return "{\"file\":" + Json.Str(file) + "}";
            }
            if (a[0] == "carlock" && a.Length > 1)
            {
                // a car's wheel lock (carscript.Lock, set by a car area while the handbrake is on): `mount carlock <net> [hb]`
                var root = MountRootOf(uint.Parse(a[1], ic));
                var car = root != null ? root.GetComponent<carscript>() : null;
                if (car == null) return "{\"error\":\"no such car\"}";
                if (a.Length > 2) car.handbrake = float.Parse(a[2], ic);
                return "{\"whlocked\":" + (car.whlocked ? "true" : "false") + ",\"handbrake\":" + car.handbrake.ToString("F2", ic) + "}";
            }
            if (a[0] == "drawbar" && a.Length > 1) return DrawbarTest(a);   // Entities.PushGrip
            if (a[0] == "grip" && a.Length > 1)
            {
                // what a trailer's drawbar is made of (cartrailer.py, the trailer bug): `mount grip <trailer>`
                var tr = MountRootOf(uint.Parse(a[1], ic));
                if (tr == null) return "{\"error\":\"no such object\"}";
                string Comps(GameObject go) { var l = new List<string>(); foreach (var c in go.GetComponents<Component>()) l.Add(c == null ? "missing" : c.GetType().Name); return string.Join(",", l); }
                var ag = tr.GetComponentInChildren<attachgripscript>(true);
                var gr = tr.attachable;
                return "{\"root\":" + Json.Str(Comps(tr.gameObject)) + ",\"P\":" + (tr.P != null ? "true" : "false")
                    + ",\"gripScript\":" + Json.Str(ag == null ? "none" : ag.name + " grip " + (ag.Grip != null ? ag.Grip.name : "null") + " prefab " + (ag.GripPrefab != null ? ag.GripPrefab.name : "null"))
                    + ",\"grip\":" + Json.Str(gr == null ? "none" : gr.name + " [" + Comps(gr.gameObject) + "] parent " + (gr.transform.parent != null ? gr.transform.parent.name : "none")
                        + " tosave " + (gr.tosave != null ? gr.tosave.name : "null") + " attached " + gr.attached)
                    + ",\"prefabGripTosave\":" + Json.Str(ag != null && ag.GripPrefab != null && ag.GripPrefab.GetComponent<attachablescript>() != null && ag.GripPrefab.GetComponent<attachablescript>().tosave != null ? ag.GripPrefab.GetComponent<attachablescript>().tosave.name : "null")
                    + "}";
            }
            if (a[0] == "hitchevents") return SessionLog.EventsJson();
            if (a[0] == "hitchhealth" && a.Length > 1) return HitchHealth(uint.Parse(a[1], ic));
            if (a[0] == "hitchstate" && a.Length > 1) return TestHitchState(uint.Parse(a[1], ic));
            if (a[0] == "unhitch" && a.Length > 2) return TestHitch(uint.Parse(a[1], ic), uint.Parse(a[2], ic), false);
            if (a[0] == "playermount") return PlayerMount(a.Length > 1 ? float.Parse(a[1], ic) : 3f);
            if (a[0] == "pickgrip" && a.Length > 1)
            {
                // a trailer's drawbar into the hands as a player takes it: `mount pickgrip <trailer> stand` puts the player
                // 0.8 m beside the drawbar on the side away from the trailer, facing it; `mount pickgrip <trailer>` picks it
                // up from where the player stands (once settled: the hold point is where the drawbar was, in the camera's
                // frame, so a player still falling or pushed off after a teleport dropped it at once)
                var tr = MountRootOf(uint.Parse(a[1], ic));
                var pl = mainscript.s != null ? mainscript.s.player : null;
                var grip = tr != null && tr.attachable != null ? tr.attachable.GetComponent<pickupable>() : null;
                if (pl == null || grip == null) return "{\"error\":\"no player or drawbar\"}";
                var gp = grip.transform.position;
                if (a.Length > 2 && a[2] == "stand")
                {
                    var away = gp - tr.transform.position; away.y = 0f;
                    away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
                    pl.Teleport(new Vector3(gp.x, gp.y + 0.1f, gp.z) + away * 0.8f);
                    pl.BodyRot.rotation = Quaternion.LookRotation(-away);
                    return "{\"standing\":true}";
                }
                var cam = pl.Cam != null ? pl.Cam.transform : pl.transform;
                float d = Vector3.Distance(cam.position, gp);
                if (d > 2.5f) return "{\"error\":" + Json.Str("out of reach: " + d.ToString("F2", ic) + " m") + "}";
                pl.Pickup(grip, gp);
                return "{\"holding\":" + (pl.pickedUp == grip ? "true" : "false") + ",\"dist\":" + d.ToString("F2", ic) + "}";
            }
            if (a[0] == "gap" && a.Length > 2)
            {
                // where a trailer's drawbar is against the car's free tow ball: both global, the drawbar ahead of the trailer's
                // root along its forward axis (tests place the trailer from it) and off the trailer's joint point (gripOff).
                // `mount gap <car> <trailer>`
                var car = MountRootOf(uint.Parse(a[1], ic)); var tr = MountRootOf(uint.Parse(a[2], ic));
                var at = tr != null ? tr.attachable : null;
                if (car == null || at == null) return "{\"error\":\"no car or trailer\"}";
                partslotscript ball = null;
                foreach (var s in car.GetComponentsInChildren<partslotscript>(true))
                {
                    bool can = false;
                    try { can = s.T != null && (s.CanCraft(at) || s.hasPart() && s.part() == at); } catch (System.Exception) { }
                    if (can) { ball = s; break; }
                }
                if (ball == null) return "{\"error\":\"no tow ball takes the drawbar\"}";
                var gb = mainscript.GlobalFromUnityPos(ball.T.position); var gg = mainscript.GlobalFromUnityPos(at.transform.position);
                float ahead = tr.transform.InverseTransformPoint(at.transform.position).z;
                var gs = tr.GetComponent<attachgripscript>();
                float off = gs != null && gs.GripPos != null ? Vector3.Distance(gs.GripPos.position, at.transform.position) : -1f;
                // where the trailer's drawbar joint holds the drawbar, against its GripPos
                float anchorErr = gs != null && gs.J != null && gs.GripPos != null ? Vector3.Distance(gs.J.transform.TransformPoint(gs.J.anchor), gs.GripPos.position) : -1f;
                return "{\"ball\":[" + gb.x.ToString("F3", ic) + "," + gb.y.ToString("F3", ic) + "," + gb.z.ToString("F3", ic) + "],\"grip\":[" +
                    gg.x.ToString("F3", ic) + "," + gg.y.ToString("F3", ic) + "," + gg.z.ToString("F3", ic) + "],\"dist\":" +
                    Vector3.Distance(ball.T.position, at.transform.position).ToString("F2", ic) + ",\"ahead\":" + ahead.ToString("F2", ic) +
                    ",\"gripOff\":" + off.ToString("F2", ic) + ",\"gripId\":" + at.GetInstanceID() + ",\"utanfuto\":" + (at.utanfuto != null ? "true" : "false") +
                    ",\"gripOwnGrip\":" + (gs != null && gs.Grip == at.gameObject ? "true" : "false") + ",\"anchorErr\":" + anchorErr.ToString("F2", ic) + ",\"slot\":" + Json.Str(ball.name) + ",\"attached\":" + (at.attached ? "true" : "false") + "}";
            }
            if ((a[0] == "hitch" || a[0] == "hitchrequest") && a.Length > 2)
            {
                // hitch a trailer to a car (cartrailer.py): the trailer's grip (its own attachable, attachgripscript) onto the
                // car's slot that takes it - as a player carrying the drawbar to the tow ball. `mount hitch <car> <trailer>`
                if (InSession) return TestHitch(uint.Parse(a[1], ic), uint.Parse(a[2], ic), true);
                var car = MountRootOf(uint.Parse(a[1], ic)); var tr = MountRootOf(uint.Parse(a[2], ic));
                if (car == null || tr == null) return "{\"error\":\"no such objects\"}";
                var grip = tr.attachable;
                if (grip == null) return "{\"error\":\"the trailer has no grip\"}";
                if (grip.tipus == null) return "{\"error\":\"the grip has no part types\"}";
                if (grip.attached) return "{\"already\":true}";
                var tried = new List<string>();
                foreach (var s in car.GetComponentsInChildren<partslotscript>(true))
                {
                    bool can = false;
                    try { can = s.tipus != null && s.CanCraft(grip); } catch (System.Exception) { }
                    if (can && s.T != null)
                    {
                        // the grip is its own object (attachgripscript instantiates it) with no save record: its part script's
                        // tosave can be empty. Attach needs it; the drawbar belongs to the trailer, so use the trailer's.
                        var gp = grip.GetComponent<pickupable>();
                        string diag = $"grip {grip.name} tosave {(grip.tosave != null ? grip.tosave.name : "null")} pickupable {(gp == null ? "none" : gp.tosave != null ? gp.tosave.name : "null")} slot {s.name}";
                        if (grip.tosave == null) grip.tosave = gp != null && gp.tosave != null ? gp.tosave : tr;
                        var ut = grip.utanfuto;
                        diag += $" | P {(grip.tosave.P != null ? "ok" : "null")} P.tosave {(grip.tosave.P != null && grip.tosave.P.tosave != null ? "ok" : "null")}"
                              + $" parts {(s.parts != null ? "ok" : "null")} utanfuto {(ut != null ? "ok" : "null")} com {(ut != null && ut.CenterOfMassAttached != null ? "ok" : "null")}"
                              + $" rb {(grip.GetComponent<Rigidbody>() != null ? "ok" : "null")} parentAtAttach {grip.parentAtAttach}";
                        try
                        {
                            // the whole trailer to where its drawbar meets the ball (the joint would yank it there)
                            var off = s.T.position - grip.transform.position;
                            foreach (var body in new[] { tr.GetComponent<Rigidbody>(), grip.GetComponent<Rigidbody>() })
                                if (body != null) { body.transform.position += off; body.position = body.transform.position; body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                            grip.Attach(s);
                        }
                        catch (System.Exception ex) { return "{\"error\":" + Json.Str("attach threw: " + diag + " " + ex.ToString().Substring(0, System.Math.Min(700, ex.ToString().Length))) + "}"; }
                        // the trailer's own parking brake (a lever, utanfutoscript.brake): off, as a player would
                        var ub = tr.GetComponent<utanfutoscript>();
                        string brake = ub == null || ub.brake == null ? "none" : ub.brake.currentTurnState.ToString(ic);
                        if (ub != null && ub.brake != null) ub.brake.currentTurnState = 0;
                        return "{\"hitched\":" + Json.Str(s.name) + ",\"brakeWas\":" + Json.Str(brake) + ",\"diag\":" + Json.Str(diag) + "}";
                    }
                    if (s.tipus != null && s.tipus.Length > 0) tried.Add(s.name + ":" + string.Join("/", s.tipus));
                }
                return "{\"error\":\"no slot takes the grip\",\"grip\":" + Json.Str(string.Join("/", grip.tipus ?? new string[0])) + ",\"slots\":" + Json.Str(string.Join(" ", tried)) + "}";
            }
            if (a[0] == "placecar" && a.Length > 3)
            {
                // put a shared car (the cargo) above storage area i of another (the carrier), facing as the area, still
                // (transporter.py): `mount placecar <carrier> <cargo> <i> [dy]`
                var carrier = MountRootOf(uint.Parse(a[1], ic)); var cargo = MountRootOf(uint.Parse(a[2], ic));
                if (carrier == null || cargo == null) return "{\"error\":\"no such objects\"}";
                var areas = carrier.GetComponentsInChildren<mountStuff>(true);
                int i = int.Parse(a[3], ic);
                if (i < 0 || i >= areas.Length || areas[i].col == null) return "{\"error\":\"no such area\"}";
                float dy = a.Length > 4 ? float.Parse(a[4], ic) : 0.5f;
                var t = areas[i].col.transform;
                var top = t.TransformPoint(areas[i].col.center - new Vector3(0f, areas[i].col.size.y * 0.5f, 0f)) + t.up * dy;
                var rb = cargo.GetComponent<Rigidbody>();
                cargo.transform.SetPositionAndRotation(top, Quaternion.LookRotation(Vector3.ProjectOnPlane(t.forward, Vector3.up), Vector3.up));
                if (rb != null) { rb.position = top; rb.rotation = cargo.transform.rotation; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                return "{\"placed\":true,\"at\":" + Json.Str(mainscript.GlobalFromUnityPos(top).ToString()) + "}";
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
            if (a[0] == "griptrace" && a.Length > 2) { _gtNet = uint.Parse(a[1], ic); _gtUntil = Time.realtimeSinceStartup + float.Parse(a[2], ic); _gtLog.Length = 0; return "{\"tracing\":" + _gtNet + "}"; }
            if (a[0] == "griplog") return "{\"log\":" + Json.Str(_gtLog.ToString()) + "}";
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
                // `d` counts from the car's middle: 1.2 m is still under a car01's hood, where a player has no room since
                // teleports check for it (2a13d34) — on, in quarter metres, to the first spot with room
                for (int k = 1; hit == null && k <= 16; k++) { at += f * 0.25f; hit = DebugBridge.StandHit(at.x, at.z, null); }
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
            if (a[0] == "drive" && a.Length > 1)
            {
                // how a car's engine reaches the road (ifadrive.py, the IFA "doesn't drive well"): the driven wheel slots and
                // colliders, each wheel's setup and what it does now. `mount drive <car>`
                var root = MountRootOf(uint.Parse(a[1], ic));
                var cs = root != null ? root.car : null;
                if (cs == null) return "{\"error\":\"no such car\"}";
                string F(float v) => v.ToString("F3", ic);
                string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
                string Wh(WheelCollider w)
                {
                    if (w == null) return "null";
                    var rb = w.attachedRigidbody; var ff = w.forwardFriction; var sf = w.sidewaysFriction; var sp = w.suspensionSpring;
                    w.GetGroundHit(out var gh);
                    return "{\"name\":" + Json.Str(w.transform.parent != null ? w.transform.parent.name + "/" + w.name : w.name) + ",\"en\":" + (w.enabled && w.gameObject.activeInHierarchy ? 1 : 0) +
                           ",\"lp\":" + V(cs.transform.InverseTransformPoint(w.transform.TransformPoint(w.center))) + ",\"r\":" + F(w.radius) + ",\"m\":" + F(w.mass) +
                           ",\"rb\":" + Json.Str(rb == null ? "none" : rb == cs.RB ? "car" : rb.name) + ",\"susp\":" + F(w.suspensionDistance) + ",\"spring\":" + F(sp.spring) + ",\"damp\":" + F(sp.damper) +
                           ",\"ff\":[" + F(ff.extremumSlip) + "," + F(ff.extremumValue) + "," + F(ff.asymptoteSlip) + "," + F(ff.asymptoteValue) + "," + F(ff.stiffness) + "]" +
                           ",\"sf\":" + F(sf.stiffness) + ",\"motor\":" + F(w.motorTorque) + ",\"brake\":" + F(w.brakeTorque) + ",\"rpm\":" + F(w.rpm) + ",\"steer\":" + F(w.steerAngle) +
                           ",\"ground\":" + (w.isGrounded ? 1 : 0) + ",\"fslip\":" + F(gh.forwardSlip) + ",\"sslip\":" + F(gh.sidewaysSlip) + ",\"load\":" + F(gh.force) + ",\"sprung\":" + F(w.sprungMass) + "}";
                }
                string List(IEnumerable<WheelCollider> l) { var r = new List<string>(); if (l != null) foreach (var w in l) r.Add(Wh(w)); return "[" + string.Join(",", r) + "]"; }
                var listed = new HashSet<WheelCollider>();
                foreach (var l in new[] { cs.WHDriven, cs.WHSteer, cs.WHInvSteer, cs.WHBrakeOnly, cs.WHHandBrake }) if (l != null) foreach (var w in l) if (w != null) listed.Add(w);
                var other = new List<WheelCollider>();
                foreach (var w in cs.GetComponentsInChildren<WheelCollider>(true)) if (!listed.Contains(w)) other.Add(w);
                var slots = new List<string>();
                if (cs.DrivenWheelSlots != null) foreach (var s in cs.DrivenWheelSlots)
                        slots.Add(s == null ? "null" : "[" + Json.Str(s.name) + "," + (s.hasPart() ? 1 : 0) + "," + Json.Str(s.hasPart() ? s.part().name : "") + "," + V(cs.transform.InverseTransformPoint(s.transform.position)) + "]");
                var graphics = new List<string>();
                foreach (var wg in cs.GetComponentsInChildren<wheelgraphicsscript>(true))
                    graphics.Add("[" + Json.Str(wg.name) + "," + Json.Str(wg.W != null ? wg.W.name : "") + "," + Json.Str(wg.W2 != null ? wg.W2.name : "") + "," + Json.Str(wg.T != null ? wg.T.name : "") + "]");
                var forgo = new List<string>();
                if (cs.forgo_kerek != null) foreach (var f in cs.forgo_kerek) if (f != null) forgo.Add("[" + Json.Str(f.T != null ? f.T.name : "null") + "," + V(f.axis) + "," + F(f.modifier) + "]");
                var gears = new List<string>();
                if (cs.gears != null) foreach (var g in cs.gears) if (g != null) gears.Add(F(g.ratio));
                var eng = cs.Engine;
                return "{\"car\":" + Json.Str(cs.name) + ",\"mass\":" + F(cs.RB != null ? cs.RB.mass : 0f) + ",\"com\":" + V(cs.RB != null ? cs.RB.centerOfMass : Vector3.zero) +
                       ",\"diff\":" + F(cs.differentialRatio) + ",\"gears\":[" + string.Join(",", gears) + "],\"gear\":" + cs.gear + ",\"selectedgr\":" + F(cs.selectedgr) +
                       ",\"drivenNum\":" + cs.DrivenWheelsNum() + ",\"avgRpm\":" + F(cs.avgRpm) + ",\"speed\":" + F(cs.speed) + ",\"throttle\":" + F(cs.throttle) +
                       ",\"engine\":" + (eng == null ? "null" : "{\"running\":" + (eng.running ? 1 : 0) + ",\"rpm\":" + F(eng.rpm) + ",\"out\":" + F(eng.actualOutput) + ",\"brakeOut\":" + F(eng.brakeOutput) + "}") +
                       ",\"drivenSlots\":[" + string.Join(",", slots) + "],\"driven\":" + List(cs.WHDriven) + ",\"steer\":" + List(cs.WHSteer) + ",\"invSteer\":" + List(cs.WHInvSteer) +
                       ",\"brakeOnly\":" + List(cs.WHBrakeOnly) + ",\"handBrake\":" + List(cs.WHHandBrake) + ",\"unlisted\":" + List(other) +
                       ",\"graphics\":[" + string.Join(",", graphics) + "],\"forgoKerek\":[" + string.Join(",", forgo) + "]}";
            }
            if (a[0] == "tow" && a.Length > 1)
            {
                // a hitched trailer and what holds it up (trailerhitch.py, "the trailer pulls its hitch down"): its body, both
                // joints of the chain trailer → drawbar → car, where each joint's two ends are now (they should meet), and
                // how the trailer and the car sit. `mount tow <trailer>`
                var tr = MountRootOf(uint.Parse(a[1], ic));
                if (tr == null) return "{\"error\":\"no such object\"}";
                string F(float v) => v.ToString("F3", ic);
                string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
                string Jt(Joint j)
                {
                    if (j == null) return "null";
                    var self = j.transform; var cb = j.connectedBody;
                    var p0 = self.TransformPoint(j.anchor);
                    var p1 = cb != null ? cb.transform.TransformPoint(j.connectedAnchor) : j.connectedAnchor;
                    string s = "{\"type\":" + Json.Str(j.GetType().Name) + ",\"on\":" + Json.Str(self.name) + ",\"to\":" + Json.Str(cb != null ? cb.name : "world") +
                               ",\"anchor\":" + V(j.anchor) + ",\"connAnchor\":" + V(j.connectedAnchor) + ",\"auto\":" + (j.autoConfigureConnectedAnchor ? 1 : 0) +
                               ",\"gap\":" + V(p1 - p0) + ",\"gapM\":" + F((p1 - p0).magnitude) + ",\"ms\":" + F(j.massScale) + ",\"cms\":" + F(j.connectedMassScale) +
                               ",\"preproc\":" + (j.enablePreprocessing ? 1 : 0) + ",\"force\":" + V(j.currentForce);
                    if (j is ConfigurableJoint c)
                        s += ",\"motion\":\"" + c.xMotion + "/" + c.yMotion + "/" + c.zMotion + " " + c.angularXMotion + "/" + c.angularYMotion + "/" + c.angularZMotion + "\"" +
                             ",\"proj\":" + Json.Str(c.projectionMode + " " + F(c.projectionDistance) + " " + F(c.projectionAngle)) + ",\"limit\":" + F(c.linearLimit.limit) +
                             ",\"limitSpring\":" + F(c.linearLimitSpring.spring) + ",\"yDrive\":" + F(c.yDrive.positionSpring) + ",\"slerp\":" + F(c.slerpDrive.positionSpring) +
                             ",\"axis\":" + V(c.axis) + ",\"sec\":" + V(c.secondaryAxis);
                    return s + "}";
                }
                var rb = tr.GetComponent<Rigidbody>();
                var ut = tr.GetComponent<utanfutoscript>();
                var ag = tr.GetComponent<attachgripscript>() ?? tr.GetComponentInChildren<attachgripscript>(true);
                var scm = tr.GetComponent<setcenterofmass>();
                var grip = tr.attachable;
                var grb = grip != null ? grip.GetComponent<Rigidbody>() : null;
                var trWheels = new List<string>();
                foreach (var w in tr.GetComponentsInChildren<WheelCollider>(true))
                {
                    w.GetGroundHit(out var gh);
                    trWheels.Add("[" + Json.Str(w.name) + "," + (w.isGrounded ? 1 : 0) + "," + F(gh.force) + "," + F(w.suspensionDistance) + "," + F(w.suspensionSpring.spring) + "," + V(tr.transform.InverseTransformPoint(w.transform.TransformPoint(w.center))) + "]");
                }
                tosaveitemscript car = grip != null && grip.point != null ? grip.point.root.GetComponent<tosaveitemscript>() : null;
                var carWheels = new List<string>();
                if (car != null)
                    foreach (var w in car.GetComponentsInChildren<WheelCollider>(true))
                    {
                        if (!w.enabled || !w.gameObject.activeInHierarchy) continue;
                        w.GetGroundHit(out var gh);
                        w.GetWorldPose(out var wp, out _);
                        float frac = Vector3.Dot(w.transform.TransformPoint(w.center) - wp, w.transform.up) / Mathf.Max(w.suspensionDistance, 0.01f);
                        carWheels.Add("[" + Json.Str(w.name) + "," + F(car.transform.InverseTransformPoint(w.transform.position).z) + "," + (w.isGrounded ? 1 : 0) + "," + F(gh.force) + "," + F(frac) + "]");
                    }
                string Body(Rigidbody b) => b == null ? "null" : "{\"mass\":" + F(b.mass) + ",\"com\":" + V(b.centerOfMass) + ",\"comW\":" + V(tr.transform.InverseTransformPoint(b.worldCenterOfMass)) +
                                                                   ",\"kin\":" + (b.isKinematic ? 1 : 0) + ",\"v\":" + F(b.velocity.magnitude) + ",\"inertia\":" + V(b.inertiaTensor) + "}";
                return "{\"trailer\":" + Json.Str(tr.name) + ",\"body\":" + Body(rb) +
                       ",\"pitch\":" + F(Mathf.Asin(Mathf.Clamp(tr.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg) + ",\"roll\":" + F(Mathf.Asin(Mathf.Clamp(tr.transform.right.y, -1f, 1f)) * Mathf.Rad2Deg) +
                       ",\"comFree\":" + (ut != null && ut.CenterOfMass != null ? V(tr.transform.InverseTransformPoint(ut.CenterOfMass.position)) : "null") +
                       ",\"comHitched\":" + (ut != null && ut.CenterOfMassAttached != null ? V(tr.transform.InverseTransformPoint(ut.CenterOfMassAttached.position)) : "null") +
                       ",\"comScript\":" + (scm != null && scm.T != null ? V(scm.T.localPosition) + ",\"comScriptParent\":" + Json.Str(scm.T.parent != null ? scm.T.parent.name : "") : "null") +
                       ",\"utAttached\":" + (ut != null && ut.attached ? 1 : 0) + ",\"brake\":" + (ut != null && ut.brake != null ? ut.brake.currentTurnState : -1) +
                       ",\"gripPos\":" + (ag != null && ag.GripPos != null ? V(tr.transform.InverseTransformPoint(ag.GripPos.position)) : "null") + ",\"fixGripPos\":" + (ag != null && ag.fixGripPos ? 1 : 0) + ",\"vGripPos\":" + (ag != null ? V(ag.VgripPos) : "null") +
                       ",\"trailerJoint\":" + Jt(ag != null ? ag.J : null) + ",\"allTrailerJoints\":" + tr.GetComponentsInChildren<Joint>(true).Length +
                       ",\"grip\":" + (grip == null ? "null" : "{\"name\":" + Json.Str(grip.name) + ",\"attached\":" + (grip.attached ? 1 : 0) + ",\"lp\":" + V(tr.transform.InverseTransformPoint(grip.transform.position)) +
                                       ",\"toPoint\":" + (grip.point != null ? V(grip.point.position - grip.transform.position) : "null") + ",\"body\":" + Body(grb) + ",\"joint\":" + Jt(grip.J) + "}") +
                       ",\"car\":" + (car == null ? "null" : "{\"name\":" + Json.Str(car.name) + ",\"pitch\":" + F(Mathf.Asin(Mathf.Clamp(car.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg) +
                                      ",\"ball\":" + V(car.transform.InverseTransformPoint(grip.point.position)) + ",\"trailerAtCar\":" + V(car.transform.InverseTransformPoint(tr.transform.position)) +
                                      ",\"mass\":" + F(car.GetComponent<Rigidbody>() != null ? car.GetComponent<Rigidbody>().mass : 0f) + ",\"wheels(name,z,grounded,load,compress)\":[" + string.Join(",", carWheels) + "]}") +
                       ",\"wheels(name,grounded,load,susp,spring,lp)\":[" + string.Join(",", trWheels) + "]}";
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
            return "{\"error\":\"mount slots <car> | off <car> <slot> | on <part> <car> <slot> | pose <part> <car> <slot> | putin <item> <car> | held <net>\"}";
        }
    }
}
