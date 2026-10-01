using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// poiScript.ItemSpawn fills a building with items in one frame: every newRandomStuffSpawnScript.Spawn(), then every
    /// undergroundSpawnScript.Spawn(). Profiled (seeded): up to ~54 ms in one frame, single spawn points up to ~18 ms.
    ///
    /// Instead, the spawn points are queued in the original order and worked off over the next frames within a small time
    /// budget (at least one spawn point per frame). Underground points are collected only after all regular points have
    /// spawned, as in the original. The queue is flushed completely before any save (savedatascript.SaveToData) and before
    /// a building is removed, so nothing can be lost.
    ///
    /// Not bit-exact like the other fixes, by nature: the spawn scripts roll with Unity's global, unseeded Random, so
    /// the vanilla game already produces different items each time. Same spawn points, same probabilities; only which
    /// frame each point rolls in changes.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.ItemSpawnSpread.Enabled / .BudgetMs
    [HarmonyPatch]
    public static class ItemSpawnSpread
    {
        public static bool Enabled = true;
        public static double BudgetMs = 2.0;

        public static long Queued, Spawned, Dropped, Flushes, Errors;
        public static double MaxTickMs;

        private sealed class Job
        {
            public poiScript Poi;
            public newRandomStuffSpawnScript[] Regular;
            public int Next;
            public undergroundSpawnScript[] Underground; // collected after the regular ones, like the original
            public int NextUnderground;
        }

        private static readonly Queue<Job> Jobs = new Queue<Job>();

        [HarmonyPatch(typeof(poiScript), nameof(poiScript.ItemSpawn))]
        [HarmonyPrefix]
        private static bool ItemSpawnPrefix(poiScript __instance)
        {
            if (!Enabled) return true;
            Jobs.Enqueue(new Job
            {
                Poi = __instance,
                // Same call as the original's first line, at the same moment
                Regular = __instance.GetComponentsInChildren<newRandomStuffSpawnScript>(includeInactive: true),
            });
            Queued++;
            return false;
        }

        /// Called once per frame from the mod's runner.
        public static void Tick()
        {
            if (Jobs.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            bool didOne = false;
            while (Jobs.Count > 0 && (!didOne || Stopwatch.GetTimestamp() - t0 < budget))
            {
                bool more;
                try { more = Step(Jobs.Peek()); }
                catch (Exception e)
                {
                    // A failing spawn point would have thrown in the original too; skip it (index already advanced)
                    Errors++;
                    if (Errors <= 5) Plugin.Log.LogWarning("ItemSpawnSpread: spawn point threw: " + e.Message);
                    more = true;
                }
                if (more) didOne = true;
                else Jobs.Dequeue(); // job finished (or its building is gone)
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }

        /// Spawns the next point of the job. Returns false when the job has nothing left.
        private static bool Step(Job job)
        {
            if (job.Poi == null) { Dropped++; return false; } // building destroyed by the game
            if (job.Next < job.Regular.Length)
            {
                var p = job.Regular[job.Next++];
                if (p != null) { p.Spawn(); Spawned++; }
                return true;
            }
            if (job.Underground == null)
                job.Underground = job.Poi.GetComponentsInChildren<undergroundSpawnScript>();
            if (job.NextUnderground < job.Underground.Length)
            {
                var p = job.Underground[job.NextUnderground++];
                if (p != null) { p.Spawn(); Spawned++; }
                return true;
            }
            return false;
        }

        /// Spawn everything still queued, right now (before saves and building removal).
        public static void Flush()
        {
            if (Jobs.Count == 0) return;
            Flushes++;
            while (Jobs.Count > 0)
            {
                var job = Jobs.Peek();
                while (Step(job)) { }
                Jobs.Dequeue();
            }
        }

        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SaveToData))]
        [HarmonyPrefix]
        private static void BeforeSave() => Flush();

        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.RemovePoi))]
        [HarmonyPrefix]
        private static void BeforeRemovePoi() => Flush();

        public static string Stats =>
            "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"queued\":" + Queued + ",\"pending\":" + Jobs.Count +
            ",\"spawned\":" + Spawned + ",\"dropped\":" + Dropped + ",\"flushes\":" + Flushes + ",\"errors\":" + Errors +
            ",\"maxTickMs\":" + MaxTickMs.ToString("F2") + "}";
    }
}
