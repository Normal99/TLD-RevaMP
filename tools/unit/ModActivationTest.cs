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
        }
        finally { Directory.Delete(root, true); }
        return fails == 0 ? 0 : 1;
    }
}
