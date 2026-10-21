using System;

namespace TLDRevamp
{
    internal static class StartupSplash
    {
        internal const string DataKey = "tldrevamp.core.StartupSplash.v1";
        internal const int Loading = 0, Patching = 1, Starting = 2, Waiting = 3, Finished = 4, Failed = 5;

        // Only framework types cross the assembly boundary; the plugin never references the preloader.
        internal static void Report(int stage, int completed = 0, int total = 0)
        {
            try { (AppDomain.CurrentDomain.GetData(DataKey) as Action<int, int, int>)?.Invoke(stage, completed, total); }
            catch { }
        }
    }
}
