using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TLDRevamp.Net
{
    /// File operations shared by the host transfer and browser; callers verify bytes before Install.
    internal static class ModFiles
    {
        internal static bool SafeVersion(string v) => v != null && Regex.IsMatch(v, @"\A[A-Za-z0-9_.-]{1,64}\z");
        internal static string Target(string dir, string id, string version) => Path.Combine(dir,
            string.Concat(id.Split(Path.GetInvalidFileNameChars())) + "-" + string.Concat(version.Split(Path.GetInvalidFileNameChars())) + ".dll");

        internal static bool PathEq(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        internal static bool Managed(string dir, string file) => !string.IsNullOrEmpty(file) &&
            PathEq(Path.GetDirectoryName(Path.GetFullPath(file)), dir) && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 &&
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0;
        internal static bool Managed(string dir, string file, string id) => Managed(dir, file) &&
            Path.GetFileNameWithoutExtension(file).StartsWith(id + "-", StringComparison.Ordinal) &&
            SafeVersion(Path.GetFileNameWithoutExtension(file).Substring(id.Length + 1));

        internal static List<string> KnownFiles(string dir, string id, IDictionary<string, string> approved)
        {
            var files = new List<string>();
            foreach (var line in approved.Values)
            {
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 2 || p[0] != id || !SafeVersion(p[1])) continue;
                var file = Target(dir, id, p[1]);
                if (Managed(dir, file) && !files.Contains(file)) files.Add(file);
            }
            return files;
        }

        internal static string Install(string dir, string id, string version, byte[] bytes, IEnumerable<string> oldFiles)
        {
            ModActivation.Validate(id);
            if (!SafeVersion(version)) throw new IOException("Invalid mod version.");
            var previous = new List<string>(oldFiles);
            var known = ModActivation.Files(dir, id);
            foreach (string file in previous)
                if (File.Exists(file) && !known.Exists(f => PathEq(f, file))) throw new IOException("Only this mod's managed DLLs can be replaced.");
            ModActivation.Safe(dir);
            string managedDir = dir;
            bool disabled = ModActivation.Desired(dir, id) == "disabled";
            if (disabled) dir = ModActivation.Folder(dir, id);
            Directory.CreateDirectory(dir);
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Managed mod folder is a link.");
            string target = Target(dir, id, version), tmp = target + ".part";
            // CreateNew refuses stale files and links. No unverified bytes ever reach this method.
            using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) s.Write(bytes, 0, bytes.Length);
            try
            {
                if (File.Exists(target)) SetAside(target, ".replaced");
                File.Move(tmp, target);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            ModActivation.Queue(managedDir, id, disabled ? "disabled" : "enabled");
            foreach (var file in previous)
            {
                if (PathEq(file, target) || !File.Exists(file)) continue;
                try { SetAside(file, ".replaced"); }
                catch (Exception e) { return "Installed, but the old copy (" + file + ") couldn't be set aside (" + e.Message + "): set it aside manually before you start the game again"; }
            }
            return null;
        }

        internal static void Remove(string dir, string file)
        {
            if (!Managed(dir, file)) throw new IOException("Only files installed in TLDRevampMods can be removed here.");
            SetAside(file, ".removed");
        }

        private static void SetAside(string file, string suffix)
        {
            string backup = file + suffix;
            for (int i = 1; File.Exists(backup); i++) backup = file + "." + i + suffix;
            File.Move(file, backup);
        }
    }
}
