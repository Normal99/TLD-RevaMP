using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A copy's tanks hold what the owner's record says, nothing else. The game simulates fluids on every machine (our
    /// sessions leave syncScript.IsConnected() false, so its host-only rules run everywhere): on a copy the coolant boils
    /// off (carscript.Update, with the owner's engine temperature since protocol 56), a worn radiator leaks
    /// (coolantTankscript.Cool, the copy's engine "running" from the owner's state), the engine burns oil
    /// (enginescript.Upd), an open cap pours out. Those losses are no edit (Entities.Edits: a copy's own simulation), and
    /// the owner sends a record only when its own changes: what the copy lost stayed lost. A joiner's car copy had no
    /// coolant in its radiator a few minutes after the owner's engine got hot, and kept it so.
    ///
    /// So on a copy (1) the game's fluid losses (fluidcontainer.ChangeWithoutMix with a negative amount) don't run, and
    /// (2) once a second every tank of a copy is checked against the owner's latest record and put back when it differs:
    /// the fill of a player (an edit, Entities.Edits) waits for its record; fluid poured from a copy into one's own can is
    /// the owner's to count. A/B switch Entities.CopyFluidsFollowOwner (false = before this change).
    public static partial class Entities
    {
        public static bool CopyFluidsFollowOwner = true;
        public static float CopyFluidCheckS = 1f, CopyFluidToleranceL = 0.01f;
        public static long CopyFluidLossSkipped, CopyFluidsRestored, CopyFluidChecks;
        private static readonly HashSet<mainscript.fluidcontainer> CopyFluidContainers = new HashSet<mainscript.fluidcontainer>();
        /// Per copy item: its tanks as the owner's latest record has them (local ids). A tank with no record is empty.
        private static readonly Dictionary<tosaveitemscript, List<save_tankscript>> CopyTankTruth = new Dictionary<tosaveitemscript, List<save_tankscript>>();
        private static readonly List<tosaveitemscript> _truthGone = new List<tosaveitemscript>();
        private static float _copyFluidAt;

        /// The tanks of a copy item as the owner just set them (a record applied, the copy spawned from one, our own fill sent).
        private static void CopyTanksAre(tosaveitemscript it, List<save_tankscript> recs)
        {
            if (it == null || it.tanks == null || it.tanks.Count == 0) return;
            var keep = new List<save_tankscript>();
            if (recs != null) foreach (var r in recs) if (r != null) keep.Add(new save_tankscript(r));
            CopyTankTruth[it] = keep;
        }

        /// The same from the item itself (it was just loaded from the owner's record).
        private static void CopyTanksAreNow(tosaveitemscript it)
        {
            if (it == null || !it.loaded || it.tanks == null || it.tanks.Count == 0) return;   // not loaded: the prefab's contents
            var keep = new List<save_tankscript>();
            for (int i = 0; i < it.tanks.Count; i++)
            {
                var t = it.tanks[i];
                if (t == null || t.F == null || t.F.GetAmount() <= 0f) continue;
                keep.Add(new save_tankscript(t, it.idInSave, i));
            }
            CopyTankTruth[it] = keep;
        }

        /// Once a second: which fluid containers are a copy's, and every copy tank back to its owner's record.
        private static void CopyFluidTick()
        {
            if (Time.realtimeSinceStartup < _copyFluidAt) return;
            _copyFluidAt = Time.realtimeSinceStartup + CopyFluidCheckS;
            CopyFluidContainers.Clear();
            _truthGone.Clear();
            foreach (var kv in CopyTankTruth) if (kv.Key == null) _truthGone.Add(kv.Key);
            foreach (var it in _truthGone) CopyTankTruth.Remove(it);
            if (!CopyFluidsFollowOwner) return;
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Root == null || e.Items == null) continue;
                for (int idx = 0; idx < e.Items.Count; idx++)
                {
                    var it = e.Items[idx];
                    if (it == null || it.tanks == null || it.tanks.Count == 0) continue;
                    if (e.Split != null && e.Split.Contains(idx)) continue;   // came off: its own entity has it
                    bool pending = (e.EditHoldUntil != null && e.EditHoldUntil.TryGetValue(idx, out float hold) && Time.realtimeSinceStartup < hold) || EditDirty.ContainsKey(it);
                    foreach (var t in it.tanks)
                    {
                        if (t == null || t.F == null) continue;
                        CopyFluidContainers.Add(t.F);
                        if (_tankFilledAt.TryGetValue(t, out float at) && Time.realtimeSinceStartup - at < 2f) pending = true;   // a player filling it: an edit is on its way
                    }
                    if (pending || !CopyTankTruth.TryGetValue(it, out var truth)) continue;
                    CopyFluidChecks++;
                    for (int i = 0; i < it.tanks.Count; i++)
                    {
                        var t = it.tanks[i];
                        if (t == null || t.F == null) continue;
                        save_tankscript rec = null;
                        foreach (var r in truth) if (r != null && r.ind == i) { rec = r; break; }
                        if (SameFluids(t.F, rec)) continue;
                        _applyingState = true;
                        try
                        {
                            if (rec != null) rec.Load(t);
                            else { t.FStart(); t.F.fluids.Clear(); t.Upd(); }
                            t.F.ResetSync();
                        }
                        finally { _applyingState = false; }
                        CopyFluidsRestored++;
                        SessionLog.Event("copy-fluid-restored", e.NetId, item: idx, slot: i, to: MyId, epoch: e.Epoch);
                    }
                }
            }
        }

        /// Does the tank hold what the record says (to CopyFluidToleranceL a fluid)?
        private static bool SameFluids(mainscript.fluidcontainer f, save_tankscript rec)
        {
            int n = rec != null && rec.amount != null && rec.type != null ? Mathf.Min(rec.amount.Length, rec.type.Length) : 0;
            foreach (var fl in f.fluids)
            {
                if (fl == null || fl.amount <= CopyFluidToleranceL) continue;
                bool found = false;
                for (int k = 0; k < n; k++) if (rec.type[k] == (int)fl.type) { found = true; break; }
                if (!found) return false;
            }
            for (int k = 0; k < n; k++)
            {
                float have = 0f;
                foreach (var fl in f.fluids) if (fl != null && (int)fl.type == rec.type[k]) have += fl.amount;
                if (Mathf.Abs(have - Mathf.Max(0f, rec.amount[k])) > CopyFluidToleranceL) return false;
            }
            return true;
        }

        /// The game's own fluid losses (boil-off, a radiator leak, oil burnt, an open cap pouring out) don't run on a copy.
        [HarmonyPatch]
        private static class CopyFluidLoss
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(mainscript.fluidcontainer), nameof(mainscript.fluidcontainer.ChangeWithoutMix), new[] { typeof(float) });
                yield return AccessTools.Method(typeof(mainscript.fluidcontainer), nameof(mainscript.fluidcontainer.ChangeWithoutMix), new[] { typeof(float), typeof(bool) });
            }

            [HarmonyPrefix]
            private static bool Prefix(mainscript.fluidcontainer __instance, float amount)
            {
                if (amount >= 0f || !CopyFluidsFollowOwner || CopyFluidContainers.Count == 0 || !CopyFluidContainers.Contains(__instance)) return true;
                CopyFluidLossSkipped++;
                return false;
            }
        }
    }
}
