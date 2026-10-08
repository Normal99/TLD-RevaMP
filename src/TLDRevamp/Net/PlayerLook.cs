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
            public readonly Vector3[] V = new Vector3[12]; // 6 local positions, 6 euler offsets (game's setPose order)
            public float ThRotX;
            public bool Zooming;
            public byte Menus;
            public PlayerActs.Acts Acts;   // hum and pour (PlayerActs)
        }

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
            WP(w, T.InverseTransformPoint(a.HHTarget.position)); WP(w, T.InverseTransformPoint(a.LHTarget.position));
            WP(w, T.InverseTransformPoint(a.RHTarget.position)); WP(w, T.InverseTransformPoint(a.LLTarget.position));
            WP(w, T.InverseTransformPoint(a.RLTarget.position)); WP(w, T.InverseTransformPoint(a.Pelvis.position));
            var te = T.eulerAngles;
            WR(w, a.HHTarget.eulerAngles - te); WR(w, a.LHTarget.eulerAngles - te); WR(w, a.RHTarget.eulerAngles - te);
            WR(w, a.LLTarget.eulerAngles - te); WR(w, a.RLTarget.eulerAngles - te); WR(w, a.Pelvis.eulerAngles - te);
            w.U16((ushort)Mathf.RoundToInt(Mathf.Repeat(pl.Th.localEulerAngles.x, 360f) / 360f * 65535f));
            w.U8((byte)(pl.zooming ? 1 : 0));
            w.U8(mainscript.s.WhichMenusOpen());
            PlayerActs.Write(w, pl, T);
            return true;
        }

        public static Pose ReadPose(NetReader r)
        {
            var p = new Pose();
            for (int i = 0; i < 6; i++) p.V[i] = RP(r);
            for (int i = 6; i < 12; i++) p.V[i] = RR(r);
            p.ThRotX = r.U16() / 65535f * 360f;
            p.Zooming = (r.U8() & 1) != 0;
            p.Menus = r.U8();
            p.Acts = PlayerActs.Read(r);
            return r.Bad ? null : p;
        }

        public static void Apply(mpplayerscript mp, Pose p)
        {
            if (mp == null || mp.anim == null) return;
            mp.zooming = p.Zooming;
            mp.anim.setPose(p.V[0], p.V[1], p.V[2], p.V[3], p.V[4], p.V[5], p.V[6], p.V[7], p.V[8], p.V[9], p.V[10], p.V[11]);
            mp.anim.SetThRotX(p.ThRotX);
            if (mp.GMenus != null) mainscript.s.SetMenus(p.Menus, mp.GMenus);
            PlayerActs.Apply(mp, p.Acts);
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
