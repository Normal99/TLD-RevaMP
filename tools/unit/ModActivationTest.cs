using System;
using System.IO;
using System.Linq;
using TLDRevamp.Net;

static class ModActivationTest
{
    static int fails;
    static readonly byte[] Bytes = { 1, 2, 3, 4 };
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
    static bool Refuses(Action a) { try { a(); return false; } catch (IOException) { return true; } }
    /// <summary>Whether this user is actually bound by a directory's mode (a privileged user bypasses it).</summary>
    static bool ModeIsEnforced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "revamp-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string probe = Path.Combine(dir, "probe.tmp");
            try { File.WriteAllBytes(probe, new byte[] { 0 }); }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            try { File.Delete(probe); return false; }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (IOException) { }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "revamp-toggle-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "BepInEx", "plugins", "TLDRevampMods"), id = "test.mod";
        Directory.CreateDirectory(dir);
        try
        {
            string dll = ModFiles.Target(dir, id, "1.0.0"), saved = Path.Combine(ModActivation.Folder(dir, id), Path.GetFileName(dll));
            File.WriteAllBytes(dll, Bytes);
            Check("new DLL on disk still needs restart to load", ModActivation.Status(dir, id, false) == "Enabled at next start");
            ModActivation.Queue(dir, id, "disabled");
            ModActivation.Alias(dir, id, "api.mod");
            Check("multiplayer id resolves to persistent browser GUID", ModActivation.Resolve(dir, "api.mod") == id);
            // DLL names are arbitrary Windows file names, not necessarily managed mod IDs.
            foreach (string name in new[] { "Better Lights-1.0.dll", "plain.dll", "日本語-1.0.dll", "a + (b), [c];=!-1.0.dll", new string('x', 80) + "-1.0.dll", "-1.0.dll" })
                File.WriteAllBytes(Path.Combine(dir, name), Bytes);
            File.WriteAllBytes(Path.Combine(dir, "dash-id-1.2.3.dll"), Bytes);
            File.WriteAllBytes(Path.Combine(dir, "dot.id-2.0.dll"), Bytes);
            Directory.CreateDirectory(Path.Combine(ModActivation.Store(dir), "odd folder"));
            File.WriteAllText(Path.Combine(ModActivation.Store(dir), "odd state.state"), "disabled");
            Check("discovery tolerates arbitrary file and receipt names", ModActivation.Ids(dir).All(ModActivation.Allowed));
            Check("dash IDs and dotted versions remain discoverable", ModActivation.Ids(dir).Contains("dash-id") && ModActivation.Files(dir, "dash-id").Count == 1);
            Check("dotted IDs remain discoverable", ModActivation.Ids(dir).Contains("dot.id") && ModActivation.Files(dir, "dot.id").Count == 1);
            bool resolved = false;
            Check("odd filenames do not break alias resolution for joining", !Refuses(() => resolved = ModActivation.Resolve(dir, "api.mod") == id) && resolved);
            BrowserChecks(Path.Combine(root, "browser"));
            Check("queue is durable and leaves loaded DLL untouched", File.Exists(dll) && !File.Exists(saved) && ModActivation.Desired(dir, id) == "disabled" && ModActivation.Pending(dir, id));
            Check("pending disable text", ModActivation.Status(dir, id) == "Disabled (takes effect at next start)");
            ModActivation.Apply(dir, id);
            Check("disable moves outside every plugins subfolder", !File.Exists(dll) && File.ReadAllBytes(saved).SequenceEqual(Bytes) && !saved.StartsWith(Path.Combine(root, "BepInEx", "plugins") + Path.DirectorySeparatorChar));
            Check("disabled survives replay and restart", ModActivation.Status(dir, id) == "Disabled" && !ModActivation.Pending(dir, id));
            ModActivation.Apply(dir, id);
            ModActivation.Queue(dir, id, "enabled");
            Check("pending enable text", ModActivation.Status(dir, id) == "Enabled at next start" && File.Exists(saved));
            ModActivation.Apply(dir, id);
            Check("enable round-trip preserves bytes", File.ReadAllBytes(dll).SequenceEqual(Bytes) && !File.Exists(saved) && ModActivation.Status(dir, id) == "Enabled");
            ModActivation.Queue(dir, id, "disabled"); ModActivation.Queue(dir, id, "enabled");
            Check("opposite queued action cancels before restart", !ModActivation.Pending(dir, id) && File.Exists(dll));
            string second = ModFiles.Target(dir, id, "1.1.0"); File.WriteAllBytes(second, Bytes);
            ModActivation.Queue(dir, id, "disabled");
            try { ModActivation.Apply(dir, id, _ => throw new IOException("simulated crash after atomic rename")); } catch (IOException) { }
            Check("crash halfway preserves every DLL and pending intent", ModActivation.Files(dir, id).Count == 2 && ModActivation.Pending(dir, id) && ModActivation.Desired(dir, id) == "disabled");
            ModActivation.Apply(dir, id);
            Check("replay completes interrupted multi-file move", !File.Exists(dll) && !File.Exists(second) && ModActivation.Files(dir, id).All(f => File.ReadAllBytes(f).SequenceEqual(Bytes)) && !ModActivation.Pending(dir, id));
            Check("disabled update installs outside plugins", ModFiles.Install(dir, id, "2.0.0", Bytes, ModActivation.Files(dir, id)) == null && !File.Exists(ModFiles.Target(dir, id, "2.0.0")));
            var files = ModActivation.Files(dir, id);
            Check("disabled update retains intent and one current version", files.Count == 1 && files[0].EndsWith("test.mod-2.0.0.dll") && ModActivation.Status(dir, id) == "Disabled" && Directory.GetFiles(ModActivation.Folder(dir, id), "*.replaced").Length == 2);
            Check("disabled update cannot hide an older DLL still loaded this session", ModActivation.Status(dir, id, true) == "Disabled (takes effect at next start)");
            ModActivation.Queue(dir, id, "removed");
            Check("disabled removal is queued without deleting", File.Exists(files[0]) && ModActivation.Pending(dir, id));
            ModActivation.Apply(dir, id);
            Check("remove disabled preserves DLL backup", ModActivation.Files(dir, id).Count == 0 && File.ReadAllBytes(files[0] + ".removed").SequenceEqual(Bytes));
            ModActivation.Apply(dir, id);
            Check("install after removal is enabled", ModFiles.Install(dir, id, "3.0.0", Bytes, Array.Empty<string>()) == null && ModActivation.Status(dir, id) == "Enabled");
            ModActivation.Queue(dir, id, "disabled"); ModActivation.Apply(dir, id);
            Directory.Move(ModActivation.Folder(dir, id), ModActivation.Folder(dir, id) + ".missing");
            Check("missing DLL folder retains disabled receipt and explains missing data", ModActivation.Ids(dir).Contains(id) && ModActivation.Status(dir, id).Contains("missing") && Refuses(() => ModActivation.Queue(dir, id, "enabled")));
            foreach (string core in new[] { "tldrevamp.core", "TLDRevamp", "tldrevamp.multiplayer", "TLDRevamp.Preloader", "TLDLoader", "BepInEx", "../escape", ".", "bad/dir" })
                Check("core or unsafe id refused: " + core, Refuses(() => ModActivation.Queue(dir, core, "disabled")) && Refuses(() => ModFiles.Install(dir, core, "1.0", Bytes, Array.Empty<string>())));
            string coreFile = Path.Combine(dir, "TLDRevamp.dll"); File.WriteAllBytes(coreFile, Bytes);
            Check("update cannot set aside a core DLL passed as an old copy", Refuses(() => ModFiles.Install(dir, "other.mod", "1.0", Bytes, new[] { coreFile })) && File.ReadAllBytes(coreFile).SequenceEqual(Bytes));
            // A conflicting destination must preserve both copies and its journal for manual recovery.
            string other = "conflict.mod"; string active = ModFiles.Target(dir, other, "1.0"); File.WriteAllBytes(active, Bytes);
            ModActivation.Queue(dir, other, "disabled"); string conflict = Path.Combine(ModActivation.Folder(dir, other), Path.GetFileName(active)); File.WriteAllBytes(conflict, new byte[] { 9 });
            Check("destination collision never overwrites", Refuses(() => ModActivation.Apply(dir, other)) && File.ReadAllBytes(active).SequenceEqual(Bytes) && File.ReadAllBytes(conflict).SequenceEqual(new byte[] { 9 }) && ModActivation.Pending(dir, other));

            // The type loader's cache must not keep naming a mod that was just enabled or disabled.
            // BepInEx writes the cache with File.OpenWrite, which does not truncate, so a mod that was
            // cached while enabled leaves its path, GUID and type name in the file's bytes forever.
            // This runs in its own tree so it cannot disturb the state the other checks read.
            {
                string cacheRoot = Path.Combine(root, "cachetest");
                string cacheDir2 = Path.Combine(cacheRoot, "BepInEx", "plugins", "TLDRevampMods");
                string cacheFolder = Path.Combine(cacheRoot, "BepInEx", "cache");
                Directory.CreateDirectory(cacheDir2);
                Directory.CreateDirectory(cacheFolder);
                string cacheFile = Path.Combine(cacheFolder, "chainloader_typeloader.dat");
                string cacheDll = ModFiles.Target(cacheDir2, id, "1.0.0");
                File.WriteAllBytes(cacheDll, Bytes);
                Check("the BepInEx root of the managed folder is the BepInEx folder",
                    ModActivation.BepInExRoot(cacheDir2) == Path.Combine(cacheRoot, "BepInEx"));
                File.WriteAllBytes(cacheFile, new byte[] { 1, 0, 0, 0, 2, 0 });
                ModActivation.Queue(cacheDir2, id, "disabled");
                ModActivation.Apply(cacheDir2, id);
                Check("applying a queued action drops the type loader's stale cache", !File.Exists(cacheFile));
                Check("the mod's DLL was moved out of the plugin tree",
                    !File.Exists(cacheDll) && ModActivation.Files(cacheDir2, id).Count == 1);
                File.WriteAllBytes(cacheFile, new byte[] { 1, 0, 0, 0, 2, 0 });
                ModActivation.Queue(cacheDir2, id, "enabled");
                ModActivation.Apply(cacheDir2, id);
                Check("enabling a mod drops the type loader's cache as well", !File.Exists(cacheFile));
                Check("the mod's DLL is back in the plugin tree", File.Exists(cacheDll));
                File.WriteAllBytes(cacheFile, new byte[] { 1, 0, 0, 0, 2, 0 });
                ModActivation.Apply(cacheDir2, id);
                Check("an apply with nothing to move leaves the cache alone", File.Exists(cacheFile));
            }

            // The cache invalidation is a hard contract of its own: it happens before the first real move,
            // exactly once per apply that really moves something, it touches only this BepInEx's own cache
            // folder, it refuses a link without following it, it handles every entry on its own, and when it
            // fails it leaves every file un-moved and the queued intent in place so the next start retries.
            {
                string cacheRoot = Path.Combine(root, "cachetest2");
                string cacheDir2 = Path.Combine(cacheRoot, "BepInEx", "plugins", "TLDRevampMods");
                string cacheFolder = Path.Combine(cacheRoot, "BepInEx", "cache");
                Directory.CreateDirectory(cacheDir2);
                Directory.CreateDirectory(cacheFolder);
                string cacheFile = Path.Combine(cacheFolder, "chainloader_typeloader.dat");
                string cacheDll = ModFiles.Target(cacheDir2, id, "1.0.0");
                File.WriteAllBytes(cacheDll, Bytes);

                // Several matching entries are all invalidated in the one call, and files that do not match
                // are never touched.
                string otherCacheEntry = Path.Combine(cacheFolder, "other_typeloader.dat");
                string unrelated = Path.Combine(cacheFolder, "notes.txt");
                string otherCopy = Path.Combine(cacheRoot, "other", "BepInEx", "cache", "chainloader_typeloader.dat");
                Directory.CreateDirectory(Path.GetDirectoryName(otherCopy));
                File.WriteAllBytes(cacheFile, new byte[] { 1, 0, 0, 0, 2, 0 });
                File.WriteAllBytes(otherCacheEntry, new byte[] { 3, 0, 0, 0, 2, 0 });
                File.WriteAllBytes(unrelated, new byte[] { 4 });
                File.WriteAllBytes(otherCopy, new byte[] { 5 });
                ModActivation.Queue(cacheDir2, id, "disabled");
                ModActivation.Apply(cacheDir2, id);
                Check("every matching cache entry is invalidated",
                    !File.Exists(cacheFile) && !File.Exists(otherCacheEntry));
                Check("an unrelated file in the cache folder is left alone", File.Exists(unrelated));
                Check("another copy's cache is left alone", File.Exists(otherCopy));
                Check("the apply that moves the file is the one that invalidated the cache",
                    !File.Exists(cacheDll) && ModActivation.Files(cacheDir2, id).Count == 1 &&
                    !File.Exists(cacheFile) && !File.Exists(otherCacheEntry));

                // A link in the cache folder is refused, never followed: the file it points at is untouched,
                // the other entries are still invalidated, and the apply itself refuses to move anything.
                string linkRoot = Path.Combine(root, "cachetest3");
                string linkDir = Path.Combine(linkRoot, "BepInEx", "plugins", "TLDRevampMods");
                string linkFolder = Path.Combine(linkRoot, "BepInEx", "cache");
                Directory.CreateDirectory(linkDir);
                Directory.CreateDirectory(linkFolder);
                string linkTarget = Path.Combine(linkRoot, "outside.dat");
                File.WriteAllBytes(linkTarget, new byte[] { 7, 7, 7 });
                string link = Path.Combine(linkFolder, "chainloader_typeloader.dat");
                string sibling = Path.Combine(linkFolder, "sibling_typeloader.dat");
                File.WriteAllBytes(sibling, new byte[] { 8 });
                bool linked = false;
                try
                {
                    if (!OperatingSystem.IsWindows()) { File.CreateSymbolicLink(link, linkTarget); linked = true; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (PlatformNotSupportedException) { }
                string linkDll = ModFiles.Target(linkDir, id, "1.0.0");
                File.WriteAllBytes(linkDll, Bytes);
                ModActivation.Queue(linkDir, id, "disabled");
                if (linked)
                {
                    bool threw = false;
                    try { ModActivation.Apply(linkDir, id); }
                    catch (IOException) { threw = true; }
                    Check("a link in the cache folder refuses the invalidation", threw);
                    Check("the link's target is never modified", File.ReadAllBytes(linkTarget).SequenceEqual(new byte[] { 7, 7, 7 }));
                    Check("the link itself is not deleted through", File.Exists(link));
                    Check("the other cache entries are still invalidated", !File.Exists(sibling));
                    Check("a refused invalidation leaves the mod's files and its intent in place",
                        File.Exists(linkDll) && ModActivation.Desired(linkDir, id) == "disabled" && ModActivation.Pending(linkDir, id));
                    // Remove the link and the retry completes: the invalidation is retried, not given up on.
                    File.Delete(link);
                    ModActivation.Apply(linkDir, id);
                    Check("the retry after a refused invalidation applies the queued action",
                        !File.Exists(linkDll) && ModActivation.Files(linkDir, id).Count == 1 && !ModActivation.Pending(linkDir, id));
                }
                else
                {
                    Console.WriteLine("SKIP the link checks: this platform cannot make a link here");
                }

                // A cache folder that cannot be written to: the invalidation fails visibly, nothing is
                // moved, the intent survives, and the action is retried once the folder is writable again.
                if (!OperatingSystem.IsWindows() && ModeIsEnforced())
                {
                    string roRoot = Path.Combine(root, "cachetest4");
                    string roDir = Path.Combine(roRoot, "BepInEx", "plugins", "TLDRevampMods");
                    string roFolder = Path.Combine(roRoot, "BepInEx", "cache");
                    Directory.CreateDirectory(roDir);
                    Directory.CreateDirectory(roFolder);
                    string roCache = Path.Combine(roFolder, "chainloader_typeloader.dat");
                    File.WriteAllBytes(roCache, new byte[] { 1, 0, 0, 0, 2, 0 });
                    string roDll = ModFiles.Target(roDir, id, "1.0.0");
                    File.WriteAllBytes(roDll, Bytes);
                    ModActivation.Queue(roDir, id, "disabled");
                    File.SetUnixFileMode(roFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    try
                    {
                        bool threw = false; string message = "";
                        try { ModActivation.Apply(roDir, id); }
                        catch (IOException e) { threw = true; message = e.Message; }
                        Check("a read-only cache folder makes the invalidation fail with an IOException", threw);
                        Check("the failure says the action was preserved and will be retried",
                            message.Contains("preserved") && message.Contains("retried"));
                        Check("a failed invalidation moves nothing", File.Exists(roDll) && ModActivation.Files(roDir, id).Count == 1);
                        Check("a failed invalidation keeps the queued intent", ModActivation.Desired(roDir, id) == "disabled" && ModActivation.Pending(roDir, id));
                    }
                    finally
                    {
                        try { File.SetUnixFileMode(roFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    ModActivation.Apply(roDir, id);
                    Check("the retry after a failed invalidation applies the queued action",
                        !File.Exists(roDll) && !File.Exists(roCache) && ModActivation.Files(roDir, id).Count == 1 && !ModActivation.Pending(roDir, id));
                }
                else Console.WriteLine("SKIP the read-only cache folder checks: this platform has no Unix file modes, or the user bypasses the folder mode");
            }
            // The link guard covers the mod tree up to and including BepInEx, never the disk layout above it: under
            // Proton a mount point above the game (a separate /home, an SD card, the drive root) reads as a link.
            {
                string real = Path.Combine(root, "mountreal");
                Directory.CreateDirectory(Path.Combine(real, "game", "BepInEx", "plugins", "TLDRevampMods"));
                string above = Path.Combine(root, "mountlink");
                bool made = false;
                try { Directory.CreateSymbolicLink(above, real); made = true; } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (PlatformNotSupportedException) { }
                if (made)
                {
                    string viaLink = Path.Combine(above, "game", "BepInEx", "plugins", "TLDRevampMods");
                    bool threw = false;
                    try { ModActivation.Safe(viaLink); ModActivation.Ids(viaLink); } catch (IOException) { threw = true; }
                    Check("a link above the BepInEx folder (a mount point under Proton) is not refused", !threw);

                    string game2 = Path.Combine(root, "game2", "BepInEx");
                    Directory.CreateDirectory(game2);
                    Directory.CreateDirectory(Path.Combine(root, "innerreal", "TLDRevampMods"));
                    Directory.CreateSymbolicLink(Path.Combine(game2, "plugins"), Path.Combine(root, "innerreal"));
                    threw = false;
                    try { ModActivation.Safe(Path.Combine(game2, "plugins", "TLDRevampMods")); } catch (IOException) { threw = true; }
                    Check("a link inside the BepInEx folder is still refused", threw);

                    string game3 = Path.Combine(root, "game3");
                    Directory.CreateDirectory(game3);
                    Directory.CreateDirectory(Path.Combine(root, "bepreal", "plugins", "TLDRevampMods"));
                    Directory.CreateSymbolicLink(Path.Combine(game3, "BepInEx"), Path.Combine(root, "bepreal"));
                    threw = false;
                    try { ModActivation.Safe(Path.Combine(game3, "BepInEx", "plugins", "TLDRevampMods")); } catch (IOException) { threw = true; }
                    Check("a linked BepInEx folder itself is still refused", threw);
                }
                else Console.WriteLine("SKIP the mount-point link checks: this platform cannot make a link here");
            }
        }

        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return fails == 0 ? 0 : 1;
    }

    static void BrowserChecks(string root)
    {
        string dir = Path.Combine(root, "BepInEx", "plugins", "TLDRevampMods");
        Directory.CreateDirectory(dir); Directory.CreateDirectory(ModActivation.Store(dir));
        ModTransfer.InstallDir = dir;
        string odd = Path.Combine(dir, "Better Lights-1.0.dll"); File.WriteAllBytes(odd, Bytes);
        File.WriteAllBytes(ModFiles.Target(dir, "good.mod", "1.0"), Bytes);
        string broken = Path.Combine(ModActivation.Store(dir), "broken.mod.state"); File.WriteAllText(broken, "unreadable state");
        ModCatalog.Current = new ModCatalog.Catalog { Mods = new System.Collections.Generic.List<ModCatalog.Mod> {
            new ModCatalog.Mod { Id = "catalog.mod", Name = "Catalog mod" },
            new ModCatalog.Mod { Id = "broken.mod", Name = "Broken receipt" }
        } };
        BepInEx.Bootstrap.Chainloader.PluginInfos["better.lights"] = new BepInEx.Bootstrap.TestPlugin {
            Location = odd, Metadata = new BepInEx.Bootstrap.TestMetadata { GUID = "better.lights", Name = "Better Lights", Version = new Version(1, 0) }
        };
        var items = ModBrowser.Items();
        Check("browser retains catalog, managed and oddly named loaded mods", new[] { "catalog.mod", "good.mod", "better.lights" }.All(id => items.Any(m => m.Id == id)));
        Check("browser skips broken receipt and shows its error", !items.Any(m => m.Id == "broken.mod") && ModCatalog.Message.Contains("broken.mod") && ModCatalog.Message.Contains("Invalid state"));
        Check("join check returns a clear error for a broken receipt", ModBrowser.RequiredError("host.mod", "other hash") is string error && error.Contains("Could not check required mod 'host.mod'") && error.Contains("broken.mod"));
        File.Delete(broken);
        Check("join check continues past arbitrary DLL names", ModBrowser.RequiredError("host.mod", "other hash") == null);
        ModActivation.Queue(dir, "good.mod", "disabled");
        File.WriteAllText(broken, "unreadable state");
        Check("disabled hint survives an unrelated broken receipt", ModBrowser.HasDisabled());
        Check("required disabled mod still explains how to join", ModBrowser.RequiredError("good.mod", "other hash") is string disabled && disabled.Contains("enable them") && disabled.Contains("restart"));
        ModCatalog.Current = null; BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
    }
}

// Only the game-facing dependencies are stubbed; discovery, browser checks and catalog types above are production code.
namespace BepInEx.Bootstrap
{
    static class Chainloader { internal static readonly System.Collections.Generic.Dictionary<string, TestPlugin> PluginInfos = new System.Collections.Generic.Dictionary<string, TestPlugin>(); }
    sealed class TestPlugin { public TestMetadata Metadata = new TestMetadata(); public string Location = ""; }
    sealed class TestMetadata { public string GUID = "", Name = ""; public Version Version = new Version(1, 0); }
}
namespace TLDRevamp.Multiplayer
{
    static class MpApi { internal static readonly System.Collections.Generic.List<TestMod> Mods = new System.Collections.Generic.List<TestMod>(); }
    sealed class TestMod { public string Id = "", File = ""; }
}
namespace TLDRevamp
{
    static class Plugin { internal static readonly TestLog Log = new TestLog(); }
    sealed class TestLog { public void LogWarning(string message) => Console.WriteLine("WARN " + message); }
    static class Json { internal static string Str(string value) => System.Text.Json.JsonSerializer.Serialize(value); }
}
namespace TLDRevamp.Net
{
    public static partial class ModCatalog { internal static Catalog Current; internal static string Message; internal static bool RestartRequired; }
    static class ModTransfer { internal static string InstallDir; internal static string Sha256(byte[] bytes) => ModCatalog.Sha256(bytes); }
}
