#pragma warning disable CS0649
using System;
// The Unity types the ribbon twin needs. Only the members the twin uses are here, with Unity's
// semantics: a float magnitude is computed in double, so it does not overflow the way a naive
// float implementation would.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 up => new Vector3(0, 1, 0);
        public float magnitude => (float)Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static Vector3 operator /(Vector3 a, float f) => new Vector3(a.x / f, a.y / f, a.z / f);
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
}
