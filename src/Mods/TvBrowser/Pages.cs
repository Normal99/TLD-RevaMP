namespace TvBrowser
{
    /// Link hints (F2 on a focused TV): every link, button and field in view gets a one- or two-letter label; typing
    /// the letters clicks it (a field gets the keyboard). Keyboard-only browsing without a mouse pointer.
    ///
    /// It runs in the page and takes its keys from the page's own key events, so the keys every machine sends into its
    /// own copy of the page label and click the same things in the same order (pages lay out the same: Tab.Width).
    internal static class HintsScript
    {
        public const string Source = @"(function(){
if (window.__tvh) return;
var L = 'asdfghjkl';
var st = { on: false, typed: '', items: [], box: null };
function shown(el) {
  var r = el.getBoundingClientRect();
  if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) return null;
  var cs = getComputedStyle(el);
  if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity === 0) return null;
  return r;
}
function labels(n) {
  var out = [];
  if (n <= L.length) { for (var i = 0; i < n; i++) out.push(L[i]); return out; }
  for (var a = 0; a < L.length && out.length < n; a++) for (var b = 0; b < L.length && out.length < n; b++) out.push(L[a] + L[b]);
  return out;
}
function stop() { if (st.box && st.box.parentNode) st.box.parentNode.removeChild(st.box); st = { on: false, typed: '', items: [], box: null }; }
function start() {
  stop();
  var els = document.querySelectorAll('a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=tab],[onclick],label[for]');
  var items = [];
  for (var i = 0; i < els.length && items.length < 81; i++) { var r = shown(els[i]); if (r) items.push({ el: els[i], r: r }); }
  var ls = labels(items.length);
  var box = document.createElement('div');
  box.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none';
  items.forEach(function (it, i) {
    it.label = ls[i];
    var d = document.createElement('div');
    d.textContent = ls[i].toUpperCase();
    d.style.cssText = 'position:fixed;left:' + Math.max(0, it.r.left) + 'px;top:' + Math.max(0, it.r.top) + 'px;background:#ffd84d;color:#000;font:bold 14px monospace;padding:1px 4px;border:1px solid #a07000;border-radius:3px;box-shadow:0 1px 3px rgba(0,0,0,.5)';
    it.div = d; box.appendChild(d);
  });
  (document.body || document.documentElement).appendChild(box);
  st = { on: true, typed: '', items: items, box: box };
}
function pick(c) {
  st.typed += c.toLowerCase();
  var left = st.items.filter(function (it) { return it.label.indexOf(st.typed) === 0; });
  if (left.length === 0) { stop(); return; }
  st.items.forEach(function (it) { it.div.style.display = it.label.indexOf(st.typed) === 0 ? '' : 'none'; });
  if (left.length === 1 && left[0].label === st.typed) {
    var el = left[0].el; stop();
    var field = el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || (el.tagName === 'INPUT' && !/^(button|submit|reset|checkbox|radio|image|file)$/i.test(el.type));
    el.focus();
    if (!field) el.click();
  }
}
window.addEventListener('keydown', function (e) {
  if (e.key === 'F2') { e.preventDefault(); e.stopImmediatePropagation(); if (st.on) stop(); else start(); return; }
  if (!st.on) return;
  e.preventDefault(); e.stopImmediatePropagation();
  if (e.key && e.key.length === 1 && L.indexOf(e.key.toLowerCase()) >= 0) pick(e.key); else stop();
}, true);
window.addEventListener('keypress', function (e) { if (st.on) { e.preventDefault(); e.stopImmediatePropagation(); } }, true);
window.addEventListener('scroll', function () { if (st.on) stop(); }, true);
window.__tvh = { count: function () { return st.on ? st.items.length : -1; } };
})();";
    }

    /// The TV's own start page (no address: shown from this machine, never fetched).
    internal static class HomePage
    {
        public static string Html(string focusKey) => @"<!doctype html><html><head><meta charset='utf-8'><title>TV</title><style>
body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle at 30% 20%,#2b3a55,#0d1117 70%);color:#e6edf3;font:18px/1.45 system-ui,'Segoe UI',sans-serif}
main{max-width:760px;padding:24px 32px}
h1{font-size:40px;margin:0 0 6px;letter-spacing:.5px}
p{margin:4px 0 14px;color:#9fb0c3}
table{border-collapse:collapse}
td{padding:4px 18px 4px 0;vertical-align:top}
kbd{display:inline-block;min-width:16px;padding:1px 7px;border:1px solid #58677a;border-bottom-width:3px;border-radius:5px;background:#1c2533;color:#fff;font:15px monospace;text-align:center}
</style></head><body><main>
<h1>TV Browser</h1>
<p>Everyone near this TV sees the same page. Whoever has the keyboard drives it.</p>
<table>
<tr><td><kbd>" + System.Net.WebUtility.HtmlEncode(focusKey) + @"</kbd></td><td>look at the TV and press it to take the keyboard; <kbd>Esc</kbd> gives it back</td></tr>
<tr><td><kbd>Ctrl</kbd> <kbd>L</kbd></td><td>type an address, or words to search</td></tr>
<tr><td><kbd>F2</kbd></td><td>label the links in view, then type a label to open one</td></tr>
<tr><td><kbd>Tab</kbd> <kbd>Enter</kbd></td><td>move between links and fields, open or send</td></tr>
<tr><td><kbd>&uarr;</kbd> <kbd>&darr;</kbd> <kbd>PgUp</kbd> <kbd>PgDn</kbd> <kbd>Space</kbd></td><td>scroll (the mouse wheel too)</td></tr>
<tr><td><kbd>Alt</kbd> <kbd>&larr;</kbd> <kbd>&rarr;</kbd></td><td>back and forward; <kbd>F5</kbd> reloads, <kbd>Ctrl</kbd> <kbd>H</kbd> comes back here</td></tr>
</table>
</main></body></html>";
    }
}
