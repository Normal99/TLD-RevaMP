// Test-only Unity stubs that RECORD every write made to a Camera.
using System;
using System.Collections.Generic;

namespace UnityEngine {
  public struct Vector3 { public float x, y, z;
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public static Vector3 zero => new Vector3(0, 0, 0);
  }
  public struct Quaternion { public float x, y, z, w;
    public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public static Quaternion identity => new Quaternion(0, 0, 0, 1);
  }
  public class Transform {
    // position and rotation are PROPERTIES, so a direct assignment (`transform.position = ...`) is recorded
    // exactly like a method call. With plain fields the write was invisible and the test could not fail.
    Vector3 _position; Quaternion _rotation = Quaternion.identity;
    public Vector3 position { get { return _position; } set { _position = value; if (owner != null) Camera.RecordWrite(owner); } }
    public Quaternion rotation { get { return _rotation; } set { _rotation = value; if (owner != null) Camera.RecordWrite(owner); } }
    public Transform parent;
    // the camera this transform belongs to, so a reparent can be recorded
    public Camera owner;
    public void SetParent(Transform p, bool worldPositionStays) {
      parent = p;
      if (owner != null) Camera.RecordWrite(owner);
    }
    public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
  }
  public struct Color { public float r, g, b, a;
    public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
  }
  public enum CameraClearFlags { Skybox = 1, Color = 2, Depth = 3, Nothing = 4 }
  public enum HideFlags { None = 0, HideAndDontSave = 61 }
  public class GameObject {
    public string name; public HideFlags hideFlags; public Transform transform = new Transform();
    public GameObject(string n) { name = n; }
    public T AddComponent<T>() where T : new() {
      var c = new T();
      if (c is Camera cam) { cam.gameObject = this; Camera.LastCreatedName = name; }
      return c;
    }
  }
  public class Camera {
    public GameObject gameObject;
    public Transform transform = new Transform();
    public CameraClearFlags clearFlags = CameraClearFlags.Skybox;

    // the recording: every mutating member records the camera that was written to
    static readonly List<Camera> Written = new List<Camera>();
    public static string LastCreatedName = "";
    public static void ResetWrites() { Written.Clear(); }
    public static List<Camera> Writes() { return new List<Camera>(Written); }
    public static bool WasWritten(Camera c) { return Written.Contains(c); }
    public Camera() { transform.owner = this; }
    public static void RecordWrite(Camera c) { Written.Add(c); }

    float _fov = 60f; bool _enabled = true; int _mask; float _depth; float _near = 0.3f, _far = 1000f;
    public float fieldOfView { get { return _fov; } set { _fov = value; Written.Add(this); } }
    public bool enabled { get { return _enabled; } set { _enabled = value; Written.Add(this); } }
    public int cullingMask { get { return _mask; } set { _mask = value; Written.Add(this); } }
    public float depth { get { return _depth; } set { _depth = value; Written.Add(this); } }
    public float nearClipPlane { get { return _near; } set { _near = value; Written.Add(this); } }
    public float farClipPlane { get { return _far; } set { _far = value; Written.Add(this); } }
    Color _bg;
    public Color backgroundColor { get { return _bg; } set { _bg = value; Written.Add(this); } }
    public bool isActiveAndEnabled => _enabled;
  }
  public static class Object {
    public static void Destroy(object o) { }
    public static void DestroyImmediate(object o) { }
  }
}
