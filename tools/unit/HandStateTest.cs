using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        public partial class Ent
        {
            internal bool HandPose;
            internal readonly HandItemGlide HandDraw = new HandItemGlide();
        }
        private static void TestHandState()
        {
            var lp = new Vector3(-1.9994f, .1234f, 2); var lq = Quaternion.Euler(71, -122, 38);
            foreach (uint car in new uint[] { 0, 595 })
            {
                var w = new NetWriter();
                WriteStateHead(w, 123, 7, 2, 3.45, new Vector3d(10, 20, 30), Quaternion.identity, Vector3.zero, false,
                    held: true, carriedBy: car, localPos: new Vector3(3, 4, 5), localRot: Quaternion.identity, handPose: true, handPos: lp, handRot: lq);
                int head = w.Len; w.U8(77);
                var without = new NetWriter();
                WriteStateHead(without, 123, 7, 2, 3.45, new Vector3d(10, 20, 30), Quaternion.identity, Vector3.zero, false,
                    held: true, carriedBy: car, localPos: new Vector3(3, 4, 5), localRot: Quaternion.identity);
                IdleTrafficTest.Check(head - without.Len == 13, "hand flag adds exactly 13 bytes with or without a car frame");
                var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadStateHead(r, out uint net, out uint epoch, out double stamp, out var world, out _, out _, out _, out _, out bool held, out _);
                IdleTrafficTest.Check(ReadHandPose && held && net == 123 && epoch == 7 && stamp == 3.45 && world.x == 10 && ReadCarriedBy == car &&
                    (car == 0 || (ReadLocalPos - new Vector3(3, 4, 5)).sqrMagnitude == 0),
                    "hand flag round trip retains world and car frame, car=" + car);
                IdleTrafficTest.Check((ReadHandPos - lp).magnitude < .0008f && Quaternion.Angle(ReadHandRot, lq) < .01f && r.U8() == 77 && !r.Bad && r.Pos == r.End,
                    "13-byte millimetre/smallest-three hand pose precedes state tail");
                var copy = new NetWriter(); ReplayLockedState(copy, w.Buf.AsSpan(0, w.Len).ToArray(), new Vector3d(40, 50, 60), Quaternion.identity);
                var replay = new NetReader { Buf = copy.Buf, Pos = 1, End = copy.Len };
                ReadStateHead(replay, out _, out _, out _, out world, out _, out _, out _, out _, out _, out _);
                IdleTrafficTest.Check(ReadHandPose && world.x == 40 && ReadCarriedBy == car && replay.U8() == 77, "cached state rewrite preserves hand extension and tail");
                for (int end = head - 13; end < head; end++)
                {
                    var shortRead = new NetReader { Buf = w.Buf, Pos = 1, End = end };
                    ReadStateHead(shortRead, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                    IdleTrafficTest.Check(shortRead.Bad, "truncated hand extension rejected");
                }
                w.Reset(); WriteStateHead(w, 123, 7, 3, 4, default, Quaternion.identity, default, false, held: true);
                r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadStateHead(r, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                IdleTrafficTest.Check(!ReadHandPose && ReadHandPos.sqrMagnitude == 0 && Quaternion.Angle(ReadHandRot, Quaternion.identity) == 0 && !r.Bad && r.Pos == r.End,
                    "absent hand flag consumes no bytes and clears previous read");
            }
            foreach (string mode in new[] { "drop", "stored", "locked" })
            {
                var w = new NetWriter(); WriteStateHead(w, 1, 1, 1, 1, default, Quaternion.identity, default, false,
                    held: mode != "drop", stored: mode == "stored", locked: mode == "locked", handPose: true, handPos: lp, handRot: lq);
                var r = new NetReader { Buf = w.Buf, Pos = 1, End = w.Len };
                ReadStateHead(r, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                IdleTrafficTest.Check(!ReadHandPose && !r.Bad && r.Pos == r.End, mode + " cannot retain a hand-relative payload");
            }
            ReadHandPose = true;
            var compact = new NetWriter(); CompactPoseCodec.Write(compact, 1, 1, 1, 1, 0, 0, 0, 0, false);
            ReadEntityHead(new NetReader { Buf = compact.Buf, Pos = 1, End = compact.Len }, true, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            IdleTrafficTest.Check(!ReadHandPose, "compact state clears previous full-state hand flag");
            IdleTrafficTest.Check(SampleControl(false, false, false, false, false, false, false, false, wHand: true),
                "grip changes bypass quiet filtering even when the world root did not move");
        }
    }
}
