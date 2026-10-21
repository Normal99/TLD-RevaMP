#pragma warning disable CS8981
using System;
using System.IO;
using TLDRevamp.Replay;

// The replay's pure core (issue #36): key writing, sampling, the file format and its refusals, the playhead.
static class ReplayCoreTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }
    static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) <= eps;

    static ReplayRot Yaw(float degrees)
    {
        double h = degrees * Math.PI / 360.0;
        return new ReplayRot(0, (float)Math.Sin(h), 0, (float)Math.Cos(h));
    }

    static ObjKey Key(float t, float x, float yaw = 0, int parent = -1, bool visible = true) =>
        new ObjKey { T = t, Parent = parent, Pos = new ReplayVec(x, 0, 0), Rot = Yaw(yaw), Visible = visible };

    static int Main()
    {
        Rotations();
        ObjectKeys();
        ObjectSampling();
        Pieces();
        Players();
        Clock();
        Codec();
        Refusals();
        Files();
        Console.WriteLine("Replay core: " + checks + " checks passed");
        return 0;
    }

    static void Rotations()
    {
        Check(Near(ReplayRot.Angle(Yaw(0), Yaw(90)), 90f, 0.01f), "angle between yaws");
        Check(Near(ReplayRot.Angle(Yaw(10), Yaw(10)), 0f), "no angle to itself");
        var half = ReplayRot.Nlerp(Yaw(0), Yaw(90), 0.5f);
        Check(Near(ReplayRot.Angle(half, Yaw(45)), 0f, 0.5f), "nlerp half way is 45 degrees");
        var q = Yaw(30); var neg = new ReplayRot(-q.X, -q.Y, -q.Z, -q.W);
        Check(Near(ReplayRot.Angle(ReplayRot.Nlerp(Yaw(20), neg, 0.5f), Yaw(25)), 0f, 0.5f), "nlerp takes the short way round a negated quaternion");
        Check(new ReplayRot(0, 0, 0, 0).Normalized().W == 1, "a zero quaternion normalises to identity, not NaN");
    }

    static void ObjectKeys()
    {
        var t = new ObjectTrack();
        Check(t.Offer(Key(0, 0)) == 1, "the first sample is always a key");
        for (int i = 1; i <= 100; i++) t.Offer(Key(i * 0.05f, 0.0005f));
        Check(t.Keys.Count == 1, "a resting object writes no keys (under the position threshold)");
        // starts moving at t = 5.05: the rest pose is repeated at the last still sample (5.0) first
        Check(t.Offer(Key(5.05f, 1)) == 2, "the first move after a rest writes the hold key and the move");
        Check(Near(t.Keys[1].T, 5.0f) && Near(t.Keys[1].Pos.X, 0f), "the hold key is the rest pose at the last still sample");
        Check(t.Offer(Key(5.10f, 2)) == 1, "a continuing move writes one key");
        Check(t.Offer(Key(5.15f, 2, 0.1f)) == 0, "a turn under the angle threshold writes nothing");
        Check(t.Offer(Key(5.20f, 2, 1f)) == 2, "a turn over the threshold writes (after a hold)");
        Check(t.Offer(Key(5.25f, 2, 1f, parent: 3)) == 1, "a new parent writes a key even without moving");
        Check(t.Offer(Key(5.30f, 50, 1f, parent: 3, visible: false)) == 1, "hiding writes a key");
        Check(t.Offer(Key(5.35f, 90, 40f, parent: 3, visible: false)) == 0, "a hidden object's moves write nothing");
        Check(t.Offer(Key(5.0f, 7)) == 0, "a sample from the past is ignored");
        var l = new ObjectTrack(); var lk = Key(0, 0); l.Offer(lk); lk.T = 0.05f; lk.Lights = 3;
        Check(l.Offer(lk) == 1, "switching lights writes a key without moving");
        Check(l.Sample(0.06f, out var lit) && lit.Lights == 3 && l.Sample(0.01f, out var dark) && dark.Lights == 0, "lights are a step, not a blend");
    }

    static void ObjectSampling()
    {
        var t = new ObjectTrack();
        Check(!t.Sample(1, out _), "an empty track has no state");
        t.Offer(Key(1, 0)); t.Offer(Key(1.05f, 0)); t.Offer(Key(2, 10, 90));
        Check(t.Sample(0.5f, out var before) && Near(before.Pos.X, 0), "before the first key the first key holds");
        Check(t.Sample(1.525f, out var mid) && Near(mid.Pos.X, 5f, 0.01f), "halfway between keys is halfway (after the hold key)");
        Check(Near(ReplayRot.Angle(mid.Rot, Yaw(45)), 0f, 0.5f), "and turned halfway");
        Check(t.Sample(9, out var after) && Near(after.Pos.X, 10), "after the last key the last key holds");
        t.Offer(Key(3, 10, 90, parent: 0));
        Check(t.Sample(2.9f, out var step) && step.Parent == -1 && Near(step.Pos.X, 10), "no interpolation across a parent change");
        t.Offer(Key(4, 10, 90, parent: 0, visible: false));
        Check(t.Sample(4.5f, out var gone) && !gone.Visible, "hidden after the hide key");
    }

    static void Pieces()
    {
        var p = new PieceTrack();
        var k0 = new PieceKey { T = 0, Pos = new ReplayVec(1, 0, 0), Rot = Yaw(0) };
        Check(p.Offer(k0) == 0, "the first piece sample is the baseline, not a key");
        Check(p.Offer(new PieceKey { T = 0.05f, Pos = k0.Pos, Rot = Yaw(0.1f) }) == 0, "a still piece writes nothing");
        Check(p.Offer(new PieceKey { T = 0.10f, Pos = k0.Pos, Rot = Yaw(30) }) == 2, "the first turn writes the baseline and the turn");
        Check(Near(p.Keys[0].T, 0.05f), "the baseline is held to the last still sample");
        Check(p.Sample(0.075f, out var s) && Near(ReplayRot.Angle(s.Rot, Yaw(15)), 0f, 0.5f), "a piece interpolates");
        var q = new PieceTrack();
        q.Offer(k0); q.Pause();
        Check(q.Offer(new PieceKey { T = 9, Pos = new ReplayVec(5, 0, 0), Rot = Yaw(0) }) == 0, "after a pause the next sample is a fresh baseline");
        var o = new ReplayObject { PieceCount = 3 };
        Check(o.Piece(2) != null && o.Piece(3) == null && o.Piece(-1) == null, "pieces are bounded by the object's piece count");
    }

    static PlayerSample Ps(float t, float x, bool visible = true)
    {
        var s = new PlayerSample { T = t, Visible = visible, Body = new ReplayVec(x, 0, 0), BodyRot = Yaw(0), Eye = new ReplayVec(x, 1.6f, 0), EyeRot = Yaw(0) };
        for (int i = 0; i < ReplayLimbs.Count; i++) s.SetLimb(i, new ReplayVec(i, x, 0), Yaw(i));
        return s;
    }

    static void Players()
    {
        var p = new ReplayPlayer();
        Check(!p.Sample(0, out _), "no samples, no player");
        p.Samples.Add(Ps(0, 0)); p.Samples.Add(Ps(0.05f, 1)); p.Samples.Add(Ps(0.10f, 2));
        Check(p.Sample(0.025f, out var a) && Near(a.Body.X, 0.5f) && Near(a.Limb(3).Y, 0.5f), "body and limbs interpolate");
        Check(Near(a.Limb(4).X, 4f), "limbs keep their order");
        Check(!p.Sample(5, out _), "long after the last sample the player is gone");
        p.Samples.Add(Ps(3, 9));
        Check(!p.Sample(1.5f, out _), "a gap in the samples is an absence, not a slide across the map");
        p.Samples.Add(Ps(3.05f, 9, visible: false));
        Check(!p.Sample(3.06f, out _), "an invisible sample hides the player");
        var bad = Ps(0, 0); bad.L2 = new ReplayVec(float.NaN, 0, 0);
        Check(!bad.Finite && Ps(0, 0).Finite, "a sample with a NaN limb is not finite");
    }

    static void Clock()
    {
        var c = new ReplayClock(); c.Reset(10);
        Check(c.Playing && c.Speed == 1f, "a new playback plays at 1x");
        c.Advance(1); Check(Math.Abs(c.Time - 0.25) < 1e-9, "a long frame advances at most 0.25 s");
        c.Faster(); c.Advance(0.1); Check(Math.Abs(c.Time - 0.45) < 1e-9, "2x speed");
        for (int i = 0; i < 10; i++) c.Faster();
        Check(c.Speed == 4f, "the speed tops out at 4x");
        for (int i = 0; i < 10; i++) c.Slower();
        Check(c.Speed == 0.1f, "and bottoms out at 0.1x");
        c.Seek(-5); Check(c.Time == 0, "seeking before the start clamps");
        c.Seek(double.NaN); Check(c.Time == 0, "a NaN seek goes to the start");
        c.SetSpeed(4f); c.Seek(9.9); c.Advance(0.2);
        Check(c.Time == 10 && !c.Playing, "playback stops at the end");
        c.Toggle(); Check(c.Playing && c.Time == 0, "play at the end starts over");
        c.Jump(3); c.Jump(-1); Check(Math.Abs(c.Time - 2) < 1e-9, "jumps");
        Check(!c.SetSpeed(3f), "only the listed speeds are accepted");
        Check(ReplayClock.Clock(125.7) == "2:05" && ReplayClock.Clock(-3) == "0:00", "clock text");
        Check(ReplayLimits.InScope(260, true) && !ReplayLimits.InScope(260, false), "scope hysteresis");
    }

    static ReplayRecording Sample()
    {
        var rec = new ReplayRecording { Protocol = 51, OriginX = 1000.5, OriginY = 20, OriginZ = -3000.25, Duration = 2 };
        var car = new ReplayObject { SaveId = 77, Prefab = 12, PieceCount = 4, LightCount = 2 };
        car.Track.Offer(Key(0, 0)); var on = Key(1, 5, 20); on.Lights = 2; car.Track.Offer(on);
        car.Piece(2).Offer(new PieceKey { T = 0, Pos = new ReplayVec(0, 1, 0), Rot = Yaw(0) });
        car.Piece(2).Offer(new PieceKey { T = 0.5f, Pos = new ReplayVec(0, 1, 0), Rot = Yaw(45) });
        var wheel = new ReplayObject { SaveId = 78, Prefab = 3 };
        wheel.Track.Offer(Key(0, 1, 0, parent: 0)); wheel.Track.Offer(Key(1.5f, 1, 90, parent: 0));
        rec.Objects.Add(car); rec.Objects.Add(wheel);
        rec.ObjectKeys = car.Track.Keys.Count + wheel.Track.Keys.Count; rec.PieceKeys = car.Pieces[2].Keys.Count;
        var me = new ReplayPlayer { Local = true, Outfit = 1, OutfitEnabled = new[] { true, false }, OutfitColors = new[] { 1f, 0.5f, 0.25f, 1f } };
        me.Samples.Add(Ps(0, 0)); me.Samples.Add(Ps(0.05f, 0.1f));
        rec.Players.Add(me); rec.Players.Add(new ReplayPlayer());
        return rec;
    }

    static byte[] Bytes(ReplayRecording rec) { var m = new MemoryStream(); ReplayCodec.Write(m, rec); return m.ToArray(); }

    static void Codec()
    {
        var bytes = Bytes(Sample());
        var back = ReplayCodec.Read(new MemoryStream(bytes));
        Check(back.Protocol == 51 && back.OriginX == 1000.5 && back.OriginZ == -3000.25 && Near(back.Duration, 2), "header round-trips");
        Check(back.Objects.Count == 2 && back.Objects[0].SaveId == 77 && back.Objects[0].Prefab == 12 && back.Objects[0].PieceCount == 4, "objects round-trip");
        Check(back.Objects[0].Track.Keys.Count == Sample().Objects[0].Track.Keys.Count, "object keys round-trip");
        Check(back.Objects[1].Track.Keys[0].Parent == 0, "parents round-trip");
        Check(back.Objects[0].LightCount == 2 && back.Objects[0].Track.Keys[1].Lights == 2, "lights round-trip");
        Check(back.Objects[0].Pieces.Length == 4 && back.Objects[0].Pieces[2].Keys.Count == 2 && back.Objects[0].Pieces[1] == null, "only moving pieces are stored");
        Check(back.ObjectKeys == Sample().ObjectKeys && back.ObjectKeys == 4 && back.PieceKeys == 2, "key counts are rebuilt");
        Check(back.Players.Count == 2 && back.Players[0].Local && !back.Players[1].Local, "players round-trip");
        Check(back.Players[0].OutfitEnabled.Length == 2 && !back.Players[0].OutfitEnabled[1] && Near(back.Players[0].OutfitColors[2], 0.25f), "outfit round-trips");
        Check(back.Players[0].Samples.Count == 2 && Near(back.Players[0].Samples[1].Limb(5).Y, 0.1f), "player samples round-trip");
        Check(Bytes(back).Length == bytes.Length, "writing a read recording gives the same size");
        // no text in the file beyond the magic: a name could never hide in it
        int letters = 0; foreach (var b in bytes) if (b >= 'a' && b <= 'z') letters++;
        Check(letters < bytes.Length / 4, "the file is numbers, not text");

        string dir = Path.Combine(Path.GetTempPath(), "replaytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, ReplayFiles.Name(new DateTime(2026, 10, 21, 18, 5, 9)));
            ReplayCodec.WriteFile(path, Sample());
            var f = ReplayCodec.ReadFile(path);
            Check(f.Objects.Count == 2 && f.Players[0].Samples.Count == 2, "gzip file round-trips");
            bool refused = false;
            try { ReplayCodec.WriteFile(path, Sample()); } catch (IOException) { refused = true; }
            Check(refused, "a recording never overwrites another");
        }
        finally { Directory.Delete(dir, true); }
    }

    static bool Refused(byte[] bytes)
    {
        try { ReplayCodec.Read(new MemoryStream(bytes)); return false; }
        catch (FormatException) { return true; }
        catch (EndOfStreamException) { return true; }
    }

    static void Refusals()
    {
        var good = Bytes(Sample());
        Check(!Refused(good), "a good file reads");
        var magic = (byte[])good.Clone(); magic[0] = (byte)'X';
        Check(Refused(magic), "a wrong magic is refused");
        var format = (byte[])good.Clone(); format[8] = 9;
        Check(Refused(format), "an unknown format is refused");
        var trunc = new byte[good.Length - 5]; Array.Copy(good, trunc, trunc.Length);
        Check(Refused(trunc), "a truncated file is refused");
        var trailing = new byte[good.Length + 1]; Array.Copy(good, trailing, good.Length);
        Check(Refused(trailing), "trailing bytes are refused");

        var rec = Sample(); var k = rec.Objects[0].Track.Keys[1]; k.T = -1; rec.Objects[0].Track.Keys[1] = k;
        Check(Refused(Bytes(rec)), "time running backwards is refused");
        rec = Sample(); k = rec.Objects[1].Track.Keys[0]; k.Parent = 1; rec.Objects[1].Track.Keys[0] = k;
        Check(Refused(Bytes(rec)), "an object that is its own parent is refused");
        rec = Sample(); k = rec.Objects[1].Track.Keys[0]; k.Parent = 9; rec.Objects[1].Track.Keys[0] = k;
        Check(Refused(Bytes(rec)), "a parent that does not exist is refused");
        rec = Sample(); k = rec.Objects[0].Track.Keys[0]; k.Pos = new ReplayVec(float.PositiveInfinity, 0, 0); rec.Objects[0].Track.Keys[0] = k;
        Check(Refused(Bytes(rec)), "a non-finite position is refused");
        rec = Sample(); k = rec.Objects[0].Track.Keys[0]; k.Rot = new ReplayRot(0, 0, 0, 3); rec.Objects[0].Track.Keys[0] = k;
        Check(Refused(Bytes(rec)), "a rotation that is not a unit quaternion is refused");
        rec = Sample(); k = rec.Objects[0].Track.Keys[1]; k.Lights = 4; rec.Objects[0].Track.Keys[1] = k;
        Check(Refused(Bytes(rec)), "a light the object does not have is refused");
        rec = Sample(); rec.Duration = 1e9f;
        Check(Refused(Bytes(rec)), "an absurd duration is refused");
        rec = Sample(); rec.Players[1].Local = true;
        Check(Refused(Bytes(rec)), "two recording players are refused");
        rec = Sample(); rec.Players[0].OutfitColors = new float[3];
        Check(Refused(Bytes(rec)), "a broken colour list is refused");
        // a huge count must be refused before anything is allocated for it
        var huge = (byte[])good.Clone();
        int objectsAt = 8 + 4 + 4 + 4 + 24 + 4;
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, objectsAt);
        Check(Refused(huge), "a huge object count is refused");
    }

    static void Files()
    {
        Check(ReplayFiles.Name(new DateTime(2026, 1, 2, 3, 4, 5)) == "replay-20260102-030405.tldreplay", "file names are the save time");
        Check(ReplayFiles.SafeName("replay-20260102-030405.tldreplay"), "a replay name is safe");
        Check(!ReplayFiles.SafeName("../x.tldreplay") && !ReplayFiles.SafeName("/etc/passwd") && !ReplayFiles.SafeName("a\\b.tldreplay") &&
              !ReplayFiles.SafeName("x.json") && !ReplayFiles.SafeName("") && !ReplayFiles.SafeName(null), "paths and other files are not");
        string dir = Path.Combine(Path.GetTempPath(), "replaylist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "replay-20260101-000000.tldreplay"), "");
            File.WriteAllText(Path.Combine(dir, "replay-20261021-120000.tldreplay"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            var list = ReplayFiles.List(dir);
            Check(list.Count == 2 && Path.GetFileName(list[0]) == "replay-20261021-120000.tldreplay", "the list is newest first and only replays");
        }
        finally { Directory.Delete(dir, true); }
        Check(ReplayFiles.List(Path.Combine(dir, "missing")).Count == 0, "a missing folder lists nothing");
        Check(ReplayCommandPolicy.IsReadOnly("status") && ReplayCommandPolicy.IsReadOnly("") && ReplayCommandPolicy.IsReadOnly("list") &&
              ReplayCommandPolicy.IsReadOnly("near") && ReplayCommandPolicy.IsReadOnly("near 5") &&
              !ReplayCommandPolicy.IsReadOnly("record") && !ReplayCommandPolicy.IsReadOnly("play") && !ReplayCommandPolicy.IsReadOnly("nearly"), "command policy");
    }
}
