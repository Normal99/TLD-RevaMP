using System;
using System.IO;
using System.Linq;
using System.Reflection;

/// The TLD Loader mod scanner (docs/MODDING-MULTIPLAYER.md §7) outside the game: reads a mod's DLL and writes
/// <mod>.md and <mod>.MpSkeleton.cs. The game's own DLLs resolve what the mod references:
///   TLD_DIR=<the game's folder> dotnet run --project tools/modscan -- <mod.dll> [--lib <more folders with DLLs it needs>]
///   [--out <folder for the report>]
/// (build TLD Revamp first: tools/build.sh, or dotnet build src/TLDRevamp -c Release)
///
/// Where the report goes: by default next to the scanned DLL, which is where the in-game scanner writes it
/// (BepInEx/TLDRevampModScan). --out sends it somewhere else. It never writes to the current working directory:
/// the output must not depend on where the tool happened to be invoked from, and a report must not land in the
/// repository root by accident.
static class Program
{
    static int Main(string[] args)
    {
        var dlls = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || args[i - 1] != "--lib") && (i == 0 || args[i - 1] != "--out")).ToList();
        var libs = args.Select((a, i) => a == "--lib" && i + 1 < args.Length ? args[i + 1] : null).Where(a => a != null).ToList();
        string outDir = args.Select((a, i) => a == "--out" && i + 1 < args.Length ? args[i + 1] : null).FirstOrDefault(a => a != null);
        string game = Environment.GetEnvironmentVariable("TLD_DIR");
        if (game != null)
            foreach (var d in new[] { "TheLongDrive_Data/Managed", "BepInEx/core", "BepInEx/plugins/TLDRevamp" }) libs.Add(Path.Combine(game, d));
        if (dlls.Count == 0) { Console.Error.WriteLine("usage: modscan <mod.dll> [--lib <folder with the game's DLLs>] [--out <folder for the report>]"); return 2; }
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in libs.Concat(dlls.Select(Path.GetDirectoryName)))
            {
                var p = Path.Combine(d ?? ".", n);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        foreach (var dll in dlls)
        {
            var r = TLDRevamp.LoaderScan.Scan(Path.GetFullPath(dll));
            string b = Path.GetFileNameWithoutExtension(dll);
            // the report goes next to the scanned DLL, or into --out: never into the current working directory
            string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(dll)) ?? ".";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string report = Path.Combine(dir, b + ".md");
            string skeleton = Path.Combine(dir, b + ".MpSkeleton.cs");
            File.WriteAllText(report, TLDRevamp.LoaderScan.Markdown(r));
            File.WriteAllText(skeleton, TLDRevamp.LoaderScan.Skeleton(r));
            Console.WriteLine($"{r.Name} {r.Version}: {r.Methods} methods, {r.Members} member sites ({r.UnresolvedMembers} unresolved), " +
                              $"{r.Findings.Count} findings, {r.TypeLoadErrors.Count} type-load errors -> {report}, {skeleton}" +
                              (r.Error != null ? " (" + r.Error + ")" : ""));
        }
        return 0;
    }
}
