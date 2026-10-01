using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Approach B for car-to-car crashes (switch CrashAuthority; approach A = each machine simulates the crash itself,
    /// Entities.Physical): ONE machine simulates the whole crash. The machine whose own car first touches another
    /// player's physical copy (a real body with its real mass — Entities.Physical) takes that car over on the spot,
    /// keeping the crash's motion, and asks the server (CrashClaim). The server referees in arrival order: the first claim
    /// for a pair of cars wins; a later one (the other machine hitting "back" in its own simulation) is refused and that
    /// machine's copy returns. While a crash lease runs, normal driver-seat claims are refused. The hit player's machine
    /// shows its car as the crash machine simulates it; when the cars have separated or slowed (1–3 s) the crash machine
    /// hands it back (CrashRelease), and the server returns it anyway after 4 s. Both screens show the same crash.
    /// Costs: the hit player's inputs do nothing during the lease, and they see their car's crash one network delay late;
    /// the crash machine decides the crash (fairness: could be restricted to the host).
    public static partial class Entities
    {
        public const byte CrashClaim = 43, CrashRelease = 44;
        public static bool CrashAuthority = true;       // default since v0.57.67 (the user's call after the A/B and relay runs); false = approach A
        public static float CrashMinRelMps = 4f;        // softer contacts (shoving, touching) stay with approach A
        public static float CrashMinHoldS = 1.0f, CrashMaxHoldS = 3.0f, CrashLeaseS = 4.0f;
        public static long CrashClaims, CrashGranted, CrashRefused, CrashReleases, CrashAutoReturned;
        public static string LastCrash = "";

        private sealed class HeldCrash { public Ent E; public Transform Partner; public float Since, CalmSince = -1f; }
        private static readonly List<HeldCrash> _held = new List<HeldCrash>();

        /// CopyContact: a hard contact between a physical copy and a car simulated here.
        internal static bool TryTakeCrash(Ent e, Rigidbody mine, float relSpeed)
        {
            if (!CrashAuthority || !e.Proxy || relSpeed < CrashMinRelMps || e.CrashTaking) return false;
            foreach (var h in _held) if (h.E == e) return false;
            NetOf(mine.transform.root, out uint partnerNet, out bool partnerProxy);
            if (partnerProxy) return false;
            e.CrashTaking = true;
            // simulated here from this instant, with the crash's motion as it is (no reset to the copy's last velocity)
            SetProxy(e, false, keepMotion: true);
            // the owner's driver keeps pressing what they pressed: nobody drives it here, so the owner's last wheel torques
            // (brake, a seated player's automatic handbrake hold, throttle) stay on it — without them the hit car rolled
            // free and was pushed 9–12 m/s where single player gives 2–3 (crashtest A/B, v0.57.64)
            if (e.Root.car != null) PhysicalCars[e.Root.car] = e;
            _held.Add(new HeldCrash { E = e, Partner = mine.transform.root, Since = Time.time });
            W.Reset(); W.U8(CrashClaim); W.U32(e.NetId); W.U32(partnerNet);
            ToServer(W, true);
            CrashClaims++;
            LastCrash = "took net " + e.NetId + " (hit by " + mine.transform.root.name + " at " + relSpeed.ToString("F1") + " m/s)";
            return true;
        }

        /// FixedTick: hand crashes back once the cars have separated or slowed.
        internal static void CrashHoldTick()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                var h = _held[i]; var e = h.E;
                if (e.Proxy || e.Root == null) { EndHeld(e); _held.RemoveAt(i); continue; }   // refused / lost
                float t = Time.time;
                var rb = e.Root.GetComponent<Rigidbody>();
                var prb = h.Partner != null ? h.Partner.GetComponent<Rigidbody>() : null;
                bool calm = rb == null || prb == null ||
                            ((rb.velocity - prb.velocity).magnitude < 1.5f && ((rb.position - prb.position).magnitude > 6f || (rb.velocity.magnitude < 2f && prb.velocity.magnitude < 2f)));
                if (calm) { if (h.CalmSince < 0f) h.CalmSince = t; } else h.CalmSince = -1f;
                bool done = t - h.Since > CrashMaxHoldS || (t - h.Since > CrashMinHoldS && h.CalmSince >= 0f && t - h.CalmSince > 0.3f);
                if (!done) continue;
                W.Reset(); W.U8(CrashRelease); W.U32(e.NetId);
                ToServer(W, true);
                CrashReleases++;
                EndHeld(e);
                _held.RemoveAt(i);
            }
        }

        private static void EndHeld(Ent e)
        {
            e.CrashTaking = false;
            if (e.Root != null && e.Root.car != null && PhysicalCars.TryGetValue(e.Root.car, out var pe) && pe == e && !e.Physical) PhysicalCars.Remove(e.Root.car);
        }

        // ------------------------------------------------------------------ server (host)

        internal static void ServerCrashClaim(int from, uint net, uint partnerNet)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            bool refuse = se.OwnerId == from ? false
                        : se.CrashReturnTo >= 0                                                   // already in someone's crash
                          || (partnerNet != 0 && Server.TryGetValue(partnerNet, out var pe) && pe.CrashReturnTo == from);   // the other machine claimed first
            if (refuse)
            {
                CrashRefused++;
                W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                ServerSendTo(from, W, true);
                // the owner too (no change for it): it sends a fresh state. The claimer dropped the owner's states while it
                // held the car provisionally — a car that came to rest meanwhile sends nothing more, and the claimer's copy
                // stayed where its own brief simulation left it (3.9 m off, relay head-on, v0.57.65)
                ServerSendTo(se.OwnerId, W, true);
                return;
            }
            if (se.OwnerId == from) return;
            se.CrashReturnTo = se.OwnerId; se.CrashUntil = Time.realtimeSinceStartup + CrashLeaseS;
            se.OwnerId = from; se.Epoch++;
            CrashGranted++;
            W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)from); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
        }

        internal static void ServerCrashRelease(int from, uint net)
        {
            if (!Server.TryGetValue(net, out var se) || se.OwnerId != from || se.CrashReturnTo < 0) return;
            ReturnCrash(se);
        }

        private static void ReturnCrash(SEnt se)
        {
            se.OwnerId = se.CrashReturnTo; se.CrashReturnTo = -1; se.Epoch++;
            W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
        }

        /// Host, per frame: a crash lease nobody handed back (the crash machine left, a lost release) returns anyway.
        internal static void ServerCrashTick()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var se in Server.Values)
                if (se.CrashReturnTo >= 0 && now > se.CrashUntil) { ReturnCrash(se); CrashAutoReturned++; }
        }

        internal static bool InCrashLease(uint net) => Server.TryGetValue(net, out var se) && se.CrashReturnTo >= 0;

        public static string CrashAuthorityStatus() =>
            "{\"enabled\":" + (CrashAuthority ? "true" : "false") + ",\"claims\":" + CrashClaims + ",\"granted\":" + CrashGranted + ",\"refused\":" + CrashRefused +
            ",\"releases\":" + CrashReleases + ",\"autoReturned\":" + CrashAutoReturned + ",\"held\":" + _held.Count + ",\"last\":" + Json.Str(LastCrash) + "}";
    }
}
