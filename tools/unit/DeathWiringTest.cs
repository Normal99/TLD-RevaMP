// The production death hooks (src/TLDRevamp/Net/SessionRules.Death.cs) with the game replaced by stubs: the
// state machine's decisions are the real ones, and the test drives the real Harmony hooks by reflection, the
// way the game would call them.
#pragma warning disable CS0649, CS8981
global using mainscript = DeathFakes.Main;
global using fpscontroller = DeathFakes.Player;
global using kaposztaleves = DeathFakes.Dev;

using System;
using TLDRevamp.Net;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => default;
        public static Quaternion Euler(float x, float y, float z) => new Quaternion { x = x, y = y, z = z, w = 1f };
    }
    public class Transform
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 eulerAngles => new Vector3(rotation.x, rotation.y, rotation.z);
        public void SetPositionAndRotation(Vector3 pos, Quaternion rot) { position = pos; rotation = rot; }
        /// The game's transform has a root: the topmost GameObject of the hierarchy. The stub has no parent, so it
        /// is its own root.
        public GameObject root { get; set; }
    }
    public class Rigidbody { public Vector3 velocity, angularVelocity; public Vector3 position; public bool isKinematic; }
    public class GameObject
    {
        public Transform transform = new Transform();
        public GameObject() { transform.root = this; }
    }
    public struct RaycastHit { public Vector3 point; }
    public static class Time
    {
        /// The stub's clock: the test moves it, so the machine's timers are driven by the test.
        public static double Now = 0;
        public static double realtimeSinceStartupAsDouble => Now;
        public static double unscaledTimeAsDouble => Now;
    }
}

/// The game's Vector3d is in the global namespace (the game's own assembly), not in UnityEngine.
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type t, string name) { }
        public HarmonyPatch(Type t, string name, Type[] args) { }
    }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
}

namespace DeathFakes
{
    public sealed class Dev
    {
        public static Dev s;
        public DevSettings settings = new DevSettings();
        public sealed class DevSettings { public bool god, fly; }
    }
    public sealed class Main
    {
        public static Main s;
        public Player player;
        public bool died;
        public static Vector3d GlobalFromUnityPos(UnityEngine.Vector3 p) => new Vector3d(p.x, p.y, p.z);
        public static UnityEngine.Vector3 UnityPosFromGlobal(Vector3d g) => new UnityEngine.Vector3((float)g.x, (float)g.y, (float)g.z);
    }
    public sealed class Player
    {
        public bool died, godModeTillGrounded, Bsitting;
        public UnityEngine.Transform transform = new UnityEngine.Transform();
        public UnityEngine.Transform BodyRot = new UnityEngine.Transform();
        public UnityEngine.Transform TpickedUpPos = new UnityEngine.Transform(), TpickedUpParent = new UnityEngine.Transform(), TpickedUpParent2 = new UnityEngine.Transform();
        public UnityEngine.Rigidbody RB = new UnityEngine.Rigidbody();
        public Pickupable pickedUp, inHandP;
        public sealed class Pickupable { public UnityEngine.Transform transform = new UnityEngine.Transform(); }
        public bool TeleportFailed;
        // The game's Teleport can fail (the game throws); the production code catches it and calls it a fallback.
        public void Teleport(UnityEngine.Vector3 at, UnityEngine.Vector3 rot)
        {
            if (TeleportFailed) throw new Exception("teleport failed");
            transform.position = at;
        }
        public void Death() { died = true; }
        public void Respawn() { died = false; }
    }
}

namespace TLDRevamp
{
    static class Plugin
    {
        internal static PluginLog Log = new PluginLog();
        internal sealed class PluginLog { internal void LogWarning(string s) { } internal void LogInfo(string s) { } }
    }
    static class Json { internal static string Str(string s) => s; }

    /// The bridge's ground probe, stubbed: this test has no physics, so the seated anchor keeps the height it was
    /// given. The production code's use of the probe is what the seated-anchor test checks (the horizontal point
    /// comes from the vehicle, not from the seat).
    static class DebugBridge
    {
        /// The height the stub's ground probe answers with. NaN = no ground (the world is not generated there).
        public static float GroundY = float.NaN;
        internal static UnityEngine.RaycastHit? GroundHit(float x, float z)
            => float.IsNaN(GroundY) ? (UnityEngine.RaycastHit?)null
                                    : new UnityEngine.RaycastHit { point = new UnityEngine.Vector3(0, GroundY, 0) };
    }
}

namespace TLDRevamp.Net
{
    /// The production SessionRules, with the parts the death file needs that live in other files.
    public static partial class SessionRules
    {
        static bool InSession => true;
        public static long Respawns;
    }

    public static partial class Entities
    {
        internal static bool Finite(Vector3d v) => !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) ||
            double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        internal static bool Finite(UnityEngine.Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }

    /// The record budget's owner, stubbed: the test counts the records the production hooks ask for.
    public static class PlayerSave
    {
        public static int Records;
        public static int DeathRecords, RespawnRecords, AliveRecords;
        internal static void SendDeathRecord() { Records++; DeathRecords++; }
        internal static void SendRespawnRecord() { Records++; RespawnRecords++; }
        internal static void SendAliveRecord() { Records++; AliveRecords++; }
    }
}

static class DeathWiringTest
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }

    private static void Main()
    {
        // The production state machine, read through reflection: the test checks the shipped object, not a copy.
        static object Machine() => typeof(TLDRevamp.Net.SessionRules)
            .GetField("Death", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        static object Member(object machine, string name)
        {
            var t = machine.GetType();
            var prop = t.GetProperty(name);
            if (prop != null) return prop.GetValue(machine);
            var field = t.GetField(name);
            if (field != null) return field.GetValue(machine);
            throw new Exception("the production state machine has no member " + name);
        }
        static int Num(object machine, string name)
        {
            var v = Member(machine, name);
            return v is Enum e ? Convert.ToInt32(e) : Convert.ToInt32(v);
        }
        static bool Flag(object machine, string name) => (bool)Member(machine, name);

        // The production hooks are private Harmony classes, so the test drives them the way the game does:
        // through the public entry points the patch declares.
        var capture = typeof(TLDRevamp.Net.SessionRules).GetNestedType("CaptureDeath", System.Reflection.BindingFlags.NonPublic);
        var put = typeof(TLDRevamp.Net.SessionRules).GetNestedType("PutDropsAtDeath", System.Reflection.BindingFlags.NonPublic);
        Check(capture != null && put != null, "the production death hooks are there");

        var prefix = capture.GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var postfix = capture.GetMethod("Postfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var dropPrefix = put.GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var dropPostfix = put.GetMethod("Postfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check(prefix != null && postfix != null && dropPrefix != null && dropPostfix != null, "and their methods");

        // The state machine's state, the production one.
        var death = Machine();
        Check(Num(death, "Phase") == 0, "alive at the start");

        var pl = new DeathFakes.Player();
        DeathFakes.Main.s = new DeathFakes.Main { player = pl };
        DeathFakes.Dev.s = new DeathFakes.Dev();
        pl.transform.position = new UnityEngine.Vector3(100, 0, 200);
        // the session: the anchor belongs to it
        typeof(TLDRevamp.Net.SessionRules).GetField("DeathSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .SetValue(null, 11);

        // ---- a real death: the anchor is captured
        object[] args = new object[] { pl, false };
        prefix.Invoke(null, args);
        Check((bool)args[1], "the death was accepted");
        Check(Num(death, "Deaths") == 1, "one death accepted");
        Check(Flag(death, "HasAnchor"), "the anchor is there");
        // mainscript.Died runs inside Death2, so the death screen is up: the death record goes out
        DeathFakes.Main.s.died = true;
        postfix.Invoke(null, new object[] { true });
        Check(TLDRevamp.Net.PlayerSave.Records == 1, "the death screen sent exactly one record");

        // ---- a duplicate death is refused: no second anchor, no second record
        pl.died = true;
        args = new object[] { pl, false };
        prefix.Invoke(null, args);
        Check(!(bool)args[1], "a duplicate death is refused");
        Check(Num(death, "RejectedAlreadyDead") == 1, "and counted as such");
        Check(Num(death, "Deaths") == 1, "no second death was accepted");
        Check(TLDRevamp.Net.PlayerSave.Records == 1, "and no second record went out");

        // ---- the respawn: the things drop at the anchor
        DeathFakes.Main.s.died = false;
        pl.died = false;
        dropPrefix.Invoke(null, new object[] { pl });
        Check(Math.Abs(pl.transform.position.x - 100) < 0.001 && Math.Abs(pl.transform.position.z - 200) < 0.001,
            "the player stands at the death point, not where the body fell");
        // the outcome is committed when the respawn really took
        dropPostfix.Invoke(null, new object[] { pl, true });
        Check(Num(death, "AnchorDrops") == 1, "the anchored drop is counted");
        Check(Num(death, "AnchorFallbacks") == 0, "and it is not a fallback");
        Check(TLDRevamp.Net.PlayerSave.Records == 2, "the respawn sent one fresh record");
        Check(!Flag(death, "HasAnchor"), "the anchor is spent");

        // ---- a placement that fails is a fallback, never an anchored drop
        var death2 = Machine();
        var pl2 = new DeathFakes.Player { TeleportFailed = true };
        DeathFakes.Main.s = new DeathFakes.Main { player = pl2 };
        pl2.transform.position = new UnityEngine.Vector3(5, 0, 5);
        var p2 = new object[] { pl2, false };
        prefix.Invoke(null, p2);
        Check((bool)p2[1], "the second death is accepted");
        dropPrefix.Invoke(null, new object[] { pl2 });
        dropPostfix.Invoke(null, new object[] { pl2, true });
        Check(Num(death2, "AnchorDrops") == 1, "the first drop is still the only anchored one");
        Check(Num(death2, "AnchorFallbacks") == 1, "the failed placement is a fallback");

        // ---- a respawn that does not take keeps the death screen
        var death3 = Machine();
        var pl3 = new DeathFakes.Player();
        DeathFakes.Main.s = new DeathFakes.Main { player = pl3 };
        var p3 = new object[] { pl3, false };
        prefix.Invoke(null, p3);
        DeathFakes.Main.s.died = true;
        int before = TLDRevamp.Net.PlayerSave.Records;
        dropPostfix.Invoke(null, new object[] { pl3, true });
        Check(TLDRevamp.Net.PlayerSave.Records == before, "a respawn that did not take sends no alive record");
        Check(Num(death3, "RespawnFailures") == 1, "and is counted as a respawn failure");
        Check(Num(death3, "Phase") == 1, "the death screen still holds");
        Check(Flag(death3, "HasAnchor"), "and the anchor survives for the respawn that will");

        // ---- a death with god mode is refused: the game ignores it
        var death4 = Machine();
        var pl4 = new DeathFakes.Player();
        DeathFakes.Main.s = new DeathFakes.Main { player = pl4 };
        DeathFakes.Dev.s = new DeathFakes.Dev();
        DeathFakes.Dev.s.settings.god = true;
        var p4 = new object[] { pl4, false };
        prefix.Invoke(null, p4);
        Check(!(bool)p4[1], "a death with god mode is refused");
        Check(Num(death4, "RejectedImmortal") == 1, "and counted");
        DeathFakes.Dev.s.settings.god = false;

        // ---- a seated death: the game leaves the seat itself, the death is still a death
        var deathS = Machine();
        var plS = new DeathFakes.Player { Bsitting = true };
        DeathFakes.Main.s = new DeathFakes.Main { player = plS };
        DeathFakes.Dev.s = new DeathFakes.Dev();
        var pS = new object[] { plS, false };
        prefix.Invoke(null, pS);
        Check((bool)pS[1], "a seated death is accepted");
        Check(Num(deathS, "SeatedDeaths") == 1, "and counted as a seated death");
        Check(Num(deathS, "Deaths") == 4, "it is a death like any other");   // the three deaths before it, plus this one
        Check(Flag(deathS, "HasAnchor"), "a seated death still has its anchor");
        Check(Flag(deathS, "AnchorSeated"), "and the anchor says it is a seated one");

        // ---- the anchor path off: the death is accepted but there is no anchor
        var deathNo = Machine();
        var plNo = new DeathFakes.Player();
        DeathFakes.Main.s = new DeathFakes.Main { player = plNo };
        DeathFakes.Dev.s = new DeathFakes.Dev();
        bool old = TLDRevamp.Net.SessionRules.DeathDropAnchor;
        int anchorsBefore = Num(deathNo, "AnchorsCaptured");
        int deathsBefore = Num(deathNo, "Deaths");
        TLDRevamp.Net.SessionRules.DeathDropAnchor = false;
        var pNo = new object[] { plNo, false };
        prefix.Invoke(null, pNo);
        Check((bool)pNo[1], "a death without the anchor path is still a death");
        Check(Num(deathNo, "Deaths") == deathsBefore + 1, "and is counted");
        Check(!Flag(deathNo, "HasAnchor"), "but there is no anchor");
        Check(Num(deathNo, "AnchorsCaptured") == anchorsBefore, "and no anchor was captured");
        // the respawn with no anchor is no anchored drop
        int dropsBefore = Num(deathNo, "AnchorDrops");
        dropPrefix.Invoke(null, new object[] { plNo });
        dropPostfix.Invoke(null, new object[] { plNo, true });
        Check(Num(deathNo, "AnchorDrops") == dropsBefore, "no anchored drop without an anchor");
        Check(Num(deathNo, "AnchorAbsent") == 1, "and it is counted as an absent anchor");
        TLDRevamp.Net.SessionRules.DeathDropAnchor = old;

        // ---- the seated anchor: the ground under the vehicle, not the seat
        {
            var deathSeat = Machine();
            int seatedBefore = Num(deathSeat, "SeatedDeaths");
            var plSeat = new DeathFakes.Player { Bsitting = true };
            DeathFakes.Main.s = new DeathFakes.Main { player = plSeat };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            // the seat is a metre and a half above the ground (the vehicle's floor)
            plSeat.transform.position = new UnityEngine.Vector3(60, 1.5f, 70);
            TLDRevamp.DebugBridge.GroundY = 0f;
            var pSeat = new object[] { plSeat, false };
            prefix.Invoke(null, pSeat);
            Check((bool)pSeat[1], "the seated death is accepted");
            Check(Num(deathSeat, "SeatedDeaths") == seatedBefore + 1, "and counted as a seated death");
            // the anchor's height is the ground, not the seat
            double ay = Convert.ToDouble(Member(deathSeat, "AnchorY"));
            Check(Math.Abs(ay - 0.0) < 0.001, "the seated anchor is on the ground, not at the seat's height");
            double ax = Convert.ToDouble(Member(deathSeat, "AnchorX"));
            double az = Convert.ToDouble(Member(deathSeat, "AnchorZ"));
            Check(Math.Abs(ax - 60.0) < 0.001 && Math.Abs(az - 70.0) < 0.001,
                "the seated anchor is under the vehicle");
            TLDRevamp.DebugBridge.GroundY = float.NaN;
        }
        {
            // No ground yet (the world is streamed): the anchor keeps the seat's own height rather than a wrong one.
            var deathSeat2 = Machine();
            var plSeat2 = new DeathFakes.Player { Bsitting = true };
            DeathFakes.Main.s = new DeathFakes.Main { player = plSeat2 };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            plSeat2.transform.position = new UnityEngine.Vector3(80, 1.5f, 90);
            TLDRevamp.DebugBridge.GroundY = float.NaN;
            var pSeat2 = new object[] { plSeat2, false };
            prefix.Invoke(null, pSeat2);
            Check((bool)pSeat2[1], "the seated death is accepted");
            double ay2 = Convert.ToDouble(Member(deathSeat2, "AnchorY"));
            Check(Math.Abs(ay2 - 1.5) < 0.001, "without a ground the anchor keeps the height it had");
        }

        // ---- the host's death: the death is counted, but the host sends no per-player record
        {
            // The record budget's owner is stubbed here, so the test drives the count itself. On the real host
            // the record cannot be sent at all (there is no per-player record for the host), so the death must
            // raise `deaths` without raising `deathRecords`: a counter that claimed a record that never left the
            // machine would be a lie in the evidence.
            var d = new TLDRevamp.Net.DeathState(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 3);
            Check(d.Deaths == 1, "the host's death is counted");
            Check(d.DeathRecords == 0, "but no record went out");
            Check(!d.AllowPeriodicRecord, "and the death screen holds the budget");
            d.DeathRecordFailed();   // the host's record could not be sent
            Check(d.Suppressed == 1, "the failure is counted as suppressed");
            Check(d.DeathRecords == 0, "and still no record was counted");
        }

        // ---- the anchor of another session is never used
        {
            var deathX = Machine();
            var plX = new DeathFakes.Player();
            DeathFakes.Main.s = new DeathFakes.Main { player = plX };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            plX.transform.position = new UnityEngine.Vector3(70, 0, 80);
            var pX = new object[] { plX, false };
            prefix.Invoke(null, pX);
            Check((bool)pX[1], "the death is accepted");
            // the session changes under the death: the anchor belongs to the old session now
            typeof(TLDRevamp.Net.SessionRules).GetField("DeathSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .SetValue(null, (int)12);
            float xPos = plX.transform.position.x;
            int dropsBeforeX = Num(deathX, "AnchorDrops");
            int absentBeforeX = Num(deathX, "AnchorAbsent");
            dropPrefix.Invoke(null, new object[] { plX });
            dropPostfix.Invoke(null, new object[] { plX, true });
            Check(Math.Abs(plX.transform.position.x - xPos) < 0.001, "a stale anchor does not move the player");
            Check(Num(deathX, "AnchorDrops") == dropsBeforeX, "and it is not an anchored drop");
            Check(Num(deathX, "AnchorAbsent") == absentBeforeX + 1, "it is counted as an absent anchor");
        }

        // ---- a death that never respawns: the anchor is given up and the record resumes
        {
            var deathA = Machine();
            var plA = new DeathFakes.Player();
            DeathFakes.Main.s = new DeathFakes.Main { player = plA };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            plA.transform.position = new UnityEngine.Vector3(30, 0, 40);
            var pA = new object[] { plA, false };
            prefix.Invoke(null, pA);
            Check((bool)pA[1], "the death is accepted");
            Check(Flag(deathA, "HasAnchor"), "the anchor is there");
            // the death is given up (the player left, the world reloaded): the self-heal sees the player alive.
            // The heal waits a moment first, so a death that was accepted a moment ago is never mistaken for a
            // death that was given up.
            DeathFakes.Main.s.died = false;
            plA.died = false;
            UnityEngine.Time.Now += 10.0;   // the death is old enough to be given up
            typeof(TLDRevamp.Net.SessionRules).GetMethod("DeathSyncWithGame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, null);
            Check(!Flag(deathA, "HasAnchor"), "the anchor is given up with the death");
            Check(Num(deathA, "Phase") == 0, "the machine is alive again");
            Check(Num(deathA, "DeathsAbandoned") == 1, "the abandoned death is counted");
            Check(Flag(deathA, "AllowPeriodicRecord"), "the periodic record is allowed again");
        }

        // ---- a fresh death is never given up by the self-heal
        {
            var deathF = Machine();
            int abandonedBeforeF = Num(deathF, "DeathsAbandoned");
            var plF = new DeathFakes.Player();
            DeathFakes.Main.s = new DeathFakes.Main { player = plF };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            plF.transform.position = new UnityEngine.Vector3(15, 0, 25);
            var pF = new object[] { plF, false };
            prefix.Invoke(null, pF);
            Check((bool)pF[1], "the death is accepted");
            // the game's flags are still false for a moment (they are set inside Death2): the heal must not act
            DeathFakes.Main.s.died = false;
            plF.died = false;
            typeof(TLDRevamp.Net.SessionRules).GetMethod("DeathSyncWithGame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, null);
            Check(Flag(deathF, "HasAnchor"), "a fresh death keeps its anchor");
            Check(Num(deathF, "DeathsAbandoned") == abandonedBeforeF, "and is not counted as abandoned");
        }

        // ---- the very first death's anchor is usable
        {
            // The session id starts at 0, which is a valid session: with -1 the first death's anchor would never
            // have been usable and the drop would have fallen back every time (until the first host or join).
            var death0 = Machine();
            var pl0 = new DeathFakes.Player();
            DeathFakes.Main.s = new DeathFakes.Main { player = pl0 };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            pl0.transform.position = new UnityEngine.Vector3(33, 0, 44);
            var p0 = new object[] { pl0, false };
            prefix.Invoke(null, p0);
            Check((bool)p0[1], "the death is accepted");
            int dropsBefore0 = Num(death0, "AnchorDrops");
            dropPrefix.Invoke(null, new object[] { pl0 });
            dropPostfix.Invoke(null, new object[] { pl0, true });
            Check(Num(death0, "AnchorDrops") == dropsBefore0 + 1, "the first death's anchor is usable");
        }

        // ---- a respawn without a session death (the death was refused or given up)
        {
            // The host still needs to know the player is alive, so a plain alive record goes out. It must not be
            // counted as the death's respawn record: there was no session death to respawn from.
            var deathN = Machine();
            var plN = new DeathFakes.Player();
            DeathFakes.Main.s = new DeathFakes.Main { player = plN };
            DeathFakes.Dev.s = new DeathFakes.Dev();
            // no death was accepted: the machine is alive (the reset puts it back to alive)
            typeof(TLDRevamp.Net.SessionRules).GetMethod("DeathReset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, null);
            Check(Num(deathN, "Phase") == 0, "the machine is alive before the respawn");
            int recordsBeforeN = TLDRevamp.Net.PlayerSave.Records;
            int respawnBeforeN = TLDRevamp.Net.PlayerSave.RespawnRecords;
            int aliveBeforeN = TLDRevamp.Net.PlayerSave.AliveRecords;
            dropPostfix.Invoke(null, new object[] { plN, true });
            Check(TLDRevamp.Net.PlayerSave.Records == recordsBeforeN + 1, "the respawn sent one alive record");
            Check(TLDRevamp.Net.PlayerSave.AliveRecords == aliveBeforeN + 1, "and it is an alive record");
            Check(TLDRevamp.Net.PlayerSave.RespawnRecords == respawnBeforeN, "and not a respawn record");
        }

        // ---- a reset (a stop or a rehost) leaves no anchor
        typeof(TLDRevamp.Net.SessionRules).GetMethod("DeathReset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, null);
        var death5 = Machine();
        Check(!Flag(death5, "HasAnchor"), "the reset leaves no anchor");
        Check(Num(death5, "Phase") == 0, "and the machine is alive again");

        Console.WriteLine($"PASS DeathWiring: {checks} checks");
    }
}
