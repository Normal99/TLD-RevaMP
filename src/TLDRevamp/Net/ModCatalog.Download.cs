using System;
using System.IO;
using TLDRevamp.Multiplayer;
using UnityEngine.Networking;

namespace TLDRevamp.Net
{
    public static partial class ModCatalog
    {
        internal static BepInEx.Configuration.ConfigEntry<string> Url;
        internal static Catalog Current;
        internal static string Message = "";
        internal static bool RestartRequired;
        private static UnityWebRequest _request;
        private static LimitedDownload _download;
        private static Mod _mod;
        private static Release _release;
        internal static bool Busy => _request != null;
        internal static string Progress => Busy ? (_mod == null ? "Fetching catalog" : "Downloading " + _mod.Name) +
            " (" + ModTransfer.Size(_download.Length) + ", " + (_request.downloadProgress * 100f).ToString("F0") + "%)" : Message;

        internal static string Reason(Mod m, Release v) => ApprovalReason(m, v, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);
        // Catalog order is newest first; an unapproved version never hides an approved older one.
        internal static Release Offered(Mod m) => Offered(m, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);

        internal static void Refresh()
        {
            if (Busy) return;
            Current = null;
            string url = Url?.Value ?? DefaultUrl;
            if (!SourceUrl(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile))
            { Message = "Catalog URL must use HTTPS (or a file URI for local testing)."; return; }
            Start(url, MaxCatalogBytes, null, null);
        }

        internal static void Download(Mod m, Release v)
        {
            if (Busy) return;
            string reason = Reason(m, v);
            if (reason != null) { Message = reason; return; }
            Start(v.Url, (int)Math.Min(v.Size, ModTransfer.MaxBytes), m, v);
        }

        private static void Start(string url, int limit, Mod m, Release v)
        {
            Message = ""; _mod = m; _release = v;
            try
            {
                _download = new LimitedDownload(limit);
                _request = new UnityWebRequest(url, "GET", _download, null) { timeout = 45, redirectLimit = 8 };
                _request.SendWebRequest();
            }
            catch (Exception e) { Message = "Download could not start: " + e.Message; Dispose(); }
        }

        internal static void Cancel()
        {
            if (!Busy) return;
            _request.Abort(); Dispose(); Message = "Download cancelled.";
        }

        internal static void Tick()
        {
            if (!Busy) return;
            if (!ModsScreen.IsVisible) { ModsScreen.Close(); return; }
            if (_download.Exceeded) _request.Abort();
            if (!_request.isDone) return;
            try
            {
                if (_download.Exceeded) throw new InvalidDataException("Download exceeds the size limit.");
                if (_request.result != UnityWebRequest.Result.Success) throw new IOException(_request.error ?? "Request failed");
                var bytes = _download.Bytes;
                if (_mod == null) { Current = Parse(bytes); Message = Current.Mods.Count == 0 ? "No mods in this catalog." : "Select a mod for details."; }
                else
                {
                    var dll = Verify(_mod, _release, bytes, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);
                    string error = ModTransfer.Install(_mod.Id, _mod.Name, _release.Version, _release.Hash, dll);
                    // Install can report that the DLL was saved but an older copy could not be moved.
                    RestartRequired |= ModTransfer.ManagedFiles(_mod.Id).Count > 0;
                    Message = error ?? "Installed " + _mod.Name + " " + _release.Version + ".";
                }
            }
            catch (Exception e) { Message = (_mod == null ? "Offline or catalog unavailable: " : "Mod refused: ") + e.Message; }
            finally { Dispose(); }
        }

        private static void Dispose()
        {
            _request?.Dispose(); _request = null; _download = null; _mod = null; _release = null;
        }

        // Refuse a large Content-Length before buffering, and bound every chunk (including chunked responses).
        private sealed class LimitedDownload : DownloadHandlerScript
        {
            private readonly LimitedBuffer _data;
            internal bool Exceeded => _data.Exceeded;
            internal long Length => _data.Length;
            internal byte[] Bytes => _data.Bytes;
            internal LimitedDownload(int limit) : base(new byte[8192]) { _data = new LimitedBuffer(limit); }
            protected override void ReceiveContentLengthHeader(ulong length) => _data.ContentLength(length);
            protected override bool ReceiveData(byte[] data, int length) => _data.Add(data, length);
            public override void Dispose() { _data.Dispose(); base.Dispose(); }
        }
    }
}
