using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// What one player may share. Each Share makes a new object on the server — kept, offered to everyone near, saved
    /// with the world — and nothing limited them: a player could send them in a loop until the host ran out of memory.
    /// Normal play shares in bursts: an area a player takes over (lease capture) brings its loot and parked cars at once
    /// (a few hundred), a crate breaking its gibs and loot (~15-20); a car's record is ~6 KB (`mp recsize`). The playtest
    /// of 2026-10-16/17 had 837 objects on the server after an hour with two players. Limits well above that:
    ///   a token bucket per player: ShareBurst at once, refilled ShareRefillPerS a second
    ///   ShareMaxRecordBytes per record, ShareMaxLive objects owned by one player
    /// Over a limit the share is dropped (the sharer keeps its item unshared) and counted; a player dropped more than
    /// ShareKickDrops times within a minute is kicked. The host's own shares are never limited.
    public static partial class Entities
    {
        public static bool ShareQuota = true;
        public static int ShareBurst = 600, ShareMaxLive = 4000, ShareMaxRecordBytes = 256 << 10, ShareKickDrops = 2000;
        public static float ShareRefillPerS = 30f;
        public static long SharesDropped, SharesKicked;

        private sealed class Bucket { public float Tokens, At; public int Drops; public float DropsSince; public int Live; public float LiveAt = -10f; }
        private static readonly Dictionary<int, Bucket> _shareBuckets = new Dictionary<int, Bucket>();
        private static readonly List<int> _kickQ = new List<int>();

        private static bool ShareAllowed(int from, int recordBytes)
        {
            if (!ShareQuota || from == 0) return true;
            float now = Time.realtimeSinceStartup;
            if (!_shareBuckets.TryGetValue(from, out var b)) _shareBuckets[from] = b = new Bucket { Tokens = ShareBurst, At = now, DropsSince = now };
            b.Tokens = Mathf.Min(ShareBurst, b.Tokens + (now - b.At) * ShareRefillPerS); b.At = now;
            string why = null;
            if (recordBytes > ShareMaxRecordBytes) why = "record of " + recordBytes + " bytes";
            else if (b.Tokens < 1f) why = "too many at once";
            else
            {
                if (now - b.LiveAt > 1f)
                {
                    b.LiveAt = now; b.Live = 0;
                    foreach (var se in Server.Values) if (se.OwnerId == from) b.Live++;
                }
                if (b.Live >= ShareMaxLive) why = "owns " + b.Live + " objects";
            }
            if (why == null) { b.Tokens -= 1f; b.Live++; return true; }
            SharesDropped++;
            if (now - b.DropsSince > 60f) { b.DropsSince = now; b.Drops = 0; }
            b.Drops++;
            if (b.Drops <= 3) Plugin.Log.LogWarning($"[anomaly] share refused: player {from}: {why}");
            if (b.Drops == ShareKickDrops) { _kickQ.Add(from); SharesKicked++; Plugin.Log.LogWarning($"[anomaly] player {from} kicked: {b.Drops} shares refused within a minute"); }
            return false;
        }

        /// Per frame (server): kicks decided while a message was being handled happen here, out of the receive loop.
        private static void ShareQuotaTick()
        {
            if (_kickQ.Count == 0) return;
            var q = _kickQ.ToArray(); _kickQ.Clear();
            if (Mp.Server == null) return;
            foreach (int id in q) Mp.Server.Kick(id);
        }

        internal static void ShareQuotaForget(int player) => _shareBuckets.Remove(player);

        public static string ShareQuotaStats() => "{\"dropped\":" + SharesDropped + ",\"kicked\":" + SharesKicked + ",\"players\":" + _shareBuckets.Count + "}";
    }
}
