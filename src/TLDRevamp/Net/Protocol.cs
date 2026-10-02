using UnityEngine;

namespace TLDRevamp.Net
{
    /// Wire protocol of the mod's multiplayer. First byte of every message = type. Positions are GLOBAL doubles (every
    /// machine has its own floating origin, see docs/MULTIPLAYER-ARCHITECTURE.md §4a).
    public static class Protocol
    {
        public const ushort Version = 9; // 9: wheel travel as a fraction of the suspension; 8: compact car states (Entities.StateCodec), batched relays

        // client → server
        public const byte Hello = 1;          // u16 protocol, str modVersion, u64 steamId, str name
        public const byte Ready = 4;          // the client's copy of the host's world is loaded: send me everything
        public const byte State = 10;         // u16 seq, pos, yaw   (own player, unreliable)
        public const byte Pose = 14;          // PlayerLook pose (IK targets, head pitch, zoom, menus), unreliable, 20 Hz
        public const byte Outfit = 16;
        public const byte Batch = 18;         // server → client: varint len + message, repeated (unreliable traffic of one tick)        // PlayerLook outfit, reliable, on join and on change

        // server → client
        public const byte Welcome = 2;        // varint yourId, i32 seed, u16 tickHz, i32 startCar, i32 map
        public const byte Reject = 3;         // str reason
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
        public static void WriteSeat(NetWriter w, uint net, int idx) { if (net == 0) { w.U8(0); return; } w.U8(1); w.U32(net); w.U8((byte)idx); }
        public static void ReadSeat(NetReader r, out uint net, out int idx) { net = 0; idx = 0; if (r.U8() == 0) return; net = r.U32(); idx = r.U8(); }
        public static void WriteYaw(NetWriter w, float yaw) => w.U16((ushort)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 65536f));
        public static float ReadYaw(NetReader r) => r.U16() / 65536f * 360f;
        private static int Cm(double v) => (int)System.Math.Round(System.Math.Max(-2.1e9, System.Math.Min(2.1e9, v * 100.0)));
    }
}
