#pragma warning disable CS0649, CS8981
using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------------------------
// The stubs for the external types the production hook needs.
//
// Only the Unity and game types are stubbed. The hook itself (src/TLDRevamp/Fixes/
// TerrainHorizon.TileHook.cs) is the shipped file, linked into the test project unchanged, and
// the other half of the TerrainHorizon partial class is a spy that records what the hook asked
// for. Nothing here models the hook's behaviour: it only records it.
// ---------------------------------------------------------------------------------------------

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static Vector3 operator *(float f, Vector3 a) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static Vector3 operator /(Vector3 a, float f) => new Vector3(a.x / f, a.y / f, a.z / f);
        public override string ToString() => "(" + x + ", " + y + ", " + z + ")";
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    /// A quaternion, with the rotation of a vector.
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);

        public static Quaternion AngleAxis(float degrees, Vector3 axis)
        {
            double len = Math.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            if (len <= 0) return identity;
            double half = degrees * Math.PI / 360.0;
            double s = Math.Sin(half);
            return new Quaternion((float)(axis.x / len * s), (float)(axis.y / len * s), (float)(axis.z / len * s), (float)Math.Cos(half));
        }

        /// Rotates a vector by this quaternion (the usual v' = v + 2q x (q x v + w v)).
        public Vector3 Rotate(Vector3 v)
        {
            var q = new Vector3(x, y, z);
            var t = new Vector3(
                2f * (q.y * v.z - q.z * v.y),
                2f * (q.z * v.x - q.x * v.z),
                2f * (q.x * v.y - q.y * v.x));
            var u = new Vector3(
                q.y * t.z - q.z * t.y,
                q.z * t.x - q.x * t.z,
                q.x * t.y - q.y * t.x);
            return new Vector3(
                v.x + w * t.x + u.x,
                v.y + w * t.y + u.y,
                v.z + w * t.z + u.z);
        }
    }

    public class Object
    {
        public string name;
    }

    public class Component : Object
    {
        public Transform transform;
    }

    /// A transform with a position, a rotation and a scale, like Unity's: a point is scaled
    /// first, then rotated, then moved.
    public class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
        public Transform parent;

        public Vector3 TransformPoint(Vector3 local)
        {
            var scaled = new Vector3(local.x * localScale.x, local.y * localScale.y, local.z * localScale.z);
            return position + rotation.Rotate(scaled);
        }

        public Vector3 InverseTransformPoint(Vector3 world)
        {
            var local = world - position;
            var unrotated = Conjugate(rotation).Rotate(local);
            return new Vector3(
                localScale.x == 0 ? 0 : unrotated.x / localScale.x,
                localScale.y == 0 ? 0 : unrotated.y / localScale.y,
                localScale.z == 0 ? 0 : unrotated.z / localScale.z);
        }

        private static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
    }

    public class Mesh : Object
    {
        public Vector3[] vertices = new Vector3[0];
        public int vertexCount => vertices.Length;
        public bool normalsRecalculated;
        public bool boundsRecalculated;
        public void RecalculateNormals() { normalsRecalculated = true; }
        public void RecalculateBounds() { boundsRecalculated = true; }
    }

    public class MeshFilter : Component
    {
        public Mesh mesh;
    }
}

namespace HarmonyLib
{
    /// The Harmony patch attribute, with the two forms the production hooks use.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class HarmonyPatchAttribute : Attribute
    {
        public HarmonyPatchAttribute(Type declaringType, string methodName)
        {
            DeclaringType = declaringType;
            MethodName = methodName;
        }
        public Type DeclaringType { get; }
        public string MethodName { get; }
    }
}

/// The game's own double-precision point.
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public static Vector3d zero => new Vector3d(0, 0, 0);
}

/// The game's global/world coordinate conversion: a pure translation by the world's origin.
public static class mainscript
{
    /// The world's origin in Unity space. The game moves it when the player strays past 2500 m.
    public static UnityEngine.Vector3 visszarakva = UnityEngine.Vector3.zero;

    public static Vector3d GlobalFromUnityPos(UnityEngine.Vector3 u) =>
        new Vector3d(u.x - visszarakva.x, u.y - visszarakva.y, u.z - visszarakva.z);

    public static UnityEngine.Vector3 UnityPosFromGlobal(Vector3d g) =>
        new UnityEngine.Vector3((float)(g.x + visszarakva.x), (float)(g.y + visszarakva.y), (float)(g.z + visszarakva.z));
}

/// The far-terrain script, whose tiles are placed at their own V[i] offset.
public class newFarTerrainScript
{
}

/// The far-map generator. Its own transform is NOT the tile's transform: the tiles are separate
/// objects that are placed or moved by the generator.
public class newNewRandomMapGen
{
    [Serializable]
    public class MeshToGenerate
    {
        public UnityEngine.Vector3 p;          // the noise seed of the tile, not a vertex offset
        public UnityEngine.Vector3[] v;
    }

    public newFarTerrainScript nfts;
    public float gridSize;
    public int resolution;
    public UnityEngine.Transform transform;

    public void UpdMesh(UnityEngine.MeshFilter F, MeshToGenerate pM) { }
}
