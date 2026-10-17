using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Per-player saves on the host (docs/PER-PLAYER-SAVES.md, architecture §7). A joiner's game sends its own player
    /// record — the game's save_player (where they stand, stats, inventory, the held item) plus a share record of every
    /// item in the inventory — every 30 s and when it leaves. The host keeps it per Steam ID. When they leave, the
    /// items still in their pocket leave the world with them (removed everywhere, kept in the record, never dropped on
    /// the ground); when they come back, they are placed where they were and the joiner's game spawns the pocket back
    /// into the same slots and loads the stats the game's own way (save_player.Load / LoadInventory). A player who
    /// died comes back fresh: the pocket lies on the ground where they died. On disk the records live next to the
    /// host's saves, one file per world seed, written on every host save; the joiners' pocket items are kept out of
    /// the host's world save (they live in the records). Enabled false = before v0.67.0 (everyone starts fresh).
    public static class PlayerSave
    {
        public static bool Enabled = true;
        public static float RecordEveryS = 30f;
        public const byte Record = 67;    // client → server: str player json, pos, u8 dead, varuint n × (u32 local id, u32 net, varuint len, bytes), [varuint m × u32 local id worn]
        public const byte Restore = 68;   // server → client: str player json, pos, u8 dead, varuint n × (u32 local id, varuint len, bytes), [varuint m × u32 local id worn]
        public static bool ShareWornLate = true;   // A/B: false = before v0.68.5 (shared loose, then put on)
        public static bool KeepWorn = true;   // A/B: false = before v0.68.2 (what they wore stayed in the world without them)
        public const int MaxRecordBytes = 4 << 20, MaxItems = 64;
        public static long RecordsSent, RecordsIn, RecordsRefused, Pocketed, Restored, ItemsRestored, ItemsDropped, FilesWritten, SaveFiltered, WornRestored;
        public static string Last = "";

        // ------------------------------------------------------------------ joiner

        private static float _nextRecord;
        private static NetReader _pending;   // a Restore that came before Ready: applied once the world is ours to play in

        /// Per frame (Plugin): a joiner's record now and then.
        public static void Tick()
        {
            var c = Mp.Client;
            if (!Enabled || c == null || c.Bot != null || !c.Ready) return;
            if (Time.unscaledTime < _nextRecord) return;
            _nextRecord = Time.unscaledTime + RecordEveryS;
            SendRecord();
        }

        /// The joiner's game: its player as its own save would write it, to the host.
        internal static void SendRecord()
        {
            var c = Mp.Client;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (c == null || pl == null || savedatascript.s == null) return;
            save_player sp;
            try { sp = new save_player(pl); } catch (Exception e) { Last = "record: " + e.Message; return; }
            sp.sit = false;   // seats are the host's world's objects: placed on foot
            bool dead = (mainscript.s != null && mainscript.s.died) || sp.hp <= 0f;
            var ids = new List<uint>();
            if (sp.inventory != null) foreach (var id in sp.inventory) if (id != 0 && !ids.Contains(id)) ids.Add(id);
            // the item in the hands: save_player looks for tosaveitemscript on the pickupable itself; it can sit on a parent
            var heldGo = pl.inHandP != null ? pl.inHandP.gameObject : pl.pickedUp != null ? pl.pickedUp.gameObject : null;
            var held = heldGo != null ? heldGo.GetComponentInParent<tosaveitemscript>() : null;
            if (held != null) { sp.heldItem = true; sp.heldItemID = held.idInSave; sp.heldItemRightHand = pl.inHandP != null; }
            if (sp.heldItem && sp.heldItemID != 0 && !ids.Contains(sp.heldItemID)) ids.Add(sp.heldItemID);
            // what they wear (hats, glasses: fpscontroller.weared) — save_player keeps no list of it
            var worn = new List<uint>();
            if (KeepWorn && pl.weared != null)
                foreach (var wp in pl.weared)
                {
                    var wt = wp != null ? (wp.tosave != null ? wp.tosave : wp.GetComponent<tosaveitemscript>()) : null;
                    if (wt == null || wt.idInSave == 0 || ids.Contains(wt.idInSave)) continue;
                    ids.Add(wt.idInSave); worn.Add(wt.idInSave);
                }
            var w = new NetWriter();
            w.U8(Record); w.Str(JsonUtility.ToJson(sp)); Protocol.WritePos(w, Mp.LocalGlobal()); w.U8((byte)(dead ? 1 : 0));
            var rows = new List<(uint id, uint net, byte[] rec)>();
            var missed = new List<string>();
            foreach (var id in ids)
            {
                if (rows.Count >= MaxItems) break;
                if (!savedatascript.s.items.TryGetValue(id, out var it) || it == null) { missed.Add(id + ":gone"); continue; }
                if (!Entities.RootNetOf(it, out uint net)) { missed.Add(id + ":unshared"); continue; }   // not shared (yet): nothing the host could keep
                byte[] rec;
                try { rec = Entities.CaptureRecord(it); } catch { continue; }
                rows.Add((id, net, rec));
            }
            w.VarU32((uint)rows.Count);
            foreach (var (id, net, rec) in rows) { w.U32(id); w.U32(net); w.VarU32((uint)rec.Length); w.Bytes(rec, 0, rec.Length); }
            w.VarU32((uint)worn.Count); foreach (var id in worn) w.U32(id);   // (an older host stops reading before this)
            if (w.Len > MaxRecordBytes) { Last = "record too large: " + w.Len + " B"; return; }
            c.SendRaw(w, SteamTransport.SendReliable);
            RecordsSent++;
            Last = $"record sent: {rows.Count} items ({worn.Count} worn), {w.Len} B{(dead ? ", dead" : "")}{(missed.Count > 0 ? ", not kept " + string.Join(" ", missed) : "")}; ids {string.Join(",", ids)}, held {(held != null ? held.name + "/" + held.idInSave : "none")} (hand {(pl.inHandP != null ? pl.inHandP.name : "-")}, picked {(pl.pickedUp != null ? pl.pickedUp.name : "-")})";
        }

        /// Leaving (Mp.Stop): the last record goes out before the connection closes.
        internal static void ClientLeaving()
        {
            var c = Mp.Client;
            if (!Enabled || c == null || c.Bot != null || !c.Ready) return;
            SendRecord();
        }

        internal static void ClientRestoreIn(NetReader r)
        {
            if (!Enabled || Mp.Client == null || Mp.Client.Bot != null) return;
            var copy = new NetReader(); copy.Load(r.Remaining); Buffer.BlockCopy(r.Buf, r.Pos, copy.Buf, 0, r.Remaining);
            _pending = copy;
            if (Mp.Client.Ready) ClientReady();
        }

        /// The joiner is standing in the host's world (Ready sent): its own things back, the game's way.
        public static string WornLog = "";
        private static void WornNote(string what)
        {
            WornLog = (WornLog.Length > 400 ? WornLog.Substring(WornLog.Length - 300) : WornLog) + what + "; ";
            Plugin.Log.LogInfo("player save: " + what);
        }

        internal static void ClientReady()
        {
            _nextRecord = Time.unscaledTime + 5f;
            var r = _pending; _pending = null;
            if (r == null) return;
            string json = r.Str(); var g = Protocol.ReadPos(r); bool dead = r.U8() == 1; int n = (int)r.VarU32();
            if (r.Bad || n < 0 || n > MaxItems) { Last = "restore: bad message"; return; }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || savedatascript.s == null) { Last = "restore: no player"; return; }
            save_player sp = null;
            try { if (!string.IsNullOrEmpty(json)) sp = JsonUtility.FromJson<save_player>(json); } catch (Exception e) { Last = "restore json: " + e.Message; }
            var here = Mp.LocalGlobal();
            var at = dead ? g : new Vector3d(here.x, here.y + 1.0, here.z);   // dead: the pocket on the ground where they fell
            var map = new Dictionary<uint, uint>();
            var bodies = new List<(uint oldId, byte[] bytes)>();
            for (int i = 0; i < n; i++)
            {
                uint oldId = r.U32(); int len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining) { Last = "restore: bad item"; break; }
                var b0 = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, b0, 0, len); r.Pos += len;
                bodies.Add((oldId, b0));
            }
            var wornOld = new List<uint>();
            if (!r.Bad && r.Remaining > 0)
            {
                int m = (int)r.VarU32();
                for (int i = 0; i < m && i < MaxItems && !r.Bad; i++) wornOld.Add(r.U32());
                if (r.Bad) wornOld.Clear();
            }
            var shareAfterWear = new List<tosaveitemscript>();
            foreach (var (oldId, bytes) in bodies)
            {
                try
                {
                    var d = RecordCodec.Decode(bytes);
                    if (d == null || d.items == null || d.items.Count == 0) continue;
                    uint rootOld = d.items[0].id;
                    ItemSnapshot.Move(d, at - d.items[0].transform.pos);
                    var m = ItemSnapshot.SpawnGroup(d);
                    if (!m.TryGetValue(rootOld, out uint rootNew) || !savedatascript.s.items.TryGetValue(rootNew, out var root) || root == null) continue;
                    map[oldId] = rootNew;
                    // what they wore is shared once it is on their head again (the share then says so: Entities.WornInShare)
                    if (ShareWornLate && !dead && wornOld.Contains(oldId)) shareAfterWear.Add(root);
                    else Entities.ShareItem(root, null);
                    ItemsRestored++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("player save: item " + oldId + " not restored: " + e.Message); }
            }
            if (sp != null && !dead)
            {
                if (sp.inventory != null) for (int i = 0; i < sp.inventory.Length; i++) sp.inventory[i] = map.TryGetValue(sp.inventory[i], out uint v) ? v : 0u;
                if (sp.heldItem) { if (map.TryGetValue(sp.heldItemID, out uint hv)) sp.heldItemID = hv; else sp.heldItem = false; }
                sp.sit = false;
                sp.pos = new vector3(pl.transform.position);   // placed already (JoinSpot): Load's teleport stays here
                try { sp.Load(pl); sp.LoadInventory(pl); }
                catch (Exception e) { Plugin.Log.LogWarning("player save: load failed: " + e); }
                // back on their head (Entities.Worn tells the others once the copies have their network ids)
                foreach (var oldId in wornOld)
                {
                    if (!map.TryGetValue(oldId, out uint nid) || !savedatascript.s.items.TryGetValue(nid, out var wi) || wi == null || wi.P == null || wi.P.wear == null)
                    { WornNote(oldId + ": not restored"); continue; }
                    int slot = (int)wi.P.wear.tipus;
                    if (slot < 0 || slot >= pl.weared.Length || pl.weared[slot] != null)
                    { WornNote(wi.name + ": slot " + slot + " taken by " + (slot >= 0 && slot < pl.weared.Length && pl.weared[slot] != null ? pl.weared[slot].name : "?")); continue; }
                    try { pl.Wear(wi.P); WornRestored++; WornNote(wi.name + ": worn"); } catch (Exception e) { WornNote(wi.name + ": " + e.Message); Plugin.Log.LogWarning("player save: not worn again: " + e.Message); }
                }
            }
            else if (wornOld.Count > 0) WornNote(wornOld.Count + " worn left loose: " + (dead ? "died" : "no player record"));
            foreach (var root in shareAfterWear) if (root != null) Entities.ShareItem(root, null);
            Restored++;
            Last = $"restored: {map.Count}/{n} items ({wornOld.Count} worn){(dead ? " on the ground (died: fresh)" : "")}";
            Plugin.Log.LogInfo("player save " + Last);
            SendRecord();   // the host's record now names the new copies
        }

        // ------------------------------------------------------------------ host

        private sealed class Item { public uint Id, Net; public byte[] Rec; }
        private sealed class Rec
        {
            public string Name = "", Json = ""; public Vector3d G; public bool Dead, Present; public int PlayerId = -1;
            public List<Item> Items = new List<Item>();
            public List<uint> Worn = new List<uint>();   // local ids (Item.Id) of the items they wore
        }
        private static readonly Dictionary<ulong, Rec> _recs = new Dictionary<ulong, Rec>();
        private static int _seed;

        /// The records on disk go with the world save they were written with: a file next to it (`<save>.players`, as the
        /// lease table's `.leases`), written with every save of the world and moved with the autosave rotation. Before,
        /// one file per world seed, also written when a player left and when the host stopped. Going to the main menu
        /// stops the session without saving the world, so the next session loaded an older world with newer records: a
        /// lamp picked up after the last save lay on the ground (old world) and came back in the pocket (new record),
        /// twice; one dropped after it was in neither (menunosave.py). And a new world with the same seed took the old
        /// world's records (rejoin.py, fixed seed). Now a world loaded from a save has the records saved with it, a new
        /// world none.
        public static bool WithSaveFile = true;   // A/B: false = one file per seed, written on leave and stop too (v0.68.2)
        private static int _sceneLoads, _recsScene = -1;
        internal static void SceneLoaded() { _sceneLoads++; }

        /// The records of the world now loaded: read once per world from the file next to the save it came from.
        private static void EnsureWorldRecs()
        {
            if (_recsScene == _sceneLoads) return;
            _recsScene = _sceneLoads;
            _recs.Clear();
            if (!Enabled) return;
            var save = Entities.WorldSaveFile;
            if (save == null) { Plugin.Log.LogInfo("player save: new world, no records"); return; }
            try
            {
                var f = ReadFile(save + ".players");
                if (f != null) foreach (var kv in f) _recs[kv.Key] = kv.Value;
                Plugin.Log.LogInfo($"player save: {_recs.Count} players' records loaded with {Path.GetFileName(save)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("player save: records not loaded: " + e.Message); }
        }

        /// The host starts a session: this world's records (hosting again in the same world keeps what is known in
        /// memory, newer than the file).
        internal static void ServerStart()
        {
            if (WithSaveFile)
            {
                EnsureWorldRecs();
                foreach (var rec in _recs.Values) rec.Present = false;
                return;
            }
            if (_seed != Mp.Seed()) _recs.Clear();
            foreach (var rec in _recs.Values) rec.Present = false;
            _seed = Mp.Seed();
            if (!Enabled) return;
            try
            {
                var f = ReadFile(SeedFilePath(_seed));
                if (f == null) return;
                foreach (var kv in f) if (!_recs.ContainsKey(kv.Key)) _recs[kv.Key] = kv.Value;
                Plugin.Log.LogInfo($"player save: {_recs.Count} players' records loaded for this world");
            }
            catch (Exception e) { Plugin.Log.LogWarning("player save: records not loaded: " + e.Message); }
        }

        internal static void ServerRecord(int from, ulong steam, string name, NetReader r)
        {
            if (!Enabled || steam == 0) return;   // no player save without an authenticated Steam account (MpServer.Identify)
            string json = r.Str(); var g = Protocol.ReadPos(r); bool dead = r.U8() == 1; int n = (int)r.VarU32();
            if (r.Bad || n < 0 || n > MaxItems || !Entities.Finite(g)) { RecordsRefused++; return; }
            var rec = new Rec { Name = name ?? "", Json = json ?? "", G = g, Dead = dead, Present = true, PlayerId = from };
            for (int i = 0; i < n; i++)
            {
                uint id = r.U32(), net = r.U32(); int len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining) { RecordsRefused++; return; }
                var bytes = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, bytes, 0, len); r.Pos += len;
                // only what they own: a record can't take other people's things out of the world
                if (!Entities.ServerOwns(net, from)) continue;
                rec.Items.Add(new Item { Id = id, Net = net, Rec = bytes });
            }
            if (r.Remaining > 0)
            {
                int m = (int)r.VarU32();
                for (int i = 0; i < m && i < MaxItems && !r.Bad; i++) { uint id = r.U32(); if (!r.Bad && rec.Items.Exists(x => x.Id == id)) rec.Worn.Add(id); }
                if (r.Bad) rec.Worn.Clear();
            }
            _recs[steam] = rec;
            RecordsIn++;
        }

        /// A joiner left (or the host stops): what is still in their pocket leaves the world with them.
        internal static void ServerOnLeave(int playerId, ulong steam)
        {
            if (!Enabled || steam == 0 || !_recs.TryGetValue(steam, out var rec) || !rec.Present || rec.PlayerId != playerId) return;
            rec.Present = false;
            var keep = new List<Item>();
            foreach (var it in rec.Items)
            {
                if (it.Net == 0 || !Entities.ServerInPocket(it.Net, playerId)) continue;   // dropped since the record: it stays in the world
                Entities.ServerRemoveItem(-1, it.Net);
                it.Net = 0; keep.Add(it); Pocketed++;
            }
            rec.Items = keep;
            rec.Worn.RemoveAll(id => !keep.Exists(x => x.Id == id));
            Last = $"player {playerId} left: {keep.Count} items in their pocket ({rec.Worn.Count} worn)";
            Plugin.Log.LogInfo("player save: " + Last);
            if (keep.Count > 0 && !WithSaveFile) WriteFile(SeedFilePath(_seed));   // else: on disk with the next save of the world
        }

        /// The host ends the session: everyone's pocket leaves the world, the records go to disk.
        internal static void ServerStopping(List<Session.Player> players)
        {
            if (!Enabled) return;
            foreach (var p in players) if (!p.Host) ServerOnLeave(p.Id, p.SteamId);
            if (!WithSaveFile) WriteFile(SeedFilePath(_seed));   // else: the session-end save (or a later save of this world)
            foreach (var rec in _recs.Values) rec.Present = false;
        }

        /// SpotReq: where a returning player stands (alive: where they left).
        internal static bool ServerSpot(ulong steam, out double gx, out double gz)
        {
            gx = gz = 0;
            if (!Enabled || steam == 0 || !_recs.TryGetValue(steam, out var rec) || rec.Present || rec.Dead) return false;
            gx = rec.G.x; gz = rec.G.z;
            return true;
        }

        /// After the JoinSpot: their record back to them (their next record replaces it here).
        internal static void ServerSendRestore(int playerId, ulong steam, Action<NetWriter> send)
        {
            if (!Enabled || steam == 0 || !_recs.TryGetValue(steam, out var rec) || rec.Present) return;
            var w = new NetWriter();
            w.U8(Restore); w.Str(rec.Dead ? "" : rec.Json); Protocol.WritePos(w, rec.G); w.U8((byte)(rec.Dead ? 1 : 0));
            w.VarU32((uint)rec.Items.Count);
            foreach (var it in rec.Items) { w.U32(it.Id); w.VarU32((uint)it.Rec.Length); w.Bytes(it.Rec, 0, it.Rec.Length); }
            var worn = rec.Dead ? new List<uint>() : rec.Worn;   // died: everything lies where they fell
            w.VarU32((uint)worn.Count); foreach (var id in worn) w.U32(id);
            send(w);
            ItemsDropped += rec.Dead ? rec.Items.Count : 0;
            // handed back: from here their own game has them (its next record names the new copies)
            rec.Items = new List<Item>(); rec.Worn = new List<uint>(); rec.Present = true; rec.PlayerId = playerId; rec.Dead = false;
            Plugin.Log.LogInfo($"player save: {rec.Name} is back");
        }

        // ------------------------------------------------------------------ disk

        /// On disk: our own binary format. It was JSON through Unity's JsonUtility, which can't see types from a mod's
        /// assembly: every file ever written was "{}" (found 2026-10-17, menunosave.py), so after a restart of the host's
        /// game no joiner got their pocket back, and those items had left the world with them.
        private const uint FileMagic = 0x31505254;   // "TRP1"
        private static string SeedFilePath(int seed) => Path.Combine(pathscript.SavePath(), "revamp-players-" + seed + ".bin");

        /// The file, or when it is missing or unreadable (the game stopped while it was being written), the one being
        /// written (.tmp) or the one before (.bak).
        private static Dictionary<ulong, Rec> ReadFile(string path)
        {
            foreach (var p in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var bytes = File.ReadAllBytes(p);
                    var r = new NetReader(); r.Load(bytes.Length); Buffer.BlockCopy(bytes, 0, r.Buf, 0, bytes.Length);
                    if (r.U32() != FileMagic) throw new Exception("not a players file");
                    var outp = new Dictionary<ulong, Rec>();
                    int n = (int)r.VarU32();
                    for (int k = 0; k < n && !r.Bad; k++)
                    {
                        ulong sid = r.U64();
                        var rec = new Rec { Name = r.Str() ?? "", Json = r.Str() ?? "", G = new Vector3d(r.F64(), r.F64(), r.F64()), Dead = r.Bool() };
                        int m = (int)r.VarU32();
                        for (int i = 0; i < m && !r.Bad; i++)
                        {
                            uint id = r.U32(); int len = (int)r.VarU32();
                            if (len < 0 || len > r.Remaining) { r.Bad = true; break; }
                            var rb = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, rb, 0, len); r.Pos += len;
                            rec.Items.Add(new Item { Id = id, Rec = rb });
                        }
                        int w = (int)r.VarU32();
                        for (int i = 0; i < w && !r.Bad; i++) rec.Worn.Add(r.U32());
                        if (!r.Bad && sid != 0) outp[sid] = rec;
                    }
                    if (r.Bad) throw new Exception("cut short");
                    if (p != path) Plugin.Log.LogWarning("player save: " + Path.GetFileName(path) + " missing or unreadable: read " + Path.GetFileName(p));
                    return outp;
                }
                catch (Exception e) { Plugin.Log.LogWarning("player save: " + Path.GetFileName(p) + " not readable: " + e.Message); }
            }
            return null;
        }

        private static void WriteFile(string path)
        {
            if (!Enabled) return;
            try
            {
                var w = new NetWriter();
                w.U32(FileMagic); w.VarU32((uint)_recs.Count);
                foreach (var kv in _recs)
                {
                    var rec = kv.Value;
                    w.U64(kv.Key); w.Str(rec.Name); w.Str(rec.Json); w.F64(rec.G.x); w.F64(rec.G.y); w.F64(rec.G.z); w.Bool(rec.Dead);
                    w.VarU32((uint)rec.Items.Count);
                    foreach (var it in rec.Items) { w.U32(it.Id); w.VarU32((uint)it.Rec.Length); w.Bytes(it.Rec, 0, it.Rec.Length); }
                    w.VarU32((uint)rec.Worn.Count); foreach (var id in rec.Worn) w.U32(id);
                }
                using (var fs = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write)) fs.Write(w.Buf, 0, w.Len);
                // swapped in one step, the previous file kept as .bak (a delete then a move left no file between them)
                if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak");
                else File.Move(path + ".tmp", path);
                FilesWritten++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("player save: records not written: " + e.Message); }
        }

        /// The host saves: the joiners' pocket items stay out of the world save (they are in the records).
        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SaveToData))]
        private static class HostSave
        {
            [HarmonyPostfix]
            private static void Postfix(savedata _data)
            {
                if (!Enabled || Mp.Server == null || _data == null || _data.itemData == null) return;
                try
                {
                    var drop = new HashSet<uint>();
                    foreach (var rec in _recs.Values)
                        if (rec.Present)
                            foreach (var it in rec.Items)
                                if (it.Net != 0 && Entities.ServerInPocket(it.Net, rec.PlayerId)) Entities.LocalIdsOf(it.Net, drop);
                    if (drop.Count > 0) SaveFiltered += Entities.DropFromSave(_data.itemData, drop);
                }
                catch (Exception e) { Plugin.Log.LogWarning("player save: pocket items not kept out of the save: " + e.Message); }
                if (!WithSaveFile) WriteFile(SeedFilePath(_seed));
            }
        }

        /// Every save of the world (the host's, the session-end one, single player): its records next to it. A world
        /// without records leaves none there (one from the save it replaces would belong to another world).
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        private static class RecordsWithSave
        {
            [HarmonyPrefix]   // before the lease table's postfix moves the world on to the new file
            private static void Prefix() { if (Enabled && WithSaveFile && Mp.Client == null) EnsureWorldRecs(); }

            [HarmonyPostfix]
            private static void Postfix(string _text, bool __runOriginal)
            {
                if (!Enabled || !WithSaveFile || !__runOriginal || Mp.Client != null || _recsScene != _sceneLoads) return;
                try
                {
                    var path = pathscript.SaveDataName(pathscript.UnDuckStringBecauseItIsDuckedByDefault(_text)) + ".players";
                    if (_recs.Count > 0) WriteFile(path);
                    else foreach (var p in new[] { path, path + ".tmp", path + ".bak" }) if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception e) { Plugin.Log.LogWarning("player save: records not written: " + e.Message); }
            }
        }

        public static string Stats()
        {
            var rows = new List<string>();
            foreach (var kv in _recs)
                rows.Add("{\"name\":" + Json.Str(kv.Value.Name) + ",\"present\":" + (kv.Value.Present ? "true" : "false") + ",\"dead\":" + (kv.Value.Dead ? "true" : "false") +
                         ",\"items\":" + kv.Value.Items.Count + ",\"gx\":" + kv.Value.G.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                         ",\"gz\":" + kv.Value.G.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}");
            return "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"sent\":" + RecordsSent + ",\"in\":" + RecordsIn + ",\"refused\":" + RecordsRefused +
                   ",\"pocketed\":" + Pocketed + ",\"restored\":" + Restored + ",\"itemsRestored\":" + ItemsRestored + ",\"wornRestored\":" + WornRestored + ",\"wornLog\":" + Json.Str(WornLog) + ",\"files\":" + FilesWritten +
                   ",\"saveFiltered\":" + SaveFiltered + ",\"last\":" + Json.Str(Last) + ",\"players\":[" + string.Join(",", rows) + "]}";
        }
    }
}
