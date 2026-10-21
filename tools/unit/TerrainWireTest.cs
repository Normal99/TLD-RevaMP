using System;
using TLDRevamp.Net;
using TLDRevamp.WorldGen;

// Offline unit test of the terrain mode on the wire (issue #32).
// dotnet run --project tools/unit/TerrainWireTest.csproj
//
// It links the production TerrainModeSync and NetBuffer, so the bytes the host sends and the bytes the joiner reads
// are the same code the game runs. The rule under test: a joiner must end up with the host's mode, and a message it
// cannot make sense of must never change the world.
static class TerrainWireTest
{
    static int checks, fails;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    static NetReader Reader(byte[] bytes)
    {
        var r = new NetReader();
        r.Load(bytes.Length);
        Array.Copy(bytes, r.Buf, bytes.Length);
        return r;
    }

    static int Main()
    {
        // The host writes the mode; the joiner reads it back.
        foreach (var mode in TerrainModes.Selectable())
        {
            var w = new NetWriter();
            w.U8(TerrainModeSync.HostModeFor(mode));
            var r = Reader(w.Buf[..w.Len]);
            bool got = TerrainModeSync.Read(r, out var read);
            Check($"the host's {TerrainModes.Name(mode)} mode survives the wire",
                got && read == mode && !r.Bad && r.Remaining == 0);
        }

        // A host that sends nothing (an old build): the joiner keeps the game's own terrain, and the read is not an error.
        {
            var r = Reader(Array.Empty<byte>());
            bool got = TerrainModeSync.Read(r, out var read);
            Check("an old host (no mode) leaves the joiner on the default",
                !got && read == TerrainModes.Default && !r.Bad);
        }

        // A byte that names no mode: refused, the world is not changed.
        {
            var r = Reader(new byte[] { 99 });
            bool got = TerrainModeSync.Read(r, out var read);
            Check("an unknown mode byte is refused", !got && read == TerrainModes.Default);
        }

        // The old 2023 terrain arrives as itself (protocol 52).
        {
            var r = Reader(new byte[] { (byte)TerrainMode.OldTerrain });
            bool got = TerrainModeSync.Read(r, out var read);
            Check("the old terrain arrives on the wire", got && read == TerrainMode.OldTerrain);
        }

        // A truncated message: no mode, and the reader says so.
        {
            var w = new NetWriter();
            w.U8(TerrainModeSync.HostModeFor(TerrainMode.MainRoadOnly));
            var bytes = w.Buf[..w.Len];
            var r = Reader(bytes[..0]);   // the byte never arrived
            bool got = TerrainModeSync.Read(r, out _);
            Check("a truncated message yields no mode", !got);
        }

        // The mode is one byte: the whole cost of the feature on the wire.
        {
            var w = new NetWriter();
            w.U8(TerrainModeSync.HostModeFor(TerrainMode.MainRoadOnly));
            Check("the mode costs one byte", w.Len == 1);
        }

        // The host sends the mode that is running: every mode is one we offer.
        {
            foreach (var mode in TerrainModes.All)
            {
                byte b = TerrainModeSync.HostModeFor(mode);
                Check($"the host sends its own mode ({TerrainModes.Name(mode)})", b == (byte)mode);
            }
            Check("the host never sends a byte that is no mode", TerrainModeSync.HostModeFor((TerrainMode)7) == (byte)TerrainModes.Default);
        }

        Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
        return fails == 0 ? 0 : 1;
    }
}
