using System;
using System.Collections.Generic;

namespace TLDRevamp.Fixes
{
    /// Pure geometry and bookkeeping for issue #37, with no Unity types: the renderer
    /// (TerrainHorizon) and the headless unit test (tools/unit/TerrainHorizonTest.cs) run the
    /// same code, so what the test proves is what the game does.
    ///
    /// Coordinates here are *global* (world) metres, the game's own road/terrain space: x/z are
    /// horizontal, y is height. The renderer converts to Unity space at the very end.
    internal static class TerrainHorizonGeometry
    {
        /// One point of a road: horizontal position plus the height the road sits at.
        internal struct Point
        {
            internal double X, Y, Z;
            internal Point(double x, double y, double z) { X = x; Y = y; Z = z; }
        }

        /// A road segment, already clipped to the far ring.
        internal struct Segment
        {
            internal Point A, B;
        }

        // ------------------------------------------------------------------ finiteness

        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        internal static bool Finite(Point p) => Finite(p.X) && Finite(p.Y) && Finite(p.Z);

        /// True when a horizontal position can be used at all. A NaN player position (a teleport
        /// that landed nowhere) would make every distance comparison false and pull the whole
        /// world's roads into one mesh, so the caller must refuse to work with it.
        internal static bool Finite(double x, double z) => Finite(x) && Finite(z);

        // ------------------------------------------------------------------ the near cut

        /// Where a segment crosses the circle of radius `radius` around (cx, cz), or null when it
        /// does not cross. The near cut is a disc, so the crossing is where the segment enters or
        /// leaves it; the returned t is in [0,1] along A->B.
        internal static bool CircleCross(double ax, double az, double bx, double bz, double cx, double cz,
                                         double radius, out double t)
        {
            t = 0;
            double dx = bx - ax, dz = bz - az;
            double fx = ax - cx, fz = az - cz;
            double a = dx * dx + dz * dz;
            if (a <= 1e-12) return false;                       // degenerate segment: no crossing
            double b = 2 * (fx * dx + fz * dz);
            double c = fx * fx + fz * fz - radius * radius;
            double disc = b * b - 4 * a * c;
            if (disc < 0) return false;                         // no real crossing
            double sq = Math.Sqrt(disc);
            double t1 = (-b - sq) / (2 * a);
            double t2 = (-b + sq) / (2 * a);
            double lo = Math.Max(0.0, Math.Min(t1, t2));
            double hi = Math.Min(1.0, Math.Max(t1, t2));
            if (lo > hi) return false;                          // both crossings outside the segment
            t = lo;
            return true;
        }

        /// The parameter interval of the segment A->B that lies inside the disc of radius
        /// `radius` around (cx, cz). Returns false when the segment misses the disc.
        private static bool DiscInterval(double ax, double az, double bx, double bz,
                                         double cx, double cz, double radius,
                                         out double t0, out double t1)
        {
            t0 = 0; t1 = 1;
            double dx = bx - ax, dz = bz - az;
            double fx = ax - cx, fz = az - cz;
            double a = dx * dx + dz * dz;
            if (a <= 1e-12)
            {
                // A point: inside the disc or not.
                return (fx * fx + fz * fz) <= radius * radius;
            }
            double b = 2 * (fx * dx + fz * dz);
            double c = fx * fx + fz * fz - radius * radius;
            double disc = b * b - 4 * a * c;
            if (disc < 0) return false;                       // misses the disc
            double sq = Math.Sqrt(disc);
            double ta = (-b - sq) / (2 * a);
            double tb = (-b + sq) / (2 * a);
            if (ta > tb) { double tmp = ta; ta = tb; tb = tmp; }
            t0 = Math.Max(0.0, ta);
            t1 = Math.Min(1.0, tb);
            return t0 <= t1;
        }

        /// Clips one road segment to the ring between `nearRadius` and `farRadius` around
        /// (cx, cz) and writes the surviving pieces into `outSegs` (at most two: the part before
        /// the segment enters the near disc and the part after it leaves).
        ///
        /// Returns the number of pieces. Zero means the segment contributes nothing:
        ///  - it lies wholly inside the near cut (the detailed world already draws it),
        ///  - it lies wholly beyond the far limit,
        ///  - any coordinate is not finite,
        ///  - the radii are not usable.
        ///
        /// Trimming at the near cut is what stops a ribbon from reaching back into the detailed
        /// 500 m world and z-fighting with the real road there.
        internal static int ClipToRing(Point a, Point b, double cx, double cz,
                                       double nearRadius, double farRadius, Segment[] outSegs)
        {
            if (outSegs == null || outSegs.Length < 2) return 0;
            if (!Finite(a) || !Finite(b)) return 0;
            if (!Finite(cx) || !Finite(cz) || nearRadius < 0 || farRadius <= nearRadius) return 0;

            // The part of the segment inside the far disc.
            double f0, f1;
            if (!DiscInterval(a.X, a.Z, b.X, b.Z, cx, cz, farRadius, out f0, out f1)) return 0;

            // The part inside the near disc: the ribbon must not be drawn there.
            double n0, n1;
            bool hitsNear = DiscInterval(a.X, a.Z, b.X, b.Z, cx, cz, nearRadius, out n0, out n1);

            if (!hitsNear)
            {
                outSegs[0] = new Segment { A = Lerp(a, b, f0), B = Lerp(a, b, f1) };
                return 1;
            }

            int count = 0;
            // The piece before the segment enters the near disc.
            if (f0 < n0)
            {
                outSegs[count] = new Segment { A = Lerp(a, b, f0), B = Lerp(a, b, n0) };
                count++;
            }
            // The piece after it leaves the near disc.
            if (n1 < f1)
            {
                if (count > 0) outSegs[count] = new Segment { A = Lerp(a, b, n1), B = Lerp(a, b, f1) };
                else outSegs[count] = new Segment { A = Lerp(a, b, n1), B = Lerp(a, b, f1) };
                count++;
            }
            return count;
        }

        /// The same clipping, for a caller that wants at most one piece (the longest one).
        internal static bool ClipToRing(Point a, Point b, double cx, double cz,
                                        double nearRadius, double farRadius, out Segment outSeg)
        {
            outSeg = default;
            var pieces = new Segment[2];
            int n = ClipToRing(a, b, cx, cz, nearRadius, farRadius, pieces);
            if (n == 0) return false;
            outSeg = pieces[0];
            if (n == 2)
            {
                double len0 = Dist2(pieces[0].A, pieces[0].B);
                double len1 = Dist2(pieces[1].A, pieces[1].B);
                if (len1 > len0) outSeg = pieces[1];
            }
            return true;
        }

        private static Point Lerp(Point a, Point b, double t) =>
            new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        private static double Dist2(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// Whether a road chunk can hold anything inside the draw distance. The chunk's own centre is
        /// used, with the chunk's size as the margin, so a road that crosses the border of a chunk
        /// that is just outside the range is still drawn.
        ///
        /// The game keeps every road chunk it ever made, so a rebuild that walked them all grew with
        /// the size of the visited world. This is the test that keeps it bounded.
        internal static bool ChunkInRange(double chunkX, double chunkZ, double atX, double atZ,
                                          double limit, double chunkSize)
        {
            if (!Finite(chunkX) || !Finite(chunkZ) || !Finite(atX) || !Finite(atZ) || !Finite(limit)) return false;
            double dx = chunkX - atX, dz = chunkZ - atZ;
            double margin = Math.Max(1.0, Math.Abs(chunkSize));
            double reach = limit + margin;
            return dx * dx + dz * dz <= reach * reach;
        }

        /// Whether a road segment [ax,az]-[bx,bz] can reach the disc of radius `limit` around
        /// (atX, atZ). The closest point of the segment to the player decides, so a road that
        /// runs past the player is kept and a road that runs far away is skipped without walking
        /// its bones.
        internal static bool SegmentInRange(double ax, double az, double bx, double bz, double atX, double atZ, double limit)
        {
            if (!Finite(ax) || !Finite(az) || !Finite(bx) || !Finite(bz) || !Finite(atX) || !Finite(atZ) || !Finite(limit)) return true;
            double dx = bx - ax, dz = bz - az;
            double fx = ax - atX, fz = az - atZ;
            double len2 = dx * dx + dz * dz;
            double t = len2 > 1e-9 ? -(fx * dx + fz * dz) / len2 : 0.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double px = fx + dx * t, pz = fz + dz * t;
            return px * px + pz * pz <= limit * limit;
        }

        // ------------------------------------------------------------------ the terrain blend

        /// How strongly a terrain vertex at horizontal distance `d` from the road is pulled onto
        /// the road: 1 at the road, 0 at `blendRadius` and beyond, smooth in between. The same
        /// smoothstep the renderer used before, so turning the fix off and on does not change the
        /// shape of the seam.
        internal static float BlendWeight(double distance, double blendRadius)
        {
            if (!Finite(distance) || !Finite(blendRadius) || blendRadius <= 0) return 0f;
            if (distance >= blendRadius) return 0f;
            float t = (float)(distance / blendRadius);
            // SmoothStep(0,1,t) = t*t*(3-2t)
            float s = t * t * (3f - 2f * t);
            return 1f - s;
        }

        /// The corrected height of a terrain vertex: the road's height where the vertex is close
        /// to the road, the vertex's own height further away, mixed by the blend weight.
        ///
        /// The blend works in both directions. The issue reports two symptoms: the coarse far mesh
        /// bulging over the road ("the road goes into the ground") and the road floating above it
        /// at crossroads ("floating roads"). The far mesh is generated from a *different* noise
        /// field than the near terrain the road was laid on, so the far terrain can be above or
        /// below the road at the same place. Pulling the terrain only down fixed the first
        /// symptom and left the second one: where the far mesh dipped below the road, the road
        /// stayed in the air.
        ///
        /// The shape of the blend is the same one the game itself uses to flatten the ground under
        /// a road (terrainGenSettings.Height lerps the terrain height towards the road height), so
        /// the corrected far terrain follows the road the way the near terrain does.
        internal static double BlendedHeight(double vertexY, double roadY, double distance, double blendRadius)
        {
            if (!Finite(vertexY) || !Finite(roadY)) return vertexY;
            float w = BlendWeight(distance, blendRadius);
            if (w <= 0f) return vertexY;
            return vertexY + (roadY - vertexY) * w;
        }

        /// The height a terrain vertex would get with no correction at all. The far mesh's own
        /// height, which the correction moves towards the road.
        internal static double UncorrectedHeight(double vertexY) => vertexY;

        /// The per-mesh record of the terrain heights the fix changed.
        ///
        /// The correction must be computed from the vertex's *original* height every time. If it
        /// were computed from the already-corrected height, each pass would pull the vertex down
        /// a little further and the terrain would slowly sink onto the roads; and turning the fix
        /// off would leave the sunk heights behind. Keeping the originals makes the correction
        /// idempotent and reversible.
        internal sealed class HeightFix
        {
            private readonly double[] _original;
            private readonly bool[] _known;

            internal HeightFix(int vertexCount)
            {
                int n = Math.Max(0, vertexCount);
                _original = new double[n];
                _known = new bool[n];
            }

            /// Records the vertex's unmodified height. Only the first call for an index counts:
            /// later calls carry the corrected value, which must not overwrite the original.
            internal void Remember(int index, double y)
            {
                if (index < 0 || index >= _original.Length) return;
                if (!Finite(y)) return;
                if (_known[index]) return;
                _original[index] = y;
                _known[index] = true;
            }

            internal bool Remembered(int index) => index >= 0 && index < _original.Length && _known[index];

            /// How many vertices this record covers.
            internal int VertexCapacity => _original.Length;

            /// How many vertices have a remembered original height.
            internal int RememberedCount() { int n = 0; for (int i = 0; i < _known.Length; i++) if (_known[i]) n++; return n; }

            internal double Original(int index) => Remembered(index) ? _original[index] : double.NaN;

            /// The height the vertex should have: the original, pulled onto the road by the
            /// blend weight. Never computed from a corrected value, so repeating it changes
            /// nothing.
            internal double Corrected(int index, double roadY, double distance, double blendRadius)
            {
                if (!Remembered(index)) return double.NaN;
                return BlendedHeight(_original[index], roadY, distance, blendRadius);
            }

            /// The vertex's original height again (used when the fix is turned off).
            internal double Restored(int index) => Original(index);
        }

        /// The blend width for a mesh whose vertices are `spacing` apart.
        ///
        /// The blend must be at least one vertex spacing wide, or the road is never reached at all:
        /// a coarse tile of 690 m with a resolution of 7 has a vertex every ~98 m, and a 38 m
        /// blend would touch nothing. It is also capped, so a very coarse tile does not flatten a
        /// whole region. The game's own terrain flattens the ground under a road between 10 m and
        /// 30 m; a coarse tile cannot do that, so it needs a wider blend.
        internal static double BlendRadius(double spacing, double minRadius, double maxRadius)
        {
            if (!Finite(spacing) || spacing <= 0) return minRadius;
            double radius = spacing * 1.5;                  // the vertex next to the road, and the one after
            if (radius < minRadius) radius = minRadius;
            if (radius > maxRadius) radius = maxRadius;
            return radius;
        }

        // ------------------------------------------------------------------ the near cut

        /// The radius at which the ribbon starts: the distance at which the game's own detailed
        /// terrain ends.
        ///
        /// The ribbon must not be drawn where the near terrain already draws the road, or the two
        /// fight for the same pixels. Hard-coding the radius means the ribbon and the terrain
        /// silently disagree as soon as the game's draw distance changes; taking it from the
        /// generator that draws the near terrain keeps the two in step by construction.
        internal static double NearCutFromDrawDistance(double nearDrawDistance, double fallback)
        {
            if (!Finite(nearDrawDistance) || nearDrawDistance <= 0) return fallback;
            return nearDrawDistance;
        }

        // ------------------------------------------------------------------ the far grid

        /// The distance between two neighbouring vertices of the far grid: its grid size divided
        /// by (resolution - 1). The far grid is coarse (a 2000 m grid at a resolution of 33 has
        /// a vertex every ~62 m), so this is the width the blend has to reach.
        internal static double FarGridSpacing(double gridSize, int resolution, double fallback)
        {
            if (!Finite(gridSize) || gridSize <= 0 || resolution < 2) return fallback;
            return gridSize / (resolution - 1);
        }

        // ------------------------------------------------------------------ the origin shift

        /// Whether a rebuild was caused by the game shifting the world, rather than by the player
        /// walking or the roads changing.
        ///
        /// The game moves the whole world in Unity space when the player strays past 2500 m
        /// (mainscript.VisszaRakas). The player's *global* position stays the same across that
        /// shift, while their Unity position jumps by kilometres. So an origin shift is: the
        /// global position barely moved compared with the previous observation, while the Unity position
        /// moved a long way. An ordinary walk moves both by the same amount, and a teleport moves
        /// both by a lot, so neither of those is an origin shift.
        internal static bool IsOriginShift(double globalDx, double globalDz, double unityMoveSq,
                                           double globalToleranceM, double unityJumpM)
        {
            if (!Finite(globalDx) || !Finite(globalDz) || !Finite(unityMoveSq)) return false;
            if (!Finite(globalToleranceM) || !Finite(unityJumpM)) return false;
            if (globalToleranceM < 0 || unityJumpM <= 0) return false;
            double globalLimit = globalToleranceM * globalToleranceM;
            double unityLimit = unityJumpM * unityJumpM;
            double globalMoveSq = globalDx * globalDx + globalDz * globalDz;
            // The global position is (nearly) unchanged ...
            if (globalMoveSq > globalLimit) return false;
            // ... and the Unity position jumped.
            return unityMoveSq >= unityLimit;
        }

        // ------------------------------------------------------------------ origin observation

        /// Observes the player's positions independently of rebuild cadence. An origin shift is
        /// identified against the previous finite observation, so walking for longer than the
        /// rebuild threshold cannot hide a subsequent world-origin shift.
        internal sealed class OriginShiftObserver
        {
            private readonly double _globalToleranceM;
            private readonly double _unityJumpM;
            private double _previousGlobalX;
            private double _previousGlobalZ;
            private double _previousUnityX;
            private double _previousUnityY;
            private double _previousUnityZ;
            private bool _hasObservation;

            internal OriginShiftObserver(double globalToleranceM, double unityJumpM)
            {
                _globalToleranceM = globalToleranceM;
                _unityJumpM = unityJumpM;
            }

            internal bool Observe(double globalX, double globalZ, double unityX, double unityY, double unityZ)
            {
                if (!Finite(globalX) || !Finite(globalZ) || !Finite(unityX) || !Finite(unityY) || !Finite(unityZ))
                {
                    Reset();
                    return false;
                }

                bool shifted = false;
                if (_hasObservation)
                {
                    double globalDx = globalX - _previousGlobalX;
                    double globalDz = globalZ - _previousGlobalZ;
                    double unityDx = unityX - _previousUnityX;
                    double unityDy = unityY - _previousUnityY;
                    double unityDz = unityZ - _previousUnityZ;
                    double unityMoveSq = unityDx * unityDx + unityDy * unityDy + unityDz * unityDz;
                    shifted = IsOriginShift(globalDx, globalDz, unityMoveSq,
                        _globalToleranceM, _unityJumpM);
                }

                _previousGlobalX = globalX;
                _previousGlobalZ = globalZ;
                _previousUnityX = unityX;
                _previousUnityY = unityY;
                _previousUnityZ = unityZ;
                _hasObservation = true;
                return shifted;
            }

            internal void Reset()
            {
                _previousGlobalX = 0;
                _previousGlobalZ = 0;
                _previousUnityX = 0;
                _previousUnityY = 0;
                _previousUnityZ = 0;
                _hasObservation = false;
            }
        }

        // ------------------------------------------------------------------ the align budget

        /// Whether the per-frame correction budget is used up.
        ///
        /// The budget is a *soft* one: a single call into the game's own road search cannot be
        /// interrupted, so the cap can be overshot by the cost of that one call. What the rule
        /// does guarantee is that no further work is started once the budget is spent, and that
        /// the work already done is charged.
        internal static bool AlignBudgetExhausted(double spentMs, double elapsedMs, double budgetMs)
        {
            if (!Finite(spentMs) || !Finite(elapsedMs) || !Finite(budgetMs)) return true;
            if (budgetMs <= 0) return true;
            return spentMs + elapsedMs >= budgetMs;
        }

        // ------------------------------------------------------------------ the road signature

        /// A cheap, bounded fingerprint of the road set. It must answer "did the roads change?"
        /// without walking the world's road hierarchy every frame. The traversal budget counts every
        /// visited generator, chunk, road, and road element, including the separate count and sample
        /// passes over road elements. Road count and element metadata remain available independently.
        internal sealed class SignatureBuilder
        {
            private readonly int _maxElements;
            private readonly int _maxRoads;
            private int _hash = 17;
            private int _elements;
            private int _roads;
            private int _visits;
            private bool _truncated;

            internal SignatureBuilder(int maxElements) : this(maxElements, 0) { }

            internal SignatureBuilder(int maxElements, int maxRoads)
            {
                _maxElements = maxElements > 0 ? maxElements : 4096;
                _maxRoads = maxRoads > 0 ? maxRoads : int.MaxValue;
            }

            internal int Elements => _elements;
            internal int Roads => _roads;
            internal int Visits => _visits;
            internal bool Truncated => _truncated;

            /// Whether another road may still be folded in. The road cap is separate from the
            /// traversal budget, but both must still permit work before the walk continues.
            internal bool AcceptsMoreRoads => _roads < _maxRoads && _visits < _maxElements;

            /// Accounts for one visited traversal entry. Every hierarchy and road-element loop must
            /// call this before inspecting the entry, so empty or null branches cannot bypass the
            /// work bound.
            internal bool TryVisit()
            {
                if (_visits >= _maxElements)
                {
                    _truncated = true;
                    return false;
                }
                _visits++;
                return true;
            }

            internal void Add(int value) { unchecked { _hash = _hash * 31 + value; } }

            /// Folds one road's shape into the signature: how many bones it has and how many
            /// elements. The counts change whenever the road is regenerated.
            internal void AddRoad(bool generated, int elementCount, int boneCount)
            {
                Add(generated ? 1 : 0);
                Add(elementCount);
                Add(boneCount);
                long elements = (long)_elements + Math.Max(0, elementCount);
                _elements = (int)Math.Min(int.MaxValue, elements);
                _roads++;
                if (_elements > _maxElements) { _truncated = true; }
            }

            /// Folds one *point* of a road into the signature.
            ///
            /// The counts alone cannot tell a road that moved from a road that did not: the same
            /// road, regenerated a kilometre further on or five metres higher, has the same
            /// element and bone counts, so the ribbon would keep the old road's shape and the old
            /// height. A bounded sample of the road's own points (its ends and a few in between)
            /// catches that without walking every bone of every road in the world.
            internal void AddPoint(double x, double y, double z)
            {
                Add(Quantize(x));
                Add(Quantize(y));
                Add(Quantize(z));
            }

            /// Folds the number of roads seen into the hash, so a world with more roads still gives
            /// a different signature even when the walk stopped early.
            internal void AddRoadCount(int count) { Add(count); }

            /// A position folded into an int, at a resolution the roads actually need: a
            /// centimetre of height and a decimetre of ground position. Rounding keeps the
            /// signature stable against the float noise the game introduces, and still catches a
            /// road that moved or changed height by more than that.
            internal static int Quantize(double v)
            {
                if (!Finite(v)) return 0;
                double scaled = Math.Round(v * 100.0);
                if (scaled > int.MaxValue / 2) scaled = int.MaxValue / 2;
                if (scaled < int.MinValue / 2) scaled = int.MinValue / 2;
                return (int)scaled;
            }

            internal int Hash => _hash;
        }

        /// The state the renderer keeps between rebuilds: where it was built, for which road
        /// signature, and when it may next rebuild.
        internal sealed class RebuildGate
        {
            private double _builtX, _builtZ;
            private int _signature;
            private bool _hasBuilt;

            internal bool HasBuilt => _hasBuilt;
            internal int Signature => _signature;
            internal double BuiltX => _builtX;
            internal double BuiltZ => _builtZ;

            /// True when a rebuild is due: never built yet, the roads changed, or the player moved
            /// further than `moveRadius` from the last build. A teleport (a jump of kilometres) and
            /// an origin shift (the player's Unity position jumps while the global one does not)
            /// both show up as a large move, so both force a rebuild.
            internal bool NeedsRebuild(double x, double z, int signature, double moveRadius)
            {
                if (!_hasBuilt) return true;
                if (signature != _signature) return true;
                if (!Finite(x) || !Finite(z)) return false;   // no usable position: keep what we have
                double dx = x - _builtX, dz = z - _builtZ;
                return dx * dx + dz * dz >= moveRadius * moveRadius;
            }

            internal void MarkBuilt(double x, double z, int signature)
            {
                _builtX = x; _builtZ = z; _signature = signature; _hasBuilt = true;
            }

            internal void Clear() { _hasBuilt = false; _signature = 0; }
        }

        // ------------------------------------------------------------------ the ribbon batch

        /// Whether a strip with these inputs would be accepted. The caller uses it to avoid
        /// creating a batch for a strip that would be thrown away.
        internal static bool StripAcceptable(Point a, Point c, double halfWidth, double lift)
        {
            if (!Finite(a) || !Finite(c)) return false;
            if (!Finite(halfWidth) || halfWidth <= 0 || halfWidth > 100) return false;
            if (!Finite(lift)) return false;
            double dx = c.X - a.X, dz = c.Z - a.Z;
            // The difference of two finite numbers can still overflow (1e308 - -1e308 is an
            // infinity), and the squared length of a huge segment overflows to infinity as well.
            // A range test cannot see that: `inf >= 1e-4` is TRUE, so the segment would be
            // accepted and the strip would then be built with a zero width side vector.
            if (!Finite(dx) || !Finite(dz)) return false;
            double len2 = dx * dx + dz * dz;
            if (!Finite(len2)) return false;
            return len2 >= 1e-4;                               // not zero length
        }

        /// The geometric normal of a triangle, in the ribbon's own (global) space.
        ///
        /// The ribbon's triangles must face up: the game's own terrain and far meshes are wound so
        /// that their normals point up, and Unity's road materials are single-sided (Cull Back). A
        /// ribbon wound the other way is culled by every camera above it, so the road would simply
        /// not be drawn. This is the check that keeps the winding honest.
        internal static void TriangleNormal(Point a, Point b, Point c, out double nx, out double ny, out double nz)
        {
            double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
            double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
            nx = e1y * e2z - e1z * e2y;
            ny = e1z * e2x - e1x * e2z;
            nz = e1x * e2y - e1y * e2x;
        }

        /// Whether a ribbon triangle of the given winding faces up. A road that is drawn with
        /// back-facing triangles is invisible from every camera above it, so this is a hard rule,
        /// not a preference.
        internal static bool FacesUp(Point a, Point b, Point c)
        {
            if (!Finite(a) || !Finite(b) || !Finite(c)) return false;
            TriangleNormal(a, b, c, out _, out double ny, out _);
            return ny > 0;                                    // a flat strip has no normal
        }

        /// The winding a ribbon's triangles must use, given the four corner points of a strip.
        ///
        /// The corners are laid out as a-left, a-right, c-left, c-right with `side` pointing to the
        /// right of the direction of travel. The triangles that face up are (a-left, c-left,
        /// a-right) and (c-left, c-right, a-right): the same handedness the game's own terrain
        /// meshes use.
        internal static void UpwardWinding(Point aLeft, Point aRight, Point cLeft, Point cRight,
                                            out int i0, out int i1, out int i2, out int j0, out int j1, out int j2)
        {
            // Try the first candidate; if it does not face up, use the other one. A flat strip has
            // exactly one upward winding, so exactly one of the two candidates passes.
            if (FacesUp(aLeft, cLeft, aRight)) { i0 = 0; i1 = 2; i2 = 1; j0 = 2; j1 = 3; j2 = 1; }
            else { i0 = 0; i1 = 1; i2 = 2; j0 = 2; j1 = 1; j2 = 3; }
        }

        /// A per-road-id batch of ribbon geometry. It refuses to grow past `maxVertices` and
        /// refuses non-finite input, so one bad road cannot blow up the mesh.
        internal sealed class Batch
        {
            internal readonly int Id;
            internal readonly List<double> Vx = new List<double>();
            internal readonly List<double> Vy = new List<double>();
            internal readonly List<double> Vz = new List<double>();
            internal readonly List<double> Uu = new List<double>();
            internal readonly List<double> Uv = new List<double>();
            internal readonly List<int> T = new List<int>();
            private readonly int _maxVertices;

            internal Batch(int id, int maxVertices)
            {
                Id = id;
                _maxVertices = maxVertices > 0 ? maxVertices : 120000;
            }

            internal int VertexCount => Vx.Count;
            internal int TriangleCount => T.Count / 3;
            internal bool AtCapacity => VertexCount >= _maxVertices;
            internal bool Full => AtCapacity;

            internal void Reset()
            {
                Vx.Clear(); Vy.Clear(); Vz.Clear(); Uu.Clear(); Uv.Clear(); T.Clear();
            }

            /// Appends one strip. Returns false without touching the batch when the input is
            /// degenerate, not finite, out of the width range, or the batch is full.
            internal bool AddStrip(Point a, Point c, double halfWidth, double lift)
            {
                if (AtCapacity) return false;
                if (!StripAcceptable(a, c, halfWidth, lift)) return false;
                double dx = c.X - a.X, dz = c.Z - a.Z;
                double len2 = dx * dx + dz * dz;
                double inv = 1.0 / Math.Sqrt(len2);
                // side = cross(up, forward) * halfWidth  ->  (dz, 0, -dx) * halfWidth * inv
                double sx = dz * inv * halfWidth;
                double sz = -dx * inv * halfWidth;
                int n = VertexCount;
                // The four corners, in a fixed order: a-left, a-right, c-left, c-right.
                Vx.Add(a.X - sx); Vy.Add(a.Y + lift); Vz.Add(a.Z - sz);
                Vx.Add(a.X + sx); Vy.Add(a.Y + lift); Vz.Add(a.Z + sz);
                Vx.Add(c.X - sx); Vy.Add(c.Y + lift); Vz.Add(c.Z - sz);
                Vx.Add(c.X + sx); Vy.Add(c.Y + lift); Vz.Add(c.Z + sz);
                Uu.Add(0); Uv.Add(0);
                Uu.Add(1); Uv.Add(0);
                Uu.Add(0); Uv.Add(1);
                Uu.Add(1); Uv.Add(1);
                // The winding that faces up. The road materials are single-sided, so a ribbon wound
                // the other way is culled by every camera above it and the road is not drawn at all.
                UpwardWinding(
                    new Point(a.X - sx, a.Y + lift, a.Z - sz),
                    new Point(a.X + sx, a.Y + lift, a.Z + sz),
                    new Point(c.X - sx, c.Y + lift, c.Z - sz),
                    new Point(c.X + sx, c.Y + lift, c.Z + sz),
                    out int i0, out int i1, out int i2, out int j0, out int j1, out int j2);
                T.Add(n + i0); T.Add(n + i1); T.Add(n + i2);
                T.Add(n + j0); T.Add(n + j1); T.Add(n + j2);
                return true;
            }
        }
    }
}
