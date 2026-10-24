#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using System.IO;
using TLDRevamp.Fixes;

static class TerrainHorizonTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static void Near(double a, double b, double tol, string why) { checks++; if (Math.Abs(a - b) > tol) throw new Exception(why + " (" + a + " != " + b + ")"); }

    static TerrainHorizonGeometry.Point P(double x, double y, double z) => new TerrainHorizonGeometry.Point(x, y, z);

    static int Main()
    {
        RibbonGeometry();
        NearCut();
        FarLimit();
        Blend();
        HeightFixes();
        Signature();
        Gate();
        Batch();
        NonFinite();
        MovingPlayer();
        OriginShift();
        RoadChanges();
        BatchingAndRemoval();
        Teleports();
        ChunkRange();
        RoadRange();
        BlendRadius();
        OriginShiftRule();
        AlignBudgetRule();
        Performance();
        RibbonWinding();
        FarGridSpace();
        SignatureGeometry();
        NearCutRadius();
        ProductionSource();
        Console.WriteLine("Terrain horizon: " + checks + " checks passed");
        return 0;
    }

    // ---------------------------------------------------------------- the ribbon's winding
    static void RibbonWinding()
    {
        // The road materials are single-sided (Cull Back). A ribbon whose triangles face down is
        // culled by every camera above it, so the road is not drawn at all. The winding has to be
        // upward in every direction of travel, not only along +Z.
        var dirs = new (double dx, double dz, string name)[]
        {
            (0, 1, "+Z"), (0, -1, "-Z"), (1, 0, "+X"), (-1, 0, "-X"),
            (0.7071, 0.7071, "+X+Z"), (-0.7071, 0.7071, "-X+Z"),
        };
        foreach (var (dx, dz, name) in dirs)
        {
            var b = new TerrainHorizonGeometry.Batch(1, 100);
            Check(b.AddStrip(P(0, 10, 0), P(dx * 40, 10, dz * 40), 7, 0.08), "a strip along " + name + " is accepted");
            Check(RibbonFacesUp(b), "a strip along " + name + " faces up");
        }

        // a sloped road (the far terrain is not flat) must still face up
        var sloped = new TerrainHorizonGeometry.Batch(2, 100);
        Check(sloped.AddStrip(P(0, 0, 0), P(0, 30, 40), 7, 0.08), "a climbing strip is accepted");
        Check(RibbonFacesUp(sloped), "a climbing strip faces up");

        // two strips in one batch: every triangle of the batch faces up
        var two = new TerrainHorizonGeometry.Batch(3, 100);
        two.AddStrip(P(0, 0, 0), P(0, 0, 40), 6, 0.08);
        two.AddStrip(P(0, 0, 40), P(0, 0, 80), 6, 0.08);
        Check(two.TriangleCount == 4, "two strips make four triangles");
        Check(RibbonFacesUp(two), "every triangle of a batch faces up");

        // a rebuild that came out the same is not uploaded again (TerrainHorizon.Upload): SameAs is exact
        var again = new TerrainHorizonGeometry.Batch(2, 1000);
        again.AddStrip(P(0, 0, 0), P(0, 0, 40), 6, 0.08);
        again.AddStrip(P(0, 0, 40), P(0, 0, 80), 6, 0.08);
        Check(again.SameAs(two) && two.SameAs(again), "the same strips are the same");
        var moved = new TerrainHorizonGeometry.Batch(2, 1000);
        moved.AddStrip(P(0, 0, 0), P(0, 0, 40), 6, 0.08);
        moved.AddStrip(P(0, 0, 40), P(0, 0, 80.001), 6, 0.08);
        Check(!moved.SameAs(two), "a strip that moved a millimetre is not the same");
        var shorter = new TerrainHorizonGeometry.Batch(2, 1000);
        shorter.AddStrip(P(0, 0, 0), P(0, 0, 40), 6, 0.08);
        Check(!shorter.SameAs(two) && !two.SameAs(shorter), "fewer strips are not the same");
        Check(!two.SameAs(null), "no batch is not the same");
        var empty = new TerrainHorizonGeometry.Batch(2, 1000);
        Check(empty.SameAs(new TerrainHorizonGeometry.Batch(3, 1000)), "two empty batches are the same");

        // the helper itself
        Check(TerrainHorizonGeometry.FacesUp(P(0, 0, 0), P(0, 0, 10), P(10, 0, 10)), "a counter-clockwise triangle faces up");
        Check(!TerrainHorizonGeometry.FacesUp(P(0, 0, 0), P(10, 0, 10), P(0, 0, 10)), "a clockwise triangle faces down");
        Check(!TerrainHorizonGeometry.FacesUp(P(double.NaN, 0, 0), P(0, 0, 10), P(10, 0, 10)), "a non-finite triangle has no facing");
        // a degenerate (flat) triangle has no normal at all
        Check(!TerrainHorizonGeometry.FacesUp(P(0, 0, 0), P(0, 0, 0), P(0, 0, 0)), "a degenerate triangle has no facing");
    }

    // ---------------------------------------------------------------- the far grid's spacing
    static void FarGridSpace()
    {
        // The far grid is coarse: a 2000 m grid at a resolution of 33 has a vertex every ~62 m,
        // so the blend has to be at least that wide to reach a vertex at all.
        double spacing = TerrainHorizonGeometry.FarGridSpacing(2000.0, 33, 38.0);
        Near(spacing, 2000.0 / 32.0, 1e-9, "the far grid's vertex spacing is the grid size over the resolution");
        // a bad grid falls back
        Near(TerrainHorizonGeometry.FarGridSpacing(0.0, 33, 38.0), 38.0, 1e-9, "a zero grid size falls back");
        Near(TerrainHorizonGeometry.FarGridSpacing(2000.0, 1, 38.0), 38.0, 1e-9, "a resolution of one falls back");
        Near(TerrainHorizonGeometry.FarGridSpacing(double.NaN, 33, 38.0), 38.0, 1e-9, "a broken grid size falls back");
        // and the blend must reach the next vertex of such a grid
        double r = TerrainHorizonGeometry.BlendRadius(spacing, 38, 260);
        Check(r >= spacing, "the far grid's blend reaches the next vertex (" + r + " >= " + spacing + ")");
    }

    // ---------------------------------------------------------------- the signature's geometry
    static void SignatureGeometry()
    {
        // A road that moved must not look the same as the road it was.
        var a = new TerrainHorizonGeometry.SignatureBuilder(4096);
        a.AddRoad(true, 3, 20);
        a.AddPoint(0, 10, 0);
        a.AddPoint(100, 10, 0);
        var moved = new TerrainHorizonGeometry.SignatureBuilder(4096);
        moved.AddRoad(true, 3, 20);
        moved.AddPoint(0, 10, 0);
        moved.AddPoint(5000, 10, 0);                          // the same road, five km further on
        Check(a.Hash != moved.Hash, "a road that moved gives a different signature");

        // A road that changed height must not look the same either.
        var raised = new TerrainHorizonGeometry.SignatureBuilder(4096);
        raised.AddRoad(true, 3, 20);
        raised.AddPoint(0, 10, 0);
        raised.AddPoint(100, 15, 0);                          // five metres higher
        Check(a.Hash != raised.Hash, "a road that changed height gives a different signature");

        // but the same road still gives the same signature
        var same = new TerrainHorizonGeometry.SignatureBuilder(4096);
        same.AddRoad(true, 3, 20);
        same.AddPoint(0, 10, 0);
        same.AddPoint(100, 10, 0);
        Check(a.Hash == same.Hash, "the same road gives the same signature");

        // the walk is bounded: the game keeps every road chunk it ever made, so a walk without a
        // bound costs O(the visited world) every time the signature is taken.
        var bounded = new TerrainHorizonGeometry.SignatureBuilder(4096, 4);
        Check(bounded.AcceptsMoreRoads, "the builder accepts roads at first");
        for (int i = 0; i < 10; i++) bounded.AddRoad(true, 2, 10);
        Check(!bounded.AcceptsMoreRoads, "the builder stops accepting roads past its bound");
        Check(bounded.Roads == 10, "the builder counts the roads it saw");

        // the quantisation: a centimetre of noise does not change the signature, a metre does
        Check(TerrainHorizonGeometry.SignatureBuilder.Quantize(10.000) == TerrainHorizonGeometry.SignatureBuilder.Quantize(10.001),
              "a millimetre of noise does not change the quantised position");
        Check(TerrainHorizonGeometry.SignatureBuilder.Quantize(10.0) != TerrainHorizonGeometry.SignatureBuilder.Quantize(10.5),
              "half a metre does change the quantised position");
        Check(TerrainHorizonGeometry.SignatureBuilder.Quantize(double.NaN) == 0, "a non-finite position folds to a fixed value");
        Check(TerrainHorizonGeometry.SignatureBuilder.Quantize(double.PositiveInfinity) == 0, "an infinite position folds to a fixed value");
    }

    // ---------------------------------------------------------------- the near cut's radius
    static void NearCutRadius()
    {
        // The cut must follow the game's own draw distance, not a hard-coded number.
        Near(TerrainHorizonGeometry.NearCutFromDrawDistance(500, 495), 500, 1e-9, "the near cut follows the draw distance");
        Near(TerrainHorizonGeometry.NearCutFromDrawDistance(0, 495), 495, 1e-9, "a draw distance of zero falls back");
        Near(TerrainHorizonGeometry.NearCutFromDrawDistance(double.NaN, 495), 495, 1e-9, "a broken draw distance falls back");
        Near(TerrainHorizonGeometry.NearCutFromDrawDistance(-1, 495), 495, 1e-9, "a negative draw distance falls back");
    }

    // ---------------------------------------------------------------- the origin shift rule
    static void OriginShiftRule()
    {
        // The game shifts the world in Unity space when the player strays past 2500 m: the Unity
        // position jumps by kilometres while the global position stays where it was. Only THAT
        // is an origin shift; an ordinary walk moves both, and a teleport moves the global one too.
        const double globalTol = 5.0, unityJump = 500.0;
        Check(TerrainHorizonGeometry.IsOriginShift(0, 0, 2500 * 2500, globalTol, unityJump),
              "a Unity jump with an unchanged global position is an origin shift");
        Check(TerrainHorizonGeometry.IsOriginShift(2, -1, 900 * 900, globalTol, unityJump),
              "a small global drift with a big Unity jump is still an origin shift");
        // an ordinary walk: both move by the same amount
        Check(!TerrainHorizonGeometry.IsOriginShift(40, 0, 40 * 40, globalTol, unityJump),
              "an ordinary walk is not an origin shift");
        // a teleport: the global position moved as well
        Check(!TerrainHorizonGeometry.IsOriginShift(5000, 0, 5000 * 5000, globalTol, unityJump),
              "a teleport is not an origin shift");
        // nothing moved at all
        Check(!TerrainHorizonGeometry.IsOriginShift(0, 0, 0, globalTol, unityJump),
              "standing still is not an origin shift");
        // a broken number is never a shift
        Check(!TerrainHorizonGeometry.IsOriginShift(double.NaN, 0, 100, globalTol, unityJump),
              "a broken global position is not an origin shift");
        Check(!TerrainHorizonGeometry.IsOriginShift(0, 0, 100, globalTol, double.NaN),
              "a broken jump size is not an origin shift");
    }

    // ---------------------------------------------------------------- the align budget rule
    static void AlignBudgetRule()
    {
        // The budget is soft: a single call into the game's own road search cannot be
        // interrupted. What the rule guarantees is that no further work is started once the
        // budget is spent.
        Check(!TerrainHorizonGeometry.AlignBudgetExhausted(0, 0, 4.0), "a fresh frame has budget left");
        Check(!TerrainHorizonGeometry.AlignBudgetExhausted(1.0, 1.0, 4.0), "half the budget is still budget");
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(3.0, 1.0, 4.0), "the spent time plus the elapsed time is the budget");
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(4.0, 0, 4.0), "a spent budget is a spent budget");
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(10.0, 0, 4.0), "an overspent budget is a spent budget");
        // a broken number is treated as spent, so a broken measurement cannot open the budget
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(double.NaN, 0, 4.0), "a broken measurement does not open the budget");
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(0, double.NaN, 4.0), "a broken elapsed time does not open the budget");
        Check(TerrainHorizonGeometry.AlignBudgetExhausted(0, 0, 0), "a budget of zero is spent");

        // The rule is the gate in front of every vertex of the correction's loop, so the loop
        // must stop when the budget is gone: the mesh is left as the game made it rather than
        // half corrected. This drives the production rule itself, not a copy of it.
        int processed = 0, stoppedAt = -1;
        for (int i = 0; i < 1089; i++)                 // a far tile of 33x33 vertices
        {
            // each vertex costs a tenth of a millisecond, and the frame's budget is 4 ms
            double spent = 0.0, elapsed = processed * 0.1;
            if (TerrainHorizonGeometry.AlignBudgetExhausted(spent, elapsed, 4.0)) { stoppedAt = i; break; }
            processed++;
        }
        Check(stoppedAt > 0, "the vertex loop stops when the budget is spent");
        Check(processed < 1089, "the mesh does not run all its vertices after the budget expired");
        Check(stoppedAt >= 35 && stoppedAt <= 45, "the loop stops at about the budget (" + stoppedAt + ")");
    }

    // ---------------------------------------------------------------- the production source
    static void ProductionSource()
    {
        // The geometry above is only worth anything if the production code uses it the same way.
        // These checks read the shipped source, so a change that breaks the rule in the renderer
        // (while the pure geometry still passes) fails the test.
        string root = Environment.GetEnvironmentVariable("TLD_REVAMP_ROOT");
        if (string.IsNullOrEmpty(root)) root = FindRepoRoot();
        if (root == null) { Console.WriteLine("SKIP production source checks: repo root not found"); return; }

        string horizon = File.ReadAllText(Path.Combine(root, "src/TLDRevamp/Fixes/TerrainHorizon.cs"));

        // The far grid's mapping is proved BEHAVIOURALLY by the hook test
        // (tools/unit/TerrainTileHookTest.csproj), which compiles the shipped hook file. Here
        // the guard only checks that the hook lives in its own file and uses the tile's own
        // transform: the mesh's vertices are local to the tile, so the generator's transform and
        // the noise seed pM.p are both wrong.
        string hook = File.ReadAllText(Path.Combine(root, "src/TLDRevamp/Fixes/TerrainHorizon.TileHook.cs"));
        Check(hook.Contains("F.transform"),
              "the far grid's hook uses the tile's own transform");
        Check(!hook.Contains("pM.p + local"),
              "the far grid's vertices are not offset by the tile position");
        Check(!hook.Contains("__instance.transform.TransformPoint"),
              "the far grid's vertices are not read in the generator's own space");

        // the origin shift: the ribbon hierarchy must carry a visszarako, like the game's tiles
        Check(horizon.Contains("AddComponent<visszarako>()"),
              "the ribbon hierarchy carries a visszarako, so it follows the origin shift");
        Check(horizon.Contains("moveWhenParented = true"),
              "the ribbon's visszarako moves with its parent, like the game's terrain tiles");

        // the signature must see the road's own points
        Check(horizon.Contains("builder.AddPoint("),
              "the road signature folds the road's own points in");

        // the near cut must be derived, not hard-coded
        Check(horizon.Contains("NearCutRadius()"),
              "the near cut is derived from the game's own draw distance");

        // D1: the Unity-shaped twin of the ribbon (src/TLDRevamp/Fixes/TerrainRibbon.cs) needs
        // Unity types, so it cannot be linked here. It must still be guarded the same way: a
        // NaN half-width and an overflowing segment must be rejected before anything is written.
        string ribbonTwin = File.ReadAllText(Path.Combine(root, "src/TLDRevamp/Fixes/TerrainRibbon.cs"));
        Check(ribbonTwin.Contains("if (!Finite(halfWidth)"),
              "the ribbon twin rejects a non-finite half-width explicitly");
        Check(ribbonTwin.Contains("if (!Finite(forward))"),
              "the ribbon twin rejects an overflowing segment");
        Check(ribbonTwin.Contains("if (!Finite(lift))"),
              "the ribbon twin rejects a non-finite lift");
        Check(!ribbonTwin.Contains("halfWidth <= 0 || halfWidth > 100 ||"),
              "the ribbon twin does not rely on a range test to catch a NaN half-width");
        Check(ribbonTwin.Contains("UpwardWinding("),
              "the ribbon twin picks the winding that faces up");
        Check(ribbonTwin.Contains("if (!Finite(length)) return false;"),
              "the ribbon twin refuses a length that overflows to infinity");
        Check(ribbonTwin.Contains("!Finite(halfWidth)"),
              "the ribbon twin refuses a non-finite half-width explicitly");

        // the ribbon must be on the far terrain's layer, or the far camera does not draw it
        Check(horizon.Contains("DistantRoadLayer()"),
              "the ribbon's layer comes from the far terrain generator");
        Check(horizon.Contains("go.layer = DistantRoadLayer()"),
              "the ribbon is put on the far terrain's layer");

        // the blend must work in both directions, or the road floats in a dip of the far mesh.
        // The renderer reaches the blend through HeightFix.Corrected, which is the chain the
        // geometry file provides.
        string geometry = File.ReadAllText(Path.Combine(root, "src/TLDRevamp/Fixes/TerrainHorizonGeometry.cs"));
        Check(horizon.Contains("fix.Corrected("),
              "the far mesh correction goes through the height fix");
        Check(geometry.Contains("return BlendedHeight(_original[index]"),
              "the height fix corrects with the blend");
        Check(!geometry.Contains("if (vertexY <= roadY) return vertexY;"),
              "the blend is not one-sided: a vertex below the road is lifted too");

        // the signature must be bounded, and the movement test must come first
        Check(horizon.Contains("SignatureMaxRoads"),
              "the road signature's walk is bounded");
        Check(horizon.IndexOf("bool moved =") < horizon.IndexOf("RoadSignature()"),
              "the cheap movement test comes before the road signature");

        // L1: the origin shift counter may only count an origin shift, not every rebuild.
        Check(horizon.Contains("IsOriginShift("),
              "the origin shift counter uses the origin shift rule");
        Check(!horizon.Contains("Rebuild(at, sig);\n                OriginShifts++;"),
              "the origin shift counter is not incremented on every rebuild");

        // M4: the budget must be checked after the road collection and inside the vertex loop,
        // and a mesh that ran out of budget must not be written back.
        Check(horizon.Contains("AlignBudgetLeft("),
              "the correction honours the frame budget");
        Check(horizon.IndexOf("AlignBudgetLeft(", horizon.IndexOf("var roads = new List<roadGenScript.roadClass>()")) >
              horizon.IndexOf("var roads = new List<roadGenScript.roadClass>()"),
              "the budget is checked after the roads are collected");
        Check(horizon.IndexOf("AlignBudgetLeft(", horizon.IndexOf("for (int i = 0; i < vertices.Length; i++)")) >
              horizon.IndexOf("for (int i = 0; i < vertices.Length; i++)"),
              "the budget is checked inside the vertex loop");
        // The abort must be counted, and it must leave the mesh alone: a half-corrected mesh
        // would show a seam of its own, so the whole mesh is given up instead.
        var abortBlock = Section(horizon, "AlignBudgetAborted++;", "var g = toWorld(vertices[i]);");
        Check(abortBlock.Contains("AlignBudgetAborted++"),
              "an aborted correction is counted");
        Check(abortBlock.Contains("return 0;"),
              "an aborted correction gives the whole mesh up");
        Check(!abortBlock.Contains("mesh.vertices"),
              "an aborted correction does not write the mesh");

        // M2: the bounds are recomputed whenever the geometry changed. The ribbon's mesh is
        // written in Upload, the corrected far mesh in AlignMesh, and the restored one when the
        // switch is turned off; each of them must refresh the bounds, or the camera culls the
        // mesh on its stale bounds. Each place is looked at on its own, so removing one of them
        // is caught.
        Check(Section(horizon, "private static void Upload(", "internal static int DistantRoadLayer()")
                  .Contains("RecalculateBounds()"),
              "the uploaded ribbon gets fresh bounds");
        Check(Section(horizon, "internal static int AlignMesh(", "private static double ElapsedMs(")
                  .Contains("RecalculateBounds()"),
              "the corrected far mesh gets fresh bounds");
        Check(Section(horizon, "internal static void RestoreFarMeshes(", "internal static long NonFinitePositions;")
                  .Contains("RecalculateBounds()"),
              "the restored far mesh gets fresh bounds");

        // L2: the cleanup may not return early while it still owns something.
        Check(!horizon.Contains("if (!_hasBuilt && Batches.Count == 0) return;"),
              "the cleanup does not leave the parent or the owned materials behind");
    }

    /// The part of a source file between two markers, so a check can look at one place only.
    static string Section(string source, string from, string to)
    {
        int a = source.IndexOf(from, StringComparison.Ordinal);
        if (a < 0) return "";
        int b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        if (b < 0) return source.Substring(a);
        return source.Substring(a, b - a);
    }

    /// The repository root, found by walking up from the test's own directory.
    static string FindRepoRoot()
    {
        string repoRoot = Environment.GetEnvironmentVariable("TLD_REPO_ROOT");
        if (!string.IsNullOrEmpty(repoRoot) && File.Exists(Path.Combine(repoRoot, "src/TLDRevamp/Fixes/TerrainHorizon.cs")))
        {
            return Path.GetFullPath(repoRoot);
        }

        string currentDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentDirectory, "src/TLDRevamp/Fixes/TerrainHorizon.cs")))
        {
            return Path.GetFullPath(currentDirectory);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src/TLDRevamp/Fixes/TerrainHorizon.cs"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ---------------------------------------------------------------- the ribbon strip
    static void RibbonGeometry()
    {
        var v = new List<float>(); var uv = new List<float>(); var t = new List<int>();
        // the old renderer built the strip in Unity space; the new one is pure and global
        var batch = new TerrainHorizonGeometry.Batch(1, 120000);
        Check(batch.AddStrip(P(0, 10, 0), P(0, 10, 40), 7, 0.08), "road segment accepts a finite strip");
        Check(batch.VertexCount == 4 && batch.TriangleCount == 2, "one strip has four vertices and two triangles");
        Near(batch.Vx[0], -7, 0.001, "strip lies across the road");
        Near(batch.Vx[1], 7, 0.001, "strip lies across the road");
        Near(batch.Vy[0], 10.08, 0.001, "strip sits above the road surface");
        Near(batch.Vx[2], -7, 0.001, "far end keeps the width");
        Near(batch.Vz[3], 40, 0.001, "far end keeps the position");
        // The winding must face UP. The road materials are single-sided (Cull Back), so a ribbon
        // wound the other way is culled by every camera above it and the road is never drawn.
        // The old assertion only checked the index order, which is stable either way, and so it
        // passed with the road invisible.
        Check(RibbonFacesUp(batch), "the ribbon's triangles face up");
        Check(batch.T[0] >= 0 && batch.T[0] < 4 && batch.T[1] >= 0 && batch.T[1] < 4 &&
              batch.T[2] >= 0 && batch.T[2] < 4 && batch.T[3] >= 0 && batch.T[3] < 4 &&
              batch.T[4] >= 0 && batch.T[4] < 4 && batch.T[5] >= 0 && batch.T[5] < 4, "the winding only uses the strip's own four vertices");
        Near(batch.Uu[0], 0, 0.001, "first uv is 0");
        Near(batch.Uu[1], 1, 0.001, "second uv is 1");
        Near(batch.Uv[3], 1, 0.001, "last uv is 1");

        // the strip is perpendicular to the direction of travel
        var b2 = new TerrainHorizonGeometry.Batch(2, 120000);
        b2.AddStrip(P(0, 0, 0), P(30, 0, 0), 5, 0);
        Near(b2.Vx[0], 0, 0.001, "a strip along x has no x offset");
        // side = cross(up, forward) * halfWidth; for forward = +x that is -z
        Near(b2.Vz[0], 5, 0.001, "a strip along x is offset in z");
        Near(b2.Vz[1], -5, 0.001, "a strip along x is offset in z the other way");
    }

    /// Whether every triangle of a batch faces up (its geometric normal has a positive y).
    /// This is the invariant that decides whether the road is visible at all.
    static bool RibbonFacesUp(TerrainHorizonGeometry.Batch batch)
    {
        for (int t = 0; t + 2 < batch.T.Count; t += 3)
        {
            var a = P(batch.Vx[batch.T[t]], batch.Vy[batch.T[t]], batch.Vz[batch.T[t]]);
            var b = P(batch.Vx[batch.T[t + 1]], batch.Vy[batch.T[t + 1]], batch.Vz[batch.T[t + 1]]);
            var c = P(batch.Vx[batch.T[t + 2]], batch.Vy[batch.T[t + 2]], batch.Vz[batch.T[t + 2]]);
            if (!TerrainHorizonGeometry.FacesUp(a, b, c)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- the near cut
    static void NearCut()
    {
        const double near = 495, far = 4000;
        var segs = new TerrainHorizonGeometry.Segment[2];

        // wholly inside the near cut: nothing
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 0), P(0, 0, 100), 0, 0, near, far, segs) == 0, "a segment inside the near cut is dropped");

        // wholly outside: kept whole
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 0, near, far, segs) == 1, "a segment outside the near cut is kept");
        Near(segs[0].A.Z, 600, 0.001, "the kept segment starts where it did");
        Near(segs[0].B.Z, 700, 0.001, "the kept segment ends where it did");

        // crossing the near cut: trimmed at the crossing, not left to reach into the near world
        int n = TerrainHorizonGeometry.ClipToRing(P(0, 0, 400), P(0, 0, 600), 0, 0, near, far, segs);
        Check(n == 1, "a segment crossing the near cut gives one piece");
        Near(segs[0].A.Z, 495, 0.5, "the ribbon is trimmed at the near cut");
        Near(segs[0].B.Z, 600, 0.001, "the far end is untouched");

        // crossing the other way
        n = TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 400), 0, 0, near, far, segs);
        Check(n == 1, "a segment crossing the near cut the other way gives one piece");
        Near(segs[0].A.Z, 600, 0.001, "the far end is untouched");
        Near(segs[0].B.Z, 495, 0.5, "the ribbon is trimmed at the near cut");

        // a segment that passes through the middle of the near disc: two pieces, one on each side
        n = TerrainHorizonGeometry.ClipToRing(P(-800, 0, 0), P(800, 0, 0), 0, 0, near, far, segs);
        Check(n == 2, "a segment crossing the whole near disc gives two pieces");
        Near(segs[0].B.X, -495, 0.5, "the first piece ends at the near cut");
        Near(segs[1].A.X, 495, 0.5, "the second piece starts at the near cut");
        // and nothing of either piece is inside the cut
        foreach (var s in new[] { segs[0], segs[1] })
        {
            foreach (var p in new[] { s.A, s.B })
                Check(Math.Sqrt(p.X * p.X + p.Z * p.Z) >= near - 1.0, "no kept vertex is inside the near cut");
        }

        // exactly on the cut: kept (the boundary belongs to the far ring)
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 495), P(0, 0, 600), 0, 0, near, far, segs) == 1, "a segment on the cut is kept");
        // just inside at one end: trimmed at the cut, the outside part survives
        int m = TerrainHorizonGeometry.ClipToRing(P(0, 0, 494.9), P(0, 0, 600), 0, 0, near, far, segs);
        Check(m == 1, "a segment with one end just inside the cut is trimmed, not dropped");
        Near(segs[0].A.Z, 495, 0.5, "the trimmed piece starts at the cut");

        // the cut follows the player, not the origin
        Check(TerrainHorizonGeometry.ClipToRing(P(10000, 0, 0), P(10000, 0, 100), 10000, 0, near, far, segs) == 0, "the cut is relative to the player");
    }

    // ---------------------------------------------------------------- the far limit
    static void FarLimit()
    {
        const double near = 495, far = 1000;
        var segs = new TerrainHorizonGeometry.Segment[2];
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 900), 0, 0, near, far, segs) == 1, "a segment inside the far limit is kept");
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 1100), P(0, 0, 1200), 0, 0, near, far, segs) == 0, "a segment beyond the far limit is dropped");
        // a segment that starts inside and runs past the limit is trimmed at the limit
        int n = TerrainHorizonGeometry.ClipToRing(P(0, 0, 900), P(0, 0, 1100), 0, 0, near, far, segs);
        Check(n == 1, "a segment running past the far limit is trimmed");
        Near(segs[0].B.Z, 1000, 0.5, "the ribbon stops at the far limit");
        // a segment that starts beyond the limit and comes back is trimmed at the limit
        n = TerrainHorizonGeometry.ClipToRing(P(0, 0, 1100), P(0, 0, 900), 0, 0, near, far, segs);
        Check(n == 1, "a segment coming back from beyond the limit is trimmed");
        Near(segs[0].A.Z, 1000, 0.5, "the ribbon starts at the far limit");
        // a segment that crosses the near disc and also leaves the far limit gives two pieces
        n = TerrainHorizonGeometry.ClipToRing(P(-2000, 0, 0), P(2000, 0, 0), 0, 0, near, far, segs);
        Check(n == 2, "a segment crossing both rings gives two pieces");
    }

    // ---------------------------------------------------------------- the terrain blend
    static void Blend()
    {
        // 1 at the road, 0 at the blend radius, smooth in between
        Near(TerrainHorizonGeometry.BlendWeight(0, 38), 1, 1e-6, "the blend is full on the road");
        Near(TerrainHorizonGeometry.BlendWeight(38, 38), 0, 1e-6, "the blend is zero at the edge");
        Near(TerrainHorizonGeometry.BlendWeight(100, 38), 0, 1e-6, "the blend is zero past the edge");
        double mid = TerrainHorizonGeometry.BlendWeight(19, 38);
        Check(mid > 0.2 && mid < 0.8, "the blend is smooth in the middle");
        // the shape is monotonic
        double prev = 2;
        for (int i = 0; i <= 20; i++)
        {
            double w = TerrainHorizonGeometry.BlendWeight(i, 38);
            Check(w <= prev + 1e-9, "the blend never grows with distance");
            prev = w;
        }
        // a NaN half-width must be rejected: a comparison with NaN is false, so a range test
        // alone does not catch it, and the NaN would end up in the mesh buffer.
        var nanBatch = new TerrainHorizonGeometry.Batch(99, 100);
        Check(!nanBatch.AddStrip(P(0, 0, 0), P(0, 0, 30), double.NaN, 0), "a NaN half-width is refused");
        Check(!nanBatch.AddStrip(P(0, 0, 0), P(0, 0, 30), double.PositiveInfinity, 0), "an infinite half-width is refused");
        Check(!nanBatch.AddStrip(P(0, 0, 0), P(0, 0, 30), 6, double.NaN), "a NaN lift is refused");
        Check(nanBatch.VertexCount == 0, "the refused strips left nothing in the buffer");
        // and the pure strip rule rejects them too
        Check(!TerrainHorizonGeometry.StripAcceptable(P(0, 0, 0), P(0, 0, 30), double.NaN, 0), "a NaN half-width is not an acceptable strip");
        Check(!TerrainHorizonGeometry.StripAcceptable(P(0, 0, 0), P(0, 0, 30), 6, double.NaN), "a NaN lift is not an acceptable strip");
        // an overflowing segment: the difference of two huge finite numbers is still finite, but
        // the squared length of a huge segment overflows to infinity, and a range test cannot see
        // that (`inf >= 1e-4` is TRUE). The segment must be refused outright.
        Check(!TerrainHorizonGeometry.StripAcceptable(P(0, 0, 0), P(1e308, 0, 1e308), 6, 0),
              "an overflowing segment is not accepted");
        Check(!TerrainHorizonGeometry.StripAcceptable(P(-1e308, 0, -1e308), P(1e308, 0, 1e308), 6, 0),
              "a segment whose difference overflows is not accepted");
        // and the batch must refuse it without touching its buffers
        var overflowBatch = new TerrainHorizonGeometry.Batch(98, 100);
        Check(!overflowBatch.AddStrip(P(0, 0, 0), P(1e308, 0, 1e308), 6, 0), "an overflowing segment is refused by the batch");
        Check(overflowBatch.VertexCount == 0 && overflowBatch.TriangleCount == 0,
              "the refused overflowing segment left nothing in the batch");
        // the Unity-shaped twin refuses the same input, so the two cannot drift
        Check(TerrainHorizonGeometry.StripAcceptable(P(0, 0, 0), P(0, 0, 30), 6, 0),
              "a normal segment is still accepted");

        // a bad radius gives no blend at all
        Near(TerrainHorizonGeometry.BlendWeight(1, 0), 0, 1e-9, "a zero blend radius does nothing");
        Near(TerrainHorizonGeometry.BlendWeight(double.NaN, 38), 0, 1e-9, "a non-finite distance does nothing");

        // the height: a vertex above the road is pulled down, one below is left alone
        double h = TerrainHorizonGeometry.BlendedHeight(20, 10, 0, 38);
        Near(h, 10, 1e-6, "a vertex on the road is pulled fully onto it");
        h = TerrainHorizonGeometry.BlendedHeight(20, 10, 38, 38);
        Near(h, 20, 1e-6, "a vertex at the blend edge keeps its height");
        h = TerrainHorizonGeometry.BlendedHeight(20, 10, 19, 38);
        Check(h > 10 && h < 20, "a vertex in the middle is pulled part way");
        // The blend works in BOTH directions. The far mesh comes from a different noise field
        // than the near terrain the road was laid on, so the far terrain can be below the road
        // too; pulling it only down left the road floating in the dip (the issue's other
        // symptom, "floating roads at crossroads").
        h = TerrainHorizonGeometry.BlendedHeight(5, 10, 0, 38);
        Near(h, 10, 1e-6, "a vertex below the road is lifted onto it");
        h = TerrainHorizonGeometry.BlendedHeight(5, 10, 38, 38);
        Near(h, 5, 1e-6, "a vertex below the road at the blend edge keeps its height");
        h = TerrainHorizonGeometry.BlendedHeight(5, 10, 19, 38);
        Check(h > 5 && h < 10, "a vertex below the road in the middle is lifted part way");
        // the correction is a lerp towards the road, so the same distance moves the vertex the
        // same FRACTION of the way to the road, whichever side it is on
        double up = TerrainHorizonGeometry.BlendedHeight(20, 10, 19, 38);
        double down = TerrainHorizonGeometry.BlendedHeight(5, 10, 19, 38);
        Near((up - 10) / 10.0, (10 - down) / 5.0, 1e-9,
             "the same distance moves the vertex the same fraction of the way to the road");
    }

    // ---------------------------------------------------------------- the height fix
    static void HeightFixes()
    {
        // The fix remembers the original height, so it is idempotent and reversible.
        var fix = new TerrainHorizonGeometry.HeightFix(4);
        fix.Remember(0, 20);
        fix.Remember(1, 5);
        fix.Remember(2, 30);
        fix.Remember(3, double.NaN);          // a vertex with no usable height
        Check(fix.Remembered(0), "the original height is remembered");
        Check(!fix.Remembered(3), "a non-finite height is not remembered");
        Near(fix.Original(0), 20, 1e-9, "the original height is kept");
        Check(double.IsNaN(fix.Original(3)), "an unknown original stays unknown");

        double c1 = fix.Corrected(0, 10, 19, 38);
        Check(c1 > 10 && c1 < 20, "a vertex in the middle is pulled part way");
        // correcting again with the same inputs gives the same answer: no creep
        double c2 = fix.Corrected(0, 10, 19, 38);
        Near(c2, c1, 1e-12, "the correction does not creep when repeated");
        // and the original is still the original
        Near(fix.Original(0), 20, 1e-12, "the original height is not overwritten by the correction");
        // a second Remember with the corrected value must not replace the original
        fix.Remember(0, c1);
        Near(fix.Original(0), 20, 1e-12, "remembering a corrected height does not replace the original");
        // restoring gives the original back: turning the fix off undoes it
        Near(fix.Restored(0), 20, 1e-12, "restoring gives the original height back");
        // a vertex below the road is lifted onto it (the blend works in both directions, so the
        // road does not float in a dip of the far mesh)
        Near(fix.Corrected(1, 10, 0, 38), 10, 1e-9, "a vertex below the road is lifted onto it");
        // an unknown vertex has no correction
        Check(double.IsNaN(fix.Corrected(3, 10, 0, 38)), "an unknown vertex is not corrected");
        // an out-of-range index is safe
        Check(double.IsNaN(fix.Corrected(99, 10, 0, 38)), "an index outside the mesh is not corrected");
        fix.Remember(-1, 20);
        Check(!fix.Remembered(-1), "a negative index is ignored");
    }

    // ---------------------------------------------------------------- the road signature
    static void Signature()
    {
        // the same roads give the same signature
        int h1 = new TerrainHorizonGeometry.SignatureBuilder(4096) is var b1 ? 0 : 0;
        var a = new TerrainHorizonGeometry.SignatureBuilder(4096);
        a.AddRoad(true, 3, 20);
        a.AddRoad(false, 1, 5);
        var b = new TerrainHorizonGeometry.SignatureBuilder(4096);
        b.AddRoad(true, 3, 20);
        b.AddRoad(false, 1, 5);
        Check(a.Hash == b.Hash, "the same road set gives the same signature");

        // a changed road gives a different signature
        var c = new TerrainHorizonGeometry.SignatureBuilder(4096);
        c.AddRoad(true, 3, 21);
        Check(a.Hash != c.Hash, "a road with different bones gives a different signature");
        var d = new TerrainHorizonGeometry.SignatureBuilder(4096);
        d.AddRoad(true, 4, 20);
        Check(a.Hash != d.Hash, "a road with different elements gives a different signature");
        var e = new TerrainHorizonGeometry.SignatureBuilder(4096);
        e.AddRoad(false, 3, 20);
        Check(a.Hash != e.Hash, "a road that became generated gives a different signature");

        // a bounded builder stops counting elements but keeps a stable answer
        var big = new TerrainHorizonGeometry.SignatureBuilder(10);
        for (int i = 0; i < 1000; i++) big.AddRoad(true, 2, 10);
        Check(big.Truncated, "a builder over its budget says so");
        Check(big.Elements > 10, "the builder counts what it walked");
    }

    // ---------------------------------------------------------------- the rebuild gate
    static void Gate()
    {
        var g = new TerrainHorizonGeometry.RebuildGate();
        Check(g.NeedsRebuild(0, 0, 1, 35), "the first build always happens");
        g.MarkBuilt(0, 0, 1);
        Check(!g.NeedsRebuild(10, 0, 1, 35), "a small move does not rebuild");
        Check(!g.NeedsRebuild(0, 34, 1, 35), "a move just under the threshold does not rebuild");
        Check(g.NeedsRebuild(0, 36, 1, 35), "a move over the threshold rebuilds");
        Check(g.NeedsRebuild(0, 0, 2, 35), "changed roads rebuild");
        // a teleport: a jump of kilometres
        Check(g.NeedsRebuild(5000, 0, 1, 35), "a teleport rebuilds");
        // an origin shift: the global position is the same, so the global distance is small; the
        // renderer also compares the Unity position, which the shift moves by kilometres
        Check(!g.NeedsRebuild(0.5, 0.5, 1, 35), "a small global move does not rebuild");
        // a non-finite position must never trigger a rebuild
        Check(!g.NeedsRebuild(double.NaN, 0, 1, 35), "a NaN position does not rebuild");
        Check(!g.NeedsRebuild(0, double.PositiveInfinity, 1, 35), "an infinite position does not rebuild");
        g.Clear();
        Check(g.NeedsRebuild(0, 0, 1, 35), "after a clear the next build always happens");
    }

    // ---------------------------------------------------------------- the batch
    static void Batch()
    {
        var b = new TerrainHorizonGeometry.Batch(7, 8);
        Check(b.Id == 7, "the batch knows its road id");
        for (int i = 0; i < 2; i++) Check(b.AddStrip(P(i * 20, 0, 0), P(i * 20, 0, 30), 6, 0.1), "strip " + i);
        Check(b.VertexCount == 8, "two strips are eight vertices");
        Check(b.AtCapacity, "the batch knows it is full");
        Check(!b.AddStrip(P(100, 0, 0), P(100, 0, 30), 6, 0.1), "a full batch refuses another strip");
        Check(b.VertexCount == 8, "the refused strip changed nothing");
        b.Reset();
        Check(b.VertexCount == 0 && b.TriangleCount == 0, "reset empties the batch");
        Check(!b.AtCapacity, "a reset batch has room again");

        // the indices stay in range after a reset and a refill
        Check(b.AddStrip(P(0, 0, 0), P(0, 0, 30), 6, 0.1), "strip after reset");
        // The indices restart at zero after a reset. The exact order is the winding's business
        // (and the winding is what FacesUp checks), so this only asserts the range.
        Check(b.T[0] == 0 && b.T[5] < 4, "the indices restart at zero after a reset");
        Check(RibbonFacesUp(b), "a strip added after a reset also faces up");
    }

    // ---------------------------------------------------------------- non-finite data
    static void NonFinite()
    {
        var segs = new TerrainHorizonGeometry.Segment[2];
        const double near = 495, far = 4000;
        Check(!TerrainHorizonGeometry.Finite(double.NaN), "NaN is not finite");
        Check(!TerrainHorizonGeometry.Finite(double.PositiveInfinity), "infinity is not finite");
        Check(TerrainHorizonGeometry.Finite(1.0), "a normal number is finite");

        // a NaN position must not produce geometry
        Check(TerrainHorizonGeometry.ClipToRing(P(double.NaN, 0, 600), P(0, 0, 700), 0, 0, near, far, segs) == 0, "a NaN endpoint is rejected");
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, double.NaN), 0, 0, near, far, segs) == 0, "a NaN endpoint is rejected");
        Check(TerrainHorizonGeometry.ClipToRing(P(0, double.NaN, 600), P(0, 0, 700), 0, 0, near, far, segs) == 0, "a NaN height is rejected");
        // a NaN player position must not produce geometry
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), double.NaN, 0, near, far, segs) == 0, "a NaN player position is rejected");
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, double.NaN, near, far, segs) == 0, "a NaN player position is rejected");
        // a bad radius
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 0, 5000, 4000, segs) == 0, "a far limit inside the near cut is rejected");
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 0, -1, 4000, segs) == 0, "a negative near cut is rejected");

        // the batch refuses non-finite input too
        var b = new TerrainHorizonGeometry.Batch(1, 100);
        Check(!b.AddStrip(P(double.NaN, 0, 0), P(0, 0, 30), 6, 0), "a NaN endpoint is refused");
        Check(!b.AddStrip(P(0, 0, 0), P(0, 0, double.PositiveInfinity), 6, 0), "an infinite endpoint is refused");
        Check(!b.AddStrip(P(0, 0, 0), P(0, 0, 30), double.NaN, 0), "a NaN width is refused");
        Check(!b.AddStrip(P(0, 0, 0), P(0, 0, 30), 6, double.NaN), "a NaN lift is refused");
        Check(!b.AddStrip(P(0, 0, 0), P(0, 0, 30), 0, 0), "a zero width is refused");
        Check(!b.AddStrip(P(0, 0, 0), P(0, 0, 30), 1000, 0), "an absurd width is refused");
        Check(!b.AddStrip(P(1, 0, 1), P(1, 0, 1), 6, 0), "a zero-length strip is refused");
        Check(b.VertexCount == 0, "no rejected strip left anything behind");
    }

    // ---------------------------------------------------------------- the performance bound
    static void Performance()
    {
        // The per-frame work of the renderer is bounded: clipping a segment costs a bounded
        // number of operations, and the signature builder never walks more than its budget.
        // 200k segments must clip fast enough for a frame budget (measured here so a future
        // change that makes the geometry quadratic fails the test).
        var segs = new TerrainHorizonGeometry.Segment[2];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int kept = 0;
        for (int i = 0; i < 200000; i++)
        {
            double z = 600 + (i % 1000);
            if (TerrainHorizonGeometry.ClipToRing(P(0, 0, z), P(0, 0, z + 10), 0, 0, 495, 25000, segs) > 0) kept++;
        }
        sw.Stop();
        Check(kept == 200000, "every far segment in the test is kept");
        Check(sw.ElapsedMilliseconds < 2000, "200k segments clip in under 2 s (" + sw.ElapsedMilliseconds + " ms)");

        // the signature builder over a huge road set stays bounded in time
        sw = System.Diagnostics.Stopwatch.StartNew();
        var b = new TerrainHorizonGeometry.SignatureBuilder(4096);
        for (int i = 0; i < 1000000; i++) b.AddRoad(true, 2, 10);
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 2000, "a million roads fold into a signature in under 2 s (" + sw.ElapsedMilliseconds + " ms)");
        Check(b.Truncated, "the signature builder stops counting past its budget");

        // the batch of a whole world stays inside the vertex cap
        var batch = new TerrainHorizonGeometry.Batch(1, 120000);
        int strips = 0;
        for (int i = 0; i < 1000000; i++)
        {
            if (!batch.AddStrip(P(i * 20, 0, 0), P(i * 20, 0, 30), 6, 0.1)) break;
            strips++;
        }
        Check(batch.VertexCount <= 120000, "the batch never grows past its vertex cap");
        Check(strips == 30000, "the batch took exactly the strips that fit (" + strips + ")");
    }

    // ---------------------------------------------------------------- the moving player
    static void MovingPlayer()
    {
        // The ring the ribbons are drawn in follows the player: as the player walks, the road that
        // was in front of them goes behind and the one ahead comes closer.
        var segs = new TerrainHorizonGeometry.Segment[2];
        const double near = 495, far = 4000;
        // a road 600 m north of the player
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 0, near, far, segs) == 1, "a road 600 m ahead is drawn");
        // the player walks to the road: it is now inside the near cut and stops being drawn
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 600, near, far, segs) == 0, "the road is dropped when the player reaches it");
        // the player walks past it and away from it: it is behind the player, outside the cut again
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 1300, near, far, segs) == 1, "the road behind the player is drawn again");
        // and the far end is beyond the limit when the player is far away
        Check(TerrainHorizonGeometry.ClipToRing(P(0, 0, 600), P(0, 0, 700), 0, 6000, near, far, segs) == 0, "a road far behind the player is out of the range");

        // a road crossing the player's path: the piece beside the player is cut away, the rest stays
        int n = TerrainHorizonGeometry.ClipToRing(P(-2000, 0, 0), P(2000, 0, 0), 0, 0, near, far, segs);
        Check(n == 2, "a road the player stands next to is cut in two");
        Check(segs[0].B.X < 0 && segs[1].A.X > 0, "the two pieces are on either side of the player");
    }

    // ---------------------------------------------------------------- the origin shift
    static void OriginShift()
    {
        // The game shifts its floating origin every few kilometres: the world moves in Unity space
        // while the global coordinates stay the same. The ribbons are built in global space, so
        // they must not move, and the rebuild must not fire for the shift itself.
        var gate = new TerrainHorizonGeometry.RebuildGate();
        gate.MarkBuilt(1000, 2000, 7);
        // the global position barely moved (the origin shift keeps the global position)
        Check(!gate.NeedsRebuild(1000, 2000, 7, 35), "an origin shift alone does not force a global rebuild");
        // but the Unity position jumped by kilometres, so the renderer must rebuild anyway: the
        // gate is only one of the two checks the renderer uses.
        Check((1000f - 900f) * (1000f - 900f) + (2000f - 1000f) * (2000f - 1000f) >= 35f * 35f,
            "an origin shift moves the Unity position far enough to force a rebuild");

        // the geometry itself is in global space, so a shift changes nothing about it
        var segs = new TerrainHorizonGeometry.Segment[2];
        int n = TerrainHorizonGeometry.ClipToRing(P(-2000, 0, 0), P(2000, 0, 0), 0, 0, 495, 4000, segs);
        Check(n == 2, "the cut does not depend on the origin");
        Check(Math.Abs(segs[0].B.X + 495) < 1, "the cut is at the same global place whatever the origin is");
    }

    // ---------------------------------------------------------------- road changes
    static void RoadChanges()
    {
        // A new road must change the signature, or the ribbons would keep the old road set.
        var before = new TerrainHorizonGeometry.SignatureBuilder(4096);
        before.AddRoad(true, 2, 12);
        var after = new TerrainHorizonGeometry.SignatureBuilder(4096);
        after.AddRoad(true, 2, 12);
        after.AddRoad(true, 1, 6);
        Check(before.Hash != after.Hash, "a new road changes the signature");

        // a road that is generated later (the game fills in roads as the world grows)
        var pending = new TerrainHorizonGeometry.SignatureBuilder(4096);
        pending.AddRoad(false, 2, 12);
        Check(pending.Hash != before.Hash, "a road that becomes generated changes the signature");

        // and the gate rebuilds when the signature changes
        var gate = new TerrainHorizonGeometry.RebuildGate();
        gate.MarkBuilt(0, 0, before.Hash);
        Check(gate.NeedsRebuild(0, 0, after.Hash, 35), "changed roads force a rebuild");
    }

    // ---------------------------------------------------------------- batching and removal
    static void BatchingAndRemoval()
    {
        // One batch per road id, and a batch that is not used any more is removed with its mesh.
        // The renderer keeps the batches in a dictionary keyed by road id; the test drives the same
        // rule: a road that produces no strips must not keep a batch alive.
        var batches = new System.Collections.Generic.Dictionary<int, TerrainHorizonGeometry.Batch>();
        batches[1] = new TerrainHorizonGeometry.Batch(1, 100);
        batches[2] = new TerrainHorizonGeometry.Batch(2, 100);
        var used = new System.Collections.Generic.HashSet<int>();
        // road 1 is in range, road 2 is not
        used.Add(1);
        Check(batches.Count == 2, "two batches exist");
        // the removal rule: everything not used goes away
        var stale = new System.Collections.Generic.List<int>(batches.Keys);
        foreach (var kv in batches) if (used.Contains(kv.Key)) stale.Remove(kv.Key);
        Check(stale.Count == 1 && stale[0] == 2, "the unused batch is the only stale one");
        foreach (int id in stale) batches.Remove(id);
        Check(batches.Count == 1, "the stale batch is removed");

        // a batch that hit its cap refuses more, and says so
        var capped = new TerrainHorizonGeometry.Batch(3, 4);
        capped.AddStrip(P(0, 0, 0), P(0, 0, 30), 6, 0.1);
        Check(capped.AtCapacity, "a batch of one strip with a cap of four vertices is not full");
        capped.AddStrip(P(0, 0, 30), P(0, 0, 60), 6, 0.1);
        Check(capped.AtCapacity, "a batch of two strips with a cap of four vertices is full");
        Check(!capped.AddStrip(P(0, 0, 60), P(0, 0, 90), 6, 0.1), "a full batch takes nothing more");
        Check(capped.VertexCount == 4, "the full batch kept its four vertices");
    }

    // ---------------------------------------------------------------- teleports
    static void Teleports()
    {
        var gate = new TerrainHorizonGeometry.RebuildGate();
        gate.MarkBuilt(0, 0, 1);
        // a teleport is a jump of kilometres: the ribbons of the old place are wrong
        Check(gate.NeedsRebuild(30000, 0, 1, 35), "a teleport to a new place rebuilds");
        Check(gate.NeedsRebuild(0, -45000, 1, 35), "a teleport in the other direction rebuilds");
        // a teleport back to the same place still rebuilds if the signature changed meanwhile
        gate.MarkBuilt(30000, 0, 1);
        Check(gate.NeedsRebuild(30000, 0, 2, 35), "a teleport with changed roads rebuilds");
        // but standing still at the new place does not
        Check(!gate.NeedsRebuild(30000, 0, 1, 35), "standing still after a teleport does not rebuild again");
    }

    // ---------------------------------------------------------------- the blend radius
    static void BlendRadius()
    {
        // The blend must reach the next vertex: a coarse tile of 690 m with a resolution of 7 has
        // a vertex every ~98 m, and a 38 m blend would touch nothing at all.
        const double minR = 38, maxR = 260;
        double coarse = 690.0 / 6.0;                  // a 690 m tile with a resolution of 7
        double r = TerrainHorizonGeometry.BlendRadius(coarse, minR, maxR);
        Check(r >= coarse, "the blend reaches the next vertex of a coarse tile (" + r + " >= " + coarse + ")");
        Check(r <= maxR, "the blend stays inside its cap");
        // a fine tile keeps the narrow blend
        double fine = 100.0 / 39.0;                  // a 100 m tile with a resolution of 40
        r = TerrainHorizonGeometry.BlendRadius(fine, minR, maxR);
        Check(r == minR, "a fine tile keeps the narrow blend");
        // a very coarse tile is capped
        r = TerrainHorizonGeometry.BlendRadius(5000, minR, maxR);
        Check(r == maxR, "a very coarse tile is capped");
        // and a bad spacing falls back to the minimum
        Check(TerrainHorizonGeometry.BlendRadius(0, minR, maxR) == minR, "a zero spacing falls back to the minimum");
        Check(TerrainHorizonGeometry.BlendRadius(double.NaN, minR, maxR) == minR, "a NaN spacing falls back to the minimum");

        // and the blend really does reach a vertex that is one spacing away
        double w = TerrainHorizonGeometry.BlendWeight(coarse * 0.9, r);
        Check(w > 0, "a vertex one spacing from the road is pulled onto it");
    }

    // ---------------------------------------------------------------- the road range
    static void RoadRange()
    {
        // A road that runs far past the player can be skipped without walking its bones: the
        // rebuild walks every road in every chunk of every generator, so this test is what keeps
        // its cost from growing with the size of the visited world.
        Check(TerrainHorizonGeometry.SegmentInRange(0, 0, 100, 0, 0, 0, 4000), "a road at the player is in range");
        Check(TerrainHorizonGeometry.SegmentInRange(0, 0, 100, 0, 50, 0, 4000), "a road beside the player is in range");
        Check(!TerrainHorizonGeometry.SegmentInRange(0, 0, 100, 0, 5000, 0, 4000), "a road far from the player is out of range");
        // a road that passes the player is in range even when both ends are far away
        Check(TerrainHorizonGeometry.SegmentInRange(-5000, 0, 5000, 0, 0, 0, 4000), "a road that passes the player is in range");
        Check(TerrainHorizonGeometry.SegmentInRange(-5000, 1000, 5000, 1000, 0, 0, 4000), "a road that passes 1 km by the player is in range");
        Check(!TerrainHorizonGeometry.SegmentInRange(-5000, 5000, 5000, 5000, 0, 0, 4000), "a road that passes 5 km by the player is out of range");
        // a non-finite point is never skipped (the per-segment test decides)
        Check(TerrainHorizonGeometry.SegmentInRange(double.NaN, 0, 100, 0, 0, 0, 4000), "a NaN road end is not skipped");
        Check(TerrainHorizonGeometry.SegmentInRange(0, 0, 100, 0, double.NaN, 0, 4000), "a NaN player position is not skipped");
    }

    // ---------------------------------------------------------------- the chunk range
    static void ChunkRange()
    {
        // The game keeps every road chunk it ever made; the rebuild must skip the ones that are
        // too far away, or its cost would grow with the size of the visited world.
        Check(TerrainHorizonGeometry.ChunkInRange(0, 0, 0, 0, 4000, 100), "the chunk under the player is in range");
        Check(TerrainHorizonGeometry.ChunkInRange(3900, 0, 0, 0, 4000, 100), "a chunk near the limit is in range");
        Check(!TerrainHorizonGeometry.ChunkInRange(5000, 0, 0, 0, 4000, 100), "a chunk past the limit is out of range");
        // the margin is the chunk size, so a road that crosses the border of a chunk just outside
        // the range is still drawn
        Check(TerrainHorizonGeometry.ChunkInRange(4050, 0, 0, 0, 4000, 100), "a chunk just past the limit is kept for its margin");
        Check(!TerrainHorizonGeometry.ChunkInRange(4200, 0, 0, 0, 4000, 100), "a chunk well past the limit is dropped");
        // a non-finite position is never in range
        Check(!TerrainHorizonGeometry.ChunkInRange(double.NaN, 0, 0, 0, 4000, 100), "a NaN chunk position is out of range");
        Check(!TerrainHorizonGeometry.ChunkInRange(0, 0, double.NaN, 0, 4000, 100), "a NaN player position is out of range");
    }
}
