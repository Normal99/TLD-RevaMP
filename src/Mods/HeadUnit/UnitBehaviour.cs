using System;
using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using TMPro;
using UnityEngine;

namespace HeadUnit
{
    /// On every head unit item (added to the model prefabs, so every spawned unit has it). Finds the car it sits in and
    /// that car's shared state (UnitState: the YouTube queue, what plays and where, its volume), and shows it.
    ///
    /// Each player picks their own tab (RADIO, MEDIA, NAV): the tab isn't shared, so the driver can look at the map
    /// while a passenger watches. The video plays on whatever tab is open, alongside the game's radio (both have their
    /// own volume and switch). Work is done only near the local player: a unit 2 km away plays nothing here.
    public sealed partial class UnitBehaviour : MonoBehaviour
    {
        public static readonly List<UnitBehaviour> All = new List<UnitBehaviour>();
        public static float ActiveRangeM = 40f;

        public Units.Model Model;
        public radioscript Radio;
        public carscript Car;                // null: not in a car's slot
        public SharedObject StateObj;
        public UnitState State => StateObj != null && StateObj.GameObject != null ? StateObj.GameObject.GetComponent<StateHolder>().State : null;

        public Touch Touch;
        public attachablescript Attachable;
        /// The radio body around the screen shown (Glue: hidden while stuck outside a slot).
        public bool BodyShown = true;
        internal readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        internal readonly List<Collider> TriggerColliders = new List<Collider>();
        private Video _video;
        private MapView _map;

        /// This player's view of the unit (not shared): the tab, the queue list, FULL, the next-bend hint.
        public Mode Tab = Mode.Radio;
        private bool _listOpen, _full, _hints = true;
        /// The queue list's first row when scrolled (UP / DOWN, the mouse wheel over the list); -1: around what plays.
        private int _listTop = -1;

        /// A question on this unit's screen (it replaces the tab's picture and buttons until answered).
        private enum Dialog { None, Link, Ytdlp, Results }
        /// A search's videos (Dialog.Results): tap one to add it; the ones added marked.
        private List<Ytdlp.Entry> _results = new List<Ytdlp.Entry>();
        private readonly HashSet<string> _resultsAdded = new HashSet<string>();
        private string _searched = ""; private int _resultsTop; private bool _searching;
        private Dialog _dialog;
        private string _link = "";
        /// The unit whose link is being typed (keys go to it, the game takes none: Controls.InputSelected).
        public static UnitBehaviour TypingInto;
        private float _savedVolume = 0.3f;
        private float _findAt, _glueAt;

        private void Awake()
        {
            var save = GetComponent<tosaveitemscript>();
            Model = save != null ? Units.ById(save.id) : null;
            Radio = GetComponentInChildren<radioscript>(true);   // radio01: on its child "Radiobj"
            Attachable = GetComponent<attachablescript>();
            var s = transform.Find(Look.ScreenName);
            if (s != null) { Touch = gameObject.AddComponent<Touch>(); Touch.Build(this, s); }
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() { All.Remove(this); _video?.Stop(); if (TypingInto == this) TypingInto = null; }
        private void OnDestroy() { _video?.Dispose(); _map?.Dispose(); if (TypingInto == this) TypingInto = null; }

        /// Has the car's power (the game's radio checks the same).
        private bool Powered => Radio == null || Radio.electronics == null || Radio.electronics.output > mainscript.lightTurnOffValue;

        private void Update()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            // stuck or not (a stick replayed from another machine, a loaded save): the body hidden or shown
            if (Time.realtimeSinceStartup >= _glueAt) { _glueAt = Time.realtimeSinceStartup + 0.5f; bool stuck = Glue.Stuck(this); if (stuck == BodyShown) Glue.Body(this, !stuck); }
            bool near = (pl.transform.position - transform.position).sqrMagnitude < ActiveRangeM * ActiveRangeM;
            if (!near) { _video?.Hold(); return; }

            if (Time.realtimeSinceStartup >= _findAt) { _findAt = Time.realtimeSinceStartup + 1f; Bind(); }
            var s = State;
            bool radioOn = Radio == null || Radio.ON;
            bool powered = Powered;

            // the video plays whatever this player looks at (a car without power plays nothing)
            if (s != null && Car != null && powered)
            {
                if (_video == null) _video = new Video(gameObject);
                _video.Follow(s, MpApi.SessionTime, Plugin.VideoVolume.Value, Ended);
            }
            else _video?.Hold();

            if (_full && (Tab != Mode.Video || _video == null || !_video.Ready)) _full = false;
            Buttons(s, powered, radioOn);
            if (Touch == null) return;
            Touch.Progress(-1f);
            if (!powered) { Show(Color.black, null, ""); return; }
            if (_dialog == Dialog.Results)
            {
                // one short line: two didn't fit the strip and showed nothing (headunit.py pictures, 2026-10-16)
                string head = Panel.Flashing ?? "tap a video to add it (blue: added)";
                if (head.Length > 44) head = head.Substring(0, 41).TrimEnd() + "...";
                Show(new Color(0.05f, 0.05f, 0.07f), null, head, top: true);
                return;
            }
            if (_dialog != Dialog.None) { Show(new Color(0.05f, 0.05f, 0.07f), null, DialogText()); Touch.Progress(Ytdlp.DownloadFrac); return; }
            if (Ytdlp.Downloading) { Show(Color.black, null, Ytdlp.Status); Touch.Progress(Ytdlp.DownloadFrac); return; }
            string hint = _hints && IsMyCar() && Nav.Events.Count > 0 && Nav.Events[0].Distance < 400f ? Nav.NextText() : null;
            string flash = Panel.Flashing;

            if (Tab == Mode.Video)
            {
                var cur = s?.Current;
                string overlay;
                if (_listOpen) overlay = s == null || s.Queue.Count == 0 ? "the queue is empty" : "";
                else if (cur == null) overlay = "queue empty: press + LINK to add a YouTube or Spotify link";
                else if (_video == null || _video.Problem == "loading...")
                {
                    float f = Ytdlp.VideoFrac(cur.VideoId), c = Ytdlp.ConvertFrac(cur.VideoId);
                    overlay = f >= 0f ? $"downloading the video {Mathf.RoundToInt(f * 100f)}%" : c >= 0f ? $"converting the video for this machine {Mathf.RoundToInt(c * 100f)}%" : "loading...";
                    Touch.Progress(f >= 0f ? f : c);
                }
                else overlay = _video.Problem ?? (s.Playing ? "" : "paused");
                if (!_listOpen && !_full && hint != null) overlay = hint + (overlay.Length > 0 ? "\n" + overlay : "");
                if (!_listOpen && flash != null) overlay = flash + (overlay.Length > 0 ? "\n" + overlay : "");
                bool pic = !_listOpen && _video != null && _video.Ready;
                Show(Color.black, pic ? _video.Texture : null, overlay, aspect: pic ? _video.Aspect : -1f);
                return;
            }
            if (Tab == Mode.Nav)
            {
                // the map: roads around the car, the road ahead in the unit's colour, heading up (redrawn every frame)
                if (_map == null) _map = new MapView(Model != null ? Model.Screen : new Color(0.3f, 0.7f, 1f));
                if (Car != null) _map.Draw(Car.transform, IsMyCar() ? Nav.Path : null);
                string t = (IsMyCar() ? Nav.NextText() : "") + "   " + MapView.ZoomText(_map.Zoom);
                Show(Color.black, _map.Texture, flash ?? t, top: true, aspect: MapView.Wpx / (float)MapView.Hpx);
                return;
            }
            // the station list fills the middle (Buttons); this line is above it: what plays, the volume
            string fr;
            if (!radioOn) fr = "radio off: press ON";
            else if (Radio == null) fr = "radio";
            else fr = (Radio.amfm ? $"AM {Radio.FR:0}" : $"FM {Radio.FR:0.0}") + "  " + (StationName(Radio) ?? "no station")
                + $"   vol {Mathf.RoundToInt((Radio.Bvolume != null ? Radio.Bvolume.tunedLerp : 0f) * 10f)}";
            var playing = s?.Current;
            if (flash != null) fr += "\n" + flash;
            else if (hint != null) fr += "\n" + hint;
            else if (playing != null && s.Playing) fr += "\nplaying: " + (playing.Title.Length > 40 ? playing.Title.Substring(0, 40) + "..." : playing.Title);
            Show(Model != null ? Model.Screen * 0.15f : Color.black, null, fr, top: radioOn && Radio != null);
        }

        /// The screen's buttons for this player's tab. YouTube presses are edits to the car's shared state (everyone in
        /// the car sees and hears them); the radio's turn the game's own knobs (the game shares those).
        private void Buttons(UnitState s, bool powered, bool radioOn)
        {
            if (Touch == null) return;
            var top = new List<(string, string, Action, bool)>
            {
                ("radio", "RADIO", () => SetTab(Mode.Radio), Tab == Mode.Radio),
                ("video", "MEDIA", () => SetTab(Mode.Video), Tab == Mode.Video),
                ("nav", "NAV", () => SetTab(Mode.Nav), Tab == Mode.Nav),
            };
            var bottom = new List<(string, string, Action, bool)>();
            List<(string, string, Action, bool)> list = null;
            int cols = 1;
            string key = Tab.ToString();
            if (!powered) { Touch.Layout("nopower", null, null); return; }
            if (_full)
            {
                Touch.Layout("full", new List<(string, string, Action, bool)> { ("unfull", "", () => _full = false, false) }, null, null, full: true);
                return;
            }
            if (_dialog == Dialog.Link)
            {
                key = "dialog-link";
                bool typing = TypingInto == this;
                bottom.Add(("paste", "PASTE", () => { _link = (GUIUtility.systemCopyBuffer ?? "").Trim(); }, false));
                bottom.Add(("type", typing ? "TYPING" : "TYPE", () => TypingInto = typing ? null : this, typing));
                bottom.Add(("del", "DEL", () => { if (_link.Length > 0) _link = _link.Substring(0, _link.Length - 1); }, false));
                bottom.Add(("add", IsLink(_link) || _link.Trim().Length == 0 ? "ADD" : "SEARCH", AddLink, false));
                bottom.Add(("cancel", "CANCEL", CloseDialog, false));
            }
            else if (_dialog == Dialog.Results)
            {
                key = "dialog-results";
                list = new List<(string, string, Action, bool)>();
                if (Touch.Looked == Touch && Touch.Wheel != 0) _resultsTop -= Touch.Wheel;
                _resultsTop = Mathf.Clamp(_resultsTop, 0, Mathf.Max(0, _results.Count - ResultRows));
                for (int i = _resultsTop; i < _results.Count && i < _resultsTop + ResultRows; i++)
                {
                    var e = _results[i];
                    bool added = _resultsAdded.Contains(e.Id);
                    string title = e.Title.Length > ResultChars ? e.Title.Substring(0, ResultChars - 3).TrimEnd() + "..." : e.Title;
                    list.Add(("r" + (i - _resultsTop), title + (e.Duration > 0 ? "  " + Clock(e.Duration) : ""), () =>
                    {
                        if (Controls.Send(this, new List<Ytdlp.Entry> { e }) == 1) { _resultsAdded.Add(e.Id); Panel.Flash("added: " + e.Title); }
                        else Panel.Flash("couldn't add: is the unit in a car's radio slot?");
                    }, added));
                }
                key += list.Count + "@" + _resultsTop + "+" + _resultsAdded.Count;
                bottom.Add(("up", "UP", () => _resultsTop -= ResultRows, false));
                bottom.Add(("down", "DOWN", () => _resultsTop += ResultRows, false));
                bottom.Add(("again", "SEARCH AGAIN", () => { _dialog = Dialog.Link; _link = _searched; }, false));
                bottom.Add(("done", "DONE", CloseDialog, false));
            }
            else if (_dialog == Dialog.Ytdlp)
            {
                key = "dialog-ytdlp";
                bottom.Add(("download", "DOWNLOAD", () => { CloseDialog(); Ytdlp.Download(); }, false));
                // without yt-dlp nothing plays; without ffmpeg only 360p: then on to the link
                bottom.Add(("notnow", "NOT NOW", () => { if (Ytdlp.Present) { Ytdlp.FfmpegDeclined = true; _dialog = Dialog.Link; _link = ""; } else CloseDialog(); }, false));
            }
            else if (Tab == Mode.Radio)
            {
                bottom.Add(("pwr", radioOn ? "OFF" : "ON", Power, radioOn));
                bottom.Add(("vol-", "VOL -", () => Knob(Radio?.Bvolume, -0.1f), false));
                bottom.Add(("vol+", "VOL +", () => Knob(Radio?.Bvolume, 0.1f), false));
                bottom.Add(("seek-", "<<", () => Seek(-1), false));
                bottom.Add(("seek+", ">>", () => Seek(1), false));
                bottom.Add(("amfm", Radio != null && Radio.amfm ? "AM" : "FM", () => Radio?.BChangeAMFM?.Turn(), false));
                if (radioOn && Radio != null)
                {
                    // every station of the band, always there: tap one to tune to it
                    bool am = Radio.amfm;
                    var on = Tuned(Radio);
                    var band = Stations.Band(am);
                    list = new List<(string, string, Action, bool)>();
                    for (int i = 0; i < band.Count; i++)
                    {
                        var ch = band[i];
                        list.Add(("st" + i, Stations.Freq(am, ch.FR) + "\n" + Stations.Name(am, ch, full: false), () => TuneTo(ch.FR), ch == on));
                    }
                    cols = list.Count > 6 ? 3 : 2;
                    key += (am ? "am" : "fm") + list.Count;
                }
            }
            else if (Tab == Mode.Video)
            {
                if (_listOpen && s != null)
                {
                    // the queue: tap one to play it
                    list = new List<(string, string, Action, bool)>();
                    int maxTop = Mathf.Max(0, s.Queue.Count - ListRows);
                    if (Touch.Looked == Touch && Touch.Wheel != 0) ScrollList(-Touch.Wheel, s);
                    int first = _listTop >= 0 ? Mathf.Min(_listTop, maxTop) : Mathf.Clamp(s.Index - 1, 0, maxTop);
                    for (int i = first; i < s.Queue.Count && i < first + ListRows; i++)
                    {
                        int at = i;
                        list.Add(("q" + (i - first), (i + 1) + ". " + s.Queue[i].Title, () => { Edit(w => { w.Byte((byte)Op.Jump); w.VarUInt((uint)at); }); }, i == s.Index));
                    }
                    key += "list" + list.Count + "@" + first;
                    bottom.Add(("up", "UP", () => ScrollList(-ListRows, State), false));
                    bottom.Add(("down", "DOWN", () => ScrollList(ListRows, State), false));
                    bottom.Add(("clear", "CLEAR", () => { if (Edit(w => w.Byte((byte)Op.Clear))) { _listOpen = false; Panel.Flash("queue cleared"); } else Panel.Flash("couldn't clear the queue"); }, false));
                    bottom.Add(("big", "BIG", () => Panel.Big = !Panel.Big, Panel.Big));
                    bottom.Add(("back", "BACK", () => { _listOpen = false; _listTop = -1; }, false));
                }
                else
                {
                    bottom.Add(("prev", "|<", () => Edit(w => w.Byte((byte)Op.Prev)), false));
                    bottom.Add(("play", s != null && s.Playing ? "PAUSE" : "PLAY", () => { var st = State; if (st != null) Edit(w => w.Byte((byte)(st.Playing ? Op.Pause : Op.Play))); }, false));
                    bottom.Add(("next", ">|", () => Edit(w => w.Byte((byte)Op.Next)), false));
                    bottom.Add(("vol-", "VOL -", () => VideoVolume(-0.1f), false));
                    bottom.Add(("vol+", "VOL +", () => VideoVolume(0.1f), false));
                    bottom.Add(("link", "+ ADD", () => { _dialog = Ytdlp.Present && !Ytdlp.OfferFfmpeg ? Dialog.Link : Dialog.Ytdlp; _link = ""; }, false));
                    bottom.Add(("list", "LIST", () => { _listOpen = true; _listTop = -1; }, false));
                    bottom.Add(("full", "FULL", () => { if (_video != null && _video.Ready) _full = true; }, false));
                }
            }
            else
            {
                bottom.Add(("zoom-", "ZOOM -", () => { if (_map != null) _map.ZoomBy(1); }, false));
                bottom.Add(("zoom+", "ZOOM +", () => { if (_map != null) _map.ZoomBy(-1); }, false));
                bottom.Add(("hints", _hints ? "HINTS: ON" : "HINTS: OFF", () => _hints = !_hints, _hints));
            }
            Touch.Layout(key, top, bottom, list, cols: cols, listTop: _dialog == Dialog.Results ? 0.2f : Tab == Mode.Radio ? 0.3f : 0f);
        }

        public static int ListRows = 5;
        /// Search results: fewer, taller rows than the queue (a title in two lines big enough to read from the seat).
        public static int ResultRows = 4, ResultChars = 52;

        /// The queue list moved by `rows` (from where it shows now).
        private void ScrollList(int rows, UnitState s)
        {
            if (s == null) return;
            int maxTop = Mathf.Max(0, s.Queue.Count - ListRows);
            int now = _listTop >= 0 ? Mathf.Min(_listTop, maxTop) : Mathf.Clamp(s.Index - 1, 0, maxTop);
            _listTop = Mathf.Clamp(now + rows, 0, maxTop);
        }

        private void SetTab(Mode m) { _listOpen = false; _full = false; CloseDialog(); Tab = m; }

        /// This player's video volume, by `d`: each player hears the car's video as loud as they set it (the config's
        /// Video.Volume, kept on this machine), so nobody turns it up or down for the others.
        private void VideoVolume(float d)
        {
            float v = Mathf.Round(Mathf.Clamp01(Plugin.VideoVolume.Value + d) * 10f) / 10f;
            Plugin.VideoVolume.Value = v;
            Panel.Flash($"video volume {Mathf.RoundToInt(v * 10f)}");
        }

        private string DialogText()
        {
            if (_dialog == Dialog.Ytdlp && Ytdlp.Present)
                return "Videos are 360p without ffmpeg. Download ffmpeg (about 80 MB, official build from GitHub, checked against its SHA-256) for "
                    + $"{Plugin.MaxHeight.Value}p picture and the best sound?";
            if (_dialog == Dialog.Ytdlp)
                return "YouTube needs yt-dlp, Deno (it solves YouTube's checks) and ffmpeg (joins HD picture and sound). Download the official releases from GitHub (about 140 MB)? Each is checked against its published SHA-256 before it is kept.";
            string shown = _link.Length > 48 ? "..." + _link.Substring(_link.Length - 45) : _link;
            string how = TypingInto == this ? "typing: Enter adds or searches, Esc stops typing" : "PASTE a copied link, or TYPE a link or words to search";
            if (_searching) how = "searching YouTube...";
            return "A YouTube or Spotify link (video, playlist, album, song), or words to search YouTube\n" + (shown.Length > 0 ? shown : "-") + (TypingInto == this ? "_" : "") + "\n" + how
                + (Panel.Flashing != null ? "\n" + Panel.Flashing : "");
        }

        private void CloseDialog() { _dialog = Dialog.None; if (TypingInto == this) TypingInto = null; }

        private static bool IsLink(string t) => Spotify.CleanLink(t) != null || Ytdlp.CleanLink(t) != null;
        private static string Clock(float s) => s >= 3600f ? $"{(int)(s / 3600)}:{(int)(s % 3600 / 60):00}:{(int)(s % 60):00}" : $"{(int)(s / 60)}:{(int)(s % 60):00}";

        /// The dialog's text: a link is added as before; anything else is searched on YouTube (Ytdlp.Search), the
        /// videos shown to pick from.
        private void AddLink()
        {
            string t = _link.Trim();
            if (t.Length == 0) { Panel.Flash("type or paste a link, or words to search"); return; }
            if (IsLink(t)) { CloseDialog(); Controls.AddLink(this, t); return; }
            if (_searching) return;
            if (TypingInto == this) TypingInto = null;
            _searching = true;
            Ytdlp.Search(t, list =>
            {
                _searching = false;
                if (_dialog != Dialog.Link) return;   // closed meanwhile
                if (list == null || list.Count == 0) { Panel.Flash(list == null ? Ytdlp.Status : "nothing found for that"); return; }
                _results = list; _resultsAdded.Clear(); _resultsTop = 0; _searched = t;
                _dialog = Dialog.Results;
            });
        }

        /// Tests (Probe.Search): the dialog's text set and ADD/SEARCH pressed; the results so far.
        public void TestSubmit(string text) { _dialog = Dialog.Link; _link = text; AddLink(); }
        public string TestResults() => _dialog != Dialog.Results ? (_searching ? "searching" : "dialog " + _dialog + " " + Panel.Flashing) : string.Join(" | ", _results.ConvertAll(e => e.Id + " " + e.Title + " " + Clock(e.Duration)));

        /// Keys for the unit whose link is being typed (once a frame, Controls).
        public static void TypeTick()
        {
            var u = TypingInto;
            if (u == null) return;
            if (u._dialog != Dialog.Link || !u.isActiveAndEnabled) { TypingInto = null; return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { TypingInto = null; return; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.V)) { u._link += (GUIUtility.systemCopyBuffer ?? "").Trim(); return; }
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (u._link.Length > 0) u._link = u._link.Substring(0, u._link.Length - 1); }
                else if (c == '\n' || c == '\r') { u.AddLink(); return; }
                else if (!char.IsControl(c) && u._link.Length < 300) u._link += c;
            }
        }

        /// The station the radio is on, by the game's own list (mainscript.radioChs / radioChsAM, each with its name
        /// and frequency), when it is close enough to be heard clearly; null between stations.
        public static string StationName(radioscript r)
        {
            var ch = Tuned(r);
            return ch != null ? Stations.Name(r.amfm, ch, full: true) : null;
        }

        /// The station of the game's list the radio is close enough to to be heard clearly; null between stations.
        public static mainscript.radioCH Tuned(radioscript r)
        {
            var chs = Channels(r);
            if (chs == null) return null;
            float best = float.MaxValue; mainscript.radioCH on = null;
            foreach (var ch in chs) { if (ch == null) continue; float d = Mathf.Abs(ch.FR - r.FR); if (d < best) { best = d; on = ch; } }
            float clear = (r.amfm ? mainscript.s.FRTAM : mainscript.s.FRTFM) * 0.5f;
            return best <= Mathf.Max(clear, (r.amfm ? r.AMStep : r.FMStep) * 0.5f) ? on : null;
        }

        private static List<mainscript.radioCH> Channels(radioscript r) =>
            r == null || mainscript.s == null ? null : r.amfm ? mainscript.s.radioChsAM : mainscript.s.radioChs;

        /// Seek: the next station up (dir 1) or down (-1) the band, round at the ends, as a car radio's << >>.
        private bool Seek(int dir)
        {
            var chs = Channels(Radio);
            if (chs == null || chs.Count == 0 || Radio.Btune == null) return false;
            float step = (Radio.amfm ? Radio.AMStep : Radio.FMStep) * 0.5f, cur = Radio.FR;
            float? pick = null;
            foreach (var ch in chs)
                if (dir > 0 ? ch.FR > cur + step && (pick == null || ch.FR < pick) : ch.FR < cur - step && (pick == null || ch.FR > pick)) pick = ch.FR;
            if (pick == null)   // round the band
                foreach (var ch in chs) if (pick == null || (dir > 0 ? ch.FR < pick : ch.FR > pick)) pick = ch.FR;
            TuneTo(pick.Value);
            return true;
        }

        /// The tuning knob turned so the radio lands on `fr` (the game's scroll-wheel call, so it syncs).
        private void TuneTo(float fr)
        {
            var u = Radio.Btune;
            float min = Radio.amfm ? Radio.AMmin : Radio.FMmin, max = Radio.amfm ? Radio.AMmax : Radio.FMmax;
            float target = Mathf.Lerp(u.tunedFloatMin, u.tunedFloatMax, Mathf.InverseLerp(min, max, fr));
            float speed = u.tuneSpeed == 0f ? 1f : u.tuneSpeed;
            u.Tuned((target - u.tempTunedFloat) / speed, syncinmulti: true);
        }

        /// Turn one of the radio's knobs by a share of its travel, as the mouse wheel does (the game syncs it).
        private static void Knob(usablescript u, float share)
        {
            if (u == null) return;
            float range = u.tunedFloatMax - u.tunedFloatMin, speed = u.tuneSpeed == 0f ? 1f : u.tuneSpeed;
            u.Tuned(range * share / speed, syncinmulti: true);
        }

        /// The radio is on while its volume is above zero: off remembers the volume, on brings it back.
        private void Power()
        {
            var v = Radio?.Bvolume;
            if (v == null) return;
            if (Radio.ON) { _savedVolume = Mathf.Max(0.1f, v.tunedLerp); Knob(v, -1f); }
            else Knob(v, _savedVolume - v.tunedLerp);
        }

        /// The car whose radio slot holds this unit, and that car's state object (if anyone made one).
        private void Bind()
        {
            Car = GetComponentInParent<carscript>();
            if (Car == null) { StateObj = null; return; }
            uint key = CarKey(Car);
            if (StateObj != null && !StateObj.Removed && State != null && State.Car == key) return;
            StateObj = FindState(key);
            if (StateObj != null || (key & 0x80000000u) != 0) return;
            // made alone (a local key) before this car got its session id: the owner moves it to the id
            var local = FindState(0x80000000u | (uint)(Car.GetInstanceID() & 0x7FFFFFFF));
            if (local != null && local.IsMine) { local.GameObject.GetComponent<StateHolder>().State.Car = key; local.RecordChanged(); StateObj = local; }
        }

        public bool IsMyCar()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return pl != null && Car != null && pl.seat != null && pl.seat.GetComponentInParent<carscript>() == Car;
        }

        /// The car as every machine knows it: its session id; alone, a local number (never sent).
        public static uint CarKey(carscript car)
        {
            if (car == null) return 0;
            uint id = MpApi.InSession ? MpApi.WorldObjectId(car.gameObject) : 0;
            return id != 0 ? id : 0x80000000u | (uint)(car.GetInstanceID() & 0x7FFFFFFF);
        }

        /// The car's state: the one with the lowest id when two players made one at the same moment (the other's owner
        /// removes its own).
        public static SharedObject FindState(uint carKey)
        {
            SharedObject best = null;
            foreach (var so in MpApi.Objects)
            {
                if (so.Removed || so.Type != Plugin.StateType || so.GameObject == null) continue;
                var st = so.GameObject.GetComponent<StateHolder>();
                if (st == null || st.State.Car != carKey) continue;
                if (best == null || Better(so, best)) best = so;
            }
            foreach (var so in new List<SharedObject>(MpApi.Objects))
            {
                if (so == best || so.Removed || so.Type != Plugin.StateType || so.GameObject == null || !so.IsMine || so.Id == 0 || best == null || best.Id == 0) continue;
                var st = so.GameObject.GetComponent<StateHolder>();
                if (st != null && st.State.Car == carKey) so.Remove();
            }
            return best;
        }

        private static bool Better(SharedObject a, SharedObject b)
        {
            if (a.Id != 0 && b.Id == 0) return true;
            if (a.Id == 0) return false;
            return a.Id < b.Id;
        }

        /// Send an edit to the car's state, making the state first if nobody has yet.
        public bool Edit(Action<RecordWriter> write)
        {
            if (Car == null) return false;
            if (StateObj == null || StateObj.Removed)
            {
                var go = new GameObject("HeadUnitState");
                DontDestroyOnLoad(go);
                var h = go.AddComponent<StateHolder>();
                h.State.Car = CarKey(Car);
                if (MpApi.InSession && MpApi.WorldObjectId(Car.gameObject) == 0) MpApi.ShareWorldObject(Car.gameObject);
                StateObj = MpApi.Share(Plugin.StateType, go);
            }
            return StateObj.RequestEdit(write);
        }

        private void Ended(int index) => Edit(w => { w.Byte((byte)Op.Ended); w.VarUInt((uint)index); });

        private string _lastText;
        private void Show(Color tint, Texture tex, string text, bool top = false, float aspect = -1f)
        {
            if (Touch == null) return;
            Touch.TextTop(top);
            Touch.Picture(tex != null ? aspect : -1f, _full);
            var m = Touch.ContentMat;
            if (m != null)
            {
                var want = tex != null ? tex : Look.Pixel(new Color(tint.r, tint.g, tint.b, 1f));   // Unlit/Texture has no tint
                if (m.mainTexture != want) m.mainTexture = want;
            }
            if (Touch.Text != null && text != _lastText) { Touch.Text.text = Touch.Readable(Touch.Text.font, text); _lastText = text; }
        }

        /// The videos that play or come next on the units near this player (the cache never deletes these).
        public string VideoDescribe => _video != null ? _video.Describe() : "no video";

        public static HashSet<string> VideosInUse()
        {
            var ids = new HashSet<string>();
            foreach (var u in All)
            {
                if (u == null) continue;
                if (u._video != null && u._video.VideoId != null) ids.Add(u._video.VideoId);
                if (u._video != null) foreach (var k in u._video.PooledIds()) ids.Add(k);
                var s = u.State;
                if (s == null) continue;
                for (int i = Mathf.Max(0, s.Index); i < s.Queue.Count && i <= s.Index + 2; i++) ids.Add(s.Queue[i].VideoId);
            }
            return ids;
        }

        public bool VideoReady => _video != null && _video.Ready;
        public string VideoProblem => _video?.Problem;
        public AudioSource VideoAudio => _video?.Audio;
        public Texture2D MapTexture => _map?.Texture;
        public bool VideoPlaying => _video != null && _video.Player != null && _video.Player.isPlaying;
        public Texture VideoTexture => _video != null && _video.Ready ? _video.Texture : null;

        internal static string Describe(NavEvent e)
        {
            string d = e.Distance < 1000f ? $"{Mathf.Round(e.Distance / 10f) * 10f:0} m" : $"{e.Distance / 1000f:0.0} km";
            switch (e.Kind)
            {
                case NavKind.BendLeft: return $"left {e.Amount:0} deg in {d}";
                case NavKind.BendRight: return $"right {e.Amount:0} deg in {d}";
                case NavKind.Descent: return $"downhill {e.Amount:0}% in {d}";
                case NavKind.Climb: return $"uphill {e.Amount:0}% in {d}";
                default: return $"{e.Note} in {d}";
            }
        }
    }
}
