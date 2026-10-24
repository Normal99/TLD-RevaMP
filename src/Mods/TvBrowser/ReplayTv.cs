using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TvBrowser
{
    /// The TVs in TLD Revamp's replays (a replay channel, API 4). While a replay records, each TV near a player records
    /// what it shows: a small picture of the page (half size, low JPEG quality, taken by the browser off the game's main
    /// thread) whenever the page changed, at most every RecordEvery seconds, and "off" when it goes off. A replay then
    /// shows those pictures on the TV's copy, at the moments they were taken. The address is not recorded, only the
    /// picture.
    ///
    /// A key: one byte (0: off or nothing showing; 1: a picture follows), then the JPEG.
    internal static class ReplayTv
    {
        public static float RecordEvery = 2f;
        public static int Recorded, Shown, Failed;
        private static readonly Dictionary<Renderer, (Material Mat, Texture2D Tex)> Copies = new Dictionary<Renderer, (Material, Texture2D)>();

        public static bool Playing => Plugin.Screen != null && Plugin.Screen.Playing;

        public static void Init(ReplayChannel ch)
        {
            ch.RecordingStarted += () => { foreach (var tv in TvScreen.All) { tv.RecordedAt = -100f; tv.RecordedFrame = -1; tv.RecordedOn = false; Record(tv, force: true); } };
            ch.Shown += Show;
            ch.PlaybackEnded += Clear;
        }

        /// Once a frame (Runner): each TV whose page changed since its last key records a new one.
        public static void Tick()
        {
            var ch = Plugin.Screen;
            if (ch == null || !ch.Recording) return;
            foreach (var tv in TvScreen.All) Record(tv, force: false);
        }

        private static void Record(TvScreen tv, bool force)
        {
            var ch = Plugin.Screen;
            if (tv == null || tv.Item == null) return;
            var tab = tv.Tab;
            bool showing = tab != null && tab.Ready && tab.Texture != null && tab.Frames > 0;
            if (!showing)
            {
                if (tv.RecordedOn || force) { if (ch.Record(tv.Item.gameObject, new byte[] { 0 })) Recorded++; tv.RecordedOn = false; tv.RecordedFrame = -1; }
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!force && (tab.Frames == tv.RecordedFrame || now - tv.RecordedAt < RecordEvery)) return;
            tv.RecordedAt = now; tv.RecordedFrame = tab.Frames;
            tab.Snapshot(jpeg =>
            {
                if (jpeg == null || jpeg.Length == 0) { Failed++; return; }
                var key = new byte[jpeg.Length + 1];
                key[0] = 1; System.Buffer.BlockCopy(jpeg, 0, key, 1, jpeg.Length);
                if (ch.Record(tv.Item.gameObject, key)) { Recorded++; tv.RecordedOn = true; } else Failed++;
            });
        }

        /// A TV's key at the playhead, onto its copy's screen: a material of its own on the copy's screen renderer
        /// (the game's TV and its material are left as they are).
        private static void Show(ReplayShown k)
        {
            if (k.Copy == null) return;
            var live = Screen(k.Live, k.Item);
            if (live == null || live.R == null) return;
            var copyR = k.CopyOf(live.R);
            if (copyR == null) return;
            Shown++;
            bool on = k.Data != null && k.Data.Length > 1 && k.Data[0] == 1;
            if (!Copies.TryGetValue(copyR, out var c))
            {
                var baseMat = live.defMaterial != null ? live.defMaterial : copyR.sharedMaterial;
                c = (new Material(baseMat) { name = "TvBrowserReplay" }, new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "TvBrowserReplay" });
                Copies[copyR] = c;
            }
            if (!on)
            {
                if (live.OffMaterial != null) copyR.sharedMaterial = live.OffMaterial;
                return;
            }
            var jpeg = new byte[k.Data.Length - 1];
            System.Buffer.BlockCopy(k.Data, 1, jpeg, 0, jpeg.Length);
            if (!c.Tex.LoadImage(jpeg, false)) return;
            c.Mat.SetTexture(TvScreen.EmissionMap, c.Tex);
            c.Mat.EnableKeyword("_EMISSION");
            copyR.sharedMaterial = c.Mat;
        }

        /// The display a key is about: the game's own TV when it is in this world, else the item's prefab.
        private static displayscript Screen(GameObject live, int item)
        {
            if (live != null) { var d = live.GetComponentInChildren<displayscript>(true); if (d != null) return d; }
            var db = itemdatabase.s;
            if (db == null || db.items == null || item < 0 || item >= db.items.Length || db.items[item] == null) return null;
            return db.items[item].GetComponentInChildren<displayscript>(true);
        }

        private static void Clear()
        {
            foreach (var c in Copies.Values) { if (c.Mat != null) Object.Destroy(c.Mat); if (c.Tex != null) Object.Destroy(c.Tex); }
            Copies.Clear();
        }
    }
}
