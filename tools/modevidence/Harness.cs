using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TLDRevamp.Net;

namespace TLDModEvidence
{
    /// <summary>One guarded test copy: its own folder, its own prefix, its own port.</summary>
    public sealed class TestCopy
    {
        public string Name = "";
        public string Root = "";
        public string GameDir = "";
        public string Prefix = "";
        public int Port;
        public string Branch = "";
        public string Commit = "";
        public bool Dirty;

        public string ManagedDir => Path.Combine(GameDir, "BepInEx", "plugins", "TLDRevampMods");
        public string StoreDir => Path.Combine(GameDir, "BepInEx", "disabled-mods");
        public string BepInExDir => Path.Combine(GameDir, "BepInEx");
        /// <summary>The plugin tree BepInEx scans at start (the chainloader walks it recursively).</summary>
        public string PluginsDir => Path.Combine(GameDir, "BepInEx", "plugins");
        /// <summary>The BepInEx cache folder (the assembly cache lives here).</summary>
        public string CacheDir => Path.Combine(GameDir, "BepInEx", "cache");
    }

    /// <summary>One mod, as the harness tracks it in a copy.</summary>
    public sealed class TrackedMod
    {
        public string Id = "";
        public string Version = "";
        public byte[] Bytes = Array.Empty<byte>();
        /// <summary>The assets the mod ships, by path, with their content hash.</summary>
        public Dictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The offline proof of the mod lifecycle. It drives the *real* production functions
    /// (ModActivation, ModFiles) over a real folder tree, so what it proves is the production behaviour and
    /// not a re-implementation of it.
    ///
    /// A "restart" here is what the game's preloader does at start: it applies the queued intent by moving the
    /// DLLs. The harness calls the same function the preloader calls.
    /// </summary>
    public sealed class Harness
    {
        public readonly List<string> Steps = new List<string>();
        public readonly List<string> Failures = new List<string>();

        /// <summary>Where the prefix roots live. Every copy gets its own prefix under here.</summary>
        public string PrefixRoot = "";

        public TestCopy NewCopy(string name, string branch, string commit, bool dirty, int port)
        {
            var copy = new TestCopy
            {
                Name = name,
                Root = Path.Combine(PrefixRoot, name),
                Prefix = Path.Combine(PrefixRoot, name, "compat"),
                Branch = branch,
                Commit = commit,
                Dirty = dirty,
                Port = port,
            };
            copy.GameDir = Path.Combine(copy.Root, "game");
            Directory.CreateDirectory(copy.ManagedDir);
            Directory.CreateDirectory(copy.StoreDir);
            Directory.CreateDirectory(copy.Prefix);
            // A copy keeps its own saves and settings; a copy that shares them is refused.
            Directory.CreateDirectory(Path.Combine(copy.Root, "UserData"));
            File.WriteAllText(Path.Combine(copy.Root, "copy.json"),
                "{\"Name\":\"" + copy.Name + "\",\"Kind\":\"Test\",\"Compat\":\"" + copy.Prefix.Replace("\\", "/") +
                "\",\"SourceRef\":\"" + branch + "\",\"Bridge\":" + (port > 0 ? "true" : "false") + "}\n");
            return copy;
        }

        /// <summary>Install a mod into a copy, exactly as the browser does.</summary>
        public void Install(TestCopy copy, TrackedMod mod)
        {
            ModFiles.Install(copy.ManagedDir, mod.Id, mod.Version, mod.Bytes, ModActivation.Files(copy.ManagedDir, mod.Id));
        }

        /// <summary>Queue an intent, exactly as the browser does.</summary>
        public void Queue(TestCopy copy, string id, string action)
        {
            ModActivation.Queue(copy.ManagedDir, id, action);
        }

        /// <summary>
        /// Restart the client: apply the queued intent. This is the preloader step
        /// (TLDRevamp.Preloader/ModActions.cs calls ModActivation.Apply).
        /// </summary>
        public void Restart(TestCopy copy)
        {
            foreach (string id in ModActivation.Ids(copy.ManagedDir).ToList())
            {
                if (ModActivation.Desired(copy.ManagedDir, id) == "removed" &&
                    ModActivation.Files(copy.ManagedDir, id).Count == 0) continue;
                ModActivation.Apply(copy.ManagedDir, id);
            }
        }

        /// <summary>Every file under the copy that belongs to a mod id, with its hash. This is the leak detector.</summary>
        public Dictionary<string, string> AssetsOf(TestCopy copy)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string root in new[] { copy.GameDir, copy.Prefix, copy.Root })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace("\\", "/");
                    if (relative.StartsWith("game/", StringComparison.Ordinal)) relative = relative.Substring(5);
                    found[relative] = Hash(file);
                }
            }
            return found;
        }

        /// <summary>Every DLL the game would load for a mod: the managed folder only.</summary>
        public List<string> LoadedDlls(TestCopy copy, string id)
        {
            if (!Directory.Exists(copy.ManagedDir)) return new List<string>();
            return Directory.GetFiles(copy.ManagedDir, "*.dll")
                .Where(f => Path.GetFileName(f).StartsWith(id + "-", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>Every DLL the game would load in the copy, whatever the mod.</summary>
        public List<string> AllLoadedDlls(TestCopy copy)
        {
            if (!Directory.Exists(copy.ManagedDir)) return new List<string>();
            return Directory.GetFiles(copy.ManagedDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>The bytes of a file, or null when it is not there.</summary>
        public static byte[] Read(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static string Hash(string path)
        {
            try
            {
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        public static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes ?? Array.Empty<byte>())).ToLowerInvariant();
        }

        /// <summary>Record a check.</summary>
        public bool Check(string name, bool ok, string detail = "")
        {
            string line = (ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " (" + detail + ")" : "");
            Steps.Add(line);
            if (!ok) Failures.Add(name);
            Console.WriteLine(line);
            return ok;
        }
    }
}
