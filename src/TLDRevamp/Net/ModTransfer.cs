using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A joiner missing a mod the host uses (or with another version of it) gets it from the host, so nobody has to
    /// hunt for files before playing together. The danger: a mod is code, and a host could send anything. So the
    /// joiner never trusts the host's word:
    ///  - TLD Revamp ships a list of approved mod files (approved-mods.txt next to TLDRevamp.dll: one SHA-256 per line,
    ///    with the mod's id and version). A file whose hash isn't on it is never downloaded.
    ///  - The bytes that arrive are hashed again; they must be the approved file the host offered, or they are thrown
    ///    away.
    ///  - The player is asked first, with each mod's name, version and size.
    /// A new mod loads at the next start of the game (BepInEx loads plugins once), so the join ends with "restart the
    /// game and join again". Single-file mods only: a mod with more files is installed by hand.
    public static class ModTransfer
    {
        public const byte ModOffer = 81;   // s→c: varint n, n × (str id, str name, str version, str sha256, u32 size)
        public const byte ModFetch = 82;   // c→s: str id
        public const byte ModChunk = 83;   // s→c: str id, u32 offset, u32 total, bytes

        /// The host sends mods to joiners (false: joiners with other mods are refused with what to install).
        public static bool HostSends = true;
        public static int MaxBytes = 16 << 20;
        public const int ChunkBytes = 48 * 1024;
        public static long Offered, Sent, Installed, RefusedUnapproved, RefusedHash;

        public static string ApprovedPath => Path.Combine(Path.GetDirectoryName(typeof(ModTransfer).Assembly.Location) ?? "", "approved-mods.txt");
        public static string InstallDir => Path.Combine(BepInEx.Paths.PluginPath, "TLDRevampMods");

        public static string Stats() => "{\"hostSends\":" + (HostSends ? "true" : "false") + ",\"approved\":" + Approved().Count + ",\"offered\":" + Offered +
            ",\"sent\":" + Sent + ",\"installed\":" + Installed + ",\"refusedUnapproved\":" + RefusedUnapproved + ",\"refusedHash\":" + RefusedHash +
            ",\"installDir\":" + Json.Str(InstallDir) + "}";

        /// sha256 (lower-case hex) → "id version" of every approved mod file.
        public static Dictionary<string, string> Approved()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(ApprovedPath)) return d;
                foreach (var raw in File.ReadAllLines(ApprovedPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string hash = parts[0].ToLowerInvariant();
                    if (hash.Length != 64) continue;
                    d[hash] = parts.Length > 1 ? parts[1].Trim() : "";
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("approved-mods.txt: " + e.Message); }
            return d;
        }

        /// The host can offer this mod: its own file is there, small enough, and still the file it registered.
        public static bool CanSend(ModInfo m) =>
            HostSends && !string.IsNullOrEmpty(m.Hash) && !string.IsNullOrEmpty(m.File) && File.Exists(m.File) && new FileInfo(m.File).Length <= MaxBytes;

        internal static byte[] ReadForSend(ModInfo m)
        {
            try
            {
                var data = File.ReadAllBytes(m.File);
                return Sha256(data) == m.Hash ? data : null;   // changed on disk since it loaded: not what the joiner was told
            }
            catch (Exception e) { Plugin.Log.LogWarning($"mods: reading {m.File} to send: {e.Message}"); return null; }
        }

        internal static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        internal static string Size(long n) => n >= 1 << 20 ? (n / 1048576.0).ToString("F1") + " MB" : Math.Max(1, n / 1024) + " KB";

        // ------------------------------------------------------------------ the joiner

        internal sealed class Offer { public string Id, Name, Version, Hash; public uint Size; public byte[] Data; public uint Got; }

        /// Write a verified file into BepInEx/plugins/TLDRevampMods and set aside an older copy of the same mod (it
        /// would load next to the new one). Returns null, or what went wrong.
        internal static string Install(Offer o)
        {
            try
            {
                var old = ManagedFiles(o.Id);
                foreach (var m in MpApi.Mods)
                    if (m.Id == o.Id && !string.IsNullOrEmpty(m.File) && !old.Contains(m.File)) old.Add(m.File);
                string error = ModFiles.Install(InstallDir, o.Id, o.Version, o.Data, old);
                if (error != null) return o.Name + " " + o.Version + ": " + error;
                Installed++;
                Plugin.Log.LogInfo($"mods: installed {o.Name} {o.Version} (sha256 {MpApi.Short(o.Hash)})");
                return null;
            }
            catch (Exception e) { return $"{o.Name} could not be saved: {e.Message}"; }
        }

        internal static string Install(string id, string name, string version, string hash, byte[] bytes) =>
            Install(new Offer { Id = id, Name = name, Version = version, Hash = hash, Data = bytes });

        internal static List<string> ManagedFiles(string id)
        {
            var files = ModFiles.KnownFiles(InstallDir, id, Approved());
            foreach (var m in MpApi.Mods)
                if (m.Id == id && ModFiles.Managed(InstallDir, m.File) && !files.Contains(m.File)) files.Add(m.File);
            foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                if (p.Metadata.GUID == id && ModFiles.Managed(InstallDir, p.Location) && !files.Contains(p.Location)) files.Add(p.Location);
            return files;
        }

        internal static void Remove(string file) => ModFiles.Remove(InstallDir, file);
    }

    public sealed partial class MpServer
    {
        private sealed class Upload { public Peer P; public string Id; public byte[] Data; public int Sent; }
        private readonly List<Upload> _uploads = new List<Upload>();
        private readonly Dictionary<int, List<ModInfo>> _offeredMods = new Dictionary<int, List<ModInfo>>();

        private void OfferMods(Peer p, List<ModInfo> mods)
        {
            _offeredMods[p.Id] = mods;
            _w.Reset(); _w.U8(ModTransfer.ModOffer); _w.VarU32((uint)mods.Count);
            foreach (var m in mods) { _w.Str(m.Id); _w.Str(m.Name); _w.Str(m.Version); _w.Str(m.Hash); _w.U32((uint)new FileInfo(m.File).Length); }
            T.Send(p.Conn, _w, SteamTransport.SendReliable);
            ModTransfer.Offered += mods.Count;
            var names = new List<string>(); foreach (var m in mods) names.Add(m.ToString());
            Plugin.Log.LogInfo($"mods: player {p.Id} lacks {string.Join(", ", names)} - offered");
        }

        private void ModFetchFrom(Peer p, NetReader r)
        {
            string id = r.Str();
            if (r.Bad || !_offeredMods.TryGetValue(p.Id, out var list)) return;
            var m = list.Find(x => x.Id == id);
            if (m == null || _uploads.Exists(u => u.P == p && u.Id == id)) return;   // only what was offered, once
            var data = ModTransfer.ReadForSend(m);
            if (data == null) { Reject(p.Conn, $"The host's {m.Name} could not be sent (its file changed since the game started).", Protocol.RejectOther); return; }
            _uploads.Add(new Upload { P = p, Id = id, Data = data });
        }

        /// A few chunks per frame and joiner: Steam's reliable queue takes a whole mod at once, but then the joiner's
        /// other traffic (and everyone's, on a slow upload) waits behind it.
        internal void UploadTick()
        {
            for (int i = _uploads.Count - 1; i >= 0; i--)
            {
                var u = _uploads[i];
                if (!_peers.ContainsValue(u.P)) { _uploads.RemoveAt(i); continue; }
                for (int k = 0; k < 4 && u.Sent < u.Data.Length; k++)
                {
                    int n = Math.Min(ModTransfer.ChunkBytes, u.Data.Length - u.Sent);
                    _w.Reset(); _w.U8(ModTransfer.ModChunk); _w.Str(u.Id); _w.U32((uint)u.Sent); _w.U32((uint)u.Data.Length);
                    _w.VarU32((uint)n); _w.Bytes(u.Data, u.Sent, n);
                    T.Send(u.P.Conn, _w, SteamTransport.SendReliable);
                    u.Sent += n;
                }
                if (u.Sent >= u.Data.Length) { ModTransfer.Sent++; _uploads.RemoveAt(i); Plugin.Log.LogInfo($"mods: sent {u.Id} to player {u.P.Id} ({ModTransfer.Size(u.Data.Length)})"); }
            }
        }

        internal void ModsForgetPeer(int id) { _offeredMods.Remove(id); _uploads.RemoveAll(u => u.P.Id == id); }
    }

    public sealed partial class MpClient
    {
        private List<ModTransfer.Offer> _modOffers;
        private int _modNext;
        internal string LoadingMod => _modOffers != null && _modNext < _modOffers.Count ? _modOffers[_modNext].Name : null;
        internal uint LoadingModBytes => _modOffers != null && _modNext < _modOffers.Count ? _modOffers[_modNext].Got : 0;
        internal uint LoadingModTotal => _modOffers != null && _modNext < _modOffers.Count ? _modOffers[_modNext].Size : 0;

        private void ModOfferIn(NetReader r)
        {
            if (Bot != null) return;
            var list = new List<ModTransfer.Offer>();
            uint n = r.VarU32();
            for (uint i = 0; i < n && i < 64 && !r.Bad; i++)
                list.Add(new ModTransfer.Offer { Id = r.Str(), Name = Session.CleanName(r.Str()), Version = Session.CleanName(r.Str()), Hash = r.Str().ToLowerInvariant(), Size = r.U32() });
            if (r.Bad || list.Count == 0) { EndJoin("The host's mod list could not be read."); return; }
            var approved = ModTransfer.Approved();
            var bad = new List<string>(); var lines = new List<string>(); long total = 0;
            foreach (var o in list)
            {
                if (!approved.ContainsKey(o.Hash) || o.Size > ModTransfer.MaxBytes || !MpApi.ValidId(o.Id)) bad.Add($"{o.Name} {o.Version} (sha256 {MpApi.Short(o.Hash)})");
                lines.Add($"{o.Name} {o.Version} ({ModTransfer.Size(o.Size)})");
                total += o.Size;
            }
            if (bad.Count > 0)
            {
                ModTransfer.RefusedUnapproved += bad.Count;
                Plugin.Log.LogWarning("[anomaly] mods: the host offered mods that aren't on the approved list: " + string.Join(", ", bad));
                EndJoin("The host uses mods that aren't on TLD Revamp's approved list, so they weren't downloaded: " + string.Join(", ", bad) +
                        ". Install them yourself only if you trust them.");
                return;
            }
            _modOffers = list; _modNext = 0;
            MpScreen.Ask("The host uses mods you don't have: " + string.Join(", ", lines) + ". Download and install? (approved files, " + ModTransfer.Size(total) + "; the game restarts once)",
                () => FetchNextMod(),
                () => EndJoin("You need " + string.Join(", ", lines) + " to join this host."));
        }

        private void FetchNextMod()
        {
            if (_modOffers == null || Lost) return;
            if (_modNext >= _modOffers.Count)
            {
                var names = new List<string>(); foreach (var o in _modOffers) names.Add(o.Name + " " + o.Version);
                EndJoin("Installed " + string.Join(", ", names) + ". Restart the game, then join again.");
                return;
            }
            var next = _modOffers[_modNext];
            next.Data = new byte[next.Size]; next.Got = 0;
            Session.Show(1, "", $"Downloading {next.Name} {next.Version} ({ModTransfer.Size(next.Size)})...");
            _w.Reset(); _w.U8(ModTransfer.ModFetch); _w.Str(next.Id);
            T.Send(_conn, _w, SteamTransport.SendReliable);
        }

        private void ModChunkIn(NetReader r)
        {
            if (_modOffers == null || _modNext >= _modOffers.Count) return;
            var o = _modOffers[_modNext];
            string id = r.Str(); uint off = r.U32(), total = r.U32(); uint n = r.VarU32();
            if (r.Bad || id != o.Id || total != o.Size || off != o.Got || n > r.Remaining || off + n > total) { EndJoin($"The download of {o.Name} broke off."); return; }
            Buffer.BlockCopy(r.Buf, r.Pos, o.Data, (int)off, (int)n); r.Pos += (int)n;
            o.Got += n;
            if (JoinLoading.Visible) JoinLoading.State.Progress(Time.realtimeSinceStartupAsDouble);
            if (o.Got < o.Size) return;
            // what arrived must be the approved file that was offered — not just whatever the host sent
            string got = ModTransfer.Sha256(o.Data);
            if (got != o.Hash || !ModTransfer.Approved().ContainsKey(got))
            {
                ModTransfer.RefusedHash++;
                Plugin.Log.LogWarning($"[anomaly] mods: {o.Name} from the host hashes {MpApi.Short(got)}, offered as {MpApi.Short(o.Hash)}: thrown away");
                EndJoin($"The host sent a different file than the approved {o.Name} {o.Version}: it was thrown away.");
                return;
            }
            string err = ModTransfer.Install(o);
            o.Data = null;
            if (err != null) { EndJoin(err + "."); return; }
            _modNext++;
            FetchNextMod();
        }

        /// End the join before the game: Session.Tick stops the client and shows why.
        private void EndJoin(string reason)
        {
            _modOffers = null;
            RejectReason = reason; RejectCode = Protocol.RejectOther;
        }
    }
}
