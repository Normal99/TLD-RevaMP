using System.Text;
using UnityEngine;

namespace TLDRevamp.VR.Native
{
    /// The body others see, measured: every height in the chain from the feet to the head the network sends, relative
    /// to the body root, so a test can compare VR with flat play.
    internal static class VrBody
    {
        internal static string Json()
        {
            var pl = VrNative.Player;
            if (pl == null) return "{\"error\":\"no player\"}";
            var a = pl.anim;
            var T = a != null && a.T != null ? a.T : pl.transform;
            var b = new StringBuilder(512);
            b.Append("{\"root\":").Append(VrNative.V(T.position));
            void H(string name, Transform t)
            {
                if (t == null) return;
                b.Append(",\"").Append(name).Append("\":").Append(VrNative.F(T.InverseTransformPoint(t.position).y));
            }
            H("cam", pl.mainCam != null ? pl.mainCam.transform : null);
            H("th", pl.Th);
            H("headLean", pl.THeadLean);
            H("standHead", pl.TStandHeadPos);
            H("defStand", pl.TdefStandPos);
            H("proneHead", pl.TProneHeadPos);
            H("camParent", pl.CamParent);
            H("characterRoot", pl.CharacterRoot);
            if (a != null) { H("headTarget", a.HHTarget); H("pelvis", a.Pelvis); }
            if (pl.mainCam != null && pl.CamParent != null)
                b.Append(",\"camLocal\":").Append(VrNative.V(pl.CamParent.InverseTransformPoint(pl.mainCam.transform.position)));
            b.Append(",\"duckValue\":").Append(VrNative.F(pl.duckValue));
            b.Append(",\"duck\":").Append(pl.Bduck ? "true" : "false");
            b.Append(",\"duckToggle\":").Append(pl.input != null && pl.input.vrDuckToggle ? "true" : "false");
            b.Append(",\"sitting\":").Append(pl.Bsitting ? "true" : "false");
            b.Append(",\"camParentIsHeadLean\":").Append(pl.CamParent != null && pl.CamParent.parent == pl.THeadLean ? "true" : "false");
            b.Append('}');
            return b.ToString();
        }
    }
}
