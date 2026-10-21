#pragma warning disable CS0649, CS8981, CS0414, CS0108, CS0169
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Color { public Color(float r, float g, float b) { } }
    public enum LogType { Error, Assert, Warning, Log, Exception }
    public class Object { public string name; public static void Destroy(Object o) { } }
    public class Component : Object { public Transform transform; public GameObject gameObject => new GameObject(); public T AddComponent<T>() where T : new() => new T(); }
    public class Transform : Component
    {
        public Vector3 position; public Vector3 localScale = Vector3.one; public Transform parent;
        public Vector3 TransformPoint(Vector3 p) => position + p;
        public Vector3 InverseTransformPoint(Vector3 p) => p - position;
        public void SetParent(Transform p, bool worldPositionStays = true) { parent = p; }
    }
    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string n) { name = n; }
        public Transform transform = new Transform();
        public bool activeSelf { get; private set; }
        public int layer;
        public void SetActive(bool value) { activeSelf = value; }
        public T AddComponent<T>() where T : new() => new T();
        public T GetComponentInChildren<T>(bool includeInactive = false) where T : new() => new T();
    }
    public class Mesh : Object
    {
        public Vector3[] vertices = Array.Empty<Vector3>(); public int vertexCount => vertices.Length;
        public Rendering.IndexFormat indexFormat;
        public void Clear() { }
        public void SetVertices(Vector3[] v) { vertices = v; }
        public void SetUVs(int c, Vector2[] v) { }
        public void SetTriangles(List<int> t, int submesh, bool calculateBounds = true) { }
        public void RecalculateNormals() { }
        public void RecalculateBounds() { }
        public bool isReadable => true;
    }
    public class MeshFilter : Component { public Mesh mesh; public Mesh sharedMesh; }
    public class Renderer : Component { public Material sharedMaterial; public Rendering.ShadowCastingMode shadowCastingMode; public bool receiveShadows; public Rendering.LightProbeUsage lightProbeUsage; public Rendering.ReflectionProbeUsage reflectionProbeUsage; }
    public class MeshRenderer : Renderer { }
    public class Shader : Object { public static Shader Find(string name) => new Shader(); }
    public class Material : Object { public Material(Shader s) { } public Color color; }
    public static class Time { public static int frameCount; public static float realtimeSinceStartup; }
    public static class Mathf { public static float Clamp(float v, float a, float b) => Math.Clamp(v, a, b); }
}

namespace UnityEngine.Rendering
{
    public enum IndexFormat { UInt16, UInt32 }
    public enum ShadowCastingMode { Off, On }
    public enum LightProbeUsage { Off, BlendProbes, UseProxyVolume, CustomProvided }
    public enum ReflectionProbeUsage { Off, BlendProbes }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x*f,a.y*f,a.z*f);
        public float sqrMagnitude => x*x+y*y+z*z;
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class HarmonyPatchAttribute : Attribute { public HarmonyPatchAttribute(Type t, string m) { } }
    public static class AccessTools
    {
        public static FieldRef<T,T2> FieldRefAccess<T,T2>(string name) => _ => default;
        public delegate T2 FieldRef<T,T2>(T instance);
    }
}

public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x=x; this.y=y; this.z=z; }
    public static Vector3d zero => new Vector3d(0,0,0);
}

public class mainscript   // test stub, not game code
{
    public static UnityEngine.Vector3 visszarakva = UnityEngine.Vector3.zero;
    public static mainscript s; public playerClass player = new playerClass();
    public static Vector3d GlobalFromUnityPos(UnityEngine.Vector3 u) => new Vector3d(u.x,u.y,u.z);
    public static UnityEngine.Vector3 UnityPosFromGlobal(Vector3d g) => new UnityEngine.Vector3((float)g.x,(float)g.y,(float)g.z);
}
public class playerClass { public UnityEngine.Transform transform = new UnityEngine.Transform(); }
public class settingsscript { public static settingsscript s = new settingsscript(); public settingsData S = new settingsData(); }
public class settingsData { public float FDrawDistance = 1000; public bool GetDistantTerrain() => true; }
public class menuhandler { public static menuhandler s = new menuhandler(); public mapSettingScript currentMainMap; }   // test stub, not game code
public class mapSettingScript { public List<roadGenScript> roadGens = new List<roadGenScript>(); public List<terrainGenScript> terrainGen = new List<terrainGenScript>(); }
public class roadGenScript
{
    public Dictionary<Vector3d,dotChunkClass> chunks = new Dictionary<Vector3d,dotChunkClass>();
    public float chunkSize = 2500;
    public class dotChunkClass { public List<roadClass> roads = new List<roadClass>(); }
    public class roadClass
    {
        public List<roadElementClass> roadElements = new List<roadElementClass>();
        public bool generated; public Vector3d start, end; public roadTypeEmu roadType;
    }
    public List<roadClass> RoadsNear(Vector3d p, bool all, poiGenScript.alignToRoadEmu align) => new List<roadClass>();
    public static roadClass NearestPointOnRoadBone(Vector3d p, List<roadClass> roads, out Vector3d result) { result=default; return null; }
    public class roadElementClass { public int roadID; public List<Vector3d> bonePos = new List<Vector3d>(); }
    public enum roadTypeEmu { mainRoad, sideRoad, other }
}
public class poiGenScript { public enum alignToRoadEmu { anyRoad } }
public class terrainGenScript { public bool isDistantTerrain; public float drawDistance; public float tileSize; public int resolution; public int layer; }
public class newTerrainTileScript { public UnityEngine.Transform t; public UnityEngine.MeshFilter mf; public UnityEngine.Transform transform; }
public class newNewRandomMapGen { public float gridSize; public int resolution; public newFarTerrainScript nfts; public UnityEngine.Transform transform; public class MeshToGenerate { public UnityEngine.Vector3 p; } public void UpdMesh(UnityEngine.MeshFilter f, MeshToGenerate m) { } }
public class newFarTerrainScript { }
public class visszarako : UnityEngine.Component { public bool moveWhenParented; public bool dontlookUnderMap; }
public class itemdatabase { public static itemdatabase s = new itemdatabase(); public UnityEngine.GameObject Road(int id) => null; }
namespace TLDRevamp { public static class Plugin { public static readonly Logger Log = new Logger(); } public class Logger { public void LogWarning(string s) { } } }
// The detailed terrain ring (Fixes/DetailedTerrain.cs) is not part of this test: its reach is the game's own here.
namespace TLDRevamp.Fixes { internal static class DetailedTerrain { internal static float EffectiveDistance(terrainGenScript gen) => gen == null ? 0f : gen.drawDistance; } }
