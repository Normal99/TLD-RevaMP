using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace TLDRevamp.VR.Native
{
    /// Both hands as the way you play (docs/VR-NATIVE.md §2).
    ///
    ///   grip      grabs what the hand touches, or pulls in what its ray points at within the game's reach. The item
    ///             follows that hand through the game's own hold (its physics spring, its weight), and letting go
    ///             drops it, or throws it with the hand's speed.
    ///   trigger   on a free hand: the game's "use" (doors, buttons, seats, keys, switches) at what that hand points at.
    ///             On the hand that holds an item: the game's "use item" (drink, eat, pour, fire).
    ///   A / X     the game's "mount": attach the held part where the hand points, or detach the part it points at
    ///             (the detached part lands in that hand).
    ///
    /// Everything happens through the game's own calls (fpscontroller.Pickup / Drop, inputscript's use / lmb / mount
    /// and the game's own interaction code), so the rules of what may be picked up, attached or used, the inventory,
    /// saving and the multiplayer sync stay the game's. The only things this adds are where the hold point is (the
    /// hand instead of in front of the face), where the interaction ray starts (the hand instead of the head, see
    /// VrPointer) and which controller button is which.
    internal static class VrHands
    {
        internal const int Left = VrControlMap.Left, Right = VrControlMap.Right, None = VrControlMap.None;

        private static readonly VrHandButtons[] _buttons = { new VrHandButtons(), new VrHandButtons() };
        private static readonly VrControlMap _map = new VrControlMap();
        private static VrGameInput _last;

        // what is held, by which hand, and where it sits in that hand
        private static pickupable _held;
        private static int _holder = None;
        private static VrHoldMode _mode;
        private static Vector3 _offPos;
        private static Quaternion _offRot = Quaternion.identity;
        private static bool[] _grabbedThisPress = new bool[2];

        // the hand's recent path, for the throw
        private const int History = 8;
        private static readonly Vector3[,] _path = new Vector3[2, History];
        private static readonly float[,] _pathT = new float[2, History];
        private static readonly int[] _pathN = new int[2];

        // counters for the bridge and the tests
        private static int _grabs, _pulls, _handovers, _drops, _throws, _adopted, _uses, _itemUses, _mounts, _faults;
        private static float _lastThrowSpeed;
        private static string _lastGrab = "", _lastFault = "";

        internal static int Holder => _holder;
        internal static int Pointer => _map.Pointer;

        // ---- the controllers

        internal struct HandPose
        {
            public bool Valid;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private static handControllerInputScript Controller(int h)
        {
            var hs = vrHandsScript.s;
            if (hs == null) return null;
            return h == Left ? hs.LH : hs.RH;
        }

        private static vrInteractHandScript Interact(int h)
        {
            var hs = vrHandsScript.s;
            if (hs == null) return null;
            return h == Left ? hs.vrLH : hs.vrRH;
        }

        /// The tracked pose of the controller (the transform the game's own menu laser starts from).
        internal static HandPose Pose(int h)
        {
            if (VrSim.On && VrSim.Hand[h]) return new HandPose { Valid = true, Pos = VrSim.Pos[h], Rot = VrSim.Rot[h] };
            var c = Controller(h);
            if (c == null || c.real == null || !c.real.gameObject.activeInHierarchy) return default;
            return new HandPose { Valid = true, Pos = c.real.position, Rot = c.real.rotation };
        }

        /// The palm: the centre of the game's own interaction sphere of that hand, or a little in front of the grip.
        internal static Vector3 Palm(int h, HandPose pose, out float radius)
        {
            radius = 0.09f;
            if (!(VrSim.On && VrSim.Hand[h]))
            {
                var ih = Interact(h);
                if (ih != null && ih.col != null)
                {
                    var t = ih.col.transform;
                    float scale = Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Max(Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z)));
                    radius = Mathf.Clamp(ih.col.radius * scale, 0.06f, 0.16f);
                    return t.TransformPoint(ih.col.center);
                }
            }
            return pose.Pos + pose.Rot * new Vector3(0f, -0.02f, 0.04f);
        }

        private static void ReadButtons(int h)
        {
            if (VrSim.On && VrSim.Hand[h])
            {
                _buttons[h].Feed(VrSim.Trigger[h], VrSim.Grip[h], VrSim.Primary[h]);
                return;
            }
            var c = Controller(h);
            if (c == null || !Pose(h).Valid) { _buttons[h].Clear(); return; }
            _buttons[h].Feed(c.triggerButtonHold, c.gripButtonHold, c.button1Hold);
        }

        private static void RecordPath(int h)
        {
            var pose = Pose(h);
            if (!pose.Valid) { _pathN[h] = 0; return; }
            for (int i = History - 1; i > 0; i--) { _path[h, i] = _path[h, i - 1]; _pathT[h, i] = _pathT[h, i - 1]; }
            _path[h, 0] = Palm(h, pose, out _);
            _pathT[h, 0] = Time.unscaledTime;
            if (_pathN[h] < History) _pathN[h]++;
        }

        /// The hand's world velocity over the last ~60 ms (the body's own movement included).
        internal static Vector3 Velocity(int h)
        {
            int n = _pathN[h];
            if (n < 2) return Vector3.zero;
            int k = 1;
            while (k < n - 1 && _pathT[h, 0] - _pathT[h, k] < 0.06f) k++;
            float dt = _pathT[h, 0] - _pathT[h, k];
            if (dt <= 1e-4f) return Vector3.zero;
            return (_path[h, 0] - _path[h, k]) / dt;
        }

        // ---- the frame: after the game has read its input, before the player acts on it

        /// inputscript.Update postfix: the game's VR block has read the controllers into its input fields; this decides
        /// what the buttons mean and writes the game's input, then acts on grabs and releases.
        internal static void Tick(inputscript input)
        {
            if (!VrNative.Hands || !VrNative.Active || input == null) { ResetIfIdle(); return; }
            var pl = VrNative.Player;
            if (pl == null) { ResetIfIdle(); return; }

            for (int h = 0; h < 2; h++) { ReadButtons(h); RecordPath(h); }
            SyncHeld(pl);

            bool menu = VrNative.MenuOpen;
            var gi = _map.Map(_buttons[Left], _buttons[Right], _holder, menu);
            _last = gi;
            VrPointer.Update(gi.Pointer);
            if (menu) return;

            if (VrNative.Headset) RestoreWalk(input, pl);

            input.use = gi.Use; input.useDown = gi.UseDown; input.useUp = gi.UseUp;
            input.lmb = gi.Lmb; input.lmbDown = gi.LmbDown; input.lmbUp = gi.LmbUp;
            input.mount = gi.Mount; input.mountDown = gi.MountDown; input.mountUp = gi.MountUp;
            // the hands own the pickup: the game's own pick-up/drop/throw key path stays out of it
            input.pickup = input.pickupDown = input.pickupUp = false;
            if (gi.UseDown) _uses++;
            if (gi.LmbDown) _itemUses++;
            if (gi.MountDown) _mounts++;

            if (gi.Release && _mode == VrHoldMode.Grip) Release(pl);
            if (gi.GrabRight == 1) Grab(pl, Right);
            if (gi.GrabLeft == 1) Grab(pl, Left);
            for (int h = 0; h < 2; h++) if (_buttons[h].Grip.Up) _grabbedThisPress[h] = false;
        }

        /// The game's VR block turns A/X held into "use held": the sticks then look instead of walking (a flat-play
        /// habit of holding E while turning a knob with the mouse). A/X is "mount" now, so the sticks keep walking.
        private static void RestoreWalk(inputscript input, fpscontroller pl)
        {
            var hs = vrHandsScript.s;
            if (hs == null || hs.LH == null || hs.RH == null) return;
            if (!hs.LH.button1Hold && !hs.RH.button1Hold) return;
            Vector2 l = hs.LH.stick, r = hs.RH.stick;
            input.forward = Mathf.Clamp(l.y, 0f, 1f);
            input.backward = Mathf.Clamp(-l.y, 0f, 1f);
            input.right = Mathf.Clamp(l.x, 0f, 1f);
            input.left = Mathf.Clamp(-l.x, 0f, 1f);
            if (pl.Car == null) { input.lookX = r.x; input.lookY = r.y; }
            else { input.lookX = 0f; input.lookY = 0f; }
            input.BGas = Mathf.Clamp(l.y, 0f, 1f);
            input.BBrake = Mathf.Clamp(-l.y, 0f, 1f);
            input.BSteer = r.x;
        }

        private static void ResetIfIdle()
        {
            if (_holder == None && _held == null) return;
            _holder = None; _held = null;
            _buttons[0].Clear(); _buttons[1].Clear();
            _map.Reset();
        }

        /// What the game holds and what the hands think they hold must agree: the game drops on its own (too far
        /// from the hold point, eaten, mounted, stored) and picks up on its own (a part detached with A/X).
        private static void SyncHeld(fpscontroller pl)
        {
            var p = pl.pickedUp;
            if (p == null) { _held = null; _holder = None; return; }
            if (ReferenceEquals(p, _held) && _holder != None) return;
            // the game put something in the hand (a detach, the inventory, a flat-screen key): the pointing hand takes it
            int h = _map.Pointer;
            var pose = Pose(h);
            if (!pose.Valid) { h = 1 - h; pose = Pose(h); }
            if (!pose.Valid) { _held = p; _holder = None; return; }
            var pp = PickedUpPoint(pl);
            Vector3 grab = pp != null && pp.parent == p.transform ? pp.position : Center(p);
            Hold(p, h, pose, grab, VrHoldMode.Sticky);
            _adopted++;
        }

        private static readonly AccessTools.FieldRef<fpscontroller, Transform> _pickedUpPoint =
            AccessTools.FieldRefAccess<fpscontroller, Transform>("pickedUpPoint");
        private static Transform PickedUpPoint(fpscontroller pl) { try { return _pickedUpPoint(pl); } catch { return null; } }

        private static void Grab(fpscontroller pl, int h)
        {
            var pose = Pose(h);
            if (!pose.Valid) return;
            var palm = Palm(h, pose, out float radius);

            if (_held != null && pl.pickedUp == _held)
            {
                if (h == _holder)
                {
                    if (_mode == VrHoldMode.Sticky) { _mode = VrHoldMode.Grip; _grabbedThisPress[h] = true; }
                    return;
                }
                // the other hand takes it over when it touches the item
                if (Touches(palm, radius, _held, out Vector3 at))
                {
                    Hold(_held, h, pose, at, VrHoldMode.Grip);
                    _grabbedThisPress[h] = true;
                    _handovers++;
                    Buzz(h, 0.35f, 0.04f);
                }
                return;
            }

            bool pulled = false;
            var p = Near(pl, palm, radius, out Vector3 point);
            if (p == null) { p = Far(pl, out point); pulled = p != null; }
            if (p == null) return;

            pl.Pickup(p, point);
            if (pl.pickedUp != p) return;   // the game refused it
            Hold(p, h, pose, point, VrHoldMode.Grip);
            _grabbedThisPress[h] = true;
            if (pulled) _pulls++; else _grabs++;
            _lastGrab = (pulled ? "pull " : "grab ") + (h == Left ? "left " : "right ") + p.name;
            Buzz(h, pulled ? 0.25f : 0.4f, 0.05f);
        }

        /// The item sits in the hand as it was when grabbed, moved so the spot that was touched (or pointed at) is
        /// in the palm.
        private static void Hold(pickupable p, int h, HandPose pose, Vector3 grabPoint, VrHoldMode mode)
        {
            _held = p;
            _holder = h;
            _mode = mode;
            var inv = Quaternion.Inverse(pose.Rot);
            var itemRot = p.transform.rotation;
            _offRot = inv * itemRot;
            Vector3 palmLocal = inv * (Palm(h, pose, out _) - pose.Pos);
            Vector3 grabLocal = Quaternion.Inverse(itemRot) * (grabPoint - p.transform.position);
            _offPos = palmLocal - _offRot * grabLocal;
        }

        private static void Release(fpscontroller pl)
        {
            var p = _held;
            int h = _holder;
            if (p == null || h == None) { _held = null; _holder = None; return; }
            Vector3 v = Velocity(h);
            if (pl.pickedUp == p) pl.Drop();
            _held = null;
            _holder = None;
            float speed = VrThrow.Speed(v.magnitude);
            if (speed > 0f && p != null && p.RB != null && !p.RB.isKinematic)
            {
                p.RB.velocity = v.normalized * speed;
                _throws++;
                _lastThrowSpeed = speed;
            }
            else _drops++;
        }

        /// fpscontroller.PickedUpForceAdd prefix: the game pulls the held item towards its hold point with its own
        /// spring; the hold point is the hand now.
        internal static bool PlaceHoldPoint(fpscontroller pl)
        {
            if (!VrNative.Hands || !VrNative.Active || pl == null || _held == null || _holder == None) return false;
            if (!ReferenceEquals(pl.pickedUp, _held) || pl.TpickedUpParent == null) return false;
            var pose = Pose(_holder);
            if (!pose.Valid) return false;
            var rot = pose.Rot * _offRot;
            var pos = pose.Pos + pose.Rot * _offPos;
            pl.TpickedUpParent.SetPositionAndRotation(pos, rot);
            return true;
        }

        // ---- what a hand can take

        private static readonly Collider[] _overlap = new Collider[256];

        private static bool CanTake(fpscontroller pl, pickupable p)
        {
            if (p == null || p.tosave == null) return false;
            if (p.tosave.attachable != null && p.tosave.attachable.slot != null) return false;   // mounted: A/X detaches it
            if (!p.CanPickup() || p.cantPickupWhileMount) return false;
            return pl.CanPickupSit(p);
        }

        /// A loose item: not a piece of something else (a bike's mirror is pickupable by the game's rules, but its
        /// parent holds it and the game drops it again at once).
        internal static bool Loose(pickupable p) => p.transform.parent == null && p.GetComponent<Joint>() == null;

        /// The nearest item the palm touches; a loose item before a piece of something else.
        private static pickupable Near(fpscontroller pl, Vector3 palm, float radius, out Vector3 point)
        {
            point = palm;
            bool bestLoose = false;
            int n = Physics.OverlapSphereNonAlloc(palm, radius, _overlap, pl.pickupLayer, QueryTriggerInteraction.Ignore);
            pickupable best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c == null) continue;
                var p = c.GetComponentInParent<pickupable>();
                if (p == null || ReferenceEquals(p, pl.inHandP) || !CanTake(pl, p)) continue;
                Vector3 q = Closest(c, palm);
                float d = (q - palm).sqrMagnitude;
                bool loose = Loose(p);
                if (loose && !bestLoose || loose == bestLoose && d < bestD) { bestD = d; best = p; point = q; bestLoose = loose; }
            }
            return best;
        }

        /// The item the hand's ray points at, within the game's own reach.
        private static pickupable Far(fpscontroller pl, out Vector3 point)
        {
            point = Vector3.zero;
            var t = VrPointer.T;
            if (t == null) return null;
            if (!Physics.Raycast(t.position, t.forward, out var hit, pl.FrayRange, pl.pickupLayer, QueryTriggerInteraction.Ignore)) return null;
            var p = hit.collider.GetComponentInParent<pickupable>();
            if (p == null || ReferenceEquals(p, pl.inHandP) || !CanTake(pl, p)) return null;
            point = hit.point;
            return p;
        }

        private static bool Touches(Vector3 palm, float radius, pickupable p, out Vector3 at)
        {
            at = palm;
            int n = Physics.OverlapSphereNonAlloc(palm, radius * 1.5f, _overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c != null && c.transform.IsChildOf(p.transform)) { at = Closest(c, palm); return true; }
            }
            return false;
        }

        private static Vector3 Closest(Collider c, Vector3 to)
        {
            try
            {
                if (c is MeshCollider mc && !mc.convex) return c.bounds.ClosestPoint(to);
                return c.ClosestPoint(to);
            }
            catch { return c.bounds.ClosestPoint(to); }
        }

        private static Vector3 Center(pickupable p)
        {
            var cols = p.GetComponentsInChildren<Collider>();
            if (cols == null || cols.Length == 0) return p.transform.position;
            var b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
            return b.center;
        }

        private static void Buzz(int h, float amplitude, float seconds)
        {
            try
            {
                var d = InputDevices.GetDeviceAtXRNode(h == Left ? XRNode.LeftHand : XRNode.RightHand);
                if (d.isValid) d.SendHapticImpulse(0, amplitude, seconds);
            }
            catch { }
        }

        // ---- the game's own grab of doors and levers (vrInteractHandScript) keeps out of a hand that holds an item

        [HarmonyPatch(typeof(vrInteractHandScript), "Update")]
        private static class InteractSeam
        {
            private static bool Prefix(vrInteractHandScript __instance)
            {
                if (__instance == null || !VrNative.Hands || !VrNative.Active) return true;
                var hs = vrHandsScript.s;
                if (hs == null) return true;
                int h = ReferenceEquals(__instance, hs.vrLH) ? Left : ReferenceEquals(__instance, hs.vrRH) ? Right : None;
                if (h == None) return true;
                if (h != _holder && !_grabbedThisPress[h]) return true;
                __instance.use = null;
                return false;
            }
        }

        [HarmonyPatch(typeof(inputscript), "Update")]
        private static class InputSeam
        {
            // before the seated rule (VrContractBridge's postfix), so a seated player's walk is still taken away
            [HarmonyPriority(Priority.High)]
            private static void Postfix(inputscript __instance)
            {
                try { Tick(__instance); }
                catch (Exception e) { Fault("tick", e); }
            }
        }

        /// The game's spring is made for a hold point that moves with the head: it moves an item at most 6 m/s
        /// (maxPickupForce, less for heavy ones), and drops it once it lags 1.25 m behind (dropDist). A hand swings
        /// faster than that, so a held item fell out of the hand halfway through every throw. While a hand holds it,
        /// the item moves with the hand: the hand's own velocity, plus the spring for what is left between the grabbed
        /// spot and the hand, up to HandMaxSpeed for a light item and the game's speed for the heaviest. The game's
        /// drop still comes, for an item pinned somewhere, at three times its distance.
        internal const float HandMaxSpeed = 25f;
        private const float DropDistScale = 3f;

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.PickedUpForceAdd))]
        private static class HoldSeam
        {
            private static void Prefix(fpscontroller __instance, out float __state)
            {
                __state = -1f;
                try
                {
                    if (!PlaceHoldPoint(__instance)) return;
                    __state = __instance.dropDist;
                    __instance.dropDist = __state * DropDistScale;
                }
                catch (Exception e) { Fault("hold", e); }
            }

            private static void Postfix(fpscontroller __instance, float __state)
            {
                if (__state < 0f) return;
                var pl = __instance;
                try
                {
                    var p = pl.pickedUp;
                    if (p == null || p.RB == null || p.RB.isKinematic || !ReferenceEquals(p, _held) || _holder == None || pl.TpickedUp == null) return;
                    var pose = Pose(_holder);
                    if (!pose.Valid) return;
                    Vector3 target = pose.Pos + pose.Rot * _offPos;
                    Vector3 v = Velocity(_holder) + (target - pl.TpickedUp.position) * pl.pickupForceM;
                    float max = Mathf.Lerp(pl.maxPickupForce, HandMaxSpeed, pl.weightM);
                    p.RB.velocity = Vector3.ClampMagnitude(v, max);
                }
                catch (Exception e) { Fault("hold", e); }
            }

            // the game's drop distance comes back even when its method throws
            private static Exception Finalizer(fpscontroller __instance, float __state, Exception __exception)
            {
                if (__state >= 0f && __instance != null) __instance.dropDist = __state;
                return __exception;
            }
        }

        // ---- the laser: the game's own menu laser, shown in play from the pointing hand to what it can reach

        internal static void LateTick()
        {
            var hs = vrHandsScript.s;
            if (hs == null || !VrNative.Hands || !VrNative.Headset || VrNative.MenuOpen) return;
            var pl = VrNative.Player;
            var t = VrPointer.T;
            if (pl == null || t == null) return;
            int pointer = _map.Pointer;
            for (int h = 0; h < 2; h++)
            {
                var c = Controller(h);
                if (c == null || c.laserT == null || c.laser == null) continue;
                bool show = false;
                if (h == pointer && Physics.Raycast(t.position, t.forward, out var hit, pl.FrayRange, pl.useLayer | pl.pickupLayer, QueryTriggerInteraction.Ignore))
                {
                    show = !(_held != null && hit.collider.transform.IsChildOf(_held.transform));
                    if (show) c.laser.SetLaserEnd(hit.point);
                }
                if (c.laserT.gameObject.activeSelf != show) c.laserT.gameObject.SetActive(show);
            }
        }

        /// The items around the player a hand could take, for tests: name, where, and whether the game would let go of it.
        internal static string ItemsJson(float radius)
        {
            var pl = VrNative.Player;
            if (pl == null) return "{\"error\":\"no player\"}";
            var b = new StringBuilder(1024);
            b.Append("{\"player\":").Append(VrNative.V(pl.transform.position));
            if (pl.Cam != null) b.Append(",\"head\":").Append(VrNative.V(pl.Cam.transform.position)).Append(",\"look\":").Append(VrNative.V(pl.Cam.transform.forward));
            b.Append(",\"items\":[");
            var hits = Physics.OverlapSphere(pl.transform.position, radius, pl.pickupLayer, QueryTriggerInteraction.Ignore);   // a test's query: every collider, however many
            var seen = new System.Collections.Generic.HashSet<int>();
            bool first = true;
            for (int i = 0; i < hits.Length; i++)
            {
                var p = hits[i] != null ? hits[i].GetComponentInParent<pickupable>() : null;
                if (p == null || !seen.Add(p.GetInstanceID())) continue;
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"name\":").Append(Json.Str(p.name)).Append(",\"pos\":").Append(VrNative.V(p.transform.position))
                 .Append(",\"center\":").Append(VrNative.V(Center(p))).Append(",\"take\":").Append(CanTake(pl, p) ? "true" : "false")
                 .Append(",\"mass\":").Append(p.RB != null ? VrNative.F(p.RB.mass) : "null")
                 .Append(",\"loose\":").Append(Loose(p) ? "true" : "false").Append('}');
            }
            b.Append("]}");
            return b.ToString();
        }

        internal static void Fault(string where, Exception e)
        {
            _faults++;
            _lastFault = where + ": " + e.GetType().Name + ": " + e.Message;
            if (_faults <= 5) Plugin.Log.LogWarning("VR hands " + _lastFault);
        }

        internal static void AppendJson(StringBuilder b)
        {
            var pl = VrNative.Player;
            b.Append("\"holder\":").Append(_holder == None ? "null" : _holder == Left ? "\"left\"" : "\"right\"");
            b.Append(",\"pointer\":").Append(_map.Pointer == Left ? "\"left\"" : "\"right\"");
            b.Append(",\"held\":").Append(_held != null ? Json.Str(_held.name) : "null");
            b.Append(",\"heldMode\":\"").Append(_mode).Append('"');
            b.Append(",\"gamePickedUp\":").Append(pl != null && pl.pickedUp != null ? Json.Str(pl.pickedUp.name) : "null");
            // the spot on the item the hand grabbed (the game's TpickedUp), and where the hand wants that spot now
            if (pl != null && pl.pickedUp != null) b.Append(",\"heldPos\":").Append(VrNative.V(pl.TpickedUp != null ? pl.TpickedUp.position : pl.pickedUp.transform.position));
            if (_holder != None)
            {
                var hp = Pose(_holder);
                if (hp.Valid) b.Append(",\"holdPoint\":").Append(VrNative.V(hp.Pos + hp.Rot * _offPos));
            }
            for (int h = 0; h < 2; h++)
            {
                var pose = Pose(h);
                b.Append(h == Left ? ",\"left\":{" : ",\"right\":{");
                b.Append("\"valid\":").Append(pose.Valid ? "true" : "false");
                if (pose.Valid)
                {
                    b.Append(",\"pos\":").Append(VrNative.V(pose.Pos));
                    b.Append(",\"palm\":").Append(VrNative.V(Palm(h, pose, out float r))).Append(",\"radius\":").Append(VrNative.F(r));
                    b.Append(",\"speed\":").Append(VrNative.F(Velocity(h).magnitude));
                }
                var bt = _buttons[h];
                b.Append(",\"trigger\":").Append(bt.Trigger.Hold ? "true" : "false");
                b.Append(",\"grip\":").Append(bt.Grip.Hold ? "true" : "false");
                b.Append(",\"primary\":").Append(bt.Primary.Hold ? "true" : "false");
                b.Append('}');
            }
            var input = inputscript.i;
            if (input != null)
                b.Append(",\"input\":{\"use\":").Append(input.use ? "true" : "false").Append(",\"lmb\":").Append(input.lmb ? "true" : "false")
                 .Append(",\"mount\":").Append(input.mount ? "true" : "false").Append('}');
            b.Append(",\"ray\":");
            var t = VrPointer.T;
            if (t != null) b.Append("{\"from\":").Append(VrNative.V(t.position)).Append(",\"dir\":").Append(VrNative.V(t.forward)).Append(",\"live\":").Append(VrPointer.Live ? "true" : "false").Append('}');
            else b.Append("null");
            b.Append(",\"counts\":{\"grabs\":").Append(_grabs).Append(",\"pulls\":").Append(_pulls).Append(",\"handovers\":").Append(_handovers)
             .Append(",\"drops\":").Append(_drops).Append(",\"throws\":").Append(_throws).Append(",\"adopted\":").Append(_adopted)
             .Append(",\"uses\":").Append(_uses).Append(",\"itemUses\":").Append(_itemUses).Append(",\"mounts\":").Append(_mounts)
             .Append(",\"rayCalls\":").Append(VrPointer.Redirected).Append(",\"faults\":").Append(_faults).Append('}');
            b.Append(",\"lastThrowSpeed\":").Append(VrNative.F(_lastThrowSpeed));
            b.Append(",\"lastGrab\":").Append(Json.Str(_lastGrab));
            b.Append(",\"lastFault\":").Append(Json.Str(_lastFault));
        }
    }
}
