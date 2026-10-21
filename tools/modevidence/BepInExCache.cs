using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TLDModEvidence
{
    /// <summary>
    /// The real BepInEx assembly cache, driven offline.
    ///
    /// The lifecycle group's "no file of a disabled mod survives" is only half of the rule: BepInEx also keeps
    /// a *cache entry* for every assembly it scanned, in <c>BepInEx/cache/&lt;name&gt;_typeloader.dat</c>. That
    /// file is a real artifact of a running client, and a stale entry for a disabled mod is exactly what the
    /// issue's "no cache entry" forbids.
    ///
    /// This drives the production loader itself — <c>BepInEx.Bootstrap.TypeLoader.FindPluginTypes</c> and
    /// <c>LoadAssemblyCache</c>, from the BepInEx 5.4.23.5 assembly this tree ships in <c>lib/</c> — with the
    /// real <c>Chainloader.ToPluginInfo</c> and <c>HasBepinPlugins</c> filters. Nothing here is a model of
    /// BepInEx: it is BepInEx, pointed at a throwaway copy.
    /// </summary>
    public static class BepInExCache
    {
        /// <summary>What one cache inspection found.</summary>
        public sealed class CacheState
        {
            public bool Available;
            public string Reason = "";
            /// <summary>The cache file's path.</summary>
            public string CacheFile = "";
            /// <summary>Every assembly path the cache holds an entry for.</summary>
            public List<string> Entries = new List<string>();
            /// <summary>Whether the cache file's bytes name the given mod.</summary>
            public bool FileNames(string id) => IdOffset(id) >= 0;
            /// <summary>Where in the cache file's bytes the mod's id appears, or -1 when it does not.</summary>
            public int IdOffset(string id)
            {
                if (string.IsNullOrEmpty(CacheFile) || !File.Exists(CacheFile) || string.IsNullOrEmpty(id)) return -1;
                string text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(CacheFile));
                return text.IndexOf(id, StringComparison.OrdinalIgnoreCase);
            }
            /// <summary>The cache file's length in bytes.</summary>
            public long FileLength => !string.IsNullOrEmpty(CacheFile) && File.Exists(CacheFile) ? new FileInfo(CacheFile).Length : -1;
            /// <summary>
            /// The number of records the loader will read from the cache file: BepInEx writes it as the
            /// first 32-bit little-endian integer, and LoadAssemblyCache reads exactly that many records.
            /// </summary>
            public int OnDiskRecordCount
            {
                get
                {
                    if (string.IsNullOrEmpty(CacheFile) || !File.Exists(CacheFile)) return -1;
                    byte[] bytes = File.ReadAllBytes(CacheFile);
                    if (bytes.Length < 4) return -1;
                    return BitConverter.ToInt32(bytes, 0);
                }
            }
        }

        /// <summary>The pinned BepInEx and Mono.Cecil this tree ships, from lib/ (copied by tools/libs.sh).</summary>
        public static string LibDir()
        {
            string root = Scan.FindRepoRoot();
            if (root == null) return "";
            string lib = Path.Combine(root, "lib");
            return File.Exists(Path.Combine(lib, "BepInEx.dll")) ? lib : "";
        }

        /// <summary>
        /// The game's own managed assemblies (netstandard, mscorlib, UnityEngine, Assembly-CSharp). BepInEx's
        /// resolver needs them to read a mod's metadata; on a Mono host they are in the game's Managed folder.
        /// The harness never launches the game: it only reads these files, and only when the owner has them.
        /// </summary>
        public static string GameManagedDir()
        {
            foreach (string candidate in new[]
            {
                // The owner's own install (the only one this machine has).
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "Steam", "steamapps", "common", "The Long Drive", "TheLongDrive_Data", "Managed"),
            })
            {
                if (File.Exists(Path.Combine(candidate, "netstandard.dll"))) return candidate;
            }
            return "";
        }

        /// <summary>
        /// Point BepInEx at <paramref name="bepInExDir"/> and scan <paramref name="pluginPath"/> for real.
        /// Returns what the cache holds afterwards.
        /// </summary>
        /// <param name="bepInExDir">The copy's BepInEx root; its cache/config folders are created here.</param>
        /// <param name="pluginPath">The plugin tree the chainloader walks.</param>
        /// <param name="cacheName">The cache's name; the file is &lt;name&gt;_typeloader.dat.</param>
        public static CacheState ScanAndRead(string bepInExDir, string pluginPath, string cacheName = "chainloader")
        {
            var state = new CacheState();
            string lib = LibDir();
            if (lib.Length == 0) { state.Reason = "lib/BepInEx.dll is not here (tools/libs.sh copies it from the game)"; return state; }

            string managed = GameManagedDir();
            if (managed.Length == 0)
            {
                state.Reason = "the game's managed assemblies are not here (the resolver cannot read a mod's metadata without them)";
                return state;
            }

            string cacheDir = Path.Combine(bepInExDir, "cache");
            string configDir = Path.Combine(bepInExDir, "config");
            string coreDir = Path.Combine(bepInExDir, "core");
            string patcherDir = Path.Combine(bepInExDir, "patchers");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(coreDir);
            Directory.CreateDirectory(patcherDir);
            state.CacheFile = Path.Combine(cacheDir, cacheName + "_typeloader.dat");

            // A real install has BepInEx's own assemblies in BepInEx/core, next to (not inside) the
            // plugin folder, and the resolver searches there. Copy them in once: after the first scan
            // Cecil's resolver holds the file it resolved open, so copying it again would fail.
            foreach (string name in new[] { "BepInEx.dll", "Mono.Cecil.dll", "0Harmony.dll" })
            {
                string from = Path.Combine(lib, name);
                string to = Path.Combine(coreDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to, true);
            }

            // One resolution context: Mono.Cecil must be the very instance BepInEx uses, or the chainloader's
            // delegates cannot be bound to it.
            var handler = new ResolveHandler(lib);
            AppDomain.CurrentDomain.AssemblyResolve += handler.Resolve;
            try
            {
                Assembly bep = Assembly.LoadFrom(Path.Combine(lib, "BepInEx.dll"));
                Assembly cecil = Assembly.LoadFrom(Path.Combine(lib, "Mono.Cecil.dll"));

                // The paths BepInEx's own bootstrap sets. Without them the resolver has nothing to search.
                SetPath(bep, "BepInExRootPath", bepInExDir);
                SetPath(bep, "CachePath", cacheDir);
                SetPath(bep, "ConfigPath", configDir);
                SetPath(bep, "BepInExConfigPath", Path.Combine(configDir, "BepInEx.cfg"));
                SetPath(bep, "PluginPath", pluginPath);
                SetPath(bep, "PatcherPluginPath", patcherDir);
                SetPath(bep, "BepInExAssemblyDirectory", coreDir);
                SetPath(bep, "BepInExAssemblyPath", Path.Combine(coreDir, "BepInEx.dll"));
                SetPath(bep, "ManagedPath", managed);
                SetPath(bep, "DllSearchPaths", new[] { managed, coreDir });

                var typeLoader = bep.GetType("BepInEx.Bootstrap.TypeLoader");
                if (typeLoader == null) { state.Reason = "BepInEx.Bootstrap.TypeLoader is missing"; return state; }
                var pluginInfo = bep.GetType("BepInEx.PluginInfo");
                var find = typeLoader.GetMethods().FirstOrDefault(m => m.Name == "FindPluginTypes");
                var load = typeLoader.GetMethods().FirstOrDefault(m => m.Name == "LoadAssemblyCache");
                if (find == null || load == null) { state.Reason = "the type loader has no FindPluginTypes/LoadAssemblyCache"; return state; }

                var chainloader = bep.GetType("BepInEx.Bootstrap.Chainloader");
                var toInfo = chainloader.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name == "ToPluginInfo");
                var hasBepin = chainloader.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name == "HasBepinPlugins");
                if (toInfo == null || hasBepin == null) { state.Reason = "the chainloader has no ToPluginInfo/HasBepinPlugins"; return state; }

                var typeDefinition = cecil.GetType("Mono.Cecil.TypeDefinition");
                var assemblyDefinition = cecil.GetType("Mono.Cecil.AssemblyDefinition");
                var selector = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeDefinition, pluginInfo), toInfo);
                var filter = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(assemblyDefinition, typeof(bool)), hasBepin);

                // Cecil's own search directories. This is the load-bearing step: BepInEx's resolver
                // (TypeLoader.Resolver, a DefaultAssemblyResolver) is what actually reads a mod's metadata,
                // and it only looks in ITS search directories. On the game's Mono host a missing reference
                // falls through to the Mono GAC and is then found through BepInEx's DllSearchPaths; on .NET,
                // Cecil's GetAssemblyInNetGac throws instead, the reference never resolves, and the type
                // loader silently SKIPS the file - which is why an unprepared harness sees an empty cache
                // rather than an error. Adding the game's Managed folder and BepInEx's core folder to the
                // resolver's search directories is what a Mono host gives BepInEx for free.
                var resolver = typeLoader.GetField("Resolver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                   ?.GetValue(null);
                var addSearch = resolver?.GetType().GetMethod("AddSearchDirectory", new[] { typeof(string) });
                if (resolver == null || addSearch == null)
                {
                    state.Reason = "the type loader has no Cecil resolver to add search directories to";
                    return state;
                }
                addSearch.Invoke(resolver, new object[] { managed });
                addSearch.Invoke(resolver, new object[] { coreDir });

                // The real scan: exactly what the chainloader does at start, over the copy's plugin tree.
                find.MakeGenericMethod(pluginInfo).Invoke(null, new object[] { pluginPath, selector, filter, cacheName });

                var cache = load.MakeGenericMethod(pluginInfo).Invoke(null, new object[] { cacheName }) as IDictionary;
                state.Available = true;
                if (cache != null)
                    foreach (var key in cache.Keys) state.Entries.Add(key?.ToString() ?? "");
                state.Entries.Sort(StringComparer.Ordinal);
                return state;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                state.Reason = inner.GetType().Name + ": " + inner.Message;
                return state;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= handler.Resolve;
            }
        }

        /// <summary>Point one of BepInEx's path properties at a folder (BepInEx sets them from its own bootstrap).</summary>
        static void SetPath(Assembly bep, string name, object value)
        {
            var field = bep.GetType("BepInEx.Paths").GetField("<" + name + ">k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) return;
            var type = field.FieldType;
            if (type.IsArray)
            {
                var element = type.GetElementType();
                var array = Array.CreateInstance(element, 1);
                array.SetValue(((string[])value)[0], 0);
                field.SetValue(null, array);
            }
            else field.SetValue(null, value);
        }

        /// <summary>Resolves the game's and BepInEx's own assemblies out of lib/.</summary>
        private sealed class ResolveHandler
        {
            readonly string _lib;
            public ResolveHandler(string lib) { _lib = lib; }
            public Assembly Resolve(object sender, ResolveEventArgs args)
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string path = Path.Combine(_lib, name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
        }
    }
}
