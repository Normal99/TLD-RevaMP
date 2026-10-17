using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HeadUnit
{
    /// The head unit's touch screen: tabs along the top (radio, YouTube, navigation), the picture or text in the
    /// middle, the buttons for the open tab along the bottom. A button is pressed as the game's own knobs are: look at
    /// it (the crosshair) and click. Built on each unit when it starts; everything is in metres on the screen's face.
    ///
    /// The buttons have no colliders: a press is found by where the look ray meets the screen's face. Everything is
    /// drawn on the screen's own layer (playtest 2026-10-14: on the Ignore Raycast layer nothing but the picture
    /// showed, and colliders on the unit can get in the way of the game's own look ray, e.g. when mounting it).
    public sealed class Touch : MonoBehaviour
    {
        public static float Reach = 1.6f;      // as far as the game lets a player use a knob
        public const float Bar = 0.2f;         // the top and bottom bars, as a share of the screen's height

        public sealed class Button
        {
            public string Id;
            public GameObject Go;
            public Rect Area;                  // on the screen's face, metres from its centre
            public MeshRenderer Back;
            public TextMeshPro Label;
            public Action Press;
            public bool Lit;
        }

        public UnitBehaviour Unit;
        public float W, H;
        public Transform Root;
        public MeshRenderer Content;           // the picture (video) between the bars
        public Material ContentMat;
        public TextMeshPro Text;               // over the picture
        private MeshRenderer _progress;        // a bar along the bottom of the picture (downloads)
        private float _aspect = -1f; private bool _full;
        private readonly List<Button> _buttons = new List<Button>();
        private Material _back, _backLit, _backOn;
        private Color _accent;
        private string _layout;

        private static readonly List<Touch> All = new List<Touch>();
        private int _layer;
        private pickupable _pick;

        public static Touch Looked;            // the unit whose button the crosshair is on
        /// The mouse wheel this frame while the crosshair is on a unit's buttons (+1 up, -1 down; 0: none).
        public static int Wheel;
        public static Button LookedButton;

        public void Build(UnitBehaviour unit, Transform screen)
        {
            Unit = unit;
            W = screen.localScale.x; H = screen.localScale.y;
            _accent = unit.Model != null ? unit.Model.Screen : new Color(0.3f, 0.7f, 1f);
            _layer = screen.gameObject.layer;
            _pick = unit.GetComponent<pickupable>();
            Root = new GameObject("HeadUnitTouch").transform;
            Root.gameObject.layer = _layer;
            Root.SetParent(screen.parent, false);
            Root.localPosition = screen.localPosition; Root.localRotation = screen.localRotation;
            var ps = screen.parent.lossyScale;
            Root.localScale = new Vector3(1f / Mathf.Max(ps.x, 1e-4f), 1f / Mathf.Max(ps.y, 1e-4f), 1f / Mathf.Max(ps.z, 1e-4f));

            _back = Look.Solid(new Color(0.12f, 0.12f, 0.14f));
            _backLit = Look.Solid(new Color(0.3f, 0.3f, 0.34f));
            var on = _accent * 0.55f; on.a = 1f;
            _backOn = Look.Solid(on);

            var content = GameObject.CreatePrimitive(PrimitiveType.Quad);
            content.name = "Content";
            content.layer = _layer;
            DestroyImmediate(content.GetComponent<Collider>());
            content.transform.SetParent(Root, false);
            content.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            content.transform.localScale = new Vector3(W, H * (1f - 2f * Bar), 1f);
            Content = content.GetComponent<MeshRenderer>();
            ContentMat = Look.ScreenMaterial(unit.Radio);
            ContentMat.mainTexture = Look.Pixel(Color.black);
            Content.sharedMaterial = ContentMat;

            Text = Label(Root, "Text", new Vector3(0f, 0f, -0.002f), new Vector2(W * 0.94f, H * (1f - 2f * Bar) * 0.9f), 0.05f);
            Text.color = _accent;

            var bar = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bar.name = "Progress";
            bar.layer = _layer;
            DestroyImmediate(bar.GetComponent<Collider>());
            bar.transform.SetParent(Root, false);
            _progress = bar.GetComponent<MeshRenderer>();
            _progress.sharedMaterial = _backOn;
            _progress.enabled = false;
        }

        /// The picture's shape (width over height, or -1: fill the middle) and whether it takes the whole screen (FULL:
        /// no bars). The picture keeps its shape, with black around it.
        public void Picture(float aspect, bool full)
        {
            if (Mathf.Abs(aspect - _aspect) < 1e-3f && full == _full) return;
            _aspect = aspect; _full = full;
            float aw = W, ah = full ? H : H * (1f - 2f * Bar);
            float w = aw, h = ah;
            if (aspect > 0f) { if (aw / ah > aspect) w = ah * aspect; else h = aw / aspect; }
            Content.transform.localScale = new Vector3(w, h, 1f);
        }

        /// A download's progress (0–1) as a bar along the bottom of the middle; below 0: none.
        public void Progress(float f)
        {
            if (_progress == null) return;
            bool on = f >= 0f;
            if (_progress.enabled != on) _progress.enabled = on;
            if (!on) return;
            f = Mathf.Clamp01(f);
            float mh = H * (1f - 2f * Bar), bh = mh * 0.06f, w = Mathf.Max(W * 0.9f * f, 0.0005f);
            _progress.transform.localPosition = new Vector3(-W * 0.45f + w * 0.5f, -mh * 0.5f + bh * 1.5f, -0.0025f);
            _progress.transform.localScale = new Vector3(w, bh, 1f);
        }

        /// The text over the picture: in the middle and large, or along the top and small (over the map).
        public void TextTop(bool top)
        {
            if (Text == null || _top == top) return;
            _top = top;
            float mh = H * (1f - 2f * Bar);
            Text.rectTransform.sizeDelta = new Vector2(W * 0.94f, top ? mh * 0.3f : mh * 0.9f);
            Text.transform.localPosition = new Vector3(0f, top ? mh * 0.33f : 0f, -0.002f);
            Text.alignment = top ? TextAlignmentOptions.Top : TextAlignmentOptions.Center;
            Text.fontSizeMax = (top ? 0.025f : 0.05f) * 10f;
            Text.fontSizeMin = top ? 0.03f : 0.1f;   // the strip is small: a line at the full screen's smallest size didn't show
        }
        private bool _top;

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void OnDestroy()
        {
            if (ContentMat != null) Destroy(ContentMat);
            foreach (var m in new[] { _back, _backLit, _backOn }) if (m != null) Destroy(m);
        }

        /// The buttons for what the screen shows now (rebuilt only when the layout changes).
        /// full: no rows; the whole screen is one button (`top`'s first: FULL's way back).
        public void Layout(string key, IList<(string id, string text, Action press, bool on)> top, IList<(string id, string text, Action press, bool on)> bottom,
                           IList<(string id, string text, Action press, bool on)> list = null, bool full = false, int cols = 1, float listTop = 0f)
        {
            if (key == _layout) { Relabel(top); Relabel(bottom); Relabel(list); return; }
            _layout = key;
            foreach (var b in _buttons) if (b.Go != null) Destroy(b.Go);
            _buttons.Clear();
            if (full)
            {
                var it = top[0];
                var go = new GameObject("Button_" + it.id) { layer = _layer };
                go.transform.SetParent(Root, false);
                _buttons.Add(new Button { Id = it.id, Go = go, Area = new Rect(-W * 0.5f, -H * 0.5f, W, H), Press = it.press });
                Content.enabled = true;
                return;
            }
            Row(top, H * (0.5f - Bar * 0.5f));
            Row(bottom, -H * (0.5f - Bar * 0.5f));
            if (list != null)
            {
                // a list in the middle, top to bottom, in `cols` columns, under a strip of `listTop` (a share of the
                // middle) left for the text
                float mid = H * (1f - 2f * Bar), area = mid * (1f - listTop);
                cols = Mathf.Max(1, cols);
                int rows = Mathf.Max((list.Count + cols - 1) / cols, cols == 1 ? 4 : 1);
                float rh = area / rows;
                for (int r = 0; r * cols < list.Count; r++)
                {
                    var row = new List<(string, string, Action, bool)>();
                    for (int c = 0; c < cols && r * cols + c < list.Count; c++) row.Add(list[r * cols + c]);
                    while (row.Count < cols && rows > 1 && cols > 1) row.Add((null, null, null, false));
                    Row(row, mid * 0.5f - mid * listTop - rh * (r + 0.5f), rh);
                }
            }
            Content.enabled = list == null;
        }

        private void Row(IList<(string id, string text, Action press, bool on)> items, float y, float height = -1f)
        {
            if (items == null || items.Count == 0) return;
            float gap = W * 0.01f, bw = (W - gap * (items.Count + 1)) / items.Count, bh = (height > 0f ? height : H * Bar) - gap;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.id == null) continue;   // a gap (the last row of a grid)
                float x = -W * 0.5f + gap + bw * 0.5f + i * (bw + gap);
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "Button_" + it.id;
                DestroyImmediate(go.GetComponent<Collider>());
                go.layer = _layer;
                go.transform.SetParent(Root, false);
                go.transform.localPosition = new Vector3(x, y, -0.001f);
                go.transform.localScale = new Vector3(bw, bh, 1f);
                var b = new Button { Id = it.id, Go = go, Area = new Rect(x - bw * 0.5f, y - bh * 0.5f, bw, bh), Back = go.GetComponent<MeshRenderer>(), Press = it.press };
                b.Label = Label(Root, "Label_" + it.id, new Vector3(x, y, -0.002f), new Vector2(bw * 0.9f, bh * 0.8f), bh * 0.55f);
                b.Label.transform.SetParent(go.transform, true);
                b.Label.text = Readable(b.Label.font, it.text);
                b.Lit = it.on;
                b.Back.sharedMaterial = it.on ? _backOn : _back;
                _buttons.Add(b);
            }
        }

        private void Relabel(IList<(string id, string text, Action press, bool on)> items)
        {
            if (items == null) return;
            foreach (var it in items)
                foreach (var b in _buttons)
                    if (b.Id == it.id)
                    {
                        if (b.Label != null) { var t = Readable(b.Label.font, it.text); if (b.Label.text != t) b.Label.text = t; }
                        b.Press = it.press; b.Lit = it.on;
                    }
        }

        /// The text with only characters the font has (a missing one shrank a whole label to nothing: a search result
        /// with "|", headunit.py 2026-10-16): an accented letter becomes its plain one, anything else a space.
        public static string Readable(TMP_FontAsset f, string s)
        {
            if (f == null || string.IsNullOrEmpty(s)) return s;
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || f.HasCharacter(c, true)) { sb?.Append(c); continue; }
                if (sb == null) sb = new System.Text.StringBuilder(s.Substring(0, i));
                string d = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
                char plain = c == '\u0131' ? 'i' : d.Length > 0 ? d[0] : ' ';
                sb.Append(plain != c && f.HasCharacter(plain, true) ? plain : ' ');
            }
            return sb != null ? sb.ToString() : s;
        }

        /// The characters of `s` the screen's font lacks (tests).
        public string Missing(string s)
        {
            var f = Text != null ? Text.font : null;
            if (f == null) return "no font";
            var m = new System.Text.StringBuilder();
            foreach (char c in s) if (!f.HasCharacter(c, true)) m.Append(c);
            return f.name + " lacks '" + m + "'";
        }

        private TextMeshPro Label(Transform parent, string name, Vector3 pos, Vector2 size, float maxSize)
        {
            var go = new GameObject(name);
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = size;
            t.enableAutoSizing = true; t.fontSizeMin = maxSize * 2f; t.fontSizeMax = maxSize * 10f;   // TMP: about 10 points a metre
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = true;
            t.color = Color.white;
            return t;
        }

        private void LateUpdate()
        {
            foreach (var b in _buttons)
            {
                if (b.Back == null) continue;
                var want = LookedButton == b ? _backLit : b.Lit ? _backOn : _back;
                if (b.Back.sharedMaterial != want) b.Back.sharedMaterial = want;
            }
        }

        /// Why the last look found no button (bridge: `get HeadUnit.Touch.Miss`), or what it found.
        public static string Miss = "";

        /// The button `ray` points at (Looked, LookedButton), or none.
        public static void Find(Ray ray)
        {
            Looked = null; LookedButton = null;
            float best = Reach; string miss = "no screen ahead within " + Reach + " m";
            foreach (var t in All)
            {
                if (t.Root == null || (t._pick != null && t._pick.pickedUp)) continue;   // not while it's carried
                var plane = new Plane(t.Root.forward, t.Root.position);
                if (Vector3.Dot(ray.direction, t.Root.forward) <= 0f || !plane.Raycast(ray, out float d) || d > best) continue;   // the face points -z
                var p = t.Root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(p.x) > t.W * 0.5f || Mathf.Abs(p.y) > t.H * 0.5f) { miss = $"off the screen at {p.x:F3}, {p.y:F3} ({d:F2} m)"; continue; }
                miss = $"on the screen at {p.x:F3}, {p.y:F3}, between buttons";
                foreach (var b in t._buttons)
                    if (b.Area.Contains(new Vector2(p.x, p.y))) { Looked = t; LookedButton = b; best = d; break; }
            }
            if (LookedButton == null) { Miss = miss; return; }
            // something solid between the eye and the screen (a wall): not pressable. The unit's own car doesn't count:
            // its dashboard can stand in front of the radio's face (playtest 2026-10-14, Car05: never pressable)
            var car = Looked.Unit.Car != null ? Looked.Unit.Car.transform : Looked.Unit.transform;
            foreach (var hit in Physics.RaycastAll(ray, best - 0.03f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!hit.collider.transform.IsChildOf(car) && !hit.collider.transform.IsChildOf(mainscript.s.player.transform))
                { Miss = "blocked by " + hit.collider.name + " (" + hit.collider.transform.root.name + ")"; Looked = null; LookedButton = null; return; }
            Miss = "on " + LookedButton.Id;
        }

        /// Where a button is: its centre on the screen's face (world space) and the face's outward direction.
        public bool Where(string id, out Vector3 point, out Vector3 outward)
        {
            point = outward = Vector3.zero;
            foreach (var b in _buttons)
                if (b.Id == id) { point = Root.TransformPoint(new Vector3(b.Area.center.x, b.Area.center.y, 0f)); outward = -Root.forward; return true; }
            return false;
        }

        public IEnumerable<string> ButtonIds() { foreach (var b in _buttons) yield return b.Id + "=" + (b.Label != null ? b.Label.text : ""); }

        /// What the crosshair is on, and a click on it. Called once a frame (Controls).
        public static void Tick(bool canClick)
        {
            Looked = null; LookedButton = null; Wheel = 0;
            // the player's own camera: the game tags none as MainCamera (Camera.main is null, so no press ever landed)
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var cam = pl != null && pl.Cam != null ? pl.Cam : Camera.main;
            if (cam == null) return;
            Find(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)));
            if (LookedButton == null) return;
            if (canClick) { float w = Input.mouseScrollDelta.y; Wheel = w > 0.01f ? 1 : w < -0.01f ? -1 : 0; }
            if (canClick && Input.GetMouseButtonDown(0)) LookedButton.Press?.Invoke();
        }
    }
}
