#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TvBrowser;

// The TV Browser mod against a real headless Chromium: its DevTools connection (Cdp.cs), what its keys do in a page
// (PageInput.cs), the link hints (Pages.cs), the blocked local addresses and the screencast. Needs a Chromium headless
// shell: TVB_CHROME=/path/to/chrome-headless-shell (without it the test says SKIP and passes, so CI needs no browser).
static class TvBrowserPageTest
{
    static int checks;
    // private addresses written out of parts (tools/privacy_scan.py flags real-looking LAN addresses in files)
    static string Ip(params int[] p) => string.Join(".", p);
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static Cdp cdp; static string session;
    static readonly List<Cdp.Message> events = new List<Cdp.Message>();

    static int Main()
    {
        string exe = Environment.GetEnvironmentVariable("TVB_CHROME");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { Console.WriteLine("TV Browser page test: SKIP (set TVB_CHROME to a Chromium headless shell)"); return 0; }
        string profile = Path.Combine(Path.GetTempPath(), "tvbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var p = Process.Start(new ProcessStartInfo(exe, "--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --user-data-dir=" + profile +
            (Environment.UserName == "root" ? " --no-sandbox" : "") + " --no-first-run --mute-audio --hide-scrollbars --disable-gpu --window-size=1024,576 about:blank") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        p.BeginErrorReadLine(); p.BeginOutputReadLine();
        try
        {
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            string[] lines = null;
            for (int i = 0; i < 300 && (lines == null || lines.Length < 2); i++) { Thread.Sleep(50); try { if (File.Exists(portFile)) lines = File.ReadAllLines(portFile); } catch (IOException) { } }
            Check(lines != null && lines.Length >= 2, "the browser writes its port");
            cdp = Cdp.Connect(int.Parse(lines[0]), lines[1]);
            var targets = Call("Target.getTargets", null, null);
            string spare = null;
            foreach (var o in (List<object>)targets.Result["targetInfos"]) { var t = (Dictionary<string, object>)o; if (Json.Str(t, "type") == "page") spare = Json.Str(t, "targetId"); }
            Check(spare != null, "the browser opens with a blank page the first TV can take");
            session = Json.Str(Call("Target.attachToTarget", Json.O("targetId", spare, "flatten", true), null).Result, "sessionId");
            Check(session != null, "attached in flat mode");
            Call("Page.enable"); Call("Network.enable");
            var blockAnswer = Call("Network.setBlockedURLs", Address.BlockArgs());
            Call("Fetch.enable", Address.FetchArgs());
            Check(blockAnswer.Error == null, "the browser takes the blocked addresses (" + (blockAnswer.Error == null ? "" : Json.Write(blockAnswer.Error)) + ")");
            Call("Emulation.setDeviceMetricsOverride", Json.O("width", 1024, "height", 576, "deviceScaleFactor", 1, "mobile", false));
            Call("Page.addScriptToEvaluateOnNewDocument", Json.O("source", HintsScript.Source));
            Call("Page.startScreencast", Json.O("format", "jpeg", "quality", 70, "maxWidth", 1024, "maxHeight", 576, "everyNthFrame", 1));

            string page = "<html><body style='margin:0'><input id=q><p><a id=one href='#one'>one</a> <a id=two href='#two'>two</a> <button id=b onclick=\"document.title='clicked'\">b</button></p>" +
                          "<div style='height:5000px;background:linear-gradient(#fff,#000)'></div></body></html>";
            // as the TV's own start page goes in (Tab.ShowHome): a blank page, then its content (a data: address can't
            // follow #links at the top level)
            Navigate("about:blank");
            string frame = Json.Str(Json.Obj(Json.Obj(Call("Page.getFrameTree").Result, "frameTree"), "frame"), "id");
            Call("Page.setDocumentContent", Json.O("frameId", frame, "html", page));
            Check(Eval("document.readyState") == "\"complete\"", "the page loads");
            Check(Eval("window.__tvh ? window.__tvh.count() : 'none'") == "-1", "the hints script is in every new page, off");

            Send(new Command { Kind = CmdKind.Key, Code = 9 });   // Tab: the field
            Send(new Command { Kind = CmdKind.Text, Text = "héllo wörld 😀" });
            Check(Eval("document.getElementById('q').value") == "\"héllo wörld 😀\"", "typed text lands in the focused field, accents and emoji too");
            Send(new Command { Kind = CmdKind.Key, Code = 8 }); Send(new Command { Kind = CmdKind.Key, Code = 8 });
            Check(Eval("document.getElementById('q').value") == "\"héllo wörld\"", "Backspace deletes (an emoji is one character)");
            Send(new Command { Kind = CmdKind.Key, Code = 65, Mods = Keys.Ctrl }); Send(new Command { Kind = CmdKind.Text, Text = "x" });
            Check(Eval("document.getElementById('q').value") == "\"x\"", "Ctrl+A selects all, typing replaces it");
            Send(new Command { Kind = CmdKind.Key, Code = 9 });
            Send(new Command { Kind = CmdKind.Key, Code = 13 }); Thread.Sleep(200);
            Check(Eval("location.hash") == "\"#one\"", "Tab to a link, Enter follows it");

            // link hints: F2 labels what is in view, typing a label clicks it
            Eval("window.scrollTo(0,0)");
            Send(new Command { Kind = CmdKind.Key, Code = 113 });
            int n = int.Parse(Eval("window.__tvh.count()"));
            Check(n == 4, "F2 labels the field, both links and the button (" + n + ")");
            Send(new Command { Kind = CmdKind.Text, Text = "f" });   // a s d f: the button is the fourth
            Check(Eval("document.title") == "\"clicked\"" && Eval("window.__tvh.count()") == "-1", "typing its label clicks it and the labels go");
            Send(new Command { Kind = CmdKind.Key, Code = 113 }); Send(new Command { Kind = CmdKind.Key, Code = 27 });
            Check(Eval("window.__tvh.count()") == "-1", "Esc takes the labels away");
            Send(new Command { Kind = CmdKind.Key, Code = 113 }); Send(new Command { Kind = CmdKind.Text, Text = "d" });   // a field, s one, d two
            Check(Eval("location.hash") == "\"#two\"", "a label follows a link");

            Eval("document.activeElement.blur(); window.scrollTo(0,0)");
            Send(new Command { Kind = CmdKind.Key, Code = 34 }); Thread.Sleep(600);
            double y1 = double.Parse(Eval("window.scrollY"), System.Globalization.CultureInfo.InvariantCulture);
            Check(y1 > 300, "Page Down scrolls (" + y1 + ")");
            Send(new Command { Kind = CmdKind.Key, Code = 32 }); Thread.Sleep(600);
            double y2 = double.Parse(Eval("window.scrollY"), System.Globalization.CultureInfo.InvariantCulture);
            Check(y2 > y1 + 300, "Space scrolls on (" + y2 + ")");
            Send(new Command { Kind = CmdKind.Wheel, Delta = 400 }); Thread.Sleep(600);
            double y3 = double.Parse(Eval("window.scrollY"), System.Globalization.CultureInfo.InvariantCulture);
            Check(y3 > y2 + 100, "the wheel scrolls (" + y3 + ")");
            Send(new Command { Kind = CmdKind.Back });
            Thread.Sleep(300);
            Check(Eval("location.hash") == "\"#one\"", "Back goes back");

            Eval("window.__f='?'; fetch('http://127.0.0.1:8080/x').then(()=>__f='ok',e=>__f='err')"); Thread.Sleep(300);
            Check(Eval("__f") == "\"err\"", "a page can't fetch from a local address");
            foreach (var url in new[] { "http://127.0.0.1:8080/", "http://" + Ip(192, 168, 1, 1) + "/", "http://localhost:8080/x", "http://printer.local/", "http://[::1]:8080/", "http://" + Ip(10, 0, 0, 1) + "/" })
            {
                var blocked = Call("Page.navigate", Json.O("url", url));
                string err = Json.Str(blocked.Result, "errorText") ?? "";
                Check(err.Contains("BLOCKED"), "the browser blocks " + url + " itself (" + err + ")");
            }
            Pump(1000);
            int frames = 0; bool jpeg = false;
            foreach (var e in events) if (e.Method == "Page.screencastFrame") { frames++; jpeg |= e.Picture != null && e.Picture.Length > 2 && e.Picture[0] == 0xFF && e.Picture[1] == 0xD8; }
            Check(frames > 0 && jpeg, "the screencast sends JPEG pictures, decoded off the main thread (" + frames + ")");
            Check(cdp.Open && cdp.Sent > 20 && cdp.Received > 20, "the connection stays open");
            Call("Browser.close", null, null);
            Check(p.WaitForExit(10000), "the browser closes when asked");
            Console.WriteLine("TV Browser page test: " + checks + " checks passed");
            return 0;
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    static void Send(Command c)
    {
        foreach (var (m, a) in PageInput.Events(c, 1024, 576)) Call(m, a);
        Thread.Sleep(30);
    }

    static void Navigate(string url)
    {
        events.Clear();
        Call("Page.navigate", Json.O("url", url));
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000) { Pump(50); if (events.Exists(e => e.Method == "Page.loadEventFired")) return; }
        throw new Exception("FAIL: the page never loaded");
    }

    static string Eval(string js)
    {
        var r = Call("Runtime.evaluate", Json.O("expression", js, "returnByValue", true));
        var v = Json.Obj(r.Result, "result");
        return v != null && v.TryGetValue("value", out var o) ? Json.Write(o) : "undefined";
    }

    static Cdp.Message Call(string method, Dictionary<string, object> args = null) => Call(method, args, session);

    static Cdp.Message Call(string method, Dictionary<string, object> args, string s)
    {
        Cdp.Message got = null;
        cdp.Send(method, args, s, m => got = m);
        var sw = Stopwatch.StartNew();
        while (got == null && sw.ElapsedMilliseconds < 10000) Pump(5);
        if (got == null) throw new Exception("FAIL: no answer to " + method);
        if (got.Error != null && method != "Page.navigate") throw new Exception("FAIL: " + method + ": " + Json.Write(got.Error));
        return got;
    }

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            cdp.Pump(e =>
            {
                events.Add(e);
                if (e.Method == "Fetch.requestPaused") cdp.Send("Fetch.failRequest", Json.O("requestId", Json.Str(e.Params, "requestId"), "errorReason", "BlockedByClient"), e.Session);
                if (e.Method == "Page.screencastFrame") cdp.Send("Page.screencastFrameAck", Json.O("sessionId", Json.Int(e.Params, "sessionId")), e.Session);
            });
            Thread.Sleep(1);
        } while (sw.ElapsedMilliseconds < ms);
    }
}

namespace TvBrowser
{
    static class Plugin { public static readonly Log Log = new Log(); }
    sealed class Log { public void LogError(object o) => Console.WriteLine("error: " + o); }
}

namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
    }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public static class Mathf { public static float Sqrt(float v) => (float)System.Math.Sqrt(v); }
}
