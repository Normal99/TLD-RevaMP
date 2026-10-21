// A minimal, real BepInEx plugin used as the fixture of the assembly-cache group of the evidence harness.
//
// It must be a plugin the *real* BepInEx chainloader will accept and cache, and every assembly it
// references must be resolvable where the harness points the resolver. So it references only BepInEx
// itself (which the harness always provides) and derives from BaseUnityPlugin, and it carries a real
// [BepInPlugin] attribute with a GUID, a name and a version.
//
// It deliberately does NOT reference the game, Unity or the multiplayer API: the cache group is about
// the *cache* (does an enabled mod's assembly get an entry, does a disabled mod's entry go away), and a
// fixture with fewer references is one fewer thing that can make the resolver fail for unrelated
// reasons. The reference-resolution prerequisites are what the harness must set up, not the fixture's
// business.
using BepInEx;

namespace TLDModEvidence.CachePlugin
{
    /// <summary>The plugin the chainloader finds, and whose assembly the type loader caches.</summary>
    [BepInPlugin("tldrevamp.cacheprobe", "Cache Probe", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Version = "1.0.0";

        private void Awake()
        {
            Logger.LogInfo("Cache probe loaded.");
        }
    }
}
