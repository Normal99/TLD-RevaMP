namespace TLDRevamp.Net
{
    internal static class HitchAuthority
    {
        internal static string Refusal(bool known, bool trailer, bool car, bool reach, bool stale, bool held, bool occupied, bool cycle)
        {
            if (!known) return "entity absent";
            if (!trailer || !car) return "not a trailer and car";
            if (!reach) return "edit reach";
            if (stale) return "stale epoch";
            if (held) return "held or stored";
            if (occupied) return "slot taken";
            if (cycle) return "already hitched";
            return null;
        }

        internal static bool CanJoin(int carOwner, int trailerOwner, int me, bool frozen, bool carProxy, bool trailerProxy) =>
            carOwner == trailerOwner && carOwner == me && !frozen && !carProxy && !trailerProxy;

        internal sealed class Transfer
        {
            internal uint Epoch, Token;
            internal int From, To;
            internal bool Frozen;
            internal float Until;
            internal bool Confirm(int from, uint epoch, uint token)
            {
                if (Frozen || from != From || epoch != Epoch || token != Token) return false;
                Frozen = true;
                return true;
            }
        }
    }
}
