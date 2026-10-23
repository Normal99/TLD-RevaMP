using System;
using System.Collections.Generic;
using UnityEngine;

namespace TvBrowser
{
    /// One TV's page in the browser (a target of its own, attached in flat mode): where it is, what it looks like (the
    /// screencast, as a texture), and what goes into it (keys, the wheel, addresses). Main thread only.
    internal sealed class Tab
    {
        /// The page's size in CSS pixels. Fixed, the same on every machine, so a page lays out the same everywhere and
        /// the same keys and scroll positions land on the same things.
        public const int Width = 1024, Height = 576;

        public readonly TvScreen Screen;
        public string TargetId, Session;
        public bool Ready => Session != null;
        public bool Closed;

        public Texture2D Texture;
        public int Frames;                 // pictures shown so far
        public float FrameAt = -1f;        // when the last one was shown (realtime)
        public string Url = "";            // the main frame's address ("about:blank" on the start page)
        public bool Home = true;           // the TV's own start page is showing
        public bool Loading;
        public float NavigatedAt = -100f;  // when this machine last sent the page somewhere
        public float InputAt = -100f;      // when a key or the wheel last went in
        public float ScrollX, ScrollY;     // the last read of the page's position
        public float ScrollReadAt = -100f;
        public int Hints;                  // links labelled (the F2 hints) at the last read; -1 if off

        private byte[] _picture; private int _pictureSession; private bool _decoding;
        private string _homeHtml;
        private readonly List<string> _pendingScript = new List<string>();

        public Tab(TvScreen screen) { Screen = screen; }

        /// The page is made (Browser, once it runs): its size, the blocked addresses, the hints script, the pictures.
        internal void Create()
        {
            var cdp = Browser.Connection;
            if (cdp == null || Closed || TargetId != null) return;
            string spare = Browser.TakeSpare();
            if (spare != null) { TargetId = spare; Attach(cdp); return; }
            TargetId = "";   // asked for
            cdp.Send("Target.createTarget", Json.O("url", "about:blank", "width", Width, "height", Height), null, r =>
            {
                TargetId = Json.Str(r.Result, "targetId");
                if (TargetId == null) { Browser.Fail("the browser didn't make a page: " + Error(r)); return; }
                if (Closed) { Browser.CloseTarget(TargetId); return; }
                Attach(cdp);
            });
        }

        private void Attach(Cdp cdp)
        {
            cdp.Send("Target.attachToTarget", Json.O("targetId", TargetId, "flatten", true), null, a =>
            {
                if (Closed) { Browser.CloseTarget(TargetId); return; }
                Session = Json.Str(a.Result, "sessionId");
                if (Session == null) { Browser.Fail("the browser didn't attach to the page: " + Error(a)); return; }
                Setup();
            });
        }

        private void Setup()
        {
            Send("Page.enable");
            Send("Network.enable");
            if (Browser.UserAgent != null) Send("Emulation.setUserAgentOverride", Browser.UserAgentArgs());
            Send("Network.setBlockedURLs", Address.BlockArgs());
            Send("Fetch.enable", Address.FetchArgs());
            Send("Emulation.setDeviceMetricsOverride", Json.O("width", Width, "height", Height, "deviceScaleFactor", 1, "mobile", false));
            Send("Emulation.setDefaultBackgroundColorOverride", Json.O("color", Json.O("r", 255, "g", 255, "b", 255, "a", 1)));
            Send("Page.addScriptToEvaluateOnNewDocument", Json.O("source", HintsScript.Source));
            Send("Runtime.evaluate", Json.O("expression", HintsScript.Source));
            Send("Page.startScreencast", Json.O("format", "jpeg", "quality", Plugin.Quality.Value, "maxWidth", Width, "maxHeight", Height, "everyNthFrame", 1));
            Screen.TabReady(this);
        }

        internal int Send(string method, Dictionary<string, object> args = null, Action<Cdp.Message> reply = null) =>
            Session == null || Browser.Connection == null ? 0 : Browser.Connection.Send(method, args, Session, reply);

        internal static string Error(Cdp.Message m) => m.Error != null ? Json.Str(m.Error, "message") ?? "error" : "no answer";

        // ------------------------------------------------------------------ events

        internal void OnEvent(Cdp.Message m)
        {
            switch (m.Method)
            {
                case "Page.screencastFrame":
                    // the newest only: one waiting picture, the browser sends the next once this one is acknowledged
                    if (_picture != null && !_decoding) Ack();
                    _picture = m.Picture; _pictureSession = Json.Int(m.Params, "sessionId");
                    if (_picture == null) Ack();
                    break;
                case "Page.frameNavigated":
                {
                    var f = Json.Obj(m.Params, "frame");
                    if (f == null || Json.Str(f, "parentId") != null) break;
                    Url = Json.Str(f, "url") ?? "";
                    if (Url != "about:blank") Home = false;
                    Hints = -1;
                    break;
                }
                case "Page.navigatedWithinDocument":
                    Url = Json.Str(m.Params, "url") ?? Url;
                    break;
                case "Page.frameStartedLoading": Loading = true; break;
                case "Page.frameStoppedLoading": Loading = false; break;
                case "Page.loadEventFired": Loading = false; break;
                case "Fetch.requestPaused":
                    // only requests to local and private hosts are held (Address.FetchArgs): none of them goes on
                    Send("Fetch.failRequest", Json.O("requestId", Json.Str(m.Params, "requestId"), "errorReason", "BlockedByClient"));
                    break;
                case "Page.javascriptDialogOpening":
                    // an alert or a confirm would stop the page on every machine: dismissed at once, the same everywhere
                    Send("Page.handleJavaScriptDialog", Json.O("accept", false));
                    break;
            }
        }

        private void Ack()
        {
            if (_pictureSession != 0) Send("Page.screencastFrameAck", Json.O("sessionId", _pictureSession));
            _pictureSession = 0;
        }

        /// The waiting picture onto the texture, at most Plugin.Fps a second (the browser waits for the acknowledgement,
        /// so a TV that shows nothing new costs nothing).
        internal void Tick()
        {
            if (_picture == null) return;
            if (Time.realtimeSinceStartup - FrameAt < 1f / Mathf.Max(1, Plugin.Fps.Value)) return;
            if (Texture == null) { Texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "TvBrowserPage", wrapMode = TextureWrapMode.Clamp }; }
            _decoding = true;
            try { if (Texture.LoadImage(_picture, false)) { Frames++; FrameAt = Time.realtimeSinceStartup; } }
            finally { _decoding = false; }
            _picture = null;
            Ack();
        }

        // ------------------------------------------------------------------ going places

        public void Navigate(string url)
        {
            if (url == null) return;
            if (url.Length == 0) { ShowHome(); return; }
            Home = false; Loading = true; NavigatedAt = Time.realtimeSinceStartup; Hints = -1;
            Send("Page.navigate", Json.O("url", url, "transitionType", "typed"));
        }

        /// The TV's own start page: what it is and how to use it (no address: it never leaves this machine).
        public void ShowHome()
        {
            Home = true; Loading = false; NavigatedAt = Time.realtimeSinceStartup; Hints = -1;
            _homeHtml = _homeHtml ?? HomePage.Html(Plugin.FocusKey.Value.ToString());
            Send("Page.navigate", Json.O("url", "about:blank"), r =>
            {
                Send("Page.getFrameTree", null, t =>
                {
                    var frame = Json.Obj(Json.Obj(t.Result, "frameTree"), "frame");
                    string id = Json.Str(frame, "id");
                    if (id != null && Home) Send("Page.setDocumentContent", Json.O("frameId", id, "html", _homeHtml));
                });
            });
        }

        public void Reload() { if (Home) ShowHome(); else { Loading = true; NavigatedAt = Time.realtimeSinceStartup; Send("Page.reload"); } }

        public void ScrollTo(float x, float y)
        {
            Send("Runtime.evaluate", Json.O("expression", "window.scrollTo(" + Num(x) + "," + Num(y) + ")"));
            ScrollX = x; ScrollY = y;
        }

        /// Reads where the page is scrolled to (and whether link hints show); the answer lands in ScrollX / ScrollY.
        public void ReadScroll()
        {
            ScrollReadAt = Time.realtimeSinceStartup;
            Send("Runtime.evaluate", Json.O("expression", "[window.scrollX, window.scrollY, window.__tvh ? window.__tvh.count() : -1]", "returnByValue", true), r =>
            {
                var v = Json.Obj(r.Result, "result");
                if (v != null && v.TryGetValue("value", out var o) && o is List<object> a && a.Count == 3 && a[0] is double x && a[1] is double y && a[2] is double h)
                { ScrollX = (float)x; ScrollY = (float)y; Hints = (int)h; }
            });
        }

        /// A small picture of what the page shows now (for replays): half size, low quality, taken by the browser.
        public void Snapshot(Action<byte[]> got)
        {
            Send("Page.captureScreenshot", Json.O("format", "jpeg", "quality", 40, "fromSurface", true,
                "clip", Json.O("x", ScrollX, "y", ScrollY, "width", Width, "height", Height, "scale", 0.5)), r =>
            {
                string data = Json.Str(r.Result, "data");
                byte[] b = null;
                try { if (data != null) b = Convert.FromBase64String(data); } catch (FormatException) { }
                got(b);
            });
        }

        // ------------------------------------------------------------------ input

        /// One command from the keyboard (this player's, or another's at the same TV).
        public void Input(Command c)
        {
            InputAt = Time.realtimeSinceStartup;
            foreach (var (method, args) in PageInput.Events(c, Width, Height)) Send(method, args);
        }

        private static string Num(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "0" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        internal void Dispose()
        {
            Closed = true;
            if (!string.IsNullOrEmpty(TargetId)) Browser.CloseTarget(TargetId);
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = null; Session = null; _picture = null;
        }
    }
}
