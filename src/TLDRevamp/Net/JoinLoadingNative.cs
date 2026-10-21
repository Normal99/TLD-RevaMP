using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// A lease on the game's localized loading screen. No searches or allocations in Update.
    internal static class JoinLoadingNative
    {
        private static readonly JoinActiveStates<GameObject> States = new JoinActiveStates<GameObject>();
        private static GameObject _backdrop;
        private static Transform _root;
        private static Slider _slider;
        private static Canvas _canvas;
        private static Image _image;
        private static TMPro.TextMeshProUGUI _title;
        private static float _value, _progress;
        internal static bool Used { get; private set; }
        internal static bool Active => Used && _root != null && _slider != null && _backdrop != null &&
            _canvas != null && _canvas.enabled && _image != null && _image.enabled && _title != null && _title.enabled;

        internal static void Begin()
        {
            Close(); Used = false; _progress = 0;
            var menu = menuhandler.s;
            if (menu == null || !menu.gameObject.activeInHierarchy) return;
            var canvas = menu.GetComponentInParent<Canvas>() ?? menu.GetComponentInChildren<Canvas>(true);
            if (canvas == null || !canvas.enabled) return;
            var root = canvas.transform.Find("UIOVERLAY/2/TextLoad");
            var image = root != null ? root.Find("Image") : null;
            var title = root != null ? root.Find("Text (TMP)") : null;
            var bar = root != null ? root.Find("LoadRoadBar") : null;
            var slider = bar != null ? bar.GetComponent<Slider>() : null;
            if (!JoinPresentation.UseNative(true, root != null, image != null && image.GetComponent<Image>() != null,
                title != null && title.GetComponent<TMPro.TextMeshProUGUI>() != null, slider != null)) return;
            _root = root; _slider = slider; _value = slider.value;
            _canvas = canvas; _image = image.GetComponent<Image>(); _title = title.GetComponent<TMPro.TextMeshProUGUI>();
            Remember(root, canvas.transform); Remember(image, root); Remember(title, root); Remember(bar, root);
            // This opaque first child is behind the native poster, localized title and road bar.
            _backdrop = new GameObject("RevaMP loading backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(CanvasGroup), typeof(Image));
            var rt = (RectTransform)_backdrop.transform; rt.SetParent(root, false); rt.SetAsFirstSibling();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var fill = _backdrop.GetComponent<Image>(); fill.color = new Color(.09f, .08f, .065f, 1); fill.raycastTarget = false;
            var group = _backdrop.GetComponent<CanvasGroup>(); group.alpha = 1; group.ignoreParentGroups = true; group.blocksRaycasts = group.interactable = false;
            Used = true; Update(0);
        }
        private static void Remember(Transform child, Transform stop)
        {
            for (var t = child; t != null; t = t.parent)
            {
                States.Remember(t.gameObject, t.gameObject.activeSelf);
                if (t == stop) break;
            }
        }
        internal static void Update(float progress)
        {
            if (!Active) { if (Used) Fallback(); return; }
            if (!_backdrop.activeSelf) _backdrop.SetActive(true);
            for (int i = States.Count - 1; i >= 0; i--)
            {
                var obj = States.Target(i);
                if (obj != null && !obj.activeSelf) obj.SetActive(true);
            }
            _progress = JoinPresentation.Forward(_progress, progress);
            _slider.SetValueWithoutNotify(_slider.minValue + (_slider.maxValue - _slider.minValue) * _progress);
        }
        internal static void Fallback() { Close(); Used = false; }
        internal static void Close()
        {
            if (_slider != null) _slider.SetValueWithoutNotify(_value);
            for (int i = 0; i < States.Count; i++)
            {
                var obj = States.Target(i);
                if (obj != null) obj.SetActive(States.Active(i));
            }
            States.Clear();
            if (_backdrop != null) { _backdrop.SetActive(false); Object.Destroy(_backdrop); }
            _root = null; _slider = null; _backdrop = null; _canvas = null; _image = null; _title = null;
        }
    }
}
