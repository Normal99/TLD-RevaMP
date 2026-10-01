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
