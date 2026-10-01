using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// roadGenScript.UpdPlaceRemove scans every road piece of every loaded chunk each frame to decide which
    /// pieces to spawn (within roadPlaceDist of a genAround, usually the player) or destroy. In steady state
    /// that full scan changes nothing, yet it costs ~2 ms/frame (profiled: 226 ms/s, 71% of road gen).
    ///
    /// This replaces it with an identical pass that also computes a "margin": how far any genAround can move
    /// before some inner chunk crosses the roadPlaceDist threshold. After a pass that did no work, later calls
    /// are skipped until a genAround moves more than the margin, the world changes (chunks, inner chunks, road
    /// elements, needed-chunk set, roadPlaceDist, genAround count), or a safety interval passes.
    ///
    /// Runtime switches (bridge `set`): TLDRevamp.Fixes.RoadPlaceCache.Enabled / .Verify
    [HarmonyPatch]
    public static class RoadPlaceCache
    {
        public static bool Enabled = true;
        /// On skipped frames, run a dry pass and count any action the original would have taken.
        public static bool Verify = false;
        public static int SafetyIntervalFrames = 120;

        public static long Passes, Skipped, VerifyChecks, VerifyMismatches;
        public static long PassesWork, PassesAfterWork, PassesSafety, PassesMoved, PassesSig;

        /// Incremental passes (v0.21.0). Driving bench: 509 of 657 full passes were caused by movement (the margin is
        /// the distance to the nearest inner chunk's roadPlaceDist threshold, which is always small while driving),
        /// each walking every road element within 3.5 km with a Unity null check (~2 ms). A full scan records each inner
        /// chunk's in-range state; later passes recompute only that state (one distance per inner chunk) and walk the
        /// elements of inner chunks whose state flipped or that are unfinished (an action stopped the pass there), in the
        /// game's order. Others were fully satisfied at their last walk and nothing in the game changes their elements
        /// except this pass (same assumption as the skip logic above). Full scan at least every SafetyIntervalFrames.
        /// Verify: before each incremental pass, a dry game-equivalent pass finds the first action; it must be the same
        /// road element ours acts on (or none for both).
        /// On by default since v0.21.1: verified (569 incremental passes, 0 mismatches), driving A/B p99 −9% (16.75 → 15.25).
        public static bool Incremental = true;
        public static long IncrPasses, FullScans, IncrWalked, IncrMismatches;
        public static string LastIncrMismatch = "";

        private class State
        {
            public bool Stable;
            public double Margin;
            public float Dist;
            public long Signature;
            public int Frames;
            public readonly List<Vector3d> GenPos = new List<Vector3d>();
            public bool Tracked;
            public int SinceFull;
            public readonly Dictionary<roadGenScript.innerChunkClass, bool> InRange = new Dictionary<roadGenScript.innerChunkClass, bool>();
            public readonly HashSet<roadGenScript.innerChunkClass> Dirty = new HashSet<roadGenScript.innerChunkClass>();
            /// Travel bound (v0.32.0): running sum over passes of the largest displacement of any generation centre.
            public double Mx;
            /// Weak: a record lives exactly as long as its inner chunk; reused across full scans (no garbage churn).
            public readonly ConditionalWeakTable<roadGenScript.innerChunkClass, Rec> Recs = new ConditionalWeakTable<roadGenScript.innerChunkClass, Rec>();
            public readonly List<Rec> Heap = new List<Rec>();
            public bool HeapReady;
            /// Heap mode: scope of the last full scan (needed chunks in order) and inner-chunk changes since then.
            public long ShapeSig;
            public readonly Dictionary<roadGenScript.dotChunkClass, int> ChunkIdxOf = new Dictionary<roadGenScript.dotChunkClass, int>();
            public readonly Dictionary<roadGenScript.dotChunkClass, Vector3d> ChunkKeyOf = new Dictionary<roadGenScript.dotChunkClass, Vector3d>();
            public readonly List<Change> Log = new List<Change>();
            public readonly HashSet<roadGenScript.innerChunkClass> LoggedNew = new HashSet<roadGenScript.innerChunkClass>();
        }

        private struct Change { public roadGenScript.dotChunkClass Dcc; public Vector3d Key; public int NewOrd; }
        public static long HeapInserts, HeapDirtied, LogFallbacks;

        /// Per inner chunk: in-range state, its distance to the placement boundary (min over centres of |d − R|), and the
        /// travel total when that was measured. No centre can have crossed the boundary while Mx − MxAtEval < Gap
        /// (a centre's distance changes by at most its displacement; displacements add up), so the state is unchanged.
        private sealed class Rec
        {
            public bool InRange; public double Gap, MxAtEval;
            // Heap mode: re-evaluate once Mx reaches Expiry (= MxAtEval + Gap); game order recorded at the last full scan
            public double Expiry;
            public int ChunkIdx, Ord;
            public roadGenScript.innerChunkClass Ic;
            public roadGenScript.dotChunkClass Dcc;
            public Vector3d ChunkKey, Key;
        }
        public static bool TravelBound = true;
        public static long EvalsSkipped, Evals;

        /// Heap mode (v0.34.0): with TravelBound, an incremental pass still visited every inner chunk of every needed
        /// chunk (three lookups each) just to find the few whose bound expired — tens of thousands per frame with 16
        /// players. Inner chunks now sit in a min-heap by Expiry; a pass pops only the expired ones (same evaluation and
        /// same condition as TravelBound, a hair earlier on floating-point ties), then walks the dirty inner chunks in the
        /// game's order (chunk position in chunksNeeded, then the inner chunk's dictionary position — both recorded at the
        /// last full scan; a changed chunk order changes the signature and forces a full scan). Verify covers it as before.
        public static bool HeapMode = true;
        public static long HeapPasses, HeapPops;

        private static readonly ConditionalWeakTable<roadGenScript, State> States = new ConditionalWeakTable<roadGenScript, State>();

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdPlaceRemove))]
        [HarmonyPrefix]
        private static bool Prefix(roadGenScript __instance, bool _moreFrame)
        {
            if (!Enabled) return true;
            var gen = __instance;
            if (!gen.isActiveAndEnabled)
            {
                // Original's "destroy everything" branch: leave it to the game.
                if (States.TryGetValue(gen, out var inactive)) inactive.Stable = false;
                return true;
            }

            var st = States.GetValue(gen, _ => new State());
            var arounds = gen.mapSetting.genArounds;
            long sig = Signature(gen);

            if (st.Stable && st.Frames < SafetyIntervalFrames && sig == st.Signature && gen.roadPlaceDist == st.Dist
                && arounds.Count == st.GenPos.Count && MaxMoved(arounds, st.GenPos) < st.Margin)
            {
                st.Frames++;
                Skipped++;
                if (Verify)
                {
                    VerifyChecks++;
                    if (Pass(gen, _moreFrame, dryRun: true, out _, out _)) VerifyMismatches++;
                }
                return false;
            }

            Passes++;
            if (!st.Stable) PassesAfterWork++;
            else if (st.Frames >= SafetyIntervalFrames) PassesSafety++;
            else if (sig != st.Signature || gen.roadPlaceDist != st.Dist || arounds.Count != st.GenPos.Count) PassesSig++;
            else PassesMoved++;
            bool didWork;
            double margin;
            if (Incremental)
            {
                bool full = !(st.Tracked && sig == st.Signature && gen.roadPlaceDist == st.Dist && arounds.Count == st.GenPos.Count
                              && st.SinceFull < SafetyIntervalFrames);
                // Heap mode also survives new inner chunks / road pieces (logged by UpdInnerChunk): only a changed scope
                // (needed chunks, their order or identity, a reset chunk) needs a full scan
                bool heapOk = HeapMode && TravelBound && st.HeapReady && st.Tracked && arounds.Count > 0 && st.GenPos.Count == arounds.Count
                              && gen.roadPlaceDist == st.Dist && st.SinceFull < SafetyIntervalFrames && ShapeSignature(gen) == st.ShapeSig;
                if (heapOk) full = false;
                roadGenScript.roadElementClass expected = null;
                bool expectAction = false;
                if (Verify && !full) expectAction = Pass(gen, _moreFrame, dryRun: true, out _, out expected);
                roadGenScript.roadElementClass acted;
                if (heapOk)
                    didWork = HeapScan(gen, st, _moreFrame, out margin, out acted);
                else
                    didWork = Scan(gen, st, _moreFrame, full, out margin, out acted);
                if (Verify && !full && (expectAction != (acted != null) || (expected != null && acted != null && expected != acted)))
                {
                    IncrMismatches++;
                    LastIncrMismatch = "expected " + (expected != null ? "action" : "none") + ", ours " + (acted != null ? "action" : "none") +
                                       (expected != null && acted != null ? " (different element)" : "");
                }
                if (full) { FullScans++; st.SinceFull = 0; } else { IncrPasses++; st.SinceFull++; }
                st.Tracked = true;
            }
            else
            {
                st.Tracked = false;
                didWork = Pass(gen, _moreFrame, dryRun: false, out margin, out _);
            }
            if (didWork) PassesWork++;
            st.Stable = !didWork;
            st.Margin = margin;
            st.Dist = gen.roadPlaceDist;
            st.Signature = Signature(gen); // after the pass, in case it changed anything we key on
            st.ShapeSig = ShapeSignature(gen);
            st.Frames = 0;
            st.GenPos.Clear();
            for (int i = 0; i < arounds.Count; i++) st.GenPos.Add(Flat(mainscript.GlobalFromUnityPos(arounds[i].upos)));
            return false;
        }

        /// Same decisions and order as the original active branch. Returns true if it placed/destroyed anything
        /// (dry run: would have). margin = min over inner chunks of |nearest genAround distance - roadPlaceDist|.
        private static bool Pass(roadGenScript gen, bool moreFrame, bool dryRun, out double margin, out roadGenScript.roadElementClass first)
        {
            first = null;
            margin = double.MaxValue;
            bool did = false;
            double R = gen.roadPlaceDist;
            var arounds = gen.mapSetting.genArounds;

            foreach (Vector3d item in gen.chunksNeeded)
            {
                if (!gen.chunks.TryGetValue(item, out var dcc)) continue;
                foreach (var inner in dcc.innerChunks)
                {
                    // flag2 in the original: any genAround within R (XZ)
                    bool inRange = false;
                    double nearest = double.MaxValue;
                    for (int g = 0; g < arounds.Count; g++)
                    {
                        Vector3d d = inner.Key - mainscript.GlobalFromUnityPos(arounds[g].upos);
                        d.y = 0.0;
                        double mag = d.magnitude;
                        if (mag < nearest) nearest = mag;
                        if (mag < R) { inRange = true; break; } // original breaks here too
                    }
                    if (arounds.Count > 0)
                    {
                        double m = Math.Abs(nearest - R);
                        if (m < margin) margin = m;
                    }
                    else margin = 0;

                    foreach (var road in inner.Value.roads)
                    {
                        foreach (int el in road.roadElements)
                        {
                            var rec = dcc.roads[road.roadId].roadElements[el];
                            if (inRange)
                            {
                                if (rec.obj != null) continue;
                                if (dryRun) { margin = 0; first = rec; return true; }
                                rec.obj = UnityEngine.Object.Instantiate(itemdatabase.s.Road(rec.roadID), gen.parent);
                                var rs = rec.obj.GetComponent<roadscript>();
                                rs.Init(gen, item, road.roadId, el, gen.mapSetting.seed);
                                rs.Place(rec.bonePos);
                                if (dcc.roads[road.roadId].roadType == roadGenScript.roadTypeEmu.mainRoad && el != 0)
                                    rs.PowerPole(itemdatabase.s.rPowerPole);
                            }
                            else
                            {
                                if (rec.obj == null) continue;
                                if (dryRun) { margin = 0; first = rec; return true; }
                                UnityEngine.Object.Destroy(rec.obj);
                            }
                            did = true;
                            if (moreFrame) { margin = 0; return true; } // original: one action per frame
                        }
                    }
                }
            }
            if (did) margin = 0;
            return did;
        }

        /// Fingerprint of everything the pass depends on besides positions: which chunks are needed, and for each one
        /// its identity, inner-chunk count and a change counter. The inner-chunk dictionaries, their road lists and element
        /// index lists are only ever modified by roadGenScript.UpdInnerChunk, which bumps the chunk's counter (postfix
        /// below). This replaced a full walk of every inner chunk each frame (~1.1 ms/frame, profiled 2026-09-25).
        private static long Signature(roadGenScript gen)
        {
            unchecked
            {
                long h = gen.chunks.Count * 1000003L + gen.chunksNeeded.Count;
                foreach (Vector3d c in gen.chunksNeeded)
                {
                    h += (long)(c.x * 73856093.0) ^ (long)(c.z * 19349663.0);
                    if (!gen.chunks.TryGetValue(c, out var dcc)) continue;
                    h = h * 31 + RuntimeHelpers.GetHashCode(dcc);
                    h = h * 31 + dcc.innerChunks.Count;
                    h = h * 31 + Changes.GetValue(dcc, _ => new Counter()).Value;
                }
                return h;
            }
        }

        /// Signature without the inner-chunk contents: which chunks are needed, in which order, which objects, reset count.
        private static long ShapeSignature(roadGenScript gen)
        {
            unchecked
            {
                long h = gen.chunks.Count * 1000003L + gen.chunksNeeded.Count;
                foreach (Vector3d c in gen.chunksNeeded)
                {
                    h += (long)(c.x * 73856093.0) ^ (long)(c.z * 19349663.0);
                    if (!gen.chunks.TryGetValue(c, out var dcc)) continue;
                    h = h * 31 + RuntimeHelpers.GetHashCode(dcc);
                    h = h * 31 + Resets.GetValue(dcc, _ => new Counter()).Value;
                }
                return h;
            }
        }

        private static readonly ConditionalWeakTable<roadGenScript.dotChunkClass, Counter> Resets =
            new ConditionalWeakTable<roadGenScript.dotChunkClass, Counter>();

        /// DotChunkMemo empties a reused chunk's inner chunks (the only place any are removed).
        internal static void ChunkReset(roadGenScript.dotChunkClass dcc)
        {
            if (dcc != null) Resets.GetValue(dcc, _ => new Counter()).Value++;
        }

        private sealed class Counter { public long Value; }
        private static readonly ConditionalWeakTable<roadGenScript.dotChunkClass, Counter> Changes =
            new ConditionalWeakTable<roadGenScript.dotChunkClass, Counter>();

        [HarmonyPatch(typeof(roadGenScript), nameof(roadGenScript.UpdInnerChunk))]
        [HarmonyPostfix]
        private static void InnerChunkChanged(roadGenScript __instance, roadGenScript.dotChunkClass _chunk, Vector3d _currentPos)
        {
            if (_chunk == null) return;
            Changes.GetValue(_chunk, _ => new Counter()).Value++;
            if (!HeapMode || !States.TryGetValue(__instance, out var st) || !st.HeapReady) return;
            var key = mainscript.GetChunkPos(_currentPos, __instance.innerChunkSize);
            if (!_chunk.innerChunks.TryGetValue(key, out var ic)) return;
            // Unknown to the last full scan and not logged yet → inserted by this very call: enumerated last
            // (inner chunks are never removed except by a chunk reset, which forces a full scan)
            bool known = (st.Recs.TryGetValue(ic, out var r) && r.Ic == ic) || st.LoggedNew.Contains(ic);
            int ord = -1;
            if (!known) { ord = _chunk.innerChunks.Count - 1; st.LoggedNew.Add(ic); }
            st.Log.Add(new Change { Dcc = _chunk, Key = key, NewOrd = ord });
        }

        /// Incremental version of Pass (see Incremental). full: walk everything and (re)record every inner chunk's state.
        private static bool Scan(roadGenScript gen, State st, bool moreFrame, bool full, out double margin, out roadGenScript.roadElementClass acted)
        {
            acted = null;
            margin = double.MaxValue;
            bool did = false, stopped = false;
            double R = gen.roadPlaceDist;
            var arounds = gen.mapSetting.genArounds;
            if (full) { st.InRange.Clear(); st.Dirty.Clear(); st.Heap.Clear(); st.ChunkIdxOf.Clear(); st.ChunkKeyOf.Clear(); }
            st.Log.Clear(); st.LoggedNew.Clear(); // a full scan sees everything; an incremental Scan runs only when nothing changed
            bool buildHeap = full && HeapMode && TravelBound;
            if (!full) st.HeapReady = false; // this pass doesn't maintain the heap
            int chunkIdx = -1;
            // Each generation centre's global position once per pass (was converted per inner chunk × centre)
            if (_gpos.Length < arounds.Count) _gpos = new Vector3d[arounds.Count * 2];
            for (int g = 0; g < arounds.Count; g++) _gpos[g] = mainscript.GlobalFromUnityPos(arounds[g].upos);
            _visited.Clear();
            // Largest centre displacement since the previous pass (horizontal, as the distances are)
            bool bound = TravelBound && !full && st.GenPos.Count == arounds.Count;
            if (bound)
            {
                double step = 0;
                for (int g = 0; g < arounds.Count; g++) { double m = (Flat(_gpos[g]) - st.GenPos[g]).magnitude; if (m > step) step = m; }
                st.Mx += step;
            }
            // (full scans re-evaluate every inner chunk because `bound` is false; records are simply overwritten)
            foreach (Vector3d item in gen.chunksNeeded)
            {
                if (!_visited.Add(item)) continue; // a repeated chunk (several players) can't change anything twice
                if (!gen.chunks.TryGetValue(item, out var dcc)) continue;
                chunkIdx++;
                if (full) { st.ChunkIdxOf[dcc] = chunkIdx; st.ChunkKeyOf[dcc] = item; }
                int ord = -1;
                foreach (var inner in dcc.innerChunks)
                {
                    var ic = inner.Value;
                    ord++;
                    bool inRange = false;
                    if (bound && st.Recs.TryGetValue(ic, out var ir) && st.Mx - ir.MxAtEval < ir.Gap)
                    {
                        inRange = ir.InRange; // provably unchanged
                        double gapNow = ir.Gap - (st.Mx - ir.MxAtEval);
                        if (gapNow < margin) margin = gapNow;
                        EvalsSkipped++;
                    }
                    else
                    {
                        // Same in-range decision as the game (any centre closer than R); gap over all centres
                        double gap = double.MaxValue;
                        for (int g = 0; g < arounds.Count; g++)
                        {
                            Vector3d d = inner.Key - _gpos[g];
                            d.y = 0.0;
                            double mag = d.magnitude;
                            if (mag < R) inRange = true;
                            double gg = Math.Abs(mag - R);
                            if (gg < gap) gap = gg;
                        }
                        if (arounds.Count > 0) { if (gap < margin) margin = gap; }
                        else margin = 0;
                        if (TravelBound)
                        {
                            ir = st.Recs.GetValue(ic, _ => new Rec());
                            ir.InRange = inRange; ir.Gap = gap; ir.MxAtEval = st.Mx;
                            if (buildHeap)
                            {
                                ir.Expiry = st.Mx + gap; ir.ChunkIdx = chunkIdx; ir.Ord = ord;
                                ir.Ic = ic; ir.Dcc = dcc; ir.ChunkKey = item; ir.Key = inner.Key;
                                st.Heap.Add(ir);
                            }
                        }
                        Evals++;
                    }

                    if (full || !st.InRange.TryGetValue(ic, out bool was) || was != inRange)
                    {
                        st.InRange[ic] = inRange;
                        st.Dirty.Add(ic);
                    }
                    if (stopped || !st.Dirty.Contains(ic)) continue;
                    IncrWalked++;
                    bool actedHere = false;
                    foreach (var road in ic.roads)
                    {
                        foreach (int el in road.roadElements)
                        {
                            var rec = dcc.roads[road.roadId].roadElements[el];
                            if (inRange)
                            {
                                if (rec.obj != null) continue;
                                rec.obj = UnityEngine.Object.Instantiate(itemdatabase.s.Road(rec.roadID), gen.parent);
                                var rs = rec.obj.GetComponent<roadscript>();
                                rs.Init(gen, item, road.roadId, el, gen.mapSetting.seed);
                                rs.Place(rec.bonePos);
                                if (dcc.roads[road.roadId].roadType == roadGenScript.roadTypeEmu.mainRoad && el != 0)
                                    rs.PowerPole(itemdatabase.s.rPowerPole);
                            }
                            else
                            {
                                if (rec.obj == null) continue;
                                UnityEngine.Object.Destroy(rec.obj);
                            }
                            if (acted == null) acted = rec;
                            did = true;
                            if (moreFrame) { actedHere = true; break; } // original: one action per frame
                        }
                        if (actedHere) break;
                    }
                    if (actedHere) stopped = true; // this inner chunk stays dirty; later ones keep their state
                    else st.Dirty.Remove(ic);
                }
            }
            if (buildHeap)
            {
                for (int i = st.Heap.Count / 2 - 1; i >= 0; i--) Down(st.Heap, i);
                st.HeapReady = arounds.Count > 0;
            }
            if (did) margin = 0;
            return did;
        }

        private static readonly List<Rec> _popped = new List<Rec>();
        private static readonly List<Rec> _dirty = new List<Rec>();
        private static readonly Comparison<Rec> GameOrder = (a, b) => a.ChunkIdx != b.ChunkIdx ? a.ChunkIdx.CompareTo(b.ChunkIdx) : a.Ord.CompareTo(b.Ord);

        /// Incremental pass in heap mode (see HeapMode): same decisions as Scan's incremental pass.
        private static bool HeapScan(roadGenScript gen, State st, bool moreFrame, out double margin, out roadGenScript.roadElementClass acted)
        {
            acted = null;
            bool did = false;
            double R = gen.roadPlaceDist;
            var arounds = gen.mapSetting.genArounds;
            HeapPasses++;
            if (_gpos.Length < arounds.Count) _gpos = new Vector3d[arounds.Count * 2];
            for (int g = 0; g < arounds.Count; g++) _gpos[g] = mainscript.GlobalFromUnityPos(arounds[g].upos);
            double step = 0;
            for (int g = 0; g < arounds.Count; g++) { double m = (Flat(_gpos[g]) - st.GenPos[g]).magnitude; if (m > step) step = m; }
            st.Mx += step;

            var h = st.Heap;
            // Inner chunks / road pieces added since the last pass
            foreach (var ch in st.Log)
            {
                if (!st.ChunkIdxOf.TryGetValue(ch.Dcc, out int idx)) continue; // chunk outside the scope
                if (!ch.Dcc.innerChunks.TryGetValue(ch.Key, out var ic)) continue;
                if (st.Recs.TryGetValue(ic, out var known) && known.Ic == ic)
                {
                    st.Dirty.Add(ic); // new road pieces to place (or already in the heap as new this pass)
                    HeapDirtied++;
                    continue;
                }
                if (ch.NewOrd < 0)
                {
                    LogFallbacks++;
                    st.HeapReady = false;
                    return Scan(gen, st, moreFrame, true, out margin, out acted);
                }
                var nr = st.Recs.GetValue(ic, _ => new Rec());
                nr.Ic = ic; nr.Dcc = ch.Dcc; nr.ChunkKey = st.ChunkKeyOf[ch.Dcc]; nr.Key = ch.Key;
                nr.ChunkIdx = idx; nr.Ord = ch.NewOrd;
                nr.Expiry = double.NegativeInfinity; // evaluated below, then dirty (no in-range state yet)
                Push(h, nr);
                HeapInserts++;
            }
            st.Log.Clear();
            st.LoggedNew.Clear();

            // Re-evaluate every inner chunk whose bound may have expired (each once per pass)
            double limit = st.Mx + Math.Abs(st.Mx) * 1e-12 + 1e-9; // ties: evaluate early rather than late
            _popped.Clear();
            while (h.Count > 0 && h[0].Expiry <= limit) _popped.Add(Pop(h));
            foreach (var ir in _popped)
            {
                bool inRange = false;
                double gap = double.MaxValue;
                for (int g = 0; g < arounds.Count; g++)
                {
                    Vector3d d = ir.Key - _gpos[g];
                    d.y = 0.0;
                    double mag = d.magnitude;
                    if (mag < R) inRange = true;
                    double gg = Math.Abs(mag - R);
                    if (gg < gap) gap = gg;
                }
                ir.InRange = inRange; ir.Gap = gap; ir.MxAtEval = st.Mx; ir.Expiry = st.Mx + gap;
                Push(h, ir);
                Evals++; HeapPops++;
                if (!st.InRange.TryGetValue(ir.Ic, out bool was) || was != inRange)
                {
                    st.InRange[ir.Ic] = inRange;
                    st.Dirty.Add(ir.Ic);
                }
            }

            // Walk dirty inner chunks in the game's order, exactly like Scan
            if (st.Dirty.Count > 0)
            {
                _dirty.Clear();
                foreach (var ic in st.Dirty)
                {
                    if (!st.Recs.TryGetValue(ic, out var ir) || ir.Ic != ic) { st.HeapReady = false; margin = 0; return Scan(gen, st, moreFrame, true, out margin, out acted); }
                    _dirty.Add(ir);
                }
                _dirty.Sort(GameOrder);
                foreach (var ir in _dirty)
                {
                    IncrWalked++;
                    bool actedHere = false;
                    foreach (var road in ir.Ic.roads)
                    {
                        foreach (int el in road.roadElements)
                        {
                            var rec = ir.Dcc.roads[road.roadId].roadElements[el];
                            if (ir.InRange)
                            {
                                if (rec.obj != null) continue;
                                rec.obj = UnityEngine.Object.Instantiate(itemdatabase.s.Road(rec.roadID), gen.parent);
                                var rs = rec.obj.GetComponent<roadscript>();
                                rs.Init(gen, ir.ChunkKey, road.roadId, el, gen.mapSetting.seed);
                                rs.Place(rec.bonePos);
                                if (ir.Dcc.roads[road.roadId].roadType == roadGenScript.roadTypeEmu.mainRoad && el != 0)
                                    rs.PowerPole(itemdatabase.s.rPowerPole);
                            }
                            else
                            {
                                if (rec.obj == null) continue;
                                UnityEngine.Object.Destroy(rec.obj);
                            }
                            if (acted == null) acted = rec;
                            did = true;
                            if (moreFrame) { actedHere = true; break; }
                        }
                        if (actedHere) break;
                    }
                    if (actedHere) break; // stays dirty; the rest keep their state
                    st.Dirty.Remove(ir.Ic);
                }
            }
            margin = did ? 0 : h.Count > 0 ? h[0].Expiry - st.Mx : double.MaxValue;
            return did;
        }

        private static void Push(List<Rec> h, Rec r)
        {
            h.Add(r);
            int i = h.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (h[p].Expiry <= h[i].Expiry) break;
                var t = h[p]; h[p] = h[i]; h[i] = t;
                i = p;
            }
        }

        private static Rec Pop(List<Rec> h)
        {
            var top = h[0];
            int last = h.Count - 1;
            h[0] = h[last];
            h.RemoveAt(last);
            if (h.Count > 0) Down(h, 0);
            return top;
        }

        private static void Down(List<Rec> h, int i)
        {
            int n = h.Count;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < n && h[l].Expiry < h[m].Expiry) m = l;
                if (r < n && h[r].Expiry < h[m].Expiry) m = r;
                if (m == i) return;
                var t = h[m]; h[m] = h[i]; h[i] = t;
                i = m;
            }
        }

        private static Vector3d[] _gpos = new Vector3d[16];
        private static readonly HashSet<Vector3d> _visited = new HashSet<Vector3d>();

        private static Vector3d Flat(Vector3d v) { v.y = 0.0; return v; }

        private static double MaxMoved(List<mapSettingScript.genAround> arounds, List<Vector3d> then)
        {
            double max = 0;
            for (int i = 0; i < arounds.Count; i++)
            {
                double m = (Flat(mainscript.GlobalFromUnityPos(arounds[i].upos)) - then[i]).magnitude;
                if (m > max) max = m;
            }
            return max;
        }
    }
}
