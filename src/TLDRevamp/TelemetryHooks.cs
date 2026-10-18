using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    // Frame numbers keep a hook earlier in this Update from being attributed to the previous frame.
    [HarmonyPatch]
    internal static class TelemetryHooks
    {
        internal struct Events { public int Frame, Flags, Terrain, Roads, Scenery, Buildings; }
        private static readonly Events[] History = new Events[2];
        private static int _saves;
        internal static bool Saving => Volatile.Read(ref _saves) > 0;
        internal sealed class SaveRun { internal long Start; internal double St, T; internal bool Auto; internal Task Task; internal string Path, PlayLog; }
        private static SaveRun _save;
        private struct SaveResult { internal double Ms, St, T; internal bool Auto; internal long Bytes; internal string PlayLog; }
        private static readonly ConcurrentQueue<SaveResult> Saves = new ConcurrentQueue<SaveResult>();
        private static bool _auto;
        private static int Slot()
        {
            int f = Time.frameCount, i = f & 1;
            if (History[i].Frame != f) History[i] = new Events { Frame = f };
            return i;
        }
        internal static Events Previous => History[(Time.frameCount - 1) & 1].Frame == Time.frameCount - 1 ? History[(Time.frameCount - 1) & 1] : default;

        [HarmonyPatch(typeof(mainscript), nameof(mainscript.VisszaRakas)), HarmonyPrefix]
        private static void Rebase() { History[Slot()].Flags |= 1; }
        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.Upd)), HarmonyPrefix]
        private static void Stream() { History[Slot()].Flags |= 2; }
        [HarmonyPatch(typeof(terrainGenScript), "PlaceMesh"), HarmonyPostfix]
        private static void Terrain() { History[Slot()].Terrain++; }
        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void RoadBefore(roadGenScript.roadClass __instance, out bool __state) { __state = __instance.generated; }
        [HarmonyPatch(typeof(roadGenScript.roadClass), nameof(roadGenScript.roadClass.Generate)), HarmonyPostfix]
        private static void RoadAfter(roadGenScript.roadClass __instance, bool __state) { if (!__state && __instance.generated) RoadFinished(); }
        internal static void RoadFinished() { History[Slot()].Roads++; }
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.Spawn)), HarmonyPostfix]
        private static void Building(GameObject __result) { if (__result != null) History[Slot()].Buildings++; }

        // Count the completion assignment, not dictionary size: chunks can be removed in the same call.
        [HarmonyPatch(typeof(objGenScript.categoryClass), nameof(objGenScript.categoryClass.UpdGen)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Scenery(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(objGenScript.chunkClass), nameof(objGenScript.chunkClass.generated));
            var count = AccessTools.Method(typeof(TelemetryHooks), nameof(SceneryFinished));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field)) yield return new CodeInstruction(OpCodes.Call, count);
            }
        }
        internal static void SceneryFinished() { History[Slot()].Scenery++; }

        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.AutoSave)), HarmonyPrefix]
        private static void AutoBefore() { _auto = true; }
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.AutoSave)), HarmonyFinalizer]
        private static void AutoAfter() { _auto = false; }
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save)), HarmonyPrefix]
        private static void SaveBefore(string _text, out SaveRun __state)
        {
            __state = _save = new SaveRun { Start = Stopwatch.GetTimestamp(), St = SessionLog.St, T = SessionLog.FileTime,
                Auto = _auto || Net.Mp.AutoSaving, PlayLog = SessionLog.CurrentPath };
            try { __state.Path = pathscript.UnDuckStringBecauseItIsDuckedByDefault(pathscript.SaveDataName(_text)); } catch { }
            Interlocked.Increment(ref _saves); History[Slot()].Flags |= 4;
        }
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save)), HarmonyFinalizer]
        private static void SaveAfter(SaveRun __state)
        {
            _save = null;
            if (__state.Task == null || __state.Task.IsCompleted) Complete(__state);
            else __state.Task.ContinueWith(_ => Complete(__state), TaskScheduler.Default);
        }
        private static void Complete(SaveRun save)
        {
            double ms = (Stopwatch.GetTimestamp() - save.Start) * 1000.0 / Stopwatch.Frequency;
            long bytes = -1;
            try { if (save.Path != null) bytes = new FileInfo(save.Path).Length; } catch { }
            Saves.Enqueue(new SaveResult { Ms = ms, St = save.St + ms / 1000, T = save.T + ms / 1000, Auto = save.Auto, Bytes = bytes, PlayLog = save.PlayLog });
            Interlocked.Decrement(ref _saves);
        }
        internal static void DrainSaves()
        {
            while (Saves.TryDequeue(out var save))
                SessionLog.Save(save.Ms, save.Auto, save.Bytes, save.PlayLog, save.St, save.T);
        }
        [HarmonyPatch]
        private static class AsyncSave
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(pathscript), "SaveBinaryAsync").MakeGenericMethod(typeof(savedata));
                yield return AccessTools.Method(typeof(pathscript), "SaveJSONAsync").MakeGenericMethod(typeof(savedata));
            }
            private static void Postfix(string __0, object __1, Task __result)
            {
                // Mono shares reference-type generic code. Ignore the properties-only call and unrelated saves.
                if (_save == null || !(__1 is savedata)) return;
                _save.Task = __result;
                try { _save.Path = pathscript.UnDuckStringBecauseItIsDuckedByDefault(__0); } catch { }
            }
        }
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.PressedStart)), HarmonyPrefix]
        private static void NewLoad() { if (Net.Mp.Client == null) SessionLog.BeginLoad("new"); } // a joiner's host-world load keeps its "join" timing from the connect
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.PressedNewStart)), HarmonyPrefix]
        private static void NewSeedLoad() => NewLoad();
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.PressedRestart)), HarmonyPrefix]
        private static void RestartLoad() => NewLoad();
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Load)), HarmonyPrefix]
        private static void SavedLoad(string _name)
        {
            if (File.Exists(_name)) SessionLog.BeginLoad("load");
        }
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.AutoContinue)), HarmonyPrefix]
        private static void AutoLoad() { if (Net.Mp.Client == null) SessionLog.BeginLoad("load"); }
        [HarmonyPatch(typeof(fpscontroller), "Update"), HarmonyPostfix]
        private static void PlayerUpdated(fpscontroller __instance) => SessionLog.PlayerUpdated(__instance);
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMainMenu)), HarmonyPrefix]
        private static void Leaving() { SessionLog.EndWorld(); }
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.FStart)), HarmonyPostfix]
        private static void WorldStarted()
        {
            if (DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu) SessionLog.WorldEntered();
        }
    }
}
