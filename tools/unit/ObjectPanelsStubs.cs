global using tosaveitemscript = PanelSave;
global using carscript = PanelCar;
using System;
using System.Collections.Generic;
using TLDRevamp;

namespace UnityEngine
{
    internal static class Time { internal static double realtimeSinceStartupAsDouble; }
    internal struct Color { internal float R, G; internal static Color white => new Color(1, 1, 1); internal Color(float r, float g, float b, float a = 1) { R = r; G = g; } }
    internal struct Vector2 { internal static Vector2 zero => default; }
    internal struct Rect { internal Rect(float x, float y, float w, float h) { } }
    internal static class Screen { internal static int width = 1920, height = 1080; }
    internal static class Mathf { internal static float Min(float a, float b) => Math.Min(a, b); internal static float Max(float a, float b) => Math.Max(a, b); internal static float Clamp01(float n) => Math.Clamp(n, 0, 1); }
    internal struct Vector3
    {
        internal float x, y, z;
        internal static Vector3 zero => default;
        internal float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
        internal static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }
    internal struct Quaternion { internal float x, y, z, w; }
    internal sealed class Transform { internal Vector3 position; internal Quaternion rotation = new Quaternion { w = 1 }; }
    internal sealed class Rigidbody { internal Vector3 velocity; }
    internal enum EventType { Repaint, Layout, ScrollWheel }
    internal sealed class Event { internal static Event current = new Event(); internal EventType type; }
    internal sealed class RectOffset { internal RectOffset(int a, int b, int c, int d) { } }
    internal sealed class GUIContent { internal string text; }
    internal sealed class GUIStyleState { internal Color textColor; }
    internal sealed class GUIStyle { internal float CalcHeight(GUIContent c, float width) => (c.text == null ? 1 : c.text.Split('\n').Length) * 16; internal int fontSize; internal bool richText, wordWrap; internal RectOffset padding; internal GUIStyleState normal = new GUIStyleState(); internal GUIStyle(GUIStyle s) { } }
    internal sealed class GUISkin { internal GUIStyle label; }
    internal static class Texture2D { internal static object whiteTexture; }
    internal static class GUI
    {
        internal static Color color; internal static int depth;
        internal static GUISkin skin = new GUISkin();
        internal static Vector2 BeginScrollView(Rect r, Vector2 v, Rect content) => v;
        internal static void EndScrollView() { }
        internal static readonly List<(string Text, Color Color)> Labels = new List<(string, Color)>();
        internal static void Label(Rect r, GUIContent c, GUIStyle s) { Labels.Add((c.text, color)); }
        internal static void DrawTexture(Rect r, object t) { }
    }
}
namespace TLDRevamp
{
    internal static class SessionLog { internal static readonly EventRing PanelEventsRing = new EventRing(); internal static bool AllocCounter; }
}
namespace TLDRevamp.Net
{
    internal static class Reports { internal static bool Share; }
    internal static class Mp { internal static MpServer Server; internal static MpClient Client; }
    public static partial class Entities
    {
        internal static int MyId, GroupScans;
        internal static bool InSession = true;
        internal static string OverlayDescription(uint net, double now) => net == 0 ? "Aim at a shared object, sit in a car or hold an item" : "net " + net;
        internal static readonly uint[] TelemetryFocus = { 0, 0, 7, 0, 0 };
        internal sealed class PanelEntities : Dictionary<uint, Ent>
        {
            internal int Walks;
            internal new ValueCollection Values { get { Walks++; return base.Values; } }
        }
        internal static readonly PanelEntities ByNet = new PanelEntities();
        internal sealed class Ent
        {
            internal uint NetId, Epoch, ParentNet, LockFrame;
            internal int OwnerId, PartIndex = -1;
            internal tosaveitemscript Root;
            internal readonly List<tosaveitemscript> Items = new List<tosaveitemscript>();
            internal HashSet<int> Split;
            internal bool Proxy, Held, Stored, PoseLocked, EngRunning;
            internal float EngRpm;
            internal UnityEngine.Vector3 FarPos, LastVel;
            internal UnityEngine.Quaternion FarRot = new UnityEngine.Quaternion { w = 1 };
        }
        private static bool IsHeld(tosaveitemscript it) => false;
        private static bool IsStored(tosaveitemscript it) => false;
        private static Ent SharedGroupOf(tosaveitemscript it) { GroupScans++; throw new Exception("unexpected group scan"); }
    }
    public sealed partial class MpServer
    {
        private sealed class Peer { internal int Id, Conn; internal bool Ready; }
        private readonly Dictionary<uint, Peer> _peers = new Dictionary<uint, Peer>();
        internal readonly SteamTransport T = new SteamTransport();
        internal void Add(int id, bool ready = true) => _peers[(uint)id] = new Peer { Id = id, Conn = id, Ready = ready };
        internal bool SendToPlayer(int id, NetWriter w, bool reliable) => _peers.TryGetValue((uint)id, out var p) && p.Ready && T.Send(id, w, 1);
    }
    internal sealed class MpClient
    {
        internal bool Ready = true;
        internal readonly List<byte[]> Sent = new List<byte[]>();
        internal void SendRaw(NetWriter w, int flags) { var b = new byte[w.Len]; Array.Copy(w.Buf, b, b.Length); Sent.Add(b); }
    }
    internal sealed class SteamTransport
    {
        internal const int SendReliable = 1;
        internal readonly List<(int target, byte[] bytes)> Sent = new List<(int, byte[])>();
        internal bool Send(int target, NetWriter w, int flags) { var b = new byte[w.Len]; Array.Copy(w.Buf, b, b.Length); Sent.Add((target, b)); return true; }
    }
}
// Minimal fixtures for the real capture path, without game implementations.
internal sealed class PanelSave
{
    internal static int Reads;
    private readonly UnityEngine.Transform _transform = new UnityEngine.Transform();
    internal UnityEngine.Transform transform { get { Reads++; return _transform; } }
    internal UnityEngine.Rigidbody RB;
    internal attachablescript attachable;
    internal PhysicsPart P;
    internal carscript car;
    internal Radio radio;
    internal List<Usable> usables;
    internal Bus busdoorscript;
    internal List<partslotscript> partslotscripts;
}
internal sealed class PhysicsPart { internal Mount physlock; }
internal sealed class Mount { internal tosaveitemscript parentTosave; }
internal sealed class attachablescript { internal bool attached; internal tosaveitemscript tosave; }
internal sealed class partslotscript
{
    internal attachablescript Part;
    internal string[] tipus;
    internal bool hasPart() => Part != null;
    internal attachablescript part() => Part;
}
internal sealed class PanelCar { internal Engine Engine; internal bool ignition; internal Usable ignitionUsable; internal Tank Tank; internal List<partslotscript> DrivenWheelSlots; }
internal sealed class Engine { internal bool running; internal float rpm; }
internal sealed class Tank { internal Fluid F; }
internal sealed class Fluid { internal float GetAmount() => 8; }
internal sealed class Radio { internal float FR; internal bool ON, amfm; }
internal sealed class Usable { internal float xRot, yRot, slideValue, tunedFloat; internal int currentTurnState; }
internal sealed class Bus { internal Door[] doors; }
internal sealed class Door { internal Leaf[] doors; internal bool Bopened; }
internal sealed class Leaf { internal UnityEngine.Vector3 opened, closed, current; }
internal static class mainscript { internal static UnityEngine.Vector3 GlobalFromUnityPos(UnityEngine.Vector3 p) => p; }
namespace TLDRevamp.Fixes { internal static class SlotOwnerSave { internal static tosaveitemscript Owner(attachablescript at) => null; } }
