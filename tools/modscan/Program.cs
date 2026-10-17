using System;
using System.IO;
using System.Linq;
using System.Reflection;

/// The TLD Loader mod scanner (docs/MODDING-MULTIPLAYER.md §7) outside the game: reads a mod's DLL, writes <mod>.md and
/// <mod>.MpSkeleton.cs into the current folder. The game's own DLLs resolve what the mod references:
///   TLD_DIR=<the game's folder> dotnet run --project tools/modscan -- <mod.dll> [--lib <more folders with DLLs it needs>]
/// (build TLD Revamp first: tools/build.sh, or dotnet build src/TLDRevamp -c Release)
static class Program
{
    static int Main(string[] args)
    {
        var dlls = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || args[i - 1] != "--lib")).ToList();
        var libs = args.Select((a, i) => a == "--lib" && i + 1 < args.Length ? args[i + 1] : null).Where(a => a != null).ToList();
        string game = Environment.GetEnvironmentVariable("TLD_DIR");
        if (game != null)
            foreach (var d in new[] { "TheLongDrive_Data/Managed", "BepInEx/core", "BepInEx/plugins/TLDRevamp" }) libs.Add(Path.Combine(game, d));
        if (dlls.Count == 0) { Console.Error.WriteLine("usage: modscan <mod.dll> [--lib <folder with the game's DLLs>]"); return 2; }
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
            File.WriteAllText(b + ".md", TLDRevamp.LoaderScan.Markdown(r));
            File.WriteAllText(b + ".MpSkeleton.cs", TLDRevamp.LoaderScan.Skeleton(r));
            Console.WriteLine($"{r.Name} {r.Version}: {r.Methods} methods, {r.Findings.Count} findings -> {b}.md, {b}.MpSkeleton.cs" + (r.Error != null ? " (" + r.Error + ")" : ""));
        }
        return 0;
    }
}
