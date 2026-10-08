using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Wire protocol of the mod's multiplayer. First byte of every message = type. Positions are GLOBAL doubles (every
    /// machine has its own floating origin, see docs/MULTIPLAYER-ARCHITECTURE.md §4a).
    public static class Protocol
    {
        public const ushort Version = 23; // 23: players standing on a shared car ride in its frame (Entities.Footing); 22: held items in a seated player's car travel in its frame (Entities.Carried); 21: joiners placed next to the host (SpotReq/JoinSpot); 20: the sender's clock in player states (bodies interpolated, RemotePlayers); 19: the old owner's last state goes to the new one (Entities.HandoverState); 18: only the owner deletes a shared object; a lost copy is fetched again (Entities.Refetch); hum and pee in poses (PlayerActs); held flag in states, cargo claims (Entities.Cargo); 17: password in Hello, reject codes, kick, chat, voice (Net/Session.cs, Net/Voice.cs); 16: players' footsteps, burps, farts (Entities.PlayerSound); 15: the host's sandstorms (Entities.StormSync); 14: limbs of bodies in states (Entities.Ragdoll); 13: the host's physics-lock setting (Entities.PhysLockSync); 12: car signals in states (Entities.CarSignals); 11: radio stations follow the host (Entities.RadioSync); 10: pushing other players' objects (Entities.PushIn); 9: wheel travel as a fraction of the suspension; 8: compact car states (Entities.StateCodec), batched relays

        // client → server
        public const byte Hello = 1;          // u16 protocol, str modVersion, u64 steamId, str name, str password
        public const byte Chat = 5;           // client → server: str text.  server → client: u8 kind (0 player, 1 notice), str name, str text
        public const byte Roster = 7;         // server → client: u8 n, then n × (varint id, u16 ping ms), every 2 s
        public const byte Voice = 6;          // client → server: u8 codec, u16 seq, bytes.  server → client: varint id, u8 codec, u16 seq, bytes (unreliable)
        public const byte SpotReq = 8;        // client → server, before Ready: where to stand in the host's world
        public const byte JoinSpot = 9;       // server → client: u8 has, f64 gx, f64 gz (next to the host; has 0: stay at the start)
        public const byte Ready = 4;          // the client's copy of the host's world is loaded: send me everything
        public const byte State = 10;         // u16 seq, pos, yaw   (own player, unreliable)
        public const byte Pose = 14;          // PlayerLook pose (IK targets, head pitch, zoom, menus), unreliable, 20 Hz
        public const byte Outfit = 16;
        public const byte Batch = 18;         // server → client: varint len + message, repeated (unreliable traffic of one tick)        // PlayerLook outfit, reliable, on join and on change

        // server → client
        public const byte Welcome = 2;        // varint yourId, i32 seed, u16 tickHz, i32 startCar, i32 map
        public const byte Reject = 3;         // str reason, u8 code (RejectOther / RejectPassword / RejectKicked / RejectFull)
        public const byte RejectOther = 0, RejectPassword = 1, RejectKicked = 2, RejectFull = 3;
        public const byte PlayerJoined = 11;  // varint id, u64 steamId, str name
        public const byte PlayerLeft = 12;    // varint id
        public const byte OtherState = 13;    // varint id, u16 seq, pos, yaw   (unreliable)
        public const byte OtherPose = 15;     // varint id + pose (only to players within PlayerLook.PoseRangeM)
        public const byte OtherOutfit = 17;   // varint id + outfit (reliable; stored by the server for newcomers)

        public const int StateHz = 20;

        /// Interest tiers (server → client relay rate by distance between the two players).
        public const double NearM = 1000, MidM = 10000;
        public const float NearHz = 20f, MidHz = 4f, FarHz = 0.5f;

        /// Global position as integer centimetres (±21,474 km — the whole playable road and far beyond), yaw in 1/65536 turns.
        public static void WritePos(NetWriter w, Vector3d g)
        {
            w.I32(Cm(g.x)); w.I32(Cm(g.y)); w.I32(Cm(g.z));
        }
        public static Vector3d ReadPos(NetReader r) => new Vector3d(r.I32() * 0.01, r.I32() * 0.01, r.I32() * 0.01);
        /// Seated in a shared object: its network id + seat index (hierarchy order: same prefab on every machine).
        /// The sender's clock (ms, wraps after 49 days) in player states: bodies play back through a PoseInterpolator.
        public static void WriteStamp(NetWriter w, double t) { w.U32((uint)(long)Math.Round(t * 1000.0)); }
        public static double ReadStamp(NetReader r) { return r.U32() / 1000.0; }
        /// Standing on a shared object (a car's roof or bed, the back of the bus): its net id, the player's place and
        /// facing in its frame (Entities.Footing). Seated players send their seat instead.
        public struct Footing { public uint Net; public Vector3 Local; public float Yaw; }
        /// 0 = on the ground, 1 = seated (net, seat index), 2 = standing on a shared object (Footing).
        public static void WriteSeat(NetWriter w, uint net, int idx, Footing f = default)
        {
            if (net != 0) { w.U8(1); w.U32(net); w.U8((byte)idx); return; }
            if (f.Net != 0) { w.U8(2); w.U32(f.Net); w.F32(f.Local.x); w.F32(f.Local.y); w.F32(f.Local.z); WriteYaw(w, f.Yaw); return; }
            w.U8(0);
        }
        public static void ReadSeat(NetReader r, out uint net, out int idx) => ReadSeat(r, out net, out idx, out _);
        public static void ReadSeat(NetReader r, out uint net, out int idx, out Footing f)
        {
            net = 0; idx = 0; f = default;
            byte k = r.U8();
            if (k == 1) { net = r.U32(); idx = r.U8(); }
            else if (k == 2)
            {
                f.Net = r.U32(); f.Local = new Vector3(r.F32(), r.F32(), r.F32()); f.Yaw = ReadYaw(r);
                // a non-finite place on the car: no footing (the global position is used); a NaN placed the player nowhere
                if (!Entities.Finite(f.Local) || f.Local.sqrMagnitude > 1e4f) f = default;
            }
        }
        public static void WriteYaw(NetWriter w, float yaw) => w.U16((ushort)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 65536f));
        public static float ReadYaw(NetReader r) => r.U16() / 65536f * 360f;
        private static int Cm(double v) => (int)System.Math.Round(System.Math.Max(-2.1e9, System.Math.Min(2.1e9, v * 100.0)));
    }
}
