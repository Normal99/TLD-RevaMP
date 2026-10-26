using UnityEngine;

namespace RoadEnd
{
    /// Draws the note's text onto a paper-coloured texture once: a TextMesh seen by a camera that renders only it,
    /// into a render texture, copied into an ordinary texture (so the sheet can use a lit material).
    public static class NoteText
    {
        public static Color Paper = new Color(0.93f, 0.90f, 0.82f), Ink = new Color(0.04f, 0.04f, 0.08f);
        const int Layer = 31;   // no game object uses it; the camera sees nothing else

        public static Texture2D Render(string text, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGB24, true) { name = "RoadEndNote", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            var font = PickFont();
            var rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
            var root = new GameObject("RoadEndNoteRender");
            root.transform.position = new Vector3(0f, -20000f, 0f);
            try
            {
                var cam = new GameObject("Camera").AddComponent<Camera>();
                cam.transform.SetParent(root.transform, false);
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = 0.5f;               // the view is 1 unit high, width/height wide
                cam.aspect = (float)width / height;
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Paper;
                cam.cullingMask = 1 << Layer;
                cam.targetTexture = rt;

                var t = new GameObject("Text") { layer = Layer };
                t.transform.SetParent(root.transform, false);
                t.transform.localPosition = new Vector3(0f, 0.02f, 1f);
                var tm = t.AddComponent<TextMesh>();
                tm.font = font;
                tm.text = text;
                tm.fontSize = 128;
                tm.characterSize = 0.016f;                  // big: the sheet is read from standing height
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = Ink;
                tm.fontStyle = FontStyle.Italic;
                var mr = t.GetComponent<MeshRenderer>();
                mr.sharedMaterial = font.material;
                // wide text shrinks to fit the sheet
                var size = mr.bounds.size;
                float maxW = cam.aspect * 0.86f;
                if (size.x > maxW) tm.characterSize *= maxW / size.x;

                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply(true);
                RenderTexture.active = prev;
                cam.targetTexture = null;
            }
            finally
            {
                Object.Destroy(root);
                RenderTexture.ReleaseTemporary(rt);
            }
            return tex;
        }

        /// The game's own fonts first (a handwritten one if there is one), else Unity's built-in one.
        static Font PickFont()
        {
            Font best = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                string n = f.name.ToLowerInvariant();
                if (n == Plugin.FontName.ToLowerInvariant()) return f;
                if (best == null && (n.Contains("hand") || n.Contains("script") || n.Contains("write"))) best = f;
            }
            return best ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        public static string Fonts()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) names.Add(f.name + (f.dynamic ? "(dyn)" : ""));
            return string.Join(", ", names);
        }
    }
}
