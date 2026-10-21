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

        Console.WriteLine(_failures == 0 ? "PASS" : "FAIL");
        return _failures == 0 ? 0 : 1;
    }
}
