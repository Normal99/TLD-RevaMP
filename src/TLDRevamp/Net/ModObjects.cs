using System;
using System.Collections.Generic;
using System.Text;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The session side of the modding API (TLDRevamp.Multiplayer.MpApi, docs/MODDING-MULTIPLAYER.md): other mods' own
    /// objects, shared with the same rules as the game's (Entities), but on their own path — a mod object has no game
    /// save script, so it never enters the game's record groups, the far store or the leases.
    ///  - Share: the creator sends the type key and the first record; the host assigns an id, keeps the record (for
    ///    joiners) and sends it to everyone, who build the object through the mod's factory.
    ///  - One owner per object, with an epoch (as Entities): records and poses from an older owner are ignored. The
    ///    owner's machine runs the object; copies are kinematic. A player who leaves hands their objects to the host.
    ///  - Edits go to the owner, who decides (SharedType.OnEdit); an accepted edit sends a new record to everyone.
    ///  - Poses (SharedType.SyncPose): the owner sends them while the object moves; copies play them back through a
    ///    PoseInterpolator, like car copies.
    ///  - The host checks everything from players before it is passed on: a registered type with the same version,
    ///    size caps, rates, finite positions, the sender being the owner. A broken or hostile record can't reach a
    ///    mod's reader as anything but bytes, and the reader never throws (RecordReader.Bad).
    /// Mods must match at join (MpServer Hello, ModCheck): the same mod ids, versions and DLL hashes.
    public sealed class ModObjects : MpApi.IBackend
    {
        // client → server, server → client (one id per direction where the layout is the same)
        public const byte ModShare = 70;      // c→s: u32 tag, str key, u8 ver, pos, rot, bytes record
        public const byte ModAssigned = 71;   // s→c: u32 tag, u32 id, u32 epoch
        public const byte ModAdd = 72;        // s→c: u32 id, varint owner, u32 epoch, str key, u8 ver, pos, rot, bytes record
        public const byte ModRecord = 73;     // c→s / s→c: u32 id, u32 epoch, pos, rot, bytes record
        public const byte ModEdit = 74;       // c→s: u32 id, bytes edit.  s→owner: u32 id, varint from, bytes edit
        public const byte ModOwner = 75;      // s→c: u32 id, varint owner, u32 epoch
        public const byte ModRemove = 76;     // c→s / s→c: u32 id
        public const byte ModPose = 77;       // c→s / s→c (unreliable): u32 id, u32 epoch, stamp, pos, rot, vel
        public const byte ModEvent = 78;      // c→s: str key, u8 target, u32 about, bytes.  s→c: str key, varint from, u32 about, bytes
        public const byte ModClaim = 79;      // c→s: u32 id
        public const byte ModClock = 80;      // c→s: f64 clientT.  s→c: f64 clientT, f64 hostT

        public static bool IsModMessage(byte t) => t >= ModShare && t <= ModClock;

        /// Switch (A/B, bridge `set TLDRevamp.Net.ModObjects.Enabled False`): mods' objects stay local.
        public static bool Enabled = true;
        public static int MaxObjectsPerPlayer = 1000;
        public static float RecordMinInterval = 0.2f;   // the owner merges record changes to at most 5 a second per object
        public static float PoseKeepAliveS = 2f;
        public static long Shared, Added, AddMissingType, AddFailed, Records, Edits, EditsRefused, Poses, Events, Dropped, OwnerChanges, Removes;
        public static string LastDrop = "";

        internal static readonly ModObjects I = new ModObjects();
        private static readonly NetWriter W = new NetWriter();

        public static void Install() { MpApi.Backend = I; }

        // ------------------------------------------------------------------ IBackend

        public bool InSession => Entities.InSession;
        public bool IsHost => Entities.IsHost;
        public int MyPlayerId => Entities.MyId < 0 ? 0 : Entities.MyId;
        public double SessionTime => Time.realtimeSinceStartupAsDouble + (IsHost ? 0.0 : _clockOffset);
        public IReadOnlyList<PlayerInfo> Players
        {
            get
            {
                _players.Clear();
                _players.Add(new PlayerInfo(MyPlayerId, Session.MyName()));
                foreach (var kv in RemotePlayers.Names()) _players.Add(new PlayerInfo(kv.Key, kv.Value));
                return _players;
            }
        }
        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();

        public void Log(string line) => Plugin.Log.LogInfo(line);

        public void Share(SharedObject so)
        {
            if (!Enabled || !InSession || !_started) return;   // local for now; shared when this machine hosts (SessionStart)
            SendShare(so);
        }

        public bool RequestEdit(SharedObject so, Action<RecordWriter> write)
        {
            if (so.Removed || so.Type == null) return false;
            var w = new RecordWriter();
            write?.Invoke(w);
            if (w.Length > so.Type.MaxEditBytes) { Drop($"edit on {so} is {w.Length} bytes (max {so.Type.MaxEditBytes})"); return false; }
            if (so.IsMine || !InSession)
            {
                bool ok = ApplyEdit(so, new RecordReader(w.Buf, 0, w.Length), MyPlayerId);
                return ok;
            }
            if (so.Id == 0) return false;
            W.Reset(); W.U8(ModEdit); W.U32(so.Id); WriteBytes(W, w.Buf, w.Length);
            ToServer(W, true);
            return true;
        }

        public void RequestOwnership(SharedObject so)
        {
            if (so.Removed || so.IsMine || so.Id == 0 || !InSession) return;
            W.Reset(); W.U8(ModClaim); W.U32(so.Id);
            ToServer(W, true);
        }

        public void Remove(SharedObject so)
        {
            if (so.Removed) return;
            if (InSession && !so.IsMine) { Log($"mods: {so}: only its owner removes it (ask the owner with an edit)"); return; }
            if (InSession && so.Id != 0) { W.Reset(); W.U8(ModRemove); W.U32(so.Id); ToServer(W, true); }
            // pending (no id yet): ModAssigned finds it removed and removes it on the host
            RemoveHere(so);
        }

        public bool SendEvent(string key, byte[] data, EventTarget target, SharedObject about)
        {
            if (target == EventTarget.Owner && (about == null || about.Id == 0)) return false;
            W.Reset(); W.U8(ModEvent); W.Str(key); W.U8((byte)target); W.U32(about?.Id ?? 0); WriteBytes(W, data, data.Length);
            ToServer(W, true);
            return true;
        }

        public uint WorldObjectId(GameObject go) => Entities.NetIdOf(go);
        public GameObject FindWorldObject(uint id) => Entities.GameObjectOf(id);
        public bool ShareWorldObject(GameObject go) => Entities.ShareGameObject(go);

        // ------------------------------------------------------------------ local side

        private sealed class Local
        {
            public PoseInterpolator Ip;
            public readonly List<KeyValuePair<Rigidbody, bool>> Kinematic = new List<KeyValuePair<Rigidbody, bool>>();
            public float PoseAcc, PoseSentAt = -100f, RecordAt = -100f;
            public Vector3d SentPos; public Quaternion SentRot = Quaternion.identity; public bool HasSent;
            public Vector3d PrevPos; public bool HasPrev;
        }

        private static readonly Dictionary<SharedObject, Local> Locals = new Dictionary<SharedObject, Local>();
        private static readonly Dictionary<uint, SharedObject> PendingByTag = new Dictionary<uint, SharedObject>();
        private static uint _tag;
        private static bool _started;
        private static double _clockOffset; private static double _clockBestRtt = double.MaxValue; private static float _clockAt = -100f, _clockBestAt;
        private static int _clockSent;

        private static Local L(SharedObject so)
        {
            if (!Locals.TryGetValue(so, out var l)) Locals[so] = l = new Local();
            return l;
        }

        private static void SendShare(SharedObject so)
        {
            if (so.Removed || so.GameObject == null || so.Id != 0 || PendingByTag.ContainsValue(so)) return;
            if (!WriteRecord(so, out var rec)) return;
            so.LocalTag = ++_tag;
            so.Owner = I.MyPlayerId;
            PendingByTag[so.LocalTag] = so;
            var t = so.GameObject.transform;
            W.Reset(); W.U8(ModShare); W.U32(so.LocalTag); W.Str(so.Type.Key); W.U8(so.Type.Version);
            WriteWorldPose(W, t.position, t.rotation);
            WriteBytes(W, rec, rec.Length);
            ToServer(W, true);
            Shared++;
        }

        private static bool WriteRecord(SharedObject so, out byte[] rec)
        {
            rec = null;
            var w = new RecordWriter();
            try { so.Type.Write(so.GameObject, w); }
            catch (Exception e) { Drop($"{so.Type.Key}.Write threw: {e}"); return false; }
            if (w.Length > so.Type.MaxRecordBytes) { Drop($"{so} record is {w.Length} bytes (max {so.Type.MaxRecordBytes}): stays local"); return false; }
            rec = w.ToArray();
            return true;
        }

        private static bool ApplyEdit(SharedObject so, RecordReader r, int from)
        {
            bool ok;
            try { ok = so.Type.OnEdit(so, r, from); }
            catch (Exception e) { Drop($"{so.Type.Key}.OnEdit threw: {e}"); ok = false; }
            if (ok) { Edits++; so.RecordDirty = true; } else EditsRefused++;
            return ok;
        }

        private static void RemoveHere(SharedObject so)
        {
            if (so.Removed) return;
            so.Removed = true; Removes++;
            try { so.Type.OnRemoved(so); }
            catch (Exception e) { Drop($"{so.Type.Key}.OnRemoved threw: {e}"); }
            MpApi.Local.Remove(so); Locals.Remove(so);
        }

        /// Once per frame (Mp.Tick).
        public static void Tick()
        {
            bool now = Enabled && Entities.InSession && mainscript.s != null && mainscript.s.player != null;
            if (now && !_started) SessionStart();
            if (!_started) return;
            float t = Time.realtimeSinceStartup;
            if (!I.IsHost && (_clockSent < 10 ? t - _clockAt > 1f : t - _clockAt > 5f))
            {
                _clockAt = t; _clockSent++;
                W.Reset(); W.U8(ModClock); W.F64(Time.realtimeSinceStartupAsDouble);
                ToServer(W, true);
            }
            int me = I.MyPlayerId;
            var list = MpApi.Local;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var so = list[i];
                if (so.GameObject == null) { if (so.Owner == me) I.Remove(so); else { so.Removed = true; list.RemoveAt(i); Locals.Remove(so); } continue; }
                if (so.Id == 0) continue;
                if (so.Owner == me) OwnerTick(so, t);
                else CopyTick(so);
            }
        }

        private static void OwnerTick(SharedObject so, float t)
        {
            var l = L(so);
            if (so.RecordDirty && t - l.RecordAt >= RecordMinInterval)
            {
                so.RecordDirty = false; l.RecordAt = t;
                if (WriteRecord(so, out var rec))
                {
                    var tr = so.GameObject.transform;
                    W.Reset(); W.U8(ModRecord); W.U32(so.Id); W.U32(so.Epoch); WriteWorldPose(W, tr.position, tr.rotation); WriteBytes(W, rec, rec.Length);
                    ToServer(W, true);
                    Records++;
                }
            }
            if (!so.Type.SyncPose || so.GameObject.transform.parent != null) return;
            float hz = Mathf.Clamp(so.Type.PoseHz, 0.5f, 20f);
            l.PoseAcc += Time.unscaledDeltaTime;
            if (l.PoseAcc < 1f / hz) return;
            float dt = l.PoseAcc; l.PoseAcc = 0f;
            var tf = so.GameObject.transform;
            var g = mainscript.GlobalFromUnityPos(tf.position);
            var vel = l.HasPrev ? (Vector3)(g - l.PrevPos) / Mathf.Max(1e-3f, dt) : Vector3.zero;
            var rb = so.GameObject.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) vel = rb.velocity;
            l.PrevPos = g; l.HasPrev = true;
            bool moved = !l.HasSent || (g - l.SentPos).sqrMagnitude > 1e-4 || Quaternion.Angle(tf.rotation, l.SentRot) > 0.5f;
            if (!moved && t - l.PoseSentAt < PoseKeepAliveS) return;
            l.SentPos = g; l.SentRot = tf.rotation; l.HasSent = true; l.PoseSentAt = t;
            W.Reset(); W.U8(ModPose); W.U32(so.Id); W.U32(so.Epoch); Protocol.WriteStamp(W, Time.realtimeSinceStartupAsDouble);
            W.F64(g.x); W.F64(g.y); W.F64(g.z); WriteRot(W, tf.rotation); W.F32(vel.x); W.F32(vel.y); W.F32(vel.z);
            ToServer(W, false);
            Poses++;
        }

        private static void CopyTick(SharedObject so)
        {
            if (!so.Type.SyncPose || so.GameObject.transform.parent != null) return;
            var l = L(so);
            if (l.Ip == null || !l.Ip.Ready) return;
            l.Ip.Evaluate(Time.realtimeSinceStartupAsDouble, Time.unscaledDeltaTime, out var g, out var rot);
            var tf = so.GameObject.transform;
            tf.SetPositionAndRotation(mainscript.UnityPosFromGlobal(g), rot);
        }

        private static void SessionStart()
        {
            _started = true;
            _clockOffset = 0; _clockBestRtt = double.MaxValue; _clockSent = 0; _clockAt = -100f;
            Plugin.Log.LogInfo($"mods: session started ({MpApi.Mods.Count} mod(s), {MpApi.Types.Count} type(s), {MpApi.Local.Count} object(s) here)");
            // what this machine made before the session (a host's world) or while joining: shared now, owned here
            int me = I.MyPlayerId;
            foreach (var so in MpApi.Local.ToArray()) if (so.Id == 0 && !so.Removed) { so.Owner = me; SendShare(so); }
            MpApi.RaiseSessionStarted();
        }

        /// Session over (Mp.Stop). The host keeps every object where it is, as its own (copies become real objects); a
        /// client goes back to its own world, so everything shared there disappears from it.
        public static void Reset(bool wasHost)
        {
            bool was = _started;
            _started = false;
            PendingByTag.Clear();
            foreach (var so in MpApi.Local.ToArray())
            {
                if (wasHost)
                {
                    bool wasCopy = so.Owner != 0;
                    so.Id = 0; so.Epoch = 0; so.Owner = 0; so.RecordDirty = false;
                    if (wasCopy && so.GameObject != null) { MakeOwned(so); Notify(so, true); }
                }
                else RemoveHere(so);
            }
            Locals.Clear();
            ServerReset();
            if (was) MpApi.RaiseSessionEnded();
        }

        private static void MakeCopy(SharedObject so)
        {
            var l = L(so);
            l.Ip = new PoseInterpolator { SendInterval = 1f / Mathf.Clamp(so.Type.PoseHz, 0.5f, 20f) };
            l.HasSent = false; l.HasPrev = false;
            if (!so.Type.KinematicCopies || so.GameObject == null) return;
            l.Kinematic.Clear();
            foreach (var rb in so.GameObject.GetComponentsInChildren<Rigidbody>(true))
            {
                l.Kinematic.Add(new KeyValuePair<Rigidbody, bool>(rb, rb.isKinematic));
                rb.isKinematic = true;
            }
        }

        private static void MakeOwned(SharedObject so)
        {
            if (!Locals.TryGetValue(so, out var l)) return;
            foreach (var kv in l.Kinematic) if (kv.Key != null) kv.Key.isKinematic = kv.Value;
            l.Kinematic.Clear();
            l.Ip = null; l.HasSent = false; l.HasPrev = false;
        }

        private static void Notify(SharedObject so, bool mine)
        {
            try { so.Type.OnOwnerChanged(so, mine); }
            catch (Exception e) { Drop($"{so.Type.Key}.OnOwnerChanged threw: {e}"); }
        }

        public static void ClientReceive(byte type, NetReader r)
        {
            switch (type)
            {
                case ModAssigned:
                {
                    uint tag = r.U32(), id = r.U32(), epoch = r.U32();
                    if (r.Bad || !PendingByTag.TryGetValue(tag, out var so)) return;
                    PendingByTag.Remove(tag);
                    so.Id = id; so.Epoch = epoch;
                    if (so.Removed) { W.Reset(); W.U8(ModRemove); W.U32(id); ToServer(W, true); }
                    break;
                }
                case ModAdd:
                {
                    uint id = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32(); string key = r.Str(); byte ver = r.U8();
                    ReadWorldPose(r, out var pos, out var rot);
                    var rec = ReadBytes(r, 1 << 20);
                    if (r.Bad || rec == null) return;
                    if (MpApi.Find(id) != null) return;
                    if (!MpApi.Types.TryGetValue(key, out var st) || st.Version != ver)
                    {
                        AddMissingType++;
                        if (AddMissingType <= 20) Plugin.Log.LogWarning($"[anomaly] mods: object {id} of type {key} v{ver}: not registered here{(st != null ? " (v" + st.Version + ")" : "")} - no copy");
                        return;
                    }
                    GameObject go = null;
                    try { go = st.Create(new RecordReader(rec), pos, rot); }
                    catch (Exception e) { Plugin.Log.LogError($"mods: {key}.Create threw: {e}"); }
                    if (go == null) { AddFailed++; if (AddFailed <= 20) Plugin.Log.LogWarning($"mods: object {id} ({key}): the mod made nothing from its record"); return; }
                    if (go.transform.parent == null && go.GetComponent<visszarako>() == null)
                        go.AddComponent<visszarako>().rb = go.GetComponent<Rigidbody>() != null;   // moves with this machine's floating origin
                    var so = new SharedObject { Id = id, Type = st, GameObject = go, Owner = owner, Epoch = epoch };
                    MpApi.Local.Add(so);
                    Added++;
                    if (owner == I.MyPlayerId) Notify(so, true);
                    else MakeCopy(so);
                    break;
                }
                case ModRecord:
                {
                    uint id = r.U32(), epoch = r.U32();
                    ReadWorldPose(r, out var pos, out var rot);
                    var rec = ReadBytes(r, 1 << 20);
                    var so = MpApi.Find(id);
                    if (r.Bad || so == null || so.IsMine || epoch < so.Epoch || so.GameObject == null) return;
                    try { so.Type.Read(so.GameObject, new RecordReader(rec)); }
                    catch (Exception e) { Drop($"{so.Type.Key}.Read threw: {e}"); }
                    if (!so.Type.SyncPose && so.GameObject.transform.parent == null) so.GameObject.transform.SetPositionAndRotation(pos, rot);
                    break;
                }
                case ModEdit:
                {
                    uint id = r.U32(); int from = (int)r.VarU32(); var edit = ReadBytes(r, 1 << 16);
                    var so = MpApi.Find(id);
                    if (r.Bad || so == null || !so.IsMine) { EditsRefused++; return; }
                    if (edit.Length > so.Type.MaxEditBytes) { EditsRefused++; return; }
                    ApplyEdit(so, new RecordReader(edit), from);
                    break;
                }
                case ModOwner:
                {
                    uint id = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    var so = MpApi.Find(id);
                    if (r.Bad || so == null || epoch <= so.Epoch) return;
                    bool wasMine = so.IsMine;
                    so.Owner = owner; so.Epoch = epoch; OwnerChanges++;
                    bool mine = so.IsMine;
                    if (mine && !wasMine) { MakeOwned(so); Notify(so, true); }
                    else if (!mine && wasMine) { MakeCopy(so); Notify(so, false); }
                    break;
                }
                case ModRemove:
                {
                    uint id = r.U32();
                    var so = MpApi.Find(id);
                    if (!r.Bad && so != null) RemoveHere(so);
                    break;
                }
                case ModPose:
                {
                    uint id = r.U32(), epoch = r.U32(); double stamp = Protocol.ReadStamp(r);
                    var g = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = ReadRot(r); var vel = new Vector3(r.F32(), r.F32(), r.F32());
                    var so = MpApi.Find(id);
                    if (r.Bad || so == null || so.IsMine || epoch < so.Epoch || !Entities.Finite(vel)) return;
                    var l = L(so);
                    if (l.Ip == null) MakeCopy(so);
                    l.Ip.Add(new PoseInterpolator.Sample { T = stamp, Pos = g, Rot = rot, Vel = vel }, Time.realtimeSinceStartupAsDouble);
                    break;
                }
                case ModEvent:
                {
                    string key = r.Str(); int from = (int)r.VarU32(); uint about = r.U32(); var data = ReadBytes(r, MpApi.MaxEventBytes);
                    if (r.Bad) return;
                    Events++;
                    MpApi.DeliverEvent(key, from, MpApi.Find(about), data);
                    break;
                }
                case ModClock:
                {
                    double sent = r.F64(), host = r.F64();
                    if (r.Bad) return;
                    double now = Time.realtimeSinceStartupAsDouble, rtt = now - sent;
                    if (rtt < 0 || rtt > 5) return;
                    // the least delayed answer is the truest (NTP's rule); an old best is let go after a minute (drift, a
                    // route change)
                    if (rtt <= _clockBestRtt * 1.2 || Time.realtimeSinceStartup - _clockBestAt > 60f)
                    {
                        _clockBestRtt = Math.Min(rtt, _clockBestRtt); _clockBestAt = Time.realtimeSinceStartup;
                        double off = host + rtt * 0.5 - now;
                        _clockOffset = _clockSent <= 1 ? off : _clockOffset + (off - _clockOffset) * 0.5;
                    }
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ the host

        private sealed class SObj
        {
            public uint Id, Epoch; public int Owner; public string Key; public byte Ver;
            public Vector3d Pos; public Quaternion Rot = Quaternion.identity; public byte[] Record;
        }

        private sealed class Budget
        {
            public float Tokens, At;
            public bool Take(float perSecond, float burst)
            {
                float now = Time.realtimeSinceStartup;
                Tokens = Mathf.Min(burst, Tokens + (now - At) * perSecond); At = now;
                if (Tokens < 1f) return false;
                Tokens -= 1f;
                return true;
            }
        }

        private static readonly Dictionary<uint, SObj> Server = new Dictionary<uint, SObj>();
        private static readonly Dictionary<int, Budget> MsgBudget = new Dictionary<int, Budget>();
        private static readonly Dictionary<int, Budget> PoseBudget = new Dictionary<int, Budget>();
        private static uint _nextId = 1;

        private static void ServerReset() { Server.Clear(); MsgBudget.Clear(); PoseBudget.Clear(); _nextId = 1; }

        private static bool Spend(Dictionary<int, Budget> d, int player, float perSecond, float burst)
        {
            if (!d.TryGetValue(player, out var b)) d[player] = b = new Budget { Tokens = burst, At = Time.realtimeSinceStartup };
            return b.Take(perSecond, burst);
        }

        public static void ServerReceive(int from, byte type, NetReader r)
        {
            if (!Enabled) return;
            // reliable traffic: shares, records, edits, events, claims (60/s, bursts of 120); poses have their own budget
            if (type == ModPose ? !Spend(PoseBudget, from, 25f * 20f, 1000f) : type != ModClock && !Spend(MsgBudget, from, 60f, 120f))
            {
                Drop($"player {from}: too many mod messages, type {type} dropped");
                return;
            }
            switch (type)
            {
                case ModShare:
                {
                    uint tag = r.U32(); string key = r.Str(); byte ver = r.U8();
                    ReadGlobalPose(r, out var pos, out var rot);
                    var rec = ReadBytes(r, 1 << 20);
                    if (r.Bad || rec == null) return;
                    if (!MpApi.Types.TryGetValue(key, out var st) || st.Version != ver) { Drop($"player {from}: share of unknown type {key} v{ver}"); return; }
                    if (rec.Length > st.MaxRecordBytes) { Drop($"player {from}: {key} record {rec.Length} bytes"); return; }
                    if (!FinitePose(pos, rot)) { Drop($"player {from}: {key} at a non-finite place"); return; }
                    int owned = 0; foreach (var s in Server.Values) if (s.Owner == from) owned++;
                    if (owned >= MaxObjectsPerPlayer) { Drop($"player {from}: over {MaxObjectsPerPlayer} mod objects"); return; }
                    var so = new SObj { Id = _nextId++, Epoch = 1, Owner = from, Key = key, Ver = ver, Pos = pos, Rot = rot, Record = rec };
                    Server[so.Id] = so;
                    W.Reset(); W.U8(ModAssigned); W.U32(tag); W.U32(so.Id); W.U32(so.Epoch);
                    SendTo(from, W, true);
                    WriteAdd(W, so);
                    SendAll(W, true, from);
                    break;
                }
                case ModRecord:
                {
                    uint id = r.U32(), epoch = r.U32();
                    ReadGlobalPose(r, out var pos, out var rot);
                    var rec = ReadBytes(r, 1 << 20);
                    if (r.Bad || !Server.TryGetValue(id, out var so) || so.Owner != from || epoch != so.Epoch) return;
                    if (!MpApi.Types.TryGetValue(so.Key, out var st) || rec.Length > st.MaxRecordBytes || !FinitePose(pos, rot)) { Drop($"player {from}: bad record for {so.Key}#{id}"); return; }
                    so.Record = rec; so.Pos = pos; so.Rot = rot;
                    W.Reset(); W.U8(ModRecord); W.U32(id); W.U32(epoch); WriteGlobalPose(W, pos, rot); WriteBytes(W, rec, rec.Length);
                    SendAll(W, true, from);
                    break;
                }
                case ModEdit:
                {
                    uint id = r.U32(); var edit = ReadBytes(r, 1 << 16);
                    if (r.Bad || !Server.TryGetValue(id, out var so)) return;
                    if (!MpApi.Types.TryGetValue(so.Key, out var st) || edit.Length > st.MaxEditBytes) { Drop($"player {from}: edit for {so.Key}#{id} too big"); return; }
                    W.Reset(); W.U8(ModEdit); W.U32(id); W.VarU32((uint)from); WriteBytes(W, edit, edit.Length);
                    SendTo(so.Owner, W, true);
                    break;
                }
                case ModClaim:
                {
                    uint id = r.U32();
                    if (r.Bad || !Server.TryGetValue(id, out var so) || so.Owner == from) return;
                    if (!MpApi.Types.TryGetValue(so.Key, out var st) || !st.AllowTakeOver) return;
                    SetOwner(so, from, "claimed");
                    break;
                }
                case ModRemove:
                {
                    uint id = r.U32();
                    if (r.Bad || !Server.TryGetValue(id, out var so)) return;
                    if (so.Owner != from && from != 0) { Drop($"player {from}: removing {so.Key}#{id}, owned by {so.Owner}"); return; }
                    Server.Remove(id);
                    W.Reset(); W.U8(ModRemove); W.U32(id);
                    SendAll(W, true, from);
                    break;
                }
                case ModPose:
                {
                    uint id = r.U32(), epoch = r.U32(); double stamp = Protocol.ReadStamp(r);
                    var g = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = ReadRot(r); var vel = new Vector3(r.F32(), r.F32(), r.F32());
                    if (r.Bad || !Server.TryGetValue(id, out var so) || so.Owner != from || epoch != so.Epoch) return;
                    if (!FinitePose(g, rot) || !Entities.Finite(vel)) { Drop($"player {from}: non-finite pose for {so.Key}#{id}"); return; }
                    so.Pos = g; so.Rot = rot;
                    W.Reset(); W.U8(ModPose); W.U32(id); W.U32(epoch); Protocol.WriteStamp(W, stamp);
                    W.F64(g.x); W.F64(g.y); W.F64(g.z); WriteRot(W, rot); W.F32(vel.x); W.F32(vel.y); W.F32(vel.z);
                    SendAll(W, false, from);
                    break;
                }
                case ModEvent:
                {
                    string key = r.Str(); var target = (EventTarget)r.U8(); uint about = r.U32(); var data = ReadBytes(r, MpApi.MaxEventBytes);
                    if (r.Bad || data == null) { Drop($"player {from}: bad mod event"); return; }
                    int slash = key.IndexOf('/');
                    if (slash <= 0 || !MpApi.EventHandlers.ContainsKey(key) && MpApi.Mods.Find(m => m.Id == key.Substring(0, slash)) == null) { Drop($"player {from}: event {key} of a mod the host hasn't"); return; }
                    W.Reset(); W.U8(ModEvent); W.Str(key); W.VarU32((uint)from); W.U32(about); WriteBytes(W, data, data.Length);
                    switch (target)
                    {
                        case EventTarget.All: SendAll(W, true, -1); break;
                        case EventTarget.Others: SendAll(W, true, from); break;
                        case EventTarget.Host: SendTo(0, W, true); break;
                        case EventTarget.Owner: if (Server.TryGetValue(about, out var so)) SendTo(so.Owner, W, true); break;
                    }
                    break;
                }
                case ModClock:
                {
                    double sent = r.F64();
                    if (r.Bad) return;
                    W.Reset(); W.U8(ModClock); W.F64(sent); W.F64(Time.realtimeSinceStartupAsDouble);
                    SendTo(from, W, true);
                    break;
                }
            }
        }

        private static void SetOwner(SObj so, int owner, string why)
        {
            so.Owner = owner; so.Epoch++;
            Plugin.Log.LogInfo($"mods: {so.Key}#{so.Id} → player {owner} ({why}), epoch {so.Epoch}");
            W.Reset(); W.U8(ModOwner); W.U32(so.Id); W.VarU32((uint)owner); W.U32(so.Epoch);
            SendAll(W, true, -1);
        }

        /// A player's world is loaded (Ready): every mod object goes to them.
        public static void ServerOnJoin(int player)
        {
            foreach (var so in Server.Values) { WriteAdd(W, so); SendTo(player, W, true); }
        }

        /// A player left: their objects go to the host, which runs them from where they are.
        public static void ServerOnLeave(int player)
        {
            MsgBudget.Remove(player); PoseBudget.Remove(player);
            foreach (var so in Server.Values) if (so.Owner == player) SetOwner(so, 0, "player " + player + " left");
        }

        public static void OnPlayerJoined(int id, string name) { if (_started) MpApi.RaisePlayerJoined(new PlayerInfo(id, name)); }
        public static void OnPlayerLeft(int id, string name) { if (_started) MpApi.RaisePlayerLeft(new PlayerInfo(id, name)); }

        private static void WriteAdd(NetWriter w, SObj so)
        {
            w.Reset(); w.U8(ModAdd); w.U32(so.Id); w.VarU32((uint)so.Owner); w.U32(so.Epoch); w.Str(so.Key); w.U8(so.Ver);
            WriteGlobalPose(w, so.Pos, so.Rot); WriteBytes(w, so.Record, so.Record.Length);
        }

        // the host's own client side gets what it sends to player 0 at once (as Entities.ServerSendTo)
        private static void SendTo(int player, NetWriter w, bool reliable)
        {
            if (player == 0) { Deliver(w); return; }
            Mp.Server?.SendToPlayer(player, w, reliable);
        }

        private static void SendAll(NetWriter w, bool reliable, int except)
        {
            Mp.Server?.SendToAll(w, reliable, except, null, 0);
            if (except != 0) Deliver(w);
        }

        private static void ToServer(NetWriter w, bool reliable)
        {
            if (Entities.IsHost)
            {
                var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len);
                ServerReceive(0, r.U8(), r);
            }
            else Mp.Client?.SendRaw(w, reliable ? SteamTransport.SendReliable : SteamTransport.SendUnreliable);
        }

        private static void Deliver(NetWriter w)
        {
            var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len);
            ClientReceive(r.U8(), r);
        }

        // ------------------------------------------------------------------ mods must match (Hello)

        /// Appended to Hello: every registered mod (id, version, DLL hash) and type (key, version).
        public static void WriteModList(NetWriter w)
        {
            w.VarU32((uint)MpApi.Mods.Count);
            foreach (var m in MpApi.Mods) { w.Str(m.Id); w.Str(m.Version); w.Str(m.Hash); }
            w.VarU32((uint)MpApi.Types.Count);
            foreach (var kv in MpApi.Types) { w.Str(kv.Key); w.U8(kv.Value.Version); }
        }

        /// What differs between a joiner's mods and the host's.
        public sealed class ModCheck
        {
            /// Host mods the joiner lacks or has in another version/file: the host can send these (ModTransfer).
            public readonly List<ModInfo> Send = new List<ModInfo>();
            /// Differences no download fixes (a mod the host doesn't have, a type version, an unreadable list).
            public readonly List<string> Problems = new List<string>();
            public bool Same => Send.Count == 0 && Problems.Count == 0;

            public string Describe()
            {
                var diff = new List<string>(Problems);
                foreach (var m in Send) diff.Add($"install {m.Name} {m.Version}");
                var sb = new StringBuilder("Your mods differ from the host's: ");
                sb.Append(string.Join("; ", diff.Count > 6 ? diff.GetRange(0, 6) : diff));
                if (diff.Count > 6) sb.Append($"; and {diff.Count - 6} more");
                sb.Append('.');
                return sb.ToString();
            }
        }

        /// The host: compare a joiner's list with its own. A joiner from before this check sends no list (r is
        /// empty): it can only match a host without mods.
        public static ModCheck CheckModList(NetReader r)
        {
            var res = new ModCheck();
            var theirs = new Dictionary<string, (string ver, string hash)>();
            var theirTypes = new Dictionary<string, byte>();
            if (r.Remaining > 0)
            {
                uint n = r.VarU32();
                for (uint i = 0; i < n && i < 256 && !r.Bad; i++) { string id = r.Str(), ver = r.Str(), hash = r.Str(); theirs[id] = (ver, hash); }
                uint nt = r.VarU32();
                for (uint i = 0; i < nt && i < 4096 && !r.Bad; i++) { string key = r.Str(); theirTypes[key] = r.U8(); }
                if (r.Bad) { res.Problems.Add("your mod list could not be read"); return res; }
            }
            foreach (var m in MpApi.Mods)
            {
                bool have = theirs.TryGetValue(m.Id, out var t);
                theirs.Remove(m.Id);
                if (have && t.ver == m.Version && t.hash == m.Hash) continue;
                if (ModTransfer.CanSend(m)) res.Send.Add(m);
                else if (!have) res.Problems.Add($"install {m.Name} {m.Version}");
                else if (t.ver != m.Version) res.Problems.Add($"{m.Name}: the host has {m.Version}, you {t.ver}");
                else res.Problems.Add($"{m.Name} {m.Version}: your file differs from the host's (sha256 {MpApi.Short(t.hash)}, host {MpApi.Short(m.Hash)})");
            }
            foreach (var kv in theirs) res.Problems.Add($"remove {kv.Key} {kv.Value.ver} (the host doesn't have it)");
            if (res.Same)
                foreach (var kv in MpApi.Types)
                    if (!theirTypes.TryGetValue(kv.Key, out byte v) || v != kv.Value.Version) res.Problems.Add($"{kv.Key}: different object type version");
            return res;
        }

        // ------------------------------------------------------------------ helpers

        private static void Drop(string why)
        {
            Dropped++; LastDrop = why;
            if (Dropped <= 50) Plugin.Log.LogWarning("[anomaly] mods: " + why);
        }

        private static bool FinitePose(Vector3d p, Quaternion q) =>
            Entities.Finite(p) && Entities.Finite(q) && Math.Abs(p.x) < 1e8 && Math.Abs(p.y) < 1e6 && Math.Abs(p.z) < 1e8;

        private static void WriteBytes(NetWriter w, byte[] b, int n) { w.VarU32((uint)n); if (n > 0) w.Bytes(b, 0, n); }

        private static byte[] ReadBytes(NetReader r, int max)
        {
            uint n = r.VarU32();
            if (r.Bad || n > max || n > r.Remaining) { r.Bad = true; return Array.Empty<byte>(); }
            var a = new byte[n];
            Buffer.BlockCopy(r.Buf, r.Pos, a, 0, (int)n); r.Pos += (int)n;
            return a;
        }

        private static void WriteRot(NetWriter w, Quaternion q) { w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
        private static Quaternion ReadRot(NetReader r)
        {
            var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            float m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (float.IsNaN(m) || m < 1e-6f) return Quaternion.identity;
            float k = 1f / Mathf.Sqrt(m);
            return new Quaternion(q.x * k, q.y * k, q.z * k, q.w * k);
        }

        private static void WriteGlobalPose(NetWriter w, Vector3d g, Quaternion q) { w.F64(g.x); w.F64(g.y); w.F64(g.z); WriteRot(w, q); }
        private static void ReadGlobalPose(NetReader r, out Vector3d g, out Quaternion q) { g = new Vector3d(r.F64(), r.F64(), r.F64()); q = ReadRot(r); }
        private static void WriteWorldPose(NetWriter w, Vector3 unity, Quaternion q) => WriteGlobalPose(w, mainscript.GlobalFromUnityPos(unity), q);
        private static void ReadWorldPose(NetReader r, out Vector3 unity, out Quaternion q)
        {
            ReadGlobalPose(r, out var g, out q);
            unity = mainscript.s != null ? mainscript.UnityPosFromGlobal(g) : (Vector3)g;
        }

        /// Bridge `mp mods`: counters and every object here.
        public static string StatusJson()
        {
            var sb = new StringBuilder("{");
            sb.Append($"\"enabled\":{(Enabled ? "true" : "false")},\"started\":{(_started ? "true" : "false")},\"mods\":{MpApi.Mods.Count},\"types\":{MpApi.Types.Count},");
            sb.Append($"\"shared\":{Shared},\"added\":{Added},\"addMissingType\":{AddMissingType},\"addFailed\":{AddFailed},\"records\":{Records},\"edits\":{Edits},\"editsRefused\":{EditsRefused},");
            sb.Append($"\"poses\":{Poses},\"events\":{Events},\"ownerChanges\":{OwnerChanges},\"removes\":{Removes},\"dropped\":{Dropped},\"lastDrop\":{Json.Str(LastDrop)},");
            sb.Append($"\"clockOffsetMs\":{(_clockOffset * 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},\"serverObjects\":{Server.Count},\"objects\":[");
            bool first = true;
            foreach (var so in MpApi.Local)
            {
                if (!first) sb.Append(','); first = false;
                var p = so.GameObject != null ? mainscript.GlobalFromUnityPos(so.GameObject.transform.position) : default;
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{{\"id\":{0},\"type\":{1},\"owner\":{2},\"epoch\":{3},\"x\":{4:F2},\"y\":{5:F2},\"z\":{6:F2}}}",
                    so.Id, Json.Str(so.Type.Key), so.Owner, so.Epoch, p.x, p.y, p.z));
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
