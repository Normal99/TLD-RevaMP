using System;
using System.Globalization;

namespace TLDRevamp
{
    internal struct NetworkSimulation
    {
        public int Latency, Jitter;
        public float Loss, Duplication, Reorder;
        public bool SendOnly;
        public bool Active => Latency != 0 || !SendOnly && Jitter != 0 || Loss != 0 || Duplication != 0 || Reorder != 0;
        public bool Valid => Latency >= 0 && Latency <= 2000 && Jitter >= 0 && Jitter <= 2000 &&
            Percent(Loss) && Percent(Duplication) && Percent(Reorder);
        private static bool Percent(float n) => !float.IsNaN(n) && n >= 0 && n <= 100;
        public static NetworkSimulation Preset(int n)
        {
            switch (n)
            {
                case 1: return new NetworkSimulation { Latency = 120, Jitter = 15, Loss = 0.5f };
                case 2: return new NetworkSimulation { Latency = 60, Jitter = 40, Loss = 3 };
                case 3: return new NetworkSimulation { Latency = 200, Jitter = 60, Loss = 5 };
                default: return default;
            }
        }
        public string Json() => "{\"latencyMs\":" + Latency + ",\"jitterMs\":" + Jitter +
            ",\"lossPct\":" + Loss.ToString(CultureInfo.InvariantCulture) + ",\"duplicationPct\":" + Duplication.ToString(CultureInfo.InvariantCulture) +
            ",\"reorderPct\":" + Reorder.ToString(CultureInfo.InvariantCulture) + ",\"sendOnly\":" + (SendOnly ? "true" : "false") + "}";
    }
}
