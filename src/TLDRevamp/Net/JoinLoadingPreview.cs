namespace TLDRevamp.Net
{
    // Presentation-only fixture. The bridge is the sole entry point; never borrow live state or metrics.
    internal static class JoinLoadingPreview
    {
        internal static bool Active { get; private set; }
        internal static int Stage { get; private set; }
        internal static bool Question { get; private set; }
        internal static bool Stuck { get; private set; }
        internal static bool Expanded { get; private set; }
        internal const int Expected = 342, Applied = 128, Received = 154;
        internal const string Host = "Sample host", QuestionText = "SAMPLE PREVIEW · This host uses Example mod 1.2 (2.41 MB). Download the required mods to join? Nothing is downloaded in preview.";
        internal static readonly string[] Names = { "Car04", "Beer", "Box", "Barrel", "Radio02", "Jerrycan" };
        internal static readonly string[] Details = {
            "Sample host · direct · 34 ms", "RevaMP version accepted; mods matched",
            "Local scene loaded · 97 B from host", "Terrain 46 / 64 tiles · Roads 12 / 12 chunks",
            "Applied 128 / 342 · received 154 / 342 · waiting for initial records",
            "Waiting for settled ground", "Sample world ready" };
        internal static void Off() => Active = Question = Stuck = Expanded = false;
        internal static bool Command(string[] args)
        {
            if (args.Length == 3 && args[2] == "off") { Off(); return true; }
            if (args.Length < 3 || args.Length > 5 || !int.TryParse(args[2], out int stage) || stage < 0 || stage > 6) return false;
            int next = 3;
            bool question = next < args.Length && args[next] == "question", stuck = next < args.Length && args[next] == "stuck";
            if (question || stuck) next++;
            bool expanded = next < args.Length && args[next] == "expanded";
            if (expanded) next++;
            if (next != args.Length) return false;
            Active = true; Stage = stage;
            Question = question; Stuck = stuck; Expanded = expanded;
            return true;
        }
    }
    public static partial class JoinLoading
    {
        internal static string PreviewFromBridge(string[] args)
        {
            if (!Plugin.BridgeEnabled) return "{\"error\":\"Preview requires the test bridge\"}";
            if (Visible) return "{\"error\":\"Cannot preview during a join\"}";
            if (!JoinLoadingPreview.Command(args)) return "{\"error\":\"mp loading preview <0-6> [question|stuck] [expanded] | off\"}";
            Disclosure.Reset(JoinLoadingPreview.Expanded);
            _nextText = 0; _outerScroll = UnityEngine.Vector2.zero;
            return "{\"preview\":" + (JoinLoadingPreview.Active ? "true" : "false") + ",\"sprites\":" + JoinLoadingSkin.SpriteInfo() + "}";
        }
        internal static bool ToggleStep(int step)
        {
            bool hasDetail = step >= 0 && step < 7 && (JoinLoadingPreview.Active ? JoinLoadingPreview.Details[step].Length > 0 : step < 6);
            if (!Showing || !Disclosure.ToggleStep(step, hasDetail)) return false;
            _nextText = 0;
            return true;
        }
        internal static bool ToggleReceived()
        {
            if (!Showing || (JoinLoadingPreview.Active ? JoinLoadingPreview.Question : MpScreen.PendingQuestion != null)) return false;
            Disclosure.ToggleReceived(); _nextText = 0;
            return true;
        }
        internal static string ToggleFromBridge(string[] args)
        {
            if (!Plugin.BridgeEnabled) return "{\"error\":\"Toggle requires the test bridge\"}";
            bool toggled = args.Length == 3 && args[2] == "received" ? ToggleReceived() :
                args.Length == 4 && args[2] == "step" && int.TryParse(args[3], out int step) && ToggleStep(step);
            return toggled ? Probe() : "{\"error\":\"mp loading toggle step <0-6> | received (requires a visible disclosure)\"}";
        }
    }
}
