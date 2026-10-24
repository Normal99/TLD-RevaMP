using System;
using System.Collections.Generic;
using UnityEngine;
using TLDRevamp.Fixes;

internal static class TerrainHorizonFullTest
{
    private static int _failures;
    private static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + label);
        if (!ok) _failures++;
    }

    private static roadGenScript.roadClass Road(int elements, int bones, bool includeNull = false)
    {
        var road = new roadGenScript.roadClass
        {
            generated = true,
            start = new Vector3d(0, 0, 0),
            end = new Vector3d(100, 0, 0)
        };
        if (includeNull) road.roadElements.Add(null);
        for (int i = 0; i < elements; i++)
        {
            var element = new roadGenScript.roadElementClass();
            for (int b = 0; b < bones; b++) element.bonePos.Add(new Vector3d(i, b, 0));
            road.roadElements.Add(element);
        }
        return road;
    }

    internal static int Main()
    {
        var road = Road(100000, 1);
        var chunk = new roadGenScript.dotChunkClass();
        chunk.roads.Add(road);
        var gen = new roadGenScript();
        gen.chunks[Vector3d.zero] = chunk;
        gen.chunksNeeded.Add(Vector3d.zero);
        var map = new mapSettingScript { roadGens = new List<roadGenScript> { gen } };
        menuhandler.s = new menuhandler { currentMainMap = map };
        Time.frameCount = 1;

        var signatureMethod = typeof(TerrainHorizon).GetMethod("RoadSignature",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (signatureMethod == null) throw new Exception("production RoadSignature missing");

        int Invoke()
        {
            try { return (int)signatureMethod.Invoke(null, null); }
            catch (System.Reflection.TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        int hash = Invoke();
        Check(TerrainHorizon.SignatureVisits == TerrainHorizon.SignatureMaxElements,
              "production RoadSignature stops visits at the finite budget");
        Check(TerrainHorizon.SignatureElements == 100000,
              "production RoadSignature retains the declared element count");
        Check(hash == Invoke(), "cached signature is stable");

        Time.frameCount += TerrainHorizon.SignatureIntervalFrames;
        road.roadElements.Clear();
        road.roadElements.Add(null);
        road.roadElements.Add(null);
        int before = (int)TerrainHorizon.SignatureVisits;
        Invoke();
        Check(TerrainHorizon.SignatureVisits <= TerrainHorizon.SignatureMaxElements,
              "null elements also consume the finite visit budget");
        Check(TerrainHorizon.SignatureVisits >= before || before == TerrainHorizon.SignatureMaxElements,
              "visit diagnostics remain monotonic across walks");

        // the whole rebuild in one Tick (before), then spread over frames: one element or one mesh a frame, and the
        // world shifted between the slices of a rebuild
        TerrainHorizon.SpreadRebuild = false; RibbonRebase(false);
        TerrainHorizon.SpreadRebuild = true; TerrainHorizon.StepBudgetMs = 0; RibbonRebase(true);
        Console.WriteLine(_failures == 0 ? "PASS" : "FAIL");
        return _failures == 0 ? 0 : 1;
    }

    private static bool Building() => typeof(TerrainHorizon).GetField("_job", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null) != null;

    /// Ticks until the rebuild in progress is done (one Tick when it isn't spread); with `shift`, the world is shifted
    /// after the rebuild's first slice, as the game may do between two frames of it.
    private static void Tick(Vector3? shift = null)
    {
        TerrainHorizon.Tick();
        if (shift.HasValue && Building()) mainscript.s.Shift(shift.Value);
        for (int i = 0; i < 1000 && Building(); i++) TerrainHorizon.Tick();
        Check(!Building(), "the rebuild finishes");
    }

    private static void RibbonRebase(bool spread)
    {
        typeof(TerrainHorizon).GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);   // a fresh ribbon root
        mainscript.s = new mainscript();
        mainscript.visszarakva = Vector3.zero;   // the world unshifted again for the second run
        var road = Road(0, 0);
        road.start = new Vector3d(0, 0, 600);
        road.end = new Vector3d(0, 0, 800);
        var element = new roadGenScript.roadElementClass { roadID = 1 };
        element.bonePos.Add(road.start); element.bonePos.Add(road.end);
        road.roadElements.Add(element);
        var gen = new roadGenScript();
        var chunk = new roadGenScript.dotChunkClass(); chunk.roads.Add(road);
        gen.chunks[Vector3d.zero] = chunk; gen.chunksNeeded.Add(Vector3d.zero);
        menuhandler.s.currentMainMap.roadGens = new List<roadGenScript> { gen };
        Tick();

        var batches = (System.Collections.IDictionary)typeof(TerrainHorizon).GetField("Batches",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        var batch = batches[1];
        var go = (GameObject)batch.GetType().GetField("Go").GetValue(batch);
        var mesh = (Mesh)batch.GetType().GetField("Mesh").GetValue(batch);
        Vector3 WorldVertex() => go.transform.TransformPoint(mesh.vertices[0]);
        var expected = WorldVertex();
        Check(mainscript.s.visszarakni.Count == 1, "only the ribbon root registers for the game's rebase");
        foreach (var delta in new[] { new Vector3(-4500, -30, 1200), new Vector3(1800, 15, -600) })
        {
            var before = WorldVertex();
            mainscript.s.Shift(delta); expected += delta;
            Check((WorldVertex() - before - delta).sqrMagnitude < 0.0001f,
                  "game rebase moves the existing ribbon exactly once before Tick");
            Tick();
            Check((WorldVertex() - expected).sqrMagnitude < 0.0001f,
                  "production rebuild does not apply the parent's rebase a second time");
        }
        // An ordinary later rebuild must also use the shifted parent's local space.
        mainscript.s.Shift(new Vector3(-4500, 0, 0)); expected += new Vector3(-4500, 0, 0);
        Tick();
        mainscript.s.player.transform.position += new Vector3(40, 0, 0);
        Tick();
        Check((WorldVertex() - expected).sqrMagnitude < 0.0001f, "walking after a rebase keeps the ribbon on its road");
        if (!spread) return;
        // a rebase between two slices of one rebuild: the ribbon still lands on its road
        mainscript.s.player.transform.position += new Vector3(40, 0, 0);
        var shift = new Vector3(2500, 0, -700); expected += shift;
        Tick(shift);
        Check((WorldVertex() - expected).sqrMagnitude < 0.0001f, "a rebase in the middle of a spread rebuild keeps the ribbon on its road");
    }
}
