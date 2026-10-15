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
        public const byte Record = 67;    // client → server: str player json, pos, u8 dead, varuint n × (u32 local id, u32 net, varuint len, bytes)
        public const byte Restore = 68;   // server → client: str player json, pos, u8 dead, varuint n × (u32 local id, varuint len, bytes)
        public const int MaxRecordBytes = 4 << 20, MaxItems = 64;
        public static long RecordsSent, RecordsIn, RecordsRefused, Pocketed, Restored, ItemsRestored, ItemsDropped, FilesWritten, SaveFiltered;
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
            if (w.Len > MaxRecordBytes) { Last = "record too large: " + w.Len + " B"; return; }
            c.SendRaw(w, SteamTransport.SendReliable);
            RecordsSent++;
            Last = $"record sent: {rows.Count} items, {w.Len} B{(dead ? ", dead" : "")}{(missed.Count > 0 ? ", not kept " + string.Join(" ", missed) : "")}; ids {string.Join(",", ids)}, held {(held != null ? held.name + "/" + held.idInSave : "none")} (hand {(pl.inHandP != null ? pl.inHandP.name : "-")}, picked {(pl.pickedUp != null ? pl.pickedUp.name : "-")})";
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
            for (int i = 0; i < n; i++)
            {
                uint oldId = r.U32(); int len = (int)r.VarU32();
                if (r.Bad || len < 0 || len > r.Remaining) { Last = "restore: bad item"; break; }
                var bytes = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, bytes, 0, len); r.Pos += len;
                try
                {
                    var d = RecordCodec.Decode(bytes);
                    if (d == null || d.items == null || d.items.Count == 0) continue;
                    uint rootOld = d.items[0].id;
                    ItemSnapshot.Move(d, at - d.items[0].transform.pos);
                    var m = ItemSnapshot.SpawnGroup(d);
                    if (!m.TryGetValue(rootOld, out uint rootNew) || !savedatascript.s.items.TryGetValue(rootNew, out var root) || root == null) continue;
                    map[oldId] = rootNew;
                    Entities.ShareItem(root, null);
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
            }
            Restored++;
            Last = $"restored: {map.Count}/{n} items{(dead ? " on the ground (died: fresh)" : "")}";
            Plugin.Log.LogInfo("player save " + Last);
            SendRecord();   // the host's record now names the new copies
        }

        // ------------------------------------------------------------------ host

        private sealed class Item { public uint Id, Net; public byte[] Rec; }
        private sealed class Rec
        {
            public string Name = "", Json = ""; public Vector3d G; public bool Dead, Present; public int PlayerId = -1;
            public List<Item> Items = new List<Item>();
        }
        private static readonly Dictionary<ulong, Rec> _recs = new Dictionary<ulong, Rec>();
        private static int _seed;

        /// The host starts a session: this world's records from disk.
        internal static void ServerStart()
        {
            // hosting again in the same world keeps what is known in memory (newer than the file)
            if (_seed != Mp.Seed()) _recs.Clear();
            foreach (var rec in _recs.Values) rec.Present = false;
            _seed = Mp.Seed();
            if (!Enabled) return;
            try
            {
                var path = FilePath(_seed);
                if (!File.Exists(path)) return;
                var f = JsonUtility.FromJson<FileData>(File.ReadAllText(path));
                if (f == null || f.players == null) return;
                foreach (var p in f.players)
                {
                    if (!ulong.TryParse(p.steam, out ulong sid)) continue;
                    var rec = new Rec { Name = p.name ?? "", Json = p.player ?? "", G = new Vector3d(p.gx, p.gy, p.gz), Dead = p.dead };
                    for (int i = 0; p.items != null && i < p.items.Count; i++)
                        rec.Items.Add(new Item { Id = i < p.ids.Count ? p.ids[i] : 0, Rec = Convert.FromBase64String(p.items[i]) });
                    if (!_recs.ContainsKey(sid)) _recs[sid] = rec;
                }
                Plugin.Log.LogInfo($"player save: {_recs.Count} players' records loaded for this world");
            }
            catch (Exception e) { Plugin.Log.LogWarning("player save: records not loaded: " + e.Message); }
        }

        internal static void ServerRecord(int from, ulong steam, string name, NetReader r)
        {
            if (!Enabled) return;
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
            _recs[steam] = rec;
            RecordsIn++;
        }

        /// A joiner left (or the host stops): what is still in their pocket leaves the world with them.
        internal static void ServerOnLeave(int playerId, ulong steam)
        {
            if (!Enabled || !_recs.TryGetValue(steam, out var rec) || !rec.Present || rec.PlayerId != playerId) return;
            rec.Present = false;
            var keep = new List<Item>();
            foreach (var it in rec.Items)
            {
                if (it.Net == 0 || !Entities.ServerInPocket(it.Net, playerId)) continue;   // dropped since the record: it stays in the world
                Entities.ServerRemoveItem(-1, it.Net);
                it.Net = 0; keep.Add(it); Pocketed++;
            }
            rec.Items = keep;
            Last = $"player {playerId} left: {keep.Count} items in their pocket";
            Plugin.Log.LogInfo("player save: " + Last);
            if (keep.Count > 0) WriteFile();   // the pocket is out of the world now: on disk at once, not at the next save
        }

        /// The host ends the session: everyone's pocket leaves the world, the records go to disk.
        internal static void ServerStopping(List<Session.Player> players)
        {
            if (!Enabled) return;
            foreach (var p in players) if (!p.Host) ServerOnLeave(p.Id, p.SteamId);
            WriteFile();
            foreach (var rec in _recs.Values) rec.Present = false;
        }

        /// SpotReq: where a returning player stands (alive: where they left).
        internal static bool ServerSpot(ulong steam, out double gx, out double gz)
        {
            gx = gz = 0;
            if (!Enabled || !_recs.TryGetValue(steam, out var rec) || rec.Present || rec.Dead) return false;
            gx = rec.G.x; gz = rec.G.z;
            return true;
        }

        /// After the JoinSpot: their record back to them (their next record replaces it here).
        internal static void ServerSendRestore(int playerId, ulong steam, Action<NetWriter> send)
        {
            if (!Enabled || !_recs.TryGetValue(steam, out var rec) || rec.Present) return;
            var w = new NetWriter();
            w.U8(Restore); w.Str(rec.Dead ? "" : rec.Json); Protocol.WritePos(w, rec.G); w.U8((byte)(rec.Dead ? 1 : 0));
            w.VarU32((uint)rec.Items.Count);
            foreach (var it in rec.Items) { w.U32(it.Id); w.VarU32((uint)it.Rec.Length); w.Bytes(it.Rec, 0, it.Rec.Length); }
            send(w);
            ItemsDropped += rec.Dead ? rec.Items.Count : 0;
            // handed back: from here their own game has them (its next record names the new copies)
            rec.Items = new List<Item>(); rec.Present = true; rec.PlayerId = playerId; rec.Dead = false;
            Plugin.Log.LogInfo($"player save: {rec.Name} is back");
        }

        // ------------------------------------------------------------------ disk

        [Serializable] public class FileData { public List<FilePlayer> players = new List<FilePlayer>(); }
        [Serializable] public class FilePlayer
        {
            public string steam, name, player; public double gx, gy, gz; public bool dead;
            public List<uint> ids = new List<uint>(); public List<string> items = new List<string>();
        }

        private static string FilePath(int seed) => Path.Combine(pathscript.SavePath(), "revamp-players-" + seed + ".json");

        internal static void WriteFile()
        {
            if (!Enabled || Mp.Server == null) return;
            try
            {
                var f = new FileData();
                foreach (var kv in _recs)
                {
                    var p = new FilePlayer { steam = kv.Key.ToString(), name = kv.Value.Name, player = kv.Value.Json, gx = kv.Value.G.x, gy = kv.Value.G.y, gz = kv.Value.G.z, dead = kv.Value.Dead };
                    foreach (var it in kv.Value.Items) { p.ids.Add(it.Id); p.items.Add(Convert.ToBase64String(it.Rec)); }
                    f.players.Add(p);
                }
                var path = FilePath(_seed);
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(f));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
                FilesWritten++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("player save: records not written: " + e.Message); }
        }

        /// The host saves: the joiners' pocket items stay out of the world save (they are in the records), and the
        /// records go to disk with it.
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
                WriteFile();
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
                   ",\"pocketed\":" + Pocketed + ",\"restored\":" + Restored + ",\"itemsRestored\":" + ItemsRestored + ",\"files\":" + FilesWritten +
                   ",\"saveFiltered\":" + SaveFiltered + ",\"last\":" + Json.Str(Last) + ",\"players\":[" + string.Join(",", rows) + "]}";
        }
    }
}
