using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TLDRevamp.Net
{
    /// Format 1 is discovery only. The local approved list is always the trust anchor.
    /// No Unity or filesystem writes here: these checks also run in the offline tests.
    public static partial class ModCatalog
    {
        public const string DefaultUrl = "https://raw.githubusercontent.com/Normal99/TLD-RevaMP/main/packaging/mods.json";
        public const int MaxCatalogBytes = 1 << 20;
        [DataContract] public sealed class Catalog
        {
            [DataMember(Name = "format")] public int Format;
            [DataMember(Name = "mods")] public List<Mod> Mods;
        }
        [DataContract] public sealed class Mod
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "author")] public string Author;
            [DataMember(Name = "description")] public string Description;
            [DataMember(Name = "source")] public string Source;
            [DataMember(Name = "versions")] public List<Release> Versions;
        }
        [DataContract] public sealed class Release
        {
            [DataMember(Name = "version")] public string Version;
            [DataMember(Name = "url")] public string Url;
            [DataMember(Name = "file")] public string File;
            [DataMember(Name = "sha256")] public string Hash;
            [DataMember(Name = "size")] public long Size;
            [DataMember(Name = "revamp")] public string Revamp;
        }

        public static bool ReleaseUrl(string url)
        {
            if (url == null || !Regex.IsMatch(url,
                @"\Ahttps://github\.com/[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+/releases/download/[^/\s?#\\]+/[^/\s?#\\]+\z") ||
                !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Host != "github.com" || u.Port != 443 || u.UserInfo.Length != 0) return false;
            // Check the original path too: Uri normalizes dot segments before exposing AbsolutePath.
            return SafeEntry(Uri.UnescapeDataString(url.Substring("https://github.com/".Length)));
        }

        public static bool SourceUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            u.Scheme == "https" && u.UserInfo.Length == 0;

        public static Catalog Parse(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxCatalogBytes) throw new InvalidDataException("Catalog size limit exceeded or empty catalog.");
            Catalog c;
            using (var s = new MemoryStream(data, false))
                c = (Catalog)new DataContractJsonSerializer(typeof(Catalog)).ReadObject(s);
            if (c == null || c.Format != 1 || c.Mods == null) throw new InvalidDataException("Unsupported mod catalog format (expected 1).");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            c.Mods.RemoveAll(m => m == null || string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id) || m.Versions == null);
            foreach (var m in c.Mods)
            {
                m.Versions.RemoveAll(v => v == null || !ReleaseUrl(v.Url));
                if (!SourceUrl(m.Source)) m.Source = null;
            }
            c.Mods.RemoveAll(m => m.Versions.Count == 0);
            return c;
        }

        public static string ApprovalReason(Mod m, Release v, IDictionary<string, string> approved, int maxBytes, Func<string, bool> validId)
        {
            if (m == null || v == null || !validId(m.Id)) return "Invalid mod id.";
            if (!ModFiles.SafeVersion(v.Version)) return "Invalid mod version.";
            if (!ReleaseUrl(v.Url)) return "Only GitHub release assets are allowed.";
            if (v.Size <= 0 || v.Size > maxBytes) return "Download exceeds the size limit.";
            if (!string.IsNullOrEmpty(v.File) && (!SafeEntry(v.File) || !v.File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) return "Invalid DLL path in archive.";
            string hash = (v.Hash ?? "").ToLowerInvariant();
            if (!Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || !approved.TryGetValue(hash, out string line))
                return "Not approved in this RevaMP version." + (string.IsNullOrEmpty(v.Revamp) ? "" : " Needs RevaMP " + v.Revamp + ".");
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != m.Id || parts[1] != v.Version) return "Approved id/version does not match the catalog.";
            return null;
        }

        public static Release Offered(Mod m, IDictionary<string, string> approved, int maxBytes, Func<string, bool> validId) =>
            m.Versions.Find(v => ApprovalReason(m, v, approved, maxBytes, validId) == null) ?? m.Versions[0];

        // Backslashes, rooted paths and dot segments are refused on every OS, including harmless-looking directories.
        public static bool SafeEntry(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] == '/' || path.Contains("\\") || path.Contains(":")) return false;
            foreach (var p in path.TrimEnd('/').Split('/'))
            {
                if (p.Length == 0 || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)) return false;
                foreach (char c in p) if (char.IsControl(c)) return false;
            }
            return true;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal sealed class LimitedBuffer : IDisposable
        {
            private readonly MemoryStream _data = new MemoryStream();
            private readonly int _limit;
            internal bool Exceeded { get; private set; }
            internal long Length => _data.Length;
            internal byte[] Bytes => _data.ToArray();
            internal LimitedBuffer(int limit) { _limit = limit; }
            internal void ContentLength(ulong length) { if (length > (ulong)_limit) Exceeded = true; }
            internal bool Add(byte[] data, int length)
            {
                if (Exceeded || data == null || length < 0 || length > data.Length || length > _limit - _data.Length) { Exceeded = true; return false; }
                _data.Write(data, 0, length); return true;
            }
            public void Dispose() => _data.Dispose();
        }

        public static byte[] Verify(Mod m, Release v, byte[] download, IDictionary<string, string> approved, int maxBytes, Func<string, bool> validId)
        {
            string reason = ApprovalReason(m, v, approved, maxBytes, validId);
            if (reason != null) throw new InvalidDataException(reason);
            if (download == null || download.Length == 0 || download.Length > maxBytes || download.LongLength != v.Size)
                throw new InvalidDataException("Download size differs from the catalog or exceeds the limit.");
            byte[] dll = download;
            if (!string.IsNullOrEmpty(v.File))
            {
                using (var stream = new MemoryStream(download, false))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry selected = null;
                    long total = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (!SafeEntry(entry.FullName)) throw new InvalidDataException("Unsafe archive path.");
                        if (entry.Length > maxBytes - total) throw new InvalidDataException("Archive uncompressed size exceeds the limit.");
                        total += entry.Length;
                        if (entry.FullName == v.File)
                        {
                            if (selected != null) throw new InvalidDataException("Duplicate DLL entry.");
                            selected = entry;
                        }
                        else if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive contains another DLL.");
                    }
                    if (selected == null || selected.Length == 0) throw new InvalidDataException("Catalog DLL is missing from the archive.");
                    using (var input = selected.Open())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (output.Length + n > maxBytes || output.Length + n > selected.Length) throw new InvalidDataException("DLL uncompressed size exceeds the limit.");
                            output.Write(buffer, 0, n);
                        }
                        if (output.Length != selected.Length) throw new InvalidDataException("Truncated archive DLL.");
                        dll = output.ToArray();
                    }
                }
            }
            if (dll.Length > maxBytes || Sha256(dll) != v.Hash.ToLowerInvariant()) throw new InvalidDataException("DLL hash differs from the approved catalog file; refused.");
            return dll;
        }
    }
}
