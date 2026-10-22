using System;
using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

static class MpApiRagdollTest
{
    static int checks, failures;
    static void Check(string name, bool ok)
    {
        checks++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    static void Throws<T>(string name, Action action, string parameter, string message) where T : ArgumentException
    {
        try { action(); Check(name, false); }
        catch (T e) { Check(name, e.ParamName == parameter && e.Message.Contains(message)); }
        catch (Exception) { Check(name, false); }
    }

    static int Main()
    {
        Check("API version 3", MpApi.ApiVersion == 3);
        Check("starts empty without RevaMP or a session", !MpApi.Available && !MpApi.InSession && MpApi.RagdollCount == 0);
        var root = new GameObject("creature");
        var a = new GameObject("hip").transform;
        var b = new GameObject("head").transform;
        var input = new[] { a, b };
        Check("unknown root is not found", !MpApi.TryGetRagdoll(root, out var limbs, out var limp) && limbs == null && limp == null);
        Check("null lookup is not found", !MpApi.TryGetRagdoll(null, out limbs, out limp) && limbs == null && limp == null);
        MpApi.RegisterRagdoll(root, input);
        Check("register and lookup without backend", MpApi.TryGetRagdoll(root, out limbs, out limp) && MpApi.RagdollCount == 1);
        Check("limb order preserved", limbs.Length == 2 && limbs[0] == a && limbs[1] == b);
        Check("null isLimp means always limp", limp == null);
        input[0] = b;
        Check("registration owns a copy", !ReferenceEquals(input, limbs) && limbs[0] == a);

        bool isLimp = false;
        Func<bool> callback = () => isLimp;
        MpApi.RegisterRagdoll(root, new[] { b }, callback);
        Check("replace keeps one root", MpApi.TryGetRagdoll(root, out limbs, out limp) && MpApi.RagdollCount == 1 && limbs.Length == 1 && limbs[0] == b);
        Check("callback preserved and reads current state", ReferenceEquals(limp, callback) && !limp());
        isLimp = true;
        Check("callback follows limp changes", limp());
        MpApi.RegisterRagdoll(root, new[] { a });
        Check("replacement can clear callback", MpApi.TryGetRagdoll(root, out limbs, out limp) && limp == null);

        Throws<ArgumentNullException>("null root rejected", () => MpApi.RegisterRagdoll(null, new[] { a }), "root", "root");
        Throws<ArgumentNullException>("null unregister root rejected", () => MpApi.UnregisterRagdoll(null), "root", "root");
        Throws<ArgumentException>("null array rejected", () => MpApi.RegisterRagdoll(root, null), "limbs", "1-63");
        Throws<ArgumentException>("zero limbs rejected", () => MpApi.RegisterRagdoll(root, Array.Empty<Transform>()), "limbs", "1-63");
        var many = new Transform[64];
        for (int i = 0; i < many.Length; i++) many[i] = new GameObject("limb" + i).transform;
        Throws<ArgumentException>("64 limbs rejected", () => MpApi.RegisterRagdoll(root, many), "limbs", "1-63");
        Throws<ArgumentException>("null limb rejected", () => MpApi.RegisterRagdoll(root, new[] { a, null }), "limbs", "index 1 is null");
        Throws<ArgumentException>("duplicate limb rejected", () => MpApi.RegisterRagdoll(root, new[] { a, a }), "limbs", "duplicate ragdoll limb at index 1");
        var deadLimb = new GameObject("dead limb").transform;
        UnityEngine.Object.Destroy(deadLimb);
        Throws<ArgumentException>("destroyed limb rejected", () => MpApi.RegisterRagdoll(root, new[] { deadLimb }), "limbs", "null or destroyed");
        Check("invalid replacement preserves registration", MpApi.TryGetRagdoll(root, out limbs, out limp) && limbs.Length == 1 && limbs[0] == a && limp == null);
        Array.Resize(ref many, 63);
        MpApi.RegisterRagdoll(root, many);
        Check("63 limbs accepted", MpApi.TryGetRagdoll(root, out limbs, out limp) && limbs.Length == 63);
        MpApi.UnregisterRagdoll(root);
        Check("unregister clears lookup and outputs", !MpApi.TryGetRagdoll(root, out limbs, out limp) && limbs == null && limp == null && MpApi.RagdollCount == 0);
        MpApi.UnregisterRagdoll(root);
        Check("unregister unknown root is harmless", MpApi.RagdollCount == 0);

        MpApi.RegisterRagdoll(root, new[] { a }, callback);
        UnityEngine.Object.Destroy(root);
        Check("stub models Unity destroyed null", root == null && !ReferenceEquals(root, null));
        Check("destroyed root is not found", !MpApi.TryGetRagdoll(root, out limbs, out limp) && limbs == null && limp == null);
        Check("destroyed lookup prunes registration", MpApi.RagdollCount == 0);
        MpApi.RegisterRagdoll(root, new[] { a });
        Check("destroyed root cannot be registered", MpApi.RagdollCount == 0);
        MpApi.UnregisterRagdoll(root);
        Check("destroyed unregister is harmless", MpApi.RagdollCount == 0);

        var survivor = new GameObject("survivor");
        var dead = new GameObject("dead");
        MpApi.RegisterRagdoll(survivor, new[] { a });
        MpApi.RegisterRagdoll(dead, new[] { b });
        UnityEngine.Object.Destroy(dead);
        Check("count prunes dead roots and keeps live ones", MpApi.RagdollCount == 1 && MpApi.TryGetRagdoll(survivor, out _, out _));
        MpApi.UnregisterRagdoll(survivor);

        var backend = new LoggingBackend();
        MpApi.Backend = backend;
        var logged = new GameObject("logged creature");
        MpApi.RegisterRagdoll(logged, new[] { a });
        Check("registration logs without a session", !MpApi.InSession && backend.Lines.Count == 1 && backend.Lines[0].Contains("logged creature"));
        MpApi.RegisterRagdoll(logged, new[] { b }, callback);
        MpApi.RegisterRagdoll(logged, new[] { a });
        Check("replacements do not spam the log", backend.Lines.Count == 1);
        var other = new GameObject("other creature");
        MpApi.RegisterRagdoll(other, new[] { b });
        Check("distinct roots each log once", backend.Lines.Count == 2 && MpApi.RagdollCount == 2);
        MpApi.UnregisterRagdoll(logged);
        MpApi.UnregisterRagdoll(other);
        MpApi.Backend = null;
        Check("all registrations removed", MpApi.RagdollCount == 0);
        Console.WriteLine(failures == 0 ? $"ALL PASS ({checks} checks)" : $"FAIL ({failures} of {checks} checks)");
        return failures == 0 ? 0 : 1;
    }

    sealed class LoggingBackend : MpApi.IBackend
    {
        public readonly List<string> Lines = new List<string>();
        public bool InSession => false;
        public bool IsHost => false;
        public int MyPlayerId => 0;
        public double SessionTime => 0;
        public IReadOnlyList<PlayerInfo> Players => Array.Empty<PlayerInfo>();
        public void Share(SharedObject so) { }
        public bool RequestEdit(SharedObject so, Action<RecordWriter> write) => false;
        public void RequestOwnership(SharedObject so) { }
        public void Remove(SharedObject so) { }
        public bool SendEvent(string key, byte[] data, EventTarget target, SharedObject about) => false;
        public uint WorldObjectId(GameObject go) => 0;
        public GameObject FindWorldObject(uint id) => null;
        public bool ShareWorldObject(GameObject go) => false;
        public void Log(string line) => Lines.Add(line);
    }
}

// The API's Unity core surface. Destroy simulates Unity's native-object null check while the managed key survives.
namespace UnityEngine
{
    public class Object
    {
        bool destroyed;
        public string name;
        public static void Destroy(Object obj) { if (!ReferenceEquals(obj, null)) obj.destroyed = true; }
        public static bool operator ==(Object a, Object b)
        {
            if (ReferenceEquals(a, null)) return ReferenceEquals(b, null) || b.destroyed;
            if (ReferenceEquals(b, null)) return a.destroyed;
            return ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object obj) => obj is Object other && this == other;
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public class GameObject : Object
    {
        public readonly Transform transform;
        public GameObject(string name) { this.name = name; transform = new Transform { gameObject = this }; }
    }
    public class Transform : Object { public GameObject gameObject; public Transform parent; }
    public static class Time { public static double realtimeSinceStartupAsDouble => 0; }
    public static class Mathf { public static float Sqrt(float value) => (float)Math.Sqrt(value); }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
}
