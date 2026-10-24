using System.Collections.Generic;

namespace TvBrowser
{
    /// A keyboard command (Command) as the DevTools calls that put it into a page: key presses with their text, so
    /// the page sees keydown, keypress and input as from a real keyboard; the wheel in the middle of the page; back
    /// and forward through the page's own history. Pure: tools/unit/TvBrowserCoreTest.cs checks it, and
    /// tools/unit/TvBrowserPageTest (with a real headless Chromium) checks what the page makes of it.
    public static class PageInput
    {
        public static List<(string Method, Dictionary<string, object> Args)> Events(Command c, int width, int height)
        {
            var list = new List<(string, Dictionary<string, object>)>();
            switch (c.Kind)
            {
                case CmdKind.Text:
                    foreach (var ch in Split(c.Text ?? "")) Type(list, ch);
                    break;
                case CmdKind.Key:
                {
                    var k = Keys.Get(c.Code, c.Mods);
                    if (k == null) break;
                    var key = k.Value;
                    bool text = key.Text != null && (c.Mods & (Keys.Ctrl | Keys.Alt)) == 0;
                    var down = Json.O("type", text ? "keyDown" : "rawKeyDown", "key", key.Key, "code", key.CodeName,
                                      "windowsVirtualKeyCode", key.Code, "nativeVirtualKeyCode", key.Code, "modifiers", (int)c.Mods);
                    if (text) { down["text"] = key.Text; down["unmodifiedText"] = key.Text; }
                    list.Add(("Input.dispatchKeyEvent", down));
                    list.Add(("Input.dispatchKeyEvent", Json.O("type", "keyUp", "key", key.Key, "code", key.CodeName, "windowsVirtualKeyCode", key.Code,
                                                              "nativeVirtualKeyCode", key.Code, "modifiers", (int)c.Mods)));
                    break;
                }
                case CmdKind.Wheel:
                    list.Add(("Input.dispatchMouseEvent", Json.O("type", "mouseWheel", "x", width / 2, "y", height / 2, "deltaX", 0, "deltaY", c.Delta)));
                    break;
                case CmdKind.Back:
                    list.Add(("Runtime.evaluate", Json.O("expression", "history.back()")));
                    break;
                case CmdKind.Forward:
                    list.Add(("Runtime.evaluate", Json.O("expression", "history.forward()")));
                    break;
            }
            return list;
        }

        private static void Type(List<(string, Dictionary<string, object>)> list, string ch)
        {
            int code = ch.Length == 1 ? VirtualKey(ch[0]) : 0;
            var down = Json.O("type", "keyDown", "key", ch, "text", ch, "unmodifiedText", ch);
            if (code != 0) { down["windowsVirtualKeyCode"] = code; down["nativeVirtualKeyCode"] = code; }
            list.Add(("Input.dispatchKeyEvent", down));
            var up = Json.O("type", "keyUp", "key", ch);
            if (code != 0) { up["windowsVirtualKeyCode"] = code; up["nativeVirtualKeyCode"] = code; }
            list.Add(("Input.dispatchKeyEvent", up));
        }

        private static int VirtualKey(char c)
        {
            if (c >= 'a' && c <= 'z') return c - 32;
            if (c >= 'A' && c <= 'Z' || c >= '0' && c <= '9') return c;
            return 0;
        }

        /// Text into characters (a surrogate pair stays one).
        private static IEnumerable<string> Split(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) { yield return s.Substring(i, 2); i++; }
                else yield return s[i].ToString();
            }
        }
    }
}
