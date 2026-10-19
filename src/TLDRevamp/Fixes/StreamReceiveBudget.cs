namespace TLDRevamp.Fixes
{
    /// Leave burst traffic in Steam's ordered queue instead of instantiating a whole arriving area in one frame.
    public static class StreamReceiveBudget
    {
        public static bool Enabled = false;   // opt-in until tested in game
        public static double BudgetMs = 2.0;
        public static long Deferred, Messages, MaxMessageUs;
    }
}
