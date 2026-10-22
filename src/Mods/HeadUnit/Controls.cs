using System.Collections.Generic;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    /// The unit the local player sits with, the touch screen's crosshair and clicks, the road ahead for navigation.
    /// No keys: everything is on the unit's screen (Touch); typing opens only for a link (Panel).
    [HarmonyPatch]
    public static class Controls
    {
        /// The unit in the car the local player sits in, or null.
        public static UnitBehaviour Mine;

        public static void Tick()
        {
            bool menu = false;
            try { menu = mainscript.s != null && (mainscript.s.SomeMenuOpen() || mainscript.s.pauseMenuOpen); } catch { }
            Touch.Tick(!menu);   // no presses through the pause menu
            UnitBehaviour.TypeTick();
            Mine = null;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var car = pl != null && pl.seat != null ? pl.seat.GetComponentInParent<carscript>() : null;
            if (car != null) foreach (var u in UnitBehaviour.All) if (u != null && u.Car == car) { Mine = u; break; }
            if (Mine != null) Nav.Tick(car.transform, car.GetComponent<Rigidbody>());
            else if (Panel.Big) Panel.Big = false;
        }

        public static string Name(Mode m) => m == Mode.Video ? "media" : m == Mode.Nav ? "navigation" : "radio";

        /// A typed link: find its videos (yt-dlp; a Spotify link's songs found on YouTube Music: Spotify.cs), then add them to the queue,
        /// a few per edit (edits are capped at 1 KB).
        public static void AddLink(UnitBehaviour unit, string text)
        {
            string spotify = Spotify.CleanLink(text);
            if (spotify != null) { AddSpotify(unit, spotify); return; }
            string link = Ytdlp.CleanLink(text);
            if (link == null) { Panel.Flash("that isn't a YouTube or Spotify link"); return; }
            Panel.Flash("looking up the link...");
            Ytdlp.List(link, list =>
            {
                if (list == null || list.Count == 0) { Panel.Flash(Ytdlp.Status); return; }
                int sent = Send(unit, list);
                Panel.Flash(sent == 0 ? "couldn't add: is the unit in a car's radio slot?" : sent == 1 ? "added: " + list[0].Title : $"added {sent} videos");
            });
        }

        /// A Spotify link: its songs go into the queue as they're found on YouTube Music (the first at once, then five
        /// at a time), so the music starts while the rest are looked up.
        private static void AddSpotify(UnitBehaviour unit, string link)
        {
            Panel.Flash("Spotify: reading the songs...");
            var pending = new List<Ytdlp.Entry>();
            int sent = 0, seen = 0;
            Spotify.Find(link, e =>
            {
                pending.Add(e); seen++;
                if (seen == 1 || pending.Count >= 5) { sent += Send(unit, pending); pending.Clear(); Panel.Flash($"Spotify: added {sent} songs so far..."); }
            }, (total, why) =>
            {
                if (pending.Count > 0) { sent += Send(unit, pending); pending.Clear(); }
                if (why != null && sent == 0) Panel.Flash(why);
                else if (sent == 0) Panel.Flash(seen == 0 ? "Spotify: no songs found on YouTube Music" : "couldn't add: is the unit in a car's radio slot?");
                else Panel.Flash($"Spotify: added {sent}" + (total > 0 ? $" of {total}" : "") + " songs" + (why != null ? " (" + why + ")" : ""));
            });
        }

        /// Adds entries to the unit's queue, up to ten per edit and as many as fit its 1 KB (ten Japanese or Cyrillic
        /// titles were 1.4–2 KB: the edit was refused and nothing added); how many went.
        internal static int Send(UnitBehaviour unit, List<Ytdlp.Entry> list)
        {
            const int EditBytes = 1000;
            int sent = 0, i = 0;
            while (i < list.Count)
            {
                var b = new List<(string Id, string Title, float Duration)>();
                int bytes = 3;
                for (; i < list.Count && b.Count < 10; i++)
                {
                    var e = list[i];
                    string title = UnitState.Clip(e.Title != null && e.Title.Length > 60 ? e.Title.Substring(0, 60) : e.Title, UnitState.TitleBytes);
                    int size = 2 + (e.Id?.Length ?? 0) + 2 + System.Text.Encoding.UTF8.GetByteCount(title) + 4;
                    if (b.Count > 0 && bytes + size > EditBytes) break;
                    b.Add((e.Id, title, e.Duration)); bytes += size;
                }
                if (unit.Edit(w =>
                {
                    w.Byte((byte)Op.Add); w.VarUInt((uint)b.Count);
                    foreach (var e in b) { w.String(e.Id); w.String(e.Title); w.Float(e.Duration); }
                })) sent += b.Count;
            }
            return sent;
        }

        /// While a link is being typed on a unit, the game takes no keys (its own check for its text fields; TLD
        /// Revamp's chat does the same).
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected))]
        [HarmonyPostfix]
        private static void InputSelected(ref bool __result) { if (UnitBehaviour.TypingInto != null) __result = true; }
    }
}
