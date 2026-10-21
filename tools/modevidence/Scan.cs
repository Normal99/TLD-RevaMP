using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TLDModEvidence
{
    /// <summary>
    /// The scanner side of the evidence: the real LoaderScan, run over a real DLL, and its report turned into a
    /// policy decision.
    ///
    /// The scanner is loaded through reflection, exactly as tools/modscan does, so the harness proves the
    /// production scanner and not a re-implementation of it. When the production assembly cannot be loaded
    /// (no game, no built plugin) the harness says so and skips the scanner-specific checks rather than
    /// pretending to have run them.
    /// </summary>
    public static class Scan
    {
        /// <summary>The production TLDRevamp.dll, when it has been built.</summary>
        public static string ProductionDll()
        {
            string root = FindRepoRoot();
            if (root == null) return "";
            string dll = Path.Combine(root, "src", "TLDRevamp", "bin", "Release", "netstandard2.1", "TLDRevamp.dll");
            return File.Exists(dll) ? dll : "";
        }

        /// <summary>The reference assemblies the scanner needs.</summary>
        public static string LibDir()
        {
            string root = FindRepoRoot();
            if (root == null) return "";
            string lib = Path.Combine(root, "lib");
            return Directory.Exists(lib) ? lib : "";
        }

        /// <summary>The repo root, found by walking up from this assembly.</summary>
        public static string FindRepoRoot()
        {
            // TLD_REVAMP_ROOT names the tree when the harness runs from a build output outside it
            // (the workers' verify area), so the walk-up below is not the only way to find it.
            string named = Environment.GetEnvironmentVariable("TLD_REVAMP_ROOT");
            if (!string.IsNullOrEmpty(named) &&
                File.Exists(Path.Combine(named, "src", "TLDRevamp", "TLDRevamp.csproj"))) return named;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "TLDRevamp")) &&
                    File.Exists(Path.Combine(dir.FullName, "src", "TLDRevamp", "TLDRevamp.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>The scanner's report, as the plain data the policy needs.</summary>
        public sealed class ScanResult
        {
            public bool Available;
            public string Reason = "";
            public string Name = "", Id = "", Version = "";
            public int Methods, Members, UnresolvedMembers, Findings;
            public List<string> FindingKinds = new List<string>();
            public List<string> Dependencies = new List<string>();
            public bool UsesApi;
            public string Error = "";
            public string Markdown = "";
        }

        /// <summary>Scan a DLL with the production scanner. Never runs the mod's code.</summary>
        public static ScanResult Run(string dll, string libDir, string workDir)
        {
            var result = new ScanResult();
            string production = ProductionDll();
            if (production.Length == 0)
            {
                result.Reason = "the production TLDRevamp.dll is not built";
                return result;
            }
            if (!File.Exists(dll)) { result.Reason = "the fixture DLL is not there"; return result; }
            Directory.CreateDirectory(workDir);
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(production);
                var loaderScan = asm.GetType("TLDRevamp.LoaderScan");
                if (loaderScan == null) { result.Reason = "TLDRevamp.LoaderScan is not in the production assembly"; return result; }

                // The same assembly resolution the modscan tool sets up: the game's DLLs resolve what the mod
                // references, so the scanner can read the mod's members.
                var libs = new List<string>();
                if (libDir.Length > 0) libs.Add(libDir);
                // On .NET (Core) an AssemblyResolve handler cannot set Resolved, so the handler loads the
                // assembly itself and returns it; the runtime then uses what the handler loaded.
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) => Resolve(s, e, libs);

                try
                {
                    var scan = loaderScan.GetMethod("Scan", new[] { typeof(string) });
                    if (scan == null) { result.Reason = "TLDRevamp.LoaderScan.Scan(string) is missing"; return result; }
                    object report = scan.Invoke(null, new object[] { Path.GetFullPath(dll) });
                    if (report == null) { result.Reason = "the scanner returned nothing"; return result; }

                    var t = report.GetType();
                    result.Available = true;
                    result.Name = (string)(t.GetField("Name").GetValue(report) ?? "");
                    result.Id = (string)(t.GetField("Id").GetValue(report) ?? "");
                    result.Version = (string)(t.GetField("Version").GetValue(report) ?? "");
                    result.Methods = (int)t.GetField("Methods").GetValue(report);
                    result.Members = (int)t.GetField("Members").GetValue(report);
                    result.UnresolvedMembers = (int)t.GetField("UnresolvedMembers").GetValue(report);
                    // UsesApi is a field on the report, not a property.
                    var usesApi = t.GetField("UsesApi");
                    result.UsesApi = usesApi != null && (bool)usesApi.GetValue(report);
                    var findings = (System.Collections.IEnumerable)t.GetField("Findings").GetValue(report);
                    foreach (var f in findings)
                    {
                        var ft = f.GetType();
                        // Kind is an enum, so its name is the readable form.
                        object kind = ft.GetField("Kind").GetValue(f);
                        result.FindingKinds.Add(kind?.ToString() ?? "");
                        result.Findings++;
                    }
                    var deps = (System.Collections.IEnumerable)t.GetField("Dependencies").GetValue(report);
                    foreach (var d in deps) result.Dependencies.Add(d?.ToString() ?? "");
                    var errField = t.GetField("Error");
                    if (errField != null) result.Error = (string)(errField.GetValue(report) ?? "");
                    var markdown = loaderScan.GetMethod("Markdown", new[] { t });
                    result.Markdown = (string)markdown.Invoke(null, new[] { report });
                }
                finally { AppDomain.CurrentDomain.AssemblyResolve -= null; }
            }
            catch (Exception e)
            {
                var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                result.Reason = inner.GetType().Name + ": " + inner.Message;
            }
            return result;
        }

        /// <summary>Resolves the game's assemblies while the scanner runs.</summary>
        static System.Reflection.Assembly Resolve(object sender, System.ResolveEventArgs e, List<string> libs)
        {
            string name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in libs)
            {
                string p = Path.Combine(dir ?? ".", name);
                if (File.Exists(p)) return System.Reflection.Assembly.LoadFrom(p);
            }
            return null;
        }
    }
}
