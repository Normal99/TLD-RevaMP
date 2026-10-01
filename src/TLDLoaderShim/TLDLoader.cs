using System.IO;

// Just enough of TLDLoader's API for TLDLoader mods to run under BepInEx on the beta (where TLDLoader itself doesn't
// work). Only what mods reference is here: the Mod base class, its logger, the config folder. TLDRevamp's ModHost
// finds the mods, creates them and calls their lifecycle (OnMenuLoad in the menu, OnLoad once a game is loaded).
namespace TLDLoader
{
    public interface ILogger
    {
        void LogInfo(string msg);
        void LogWarning(string msg);
        void LogError(string msg);
    }

    /// The host's logger (BepInEx), handed over as three callbacks — TLDRevamp only reaches this assembly by reflection.
    public sealed class HostLogger : ILogger
    {
        private readonly System.Action<string> _info, _warn, _error;
        public HostLogger(System.Action<string> info, System.Action<string> warn, System.Action<string> error) { _info = info; _warn = warn; _error = error; }
        public void LogInfo(string msg) => _info?.Invoke(msg);
        public void LogWarning(string msg) => _warn?.Invoke(msg);
        public void LogError(string msg) => _error?.Invoke(msg);
    }

    public abstract class Mod
    {
        public virtual string ID => GetType().Name;
        public virtual string Name => ID;
        public virtual string Version => "";
        public virtual string Author => "";
        public virtual bool LoadInMenu => false;
        public virtual float ConfigScrollHeight => 0f;

        /// Set by the host before any lifecycle call.
        public ILogger Logger { get; set; }

        public virtual void OnMenuLoad() { }
        public virtual void OnLoad() { }
        public virtual void OnUnload() { }
        public virtual void Config() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void OnGUI() { }
    }

    public static class ModLoader
    {
        /// Set by the host: BepInEx/config/TLDLoaderMods.
        public static string ConfigRoot = "";

        public static string GetModConfigFolder(Mod mod)
        {
            string dir = Path.Combine(ConfigRoot, mod.ID);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
