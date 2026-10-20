using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Bootstrap;
using TLDRevamp.Multiplayer;

namespace TLDRevamp.Net
{
    internal static class ModBrowser
    {
        internal static List<ModCatalog.Mod> Items()
        {
            var result = new List<ModCatalog.Mod>();
            if (ModCatalog.Current != null) result.AddRange(ModCatalog.Current.Mods);
            foreach (string id in ModActivation.Ids(ModTransfer.InstallDir))
            {
                if (ModActivation.Core(id) || result.Exists(m => m.Id == id)) continue;
                ModActivation.Validate(id);
                var files = ModActivation.Files(ModTransfer.InstallDir, id);
                if (files.Count == 0 && ModActivation.Desired(ModTransfer.InstallDir, id) != "disabled") continue;
                string v = files.Count == 0 ? "unknown" : Version(files[0], id);
                result.Add(Local(id, id, v));
            }
            foreach (var p in Chainloader.PluginInfos.Values)
                if (!result.Exists(m => m.Id == p.Metadata.GUID)) result.Add(Local(p.Metadata.GUID, p.Metadata.Name, p.Metadata.Version.ToString()));
            return result;
        }
        private static ModCatalog.Mod Local(string id, string name, string version) => new ModCatalog.Mod {
            Id = id, Name = name, Description = "Local installation.", Versions = new List<ModCatalog.Release> { new ModCatalog.Release { Version = version } }
        };
        internal static string Version(string file, string id) => Path.GetFileNameWithoutExtension(file).Substring(id.Length + 1);
        private static bool Loaded(string id)
        {
            foreach (var p in Chainloader.PluginInfos.Values) if (p.Metadata.GUID == id) return true;
            return false;
        }
        internal static bool Pending(string id) => ModActivation.Pending(ModTransfer.InstallDir, id, Loaded(id));
        internal static string State(string id) => ModActivation.Status(ModTransfer.InstallDir, id, Loaded(id));
        internal static bool HasDisabled()
        {
            foreach (string id in ModActivation.Ids(ModTransfer.InstallDir))
                if (!ModActivation.Core(id) && ModActivation.Desired(ModTransfer.InstallDir, id) == "disabled") return true;
            return false;
        }
        internal static bool DisabledRequired(string id, string hash)
        {
            if (!ModActivation.Allowed(id)) return false;
            string managed = ModActivation.Resolve(ModTransfer.InstallDir, id);
            if (ModActivation.Desired(ModTransfer.InstallDir, managed) == "disabled") return true;
            foreach (string candidate in ModActivation.Ids(ModTransfer.InstallDir))
            {
                if (ModActivation.Core(candidate) || ModActivation.Desired(ModTransfer.InstallDir, candidate) != "disabled") continue;
                foreach (string file in ModActivation.Files(ModTransfer.InstallDir, candidate))
                    if (ModTransfer.Sha256(File.ReadAllBytes(file)) == hash) return true;
            }
            return false;
        }
        internal static void Action(string id, string action)
        {
            ModActivation.Validate(id);
            // A hand-installed copy would still load even after moving a managed copy.
            foreach (var p in Chainloader.PluginInfos.Values)
                if (p.Metadata.GUID == id && !ModFiles.Managed(ModTransfer.InstallDir, p.Location, id))
                    throw new IOException("Installed by hand: manage this mod's files outside the browser.");
            var files = ModActivation.Files(ModTransfer.InstallDir, id);
            foreach (var p in MpApi.Mods)
                if (!string.IsNullOrEmpty(p.File) && files.Exists(f => ModFiles.PathEq(f, p.File))) ModActivation.Alias(ModTransfer.InstallDir, id, p.Id);
            ModActivation.Queue(ModTransfer.InstallDir, id, action);
            ModCatalog.RestartRequired = true;
        }
        internal static string Bridge(string arg)
        {
            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && (parts[0] == "enable" || parts[0] == "disable" || parts[0] == "remove"))
            {
                Action(parts[1], parts[0] == "enable" ? "enabled" : parts[0] == "disable" ? "disabled" : "removed");
                return "{\"ok\":true}";
            }
            if (arg.Trim() != "list") return "{\"error\":\"mods list | enable|disable|remove <id>\"}";
            var sb = new StringBuilder("{\"mods\":["); bool comma = false;
            foreach (var m in Items())
            {
                if (comma) sb.Append(','); comma = true;
                bool core = ModActivation.Core(m.Id), loaded = false, registered = false;
                string version = null;
                foreach (var p in Chainloader.PluginInfos.Values)
                    if (p.Metadata.GUID == m.Id)
                    {
                        loaded = true; version = p.Metadata.Version.ToString();
                        foreach (var api in MpApi.Mods)
                            if (!string.IsNullOrEmpty(api.File) && ModFiles.PathEq(p.Location, api.File)) registered = true;
                    }
                var files = core ? new List<string>() : ModActivation.Files(ModTransfer.InstallDir, m.Id);
                foreach (var p in MpApi.Mods)
                    if (p.Id == m.Id || !string.IsNullOrEmpty(p.File) && files.Exists(f => ModFiles.PathEq(f, p.File))) registered = true;
                foreach (string file in files) version = Version(file, m.Id);
                string desired = core ? "enabled" : ModActivation.Desired(ModTransfer.InstallDir, m.Id);
                bool installed = files.Count > 0 || loaded;
                bool pending = !core && Pending(m.Id);
                sb.Append("{\"id\":").Append(Json.Str(m.Id)).Append(",\"version\":").Append(Json.Str(version))
                    .Append(",\"installed\":").Append(installed ? "true" : "false")
                    .Append(",\"enabled\":").Append(installed && desired == "enabled" ? "true" : "false")
                    .Append(",\"loaded\":").Append(loaded ? "true" : "false")
                    .Append(",\"registered\":").Append(registered ? "true" : "false")
                    .Append(",\"pending\":").Append(Json.Str(pending ? desired : null))
                    .Append(",\"state\":").Append(Json.Str(core ? "Protected core" : files.Count == 0 && loaded ? "Enabled (installed by hand)" : State(m.Id))).Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
