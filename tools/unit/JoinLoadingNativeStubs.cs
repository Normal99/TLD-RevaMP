using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object { public string name; public static void Destroy(Object obj) { } }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T GetComponentInParent<T>() where T : Component
        {
            for (var at = transform; at != null; at = at.parent) { var c = at.GetComponent<T>(); if (c != null) return c; }
            return null;
        }
        public T GetComponentInChildren<T>(bool inactive) where T : Component
        {
            var own = GetComponent<T>(); if (own != null) return own;
            foreach (var child in transform.Children) { var c = child.GetComponentInChildren<T>(inactive); if (c != null) return c; }
            return null;
        }
    }
    public class GameObject : Object
    {
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public Transform transform;
        private readonly Dictionary<Type, Component> _components = new Dictionary<Type, Component>();
        public GameObject(string name, params Type[] components)
        {
            this.name = name; transform = new RectTransform { gameObject = this };
            _components[typeof(RectTransform)] = transform;
            foreach (var type in components) if (type != typeof(RectTransform)) _components[type] = (Component)Activator.CreateInstance(type);
            foreach (var component in _components.Values) component.gameObject = this;
        }
        public T GetComponent<T>() where T : Component => _components.TryGetValue(typeof(T), out var c) ? (T)c : null;
        public void SetActive(bool value) => activeSelf = value;
    }
    public class Transform : Component
    {
        public Vector3 position = new Vector3 { y = 1.1f };
        public Transform parent;
        public readonly List<Transform> Children = new List<Transform>();
        public Transform Find(string path)
        {
            var segments = path.Split('/'); var at = this;
            foreach (var segment in segments) { at = at.Children.Find(c => c.gameObject.name == segment); if (at == null) return null; }
            return at;
        }
        public void SetParent(Transform value, bool keep) { parent?.Children.Remove(this); parent = value; parent?.Children.Add(this); }
        public void SetAsFirstSibling() { parent.Children.Remove(this); parent.Children.Insert(0, this); }
    }
    public class RectTransform : Transform { public Vector2 anchorMin, anchorMax, offsetMin, offsetMax; }
    public class Canvas : Component { public bool enabled = true; }
    public class CanvasGroup : Component { public float alpha; public bool ignoreParentGroups, blocksRaycasts, interactable; }
    public class CanvasRenderer : Component { }
    public struct Vector4 { public float x,y,z,w; public Vector4(float x,float y,float z,float w) { this.x=x; this.y=y; this.z=z; this.w=w; } }
    public class Sprite : Object
    {
        public Rect textureRect = new Rect(16, 32, 80, 60);
        public Vector4 border = new Vector4(5, 6, 7, 8);
        public Texture2D texture = new Texture2D(128, 128);
    }
    public static class Resources
    {
        public static readonly Font Liberation = new Font { name = "LiberationSans" };
        public static Sprite[] Sprites = { new Sprite { name = "newpaper" }, new Sprite { name = "newmenubutton2" }, new Sprite { name = "loading text" } };
        public static T[] FindObjectsOfTypeAll<T>() => typeof(T) == typeof(Sprite) ? (T[])(object)Sprites : (T[])(object)new[] { Liberation };
        public static T GetBuiltinResource<T>(string name) where T : new() => new T();
    }
}
namespace UnityEngine.UI
{
    public class Image : UnityEngine.Component { public UnityEngine.Color color; public bool raycastTarget, enabled = true; }
    public class Slider : UnityEngine.Component { public float value, minValue, maxValue = 1; public void SetValueWithoutNotify(float next) => value = next; }
}
namespace TMPro { public class TextMeshProUGUI : UnityEngine.Component { public string text = "Localized loading"; public bool enabled = true; } }
