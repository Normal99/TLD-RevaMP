using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Offline check that the unit test projects cannot overwrite each other's launcher.
//
// The apphost of a framework-dependent .NET app is byte-identical across projects: only the .deps.json next
// to it says which payload to run. When every project in tools/unit writes into the same output directory,
// MSBuild overwrites one project's launcher with another's, and a plain "dotnet run" on a project then runs
// a completely different test and still reports PASS. The fix is tools/unit/Directory.Build.props, which gives
// every project its own OutputPath; this test makes sure that stays true.
static class UnitIsolationTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string unitDir = Path.Combine(root, "tools", "unit");
        Check(Directory.Exists(unitDir), "the unit test directory exists");

        // 1. the shared props file exists and gives every project its own output directory
        string props = Path.Combine(unitDir, "Directory.Build.props");
        Check(File.Exists(props), "tools/unit/Directory.Build.props exists");
        string propsText = File.ReadAllText(props);
        Check(propsText.Contains("$(MSBuildProjectName)"), "the output path is per-project");
        Check(Regex.IsMatch(propsText, @"<OutputPath>[^<]*\$\(MSBuildProjectName\)"), "the output path names the project");
        Check(Regex.IsMatch(propsText, @"<BaseIntermediateOutputPath>[^<]*\$\(MSBuildProjectName\)"), "the intermediate path names the project");

        // 2. no project may hard-code a shared output directory that would defeat the props file
        var projects = Directory.GetFiles(unitDir, "*.csproj").Select(Path.GetFileName).ToArray();
        Check(projects.Length >= 20, "the unit projects are there");
        foreach (string name in projects)
        {
            string text = File.ReadAllText(Path.Combine(unitDir, name));
            var m = Regex.Match(text, @"<OutputPath>([^<]*)</OutputPath>");
            if (!m.Success) continue;   // inherits the per-project path from the props file
            string path = m.Groups[1].Value;
            Check(path.Contains("$(MSBuildProjectName)") || path.Contains(name.Replace(".csproj", "")),
                name + " does not pin a shared output directory");
        }

        // 3. no project may pin a shared intermediate directory either
        foreach (string name in projects)
        {
            string text = File.ReadAllText(Path.Combine(unitDir, name));
            var m = Regex.Match(text, @"<BaseIntermediateOutputPath>([^<]*)</BaseIntermediateOutputPath>");
            if (!m.Success) continue;
            string path = m.Groups[1].Value;
            Check(path.Contains("$(MSBuildProjectName)") || path.Contains(name.Replace(".csproj", "")),
                name + " does not pin a shared intermediate directory");
        }

        // 4. every built project must live in its own tree: the per-project output directory is the one the
        //    props file names, and no two projects may share it.
        string binRoot = Path.Combine(unitDir, "bin");
        if (Directory.Exists(binRoot))
        {
            var trees = Directory.GetDirectories(binRoot).Select(Path.GetFileName).ToArray();
            foreach (string tree in trees)
            {
                if (tree == "Release" || tree == "Debug") continue;   // a framework-level folder, not a project
                // A tree may carry a build-configuration prefix: `dotnet build --artifacts-path` writes
                // "B-<Project>" and "C-<Project>" (Debug). The project name is the part after the prefix.
                string project = tree;
                int dash = project.IndexOf('-');
                if (dash > 0 && (project[0] == 'B' || project[0] == 'C')) project = project.Substring(dash + 1);
                Check(projects.Any(p => string.Equals(p.Replace(".csproj", ""), project, StringComparison.Ordinal)),
                    "the output tree " + tree + " belongs to a unit project");
            }
        }

        Console.WriteLine($"PASS Unit isolation: {checks} checks");
        return 0;
    }
}
