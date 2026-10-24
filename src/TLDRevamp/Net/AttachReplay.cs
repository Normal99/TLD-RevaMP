using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    /// Which kept attaches (an object's last AttachSync) a player gets when an object is sent to them
    /// (Entities.ReplayAttaches): the object's own, those of objects bolted onto it, and — with `parts` — those of its
    /// parts that came off. Each once, and only when the player knows what it is attached to (0 = the world), so its
    /// copy can attach there.
    ///
    /// The parts' own attaches were missing: an engine taken out of one car and put into another came back loose for a
    /// player who joined later, if the car it went into was sent before the car it came from (sent first, its bolted-on
    /// kid was a part this player didn't know yet; sent second, the engine's own car replayed only its own and its kids'
    /// attaches). mountedtraffic.py, 2026-10-24: after a rejoin, the joiner's engine in the host's car not in its slot.
    internal static class AttachReplay
    {
        internal static void Pick(uint self, bool selfKept, uint selfParent, IEnumerable<uint> kids, IEnumerable<uint> parts,
                                  Func<uint, bool> kept, Func<uint, uint> parentOf, Func<uint, bool> known, List<uint> into)
        {
            into.Clear();
            if (selfKept && (selfParent == 0 || known(selfParent))) into.Add(self);
            if (kids != null)
                foreach (var k in kids)
                    if (k != self && kept(k) && known(k) && !into.Contains(k)) into.Add(k);
            if (parts != null)
                foreach (var p in parts)
                {
                    if (p == self || !kept(p) || into.Contains(p)) continue;
                    uint pa = parentOf(p);
                    if (pa == 0 || known(pa)) into.Add(p);
                }
        }
    }
}
