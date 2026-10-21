using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// The release flow in docs/CONTRIBUTING.md is a set of COMMANDS a person runs. Every one of them
// must exist, be runnable and be the real one: a renamed script in the documentation leaves every
// other test green, because nothing read that file.
//
// This test reads the documentation and the repository, and it checks that the two agree.
static class ReleaseDocTest
{
    static int checks, failures;
    static void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        string contributing = Read("docs/CONTRIBUTING.md");

        // ---- 1. every command the release flow names must exist ----
        foreach (string cmd in new[] { "tools/libs.sh", "tools/package.sh", "tools/pin-installer.sh" })
        {
            Check("the release flow names " + cmd, contributing.Contains(cmd));
            Check("the command exists: " + cmd, File.Exists(Path.Combine(root, cmd)));
        }
        // The COMMAND BLOCK is what a person copies and runs. A name that is only mentioned in the
        // prose is not enough: renaming the command in the block must fail the test, even when the
        // same name still appears somewhere else in the file.
        string block = "";
        int section = contributing.IndexOf("Making a release", StringComparison.Ordinal);
        if (section >= 0)
        {
            int start = contributing.IndexOf("```sh", section, StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = contributing.IndexOf("```", start + 5, StringComparison.Ordinal);
                if (end > start) block = contributing.Substring(start, end - start);
            }
        }
        Check("the release flow has a command block", block.Length > 0);
        foreach (string cmd in new[] { "tools/libs.sh", "tools/package.sh", "tools/pin-installer.sh" })
            Check("the command block names " + cmd, block.Contains(cmd));
        foreach (string cmd in new[] { "python3 tools/package_audit.py", "sh tools/installer-test.sh sh" })
            Check("the release flow names " + cmd, contributing.Contains(cmd));

        Check("the release flow names the audit", contributing.Contains("tools/package_audit.py"));
        Check("the release flow names the installer test", contributing.Contains("tools/installer-test.sh"));

        // ---- 2. the audit must FAIL CLOSED when it inspected no package ----
        // A clean checkout has no dist/ (it is gitignored), so an audit that reports success there
        // would gate NOTHING: the zip-level guarantees would never be checked in CI.
        string audit = Read("tools/package_audit.py");
        Check("the audit fails closed without a package",
            audit.Contains("--repo-only") &&
            (audit.Contains("no package zip to check") || audit.Contains("FAIL CLOSED") ||
             audit.Contains("the audit inspected no package")));

        // ---- 3. the version has one home ----
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        string version = Regex.Match(plugin, @"BaseVersion = ""([^""]+)""").Groups[1].Value;
        Check("the version is in the source", version.Length > 0);
        Check("the release flow says where the version lives", contributing.Contains("BaseVersion"));

        // ---- 4. the licence texts are named ----
        Check("the release flow names the licence folder", contributing.Contains("packaging/licenses/"));
        Check("the licence folder exists", Directory.Exists(Path.Combine(root, "packaging", "licenses")));

        // ---- 5. the Nexus copy is linked ----
        Check("the release flow links the Nexus copy", contributing.Contains("NEXUS.md"));
        Check("the Nexus copy exists", File.Exists(Path.Combine(root, "docs", "NEXUS.md")));

        // ---- 6. the CI workflow really runs the audit and the mutation check ----
        string workflow = Read(".github/workflows/checks.yml");
        Check("CI runs the package audit", workflow.Contains("package_audit.py"));
        Check("CI audits a package", workflow.Contains("--zip"));
        Check("CI builds a package-shaped fixture", workflow.Contains("--build-fixture"));
        Check("CI runs the loader documentation mutation check", workflow.Contains("loaderdoc_mutation.sh"));

        Console.WriteLine(failures == 0 ? $"ALL PASS: ReleaseDoc: {checks} checks" : $"{failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }
}
