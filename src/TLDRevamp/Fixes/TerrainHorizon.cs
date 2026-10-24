using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp.Fixes
{
    /// Issue #37: keep the game's near 500 m world intact, correct the coarse far mesh where a road crosses it, and draw
    /// only lightweight road ribbons beyond the overlap. Far terrain uses a separate noise generator from the playable
    /// terrain; raising DrawDistance alone therefore exposed roads floating over unrelated hills.
    ///
    /// Two independent switches, each on its own:
    ///  - `CorrectFar` pulls the coarse distant/far terrain vertices onto the road height near real roads, so the
    ///    road is not buried in a hill. The original heights are kept, so the fix is idempotent and turning it off
    ///    puts the terrain back.
    ///  - `DistantRoads` draws lightweight road ribbons beyond the detailed area, so a road that continues past the
    ///    detailed world is still visible and lands on the corrected terrain.
    ///
    /// The geometry itself lives in TerrainHorizonGeometry, which has no Unity types: the headless unit test runs
    /// exactly the code the game runs. All work is pooled and bounded to one rebuild per motion threshold.
    public static partial class TerrainHorizon
    {
        internal static bool CorrectFar = true;
        internal static bool DistantRoads = true;
        public static bool RibbonLocalSpace = true;
        /// The fallback radius at which the ribbon starts, when the game's own near-terrain draw
        /// distance cannot be read. The game's near camera clips at 500 m (mainscript.cs), so the
        /// detailed world reaches that far; the ribbon must not be drawn inside it.
        internal const float NearCutM = 495f;
        /// The default blend width. A coarse tile's vertices are far apart (a 690 m tile with a
        /// resolution of 7 has a vertex every ~98 m), so the blend has to be at least that wide to
        /// reach a vertex at all; the real width is derived from the tile's own vertex spacing.
        internal const float BlendM = 38f;
        /// The widest the blend may grow to, so a very coarse tile does not flatten a whole region.
        internal const float MaxBlendM = 260f;
        internal const float MoveRebuildM = 35f;
        /// A rebuild is spread over frames: the strips a slice at a time, then each road's mesh swapped in, StepBudgetMs of
        /// work a frame (at least one element or one mesh). In one frame it walked every road in the view distance and
        /// re-uploaded every mesh: ~80 ms every 35 m driven at a 25 km view distance, a stutter about once a second at
        /// 120 km/h (playtest 2026-10-23, "constant-stutter": Runner.Update 79-90 ms every 33-36 m). The ribbons it
        /// replaces stay on screen until their road is done; a road whose strips came out the same is not uploaded again.
        public static bool SpreadRebuild = true;   // A/B: false = the whole rebuild in one frame (before v0.69.2)
        internal static double StepBudgetMs = 2.0;
        /// How far the ribbon sits above the road it is drawn on, in metres.
        ///
        /// The road mesh of the game is drawn on the ground; a ribbon that is lifted above it
        /// shows a step where it meets the near terrain, and a ribbon that is sunk into it
        /// disappears. The lift is kept small and positive so the ribbon wins against the far
        /// terrain's own noise instead of z-fighting with it.
        internal const float RibbonLiftM = 0.08f;

        /// The widest a drawn road may be, in metres, and the most vertices one road's batch may hold.
        internal const float MaxRoadHalfWidthM = 12f;
        internal const int MaxBatchVertices = 120000;
        /// The far ring ends here: the game's own draw distance, but never inside the near cut.
        internal const float MinDrawM = 500f, MaxDrawM = 25000f;

        /// How often the road signature is recomputed. The signature answers "did the roads change?" and
        /// walking the world's road chunks is not free, so it is sampled rather than done every frame.
        internal const int SignatureIntervalFrames = 30;
        /// The most traversal entries the signature may visit. The budget includes generators,
        /// chunks, roads, and road elements in both the count and sample passes; a signature is a
        /// fingerprint, not a census, and the walk stops once this bound is reached.
        internal const int SignatureMaxElements = 4096;
        /// The most roads the signature walks. The game keeps every road chunk it ever made, so a
        /// walk without a bound costs O(the size of the visited world) every time the signature is
        /// taken. The walk stops after this many roads; what it saw is still folded into the hash,
        /// so a world with more roads than the bound still gives a different signature.
        internal const int SignatureMaxRoads = 512;

        internal static long FarMeshesCorrected, FarVerticesCorrected, Rebuilds, Failed;
        internal static long BatchesRemoved, SignaturesTaken, SignatureElements, SignatureVisits;
        internal static long StripClipped, StripsAdded, StripsRejected;
        /// How often a road was cut short because its batch reached the vertex cap. A road that
        /// silently disappears is worse than a road that is a little short, so the count is kept
        /// and reported.
        internal static long StripsCapped;
        internal static double LastRebuildMs, LastSignatureMs, MaxRebuildMs;
        /// The most rebuild work done in one frame (the hitch it can cause), the frames the last rebuild took, meshes
        /// uploaded and skipped (unchanged), rebuilds dropped unfinished (the world went, the switch went off).
        internal static double LastStepMaxMs, MaxStepMs;
        internal static int LastRebuildFrames;
        internal static long Uploads, UploadsSkipped, RebuildsDropped;

        private static Vector3 _builtUnityAt;
        private static bool _hasBuilt;
        private static bool _farFixWasOn;
        private static int _roadSignature;
        private static int _signatureFrame = -1;
        private static int _signatureValue;
        private static Transform _parent;
        private static readonly Dictionary<int, Batch> Batches = new Dictionary<int, Batch>();
        private static readonly List<int> Stale = new List<int>();
        private static readonly Dictionary<int, Material> Materials = new Dictionary<int, Material>();
        /// The fallback materials this fix created. The road prefab's own material is a shared game
        /// asset: it must never be destroyed here, only the ones we made ourselves.
        private static readonly List<Material> OwnedMaterials = new List<Material>();
        private static readonly TerrainHorizonGeometry.RebuildGate Gate = new TerrainHorizonGeometry.RebuildGate();

        private sealed class Batch
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            /// The strips on screen, and the ones the rebuild in progress writes (swapped in by Upload).
            public TerrainHorizonGeometry.Batch Data, Next;
            public bool Capped;
            public Vector3[] Vertices = new Vector3[0];
            public Vector2[] Uvs = new Vector2[0];
            public Batch(int id) { Data = new TerrainHorizonGeometry.Batch(id, MaxBatchVertices); Next = new TerrainHorizonGeometry.Batch(id, MaxBatchVertices); }
        }

        internal static void Tick()
        {
            // The two switches are independent: turning the far fix off puts the terrain back even
            // while the road ribbons keep running, and vice versa.
            if (!CorrectFar && _farFixWasOn) { RestoreFarMeshes(); _farFixWasOn = false; }
            if (!DistantRoads || settingsscript.s == null || !settingsscript.s.S.GetDistantTerrain() ||
                mainscript.s == null || mainscript.s.player == null) { Clear(); return; }
            if (CorrectFar) _farFixWasOn = true;

            var unityAt = mainscript.s.player.transform.position;
            var at = mainscript.GlobalFromUnityPos(unityAt);
            // A player position that is not a number would make every distance test false and pull the
            // whole world's roads into one mesh. Refuse to work with it and keep what we have.
            if (!TerrainHorizonGeometry.Finite(at.x, at.z) || !TerrainHorizonGeometry.Finite(unityAt.x, unityAt.z))
            {
                NonFinitePositions++;
                ResetOriginObservation();
                return;
            }

            // The cheap movement test comes FIRST. The road signature walks the world's road set,
            // so computing it before the movement test meant paying for it on every single frame
            // just to discover that nothing had changed. The signature is only needed when a
            // rebuild is actually due.
            if (_job != null) { Step(); return; }   // the rebuild in progress first: it is for a point at most a step back
            bool moved = !Gate.HasBuilt ||
                         (unityAt - _builtUnityAt).sqrMagnitude >= MoveRebuildM * MoveRebuildM;
            // Was this rebuild caused by the game shifting the world? The shift moves the player's
            // Unity position by kilometres while their global position stays the same, so it is
            // told apart from an ordinary walk (both move) and from a teleport (the global
            // position moves too). Compare with the previous frame's observation, not the previous
            // rebuild. The decision is taken BEFORE the rebuild, because the rebuild overwrites
            // the position used by the rebuild gate.
            bool originShift = IsOriginShift(at, unityAt);
            if (!moved)
            {
                // The player has not moved far enough. The roads may still have changed, but that is
                // what the signature is for, and it is sampled (SignatureIntervalFrames) rather
                // than taken every frame.
                int sig = RoadSignature();
                if (!Gate.NeedsRebuild(at.x, at.z, sig, MoveRebuildM)) return;
                Rebuild(at, sig);
                if (originShift) OriginShifts++;
                return;
            }
            Rebuild(at, RoadSignature());
            if (originShift) OriginShifts++;
        }

        /// A cheap fingerprint of the road set. It counts the chunks, the roads in them and the road
        /// elements, and stops after a bounded number of elements: a per-frame walk of every bone of
        /// every road in the world was the single most expensive thing this fix did.
        private static int RoadSignature()
        {
            if (_signatureFrame >= 0 && Time.frameCount - _signatureFrame < SignatureIntervalFrames) return _signatureValue;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var builder = new TerrainHorizonGeometry.SignatureBuilder(SignatureMaxElements, SignatureMaxRoads);
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != null && map.roadGens != null)
                foreach (var gen in map.roadGens)
                {
                    if (!builder.TryVisit()) goto SignatureDone;
                    if (gen == null || gen.chunks == null) continue;
                    builder.Add(gen.chunks.Count);
                    foreach (var kv in gen.chunks)
                    {
                        if (!builder.TryVisit()) goto SignatureDone;
                        if (!builder.AcceptsMoreRoads) break;   // the walk is bounded, not only the count
                        var chunk = kv.Value;
                        if (chunk == null || chunk.roads == null) continue;
                        builder.Add(chunk.roads.Count);
                        foreach (var road in chunk.roads)
                        {
                            if (!builder.TryVisit()) goto SignatureDone;
                            if (road == null) continue;
                            if (!builder.AcceptsMoreRoads) break;
                            int elements = road.roadElements != null ? road.roadElements.Count : 0;
                            builder.AddRoad(road.generated, elements, 0);
                            // Bone counts are folded in per visited element, rather than by an
                            // unbounded preliminary walk, so even a long bounded road prefix can
                            // contribute its count and spatial fingerprint before the visit budget
                            // is exhausted.
                            builder.AddPoint(road.start.x, road.start.y, road.start.z);
                            builder.AddPoint(road.end.x, road.end.y, road.end.z);
                            if (road.roadElements != null)
                                foreach (var e in road.roadElements)
                                {
                                    if (!builder.TryVisit()) goto SignatureDone;
                                    if (e == null || e.bonePos == null) continue;
                                    builder.Add(e.bonePos.Count);
                                    if (e.bonePos.Count > 0)
                                    {
                                        // the first, the middle and the last bone of the element
                                        builder.AddPoint(e.bonePos[0].x, e.bonePos[0].y, e.bonePos[0].z);
                                        int mid = e.bonePos.Count / 2;
                                        builder.AddPoint(e.bonePos[mid].x, e.bonePos[mid].y, e.bonePos[mid].z);
                                        int last = e.bonePos.Count - 1;
                                        builder.AddPoint(e.bonePos[last].x, e.bonePos[last].y, e.bonePos[last].z);
                                    }
                                }
                        }
                    }
                }
            SignatureDone:
            // The number of roads seen goes into the hash as well, so a world with more roads than
            // the walk's bound still gives a different signature.
            builder.AddRoadCount(builder.Roads);
            _signatureValue = builder.Hash;
            _signatureFrame = Time.frameCount;
            SignaturesTaken++;
            SignatureElements = builder.Elements;
            SignatureVisits = builder.Visits;
            LastSignatureMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return _signatureValue;
        }

        /// Where the last rebuild's time went, in milliseconds: finding the roads, their strips, the meshes (summed
        /// over the frames it took).
        internal static double LastScanMs, LastBuildMs, LastUploadMs;

        /// A rebuild in progress: the roads in range (found when it starts), how far the strips have got, then the meshes.
        private sealed class Job
        {
            public Vector3d At; public int Signature; public float Limit; public double NearCut;
            public readonly List<roadGenScript.roadClass> Roads = new List<roadGenScript.roadClass>();
            public readonly HashSet<int> Used = new HashSet<int>();
            public readonly List<int> ToUpload = new List<int>();
            public int Road, Element, Upload, Frames;
            public bool Uploading;
            public double Work, StepMax;
        }
        private static Job _job;

        private static void Rebuild(Vector3d at, int signature)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (_parent == null)
                {
                    var parentGo = new GameObject("RevaMP Distant Roads");
                    // The game keeps the world centred by shifting everything when the player strays
                    // past 2500 m (mainscript.VisszaRakas). Every object that carries a visszarako is
                    // moved by that shift; the terrain tiles carry one, and without it the ribbons
                    // stayed behind and drifted kilometres away from the ground they belong to.
                    // The tiles use exactly this: moveWhenParented, so the shift still moves them
                    // when they hang under the map's parent.
                    var vz = parentGo.AddComponent<visszarako>();
                    vz.moveWhenParented = true;
                    vz.dontlookUnderMap = true;
                    _parent = parentGo.transform;
                    Batches.Clear();
                }
                var job = new Job { At = at, Signature = signature };
                foreach (var b in Batches.Values) { b.Next.Reset(); b.Capped = false; }
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                job.Limit = Mathf.Clamp(settingsscript.s.S.FDrawDistance, MinDrawM, MaxDrawM);
                job.NearCut = NearCutRadius();
                if (map != null && map.roadGens != null)
                    foreach (var gen in map.roadGens)
                    {
                        if (gen == null || gen.chunks == null) continue;
                        foreach (var kv in gen.chunks)
                        {
                            if (kv.Value == null || kv.Value.roads == null) continue;
                            // Skip the chunks that are too far away to contribute anything. The game
                            // keeps every chunk it ever made, so walking them all was the other half
                            // of the rebuild's cost.
                            if (!ChunkInRange(kv.Key, at, job.Limit, gen.chunkSize)) continue;
                            // Only the roads the game will place when the player gets there: a chunk it does not
                            // need (the chunks beside the needed ones live for a frame) never has its roads placed,
                            // and a ribbon of one is a road you can see in the distance and never reach.
                            if (!gen.chunksNeeded.Contains(kv.Key)) continue;
                            foreach (var road in kv.Value.roads)
                                if (road != null && road.generated && road.roadElements != null && RoadInRange(road, at, job.Limit)) job.Roads.Add(road);
                        }
                    }
                // the gate moves on now: the ribbons being built are for here
                _builtUnityAt = mainscript.s.player.transform.position;
                _hasBuilt = true;
                _roadSignature = signature;
                Gate.MarkBuilt(at.x, at.z, signature);
                _job = job;
                job.Work = LastScanMs = Ms(started);
                LastBuildMs = LastUploadMs = 0;
            }
            catch (Exception e) { Failed++; _job = null; Plugin.Log.LogWarning("terrain horizon rebuild: " + e.Message); return; }
            Step(started);
        }

        private static double Ms(long since) => (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// This frame's share of the rebuild in progress: strips element by element, then the meshes one by one, until the
        /// budget is spent (all of it in one go with SpreadRebuild off).
        private static void Step(long frameStart = 0)
        {
            var job = _job;
            if (job == null) return;
            long started = frameStart != 0 ? frameStart : System.Diagnostics.Stopwatch.GetTimestamp();
            double budget = SpreadRebuild ? StepBudgetMs : double.PositiveInfinity;
            job.Frames++;
            try
            {
                if (_parent == null) { RebuildsDropped++; _job = null; return; }
                long stripsAt = System.Diagnostics.Stopwatch.GetTimestamp();
                while (!job.Uploading)
                {
                    if (job.Road >= job.Roads.Count)
                    {
                        // every road done: the meshes, the batches this rebuild wrote and the ones it no longer needs
                        job.Uploading = true;
                        job.ToUpload.AddRange(Batches.Keys);
                        break;
                    }
                    var road = job.Roads[job.Road];
                    if (road == null || road.roadElements == null || job.Element >= road.roadElements.Count) { job.Road++; job.Element = 0; continue; }
                    AddElement(road, road.roadElements[job.Element++], job.At, job.Used, job.NearCut);
                    if (Ms(started) >= budget) break;
                }
                LastBuildMs += Ms(stripsAt);
                long uploadAt = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int n = 0; job.Uploading && job.Upload < job.ToUpload.Count && (n == 0 || Ms(started) < budget); n++)   // at least one a frame
                    if (Batches.TryGetValue(job.ToUpload[job.Upload++], out var b)) Upload(b);
                if (job.Uploading && job.Upload >= job.ToUpload.Count)
                {
                    // A road that is no longer drawn is removed completely, with its mesh: leaving the mesh
                    // behind leaked a native mesh every time a road went out of range.
                    Stale.Clear();
                    foreach (int id in Batches.Keys) if (!job.Used.Contains(id)) Stale.Add(id);
                    foreach (int id in Stale)
                    {
                        if (!Batches.TryGetValue(id, out var b)) continue;
                        if (b.Go != null) { b.Go.SetActive(false); b.Go.transform.SetParent(null); UnityEngine.Object.Destroy(b.Go); }
                        if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh);
                        Batches.Remove(id);
                        BatchesRemoved++;
                    }
                    Rebuilds++;
                    _job = null;
                }
                LastUploadMs += Ms(uploadAt);
            }
            catch (Exception e) { Failed++; _job = null; Plugin.Log.LogWarning("terrain horizon rebuild: " + e.Message); }
            double ms = Ms(started);
            job.Work += frameStart != 0 ? ms - job.Work : ms;
            if (ms > job.StepMax) job.StepMax = ms;
            if (ms > MaxStepMs) MaxStepMs = ms;
            if (_job == null)
            {
                LastRebuildMs = job.Work; LastStepMaxMs = job.StepMax; LastRebuildFrames = job.Frames;
                if (LastRebuildMs > MaxRebuildMs) MaxRebuildMs = LastRebuildMs;
            }
        }

        /// The clip buffer, reused between roads so a rebuild does not allocate one per road.
        private static readonly TerrainHorizonGeometry.Segment[] Pieces = new TerrainHorizonGeometry.Segment[2];

        /// Whether a road can reach the ring around the player. The road's own two ends bound it:
        /// if both are further from the player than the draw distance, the road cannot cross the
        /// ring and is skipped without walking its bones.
        internal static bool RoadInRange(roadGenScript.roadClass road, Vector3d at, double limit)
        {
            if (road == null) return false;
            if (!TerrainHorizonGeometry.Finite(at.x, at.z) || !TerrainHorizonGeometry.Finite(limit)) return false;
            if (!TerrainHorizonGeometry.Finite(road.start.x, road.start.z) || !TerrainHorizonGeometry.Finite(road.end.x, road.end.z))
                return true;    // an unusable road: let the per-segment test decide
            return TerrainHorizonGeometry.SegmentInRange(road.start.x, road.start.z, road.end.x, road.end.z, at.x, at.z, limit);
        }

        /// Whether a road chunk can hold anything inside the draw distance. The chunk's own centre is
        /// used, with the chunk's size as the margin, so a road that crosses the border of a chunk
        /// that is just outside the range is still drawn.
        private static bool ChunkInRange(Vector3d key, Vector3d at, float limit, float chunkSize) =>
            TerrainHorizonGeometry.ChunkInRange(key.x, key.z, at.x, at.z, limit, chunkSize);

        /// The radius at which the ribbon starts: where the game's own detailed terrain ends.
        ///
        /// The game's near camera clips at 500 m, and its near terrain covers that far, so a
        /// ribbon drawn inside it fights the real road for the same pixels. The radius is taken
        /// from the terrain generator that draws the near world when it can be found, so the two
        /// stay in step when the game's draw distance changes; the fallback is the camera's own
        /// clip distance.
        internal static double NearCutRadius()
        {
            try
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map != null && map.terrainGen != null)
                    foreach (var gen in map.terrainGen)
                    {
                        if (gen == null || gen.isDistantTerrain) continue;   // the near world only
                        // The detailed ring (DetailedTerrain) may reach further than the game's own distance.
                        float reach = DetailedTerrain.EffectiveDistance(gen);
                        if (reach > 0)
                            return TerrainHorizonGeometry.NearCutFromDrawDistance(reach, NearCutM);
                    }
            }
            catch { }
            return NearCutM;
        }

        private static void AddElement(roadGenScript.roadClass road, roadGenScript.roadElementClass element, Vector3d at, HashSet<int> used, double nearCut)
        {
            float width = road.roadType == roadGenScript.roadTypeEmu.mainRoad ? 16f : road.roadType == roadGenScript.roadTypeEmu.sideRoad ? 12f : 10f;
            double halfWidth = width * 0.5;
            if (halfWidth > MaxRoadHalfWidthM) halfWidth = MaxRoadHalfWidthM;
            var pieces = Pieces;
            if (element == null || element.bonePos == null || element.bonePos.Count < 2 || element.roadID <= 0) return;
            for (int i = 1; i < element.bonePos.Count; i++)
            {
                var p0 = element.bonePos[i - 1]; var p1 = element.bonePos[i];
                var a = new TerrainHorizonGeometry.Point(p0.x, p0.y, p0.z);
                var c = new TerrainHorizonGeometry.Point(p1.x, p1.y, p1.z);
                int pieces1 = TerrainHorizonGeometry.ClipToRing(a, c, at.x, at.z, nearCut, _job.Limit, pieces);
                if (pieces1 == 0) continue;
                for (int k = 0; k < pieces1; k++)
                {
                    StripClipped++;
                    var seg = pieces[k];
                    var unityA = mainscript.UnityPosFromGlobal(new Vector3d(seg.A.X, seg.A.Y, seg.A.Z));
                    var unityB = mainscript.UnityPosFromGlobal(new Vector3d(seg.B.X, seg.B.Y, seg.B.Z));
                    // The game's visszarako already shifts the parent. Mesh vertices are local:
                    // writing rebased Unity positions here would apply that shift a second time.
                    // (A shift between two slices of a rebuild moves both alike: the local points stay right.)
                    if (RibbonLocalSpace)
                    {
                        unityA = _parent.InverseTransformPoint(unityA);
                        unityB = _parent.InverseTransformPoint(unityB);
                    }
                    // A batch is only created when a strip really goes into it: creating one for
                    // a road that contributes nothing left empty batches behind for ever.
                    var probe = new TerrainHorizonGeometry.Point(unityA.x, unityA.y, unityA.z);
                    var probe2 = new TerrainHorizonGeometry.Point(unityB.x, unityB.y, unityB.z);
                    if (!TerrainHorizonGeometry.StripAcceptable(probe, probe2, halfWidth, RibbonLiftM)) { StripsRejected++; continue; }
                    var batch = EnsureBatch(element.roadID);
                    if (batch.Next.AddStrip(probe, probe2, halfWidth, RibbonLiftM))
                    {
                        StripsAdded++;
                        used.Add(element.roadID);
                    }
                    else
                    {
                        StripsRejected++;
                        batch.Capped = true;
                        if (batch.Next.AtCapacity) StripsCapped++;
                    }
                }
            }
        }

        private static void Upload(Batch b)
        {
            bool same = b.Next.SameAs(b.Data);
            var swap = b.Data; b.Data = b.Next; b.Next = swap;
            if (same && b.Mesh != null && b.Go != null && b.Go.activeSelf == (b.Data.VertexCount >= 4)) { UploadsSkipped++; return; }
            Uploads++;
            var data = b.Data;
            bool show = data.VertexCount >= 4;
            if (b.Go != null && b.Go.activeSelf != show) b.Go.SetActive(show);
            if (!show) return;
            if (b.Mesh == null) return;
            if (b.Vertices.Length < data.VertexCount) { b.Vertices = new Vector3[data.VertexCount]; b.Uvs = new Vector2[data.VertexCount]; }
            var vertices = b.Vertices;
            var uvs = b.Uvs;
            for (int i = 0; i < data.VertexCount; i++)
            {
                vertices[i] = new Vector3((float)data.Vx[i], (float)data.Vy[i], (float)data.Vz[i]);
                uvs[i] = new Vector2((float)data.Uu[i], (float)data.Uv[i]);
            }
            b.Mesh.Clear();
            b.Mesh.SetVertices(vertices);
            b.Mesh.SetUVs(0, uvs);
            b.Mesh.SetTriangles(data.T, 0, true);
            b.Mesh.RecalculateNormals();
            // The ribbon spans kilometres while the mesh it replaces is a few metres wide, so the
            // old bounds would let the camera cull it. The bounds are recomputed for the geometry
            // that was just written.
            b.Mesh.RecalculateBounds();
        }

        /// The layer the distant road ribbons must be on.
        ///
        /// The far camera renders the far terrain, and a camera only renders the layers its culling
        /// mask includes. The ribbon is drawn where the far terrain is, so it has to be on the far
        /// terrain's layer: on the default layer it would be drawn by the near camera (which clips
        /// at 500 m, so the ribbon would be cut off exactly where it starts) and not by the far
        /// camera that is supposed to show it.
        internal static int DistantRoadLayer()
        {
            try
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map != null && map.terrainGen != null)
                    foreach (var gen in map.terrainGen)
                        if (gen != null && gen.isDistantTerrain) return gen.layer;
            }
            catch { }
            return 0;
        }

        private static Batch EnsureBatch(int roadID)
        {
            if (Batches.TryGetValue(roadID, out var b)) return b;
            b = new Batch(roadID);
            var go = new GameObject("road " + roadID);
            go.layer = DistantRoadLayer();
            go.transform.SetParent(_parent, false);
            var mf = go.AddComponent<MeshFilter>(); var mr = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh { name = "DistantRoad-" + roadID, indexFormat = IndexFormat.UInt32 }; mf.sharedMesh = mesh;
            mr.sharedMaterial = Material(roadID); mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            b.Go = go; b.Mesh = mesh; b.Filter = mf; b.Renderer = mr;
            Batches[roadID] = b;
            return b;
        }

        private static Material Material(int roadID)
        {
            if (Materials.TryGetValue(roadID, out var m) && m != null) return m;
            // Always the fix's own plain asphalt material. The road prefab's material was tried first, but its
            // shader does not work on a ribbon mesh: seen live, every distant road was drawn pure red (255,0,0).
            var shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            m = new Material(shader) { color = new Color(0.25f, 0.25f, 0.24f) }; m.name = "RevaMP distant road";
            OwnedMaterials.Add(m);          // ours to destroy later
            Materials[roadID] = m; return m;
        }

        private static void Clear()
        {
            ResetOriginObservation();
            // The early return must also look at what this fix owns: the parent GameObject and the
            // fallback materials can exist while the build never finished (the parent is made
            // before the build is marked done, and a fallback material before its batch is
            // registered), and then nothing would ever destroy them.
            if (_job != null) { _job = null; RebuildsDropped++; }
            if (!_hasBuilt && Batches.Count == 0 && _parent == null && OwnedMaterials.Count == 0) return;
            foreach (var b in Batches.Values)
            {
                if (b.Go != null) { b.Go.SetActive(false); UnityEngine.Object.Destroy(b.Go); }
                if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh);
            }
            Batches.Clear();
            Stale.Clear();
            // The parent GameObject and the materials it holds must go too: the parent survived a
            // world change, so the next world inherited a hierarchy that belonged to the last one,
            // and the fallback materials were never released.
            if (_parent != null) { UnityEngine.Object.Destroy(_parent.gameObject); _parent = null; }
            // Only the materials this fix created are destroyed. The road prefab's own material is
            // a shared game asset that other renderers use as well.
            foreach (var m in OwnedMaterials) if (m != null) UnityEngine.Object.Destroy(m);
            OwnedMaterials.Clear();
            Materials.Clear();
            _builtUnityAt = default;
            _roadSignature = 0;
            _hasBuilt = false;
            Gate.Clear();
            _signatureFrame = -1;
        }

        /// Pulls the coarse terrain's vertices onto the road near real roads.
        ///
        /// The original heights are kept, so the correction is idempotent (a second pass gives the same
        /// result) and reversible (turning the switch off puts the terrain back). The correction is
        /// always computed from the *original* height, never from an already corrected one: computing
        /// it from the corrected height made the terrain sink a little further onto the road with
        /// every pass.
        ///
        /// `worldToLocal` turns a world point into the mesh's own vertex space, and `toWorld` turns a
        /// vertex back into a world point; the caller supplies them because the near tiles, the
        /// distant tiles and the far grid all live in different spaces.
        /// The soft budget for the terrain correction, in milliseconds per frame.
        ///
        /// The budget is *soft*: a single call into the game's own road search cannot be
        /// interrupted, so the cap can be overshot by the cost of that one call. What it does
        /// guarantee is that no further road search is started once the budget is spent, and that
        /// a mesh which was cut short is not written back at all.
        internal const double AlignBudgetMs = 4.0;
        private static double _alignSpentThisFrame = -1;
        private static int _alignFrame = -1;

        /// Whether the correction of one mesh may go on, given what has already been spent this
        /// frame and how long the current call has been running.
        internal static bool AlignBudgetLeft(double spentMs, double elapsedMs) =>
            !TerrainHorizonGeometry.AlignBudgetExhausted(spentMs, elapsedMs, AlignBudgetMs);

        internal static int AlignMesh(Mesh mesh, Func<Vector3, Vector3> toWorld, Func<Vector3, Vector3> worldToLocal, Vector3d searchOrigin, double blendRadius)
        {
            if (!CorrectFar || mesh == null) return 0;
            if (Time.frameCount != _alignFrame) { _alignFrame = Time.frameCount; _alignSpentThisFrame = 0; }
            if (!AlignBudgetLeft(_alignSpentThisFrame, 0)) { AlignBudgetSkipped++; return 0; }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            // Everything that is started is charged, whatever the outcome is: the road search and
            // the per-vertex work are the expensive part, and an aborted mesh still cost its time.
            try
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map == null || map.roadGens == null || settingsscript.s == null || !settingsscript.s.S.GetDistantTerrain()) return 0;
                var vertices = mesh.vertices;
                if (vertices == null || vertices.Length == 0 || vertices.Length > 200000) return 0;

                var roads = new List<roadGenScript.roadClass>();
                try
                {
                    foreach (var rg in map.roadGens)
                    {
                        if (rg == null) continue;
                        // The chunk-limited search, the same one the game's own terrain uses: the
                        // unlimited fallback walks every chunk in the world, which is far too slow
                        // to do for every tile.
                        var near = rg.RoadsNear(searchOrigin, false, poiGenScript.alignToRoadEmu.anyRoad);
                        if (near != null) roads.AddRange(near);
                    }
                }
                catch { return 0; }
                if (roads.Count == 0) return 0;

                // The road collection is the first expensive step, so the budget is checked again
                // now: a mesh that starts after the budget is spent must not even look for roads.
                if (!AlignBudgetLeft(_alignSpentThisFrame, ElapsedMs(started)))
                { AlignBudgetSkipped++; return 0; }

                var fix = FixFor(mesh);
                int changed = 0;
                for (int i = 0; i < vertices.Length; i++)
                {
                    // The per-vertex road search is the second expensive step (one search per
                    // vertex against every road near the mesh). The budget is checked before it,
                    // so a mesh that runs out of time stops instead of running all its vertices.
                    if (!AlignBudgetLeft(_alignSpentThisFrame, ElapsedMs(started)))
                    {
                        // The mesh is left as the game made it: a half-corrected mesh would show
                        // a seam of its own.
                        AlignBudgetSkipped++;
                        AlignBudgetAborted++;
                        return 0;
                    }
                    var g = toWorld(vertices[i]);
                    if (!TerrainHorizonGeometry.Finite(g.x, g.z)) continue;
                    var global = mainscript.GlobalFromUnityPos(g);
                    var road = roadGenScript.NearestPointOnRoadBone(global, roads, out var point);
                    if (road == null) continue;
                    double dx = point.x - global.x, dz = point.z - global.z;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > blendRadius) continue;
                    fix.Remember(i, vertices[i].y);
                    double y = fix.Corrected(i, point.y, d, blendRadius);
                    if (double.IsNaN(y)) continue;
                    if (Math.Abs(y - vertices[i].y) < 0.0005) continue;
                    vertices[i] = worldToLocal(mainscript.UnityPosFromGlobal(new Vector3d(global.x, y, global.z)));
                    changed++;
                }
                if (changed == 0) return 0;
                mesh.vertices = vertices;
                mesh.RecalculateNormals();
                // The corrected heights moved the vertices, so the bounds are recomputed with them.
                mesh.RecalculateBounds();
                FarMeshesCorrected++; FarVerticesCorrected += changed;
                return changed;
            }
            finally
            {
                ChargeAlign(started);
            }
        }

        /// The milliseconds since a start stamp.
        private static double ElapsedMs(long started) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void ChargeAlign(long started)
        {
            double ms = ElapsedMs(started);
            _alignSpentThisFrame += ms;
            if (ms > MaxAlignMs) MaxAlignMs = ms;
        }

        /// The distance between two neighbouring vertices of a tile: tileSize / (resolution - 1).
        internal static double TileSpacing(terrainGenScript gen)
        {
            if (gen == null || gen.resolution < 2 || gen.tileSize <= 0) return BlendM;
            return (double)gen.tileSize / (gen.resolution - 1);
        }

        /// The distance between two neighbouring vertices of the far grid: its grid size / (resolution - 1).
        internal static double FarGridSpacing(newNewRandomMapGen gen)
        {
            if (gen == null) return BlendM;
            return TerrainHorizonGeometry.FarGridSpacing(gen.gridSize, gen.resolution, BlendM);
        }

        /// The blend width for a mesh whose vertices are `spacing` apart. It must be at least one
        /// spacing (otherwise the road is never reached) and at most MaxBlendM (so a very coarse
        /// tile does not flatten a whole region). The game's own terrain flattens the ground under
        /// a road between 10 m and 30 m; a coarse tile cannot do that, so it needs a wider blend.
        internal static double BlendRadius(double spacing) =>
            TerrainHorizonGeometry.BlendRadius(spacing, BlendM, MaxBlendM);

        /// The game keeps the tile it just placed in a private field.
        private static readonly AccessTools.FieldRef<terrainGenScript, newTerrainTileScript> TerrainField =
            AccessTools.FieldRefAccess<terrainGenScript, newTerrainTileScript>("terrain");

        private static newTerrainTileScript TerrainRef(terrainGenScript gen)
        {
            try { return gen == null ? null : TerrainField(gen); }
            catch { return null; }
        }

        /// The original heights of one mesh, so the fix can be undone and repeated safely.
        private static readonly Dictionary<Mesh, TerrainHorizonGeometry.HeightFix> Fixes = new Dictionary<Mesh, TerrainHorizonGeometry.HeightFix>();

        private static TerrainHorizonGeometry.HeightFix FixFor(Mesh mesh)
        {
            if (!Fixes.TryGetValue(mesh, out var fix))
            {
                fix = new TerrainHorizonGeometry.HeightFix(mesh.vertexCount);
                Fixes[mesh] = fix;
            }
            else if (fix.RememberedCount() == 0 && mesh.vertexCount != fix.VertexCapacity)
            {
                // the game gave the mesh a new size (a regenerated tile): start over
                fix = new TerrainHorizonGeometry.HeightFix(mesh.vertexCount);
                Fixes[mesh] = fix;
            }
            return fix;
        }

        /// Puts the terrain back the way it was, for the meshes the fix changed. Called when the
        /// switch is turned off, so the player gets the game's own terrain again.
        internal static void RestoreFarMeshes()
        {
            if (Fixes.Count == 0) return;
            foreach (var kv in Fixes)
            {
                var mesh = kv.Key;
                if (mesh == null) continue;
                var vertices = mesh.vertices;
                bool changed = false;
                for (int i = 0; i < vertices.Length && i < kv.Value.VertexCapacity; i++)
                {
                    double original = kv.Value.Original(i);
                    if (double.IsNaN(original)) continue;
                    if (Math.Abs(vertices[i].y - original) < 0.0005) continue;
                    vertices[i].y = (float)original;
                    changed = true;
                }
                if (changed) { mesh.vertices = vertices; mesh.RecalculateNormals(); mesh.RecalculateBounds(); }
            }
            Fixes.Clear();
        }

        internal static long NonFinitePositions;
        internal static long AlignBudgetSkipped;
        /// How often a mesh was left uncorrected because the frame's budget ran out while its
        /// vertices were being corrected.
        internal static long AlignBudgetAborted;
        internal static double MaxAlignMs;
        /// How often the ribbon was rebuilt because the player's Unity position jumped (an origin
        /// shift or a teleport). The game shifts the world every time the player strays past
        /// 2500 m, so this is the counter that says how often the ribbons had to catch up.
        internal static long OriginShifts;

        /// The distant tiles: the coarse terrain between the detailed 500 m world and the far grid.
        /// These are the meshes that actually cover a road just past the near cut, and they were the
        /// ones left uncorrected.
        [HarmonyPatch(typeof(terrainGenScript), "PlaceMesh")]
        private static class AlignDistantTiles
        {
            private static void Postfix(terrainGenScript __instance, Vector3d _gpos)
            {
                if (__instance == null || !__instance.isDistantTerrain) return;
                var tile = TerrainRef(__instance);
                if (tile == null || tile.mf == null || tile.mf.mesh == null) return;
                var mesh = tile.mf.mesh;
                // The tile's own transform: the mesh's vertices are local to the tile object.
                var tr = tile.t != null ? tile.t : tile.transform;
                // The tile's own vertex spacing: a 690 m tile with a resolution of 7 has a vertex
                // every ~98 m, so a 38 m blend would never reach the road.
                double spacing = TileSpacing(__instance);
                AlignMesh(mesh,
                    local => tr.TransformPoint(local),
                    world => tr.InverseTransformPoint(world),
                    _gpos,
                    BlendRadius(spacing));
            }
        }

        internal static string Stats()
        {
            long vertices = 0, triangles = 0;
            foreach (var b in Batches.Values) { vertices += b.Data.VertexCount; triangles += b.Data.TriangleCount; }
            return "{\"correctFar\":" + (CorrectFar ? "true" : "false") + ",\"distantRoads\":" + (DistantRoads ? "true" : "false") +
            ",\"nearCutM\":" + NearCutRadius() + ",\"blendM\":" + BlendM + ",\"moveRebuildM\":" + MoveRebuildM +
            ",\"batches\":" + Batches.Count + ",\"vertices\":" + vertices +
            ",\"triangles\":" + triangles + ",\"rebuilds\":" + Rebuilds + ",\"farMeshWrites\":" + FarMeshesCorrected +
            ",\"farVertices\":" + FarVerticesCorrected + ",\"failed\":" + Failed +
            ",\"lastRebuildMs\":" + LastRebuildMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"lastScanMs\":" + LastScanMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"lastBuildMs\":" + LastBuildMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"lastUploadMs\":" + LastUploadMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxRebuildMs\":" + MaxRebuildMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"spread\":" + (SpreadRebuild ? "true" : "false") + ",\"lastRebuildFrames\":" + LastRebuildFrames +
            ",\"lastStepMaxMs\":" + LastStepMaxMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxStepMs\":" + MaxStepMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"uploads\":" + Uploads + ",\"uploadsSkipped\":" + UploadsSkipped + ",\"rebuildsDropped\":" + RebuildsDropped + ",\"building\":" + (_job != null ? "true" : "false") +
            ",\"lastSignatureMs\":" + LastSignatureMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"signatures\":" + SignaturesTaken + ",\"signatureElements\":" + SignatureElements +
            ",\"signatureVisits\":" + SignatureVisits +
            ",\"stripsAdded\":" + StripsAdded + ",\"stripsClipped\":" + StripClipped + ",\"stripsRejected\":" + StripsRejected +
            ",\"stripsCapped\":" + StripsCapped +
            ",\"batchesRemoved\":" + BatchesRemoved + ",\"nonFinitePositions\":" + NonFinitePositions +
            ",\"originShifts\":" + OriginShifts +
            ",\"alignBudgetSkipped\":" + AlignBudgetSkipped +
            ",\"alignBudgetAborted\":" + AlignBudgetAborted + ",\"maxAlignMs\":" + MaxAlignMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
    }
}
