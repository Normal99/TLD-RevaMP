using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using TLDRevamp.Net;

namespace TLDRevamp.Preloader
{
    public static class ModActions
    {
        public static IEnumerable<string> TargetDLLs => Array.Empty<string>();
        public static void Patch(AssemblyDefinition assembly) { }
        public static void Initialize()
        {
            StartupSplashWindow.Start();
            try { Apply(); }
            catch { StartupSplash.Report(StartupSplash.Failed); throw; }
        }

        static void Apply()
        {
            var log = Logger.CreateLogSource("RevaMP mod actions");
            string dir = Path.Combine(Paths.PluginPath, "TLDRevampMods");
            try
            {
                foreach (string id in ModActivation.Ids(dir))
                    try { ModActivation.Apply(dir, id); }
                    catch (Exception) { StartupSplash.Report(StartupSplash.Failed); log.LogError("A queued mod action could not finish; its files and intent were preserved. Check the mod browser."); }
            }
            catch (Exception) { StartupSplash.Report(StartupSplash.Failed); log.LogError("Mod actions could not be read; no files were deleted."); }
        }
    }
}
