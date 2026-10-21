using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// What other players see of you: the game's own character model, animated by its IK targets (head, hands, feet,
    /// pelvis — relative to the body, like the official MP's playerAnim message) and dressed in your outfit.
    public static class PlayerLook
    {
        public const double PoseRangeM = 300; // limbs aren't visible further away: no pose traffic beyond this

        public sealed class Pose
        {
            public double Stamp, Clock;
            public uint Net, Epoch;
            public int Seat = -1;
            public Vector3 Body;
            public Quaternion Facing = Quaternion.identity;
            public readonly Vector3[] V = new Vector3[12]; // 6 local positions, 6 euler offsets (game's setPose order)
            public float ThRotX;
            public bool Zooming;
            public byte WheelHands;   // 1 left, 2 right: that driver's hand is placed by the copies (no data); 4 left, 8 right: on a usable
            public readonly HandControl[] Controls = new HandControl[2];   // the usable a placed hand is on (WheelHands 4/8)
            public byte Menus;
            public PlayerActs.Acts Acts;   // hum and pour (PlayerActs)
        }

        /// A hand on an object's usable (gear stick lever, knob, door handle): the usable's address and the spot in its frame.
        public struct HandControl { public uint Net; public int Item, Usable; public Vector3 Pos, Rot; }
        public struct DriverHandsOut { public byte Flags; public HandControl Left, Right; }

        // ---- pose: positions in mm (±32 m), euler offsets in 1/20° (±1638°), head pitch in 1/65536 turn
        private static void WP(NetWriter w, Vector3 v) { w.U16((ushort)Q(v.x * 1000f)); w.U16((ushort)Q(v.y * 1000f)); w.U16((ushort)Q(v.z * 1000f)); }
        private static void WR(NetWriter w, Vector3 v) { w.U16((ushort)Q(v.x * 20f)); w.U16((ushort)Q(v.y * 20f)); w.U16((ushort)Q(v.z * 20f)); }
        private static short Q(float f) => (short)Mathf.Clamp(Mathf.RoundToInt(f), short.MinValue, short.MaxValue);
        private static Vector3 RP(NetReader r) => new Vector3((short)r.U16() / 1000f, (short)r.U16() / 1000f, (short)r.U16() / 1000f);
        private static Vector3 RR(NetReader r) => new Vector3((short)r.U16() / 20f, (short)r.U16() / 20f, (short)r.U16() / 20f);

        /// The local player's pose, as the official playerAnim message would carry it.
        public static bool WriteLocalPose(NetWriter w)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var a = pl != null ? pl.anim : null;
            if (a == null || a.HHTarget == null) return false;
            var T = a.T != null ? a.T : a.transform;
            w.F64(Time.realtimeSinceStartupAsDouble);
            Entities.LocalSeat(out uint net, out int seat);
            if (net == 0) { Entities.LocalFooting(out var foot); net = foot.Net; seat = -1; }
            var frame = net != 0 ? seat >= 0 ? Entities.SeatTransform(net, seat) : Entities.FootRootOf(net) : null;
            if (frame == null || !Entities.RidingClock(net, out uint epoch, out double clock)) { net = 0; epoch = 0; clock = 0; }
            w.U32(net);
            if (net != 0)
            {
                w.U32(epoch); w.I32(seat); w.F64(clock);
                var body = frame.InverseTransformPoint(T.position);
                w.F32(body.x); w.F32(body.y); w.F32(body.z);
                WR(w, (Quaternion.Inverse(frame.rotation) * T.rotation).eulerAngles);
            }
            // A driver's hand on the wheel or a car control isn't sent: every copy puts it on its own wheel's grip and moves it
            // to its own gear stick, horn or handbrake when it applies that change (RemotePlayers.DriverHands). Sent, it trailed
            // both. Any other hand (radio, window, a held item) is sent.
            var hands = LocalDriverHands != null ? LocalDriverHands(pl, a) : default;
            byte wheel = hands.Flags;
            w.U8(wheel);
            if ((wheel & 4) != 0) WC(w, hands.Left);
            if ((wheel & 8) != 0) WC(w, hands.Right);
            WP(w, T.InverseTransformPoint(a.HHTarget.position));
            if ((wheel & 1) == 0) WP(w, T.InverseTransformPoint(a.LHTarget.position));
            if ((wheel & 2) == 0) WP(w, T.InverseTransformPoint(a.RHTarget.position));
            WP(w, T.InverseTransformPoint(a.LLTarget.position)); WP(w, T.InverseTransformPoint(a.RLTarget.position)); WP(w, T.InverseTransformPoint(a.Pelvis.position));
            var te = T.eulerAngles;
            Vector3 Rotation(Transform t) => net != 0 ? (Quaternion.Inverse(T.rotation) * t.rotation).eulerAngles : t.eulerAngles - te;
            WR(w, Rotation(a.HHTarget));
            if ((wheel & 1) == 0) WR(w, Rotation(a.LHTarget));
            if ((wheel & 2) == 0) WR(w, Rotation(a.RHTarget));
            WR(w, Rotation(a.LLTarget)); WR(w, Rotation(a.RLTarget)); WR(w, Rotation(a.Pelvis));
            w.U16((ushort)Mathf.RoundToInt(Mathf.Repeat(pl.Th.localEulerAngles.x, 360f) / 360f * 65535f));
            w.U8((byte)(pl.zooming ? 1 : 0));
            w.U8(mainscript.s.WhichMenusOpen());
            PlayerActs.Write(w, pl, T);
            return true;
        }

        public static Pose ReadPose(NetReader r)
        {
            var p = new Pose { Stamp = r.F64(), Net = r.U32() };
            if (p.Net != 0)
            {
                p.Epoch = r.U32(); p.Seat = r.I32(); p.Clock = r.F64();
                p.Body = new Vector3(r.F32(), r.F32(), r.F32()); p.Facing = Quaternion.Euler(RR(r));
                if (!Entities.Finite(p.Body) || p.Body.sqrMagnitude > 1e4f || double.IsNaN(p.Clock) || double.IsInfinity(p.Clock) || p.Seat < -1 || p.Seat > 255) return null;
            }
            p.WheelHands = (byte)(r.U8() & 15);
            if ((p.WheelHands & 4) != 0) p.Controls[0] = RC(r);
            if ((p.WheelHands & 8) != 0) p.Controls[1] = RC(r);
            for (int i = 0; i < 6; i++) if (!(i == 1 && (p.WheelHands & 1) != 0 || i == 2 && (p.WheelHands & 2) != 0)) p.V[i] = RP(r);
            for (int i = 6; i < 12; i++) if (!(i == 7 && (p.WheelHands & 1) != 0 || i == 8 && (p.WheelHands & 2) != 0)) p.V[i] = RR(r);
            p.ThRotX = r.U16() / 65535f * 360f;
            p.Zooming = (r.U8() & 1) != 0;
            p.Menus = r.U8();
            p.Acts = PlayerActs.Read(r);
            return r.Bad || double.IsNaN(p.Stamp) || double.IsInfinity(p.Stamp) ? null : p;
        }

        private static void HandsKept(playeranimationscript a, Pose p)
        {
            var T = a.T != null ? a.T : a.transform;
            if (a.LHTarget == null || a.RHTarget == null) return;
            if ((p.WheelHands & 1) != 0) { p.V[1] = T.InverseTransformPoint(a.LHTarget.position); p.V[7] = a.LHTarget.eulerAngles - T.eulerAngles; }
            if ((p.WheelHands & 2) != 0) { p.V[2] = T.InverseTransformPoint(a.RHTarget.position); p.V[8] = a.RHTarget.eulerAngles - T.eulerAngles; }
        }
        /// Set by the plugin: RemotePlayers.LocalDriverHands (game types this file's unit test doesn't stub).
        internal static System.Func<fpscontroller, playeranimationscript, DriverHandsOut> LocalDriverHands;
        private static void WC(NetWriter w, HandControl h) { w.VarU32(h.Net); w.VarU32((uint)h.Item); w.VarU32((uint)h.Usable); WP(w, h.Pos); WR(w, h.Rot); }
        private static HandControl RC(NetReader r) => new HandControl { Net = r.VarU32(), Item = (int)r.VarU32(), Usable = (int)r.VarU32(), Pos = RP(r), Rot = RR(r) };

        public static void Apply(mpplayerscript mp, Pose p)
        {
            if (mp == null || mp.anim == null) return;
            mp.zooming = p.Zooming;
            if (p.WheelHands != 0) HandsKept(mp.anim, p);   // until the seat is known here a wheel hand stays where it was
            mp.anim.setPose(p.V[0], p.V[1], p.V[2], p.V[3], p.V[4], p.V[5], p.V[6], p.V[7], p.V[8], p.V[9], p.V[10], p.V[11]);
            mp.anim.SetThRotX(p.ThRotX);
            if (mp.GMenus != null) mainscript.s.SetMenus(p.Menus, mp.GMenus);
            PlayerActs.Apply(mp, p.Acts);
        }

        /// Called immediately before IK, after the vehicle has moved. The game's Update otherwise leaves world-space
        /// targets from an earlier place on the bus, even though the model root is parented to the correct seat.
        internal static void ApplyRiding(mpplayerscript mp, Pose p, Transform frame)
        {
            var a = mp.anim;
            a.gotUpd = false;
            var at = frame.TransformPoint(p.Body); var rot = frame.rotation * p.Facing;
            void Target(Transform t, int i)
            {
                if (t != null) t.SetPositionAndRotation(at + rot * p.V[i], rot * Quaternion.Euler(p.V[i + 6]));
            }
            Target(a.HHTarget, 0); Target(a.LHTarget, 1); Target(a.RHTarget, 2);
            Target(a.LLTarget, 3); Target(a.RLTarget, 4); Target(a.Pelvis, 5);
            if (a.Th != null) { a.Th.position = a.HHTarget.position; a.Th.localRotation = Quaternion.Euler(p.ThRotX, 0, 0); }
            mp.zooming = p.Zooming;
        }

        // ---- outfit: the game's own GetData/SetData triple
        public static bool LocalOutfit(out int selected, out bool[] enabled, out Color[] colors)
        {
            selected = 0; enabled = null; colors = null;
            var o = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.outfit : null;
            if (o == null) return false;
            o.GetData(out selected, out enabled, out colors);
            return enabled != null && colors != null;
        }

        public static void WriteOutfit(NetWriter w, int selected, bool[] enabled, Color[] colors)
        {
            w.I32(selected);
            w.VarU32((uint)enabled.Length); foreach (var e in enabled) w.Bool(e);
            w.VarU32((uint)colors.Length); foreach (var c in colors) { w.F32(c.r); w.F32(c.g); w.F32(c.b); w.F32(c.a); }
        }

        public static bool ReadOutfit(NetReader r, out int selected, out bool[] enabled, out Color[] colors)
        {
            selected = r.I32();
            int n = (int)r.VarU32(); enabled = null; colors = null;
            if (r.Bad || n < 0 || n > r.Remaining) return false;
            enabled = new bool[n]; for (int i = 0; i < n; i++) enabled[i] = r.Bool();
            int m = (int)r.VarU32();
            // m * 16 overflowed: m = 2^28 passed as 0 and asked for a 4 GB array (an outfit is relayed to everyone)
            if (r.Bad || m < 0 || m > r.Remaining / 16) return false;
            colors = new Color[m]; for (int i = 0; i < m; i++) colors[i] = new Color(r.F32(), r.F32(), r.F32(), r.F32());
            return !r.Bad;
        }

        /// Cheap fingerprint to notice outfit changes.
        public static int OutfitHash(int selected, bool[] enabled, Color[] colors)
        {
            unchecked
            {
                int h = selected * 31;
                foreach (var e in enabled) h = h * 31 + (e ? 1 : 0);
                foreach (var c in colors) h = h * 31 + c.GetHashCode();
                return h;
            }
        }
    }
}
