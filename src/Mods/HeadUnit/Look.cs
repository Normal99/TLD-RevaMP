using TMPro;
using UnityEngine;

namespace HeadUnit
{
    /// How a head unit looks: the game's radio body in the model's colour, with a screen on its front and a line of
    /// text over it. The front is where the radio's knobs stick out (no fixed axes: the radio mesh decides).
    public static class Look
    {
        public const string ScreenName = "HeadUnitScreen", BezelName = "HeadUnitBezel";
        public static float ScreenW = 0.20f, ScreenH = 0.11f, Proud = 0.03f;   // metres: a double-DIN face, standing out of the radio over its knobs

        public static void Dress(GameObject prefab, Units.Model m)
        {
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !mats[i].HasProperty("_Color")) continue;
                    var c = new Material(mats[i]) { name = mats[i].name + "_" + m.Key };
                    c.color = Color.Lerp(c.color, m.Body, 0.85f);
                    mats[i] = c;
                }
                r.sharedMaterials = mats;
            }

            var radio = prefab.GetComponentInChildren<radioscript>(true);
            if (!Front(prefab, radio, out var center, out var normal, out var up, out float width, out float height)) return;

            // all in the radio's own space (the prefab sits under an inactive holder: no renderer bounds there). A screen
            // the size of a car's double-DIN unit, over the radio's face (its knobs are on the screen: Touch, Radio tab)
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = ScreenName;
            Object.DestroyImmediate(screen.GetComponent<Collider>());
            screen.transform.SetParent(prefab.transform, false);
            screen.transform.localPosition = center + normal * (Proud + 0.001f);
            screen.transform.localRotation = Quaternion.LookRotation(-normal, up);
            screen.transform.localScale = new Vector3(ScreenW, ScreenH, 1f);
            screen.GetComponent<MeshRenderer>().sharedMaterial = Solid(new Color(0.03f, 0.03f, 0.035f));
            // its bezel, in the model's colour: a lit, solid body (Standard). The unlit screen draws no depth for the game's
            // image effects (Unlit/Texture has no shadow-caster pass), so they shaded the screen with the dashboard
            // behind it: the unit looked see-through (playtest 2026-10-15). The bezel's face, 1 mm behind the screen,
            // gives that depth.
            var bezel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bezel.name = BezelName;
            Object.DestroyImmediate(bezel.GetComponent<Collider>());
            bezel.transform.SetParent(prefab.transform, false);
            bezel.transform.localPosition = center + normal * (Proud * 0.5f);
            bezel.transform.localRotation = screen.transform.localRotation;
            bezel.transform.localScale = new Vector3(ScreenW + 0.012f, ScreenH + 0.012f, Proud);
            var body = new Material(Shader.Find("Standard")) { name = "HeadUnitBezel_" + m.Key, color = m.Body };
            if (body.HasProperty("_Glossiness")) body.SetFloat("_Glossiness", 0.35f);
            bezel.GetComponent<MeshRenderer>().sharedMaterial = body;

            // the game's own knobs are under the screen now: its buttons turn them (Touch, Radio tab). Their colliders
            // off, so a press on the screen doesn't also turn the knob behind it.
            foreach (var u in prefab.GetComponentsInChildren<usablescript>(true))
                foreach (var c in u.GetComponents<Collider>()) c.enabled = false;
        }

        /// A flat colour that ignores the car's lighting. The game's build has Unlit/Texture but it ignores _Color (a
        /// tinted screen came out white, playtest 2026-10-14), so the colour is a 1×1 texture.
        public static Material Solid(Color c)
        {
            var m = ScreenMaterial(null);
            m.mainTexture = Pixel(c);
            if (m.HasProperty("_Color")) m.color = c;   // the Standard fallback
            return m;
        }

        private static readonly System.Collections.Generic.Dictionary<Color, Texture2D> _pixels = new System.Collections.Generic.Dictionary<Color, Texture2D>();
        public static Texture2D Pixel(Color c)
        {
            if (_pixels.TryGetValue(c, out var t) && t != null) return t;
            t = new Texture2D(1, 1, TextureFormat.RGBA32, false); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _pixels[c] = t;
            return t;
        }

        /// The material screens use: unlit if the game has the shader, else the radio's own lit-scale material.
        public static Material ScreenMaterial(radioscript radio)
        {
            var sh = Shader.Find("Unlit/Texture");
            if (sh != null) return new Material(sh);
            if (radio != null && radio.SOn != null) return new Material(radio.SOn);
            return new Material(Shader.Find("Standard"));
        }

        /// The radio's front face, in the radio's own space: centre, outward normal, up, and the size a screen on it
        /// may take (half the face's width: the knobs keep the sides).
        private static bool Front(GameObject prefab, radioscript radio, out Vector3 center, out Vector3 normal, out Vector3 up, out float width, out float height)
        {
            center = normal = up = Vector3.zero; width = height = 0f;
            var root = prefab.transform;
            var toLocal = root.worldToLocalMatrix;
            Bounds b = default; bool any = false;
            // the body: its solid box collider (radio01: "Collider", 0.20 × 0.06 × 0.12). The meshes' bounds put the
            // centre ahead of the knobs, and the screen went on the back (playtest 2026-10-14)
            foreach (var bc in prefab.GetComponentsInChildren<BoxCollider>(true))
            {
                if (bc.isTrigger || bc.GetComponent<usablescript>() != null || bc.gameObject.layer != root.gameObject.layer) continue;
                var m = toLocal * bc.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(bc.center + Vector3.Scale(bc.size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any)
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.gameObject.name == ScreenName) continue;
                var mb = mf.sharedMesh.bounds; var m = toLocal * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any) return false;
            // the knobs: the volume and tuning buttons sit on the front
            Vector3 knobs = Vector3.zero; int n = 0;
            foreach (var u in prefab.GetComponentsInChildren<usablescript>(true)) { knobs += root.InverseTransformPoint(u.transform.position); n++; }
            Vector3 dir = n > 0 ? knobs / n - b.center : Vector3.forward;
            Vector3[] axes = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            float best = -2f;
            foreach (var a in axes) { float d = Vector3.Dot(a, dir.normalized); if (d > best) { best = d; normal = a; } }
            up = Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up;
            var side = Vector3.Cross(up, normal);
            float depth = Vector3.Dot(b.extents, Abs(normal));
            float halfW = Vector3.Dot(b.extents, Abs(side)), halfH = Vector3.Dot(b.extents, Abs(up));
            center = b.center + normal * depth;
            width = halfW; height = halfH * 1.6f;
            return width > 0.001f && height > 0.001f;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
