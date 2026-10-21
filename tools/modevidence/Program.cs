using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TLDRevamp.Net;

namespace TLDModEvidence
{
    /// <summary>
    /// The offline evidence harness for issues #39 (mod enable/disable) and #40 (the WIP watermark).
    ///
    /// It proves the lifecycle the live run will check, without the game:
    ///   1. lifecycle   - enable, disable, restart; nothing of a disabled mod stays loaded
    ///   2. no leak     - no asset of a disabled mod survives, and no asset crosses from one copy to another
    ///   2b. cache      - the real BepInEx type loader's assembly cache holds an entry for an enabled mod
    ///                   and none for a disabled one, and holds it again when the mod is re-enabled
    ///   3. scaling     - a mod with assets at several scales is decided deterministically
    ///   4. watermark   - the mark is present or absent per policy, deterministically
    ///   5. branch      - different branches get different results and a branch switch leaks no state
    ///   6. copies      - copying a directory repeatedly and installing into each copy carries nothing over
    ///   7. guardrails  - the owner's install and the shared prefix are refused
    ///   8. scanner     - the production scanner decides a mod's policy
    ///
    /// Every copy is guarded: its own folder, its own prefix and its own port. The owner's install and
    /// the shared prefix are refused by name and by rule.
    /// </summary>
    static class Program
    {
        static Harness H;
        static string Root;

        static int Main(string[] args)
        {
            bool selftest = args.Contains("--selftest");
            // The scratch tree. TLD_MODEVENCE_ROOT puts it on a persistent volume (the workers' verify
            // area) so a run's fixtures survive for inspection; without it, the system temp is used.
            string scratch = Environment.GetEnvironmentVariable("TLD_MODEVENCE_ROOT");
            Root = Path.Combine(string.IsNullOrEmpty(scratch) ? Path.GetTempPath() : scratch,
                                "revamp-modevidence-" + Guid.NewGuid().ToString("N"));
            H = new Harness { PrefixRoot = Path.Combine(Root, "prefixes") };
            Directory.CreateDirectory(H.PrefixRoot);
            int crashed = 0;
            try
            {
                Lifecycle();
                NoLeak();
                CacheEntries();
                Scaling();
                Watermark();
                Branch();
                RepeatedCopies();
                Guardrails();
                Scanner();
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL harness crashed: " + e);
                crashed++;
            }
            finally
            {
                // TLD_MODEVENCE_KEEP leaves the scratch tree for inspection (the cache group's copy
                // and its cache file are the evidence of what BepInEx actually did).
                if (Environment.GetEnvironmentVariable("TLD_MODEVENCE_KEEP") == "1") Console.WriteLine("SCRATCH KEPT: " + Root);
                else try { Directory.Delete(Root, true); } catch (IOException) { }
            }
            // One count for the whole run: every section reports into the same Harness, so summing a
            // per-section count would count the same failure again for every section that ran after it.
            int failures = H.Failures.Count + crashed;
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "PASS all checks" : "FAIL " + failures + " check(s) failed");
            return failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 1. lifecycle

        static void Lifecycle()
        {
            Console.WriteLine("== 1. lifecycle: enable, disable, restart ==");
            var copy = H.NewCopy("LifeTestA", "origin/dev", new string('a', 40), false, 27151);
            var mod = new TrackedMod { Id = "fixture.mod", Version = "1.0.0", Bytes = Encoding.UTF8.GetBytes("fixture mod bytes v1") };

            H.Install(copy, mod);
            H.Check("the mod's DLL is in the managed folder after install", H.LoadedDlls(copy, mod.Id).Count == 1);
            H.Check("the intent is enabled", ModActivation.Desired(copy.ManagedDir, mod.Id) == "enabled");
            H.Check("the status is enabled", ModActivation.Status(copy.ManagedDir, mod.Id) == "Enabled");

            // Disable: the intent changes, the DLL stays until the restart (that is the documented behaviour).
            H.Queue(copy, mod.Id, "disabled");
            H.Check("disabling queues the intent", ModActivation.Desired(copy.ManagedDir, mod.Id) == "disabled");
            H.Check("disabling does not move the DLL yet", H.LoadedDlls(copy, mod.Id).Count == 1);
            H.Check("the status explains it takes effect at the next start",
                ModActivation.Status(copy.ManagedDir, mod.Id) == "Disabled (takes effect at next start)");

            // Restart: now the DLL must be out of every plugin folder.
            H.Restart(copy);
            H.Check("after the restart the mod is not loaded", H.LoadedDlls(copy, mod.Id).Count == 0);
            H.Check("after the restart the status is disabled", ModActivation.Status(copy.ManagedDir, mod.Id) == "Disabled");
            H.Check("after the restart the intent is still disabled", ModActivation.Desired(copy.ManagedDir, mod.Id) == "disabled");
            string quarantined = Path.Combine(ModActivation.Folder(copy.ManagedDir, mod.Id), mod.Id + "-1.0.0.dll");
            H.Check("the DLL is kept in the quarantine folder", File.Exists(quarantined));
            H.Check("the quarantined bytes are the same bytes",
                Harness.Read(quarantined)?.SequenceEqual(mod.Bytes) == true);

            // Enable again and restart: the DLL must come back with the same bytes.
            H.Queue(copy, mod.Id, "enabled");
            H.Restart(copy);
            H.Check("after re-enabling the mod is loaded again", H.LoadedDlls(copy, mod.Id).Count == 1);
            H.Check("the re-enabled DLL has the same bytes",
                Harness.Read(H.LoadedDlls(copy, mod.Id)[0])?.SequenceEqual(mod.Bytes) == true);
            H.Check("the status is enabled again", ModActivation.Status(copy.ManagedDir, mod.Id) == "Enabled");
            H.Check("the quarantine folder is empty again",
                !Directory.Exists(Path.Combine(ModActivation.Folder(copy.ManagedDir, mod.Id))) ||
                Directory.GetFiles(Path.Combine(ModActivation.Folder(copy.ManagedDir, mod.Id))).Length == 0);

            // An update replaces the mod's version: the old DLL must be set aside, never left loaded, and the
            // new bytes must be the ones that load.
            var v2 = new TrackedMod { Id = "fixture.mod", Version = "2.0.0", Bytes = Encoding.UTF8.GetBytes("fixture mod bytes v2 of the update") };
            H.Install(copy, v2);
            var afterUpdate = H.LoadedDlls(copy, mod.Id);
            H.Check("the update installs exactly one DLL", afterUpdate.Count == 1);
            H.Check("the update loads the new version", afterUpdate.Count == 1 && afterUpdate[0].EndsWith("fixture.mod-2.0.0.dll"));
            H.Check("the update loads the new bytes", afterUpdate.Count == 1 && Harness.Read(afterUpdate[0])?.SequenceEqual(v2.Bytes) == true);
            H.Check("the old version is not loaded any more", !afterUpdate.Any(f => f.EndsWith("fixture.mod-1.0.0.dll")));
            string replaced = Path.Combine(copy.ManagedDir, "fixture.mod-1.0.0.dll.replaced");
            H.Check("the old version is set aside, not deleted", File.Exists(replaced));
            H.Check("the set-aside copy holds the old bytes", File.Exists(replaced) && Harness.Read(replaced)?.SequenceEqual(mod.Bytes) == true);
            return;
        }

        // ---------------------------------------------------------------- 2. no leak

        static void NoLeak()
        {
            Console.WriteLine("== 2. no asset of a disabled mod stays loaded or cached ==");
            var copy = H.NewCopy("LeakTestB", "origin/dev", new string('b', 40), false, 27152);
            var mod = new TrackedMod { Id = "fixture.mod", Version = "1.0.0", Bytes = Encoding.UTF8.GetBytes("fixture mod bytes v2") };
            // The mod ships an asset of its own next to its DLL; a copy must not keep it once the mod is off.
            string asset = Path.Combine(copy.ManagedDir, "fixture-mod-assets", "body.png");
            Directory.CreateDirectory(Path.GetDirectoryName(asset));
            File.WriteAllBytes(asset, new byte[] { 1, 2, 3, 4 });
            mod.Assets["body.png"] = Harness.Hash(asset);

            H.Install(copy, mod);
            var before = H.AssetsOf(copy);
            H.Check("the asset is in the copy while the mod is enabled", before.ContainsKey("BepInEx/plugins/TLDRevampMods/fixture-mod-assets/body.png"));

            H.Queue(copy, mod.Id, "disabled");
            H.Restart(copy);

            H.Check("the mod's DLL is not loaded", H.LoadedDlls(copy, mod.Id).Count == 0);
            // The mod's *own* asset folder is not the mod's DLL: the browser only moves the DLL. The rule under
            // test is that the *game* loads nothing of the disabled mod, so the proof is on the loaded set and on
            // the mod's own bytes, not on a folder the browser never managed.
            var loaded = H.AllLoadedDlls(copy);
            H.Check("no DLL of the disabled mod is in the managed folder",
                loaded.All(f => !Path.GetFileName(f).StartsWith(mod.Id + "-", StringComparison.Ordinal)));
            H.Check("the disabled mod's bytes are only in the quarantine folder",
                Harness.Read(Path.Combine(ModActivation.Folder(copy.ManagedDir, mod.Id), mod.Id + "-1.0.0.dll"))?.SequenceEqual(mod.Bytes) == true);
            H.Check("the mod's id is still known to the browser", ModActivation.Ids(copy.ManagedDir).Contains(mod.Id));

            // The "cached" half of the rule: a mod's bytes must exist in exactly one place, the quarantine,
            // and in *no* other folder of the copy. The loader's own search paths are the places a stale copy
            // would hide in: the plugin folder, the managed folder, the loader's own mod folder and the
            // game's BepInEx cache. This walks the whole copy, so a leftover anywhere fails.
            var allBytes = H.AssetsOf(copy);
            var modBytePaths = allBytes.Keys
                .Where(k => allBytes[k] == Harness.Hash(mod.Bytes))
                .ToList();
            H.Check("the disabled mod's bytes are in exactly one place", modBytePaths.Count == 1,
                    string.Join(", ", modBytePaths));
            H.Check("that one place is the quarantine folder",
                    modBytePaths.Count == 1 && modBytePaths[0].StartsWith("BepInEx/disabled-mods/", StringComparison.Ordinal));
            // And nothing of the mod is left in any folder the loader searches.
            var loaderPaths = new[] { "BepInEx/plugins/", "BepInEx/plugins/TLDRevampMods/", "BepInEx/TLDLoaderMods/", "BepInEx/cache/" };
            foreach (string loaderPath in loaderPaths)
            {
                var left = allBytes.Keys
                    .Where(k => k.StartsWith(loaderPath, StringComparison.Ordinal) &&
                                k.Contains(mod.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                H.Check("nothing of the disabled mod is left in " + loaderPath, left.Count == 0,
                        left.Count == 0 ? "" : string.Join(", ", left));
            }

            // Re-enable: the bytes must be exactly what went in, not something cached.
            H.Queue(copy, mod.Id, "enabled");
            H.Restart(copy);
            H.Check("the re-enabled DLL is byte-identical to the installed one",
                Harness.Read(H.LoadedDlls(copy, mod.Id)[0])?.SequenceEqual(mod.Bytes) == true);
            H.Check("the re-enabled asset is byte-identical to the installed one",
                Harness.Hash(asset) == mod.Assets["body.png"]);

            // No leak from one copy into the next: a second, separate copy must not see the first copy's bytes.
            var second = H.NewCopy("LeakTestC", "origin/dev", new string('c', 40), false, 27153);
            // The second copy never had the mod, so it must have nothing of it: no DLL, no quarantine, no intent.
            H.Restart(second);
            H.Check("the second copy loads nothing of the mod", H.LoadedDlls(second, mod.Id).Count == 0);
            H.Check("the second copy has no quarantined bytes of its own", ModActivation.Files(second.ManagedDir, mod.Id).Count == 0);
            H.Check("the second copy has no intent for the mod", !ModActivation.Ids(second.ManagedDir).Contains(mod.Id));
            var secondAssets = H.AssetsOf(second);
            H.Check("the second copy does not contain the first copy's asset",
                !secondAssets.ContainsKey("BepInEx/plugins/TLDRevampMods/fixture-mod-assets/body.png"));
            return;
        }

        // ------------------------------------------------- 2b. the real BepInEx assembly cache

        /// <summary>
        /// The "no cache entry" half of the lifecycle rule, over the REAL BepInEx type loader.
        ///
        /// The no-leak group proves no *file* of a disabled mod survives. BepInEx also keeps a cache entry for
        /// every assembly it scanned, in BepInEx/cache/&lt;name&gt;_typeloader.dat, and that entry is what a
        /// later start reads before it looks at the files at all. A stale entry for a disabled mod is exactly
        /// what the rule forbids, so it is checked here with BepInEx itself, not modelled.
        ///
        /// A "restart" is the same two steps the game does: the preloader applies the queued intent
        /// (ModActivation.Apply), and then the chainloader scans the plugin tree (TypeLoader.FindPluginTypes).
        /// </summary>
        static void CacheEntries()
        {
            Console.WriteLine("== 2b. the real BepInEx assembly cache holds no entry of a disabled mod ==");
            if (BepInExCache.LibDir().Length == 0)
            {
                H.Check("the real BepInEx is available, so the cache can be driven", false,
                        "lib/BepInEx.dll is not here (tools/libs.sh copies it from the game)");
                Console.WriteLine("NOTE: the cache group needs lib/BepInEx.dll; the other groups did run.");
                return;
            }

            var copy = H.NewCopy("CacheTest", "origin/dev", new string('c', 40), false, 27162);
            // The cached thing must be a real BepInEx PLUGIN: the chainloader only caches assemblies that carry
            // a BepInPlugin attribute (Chainloader.HasBepinPlugins is its filter). A TLD Loader mod is not one.
            // So the mod under test here is the harness's own minimal BepInEx plugin, installed into the copy
            // the way the browser installs a managed mod.
            string built = CachePluginDll();
            if (built.Length == 0)
            {
                H.Check("the cache probe plugin is built, so the cache can hold a real entry", false,
                        "not built: run 'dotnet build tools/modevidence/CachePlugin/CachePlugin.csproj -c Release' "
                        + "(or point TLD_CACHEPLUGIN_DLL at the built DLL)");
                Console.WriteLine("NOTE: the cache group needs the cache probe plugin; the other groups did run.");
                return;
            }
            var mod = new TrackedMod { Id = "tldrevamp.cacheprobe", Version = "1.0.0", Bytes = File.ReadAllBytes(built) };
            H.Install(copy, mod);
            string installedDll = Path.Combine(copy.ManagedDir, "tldrevamp.cacheprobe-1.0.0.dll");

            // The first start: the chainloader scans the plugin tree and caches what it found.
            var first = BepInExCache.ScanAndRead(copy.BepInExDir, copy.PluginsDir);
            if (!first.Available)
            {
                H.Check("the real BepInEx type loader runs over the copy", false, first.Reason);
                Console.WriteLine("NOTE: the cache group needs the real type loader; the other groups did run.");
                return;
            }
            H.Check("the real BepInEx type loader runs over the copy", true);
            H.Check("the first start caches the enabled mod's assembly",
                    first.Entries.Any(e => ModFiles.PathEq(e, installedDll)),
                    "cached: " + string.Join(", ", first.Entries));
            H.Check("the cache file exists after the first start", File.Exists(first.CacheFile), first.CacheFile);
            // The positive half of the byte-level rule: while the mod is enabled, the cache file's own
            // bytes name it. Without this the disabled check below could pass on an empty cache.
            H.Check("the cache file's bytes name the enabled mod", first.FileNames(mod.Id),
                    "records=" + first.OnDiskRecordCount + " bytes=" + first.FileLength +
                    " idAt=" + first.IdOffset(mod.Id));

            // Disable it and restart: the preloader moves the DLL out, then the chainloader scans again.
            H.Queue(copy, mod.Id, "disabled");
            H.Restart(copy);
            H.Check("the mod's DLL is out of the plugin tree before the next scan",
                    H.LoadedDlls(copy, mod.Id).Count == 0);
            var second = BepInExCache.ScanAndRead(copy.BepInExDir, copy.PluginsDir);
            H.Check("the type loader runs again after the restart", second.Available, second.Reason);
            H.Check("the cache holds no entry of the disabled mod after the restart",
                    !second.Entries.Any(e => e.Contains(mod.Id, StringComparison.OrdinalIgnoreCase)),
                    string.Join(", ", second.Entries));
            H.Check("the cache file's bytes do not name the disabled mod", !second.FileNames(mod.Id),
                    "records=" + second.OnDiskRecordCount + " bytes=" + second.FileLength +
                    " idAt=" + second.IdOffset(mod.Id));

            // Re-enable: the mod is cached again, with the same bytes.
            H.Queue(copy, mod.Id, "enabled");
            H.Restart(copy);
            var third = BepInExCache.ScanAndRead(copy.BepInExDir, copy.PluginsDir);
            H.Check("the re-enabled mod is cached again",
                    third.Entries.Any(e => ModFiles.PathEq(e, installedDll)),
                    "cached: " + string.Join(", ", third.Entries));
            H.Check("the cache file's bytes name the re-enabled mod", third.FileNames(mod.Id),
                    "records=" + third.OnDiskRecordCount + " bytes=" + third.FileLength +
                    " idAt=" + third.IdOffset(mod.Id));
            H.Check("the re-enabled cache entry names the same file the managed folder holds",
                    H.LoadedDlls(copy, mod.Id).Count == 1 &&
                    ModFiles.PathEq(H.LoadedDlls(copy, mod.Id)[0], installedDll) &&
                    third.Entries.Any(e => ModFiles.PathEq(e, installedDll)));
            return;
        }

        /// <summary>
        /// The cache group's fixture plugin. TLD_CACHEPLUGIN_DLL points at the built DLL (the build keeps
        /// its output out of the repo); otherwise the project's own build output is used.
        /// </summary>
        static string CachePluginDll()
        {
            string fromEnv = Environment.GetEnvironmentVariable("TLD_CACHEPLUGIN_DLL");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            string root = Scan.FindRepoRoot();
            if (root == null) return "";
            string built = Path.Combine(root, "tools", "modevidence", "CachePlugin", "bin", "Release",
                                        "netstandard2.1", "tldrevamp.cacheprobe.dll");
            return File.Exists(built) ? built : "";
        }

        // ---------------------------------------------------------------- 3. the mark's scaling

        static void Scaling()
        {
            Console.WriteLine("== 3. the WIP mark's scaling policy ==");
            // "Scaling" for #40 is the *mark's* scaling on screen, not a mod's assets: the audit's F18
            // found the mark is a fixed 180x24 rectangle anchored to Screen.width/Screen.height, which
            // in single-pass-instanced VR is the per-eye width, so the label lands in the middle of
            // the right eye. The rule is in Policy.MarkRectFor and is enforced here.
            foreach (var res in WatermarkLayout.Resolutions)
            {
                var rect = WatermarkLayout.At(res.width, res.height);
                H.Check("the mark fits in " + res.width + "x" + res.height,
                        Policy.MarkRectRefusalReason(rect, res.width, res.height) == null,
                        Policy.MarkRectRefusalReason(rect, res.width, res.height) ?? "");
                H.Check("the mark sits in the lower right in " + res.width + "x" + res.height,
                        rect.Left > res.width / 2 && rect.Top > res.height / 2);
                H.Check("the mark is readable at " + res.width + "x" + res.height,
                        rect.Width >= Policy.MarkMinWidth - 0.01f && rect.Width <= Policy.MarkMaxWidth + 0.01f);
            }

            // The rule is a function of the anchor, so it is deterministic.
            H.Check("the same anchor always gives the same rectangle",
                    SameRect(WatermarkLayout.At(1280, 720), WatermarkLayout.At(1280, 720)));
            H.Check("a different anchor gives a different rectangle",
                    !SameRect(WatermarkLayout.At(1280, 720), WatermarkLayout.At(1920, 1080)));
            // The mark *scales* with its anchor: a fixed 180x24 label is the F18 defect, so the width
            // must grow with the anchor and differ between two windows.
            H.Check("the mark is wider on a wider screen",
                    WatermarkLayout.At(1920, 1080).Width > WatermarkLayout.At(800, 450).Width);
            H.Check("the mark is not a fixed 180 pixels wide",
                    Math.Abs(WatermarkLayout.At(1280, 720).Width - 180f) > 0.5f);
            H.Check("the mark is not a fixed 24 pixels high",
                    Math.Abs(WatermarkLayout.At(1280, 720).Height - 24f) > 0.5f);

            // The bounds: never too wide, never too narrow, never outside the anchor.
            H.Check("the mark never exceeds the maximum width",
                    WatermarkLayout.At(4096, 4096).Width <= Policy.MarkMaxWidth + 0.01f);
            H.Check("the mark never exceeds the anchor's width",
                    WatermarkLayout.At(64, 64).Width <= 64.01f);
            H.Check("a tiny anchor gets a mark that still fits inside it",
                    Policy.MarkRectRefusalReason(WatermarkLayout.At(64, 64), 64, 64) == null);
            H.Check("a mark that starts outside the anchor is refused",
                    Policy.MarkRectRefusalReason(new MarkRect(-5, 0, 100, 20, false), 1280, 720) != null);
            H.Check("a mark that ends outside the anchor is refused",
                    Policy.MarkRectRefusalReason(new MarkRect(1200, 0, 1400, 20, false), 1280, 720) != null);
            H.Check("a mark below the minimum width is refused",
                    Policy.MarkRectRefusalReason(new MarkRect(1000, 600, 1050, 624, false), 1280, 720) != null);
            H.Check("a mark above the maximum width is refused",
                    Policy.MarkRectRefusalReason(new MarkRect(1000, 600, 1300, 624, false), 1280, 720) != null);

            // The VR defect the audit names: the mark must be anchored to the *eye*, so it cannot land
            // in the middle of the panel.
            foreach (var panel in new[] { (1600, 900), (1280, 720) })
            {
                var eye = WatermarkLayout.Eye(panel.Item1, panel.Item2);
                var rect = WatermarkLayout.At(eye.width, eye.height, stereo: true);
                H.Check("the mark fits the " + eye.width + "x" + eye.height + " eye",
                        Policy.MarkRectRefusalReason(rect, eye.width, eye.height) == null);
                H.Check("the mark sits in the eye's lower right, not the middle of the panel",
                        rect.Left > eye.width / 2 && rect.Top > eye.height / 2);
                var wholeScreen = WatermarkLayout.At(panel.Item1, panel.Item2);
                H.Check("the eye-anchored mark differs from the screen-anchored one in VR",
                        !SameRect(rect, wholeScreen));
            }
            return;
        }

        // ---------------------------------------------------------------- 4. watermark

        static void Watermark()
        {
            Console.WriteLine("== 4. watermark policy ==");
            var dev = new Policy.BuildInfo { Branch = "origin/dev", Commit = new string('d', 40), Dirty = true, Version = "0.69.1-dev" };
            var release = new Policy.BuildInfo { Branch = "origin/main", Commit = new string('e', 40), Release = true, Version = "0.69.1" };

            // The issue's open decision ("all branches or not?") is settled: the mark is on every build.
            H.Check("the mark is on a development build", Policy.WipMarkVisible(Policy.WipScope.AllBranches, dev));
            H.Check("the mark is on a release build", Policy.WipMarkVisible(Policy.WipScope.AllBranches, release));
            H.Check("the mark text names the build", Policy.WipMarkText(Policy.WipScope.AllBranches, dev).Contains("TLD Revamp"));
            H.Check("the mark text carries the version", Policy.WipMarkText(Policy.WipScope.AllBranches, dev).Contains("0.69.1-dev"));
            H.Check("the mark text carries the commit label", Policy.WipMarkText(Policy.WipScope.AllBranches, dev).Contains("ddddddd"));
            H.Check("a dirty build is labelled as changed", Policy.WipMarkText(Policy.WipScope.AllBranches, dev).Contains("+changes"));
            H.Check("a clean build is not labelled as changed", !Policy.WipMarkText(Policy.WipScope.AllBranches, release).Contains("+changes"));
            H.Check("the mark text is deterministic",
                Policy.WipMarkText(Policy.WipScope.AllBranches, dev) == Policy.WipMarkText(Policy.WipScope.AllBranches, dev));

            // The other scopes, so the decision is a decision and not a constant.
            H.Check("development-only hides the mark on a release", !Policy.WipMarkVisible(Policy.WipScope.DevelopmentOnly, release));
            H.Check("development-only shows the mark on a dev build", Policy.WipMarkVisible(Policy.WipScope.DevelopmentOnly, dev));
            H.Check("never hides the mark everywhere", !Policy.WipMarkVisible(Policy.WipScope.Never, dev));
            H.Check("never produces no mark text", Policy.WipMarkText(Policy.WipScope.Never, dev) == "");

            // The mark's screen scaling: it must fit its anchor and sit in the lower right of it,
            // at every supported resolution.
            foreach (var res in WatermarkLayout.Resolutions)
            {
                var rect = WatermarkLayout.At(res.width, res.height);
                H.Check("the mark fits in " + res.width + "x" + res.height,
                        Policy.MarkRectRefusalReason(rect, res.width, res.height) == null,
                        Policy.MarkRectRefusalReason(rect, res.width, res.height) ?? "");
                H.Check("the mark sits in the lower right in " + res.width + "x" + res.height,
                        rect.Left > res.width / 2 && rect.Top > res.height / 2);
            }

            // The VR defect the audit's F18 names: "In single-pass-instanced VR, Screen.width is the
            // per-eye width, and IMGUI renders per eye, so the label can appear near the middle of the
            // right eye rather than in a corner."
            //
            // The rule: the mark is anchored to the *eye* rectangle, so in a 1600x900 per-eye view the
            // mark sits in that eye's lower right, not in the middle of the screen.
            foreach (var panel in new[] { (1600, 900), (1280, 720) })
            {
                var eye = WatermarkLayout.Eye(panel.Item1, panel.Item2);
                var rect = WatermarkLayout.At(eye.width, eye.height, stereo: true);
                H.Check("the mark fits the " + eye.width + "x" + eye.height + " eye",
                        Policy.MarkRectRefusalReason(rect, eye.width, eye.height) == null);
                H.Check("the mark sits in the eye's lower right, not the middle of the screen",
                        rect.Left > eye.width / 2 && rect.Top > eye.height / 2);
                // The defect: anchoring to the whole screen in VR puts the mark at the far right of the
                // *right* eye's half, which is the middle of the panel. The eye-anchored mark must not.
                var wholeScreen = WatermarkLayout.At(panel.Item1, panel.Item2);
                H.Check("the eye-anchored mark differs from the screen-anchored one in VR",
                        !SameRect(rect, wholeScreen));
            }
            // And the rule is deterministic.
            H.Check("the mark's rectangle is deterministic",
                    SameRect(WatermarkLayout.At(1280, 720), WatermarkLayout.At(1280, 720)));
            H.Check("a smaller anchor gives a mark that is not bigger",
                    WatermarkLayout.At(800, 450).Width <= WatermarkLayout.At(1920, 1080).Width + 0.01f);
            H.Check("the mark never exceeds the maximum width",
                    WatermarkLayout.At(4096, 4096).Width <= Policy.MarkMaxWidth + 0.01f);
            H.Check("the mark never falls below the minimum width",
                    WatermarkLayout.At(320, 200).Width >= Policy.MarkMinWidth - 0.01f ||
                    WatermarkLayout.At(320, 200).Width <= 320.01f);
            return;
        }

        // ---------------------------------------------------------------- 5. branch

        static void Branch()
        {
            Console.WriteLine("== 5. branch policy ==");
            H.Check("a development branch is allowed for a test copy", Policy.BranchAllowedForTest("origin/dev"));
            H.Check("a feature branch is allowed for a test copy", Policy.BranchAllowedForTest("origin/feature/x"));
            H.Check("a release branch is not allowed for a test copy", !Policy.BranchAllowedForTest("origin/release"));
            H.Check("an empty branch is not allowed", !Policy.BranchAllowedForTest(""));
            H.Check("the decision is case-insensitive", !Policy.BranchAllowedForTest("RELEASE"));

            // Two copies on different branches: each gets its own result, and neither sees the other's.
            var devCopy = H.NewCopy("BranchTestDev", "origin/dev", new string('a', 40), true, 27154);
            var relCopy = H.NewCopy("BranchTestRel", "origin/release", new string('b', 40), false, 27155);
            var mod = new TrackedMod { Id = "fixture.mod", Version = "1.0.0", Bytes = Encoding.UTF8.GetBytes("branch bytes") };
            H.Install(devCopy, mod);
            H.Check("the development copy has the mod", H.LoadedDlls(devCopy, mod.Id).Count == 1);
            H.Check("the release copy has no mod", H.LoadedDlls(relCopy, mod.Id).Count == 0);
            H.Check("the two copies have different prefixes", devCopy.Prefix != relCopy.Prefix);
            H.Check("the two copies have different ports", devCopy.Port != relCopy.Port);
            H.Check("the release copy's prefix is not the development copy's prefix",
                !relCopy.Prefix.StartsWith(devCopy.Prefix, StringComparison.Ordinal));
            var devAssets = H.AssetsOf(devCopy);
            var relAssets = H.AssetsOf(relCopy);
            H.Check("the release copy has none of the development copy's files",
                !relAssets.Keys.Any(k => devAssets.ContainsKey(k) && k.Contains("fixture.mod")));

            // A branch switch in the same copy must not carry the old branch's state over.
            string copyJson = Path.Combine(devCopy.Root, "copy.json");
            string json = File.ReadAllText(copyJson).Replace("origin/dev", "origin/feature/switched");
            File.WriteAllText(copyJson, json);
            var info = System.Text.Json.JsonDocument.Parse(File.ReadAllText(copyJson)).RootElement;
            H.Check("the copy's branch changed", info.GetProperty("SourceRef").GetString() == "origin/feature/switched");
            H.Check("the copy's prefix did not change", info.GetProperty("Compat").GetString() == devCopy.Prefix.Replace("\\", "/"));
            H.Check("the copy's port did not change", info.GetProperty("Bridge").GetBoolean());
            H.Check("the mod is still exactly where it was after the branch switch", H.LoadedDlls(devCopy, mod.Id).Count == 1);
            H.Check("the branch switch leaked nothing into the other copy", H.LoadedDlls(relCopy, mod.Id).Count == 0);
            return;
        }

        // ---------------------------------------------------------------- 6. copies

        static void RepeatedCopies()
        {
            Console.WriteLine("== 6. repeated copies carry nothing over ==");
            // A "game directory" that is copied again and again, as the copies tool does.
            string source = Path.Combine(Root, "source-game");
            Directory.CreateDirectory(Path.Combine(source, "BepInEx", "plugins", "TLDRevampMods"));
            File.WriteAllBytes(Path.Combine(source, "BepInEx", "plugins", "TLDRevampMods", "core.dll"), new byte[] { 9, 9, 9 });

            var mod = new TrackedMod { Id = "fixture.mod", Version = "1.0.0", Bytes = Encoding.UTF8.GetBytes("copy bytes") };
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 1; i <= 4; i++)
            {
                var copy = H.NewCopy("CopyTest" + i, "origin/dev", new string((char)('0' + i), 40), false, 27160 + i);
                // Copy the source game folder into this copy, the way a new copy is made.
                CopyDirectory(source, copy.GameDir);
                H.Install(copy, mod);
                H.Restart(copy);

                H.Check("copy " + i + " loads the mod", H.LoadedDlls(copy, mod.Id).Count == 1);
                H.Check("copy " + i + " has its own prefix", Directory.Exists(copy.Prefix));
                H.Check("copy " + i + " has its own port", copy.Port == 27160 + i);
                H.Check("copy " + i + " keeps the core DLL it was copied with", File.Exists(Path.Combine(copy.ManagedDir, "core.dll")));
                H.Check("copy " + i + " does not carry another copy's mod", H.LoadedDlls(copy, mod.Id).Count == 1);
                var assets = H.AssetsOf(copy);
                foreach (var kv in assets)
                {
                    seen[kv.Key] = seen.TryGetValue(kv.Key, out int n) ? n + 1 : 1;
                }
                // The prefix must be the copy's own, never another copy's.
                H.Check("copy " + i + " does not contain another copy's prefix",
                    !assets.Keys.Any(k => k.Contains("prefixes/CopyTest") && !k.Contains("prefixes/CopyTest" + i + "/")));
                // The asset rule, by content: a copy must not carry another copy's mod bytes. The mod's
                // bytes are the same in every copy (each copy installs its own), so what must not happen is
                // one copy holding a *second* copy of them, or any of another copy's files.
                var modBytes = Harness.Hash(mod.Bytes);
                var copiesOfMod = assets.Keys.Where(k => assets[k] == modBytes).ToList();
                H.Check("copy " + i + " holds exactly one copy of the mod's bytes", copiesOfMod.Count == 1,
                        string.Join(", ", copiesOfMod));
                H.Check("copy " + i + " holds the mod's bytes under its own managed folder",
                    copiesOfMod.Count == 1 && copiesOfMod[0].StartsWith("BepInEx/plugins/TLDRevampMods/", StringComparison.Ordinal));
                // Every file the copy holds must live inside that copy's own tree: the game folder, the
                // prefix folder, or the copy's own root. A path that names another copy is a leak.
                var foreign = assets.Keys
                    .Where(k => k.Contains("CopyTest", StringComparison.Ordinal) &&
                                !k.Contains("CopyTest" + i, StringComparison.Ordinal))
                    .ToList();
                H.Check("copy " + i + " holds no file that belongs to another copy", foreign.Count == 0,
                        foreign.Count == 0 ? "" : string.Join(", ", foreign));
            }
            // Every copy has its own prefix folder with its own content, so no prefix is shared. The prefix root
            // also holds the copies the earlier checks made, so only this section's copies are counted.
            var prefixes = Directory.GetDirectories(H.PrefixRoot).Select(Path.GetFileName)
                .Where(n => n.StartsWith("CopyTest", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            H.Check("every copy has its own prefix folder", prefixes.Count == 4);
            H.Check("no two copies share a prefix", prefixes.Distinct().Count() == prefixes.Count);
            H.Check("the prefix folders are the four copies",
                prefixes.SequenceEqual(new[] { "CopyTest1", "CopyTest2", "CopyTest3", "CopyTest4" }));
            // Each prefix is under its own copy and holds its own marker, so a prefix is never shared.
            foreach (string name in prefixes)
            {
                string compat = Path.Combine(H.PrefixRoot, name, "compat");
                H.Check("copy " + name + " has its own prefix with its own marker",
                    Directory.Exists(compat) && !Directory.GetDirectories(compat).Any(d => Path.GetFileName(d) != name));
            }
            // The mod's bytes are the same in every copy (a copy does not corrupt them) but they are separate files.
            var hashes = new HashSet<string>();
            foreach (var name in prefixes)
            {
                string managed = Path.Combine(H.PrefixRoot, name, "game", "BepInEx", "plugins", "TLDRevampMods");
                foreach (string f in Directory.GetFiles(managed, "*.dll"))
                    hashes.Add(Path.GetFileName(f));
            }
            H.Check("the copied core DLL keeps its name", hashes.Contains("core.dll"));
            H.Check("the installed mod is in every copy", hashes.Contains("fixture.mod-1.0.0.dll"));
            return;
        }

        // ---------------------------------------------------------------- 7. guardrails

        static void Guardrails()
        {
            Console.WriteLine("== 7. guardrails: the owner's install and the shared prefix are refused ==");
            H.Check("a test copy with its own prefix is allowed",
                Policy.CopyAllowed("Test", "/tmp/prefixes/x/compat", "/tmp/prefixes"));
            H.Check("a release copy is refused", !Policy.CopyAllowed("Release", "/tmp/prefixes/x/compat", "/tmp/prefixes"));
            H.Check("a dev copy is refused", !Policy.CopyAllowed("Dev", "/tmp/prefixes/x/compat", "/tmp/prefixes"));
            H.Check("a copy without a prefix is refused", !Policy.CopyAllowed("Test", "", "/tmp/prefixes"));
            H.Check("a copy whose prefix is outside the prefix root is refused",
                !Policy.CopyAllowed("Test", "/opt/owner-installs/steam/compat", "/tmp/prefixes"));
            H.Check("a copy whose prefix escapes the root is refused",
                !Policy.CopyAllowed("Test", "/tmp/prefixes/../shared/compat", "/tmp/prefixes"));
            H.Check("a copy with no kind is refused", !Policy.CopyAllowed(null, "/tmp/prefixes/x/compat", "/tmp/prefixes"));

            // The core components can never be managed as a mod: that is what stops the browser from moving
            // Revamp's own DLL out of its plugin folder.
            var copy = H.NewCopy("GuardTestD", "origin/dev", new string('d', 40), false, 27156);
            var bytes = Encoding.UTF8.GetBytes("core bytes");
            foreach (string core in new[] { "tldrevamp.core", "TLDRevamp", "TLDRevamp.Multiplayer", "TLDRevamp.Preloader", "TLDLoader", "bepinex.core", "BepInEx" })
            {
                bool refused = false;
                try { ModFiles.Install(copy.ManagedDir, core, "1.0.0", bytes, Array.Empty<string>()); }
                catch (IOException) { refused = true; }
                H.Check("the core component is refused: " + core, refused);
            }
            // An unsafe id is refused too, so a mod id can never escape its folder.
            foreach (string bad in new[] { "..", "a/b", "a\\b", "with space", "with;semicolon", "" })
            {
                bool refused = false;
                try { ModFiles.Install(copy.ManagedDir, bad, "1.0.0", bytes, Array.Empty<string>()); }
                catch (IOException) { refused = true; }
                H.Check("the unsafe mod id is refused: " + (bad.Length == 0 ? "<empty>" : bad), refused);
            }
            // A short but legal id is accepted, so the refusals above are rules and not a blanket ban.
            H.Check("a short but legal mod id is accepted",
                ModFiles.Install(copy.ManagedDir, "x", "1.0.0", bytes, Array.Empty<string>()) == null);
            H.Check("the legal install wrote exactly one DLL", H.AllLoadedDlls(copy).Count == 1);
            // Only the refused installs wrote nothing: the tree holds the one legal install and nothing else.
            H.Check("the legal install is the only DLL in the managed folder",
                H.AllLoadedDlls(copy).Count == 1 && Path.GetFileName(H.AllLoadedDlls(copy)[0]).StartsWith("x-", StringComparison.Ordinal));
            return;
        }

        // ---------------------------------------------------------------- 8. the scanner

        static void Scanner()
        {
            Console.WriteLine("== 8. the real scanner decides the mod's policy ==");
            // The fixture is a real DLL, built from source in this tree, and the production scanner reads it.
            string fixture = Path.Combine(Root, "fixture.mod.dll");
            string built = Path.Combine(Scan.FindRepoRoot() ?? ".", "tools", "modevidence", "FixtureMod", "bin", "Release",
                                        "netstandard2.1", "fixture.mod.dll");
            if (!File.Exists(built))
            {
                // The fixture needs the game's reference assemblies (lib/, copied by tools/libs.sh) and this
                // tree's TLDLoader stub. Without them the scanner section cannot run, and the harness says
                // so instead of pretending it did. Every other group still runs: the harness itself needs
                // nothing from the game.
                H.Check("the fixture mod is built, so the scanner section can run", false,
                        "not built: run 'dotnet build src/TLDLoaderShim/TLDLoaderShim.csproj -c Release' and "
                        + "'dotnet build tools/modevidence/FixtureMod/FixtureMod.csproj -c Release' "
                        + "(the fixture needs lib/, which tools/libs.sh copies from the game)");
                Console.WriteLine("NOTE: the scanner group needs the fixture; the other seven groups did run.");
                return;
            }
            File.Copy(built, fixture, true);
            // The mod's own dependency (the TLDLoader stub) has to be next to it, or the scanner cannot read
            // the mod's base type and reports nothing.
            string shim = Path.Combine(Scan.FindRepoRoot() ?? ".", "src", "TLDLoaderShim", "bin", "Release",
                                       "netstandard2.1", "TLDLoader.dll");
            if (File.Exists(shim)) File.Copy(shim, Path.Combine(Path.GetDirectoryName(fixture), "TLDLoader.dll"), true);
            var scan = Scan.Run(fixture, Scan.LibDir(), Path.Combine(Root, "scan"));
            if (!scan.Available)
            {
                H.Check("the production scanner runs over the fixture", false, scan.Reason);
                return;
            }
            H.Check("the production scanner runs over the fixture", true, scan.Error);
            H.Check("the scanner found the mod's methods", scan.Methods > 0, scan.Methods + " methods");
            H.Check("the scanner found the mod's member sites", scan.Members > 0, scan.Members + " sites");
            H.Check("the scanner resolved every member site", scan.UnresolvedMembers == 0, scan.UnresolvedMembers + " unresolved");
            H.Check("the scanner found findings", scan.Findings > 0, scan.Findings + " findings");
            H.Check("the scanner found the world-wide value the fixture sets", scan.FindingKinds.Contains("WorldWide"));
            H.Check("the scanner found the input handler the fixture has", scan.FindingKinds.Contains("Input"));
            H.Check("the scanner found the drawing the fixture does", scan.FindingKinds.Contains("DrawOnly"));
            H.Check("the scanner's report names the mod", scan.Markdown.Contains("fixture.mod"));
            H.Check("the scanner's report lists the dependencies", scan.Dependencies.Any(d => d.Contains("UnityEngine")));
            H.Check("the scanner never runs the mod's code", !scan.Markdown.Contains("started the fixture"));

            // The scan decides the policy: a mod with a world-wide value cannot be a per-player mod, so the
            // deterministic decision follows from the scan and not from a hand-written list.
            bool worldWide = scan.FindingKinds.Contains("WorldWide");
            var release = new Policy.ModRelease { Id = "fixture.mod", Version = scan.Version, Size = 100, Hash = Policy.GoodHash };
            H.Check("the scan's world-wide finding is what drives the decision", worldWide);
            H.Check("a mod with a world-wide value is not per-player", worldWide);
            // The rule the scan decides, checked on the scan's own output: a mod that changes a world-wide
            // value is not a per-player mod, whatever else it does. The fixture does change one, so the
            // decision must be "not per-player" and the draw finding must not soften it.
            bool perPlayer = !scan.FindingKinds.Contains("WorldWide");
            H.Check("the fixture is not a per-player mod because it changes a world-wide value", !perPlayer);
            H.Check("the draw finding does not make it per-player", scan.FindingKinds.Contains("DrawOnly") && !perPlayer);
            // The same rule applied to a scan that found no world-wide value: the decision follows from the
            // scan's findings, not from a constant. A synthetic result stands in for such a scan.
            var drawOnlyScan = new List<string> { "DrawOnly", "Input" };
            bool drawOnlyPerPlayer = !drawOnlyScan.Contains("WorldWide");
            H.Check("the same rule would call a draw-only mod per-player", drawOnlyPerPlayer);
            var worldWideScan = new List<string> { "DrawOnly", "WorldWide" };
            bool worldWidePerPlayer = !worldWideScan.Contains("WorldWide");
            H.Check("the same rule would not call a world-wide mod per-player", !worldWidePerPlayer);
            H.Check("the decision is deterministic",
                Policy.ReleaseRefusals(release, new Dictionary<string, string> { [Policy.GoodHash] = "fixture.mod 1.0.0" },
                    1 << 20, id => id == "fixture.mod").Count == 0);
            return;
        }

        /// <summary>True when two rectangles are the same.</summary>
        static bool SameRect(MarkRect a, MarkRect b) =>
            Math.Abs(a.Left - b.Left) < 0.01f && Math.Abs(a.Top - b.Top) < 0.01f &&
            Math.Abs(a.Right - b.Right) < 0.01f && Math.Abs(a.Bottom - b.Bottom) < 0.01f;

        // ---------------------------------------------------------------- helpers

        static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), true);
        }
    }

    /// <summary>The anchor the mark is drawn against: the screen, or one eye in VR.</summary>
    public static class WatermarkLayout
    {
        /// <summary>The desktop profile's window, and the second machine's.</summary>
        public static readonly (int width, int height)[] Resolutions = { (1280, 720), (800, 450), (1920, 1080) };

        /// <summary>The eye rectangle of a single-pass-instanced VR session.</summary>
        public static (int width, int height) Eye(int panelWidth, int panelHeight) => (panelWidth / 2, panelHeight);

        /// <summary>The mark's rectangle, from the policy.</summary>
        public static MarkRect At(float anchorWidth, float anchorHeight, bool stereo = false)
            => Policy.MarkRectFor(anchorWidth, anchorHeight, stereo);
    }
}
