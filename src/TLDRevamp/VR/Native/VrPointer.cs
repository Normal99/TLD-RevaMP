using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.VR.Native
{
    /// The game's interaction ray, from the hand instead of the head.
    ///
    /// Everything the player does to the world in flat play goes through one ray from the camera: fpscontroller.RayStuff
    /// casts `Cam.transform.position` along `Cam.transform.forward` to find what to pick up, use, attach, detach, push
    /// or sit on, and LookPointCalc casts the same from `mainCam` for the look point (the 3D crosshair, the hand that
    /// points). In VR the camera is the head, so you had to look at a door to open it.
    ///
    /// The transpiler swaps exactly those `camera.transform` reads in those methods for VrPointer.Ray(camera), which
    /// is the camera's own transform unless native VR runs, and then a transform that sits on the pointing hand's
    /// controller. Nothing else about the game's rules changes: the reach (FrayRange), the layers, what counts as usable.
    internal static class VrPointer
    {
        private static Transform _t;
        internal static int Redirected;

        /// The hand ray's transform (position on the controller, forward along it), or null before the first frame.
        internal static Transform T => _t;

        /// True while the ray really comes from a hand.
        internal static bool Live { get; private set; }

        internal static void Update(int hand)
        {
            var pose = VrHands.Pose(hand);
            if (!pose.Valid) pose = VrHands.Pose(1 - hand);
            if (!pose.Valid) { Live = false; return; }
            if (_t == null)
            {
                var go = new GameObject("TLDRevamp VR pointer") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                _t = go.transform;
            }
            _t.SetPositionAndRotation(pose.Pos, pose.Rot);
            Live = true;
        }

        /// The transform a game interaction ray starts from.
        public static Transform Ray(Camera camera)
        {
            if (Live && _t != null && VrNative.Hands && VrNative.Active && !VrNative.MenuOpen)
            {
                Redirected++;
                return _t;
            }
            return camera.transform;
        }

        private static readonly MethodInfo GetTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo RayMethod = AccessTools.Method(typeof(VrPointer), nameof(Ray));
        private static readonly FieldInfo CamField = AccessTools.Field(typeof(fpscontroller), nameof(fpscontroller.Cam));
        private static readonly FieldInfo MainCamField = AccessTools.Field(typeof(fpscontroller), nameof(fpscontroller.mainCam));

        /// `ldfld Cam|mainCam ; callvirt Component::get_transform` -> `ldfld Cam|mainCam ; call VrPointer.Ray`.
        internal static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, string method)
        {
            var list = new List<CodeInstruction>(instructions);
            int swapped = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (!(list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) || !Equals(list[i].operand, GetTransform)) continue;
                var prev = list[i - 1];
                if (prev.opcode != OpCodes.Ldfld) continue;
                if (!Equals(prev.operand, CamField) && !Equals(prev.operand, MainCamField)) continue;
                list[i].opcode = OpCodes.Call;
                list[i].operand = RayMethod;
                swapped++;
            }
            Plugin.Log.LogInfo("VR pointer: " + method + ": " + swapped + " camera ray reads now come from the pointing hand in VR");
            if (swapped == 0) throw new System.InvalidOperationException("VR pointer: no camera ray found in " + method + " (game changed?)");
            return list;
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class RayStuffSeam
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Swap(instructions, "RayStuff");
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.LookPointCalc))]
        private static class LookPointSeam
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Swap(instructions, "LookPointCalc");
        }
    }
}
