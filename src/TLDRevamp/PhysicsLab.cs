using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// Test helpers for physics work (bridge commands `items`, `spawnpile`, `clearpile`, `physics`).
    /// Items are spawned through the game's own dev-menu path (mainscript.Spawn), then moved onto a fixed grid,
    /// with Unity's Random seeded first so random variants come out the same every run.
    public static class PhysicsLab
    {
        private static readonly List<GameObject> Spawned = new List<GameObject>();

        /// EXPERIMENT: collision detection mode forced on spawned test items. 0 = keep (game: Continuous),
        /// 1 = Discrete, 2 = ContinuousSpeculative. Only affects items spawned by `spawnpile`.
        public static int CcdMode = 0;

        public static string Items(string filter)
        {
            var db = itemdatabase.s;
            var sb = new StringBuilder("{\"items\":[");
            bool first = true;
            for (int i = 0; i < db.items.Length; i++)
            {
                var g = db.items[i];
                if (g == null) continue;
                if (filter.Length > 0 && g.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(i).Append(",\"name\":").Append(Json.Str(g.name))
                  .Append(",\"rb\":").Append(g.GetComponentsInChildren<Rigidbody>(true).Length).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        /// ids: comma-separated item ids, cycled. Grid of `cols` columns, `spacing` metres apart, starting `height` above (x,y,z).
        public static string SpawnPile(string ids, int count, Vector3 origin, int cols, float spacing, float height, int seed)
        {
            var idList = ids.Split(',').Select(int.Parse).ToArray();
            Random.InitState(seed);
            int ok = 0;
            for (int n = 0; n < count; n++)
            {
                var prefab = itemdatabase.s.Item(idList[n % idList.Length]);
                if (prefab == null) continue;
                var g = mainscript.s.Spawn(prefab);
                if (g == null) continue;
                int col = n % cols, row = n / cols;
                int layer = row / cols;
                row %= cols;
                g.transform.position = origin + new Vector3(col * spacing, height + layer * spacing, row * spacing);
                g.transform.rotation = Quaternion.identity;
                foreach (var rb in g.GetComponentsInChildren<Rigidbody>())
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    if (CcdMode == 1) rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    else if (CcdMode == 2) rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                }
                Spawned.Add(g);
                ok++;
            }
            Physics.SyncTransforms();
            return "{\"spawned\":" + ok + "}";
        }

        /// A test car in a known state: the dev menu's spawn (mainscript.Spawn with a part condition, as kaposztaleves does)
        /// — the plain Spawn(prefab) leaves the prefab's random conditions, and one such car01 had an engine making 40 N·m
        /// that couldn't pull away. condition 0 = new. Placed at `at` facing `yaw`, at rest.
        public static string SpawnCar(int id, int condition, Vector3 at, float yaw)
        {
            var prefab = itemdatabase.s.Item(id);
            if (prefab == null) return "{\"error\":\"no item " + id + "\"}";
            var g = mainscript.s.Spawn(prefab, Color.white, condition, -1, false);
            if (g == null) return "{\"error\":\"spawn failed\"}";
            g.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            foreach (var rb in g.GetComponentsInChildren<Rigidbody>()) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Physics.SyncTransforms();
            Spawned.Add(g);
            return "{\"spawned\":" + Json.Str(g.name) + ",\"condition\":" + condition + "}";
        }

        public static string ClearPile()
        {
            int n = 0;
            foreach (var g in Spawned) if (g != null) { Object.Destroy(g); n++; }
            Spawned.Clear();
            return "{\"destroyed\":" + n + "}";
        }

        /// Awake, non-kinematic bodies grouped by root object name: count, mean/max linear and angular speed,
        /// and the kinetic-energy-per-mass value PhysX compares with the sleep threshold.
        public static string Awake(int top)
        {
            var groups = new Dictionary<string, (int n, float v, float vmax, float w, float e)>();
            foreach (var b in Object.FindObjectsOfType<Rigidbody>())
            {
                if (b.isKinematic || b.IsSleeping()) continue;
                string key = b.transform.root.name;
                float v = b.velocity.magnitude, w = b.angularVelocity.magnitude;
                float e = 0.5f * (v * v + w * w); // rough energy/mass proxy
                groups.TryGetValue(key, out var g);
                groups[key] = (g.n + 1, g.v + v, Mathf.Max(g.vmax, v), g.w + w, g.e + e);
            }
            var sb = new StringBuilder("{\"groups\":[");
            bool first = true;
            foreach (var kv in groups.OrderByDescending(k => k.Value.n).Take(top))
            {
                if (!first) sb.Append(',');
                first = false;
                var g = kv.Value;
                sb.Append("{\"root\":").Append(Json.Str(kv.Key)).Append(",\"n\":").Append(g.n)
                  .Append(",\"vAvg\":").Append((g.v / g.n).ToString("F4")).Append(",\"vMax\":").Append(g.vmax.ToString("F4"))
                  .Append(",\"wAvg\":").Append((g.w / g.n).ToString("F4")).Append(",\"eAvg\":").Append((g.e / g.n).ToString("F5")).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        /// Times the two halves of wheelgraphicsscript.Graphics on every active wheel: the pose query vs writing the
        /// visual transform (same values back, so nothing changes visually). `reps` repetitions each.
        public static string WheelBench(int reps)
        {
            var wheels = Object.FindObjectsOfType<wheelgraphicsscript>();
            long tPose = 0, tWrite = 0; int n = 0, children = 0, childColliders = 0;
            var sw = new System.Diagnostics.Stopwatch();
            foreach (var w in wheels)
            {
                if (w.W == null || !w.W.enabled || w.T == null) continue;
                n++;
                children += w.T.GetComponentsInChildren<Transform>().Length - 1;
                childColliders += w.T.GetComponentsInChildren<Collider>().Length;
                Vector3 v = default; Quaternion q = default;
                sw.Restart();
                for (int i = 0; i < reps; i++) w.W.GetWorldPose(out v, out q);
                sw.Stop(); tPose += sw.ElapsedTicks;
                Vector3 p0 = w.T.position; Quaternion r0 = w.T.rotation;
                sw.Restart();
                for (int i = 0; i < reps; i++) { w.T.position = p0; w.T.rotation = r0; }
                sw.Stop(); tWrite += sw.ElapsedTicks;
            }
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency / System.Math.Max(1, n * reps);
            return "{\"wheels\":" + n + ",\"reps\":" + reps + ",\"poseMsPerCall\":" + (tPose * f).ToString("F5") +
                   ",\"writeMsPerCall\":" + (tWrite * f).ToString("F5") + ",\"avgChildren\":" + (n > 0 ? children / n : 0) +
                   ",\"avgChildColliders\":" + (n > 0 ? childColliders / n : 0) + "}";
        }

        /// Experiment: put every car body (root rigidbody of a carscript) to sleep once. Returns how many were put to sleep.
        public static string SleepCars()
        {
            int n = 0;
            foreach (var car in Object.FindObjectsOfType<carscript>())
            {
                var rb = car.GetComponentInParent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue;
                rb.Sleep();
                n++;
            }
            return "{\"sleptCars\":" + n + "}";
        }

        /// How many car bodies are currently awake / asleep.
        public static string CarSleepState()
        {
            int awake = 0, asleep = 0;
            foreach (var car in Object.FindObjectsOfType<carscript>())
            {
                var rb = car.GetComponentInParent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue;
                if (rb.IsSleeping()) asleep++; else awake++;
            }
            return "{\"carsAwake\":" + awake + ",\"carsAsleep\":" + asleep + "}";
        }

        /// Rendering inventory: what the scene asks the renderer to do (release builds have no UnityStats).
        public static string RenderStats()
        {
            var cams = Object.FindObjectsOfType<Camera>();
            int camsOn = 0; var camNames = new List<string>();
            foreach (var c in cams) if (c.enabled && c.gameObject.activeInHierarchy) { camsOn++; camNames.Add(c.name + (c.targetTexture != null ? "(RT)" : "")); }
            var probes = Object.FindObjectsOfType<ReflectionProbe>();
            int probesRealtime = 0, probesEveryFrame = 0, probesOn = 0;
            foreach (var p in probes)
            {
                if (!p.enabled || !p.gameObject.activeInHierarchy) continue;
                probesOn++;
                if (p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime) probesRealtime++;
                if (p.refreshMode == UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame) probesEveryFrame++;
            }
            var lights = Object.FindObjectsOfType<Light>();
            int lightsOn = 0, lightsShadow = 0;
            foreach (var l in lights) if (l.enabled && l.gameObject.activeInHierarchy) { lightsOn++; if (l.shadows != LightShadows.None) lightsShadow++; }
            var rends = Object.FindObjectsOfType<Renderer>();
            int visible = 0, shadowCast = 0, visibleShadow = 0;
            foreach (var r in rends)
            {
                bool vis = r.isVisible;
                bool sc = r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
                if (vis) visible++;
                if (sc) shadowCast++;
                if (vis && sc) visibleShadow++;
            }
            int lodGroups = Object.FindObjectsOfType<LODGroup>().Length;
            return "{\"cameras\":" + camsOn + ",\"cameraNames\":" + Json.Str(string.Join(",", camNames)) +
                   ",\"reflectionProbes\":" + probesOn + ",\"probesRealtime\":" + probesRealtime + ",\"probesEveryFrame\":" + probesEveryFrame +
                   ",\"lights\":" + lightsOn + ",\"lightsWithShadows\":" + lightsShadow +
                   ",\"renderers\":" + rends.Length + ",\"visible\":" + visible + ",\"shadowCasters\":" + shadowCast + ",\"visibleShadowCasters\":" + visibleShadow +
                   ",\"lodGroups\":" + lodGroups +
                   ",\"shadowDistance\":" + QualitySettings.shadowDistance + ",\"shadowCascades\":" + QualitySettings.shadowCascades +
                   ",\"shadows\":" + Json.Str(QualitySettings.shadows.ToString()) + ",\"lodBias\":" + QualitySettings.lodBias +
                   ",\"pixelLights\":" + QualitySettings.pixelLightCount + ",\"realtimeReflectionProbes\":" + (QualitySettings.realtimeReflectionProbes ? "true" : "false") + "}";
        }

        /// Every camera: owner, enabled, render texture, culling mask bits, far clip, depth, whether it renders to screen.
        public static string Cameras()
        {
            var sb = new StringBuilder("{\"cameras\":[");
            bool first = true;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c.gameObject.scene.name == null) continue; // prefab assets
                if (!first) sb.Append(',');
                first = false;
                int bits = 0; for (int m = c.cullingMask; m != 0; m &= m - 1) bits++;
                var rt = c.targetTexture;
                sb.Append("{\"name\":").Append(Json.Str(c.name)).Append(",\"path\":").Append(Json.Str(Path(c.transform)))
                  .Append(",\"enabled\":").Append(c.enabled && c.gameObject.activeInHierarchy ? "true" : "false")
                  .Append(",\"rt\":").Append(Json.Str(rt != null ? rt.width + "x" + rt.height : ""))
                  .Append(",\"layers\":").Append(bits).Append(",\"far\":").Append(c.farClipPlane)
                  .Append(",\"depth\":").Append(c.depth).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        /// Experiment: enable/disable cameras by name (all cameras with that name).
        public static string SetCamera(string name, bool on)
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                if (c.gameObject.scene.name != null && c.name == name) { c.enabled = on; n++; }
            return "{\"changed\":" + n + "}";
        }

        /// Floating origin: the world's offset (mainscript.visszarakva) and the player's Unity-space position.
        private static string Origin()
        {
            if (mainscript.s == null) return "";
            var o = mainscript.s.visszarakva; var p = mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
            return ",\"origin\":[" + o.x.ToString("F0") + "," + o.y.ToString("F0") + "," + o.z.ToString("F0") + "],\"playerUnity\":[" + p.x.ToString("F0") + "," + p.y.ToString("F0") + "," + p.z.ToString("F0") + "]";
        }

        public static string Stats()
        {
            var bodies = Object.FindObjectsOfType<Rigidbody>();
            int awake = 0, kinematic = 0, continuous = 0, interp = 0;
            foreach (var b in bodies)
            {
                if (!b.IsSleeping()) awake++;
                if (b.isKinematic) kinematic++;
                if (b.collisionDetectionMode != CollisionDetectionMode.Discrete) continuous++;
                if (b.interpolation != RigidbodyInterpolation.None) interp++;
            }
            var cols = Object.FindObjectsOfType<Collider>();
            int mesh = 0, convex = 0, box = 0, sphere = 0, capsule = 0, wheel = 0, trig = 0;
            foreach (var c in cols)
            {
                if (c.isTrigger) trig++;
                switch (c)
                {
                    case MeshCollider mc: mesh++; if (mc.convex) convex++; break;
                    case BoxCollider _: box++; break;
                    case SphereCollider _: sphere++; break;
                    case CapsuleCollider _: capsule++; break;
                    case WheelCollider _: wheel++; break;
                }
            }
            return "{\"rigidbodies\":" + bodies.Length + ",\"awake\":" + awake + ",\"kinematic\":" + kinematic +
                   ",\"continuousCD\":" + continuous + ",\"interpolated\":" + interp +
                   ",\"colliders\":" + cols.Length + ",\"triggers\":" + trig + ",\"mesh\":" + mesh + ",\"meshConvex\":" + convex +
                   ",\"box\":" + box + ",\"sphere\":" + sphere + ",\"capsule\":" + capsule + ",\"wheel\":" + wheel +
                   ",\"fixedDeltaTime\":" + Time.fixedDeltaTime + ",\"maxDeltaTime\":" + Time.maximumDeltaTime +
                   ",\"solverIterations\":" + Physics.defaultSolverIterations + ",\"solverVelocityIterations\":" + Physics.defaultSolverVelocityIterations +
                   ",\"sleepThreshold\":" + Physics.sleepThreshold + ",\"bounceThreshold\":" + Physics.bounceThreshold +
                   ",\"defaultContactOffset\":" + Physics.defaultContactOffset + ",\"autoSyncTransforms\":" + (Physics.autoSyncTransforms ? "true" : "false") +
                   ",\"simulationMode\":" + Json.Str(Physics.simulationMode.ToString()) + Origin() + "}";
        }
            /// bridge `followdump [n]`: what the game's arrayfollow scripts move (arrayfollow.Update copies each Target's world
        /// pose onto T — or T onto Target when inv — every frame; 55 ms/s on the laptop with 16 cars, ~76 µs a call).
        /// Per pair: names, path relative to the car root, colliders and bodies under the written transform, same parent,
        /// and whether the write changes anything (local pose already equal).
        public static string FollowDump(int n)
        {
            var sb = new StringBuilder("{\"arrayfollows\":" + Object.FindObjectsOfType<arrayfollow>().Length + ",\"sample\":[");
            bool first = true;
            foreach (var af in Object.FindObjectsOfType<arrayfollow>().Take(n))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"root\":").Append(Json.Str(af.transform.root.name)).Append(",\"on\":").Append(Json.Str(af.name))
                  .Append(",\"inv\":").Append(af.inv ? "true" : "false").Append(",\"pairs\":[");
                for (int i = 0; i < (af.T?.Length ?? 0); i++)
                {
                    if (i > 0) sb.Append(',');
                    var t = af.T[i]; var g = i < (af.Target?.Length ?? 0) ? af.Target[i] : null;
                    var written = af.inv ? g : t; var source = af.inv ? t : g;
                    if (written == null || source == null) { sb.Append("{\"null\":true}"); continue; }
                    int cols = written.GetComponentsInChildren<Collider>(true).Length, bodies = written.GetComponentsInChildren<Rigidbody>(true).Length;
                    float dp = (written.position - source.position).magnitude * 100f, dr = Quaternion.Angle(written.rotation, source.rotation);
                    sb.Append("{\"written\":").Append(Json.Str(Path(written))).Append(",\"from\":").Append(Json.Str(Path(source)))
                      .Append(",\"children\":").Append(written.GetComponentsInChildren<Transform>(true).Length)
                      .Append(",\"colliders\":").Append(cols).Append(",\"bodies\":").Append(bodies)
                      .Append(",\"sameParent\":").Append(written.parent == source.parent ? "true" : "false")
                      .Append(",\"offCm\":").Append(dp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"offDeg\":").Append(dr.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }
    }
}
