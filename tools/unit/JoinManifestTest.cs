using System;
using System.Collections.Generic;
using System.Linq;
using TLDRevamp.Net;

namespace UnityEngine { }
struct Vector3d
{
    internal double x, y, z;
    internal Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    internal double magnitude => Math.Sqrt(x*x+y*y+z*z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x-b.x,a.y-b.y,a.z-b.z);
}
namespace TLDRevamp.Net
{
    static class Protocol { internal const byte JoinLoading = 110; }
    static class Mp
    {
        internal static Recorder Server = new Recorder();
        internal static Vector3d LocalGlobal() => default;
    }
    sealed class Recorder
    {
        internal readonly List<byte[]> Messages = new List<byte[]>();
        internal bool SendToPlayer(int player, NetWriter w, bool reliable)
        { Messages.Add(w.Buf.Take(w.Len).ToArray()); return true; }
    }
    public static partial class Entities
    {
        internal sealed class SEnt { internal uint NetId, ParentNet; internal int PartIndex = -1; internal Vector3d Where; }
        internal sealed class Ent { internal object Root; internal Vector3d FarPos; }
        internal static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        internal static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        internal static readonly NetWriter W = new NetWriter();
        internal static readonly HashSet<uint> Known = new HashSet<uint>();
        private static bool _tpaSending, _tpaSendFailed;
        internal static bool Fail;
        internal static readonly List<uint> Sent = new List<uint>(), Refreshed = new List<uint>();
        private static void ServerLockedPose(SEnt s) { }
        private static HashSet<uint> KnownBy(int player) => Known;
        private static void SendObject(int player, SEnt s) { Sent.Add(s.NetId); if (_tpaSending && Fail) _tpaSendFailed = true; }
        private static void SendTpaState(int player, SEnt s) { Refreshed.Add(s.NetId); if (_tpaSending && Fail) _tpaSendFailed = true; }
        private static void ServerSendTo(int player, NetWriter w, bool reliable) => Mp.Server.SendToPlayer(player, w, reliable);
    }
}
static class JoinManifestTest
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    static int Main()
    {
        // Run the production manifest selection/sending code with transport and entity-storage observation fakes.
        Entities.Server[1] = new Entities.SEnt { NetId=1, Where=new Vector3d(10,0,0) };
        Entities.Server[2] = new Entities.SEnt { NetId=2, Where=new Vector3d(300,0,0) };
        Entities.Server[3] = new Entities.SEnt { NetId=3, Where=new Vector3d(301,0,0) };
        Entities.Server[4] = new Entities.SEnt { NetId=4, PartIndex=2, ParentNet=5, Where=new Vector3d(20,0,0) };
        Entities.Server[5] = new Entities.SEnt { NetId=5, Where=new Vector3d(1000,0,0) };
        Entities.Known.Add(2);
        Entities.ServerJoinLoading(7, default);
        Check(Mp.Server.Messages.Count == 2, "manifest and end marker");
        var bytes = Mp.Server.Messages[0]; var r = new NetReader { Buf=bytes, Pos=0, End=bytes.Length };
        Check(r.U8()==110 && r.U8()==0 && r.VarU32()==4, "wire header/count");
        var ids = new HashSet<uint>(); while (r.Remaining>0) ids.Add(r.U32());
        Check(ids.SetEquals(new uint[] {1,2,4,5}) && !r.Bad, "300 m boundary and detached parent dependency");
        Check(Entities.Sent.SequenceEqual(new uint[] {1,5}) && Entities.Refreshed.SequenceEqual(new uint[] {2}), "unknown roots sent and Known roots refreshed");
        Check(Mp.Server.Messages[1].SequenceEqual(new byte[] {110,1}), "end marker follows records");
        Mp.Server.Messages.Clear(); Entities.Fail=true;
        Entities.ServerJoinLoading(7, default);
        Check(Mp.Server.Messages.Count==1, "send failure never announces completion");
        Entities.ByNet[1] = new Entities.Ent { Root=new object() };
        Entities.ByNet[2] = new Entities.Ent { FarPos=new Vector3d(300,0,0) };
        Entities.ByNet[5] = new Entities.Ent { FarPos=new Vector3d(1000,0,0) };
        Check(Entities.JoinApplied(ids)==2, "missing part and nearby far record do not count as applied");
        Entities.ByNet[2].Root = new object(); Entities.ByNet[4] = new Entities.Ent { Root=new object() };
        Check(Entities.JoinApplied(ids)==4, "far-store rebind completes application count");
        Console.WriteLine("PASS join manifest: production selection, wire order, Known, send failure, far-store placement");
        return 0;
    }
}
