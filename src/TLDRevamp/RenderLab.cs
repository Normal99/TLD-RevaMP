using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TLDRevamp
{
    /// Rendering structure inventory (bridge `renderinv`): what every camera, light, reflection probe and terrain is set
    /// up to do, quality settings, and renderer counts per layer. Structure only, no timing.
    public static class RenderLab
    {
        public static string Inventory()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"quality\":{")
              .Append("\"level\":").Append(Json.Str(QualitySettings.names[QualitySettings.GetQualityLevel()]))
              .Append(",\"shadows\":").Append(Json.Str(QualitySettings.shadows.ToString()))
              .Append(",\"shadowDistance\":").Append(QualitySettings.shadowDistance)
              .Append(",\"shadowCascades\":").Append(QualitySettings.shadowCascades)
              .Append(",\"shadowResolution\":").Append(Json.Str(QualitySettings.shadowResolution.ToString()))
              .Append(",\"shadowProjection\":").Append(Json.Str(QualitySettings.shadowProjection.ToString()))
              .Append(",\"pixelLightCount\":").Append(QualitySettings.pixelLightCount)
              .Append(",\"lodBias\":").Append(QualitySettings.lodBias)
              .Append(",\"maximumLODLevel\":").Append(QualitySettings.maximumLODLevel)
              .Append(",\"antiAliasing\":").Append(QualitySettings.antiAliasing)
              .Append(",\"softParticles\":").Append(QualitySettings.softParticles ? "true" : "false")
              .Append(",\"realtimeReflectionProbes\":").Append(QualitySettings.realtimeReflectionProbes ? "true" : "false")
              .Append(",\"vSyncCount\":").Append(QualitySettings.vSyncCount)
              .Append(",\"targetFrameRate\":").Append(Application.targetFrameRate)
              .Append(",\"screen\":").Append(Json.Str(Screen.width + "x" + Screen.height))
              .Append(",\"graphicsJobs\":").Append(SystemInfo.graphicsMultiThreaded ? "true" : "false")
              .Append(",\"gfx\":").Append(Json.Str(SystemInfo.graphicsDeviceType.ToString()))
              .Append('}');

            sb.Append(",\"cameras\":[");
            bool first = true;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c.gameObject.scene.name == null) continue;
                if (!first) sb.Append(',');
                first = false;
                var rt = c.targetTexture;
                var lcd = c.layerCullDistances;
                var cullDist = new List<string>();
                if (lcd != null) for (int i = 0; i < lcd.Length; i++) if (lcd[i] > 0) cullDist.Add(LayerMask.LayerToName(i) + "=" + lcd[i]);
                var comps = c.GetComponents<Behaviour>().Where(b => !(b is Camera)).Select(b => b.GetType().Name + (b.enabled ? "" : "(off)"));
                sb.Append("{\"name\":").Append(Json.Str(c.name))
                  .Append(",\"on\":").Append(c.enabled && c.gameObject.activeInHierarchy ? "true" : "false")
                  .Append(",\"depth\":").Append(c.depth)
                  .Append(",\"clear\":").Append(Json.Str(c.clearFlags.ToString()))
                  .Append(",\"near\":").Append(c.nearClipPlane).Append(",\"far\":").Append(c.farClipPlane)
                  .Append(",\"fov\":").Append(c.fieldOfView.ToString("F1"))
                  .Append(",\"rt\":").Append(Json.Str(rt != null ? rt.width + "x" + rt.height + " " + rt.format : ""))
                  .Append(",\"path\":").Append(Json.Str(c.actualRenderingPath.ToString()))
                  .Append(",\"occlusion\":").Append(c.useOcclusionCulling ? "true" : "false")
                  .Append(",\"hdr\":").Append(c.allowHDR ? "true" : "false").Append(",\"msaa\":").Append(c.allowMSAA ? "true" : "false")
                  .Append(",\"layers\":").Append(Json.Str(Layers(c.cullingMask)))
                  .Append(",\"layerCull\":").Append(Json.Str(string.Join(" ", cullDist)))
                  .Append(",\"sphericalCull\":").Append(c.layerCullSpherical ? "true" : "false")
                  .Append(",\"components\":").Append(Json.Str(string.Join(" ", comps)))
                  .Append('}');
            }
            sb.Append(']');

            sb.Append(",\"lights\":[");
            first = true;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Json.Str(l.name)).Append(",\"type\":").Append(Json.Str(l.type.ToString()))
                  .Append(",\"on\":").Append(l.enabled ? "true" : "false")
                  .Append(",\"shadows\":").Append(Json.Str(l.shadows.ToString()))
                  .Append(",\"shadowRes\":").Append(Json.Str(l.shadowResolution.ToString()))
                  .Append(",\"range\":").Append(l.range.ToString("F0"))
                  .Append(",\"renderMode\":").Append(Json.Str(l.renderMode.ToString()))
                  .Append(",\"layers\":").Append(Json.Str(Layers(l.cullingMask)))
                  .Append(",\"intensity\":").Append(l.intensity.ToString("F2")).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"probes\":[");
            first = true;
            foreach (var p in Object.FindObjectsOfType<ReflectionProbe>())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Json.Str(p.name)).Append(",\"on\":").Append(p.enabled ? "true" : "false")
                  .Append(",\"mode\":").Append(Json.Str(p.mode.ToString()))
                  .Append(",\"refresh\":").Append(Json.Str(p.refreshMode.ToString()))
                  .Append(",\"timeSlicing\":").Append(Json.Str(p.timeSlicingMode.ToString()))
                  .Append(",\"resolution\":").Append(p.resolution)
                  .Append(",\"far\":").Append(p.farClipPlane).Append(",\"shadowDistance\":").Append(p.shadowDistance)
                  .Append(",\"layers\":").Append(Json.Str(Layers(p.cullingMask)))
                  .Append(",\"hdr\":").Append(p.hdr ? "true" : "false")
                  .Append(",\"path\":").Append(Json.Str(PathOf(p.transform))).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"terrains\":[");
            first = true;
            int terrainCount = 0;
            foreach (var t in Terrain.activeTerrains)
            {
                terrainCount++;
                if (terrainCount > 3) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Json.Str(t.name))
                  .Append(",\"pixelError\":").Append(t.heightmapPixelError)
                  .Append(",\"basemapDist\":").Append(t.basemapDistance)
                  .Append(",\"drawTrees\":").Append(t.drawTreesAndFoliage ? "true" : "false")
                  .Append(",\"detailDist\":").Append(t.detailObjectDistance)
                  .Append(",\"treeDist\":").Append(t.treeDistance)
                  .Append(",\"castShadows\":").Append(Json.Str(t.shadowCastingMode.ToString()))
                  .Append(",\"heightmapRes\":").Append(t.terrainData != null ? t.terrainData.heightmapResolution : 0)
                  .Append(",\"size\":").Append(Json.Str(t.terrainData != null ? t.terrainData.size.ToString() : "")).Append('}');
            }
            sb.Append("],\"terrainCount\":").Append(terrainCount);

            // Renderers per layer: total / visible (to any camera, last frame) / shadow casters / visible shadow casters
            var per = new Dictionary<int, int[]>();
            int total = 0, particle = 0, skinned = 0, lodded = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                total++;
                if (r is ParticleSystemRenderer) particle++;
                if (r is SkinnedMeshRenderer) skinned++;
                int layer = r.gameObject.layer;
                if (!per.TryGetValue(layer, out var a)) per[layer] = a = new int[5];
                a[0]++;
                if (r.isVisible) a[1]++;
                bool caster = r.shadowCastingMode != ShadowCastingMode.Off && r.enabled && r.gameObject.activeInHierarchy;
                if (caster) a[2]++;
                if (caster && r.isVisible) a[3]++;
                if (r.enabled && r.gameObject.activeInHierarchy) a[4]++;
            }
            foreach (var g in Object.FindObjectsOfType<LODGroup>()) lodded += g.lodCount > 0 ? 1 : 0;
            sb.Append(",\"renderers\":{\"total\":").Append(total).Append(",\"particle\":").Append(particle).Append(",\"skinned\":").Append(skinned)
              .Append(",\"lodGroups\":").Append(lodded).Append(",\"byLayer\":[");
            first = true;
            foreach (var kv in per.OrderByDescending(kv => kv.Value[1]))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"layer\":").Append(Json.Str(kv.Key + ":" + LayerMask.LayerToName(kv.Key)))
                  .Append(",\"total\":").Append(kv.Value[0]).Append(",\"active\":").Append(kv.Value[4]).Append(",\"visible\":").Append(kv.Value[1])
                  .Append(",\"casters\":").Append(kv.Value[2]).Append(",\"visibleCasters\":").Append(kv.Value[3]).Append('}');
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        // Per-camera main-thread render time: Camera.onPreCull → Camera.onPostRender (culling, shadows, draw submission,
        // image effects of that camera). Reflection-probe and other internal cameras show up with their CameraType.
        private sealed class CamStat { public long Ticks, Max, Frames; public string Type; }
        private static readonly Dictionary<string, CamStat> CamStats = new Dictionary<string, CamStat>();
        private static readonly Dictionary<Camera, long> Started = new Dictionary<Camera, long>();
        private static bool _timing;
        public static bool TimingWithPos;
        private static int _frames0;
        private static readonly string[] CounterNames = { "Batches Count", "SetPass Calls Count", "Draw Calls Count", "Triangles Count",
                                                          "Vertices Count", "Shadow Casters Count", "Render Textures Changes Count", "Used Buffers Count" };
        private static Unity.Profiling.ProfilerRecorder[] _recorders;
        private static long[] _counterSums;

        public static string TimingStart()
        {
            if (!_timing)
            {
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
                _timing = true;
            }
            CamStats.Clear();
            Started.Clear();
            _frames0 = Time.frameCount;
            if (_recorders == null)
            {
                _recorders = new Unity.Profiling.ProfilerRecorder[CounterNames.Length];
                for (int i = 0; i < CounterNames.Length; i++)
                    _recorders[i] = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, CounterNames[i]);
            }
            _counterSums = new long[CounterNames.Length];
            return "{\"timing\":true}";
        }

        /// Called once per frame by the Runner: sample the render counters (last frame's values).
        public static void Tick()
        {
            if (!_timing || _recorders == null) return;
            for (int i = 0; i < _recorders.Length; i++) if (_recorders[i].Valid) _counterSums[i] += _recorders[i].LastValue;
        }

        public static string TimingStop()
        {
            if (_timing)
            {
                Camera.onPreCull -= PreCull;
                Camera.onPostRender -= PostRender;
                _timing = false;
            }
            int frames = Mathf.Max(1, Time.frameCount - _frames0);
            double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var sb = new StringBuilder("{\"frames\":").Append(frames).Append(",\"cameras\":[");
            bool first = true;
            foreach (var kv in CamStats.OrderByDescending(kv => kv.Value.Ticks))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"cam\":").Append(Json.Str(kv.Key)).Append(",\"type\":").Append(Json.Str(kv.Value.Type))
                  .Append(",\"msPerFrame\":").Append((kv.Value.Ticks * tickMs / frames).ToString("F3"))
                  .Append(",\"rendersPerFrame\":").Append((kv.Value.Frames / (double)frames).ToString("F2"))
                  .Append(",\"maxMs\":").Append((kv.Value.Max * tickMs).ToString("F2")).Append('}');
            }
            sb.Append("],\"counters\":{");
            for (int i = 0; i < CounterNames.Length; i++)
            {
                if (i > 0) sb.Append(',');
                bool valid = _recorders != null && _recorders[i].Valid;
                sb.Append(Json.Str(CounterNames[i])).Append(':').Append(valid ? (_counterSums[i] / (double)frames).ToString("F0") : "null");
            }
            return sb.Append("}}").ToString();
        }

        // Gaps between camera renders within a frame (image effects after OnPostRender, probe/RT work, etc.), attributed
        // to "after <previous camera>".
        private static long _lastEventTicks;
        private static int _lastEventFrame = -1;
        private static string _lastEventCam = "";

        private static long _firstPreCull, _lastPostRender;
        private static int _firstPreCullFrame = -1;

        /// From PlayerLoopProfiler (needs `loopprof start`): the render phase's start/end this frame.
        public static void FinishFrameBounds(long start, long end)
        {
            if (!_timing) return;
            if (_firstPreCullFrame == Time.frameCount && _firstPreCull >= start && _firstPreCull <= end)
            {
                Add("before first camera", _firstPreCull - start);
                if (_lastPostRender >= _firstPreCull && _lastPostRender <= end) Add("after last camera", end - _lastPostRender);
            }
            Add("render phase total", end - start);
        }

        private static void Add(string key, long dt)
        {
            if (!CamStats.TryGetValue(key, out var g)) CamStats[key] = g = new CamStat { Type = "phase" };
            g.Ticks += dt; g.Frames++; if (dt > g.Max) g.Max = dt;
        }

        private static void PreCull(Camera c)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_firstPreCullFrame != Time.frameCount && c.cameraType != CameraType.Reflection && c.name != "GrassCamera")
            { _firstPreCullFrame = Time.frameCount; _firstPreCull = now; }
            if (_lastEventFrame == Time.frameCount && Started.Count == 0) // not nested inside another camera
            {
                string key = "gap after " + _lastEventCam;
                if (!CamStats.TryGetValue(key, out var g)) CamStats[key] = g = new CamStat { Type = "gap" };
                long dt = now - _lastEventTicks;
                g.Ticks += dt; g.Frames++; if (dt > g.Max) g.Max = dt;
            }
            Started[c] = now;
        }

        private static void PostRender(Camera c)
        {
            if (!Started.TryGetValue(c, out long t0)) return;
            Started.Remove(c);
            long nowT = System.Diagnostics.Stopwatch.GetTimestamp();
            long dt = nowT - t0;
            if (Started.Count == 0) { _lastEventTicks = nowT; _lastEventFrame = Time.frameCount; _lastEventCam = c.name; _lastPostRender = nowT; }
            string key = c.name + (c.cameraType != CameraType.Game ? " [" + c.cameraType + "]" : "") + (c.targetTexture != null ? " rt" : "");
            if (TimingWithPos && c.cameraType == CameraType.Reflection) key += " @" + Vector3Int.RoundToInt(c.transform.position) + " far" + c.farClipPlane.ToString("F0");
            if (!CamStats.TryGetValue(key, out var s)) CamStats[key] = s = new CamStat { Type = c.cameraType.ToString() };
            s.Ticks += dt;
            s.Frames++;
            if (dt > s.Max) s.Max = dt;
        }

        // Measurement-only toggles (bridge `rtoggle <what> <value>`); `rtoggle restore` puts every original back.
        private static readonly List<System.Action> Undo = new List<System.Action>();

        public static string Toggle(string what, string val)
        {
            int n = 0;
            switch (what)
            {
                case "restore":
                    for (int i = Undo.Count - 1; i >= 0; i--) Undo[i]();
                    n = Undo.Count;
                    Undo.Clear();
                    break;
                case "sunshadows":   // on/off
                    foreach (var l in Object.FindObjectsOfType<Light>())
                        if (l.type == LightType.Directional && l.enabled) { var o = l.shadows; var lt = l; Undo.Add(() => lt.shadows = o); l.shadows = val == "on" ? LightShadows.Soft : LightShadows.None; n++; }
                    break;
                case "spotshadows":
                    foreach (var l in Object.FindObjectsOfType<Light>())
                        if (l.type != LightType.Directional && l.enabled) { var o = l.shadows; var lt = l; Undo.Add(() => lt.shadows = o); l.shadows = val == "on" ? LightShadows.Hard : LightShadows.None; n++; }
                    break;
                case "cascades": { var o = QualitySettings.shadowCascades; Undo.Add(() => QualitySettings.shadowCascades = o); QualitySettings.shadowCascades = int.Parse(val); n = 1; break; }
                case "shadowdist": { var o = QualitySettings.shadowDistance; Undo.Add(() => QualitySettings.shadowDistance = o); QualitySettings.shadowDistance = float.Parse(val, System.Globalization.CultureInfo.InvariantCulture); n = 1; break; }
                case "msaa": { var o = QualitySettings.antiAliasing; Undo.Add(() => QualitySettings.antiAliasing = o); QualitySettings.antiAliasing = int.Parse(val); n = 1; break; }
                case "layer":   // layer:<name> → renderer.enabled=false for every enabled renderer on that layer (measurement)
                    {
                        int layer = LayerMask.NameToLayer(val);
                        foreach (var r in Object.FindObjectsOfType<Renderer>())
                            if (r.gameObject.layer == layer && r.enabled) { var rr = r; Undo.Add(() => { if (rr != null) rr.enabled = true; }); r.enabled = false; n++; }
                        break;
                    }
                case "smallcasters":   // shadow casting off for renderers with bounds radius < val metres (measurement)
                    {
                        float rad = float.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
                        foreach (var r in Object.FindObjectsOfType<Renderer>())
                            if (r.shadowCastingMode == ShadowCastingMode.On && r.bounds.extents.magnitude < rad)
                            { var rr = r; Undo.Add(() => { if (rr != null) rr.shadowCastingMode = ShadowCastingMode.On; }); r.shadowCastingMode = ShadowCastingMode.Off; n++; }
                        break;
                    }
                case "probe":
                    foreach (var p in Object.FindObjectsOfType<ReflectionProbe>())
                        if (p.enabled) { var pp = p; Undo.Add(() => pp.enabled = true); p.enabled = false; n++; }
                    break;
                default:
                    // "pp:<cameraName>" off → disable that camera's PostProcessLayer (and other image-effect behaviours named like it)
                    if (what.StartsWith("pp:"))
                    {
                        string cam = what.Substring(3);
                        foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                        {
                            if (c.gameObject.scene.name == null || c.name != cam || !c.isActiveAndEnabled) continue;
                            foreach (var bh in c.GetComponents<Behaviour>())
                                if (bh.GetType().Name == "PostProcessLayer" && bh.enabled == (val == "off")) { var b = bh; bool o = b.enabled; Undo.Add(() => b.enabled = o); b.enabled = val != "off"; n++; }
                        }
                    }
                    else if (what.StartsWith("comp:"))  // comp:<TypeName> off → disable every enabled component of that type
                    {
                        var type = Reflect.FindType(what.Substring(5));
                        if (type == null) return "{\"error\":\"type not found\"}";
                        foreach (var o in Object.FindObjectsOfType(type))
                            if (o is Behaviour bh && bh.enabled) { var bb = bh; Undo.Add(() => { if (bb != null) bb.enabled = true; }); bh.enabled = false; n++; }
                    }
                    else if (what.StartsWith("near:"))  // near:<cameraName> <meters>
                    {
                        string cam = what.Substring(5);
                        float v = float.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
                        foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                            if (c.gameObject.scene.name != null && c.name == cam && c.isActiveAndEnabled) { var cc = c; float o = c.nearClipPlane; Undo.Add(() => cc.nearClipPlane = o); c.nearClipPlane = v; n++; }
                    }
                    else if (what.StartsWith("cam:"))  // cam:<name> off → camera component off
                    {
                        string cam = what.Substring(4);
                        foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                            if (c.gameObject.scene.name != null && c.name == cam && c.enabled == (val == "off")) { var cc = c; bool o = c.enabled; Undo.Add(() => cc.enabled = o); c.enabled = val != "off"; n++; }
                    }
                    else return "{\"error\":\"unknown toggle\"}";
                    break;
            }
            return "{\"changed\":" + n + ",\"pendingUndo\":" + Undo.Count + "}";
        }

        private static readonly Dictionary<string, long> Tris = new Dictionary<string, long>();

        /// Visible renderers (isVisible: any camera incl. shadows) grouped by root object, with materials and casters.
        public static string VisibleByRoot(int top)
        {
            var groups = new Dictionary<string, int[]>();
            Tris.Clear();
            var mats = new Dictionary<string, HashSet<int>>();
            var cam = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Cam : null;
            var planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.isVisible) continue;
                var root = r.transform.root;
                string key = root.name;
                if (key.EndsWith("Parent") && r.transform.parent != null) key = root.name + "/" + (r.transform.parent == root ? r.name : r.transform.parent.name);
                key = System.Text.RegularExpressions.Regex.Replace(key, @" \(\d+\)|\(Clone\)", "");
                if (!groups.TryGetValue(key, out var g)) { groups[key] = g = new int[5]; mats[key] = new HashSet<int>(); }
                g[0]++;
                if (r.shadowCastingMode != ShadowCastingMode.Off) g[1]++;
                if (planes != null && GeometryUtility.TestPlanesAABB(planes, r.bounds)) g[2]++;
                g[3] += r.sharedMaterials.Length;
                if (cam != null)
                {
                    float dist = Vector3.Distance(cam.transform.position, r.bounds.center);
                    float k = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                    float px = dist > 0.01f ? r.bounds.extents.magnitude * 2f * k / dist : 9999f;
                    if (px < 4f) g[4]++;
                }
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : (r is SkinnedMeshRenderer smr ? smr.sharedMesh : null);
                if (mesh != null) { long tri = 0; for (int sm = 0; sm < mesh.subMeshCount; sm++) tri += mesh.GetIndexCount(sm) / 3; Tris.TryGetValue(key, out long t0); Tris[key] = t0 + tri; }
                foreach (var m in r.sharedMaterials) if (m != null) mats[key].Add(m.GetInstanceID());
            }
            var sb = new StringBuilder("{\"groups\":[");
            bool first = true;
            foreach (var kv in groups.OrderByDescending(kv => kv.Value[3]).Take(top))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"root\":").Append(Json.Str(kv.Key)).Append(",\"visible\":").Append(kv.Value[0])
                  .Append(",\"inMainFrustum\":").Append(kv.Value[2]).Append(",\"casters\":").Append(kv.Value[1])
                  .Append(",\"submeshes\":").Append(kv.Value[3]).Append(",\"materials\":").Append(mats[kv.Key].Count)
                  .Append(",\"under4px\":").Append(kv.Value[4]).Append(",\"ktris\":").Append(Tris.TryGetValue(kv.Key, out long tt) ? (tt / 1000) : 0).Append('}');
            }
            return sb.Append("],\"groupCount\":").Append(groups.Count).Append('}').ToString();
        }

        /// Instancing potential: visible MeshRenderers grouped by (mesh, material); how many materials have
        /// enableInstancing; groups with ≥2 members could become instanced draws.
        public static string InstancingInfo()
        {
            var groups = new Dictionary<long, int>();
            int visible = 0, drawsVisible = 0, instOn = 0, instOff = 0;
            var mats = new HashSet<Material>();
            var shaders = new Dictionary<string, int>();
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!r.isVisible) continue;
                visible++;
                var mf = r.GetComponent<MeshFilter>();
                int meshId = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    drawsVisible++;
                    long key = ((long)meshId << 32) ^ (uint)m.GetInstanceID();
                    groups.TryGetValue(key, out int c); groups[key] = c + 1;
                    if (mats.Add(m))
                    {
                        if (m.enableInstancing) instOn++; else instOff++;
                        string sn = m.shader != null ? m.shader.name : "null";
                        shaders.TryGetValue(sn, out int k); shaders[sn] = k + 1;
                    }
                }
            }
            int inGroups = groups.Values.Where(v => v >= 2).Sum(), groupCount = groups.Values.Count(v => v >= 2);
            var sb = new StringBuilder("{\"visibleMeshRenderers\":").Append(visible).Append(",\"visibleDraws\":").Append(drawsVisible)
                .Append(",\"drawsInSharedGroups\":").Append(inGroups).Append(",\"sharedGroups\":").Append(groupCount)
                .Append(",\"materials\":").Append(mats.Count).Append(",\"instancingOn\":").Append(instOn).Append(",\"instancingOff\":").Append(instOff)
                .Append(",\"shaders\":{");
            bool first = true;
            foreach (var kv in shaders.OrderByDescending(kv => kv.Value).Take(15))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Str(kv.Key)).Append(':').Append(kv.Value);
            }
            return sb.Append("}}").ToString();
        }

        /// Measurement toggle: enableInstancing on every material of every renderer in the scene (undo via rtoggle restore).
        public static string InstancingOn()
        {
            int n = 0;
            var seen = new HashSet<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
                foreach (var m in r.sharedMaterials)
                    if (m != null && seen.Add(m) && !m.enableInstancing)
                    {
                        var mm = m; Undo.Add(() => mm.enableInstancing = false);
                        m.enableInstancing = true;
                        n++;
                    }
            return "{\"enabled\":" + n + "}";
        }

        /// Particle systems: how many exist / are playing / emitting / have particles (idle-but-playing systems still update).
        public static string Particles()
        {
            int total = 0, alive = 0, playing = 0, emitting = 0, withParticles = 0, looping = 0, collision = 0, subEmitters = 0;
            var byRoot = new Dictionary<string, int>();
            foreach (var ps in Object.FindObjectsOfType<ParticleSystem>())
            {
                total++;
                if (!ps.gameObject.activeInHierarchy) continue;
                alive++;
                if (ps.isPlaying) playing++;
                if (ps.isEmitting) emitting++;
                if (ps.particleCount > 0) withParticles++;
                if (ps.main.loop) looping++;
                if (ps.collision.enabled) collision++;
                if (ps.subEmitters.enabled) subEmitters++;
                if (ps.isPlaying && ps.particleCount == 0)
                {
                    string k = System.Text.RegularExpressions.Regex.Replace(ps.transform.root.name, @" \(\d+\)|\(Clone\)", "") + "/" + ps.name;
                    byRoot.TryGetValue(k, out int c); byRoot[k] = c + 1;
                }
            }
            var sb = new StringBuilder("{\"total\":").Append(total).Append(",\"activeInHierarchy\":").Append(alive).Append(",\"playing\":").Append(playing)
                .Append(",\"emitting\":").Append(emitting).Append(",\"withParticles\":").Append(withParticles).Append(",\"looping\":").Append(looping)
                .Append(",\"collision\":").Append(collision).Append(",\"subEmitters\":").Append(subEmitters).Append(",\"idlePlayingByObject\":{");
            bool first = true;
            foreach (var kv in byRoot.OrderByDescending(kv => kv.Value).Take(15))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Str(kv.Key)).Append(':').Append(kv.Value);
            }
            return sb.Append("}}").ToString();
        }

        /// Material sharing among renderers under roots matching `filter` (e.g. item names): distinct material objects,
        /// how many are "(Instance)" copies, renderers with a MaterialPropertyBlock, instancing flag.
        public static string MaterialSharing(string filter)
        {
            int renderers = 0, withMpb = 0, slots = 0, instanceNamed = 0, instOn = 0;
            var distinct = new HashSet<Material>();
            var byName = new Dictionary<string, HashSet<Material>>();
            var mpb = new MaterialPropertyBlock();
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                string root = r.transform.root.name;
                if (filter.Length > 0 && root.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                renderers++;
                if (r.HasPropertyBlock()) withMpb++;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    slots++;
                    if (distinct.Add(m))
                    {
                        if (m.name.EndsWith("(Instance)")) instanceNamed++;
                        if (m.enableInstancing) instOn++;
                    }
                    string baseName = m.name.Replace(" (Instance)", "");
                    if (!byName.TryGetValue(baseName, out var set)) byName[baseName] = set = new HashSet<Material>();
                    set.Add(m);
                }
            }
            var sb = new StringBuilder("{\"renderers\":").Append(renderers).Append(",\"withPropertyBlock\":").Append(withMpb)
                .Append(",\"materialSlots\":").Append(slots).Append(",\"distinctMaterials\":").Append(distinct.Count)
                .Append(",\"instanceCopies\":").Append(instanceNamed).Append(",\"instancingOn\":").Append(instOn).Append(",\"copiesPerName\":{");
            bool first = true;
            foreach (var kv in byName.OrderByDescending(kv => kv.Value.Count).Take(12))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Str(kv.Key)).Append(':').Append(kv.Value.Count);
            }
            return sb.Append("}}").ToString();
        }

        /// Which manually rendered mirrors are inside the main world camera's view this frame (vs merely isVisible).
        public static string Mirrors()
        {
            var ms = mainscript.s;
            if (ms == null || ms.Mirrors == null) return "{\"mirrors\":[]}";
            var cam = ms.player != null && ms.player.Cam != null ? ms.player.Cam : null;
            var planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            var sb = new StringBuilder("{\"bMirrors\":").Append(settingsscript.s.S.BMirrors ? "true" : "false")
                .Append(",\"drawDist\":").Append(settingsscript.s.S.FMirrorDrawDistance).Append(",\"mirrors\":[");
            for (int i = 0; i < ms.Mirrors.Count; i++)
            {
                var m = ms.Mirrors[i];
                if (i > 0) sb.Append(',');
                bool inView = planes != null && m.R != null && GeometryUtility.TestPlanesAABB(planes, m.R.bounds);
                sb.Append("{\"i\":").Append(i).Append(",\"dist\":").Append(m.distance.ToString("F1"))
                  .Append(",\"isVisible\":").Append(m.R != null && m.R.isVisible ? "true" : "false")
                  .Append(",\"inMainView\":").Append(inView ? "true" : "false")
                  .Append(",\"far\":").Append(m.C != null ? m.C.farClipPlane.ToString("F0") : "0")
                  .Append(",\"name\":").Append(Json.Str(m.T != null ? m.T.root.name + "/" + m.T.name : "")).Append('}');
                if (i >= 8) break;
            }
            return sb.Append("]}").ToString();
        }

        private static string Layers(int mask)
        {
            if (mask == -1) return "Everything";
            var names = new List<string>();
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
            return string.Join(",", names);
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
