using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TLDRevamp.Net
{
    // Shared with the preloader. Intent is durable before any move; each same-volume rename is replayable.
    internal static class ModActivation
    {
        internal static bool Core(string id) => string.Equals(id, "tldrevamp.core", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "TLDRevamp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "TLDRevamp.Multiplayer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "TLDRevamp.Preloader", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "TLDLoader", StringComparison.OrdinalIgnoreCase) || id.StartsWith("bepinex", StringComparison.OrdinalIgnoreCase);
        internal static void Validate(string id)
        {
            if (!Allowed(id))
                throw new IOException("This mod cannot be managed here.");
        }
        internal static bool Allowed(string id) => id != null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]{0,63}\z") && !Core(id);
        internal static string Store(string dir) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dir))), "disabled-mods");
        internal static string Folder(string dir, string id) { Validate(id); return Path.Combine(Store(dir), id); }
        internal static void Safe(string path)
        {
            for (string p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Mod files or folders must not be links.");
        }
        internal static string Desired(string dir, string id)
        {
            Validate(id); string file = Path.Combine(Store(dir), id + ".state"); Safe(file);
            if (!File.Exists(file)) return "enabled";
            string value = File.ReadAllText(file).Trim();
            if (value != "enabled" && value != "disabled" && value != "removed") throw new IOException("Invalid mod state.");
            return value;
        }
        internal static List<string> Files(string dir, string id)
        {
            Validate(id); var result = new List<string>();
            foreach (string folder in new[] { dir, Folder(dir, id) })
            {
                Safe(folder); if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*.dll"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith(id + "-", StringComparison.Ordinal) || !ModFiles.SafeVersion(name.Substring(id.Length + 1))) continue;
                    Safe(file); result.Add(file);
                }
            }
            return result;
        }
        internal static IEnumerable<string> Ids(string dir)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string store = Store(dir); Safe(store);
            if (Directory.Exists(store))
            {
                foreach (string folder in Directory.GetDirectories(store)) ids.Add(Path.GetFileName(folder));
                foreach (string file in Directory.GetFiles(store, "*.state")) ids.Add(Path.GetFileNameWithoutExtension(file));
            }
            Safe(dir);
            if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                bool known = false; foreach (string id in ids) if (name.StartsWith(id + "-", StringComparison.Ordinal)) { known = true; break; }
                int split = name.LastIndexOf('-');
                if (!known && split > 0) ids.Add(name.Substring(0, split));
            }
            return ids;
        }
        internal static void Alias(string dir, string id, string alias)
        {
            Validate(id); Validate(alias);
            string file = Path.Combine(Store(dir), id + ".alias"); Safe(file);
            Directory.CreateDirectory(Store(dir));
            Write(file, alias);
        }
        internal static string Resolve(string dir, string id)
        {
            Validate(id);
            foreach (string candidate in Ids(dir))
            {
                if (Core(candidate)) continue;
                Validate(candidate); string file = Path.Combine(Store(dir), candidate + ".alias"); Safe(file);
                if (File.Exists(file) && File.ReadAllText(file) == id) return candidate;
            }
            return id;
        }
        internal static bool Pending(string dir, string id, bool? loaded = null)
        {
            string desired = Desired(dir, id);
            var files = Files(dir, id);
            foreach (string file in files)
                if (desired == "removed" || ModFiles.PathEq(Path.GetDirectoryName(file), dir) != (desired == "enabled")) return true;
            return loaded.HasValue && (files.Count > 0 || loaded.Value) && loaded.Value != (desired == "enabled");
        }
        internal static string Status(string dir, string id, bool? loaded = null)
        {
            string desired = Desired(dir, id);
            if (desired == "removed") return Pending(dir, id, loaded) ? "Removed at next start" : "Removed";
            if (Files(dir, id).Count == 0) return desired == "disabled" ? "Disabled (DLL folder or file missing)" : "Not installed";
            return Pending(dir, id, loaded) ? desired == "disabled" ? "Disabled (takes effect at next start)" : "Enabled at next start" : desired == "disabled" ? "Disabled" : "Enabled";
        }
        internal static void Queue(string dir, string id, string action)
        {
            Validate(id);
            if (action != "enabled" && action != "disabled" && action != "removed") throw new IOException("Invalid mod action.");
            if (Files(dir, id).Count == 0) throw new IOException("Managed mod DLL is missing.");
            string folder = Folder(dir, id); Safe(folder); Directory.CreateDirectory(folder);
            Write(Path.Combine(Store(dir), id + ".state"), action);
        }
        private static void Write(string state, string action)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(state), Guid.NewGuid().ToString("N") + ".part");
            using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(s)) { w.Write(action); w.Flush(); s.Flush(true); }
            Safe(state);
            if (File.Exists(state)) File.Replace(tmp, state, null); else File.Move(tmp, state);
        }
        internal static void Apply(string dir, string id, Action<string> moved = null)
        {
            string desired = Desired(dir, id);
            foreach (string source in Files(dir, id))
            {
                string folder = desired == "enabled" ? dir : Folder(dir, id); Safe(folder); Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, Path.GetFileName(source));
                if (desired == "removed")
                {
                    target += ".removed";
                    for (int i = 1; File.Exists(target); i++) target = Path.Combine(folder, Path.GetFileName(source)) + "." + i + ".removed";
                }
                if (ModFiles.PathEq(source, target)) continue;
                Safe(target); File.Move(source, target); moved?.Invoke(source);
            }
        }
    }
}
