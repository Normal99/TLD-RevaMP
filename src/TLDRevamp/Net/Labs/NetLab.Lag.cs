namespace TLDRevamp.Net
{
    public static partial class NetLab
    {
        /// Simulated bad internet for every Steam socket in this process (bridge `net lag <rttMs> [loss%] [jitter%] [jitterMs]`).
        /// Legacy RTT argument adds half that delay on sends only; receives remain clear.
        /// Utils003 approximates jitter with reorder delay; `net conditions` exposes the exact per-direction values.
        public static string Lag(int rttMs, float lossPct, float jitterPct, int jitterMs)
        {
            string result = NetworkConditions.Apply(new NetworkSimulation { Latency = rttMs / 2, Loss = lossPct, Reorder = jitterPct, Jitter = jitterMs, SendOnly = true });
            if (result.Contains("\"error\"")) return result;
            LagInfo = rttMs + " ms RTT, " + lossPct + " % loss, " + jitterPct + " % +" + jitterMs + " ms";
            return "{\"ok\":true,\"lag\":" + Json.Str(LagInfo) + "}";
        }
    }
}
