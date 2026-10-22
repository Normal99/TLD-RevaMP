using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using UnityEngine;

[assembly: InternalsVisibleTo("TLDRevamp")]

namespace TLDRevamp.Multiplayer
{
    /// The multiplayer API for other mods (docs/MODDING-MULTIPLAYER.md).
    ///
    /// Usage, once at startup (a BepInEx plugin's Awake, a TLD Loader mod's OnLoad):
    ///   var mod = MpApi.RegisterMod("mymod", "My Mod", "1.0.0");
    ///   var beacon = MpApi.RegisterType(mod, new BeaconType());
    /// Then, when the mod makes an object:
    ///   var shared = MpApi.Share(beacon, go);
    ///
    /// Without TLD Revamp, or outside a multiplayer session, everything still works on this machine: Share returns an
    /// object that is owned here, edits are applied here, events reach only this machine.
    public static class MpApi
    {
        /// Raised with each release that changes the API. Mods can check it to use newer calls.
        public const int ApiVersion = 3;   // 3: custom ragdolls (RegisterRagdoll); 2: state records with interest, the far store (SharedType.HasState, ViewRange), session-wide settings

        /// TLD Revamp is installed and running (multiplayer may still be off).
        public static bool Available => Backend != null;
        /// In a multiplayer session (hosting or joined).
        public static bool InSession => Backend != null && Backend.InSession;
        public static bool IsHost => Backend != null && Backend.IsHost;
        /// This machine's player id (0 = the host, and also when not in a session).
        public static int MyPlayerId => Backend != null && Backend.InSession ? Backend.MyPlayerId : 0;

        /// The session's shared clock in seconds: the same value on every machine (within a few milliseconds), the
        /// host's clock. Outside a session, this machine's clock. For things that must happen at the same moment
        /// everywhere: a video's position, a timer.
        public static double SessionTime => Backend != null && Backend.InSession ? Backend.SessionTime : Time.realtimeSinceStartupAsDouble;

        /// The players in the session, this one included.
        public static IReadOnlyList<PlayerInfo> Players => Backend != null && Backend.InSession ? Backend.Players : NoPlayers;
        private static readonly PlayerInfo[] NoPlayers = new PlayerInfo[0];

        public static event Action SessionStarted;
        public static event Action SessionEnded;
        public static event Action<PlayerInfo> PlayerJoined;
        public static event Action<PlayerInfo> PlayerLeft;

        // ------------------------------------------------------------------ registration

        internal static readonly List<ModInfo> Mods = new List<ModInfo>();
        internal static readonly Dictionary<string, SharedType> Types = new Dictionary<string, SharedType>();
        internal static readonly Dictionary<string, Action<ModEvent>> EventHandlers = new Dictionary<string, Action<ModEvent>>();

        /// Register a mod. id: short, unique, stable ("mymod"); letters, digits, '-', '_' and '.'. The DLL that calls
        /// this is the one hashed and compared at join (pass the assembly to hash another one).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ModInfo RegisterMod(string id, string name, string version, Assembly assembly = null)
        {
            if (!ValidId(id)) throw new ArgumentException("mod id must be 1-64 letters, digits, '-', '_' or '.': " + id);
            foreach (var m in Mods)
                if (m.Id == id) return m;
            assembly = assembly ?? Assembly.GetCallingAssembly();
            var info = new ModInfo { Id = id, Name = name ?? id, Version = version ?? "", File = SafeLocation(assembly) };
            info.Hash = HashFile(info.File);
            Mods.Add(info);
            Backend?.Log("mods: registered " + info + " sha256 " + Short(info.Hash));
            return info;
        }

        /// Register a shared type. Every player's game must register the same types (same mod, same version).
        public static T RegisterType<T>(ModInfo mod, T type) where T : SharedType
        {
            if (mod == null || !Mods.Contains(mod)) throw new ArgumentException("register the mod first (RegisterMod)");
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!ValidId(type.Name)) throw new ArgumentException("type name must be 1-64 letters, digits, '-', '_' or '.': " + type.Name);
            string key = mod.Id + "/" + type.Name;
            if (Types.ContainsKey(key)) throw new ArgumentException("type already registered: " + key);
            type.Key = key; type.Mod = mod;
            Types[key] = type;
            return type;
        }

        /// Receive a mod event (MpApi.SendEvent with the same mod and name). One handler per name.
        public static void OnEvent(ModInfo mod, string name, Action<ModEvent> handler)
        {
            if (mod == null || !Mods.Contains(mod)) throw new ArgumentException("register the mod first (RegisterMod)");
            if (!ValidId(name)) throw new ArgumentException("event name must be 1-64 letters, digits, '-', '_' or '.': " + name);
            EventHandlers[mod.Id + "/" + name] = handler;
        }

        // ------------------------------------------------------------------ objects

        /// Share an object the mod made. This machine owns it. Outside a session it stays local; when a session starts
        /// (hosting), objects shared before go out with it.
        public static SharedObject Share(SharedType type, GameObject obj)
        {
            if (type == null || type.Mod == null) throw new ArgumentException("register the type first (RegisterType)");
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var existing = Find(obj);
            if (existing != null) return existing;
            var so = new SharedObject { Type = type, GameObject = obj, Owner = MyPlayerId };
            Local.Add(so);
            Backend?.Share(so);
            return so;
        }

        /// The shared object for a GameObject (or one of its children), or null.
        public static SharedObject Find(GameObject obj)
        {
            if (obj == null) return null;
            for (var t = obj.transform; t != null; t = t.parent)
                foreach (var so in Local)
                    if (so.GameObject == t.gameObject && !so.Removed) return so;
            return null;
        }

        /// The shared object with this session id, or null (not here yet, or removed).
        public static SharedObject Find(uint id)
        {
            if (id == 0) return null;
            foreach (var so in Local)
                if (so.Id == id && !so.Removed) return so;
            return null;
        }

        /// Every shared object this machine knows (its own and copies).
        public static IReadOnlyList<SharedObject> Objects => Local;
        internal static readonly List<SharedObject> Local = new List<SharedObject>();

        // ------------------------------------------------------------------ the game's own objects

        /// The session id of one of the game's objects (a car, an item) that TLD Revamp shares, or 0 if it isn't shared.
        /// Write it in a record to point at the object; FindWorldObject finds it on the other machines.
        public static uint WorldObjectId(GameObject gameObject) => Backend != null ? Backend.WorldObjectId(gameObject) : 0;

        /// The game's object with this session id on this machine, or null.
        public static GameObject FindWorldObject(uint id) => Backend?.FindWorldObject(id);

        /// Ask TLD Revamp to share one of the game's objects (a car with its parts) so it gets a WorldObjectId. Returns
        /// false outside a session or if the game can't save it. The id arrives a moment later.
        public static bool ShareWorldObject(GameObject gameObject) => Backend != null && Backend.ShareWorldObject(gameObject);

        // ------------------------------------------------------------------ custom ragdolls (API 3)

        private static readonly Dictionary<GameObject, (Transform[] Limbs, Func<bool> IsLimp)> Ragdolls =
            new Dictionary<GameObject, (Transform[], Func<bool>)>();

        /// Register a ragdoll whose limbs the game's own list (tosaveitemscript.ragdollPos) does not describe. root is
        /// the object RevaMP syncs (the GameObject with the item's tosaveitemscript); limbs are its transforms in a
        /// fixed order that is the same on every machine (same prefab, same mod version). isLimp says when the limbs
        /// move on their own (null: always). Call when created, before or after sharing; again to replace.
        public static void RegisterRagdoll(GameObject root, Transform[] limbs, Func<bool> isLimp = null)
        {
            if (ReferenceEquals(root, null)) throw new ArgumentNullException(nameof(root));
            PruneRagdolls();
            if (root == null) return;
            if (limbs == null || limbs.Length < 1 || limbs.Length > 63)
                throw new ArgumentException("ragdoll limbs must contain 1-63 transforms", nameof(limbs));
            var seen = new HashSet<Transform>();
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i] == null) throw new ArgumentException("ragdoll limb at index " + i + " is null or destroyed", nameof(limbs));
                if (!seen.Add(limbs[i])) throw new ArgumentException("duplicate ragdoll limb at index " + i, nameof(limbs));
            }
            bool added = !Ragdolls.ContainsKey(root);
            Ragdolls[root] = ((Transform[])limbs.Clone(), isLimp);
            if (added) Backend?.Log("mods: registered ragdoll " + root.name + " (" + limbs.Length + " limbs)");
        }

        /// Stop using a custom limb list for this root. Destroyed roots are also removed lazily.
        public static void UnregisterRagdoll(GameObject root)
        {
            if (ReferenceEquals(root, null)) throw new ArgumentNullException(nameof(root));
            Ragdolls.Remove(root);
        }

        internal static bool TryGetRagdoll(GameObject root, out Transform[] limbs, out Func<bool> isLimp)
        {
            limbs = null; isLimp = null;
            if (ReferenceEquals(root, null)) return false;
            if (root == null) { Ragdolls.Remove(root); return false; }
            if (!Ragdolls.TryGetValue(root, out var ragdoll)) return false;
            limbs = ragdoll.Limbs; isLimp = ragdoll.IsLimp;
            return true;
        }

        internal static int RagdollCount
        {
            get { PruneRagdolls(); return Ragdolls.Count; }
        }

        private static void PruneRagdolls()
        {
            List<GameObject> dead = null;
            foreach (var root in Ragdolls.Keys)
            {
                if (root != null) continue;
                if (dead == null) dead = new List<GameObject>();
                dead.Add(root);
            }
            if (dead != null) foreach (var root in dead) Ragdolls.Remove(root);
        }

        // ------------------------------------------------------------------ events

        /// Send a short message to other players: a sound, an effect, a notice; things that aren't an object's state.
        /// Rate-limited (about 20 a second per player) and size-capped (1 KB). about: the object it concerns (needed for
        /// EventTarget.Owner). Returns false if it was dropped.
        public static bool SendEvent(ModInfo mod, string name, Action<RecordWriter> write, EventTarget target = EventTarget.All, SharedObject about = null)
        {
            if (mod == null || !ValidId(name)) return false;
            var w = new RecordWriter();
            write?.Invoke(w);
            if (w.Length > MaxEventBytes) { Backend?.Log($"mods: event {mod.Id}/{name} is {w.Length} bytes (max {MaxEventBytes}): dropped"); return false; }
            if (Backend != null && Backend.InSession) return Backend.SendEvent(mod.Id + "/" + name, w.ToArray(), target, about);
            // alone: this machine is everyone, the host and the owner
            if (target != EventTarget.Others) DeliverEvent(mod.Id + "/" + name, 0, about, w.ToArray());
            return true;
        }

        public const int MaxEventBytes = 1024;

        // ------------------------------------------------------------------ session-wide settings (API 2)

        /// A setting that must be the same for everyone in a session (rules, difficulty, spawn rates; never graphics or
        /// keys). get reads this machine's value, apply sets one. In a session the host's value is sent to every player
        /// when they join and whenever it changes on the host (checked about once a second), and applied with apply; a
        /// joiner's own value is applied again when the session ends. Values are strings (at most 1024 UTF-8 bytes):
        /// the mod chooses the format. Every player registers the same settings (they come with the same mods).
        public static SessionSetting RegisterSetting(ModInfo mod, string name, Func<string> get, Action<string> apply)
        {
            if (mod == null || !Mods.Contains(mod)) throw new ArgumentException("register the mod first (RegisterMod)");
            if (!ValidId(name)) throw new ArgumentException("setting name must be 1-64 letters, digits, '-', '_' or '.': " + name);
            if (get == null || apply == null) throw new ArgumentNullException(get == null ? nameof(get) : nameof(apply));
            string key = mod.Id + "/" + name;
            foreach (var s in Settings) if (s.Key == key) throw new ArgumentException("setting already registered: " + key);
            int ofMod = 0; foreach (var s in Settings) if (s.Mod == mod) ofMod++;
            if (ofMod >= MaxSettingsPerMod) throw new ArgumentException($"at most {MaxSettingsPerMod} settings per mod");
            var st = new SessionSetting { Key = key, Name = name, Mod = mod, Get = get, Apply = apply };
            Settings.Add(st);
            return st;
        }

        public const int MaxSettingsPerMod = 64, MaxSettingBytes = 1024;
        internal static readonly List<SessionSetting> Settings = new List<SessionSetting>();

        // ------------------------------------------------------------------ the backend (TLD Revamp)

        internal interface IBackend
        {
            bool InSession { get; }
            bool IsHost { get; }
            int MyPlayerId { get; }
            double SessionTime { get; }
            IReadOnlyList<PlayerInfo> Players { get; }
            void Share(SharedObject so);
            bool RequestEdit(SharedObject so, Action<RecordWriter> write);
            void RequestOwnership(SharedObject so);
            void Remove(SharedObject so);
            bool SendEvent(string key, byte[] data, EventTarget target, SharedObject about);
            uint WorldObjectId(GameObject go);
            GameObject FindWorldObject(uint id);
            bool ShareWorldObject(GameObject go);
            void Log(string line);
        }

        internal static IBackend Backend;

        internal static void DeliverEvent(string key, int from, SharedObject about, byte[] data)
        {
            if (!EventHandlers.TryGetValue(key, out var h)) return;
            int slash = key.IndexOf('/');
            try { h(new ModEvent(from, key.Substring(slash + 1), about, new RecordReader(data))); }
            catch (Exception e) { Backend?.Log("mods: event handler " + key + " threw: " + e); }
        }

        internal static void RaiseSessionStarted() => Raise(SessionStarted, "SessionStarted");
        internal static void RaiseSessionEnded() => Raise(SessionEnded, "SessionEnded");
        internal static void RaisePlayerJoined(PlayerInfo p) { try { PlayerJoined?.Invoke(p); } catch (Exception e) { Backend?.Log("mods: PlayerJoined handler threw: " + e); } }
        internal static void RaisePlayerLeft(PlayerInfo p) { try { PlayerLeft?.Invoke(p); } catch (Exception e) { Backend?.Log("mods: PlayerLeft handler threw: " + e); } }
        private static void Raise(Action a, string what) { try { a?.Invoke(); } catch (Exception e) { Backend?.Log("mods: " + what + " handler threw: " + e); } }

        // ------------------------------------------------------------------ helpers

        internal static bool ValidId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
            foreach (char c in s)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }

        private static string SafeLocation(Assembly a)
        {
            try { return a.Location ?? ""; } catch { return ""; }
        }

        internal static string HashFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                using (var sha = SHA256.Create())
                using (var f = System.IO.File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        internal static string Short(string hash) => string.IsNullOrEmpty(hash) ? "(none)" : hash.Length > 12 ? hash.Substring(0, 12) : hash;
    }
}
