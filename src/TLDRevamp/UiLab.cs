using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// UI hierarchy dump (bridge `uidump <path or "settings"> [depth]`): names, active state, components, rects, texts.
    public static class UiLab
    {
        public static string Dump(string what, int depth)
        {
            Transform root = null;
            if (what == "settings")
            {
                var s = Resources.FindObjectsOfTypeAll<settingsscript>().FirstOrDefault(x => x.gameObject.scene.name != null);
                root = s != null ? s.transform : null;
            }
            else
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    if (t.gameObject.scene.name != null && PathOf(t).EndsWith(what)) { root = t; break; }
            }
            if (root == null) return "{\"error\":\"not found\"}";
            var sb = new StringBuilder();
            Walk(root, 0, depth, sb);
            return "{\"path\":" + Json.Str(PathOf(root)) + ",\"tree\":" + Json.Str(sb.ToString()) + "}";
        }

        private static void Walk(Transform t, int level, int max, StringBuilder sb)
        {
            sb.Append(new string(' ', level * 2)).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name);
            var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
            sb.Append("  [").Append(string.Join(",", comps)).Append(']');
            var rt = t as RectTransform;
            if (rt != null) sb.Append(" pos=").Append(rt.anchoredPosition.ToString("F0")).Append(" size=").Append(rt.rect.size.ToString("F0"))
                .Append(" anch=").Append(rt.anchorMin.ToString("F2")).Append(rt.anchorMax.ToString("F2")).Append(" piv=").Append(rt.pivot.ToString("F2"));
            var img = t.GetComponent<UnityEngine.UI.Image>();
            if (img != null) sb.Append(" img=").Append(img.sprite != null ? img.sprite.name : "none").Append('/').Append(img.type).Append(" col=").Append(ColorUtility.ToHtmlStringRGBA(img.color));
            var tt = t.GetComponent<TMPro.TMP_Text>();
            if (tt != null) sb.Append(" font=").Append(tt.font != null ? tt.font.name : "?").Append(" fs=").Append(tt.fontSize).Append(" auto=").Append(tt.enableAutoSizing).Append(" col=").Append(ColorUtility.ToHtmlStringRGBA(tt.color)).Append(" al=").Append(tt.alignment);
            var tmp = t.GetComponent<TMPro.TMP_Text>();
            if (tmp != null) sb.Append(" text=\"").Append(tmp.text.Length > 40 ? tmp.text.Substring(0, 40) : tmp.text).Append('"');
            sb.Append('\n');
            if (level >= max) { if (t.childCount > 0) sb.Append(new string(' ', level * 2 + 2)).Append("… ").Append(t.childCount).Append(" children\n"); return; }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), level + 1, max, sb);
        }

        /// Persistent listeners of every UnityEvent-bearing component on the object at `what` (Button.onClick, Toggle...).
        public static string Events(string what)
        {
            Transform root = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.gameObject.scene.name != null && PathOf(t).EndsWith(what)) { root = t; break; }
            if (root == null) return "{\"error\":\"not found\"}";
            var sb = new StringBuilder();
            foreach (var c in root.GetComponents<Component>())
            {
                if (c == null) continue;
                foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (!typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType)) continue;
                    var ev = f.GetValue(c) as UnityEngine.Events.UnityEventBase;
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                    {
                        var target = ev.GetPersistentTarget(i);
                        sb.Append(c.GetType().Name).Append('.').Append(f.Name).Append(" -> ")
                          .Append(target != null ? (target is Component tc ? PathOf(tc.transform) + " (" + target.GetType().Name + ")" : target.name + " (" + target.GetType().Name + ")") : "null")
                          .Append('.').Append(ev.GetPersistentMethodName(i)).Append('\n');
                    }
                }
            }
            return "{\"events\":" + Json.Str(sb.ToString()) + "}";
        }

        /// Simulate a pointer click on the object whose path ends with `what` (as the EventSystem would dispatch it).
        public static string Click(string what)
        {
            Transform target = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.gameObject.scene.name != null && t.gameObject.activeInHierarchy && PathOf(t).EndsWith(what)) { target = t; break; }
            if (target == null) return "{\"error\":\"not found or inactive\"}";
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ev = new UnityEngine.EventSystems.PointerEventData(es) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
            var handler = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(target.gameObject);
            bool ok = UnityEngine.EventSystems.ExecuteEvents.Execute(handler, ev, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            var tg = handler != null ? handler.GetComponent<UnityEngine.UI.Toggle>() : null;
            return "{\"handler\":" + Json.Str(handler != null ? PathOf(handler.transform) : "none") + ",\"executed\":" + (ok ? "true" : "false") +
                   ",\"toggleIsOn\":" + (tg != null ? (tg.isOn ? "true" : "false") : "null") + ",\"interactable\":" + (tg != null ? (tg.IsInteractable() ? "true" : "false") : "null") + "}";
        }

        /// Pointer enter on an active UI object whose path ends with `what` (hover explanations), then reports the Revamp tab's
        /// explanation panel: its text height against the visible area, so a test can tell whether it needs scrolling.
        public static string Hover(string what)
        {
            Transform target = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.gameObject.scene.name != null && t.gameObject.activeInHierarchy && PathOf(t).EndsWith(what)) { target = t; break; }
            if (target == null) return "{\"error\":\"not found or inactive\"}";
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ev = new UnityEngine.EventSystems.PointerEventData(es);
            var handler = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerEnterHandler>(target.gameObject);
            bool ok = UnityEngine.EventSystems.ExecuteEvents.Execute(handler, ev, UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
            var sb = new StringBuilder("{\"handler\":").Append(Json.Str(handler != null ? PathOf(handler.transform) : "none")).Append(",\"executed\":").Append(ok ? "true" : "false");
            foreach (var sr in Object.FindObjectsOfType<UnityEngine.UI.ScrollRect>())
            {
                if (!PathOf(sr.transform).Contains("RevampInfo") || sr.content == null) continue;
                Canvas.ForceUpdateCanvases();
                var view = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
                sb.Append(",\"textHeight\":").Append(sr.content.rect.height.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"viewHeight\":").Append(view.rect.height.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"scrollbar\":").Append(sr.verticalScrollbar != null && sr.verticalScrollbar.gameObject.activeInHierarchy ? "true" : "false");
            }
            return sb.Append('}').ToString();
        }

        /// A mouse-wheel step over the object at `what` ("<path> <dy>"), dispatched as the input module does: raycast at the
        /// object's centre, then the first IScrollHandler up from the top hit. Reports who took it and how far the Revamp
        /// tab's list moved, so a test can tell whether rows let the wheel through to the list.
        public static string Scroll(string arg)
        {
            int sp = arg.LastIndexOf(' ');
            if (sp < 0) return "{\"error\":\"usage: uiscroll <path> <dy>\"}";
            string what = arg.Substring(0, sp);
            float dy = float.Parse(arg.Substring(sp + 1), System.Globalization.CultureInfo.InvariantCulture);
            RectTransform target = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<RectTransform>())
                if (t.gameObject.scene.name != null && t.gameObject.activeInHierarchy && PathOf(t).EndsWith(what)) { target = t; break; }
            if (target == null) return "{\"error\":\"not found or inactive\"}";
            var canvas = target.GetComponentInParent<Canvas>().rootCanvas;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var es = UnityEngine.EventSystems.EventSystem.current;
            var ev = new UnityEngine.EventSystems.PointerEventData(es)
            {
                position = RectTransformUtility.WorldToScreenPoint(cam, target.TransformPoint(target.rect.center)),
                scrollDelta = new Vector2(0f, dy)
            };
            var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ev, hits);
            var top = hits.Count > 0 ? hits[0].gameObject : null;
            UnityEngine.UI.ScrollRect list = null;
            foreach (var sr in Object.FindObjectsOfType<UnityEngine.UI.ScrollRect>())
            {
                var p = PathOf(sr.transform);
                if (p.Contains("TabRevamp") && !p.Contains("RevampInfo")) list = sr;
            }
            float before = list != null ? list.content.anchoredPosition.y : float.NaN;
            var handler = top != null ? UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IScrollHandler>(top) : null;
            if (handler != null) UnityEngine.EventSystems.ExecuteEvents.Execute(handler, ev, UnityEngine.EventSystems.ExecuteEvents.scrollHandler);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"hit\":").Append(Json.Str(top != null ? PathOf(top.transform) : "none"))
                .Append(",\"handler\":").Append(Json.Str(handler != null ? PathOf(handler.transform) + " (" + string.Join(",", handler.GetComponents<UnityEngine.EventSystems.IScrollHandler>().Select(h => h.GetType().Name)) + ")" : "none"));
            if (list != null)
            {
                var view = list.viewport != null ? list.viewport : (RectTransform)list.transform;
                sb.Append(",\"listBefore\":").Append(before.ToString("F0", inv)).Append(",\"listAfter\":").Append(list.content.anchoredPosition.y.ToString("F0", inv))
                  .Append(",\"sensitivity\":").Append(list.scrollSensitivity.ToString("F1", inv))
                  .Append(",\"contentHeight\":").Append(list.content.rect.height.ToString("F0", inv)).Append(",\"viewHeight\":").Append(view.rect.height.ToString("F0", inv))
                  .Append(",\"movement\":\"").Append(list.movementType).Append('"');
            }
            return sb.Append('}').ToString();
        }

        public static string SettingsInstances()
        {
            var sb = new StringBuilder("[");
            foreach (var s in Resources.FindObjectsOfTypeAll<settingsscript>())
            {
                if (s.gameObject.scene.name == null) continue;
                var root = s.transform.Find("NewSettings");
                sb.Append("{\"path\":").Append(Json.Str(PathOf(s.transform))).Append(",\"scene\":").Append(Json.Str(s.gameObject.scene.name))
                  .Append(",\"active\":").Append(s.gameObject.activeInHierarchy ? "true" : "false")
                  .Append(",\"isStatic\":").Append(ReferenceEquals(s, settingsscript.s) ? "true" : "false")
                  .Append(",\"hasTab\":").Append(root != null && root.Find("TabRevamp") != null ? "true" : "false").Append("},");
            }
            return sb.Append("]").ToString();
        }

        public static string PathOf(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
