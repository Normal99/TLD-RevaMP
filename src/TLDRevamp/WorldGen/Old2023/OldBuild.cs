using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace TLDRevamp.WorldGen.Old2023
{
    /// The 2023 build's own code, loaded beside the beta's (docs/OLD2023-FROM-STEAM.md).
    ///
    /// At startup (Probe) the files are looked for in BepInEx/cache/TLDRevamp/old2023 and in the usual folders of a
    /// Steam console download, checked against the pinned SHA-256 and copied into the cache, so the player may delete
    /// the 321 MB download afterwards. Nothing is loaded yet: that costs ~0.2 s and only an old-terrain world needs it.
    ///
    /// Load (main thread, the first old-terrain world): a copy of the 2023 Assembly-CSharp is rewritten in memory with
    /// Mono.Cecil - the assembly becomes Assembly-CSharp-2023 and every namespace gets an Old2023 prefix, so neither the
    /// game nor another mod's type lookup can find an old type by accident - and loaded from bytes. References to
    /// Unity's own assemblies bind to the beta's; Unity.Collections/Unity.Mathematics (the beta has neither) come from
    /// the cache. RevaMP is never compiled against the old build: it calls it through the members resolved here.
    internal static class OldBuild
    {
        public enum State { NotChecked, Missing, WrongFiles, Found, Loaded, LoadFailed }

        public static State Status { get; private set; } = State.NotChecked;
        public static string Source = "", Error = "";
        public static double ProbeMs, LoadMs;
        public static Assembly Asm { get; private set; }

        /// The files are here and verified (loaded or not yet): an old-terrain world can be made.
        public static bool Available => Status == State.Found || Status == State.Loaded;

        public static string CacheDir => Path.Combine(Path.Combine(Paths.CachePath, "TLDRevamp"), "old2023");

        // ---------------------------------------------------------------- finding the files

        public static void Probe()
        {
            var t0 = DateTime.UtcNow;
            try
            {
                if (Verified(CacheDir)) { Status = State.Found; Source = "cache"; return; }
                bool windows = Path.DirectorySeparatorChar == '\\';
                var homes = new List<string>();
                if (windows && Directory.Exists("Z:/home"))   // Proton: the Linux side's home folders
                    foreach (var d in Directory.GetDirectories("Z:/home")) homes.Add("/home/" + Path.GetFileName(d));
                var roots = OldBuildFiles.SteamRoots(Paths.GameRootPath, Environment.GetEnvironmentVariable, windows, homes);
                bool wrong = false;
                foreach (var depot in OldBuildFiles.DepotDirs(roots))
                {
                    string managed = OldBuildFiles.ManagedDir(depot);
                    if (!File.Exists(Path.Combine(managed, OldBuildFiles.Names[0]))) continue;
                    if (!Verified(managed)) { wrong = true; Error = "the files in " + managed + " are not the v2023.05.02d build's"; continue; }
                    Directory.CreateDirectory(CacheDir);
                    foreach (var n in OldBuildFiles.Names) File.Copy(Path.Combine(managed, n), Path.Combine(CacheDir, n), true);
                    if (!Verified(CacheDir)) { Error = "the copy into " + CacheDir + " does not match"; continue; }
                    Status = State.Found;
                    Source = managed;
                    Plugin.Log.LogInfo("Old terrain: the 2023 game files found in " + managed + ", kept in " + CacheDir);
                    return;
                }
                Status = wrong ? State.WrongFiles : State.Missing;
                if (!wrong) Error = "not downloaded (" + OldBuildFiles.DownloadCommand + ")";
                Plugin.Log.LogInfo("Old terrain (2023) unavailable: " + Error);
            }
            catch (Exception e)
            {
                Status = State.Missing;
                Error = e.Message;
                Plugin.Log.LogWarning("Old terrain: looking for the 2023 game files failed: " + e.Message);
            }
            finally { ProbeMs = (DateTime.UtcNow - t0).TotalMilliseconds; }
        }

        private static bool Verified(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            using (var sha = SHA256.Create())
                for (int i = 0; i < OldBuildFiles.Names.Length; i++)
                {
                    string p = Path.Combine(dir, OldBuildFiles.Names[i]);
                    if (!File.Exists(p)) return false;
                    using (var f = File.OpenRead(p))
                        if (OldBuildFiles.Hex(sha.ComputeHash(f)) != OldBuildFiles.Sha256[i]) return false;
                }
            return true;
        }

        // ---------------------------------------------------------------- loading

        /// Load the old code (once). False when it can't be: the caller then makes no old-terrain world.
        public static bool EnsureLoaded()
        {
            if (Status == State.Loaded) return true;
            if (Status != State.Found) return false;
            var t0 = DateTime.UtcNow;
            try
            {
                if (!Verified(CacheDir)) throw new IOException("the cached 2023 files changed since startup");
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(CacheDir);
                resolver.AddSearchDirectory(Paths.ManagedPath);
                byte[] rewritten;
                using (var mod = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(CacheDir, OldBuildFiles.Names[0]))),
                           new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
                {
                    mod.Assembly.Name.Name = "Assembly-CSharp-2023";
                    mod.Name = "Assembly-CSharp-2023.dll";
                    foreach (var t in mod.Types) t.Namespace = t.Namespace.Length == 0 ? "Old2023" : "Old2023." + t.Namespace;
                    var ms = new MemoryStream();
                    mod.Write(ms);
                    rewritten = ms.ToArray();
                }
                Asm = Assembly.Load(rewritten);
                Bind();
                Status = State.Loaded;
                LoadMs = (DateTime.UtcNow - t0).TotalMilliseconds;
                Plugin.Log.LogInfo($"Old terrain: the 2023 game code loaded as {Asm.GetName().Name} in {LoadMs:F0} ms");
                return true;
            }
            catch (Exception e)
            {
                Status = State.LoadFailed;
                Error = e.GetType().Name + ": " + e.Message;
                LoadMs = (DateTime.UtcNow - t0).TotalMilliseconds;
                Plugin.Log.LogError("Old terrain: the 2023 game code could not be loaded: " + e);
                return false;
            }
        }

        /// Unity.Collections and Unity.Mathematics for the old assembly (by name only: the beta has neither).
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            for (int i = 1; i < OldBuildFiles.Names.Length; i++)
            {
                if (OldBuildFiles.Names[i] != name + ".dll") continue;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == name) return a;
                return Assembly.Load(File.ReadAllBytes(Path.Combine(CacheDir, OldBuildFiles.Names[i])));
            }
            return null;
        }

        // ---------------------------------------------------------------- the old members RevaMP calls

        public static Type Job, Generator, Manager, Settings, Coord2, Road, RoadNoise, RoadClass, RoadCoord, Buildings,
            BuildingGroup, BuildingType, Side, BuildingInMemory, Main, Menu, Temporary, DrawDistance, BuildingEnum;

        private static Func<object, Vector2, float> _randomize;
        private static Func<object, Vector2, float, Color> _vertexColor;
        private static MethodInfo _calcOneRoad, _roadStart, _buildingsStart;

        private static void Bind()
        {
            Type T(string n) => Asm.GetType("Old2023." + n, true);
            Job = T("TerrainGenerationJob");
            Generator = T("TerrainGenerator");
            Manager = T("TerrainManager");
            Settings = T("TerrainGenerationSettings");
            Coord2 = T("visszarako");
            Road = T("roadGenScript");
            RoadNoise = T("roadGenScript+noise");
            RoadClass = T("roadclass");
            RoadCoord = T("coord");
            Buildings = T("roadBuildingScript");
            BuildingGroup = T("roadBuildingScript+buildingGroup");
            BuildingType = T("roadBuildingScript+buildingType");
            Side = T("roadBuildingScript+randomside");
            BuildingInMemory = T("roadBuildingScript+buildinginmemory");
            Main = T("mainscript");
            Menu = T("menuhandler");
            Temporary = T("temporaryTurnOffGeneration");
            DrawDistance = T("settingsscript+DrawDistanceSetting");
            BuildingEnum = T("itemdatabase+b");

            // TerrainGenerationJob.randomize(c, edgeOfTheWorld: false, alignToRoad: false, alignToBuilding: false) and
            // VertexColor(c, h): instance methods of a struct, called on a boxed job of the calling thread
            var randomize = Job.GetMethod("randomize", new[] { typeof(Vector2), typeof(bool), typeof(bool), typeof(bool) });
            _randomize = (Func<object, Vector2, float>)StructCall("randomize", randomize, typeof(Func<object, Vector2, float>),
                typeof(float), new[] { typeof(object), typeof(Vector2) }, il => { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldc_I4_0); });
            var color = Job.GetMethod("VertexColor", new[] { typeof(Vector2), typeof(float) });
            _vertexColor = (Func<object, Vector2, float, Color>)StructCall("VertexColor", color, typeof(Func<object, Vector2, float, Color>),
                typeof(Color), new[] { typeof(object), typeof(Vector2), typeof(float) }, null);
            _calcOneRoad = Road.GetMethod("CalcOneRoad", Type.EmptyTypes);
            _roadStart = Road.GetMethod("FStart", Type.EmptyTypes);
            _buildingsStart = Buildings.GetMethod("FStart", Type.EmptyTypes);

            // The -z road and its buildings are computed on the terrain turned by 180 degrees (OldScene.Mirror)
            var height = Generator.GetMethod("GetTerrainHeightWorld", new[] { typeof(Vector2), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
            new Harmony("tldrevamp.old2023").Patch(height, prefix: new HarmonyMethod(typeof(OldBuild), nameof(MirrorPrefix)));
        }

        /// (object box, args) => ((Job)box).method(args, extra), so a hot call costs no reflection.
        private static Delegate StructCall(string name, MethodInfo m, Type delegateType, Type ret, Type[] args, Action<ILGenerator> extra)
        {
            if (m == null) throw new MissingMethodException(Job.FullName, name);
            var dm = new DynamicMethod("Old2023." + name, ret, args, typeof(OldBuild).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Unbox, Job);
            for (short i = 1; i < args.Length; i++) il.Emit(OpCodes.Ldarg, i);
            extra?.Invoke(il);
            il.Emit(OpCodes.Call, m);
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(delegateType);
        }

        private static void MirrorPrefix(ref Vector2 pv1)
        {
            if (OldScene.Mirror) pv1 = -pv1;
        }

        /// A TerrainGenerationJob for this seed, boxed (one per thread: the job writes its own scratch fields).
        public static object NewJob(int seed)
        {
            object job = Activator.CreateInstance(Job);
            Set(job, "seed", seed);
            return job;
        }

        public static float Randomize(object job, Vector2 c) => _randomize(job, c);
        public static Color VertexColor(object job, Vector2 c, float h) => _vertexColor(job, c, h);
        public static bool CalcOneRoad(object road) => (bool)_calcOneRoad.Invoke(road, null);
        public static void RoadStart(object road) => _roadStart.Invoke(road, null);
        public static void BuildingsStart(object buildings) => _buildingsStart.Invoke(buildings, null);

        // ---------------------------------------------------------------- fields

        private static readonly Dictionary<(Type, string), FieldInfo> Fields = new Dictionary<(Type, string), FieldInfo>();

        public static FieldInfo F(Type t, string name)
        {
            lock (Fields)
            {
                if (Fields.TryGetValue((t, name), out var f)) return f;
                f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(t.FullName, name);
                Fields[(t, name)] = f;
                return f;
            }
        }

        public static void Set(object o, string name, object value) => F(o.GetType(), name).SetValue(o, value);
        public static object Get(object o, string name) => F(o.GetType(), name).GetValue(o);
        public static void SetStatic(Type t, string name, object value) => F(t, name).SetValue(null, value);

        /// An old object with no constructor run and no Unity object behind it (MonoBehaviours too).
        public static object StandIn(Type t) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);

        public static string Stats() =>
            "{\"status\":" + Json.Str(Status.ToString()) + ",\"source\":" + Json.Str(Source) + ",\"reason\":" + Json.Str(Error) +
            ",\"probeMs\":" + ProbeMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"loadMs\":" + LoadMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "}";
    }
}
