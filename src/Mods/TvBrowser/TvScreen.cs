using System;
using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TvBrowser
{
    /// On every screen the game has (displayscript: the TV "ujtv", a computer's monitor; added by a patch on its Start).
    /// While it is on, shows nothing of a computer's and is near the local player, it shows a page from this machine's
    /// browser, and keeps that page on the TV's shared state (TvState): the address, the scroll position, the keys the
    /// player at its keyboard presses.
    ///
    /// The pattern is the Head Unit's: one shared object per TV, keyed by the TV's id in the session, made by whoever
    /// first takes its keyboard; edits go to its owner, who checks them (TvEdits). Every machine fetches and draws the
    /// page itself: only the address, the scroll position and keys pass between players.
    public sealed class TvScreen : MonoBehaviour
    {
        public static readonly List<TvScreen> All = new List<TvScreen>();

        public displayscript Display;
        public tosaveitemscript Item;
        public SharedObject StateObj;
        public TvState State => StateObj != null && !StateObj.Removed && StateObj.GameObject != null ? StateObj.GameObject.GetComponent<TvHolder>()?.State : null;
        internal Tab Tab;

        private bool _applied;
        private float _findAt;
        private uint _lastKey;
        // following the shared page (not the driver)
        private bool _followed;
        private string _followedUrl = "";
        private uint _followedSeq;
        private float _scrollFixAt;
        // reporting it (the driver)
        private string _reportedUrl;
        private float _scrollSentAt;
        public int Navigations, ScrollFixes;
        /// The replay's view of this TV (ReplayTv): the last picture recorded, and when.
        internal float RecordedAt = -100f;
        internal int RecordedFrame = -1;
        internal bool RecordedOn;

        private void Awake()
        {
            Display = GetComponent<displayscript>();
            Item = GetComponentInParent<tosaveitemscript>();
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() { All.Remove(this); CloseTab(); Unapply(); if (Keyboard.Focused == this) Keyboard.Unfocus(); }
        private void OnDestroy() { CloseTab(); }

        /// On (its button), and not showing a computer.
        public bool On => Display != null && (Display.button == null || Display.button.currentTurnState == 1) && Display.computer == null;

        public float Distance
        {
            get
            {
                var pl = mainscript.s != null ? mainscript.s.player : null;
                return pl != null ? Vector3.Distance(pl.transform.position, transform.position) : float.MaxValue;
            }
        }

        private void Update()
        {
            if (mainscript.s == null || mainscript.s.player == null) return;
            if (ReplayTv.Playing) return;   // the replay draws its own copy of the TV; this one is hidden and stays as it is
            if (Time.realtimeSinceStartup >= _findAt) { _findAt = Time.realtimeSinceStartup + 1f; Bind(); }
            bool show = On && Distance < Plugin.ActiveRange.Value && Browser.Installed && Browser.Now != Browser.Phase.Failed;
            if (!show) { CloseTab(); Unapply(); return; }
            if (Tab == null) { Tab = Browser.Open(this); _followed = false; }
            if (!Tab.Ready) return;
            Apply();
            Sync();
        }

        // ------------------------------------------------------------------ the picture

        /// The page's picture on the screen's own material, where the game puts a computer's picture (its emission map).
        private void Apply()
        {
            if (Tab.Texture == null || Display.material == null) return;
            var m = Display.material;
            if (m.GetTexture(EmissionMap) != Tab.Texture)
            {
                m.SetTexture(EmissionMap, Tab.Texture);
                m.EnableKeyword("_EMISSION");
            }
            _applied = true;
        }

        private void Unapply()
        {
            if (!_applied) return;
            _applied = false;
            if (Display != null && Display.material != null && Display.computer == null) Display.material.SetTexture(EmissionMap, null);
        }

        internal static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");

        private void CloseTab()
        {
            if (Tab == null) return;
            Browser.Close(Tab);
            Tab = null; _followed = false;
        }

        internal void TabReady(Tab t) { if (t == Tab) _followed = false; }
        internal void TabLost(Tab t) { if (t == Tab) { Tab = null; _followed = false; Unapply(); } }

        // ------------------------------------------------------------------ keeping to the shared page

        public bool Driving => Keyboard.Focused == this && State != null && State.Driver == MpApi.MyPlayerId;

        private void Sync()
        {
            var s = State;
            float now = Time.realtimeSinceStartup;
            string url = s != null ? s.Url : "";
            uint seq = s != null ? s.NavSeq : 0;
            if (!Driving)
            {
                if (!_followed || seq != _followedSeq || url != _followedUrl)
                {
                    bool there = url.Length == 0 ? Tab.Home : !Tab.Home && Address.Clean(Tab.Url) == url;
                    // a new address, a reload, or a page this machine isn't on yet: go there (the same page twice, as
                    // when a link the keys clicked here too already took it there, isn't loaded again)
                    if (!_followed || seq != _followedSeq || !there) { Tab.Navigate(url); Navigations++; }
                    _followed = true; _followedUrl = url; _followedSeq = seq;
                    _scrollFixAt = now + 1.5f;
                }
                // the scroll position: the keys scroll every machine's page alike; where they differ (a page that
                // loaded later here, an image of another height), this one is put where the driver's is
                if (s != null && !Tab.Loading && now >= _scrollFixAt && now - Tab.InputAt > 1f)
                {
                    _scrollFixAt = now + 0.5f;
                    if (now - Tab.ScrollReadAt > 0.45f) Tab.ReadScroll();
                    else if (Mathf.Abs(Tab.ScrollY - s.ScrollY) > ScrollSlack || Mathf.Abs(Tab.ScrollX - s.ScrollX) > ScrollSlack)
                    { Tab.ScrollTo(s.ScrollX, s.ScrollY); ScrollFixes++; }
                }
                return;
            }
            _followed = true; _followedUrl = url; _followedSeq = seq;
            // the driver's page went somewhere by itself (a link, a form, a redirect, back): the others follow
            if (!Tab.Home && Tab.Url != _reportedUrl)
            {
                _reportedUrl = Tab.Url;
                string clean = Address.Clean(Tab.Url);
                if (clean != null && clean != url) Edit(w => { w.Byte((byte)Op.Url); w.String(clean); });
            }
            if (now - Tab.ScrollReadAt > 0.25f) Tab.ReadScroll();
            if (now - _scrollSentAt > 0.25f && (Mathf.Abs(Tab.ScrollX - s.ScrollX) >= 2f || Mathf.Abs(Tab.ScrollY - s.ScrollY) >= 2f))
            {
                _scrollSentAt = now;
                float x = Tab.ScrollX, y = Tab.ScrollY;
                Edit(w => { w.Byte((byte)Op.Scroll); w.Float(x); w.Float(y); });
            }
        }

        public static float ScrollSlack = 24f;

        /// The driver's address bar (or the start page, url ""): the shared page goes there, this machine's at once.
        public void Go(string url)
        {
            if (!Edit(w => { w.Byte((byte)Op.Navigate); w.String(url); })) return;
            Tab?.Navigate(url);
            _reportedUrl = Tab?.Url;
        }

        public void Reload()
        {
            if (Edit(w => w.Byte((byte)Op.Reload))) Tab?.Reload();
        }

        /// A page asked for a new window (a link with target=_blank, window.open): it opens here instead.
        internal void PopupOpened(Tab t, string url)
        {
            if (t != Tab) return;
            Tab.Navigate(url);
        }

        // ------------------------------------------------------------------ the shared state

        /// The TV as every machine knows it: its session id; alone, a local number (never sent).
        public uint Key()
        {
            var go = Item != null ? Item.gameObject : gameObject;
            uint id = MpApi.InSession ? MpApi.WorldObjectId(go) : 0;
            return id != 0 ? id : 0x80000000u | (uint)(go.GetInstanceID() & 0x7FFFFFFF);
        }

        private void Bind()
        {
            uint key = Key();
            if (StateObj != null && !StateObj.Removed && State != null && State.Tv == key) { _lastKey = key; return; }
            StateObj = FindState(key);
            // the TV's key changed under a state this machine owns (a session started or ended): the owner moves it
            if (StateObj == null && _lastKey != 0 && _lastKey != key)
            {
                var old = FindState(_lastKey);
                if (old != null && old.IsMine) { old.GameObject.GetComponent<TvHolder>().State.Tv = key; old.RecordChanged(); StateObj = old; }
            }
            if (StateObj != null) _lastKey = key;
            if (StateObj != null || (key & 0x80000000u) != 0) return;
            var go = Item != null ? Item.gameObject : gameObject;
            var local = FindState(0x80000000u | (uint)(go.GetInstanceID() & 0x7FFFFFFF));
            if (local != null && local.IsMine) { local.GameObject.GetComponent<TvHolder>().State.Tv = key; local.RecordChanged(); StateObj = local; _lastKey = key; }
        }

        /// A TV's state: the one with the lowest id when two players made one at the same moment (the other's owner
        /// removes its own).
        public static SharedObject FindState(uint key)
        {
            SharedObject best = null;
            foreach (var so in MpApi.Objects)
            {
                if (so.Removed || so.Type != Plugin.StateType || so.GameObject == null) continue;
                var h = so.GameObject.GetComponent<TvHolder>();
                if (h == null || h.State.Tv != key) continue;
                if (best == null || Better(so, best)) best = so;
            }
            if (best == null || best.Id == 0) return best;
            foreach (var so in new List<SharedObject>(MpApi.Objects))
            {
                if (so == best || so.Removed || so.Type != Plugin.StateType || so.GameObject == null || !so.IsMine || so.Id == 0) continue;
                var h = so.GameObject.GetComponent<TvHolder>();
                if (h != null && h.State.Tv == key) so.Remove();
            }
            return best;
        }

        private static bool Better(SharedObject a, SharedObject b)
        {
            if (a.Id != 0 && b.Id == 0) return true;
            if (a.Id == 0) return false;
            return a.Id < b.Id;
        }

        /// Sends an edit to the TV's state, making the state first if nobody has.
        public bool Edit(Action<RecordWriter> write)
        {
            if (StateObj == null || StateObj.Removed)
            {
                var go = Item != null ? Item.gameObject : gameObject;
                // the TV shared first, so the state carries its session id from the start
                if (MpApi.InSession && MpApi.WorldObjectId(go) == 0) MpApi.ShareWorldObject(go);
                var holder = new GameObject("TvBrowserState");
                DontDestroyOnLoad(holder);
                holder.AddComponent<TvHolder>().State.Tv = Key();
                StateObj = MpApi.Share(Plugin.StateType, holder);
                _lastKey = Key();
            }
            return StateObj.RequestEdit(write);
        }

        public string Describe()
        {
            var s = State;
            return $"{(Item != null ? Item.name : name)} on {On} dist {Distance:0.0} key {Key()} state {(s == null ? "none" : $"#{StateObj.Id} owner {StateObj.Owner} url '{s.Url}' seq {s.NavSeq} driver {s.Driver} scroll {s.ScrollX:0},{s.ScrollY:0}")}"
                + (Tab == null ? " tab none" : $" tab ready {Tab.Ready} url '{Tab.Url}' home {Tab.Home} loading {Tab.Loading} frames {Tab.Frames} scroll {Tab.ScrollX:0},{Tab.ScrollY:0} hints {Tab.Hints}")
                + $" applied {_applied} navigations {Navigations} scrollfixes {ScrollFixes} driving {Driving}";
        }
    }

    /// One shared object per TV in use: it carries the TvState. It has no body (an empty GameObject); the TV finds it
    /// by its key. Whoever first takes a TV's keyboard makes it; edits from everyone go to its owner.
    public sealed class TvStateType : SharedType
    {
        public override string Name => "tvstate";
        public override int MaxRecordBytes => 4096;
        public override int MaxEditBytes => 4096;   // an address is up to 2 KB

        public override GameObject Create(RecordReader record, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("TvBrowserState");
            var h = go.AddComponent<TvHolder>();
            if (!h.State.Read(record)) { UnityEngine.Object.Destroy(go); return null; }
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go;
        }

        public override void Write(GameObject obj, RecordWriter record) => obj.GetComponent<TvHolder>().State.Write(record);

        public override void Read(GameObject obj, RecordReader record)
        {
            var h = obj.GetComponent<TvHolder>();
            var copy = new TvState();
            if (copy.Read(record)) h.State = copy;
        }

        public override bool OnEdit(SharedObject obj, RecordReader edit, int fromPlayer) =>
            TvEdits.Apply(obj.GameObject.GetComponent<TvHolder>().State, edit, fromPlayer);

        /// The player at a TV's keyboard left: nobody has it (the owner, who may now be the host, lets it go).
        internal static void PlayerLeft(PlayerInfo p)
        {
            foreach (var so in MpApi.Objects)
            {
                if (so.Removed || !so.IsMine || so.Type != Plugin.StateType || so.GameObject == null) continue;
                var h = so.GameObject.GetComponent<TvHolder>();
                if (h != null && h.State.Driver == p.Id) { h.State.Driver = -1; so.RecordChanged(); }
            }
        }
    }

    /// Holds a TV's state on this machine (the shared object's GameObject).
    public sealed class TvHolder : MonoBehaviour
    {
        public TvState State = new TvState();
    }
}
