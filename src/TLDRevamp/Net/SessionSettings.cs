using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Server-owned settings and their single reliable update path. Password changes affect future joins; every other
    /// field is also visible read-only on joiners' own card.
    public sealed partial class MpServer
    {
        public readonly SessionSettingsSnapshot SessionSettings = new SessionSettingsSnapshot();

        internal void BeginSessionSettings()
        {
            var bytes = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            uint token = BitConverter.ToUInt32(bytes, 0);
            if (token == 0) token = 1;
            SessionSettings.SessionToken = token;
            SessionSettings.Revision = 1;
            SessionSettings.TpaEnabled = Tpa.Enabled;
            SessionSettings.TpaCooldownS = Tpa.Enabled ? Tpa.CooldownS : 0;
            SessionSettings.JoinAtHost = SessionSettingsDefaults.JoinAtHost;
            SessionSettings.DeveloperPolicy = SessionSettingsDefaults.DeveloperPolicy;
            SessionSettings.HasPassword = Password.Length > 0;
            SessionSettings.VoiceEveryone = SessionSettingsDefaults.VoiceEveryone;
            SessionSettings.Normalize();
            ApplySessionSettingsLocally();
        }

        private void ApplySessionSettingsLocally()
        {
            Tpa.Enabled = SessionSettings.TpaEnabled;
            if (SessionSettings.TpaEnabled) Tpa.CooldownS = SessionSettings.TpaCooldownS;
            MpServer.JoinAtHost = SessionSettings.JoinAtHost;
            DeveloperAccess.Apply();
        }

        internal SessionSettingsSnapshot UpdateSessionSettings(Action<SessionSettingsSnapshot> change, bool persistDefaults)
        {
            var s = SessionSettings;
            var old = s.Copy();
            change(s);
            s.HasPassword = Password.Length > 0;
            s.Normalize();
            s.Revision++;
            ApplySessionSettingsLocally();
            if (persistDefaults)
            {
                SessionSettingsDefaults.TpaChoice = Math.Max(0, Array.IndexOf(Tpa.Choices, s.TpaEnabled ? s.TpaCooldownS : -1));
                SessionSettingsDefaults.JoinAtHost = s.JoinAtHost;
                SessionSettingsDefaults.VoiceEveryone = s.VoiceEveryone;
                RevampSettings.Save();
            }
            PublishSessionSettings(old);
            return s.Copy();
        }

        internal void SetSessionPassword(string password, bool persistDefault)
        {
            var old = SessionSettings.Copy();
            Password = SessionSettingsRules.CleanPassword(password);
            SessionSettings.HasPassword = Password.Length > 0;
            SessionSettings.Revision++;
            // The password is never written to disk: the session default is an in-memory value only, so a
            // settings file, a report or a log cannot carry it. See SessionSettingsDefaults.Migrate.
            if (persistDefault) { SessionSettingsDefaults.RememberPassword(Password); RevampSettings.Save(); }
            ApplySessionSettingsLocally();
            PublishSessionSettings(old);
        }

        internal void PublishSessionSettings(SessionSettingsSnapshot previous = null)
        {
            var w = new NetWriter();
            w.U8(Protocol.SessionSettings);
            SessionSettings.Write(w);
            SendToAll(w, true, 0, null, 0);
            if (previous != null) Tpa.OnSettingsChanged(previous, SessionSettings);
        }

        /// The settings snapshot for one peer. A peer that was admitted but is not ready yet (it is loading the
        /// host's world) can have missed a change published while it was loading, so it gets the current snapshot
        /// again when it says Ready.
        private void SendSessionSettingsTo(Peer peer)
        {
            if (peer == null) return;
            var w = new NetWriter();
            w.U8(Protocol.SessionSettings);
            SessionSettings.Write(w);
            T.Send(peer.Conn, w, SteamTransport.SendReliable);
        }

        /// The host changes the developer policy of this session. Only the host can do it, and the change is
        /// published to everyone (the joiners' dev access follows it). The policy is session-only: it is not saved.
        internal SessionSettingsSnapshot SetDeveloperPolicy(DeveloperAccessPolicy policy)
        {
            return UpdateSessionSettings(s => s.DeveloperPolicy = policy, persistDefaults: false);
        }
    }

    /// Persisted host defaults and session-scoped developer access. The policy itself is intentionally not saved.
    public static class SessionSettingsDefaults
    {
        public const DeveloperAccessPolicy DefaultDeveloperPolicy = DeveloperAccessPolicy.HostOnly;

        internal static int TpaChoice
        {
            get
            {
                var e = RevampSettings.Entries.Find(x => x.Key == "tpa");
                return e?.Get != null ? e.Get() : Math.Max(0, Array.IndexOf(Tpa.Choices, Tpa.Enabled ? Tpa.CooldownS : -1));
            }
            set
            {
                var e = RevampSettings.Entries.Find(x => x.Key == "tpa");
                if (e?.Set != null) e.Set(value);
            }
        }

        public static DeveloperAccessPolicy DeveloperPolicy = DefaultDeveloperPolicy;

        /// The password of the last session, in memory only. It is never written to the settings file: a file on disk
        /// is readable by anything that can read the machine, and it would travel into report archives and backups.
        private static string _sessionPassword = "";

        /// The password the host screen offers when the field is empty. The safe default is an empty password (no
        /// password): a host that wants one types it.
        public static string Password => _sessionPassword;

        /// Remember the password for this game session only (in memory).
        internal static void RememberPassword(string password) => _sessionPassword = SessionSettingsRules.CleanPassword(password);

        /// The password used when the host screen's field is empty: the session default, or nothing.
        internal static string HostPasswordDefault => _sessionPassword;

        public static bool JoinAtHost
        {
            get => RevampSettings.Int("hostJoinAtHost", 1) != 0;
            set => RevampSettings.SetInt("hostJoinAtHost", value ? 1 : 0);
        }

        public static bool VoiceEveryone
        {
            get => RevampSettings.Int("hostVoiceEveryone", 0) != 0;
            set => RevampSettings.SetInt("hostVoiceEveryone", value ? 1 : 0);
        }

        internal static void ResetSessionPolicy() => DeveloperPolicy = DefaultDeveloperPolicy;

        /// The one-time migration of the old settings file: a `hostPassword=` line with a plaintext value is removed
        /// (the file is rewritten without it) and the value is kept in memory for this session only. A file that
        /// already has no such line is left alone. The return value says whether the file was changed.
        public static bool Migrate()
        {
            try
            {
                var path = RevampSettings.SettingsFilePath;
                if (!System.IO.File.Exists(path)) return false;
                var lines = System.IO.File.ReadAllLines(path);
                if (!SessionSettingsRules.StripPlaintextPassword(lines, out string password, out string[] kept)) return false;
                // the value goes into memory for this session and leaves the file
                if (_sessionPassword.Length == 0 && password.Length > 0) _sessionPassword = password;
                System.IO.File.WriteAllLines(path, kept);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("session settings: the old password not removed: " + e.Message); return false; }
        }
    }

    /// Applies the server's policy to the game's real BDevMode gate. The saved preference is captured once and never
    /// overwritten by the temporary off state; teardown restores it even when the session ends abnormally.
    [HarmonyLib.HarmonyPatch]
    public static class DeveloperAccess
    {
        private static bool _captured;
        private static bool _savedPreference;
        private static bool _runtimeForcedOff;
        private static SessionSettingsSnapshot _clientSettings;

        public static long DeniedBridgeCommands, Applied, Restored;

        internal static SessionSettingsSnapshot ClientSettings => _clientSettings?.Copy();

        internal static void ApplyClientSnapshot(SessionSettingsSnapshot settings, bool initial)
        {
            if (settings == null) return;
            if (initial) { RestorePreference(true); _clientSettings = settings.Copy(); }
            else
            {
                if (!SessionSettingsSnapshot.Accept(_clientSettings, settings)) return;
                _clientSettings = settings.Copy();
            }
            Apply();
        }

        internal static void ResetClient()
        {
            RestorePreference(true);
            _clientSettings = null;
        }

        internal static bool IsHost => Mp.Server != null;

        internal static DeveloperAccessPolicy Policy
        {
            get
            {
                if (Mp.Server != null) return Mp.Server.SessionSettings.DeveloperPolicy;
                if (Mp.Client != null && Mp.Client.Ready && _clientSettings != null) return _clientSettings.DeveloperPolicy;
                return SessionSettingsDefaults.DefaultDeveloperPolicy;
            }
        }

        public static bool Allowed
        {
            get
            {
                if (Mp.Server == null && Mp.Client == null) return true;
                var policy = Policy;
                return policy == DeveloperAccessPolicy.Everyone || policy == DeveloperAccessPolicy.HostOnly && IsHost;
            }
        }

        internal static void Apply()
        {
            if (Mp.Server == null && Mp.Client == null) { RestorePreference(true); return; }
            bool allow = Allowed;
            if (!allow)
            {
                var script = settingsscript.s;
                if (script == null || script.S == null) return;
                if (!_captured) Capture(script.S.BDevMode);
                if (script.S.BDevMode) script.S.BDevMode = false;
                _runtimeForcedOff = true;
                ForceOffUiAndFeatures();
                Applied++;
            }
            else RestorePreference(false);
        }

        private static void RestorePreference(bool clear)
        {
            if (_captured)
            {
                var script = settingsscript.s;
                if (script != null && script.S != null) script.S.BDevMode = _savedPreference;
                SyncConfigToggle();
                Restored++;
            }
            _runtimeForcedOff = false;
            if (clear) { _captured = false; _savedPreference = false; }
        }

        private static void Capture(bool preference) { _captured = true; _savedPreference = preference; }

        private static void ForceOffUiAndFeatures()
        {
            var script = settingsscript.s;
            if (script == null || script.S == null) return;
            script.S.BDevMode = false;
            SyncConfigToggle();
            try
            {
                var dev = kaposztaleves.s;
                if (dev != null) { dev.settings.Disabled(); dev.UpdDevBools(); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("developer access reset: " + e.Message); }
        }

        private static void SyncConfigToggle()
        {
            try
            {
                var s = settingsscript.s;
                if (s != null && s.tabMisc != null && s.tabMisc.toggles != null && s.tabMisc.toggles.Length > 18 && s.tabMisc.toggles[18] != null)
                    s.tabMisc.toggles[18].ChangeWithoutNotify(s.S != null && s.S.BDevMode);
            }
            catch { }
        }

        internal static bool IsRuntimeOverrideActive => _runtimeForcedOff;
        internal static bool HasCapturedPreference => _captured;
        internal static bool CapturedPreference => _savedPreference;
        internal static void CaptureSavedPreference(bool value) { if (!_captured) Capture(value); }
        internal static bool RestoreCapturedPreference(bool clear)
        {
            bool value = _savedPreference;
            RestorePreference(clear);
            return value;
        }
        internal static void ForceOffForTest() { _runtimeForcedOff = true; ForceOffUiAndFeatures(); }

        internal static bool DenyBridge()
        {
            DeniedBridgeCommands++;
            return false;
        }

        internal static string Status()
        {
            var policy = Policy;
            var snapshot = Mp.Server != null ? Mp.Server.SessionSettings : ClientSettings;
            return "{\"allowed\":" + (Allowed ? "true" : "false") + ",\"host\":" + (IsHost ? "true" : "false") +
                   ",\"policy\":\"" + policy + "\",\"captured\":" + (_captured ? "true" : "false") + ",\"forcedOff\":" + (_runtimeForcedOff ? "true" : "false") +
                   ",\"savedPreference\":" + (_savedPreference ? "true" : "false") + ",\"deniedBridge\":" + DeniedBridgeCommands +
                   ",\"applied\":" + Applied + ",\"restored\":" + Restored + ",\"settings\":" + (snapshot != null ? snapshot.Json() : "null") + "}";
        }

        [HarmonyLib.HarmonyPatch(typeof(settingsscript), "LoadSettings")]
        private static class AfterSettingsLoad
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() { if (_runtimeForcedOff) ForceOffUiAndFeatures(); }
        }

        [HarmonyLib.HarmonyPatch(typeof(settingsscript), "RefreshSettings")]
        private static class KeepUiOffWhileDenied
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() { if (_runtimeForcedOff) ForceOffUiAndFeatures(); }
        }

        [HarmonyLib.HarmonyPatch(typeof(settingsscript), "SaveSettings")]
        private static class SavePreferenceNotOverride
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix()
            {
                if (!_runtimeForcedOff || !_captured) return;
                var script = settingsscript.s;
                if (script != null && script.S != null) script.S.BDevMode = _savedPreference;
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix() { if (_runtimeForcedOff) ForceOffUiAndFeatures(); }
        }
    }
}
