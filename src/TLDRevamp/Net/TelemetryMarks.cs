using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class TelemetryMarks
    {
        // One new message: requests carry note; broadcasts carry note, host time, anonymous origin and a self bit.
        internal const byte Message = 92;
        private static readonly MarkLimiter Limit = new MarkLimiter();
        internal static void Reset() => Limit.Clear();
        internal static string Note(string note)
        {
            return TelemetryPrivacy.Note(Session.Clean(note));
        }
        internal static string Local(string note)
        {
            note = Note(note);
            if (Mp.Server != null) return Server(0, 0, note) ? "{\"ok\":true}" : "{\"error\":\"one mark per 10 seconds\"}";
            if (!Limit.Try(0, Time.realtimeSinceStartupAsDouble)) return "{\"error\":\"one mark per 10 seconds\"}";
            if (Mp.Client != null)
            {
                if (!Mp.Client.Ready) return "{\"error\":\"not ready\"}";
                var w = new NetWriter(); w.U8(Message); w.Str(note); Mp.Client.SendRaw(w, SteamTransport.SendReliable);
            }
            else SessionLog.Mark(note, "me", SessionLog.St);
            return "{\"ok\":true}";
        }
        internal static bool Server(int from, ulong account, string note)
        {
            ulong key = from == 0 ? 0 : account != 0 ? account : 0xFFFFFFFF00000000UL | (uint)from;
            if (!Limit.Try(key, Time.realtimeSinceStartupAsDouble)) return false;
            note = Note(note); double st = SessionLog.St;
            SessionLog.Mark(note, from == 0 ? "me" : "player", st);
            Mp.Server.SendMark(from, note, st); return true;
        }
        internal static void Client(NetReader r)
        {
            string note = r.Str(); double st = r.F64(); byte origin = r.U8(), self = r.U8();
            if (r.Bad || double.IsNaN(st) || double.IsInfinity(st) || origin > 1 || self > 1) return;
            SessionLog.Mark(Note(note), self == 1 ? "me" : origin == 0 ? "host" : "player", st);
        }
    }
    public static partial class Entities
    {
        private const int TelemetryRecordLimit = 32768;
        private static readonly System.Collections.Generic.Dictionary<uint, save_item> TelemetryRecords = new System.Collections.Generic.Dictionary<uint, save_item>(TelemetryRecordLimit);
        /// Capture observed copies as well as owners: resolving/spawning here would hide the desync.
        internal static void CaptureTelemetry(TelemetryState s)
        {
            var data = savedatascript.s?.data?.itemData;
            TelemetryRecords.Clear();
            try
            {
                if (data != null)
                {
                    int count = Math.Min(data.items.Count, TelemetryRecordLimit);
                    for (int i = 0; i < count; i++) if (data.items[i] != null) TelemetryRecords[data.items[i].id] = data.items[i];
                    s.Dropped += data.items.Count - count;
                }
                CaptureTelemetryState(s);
            }
            finally { TelemetryRecords.Clear(); }
        }
        private static save_item TelemetryRecord(uint id, bool seated = false)
        {
            if (TelemetryRecords.TryGetValue(id, out var record)) return record;
            var data = savedatascript.s?.data?.itemData;
            return seated && data != null && savedatascript.IndexOfID(data.items, id, out int k) ? data.items[k] : null;
        }
        private static void CaptureTelemetryState(TelemetryState s)
        {
            s.St = SessionLog.St; s.LocalPlayer = MyId; s.Day = double.NaN; s.WorldTime = double.NaN;
            s.AllOwnedPhysics = TelemetryAllOwnedPhysics; s.PhysicsSkipped = TelemetryPhysicsSkipped;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            s.HasCenter = pl != null;
            var center = pl != null ? mainscript.GlobalFromUnityPos(pl.transform.position) : default;
            s.X = center.x; s.Y = center.y; s.Z = center.z;
            LocalSeat(out uint localNet, out int localSeat);
            TelemetryPrivacy.Name(Session.MyName());
            s.Add(new StatePlayer { Id = MyId, Name = Session.MyName(), X = center.x, Y = center.y, Z = center.z, HasPos = pl != null, St = s.St, PoseSt = Time.realtimeSinceStartupAsDouble, Net = localNet, Seat = localSeat });
            RemotePlayers.CaptureTelemetry(s);
            CaptureCarried(s, pl);
            var sky = napszakvaltakozas.s;
            if (sky != null)
            {
                s.WorldTime = sky.currentTime * 86400.0; s.Cloud = sky.CloudLowDensityCurrent;
                foreach (var storm in sky.sandStorms) if (storm != null)
                {
                    var g = mainscript.GlobalFromUnityPos(storm.transform.position);
                    s.Sand = Mathf.Max(s.Sand, storm.distanceLerp);
                    s.Add(new StateStorm { X = g.x, Z = g.z, Prefab = storm.prefabid, Size = storm.size, Speed = storm.speed, Life = storm.life, Yaw = storm.transform.eulerAngles.y });
                }
            }
            if (mainscript.s != null) { CaptureStations(s, mainscript.s.radioChs, false); CaptureStations(s, mainscript.s.radioChsAM, true); }
            // Seated cars win the budget, including remote players beyond the local streaming radius.
            for (int pass = 0; pass < 3; pass++) foreach (var e in ByNet.Values)
            {
                bool seated = false;
                for (int p = 0; p < s.PlayerCount; p++) if (s.Players[p].Net == e.NetId && e.NetId != 0) { seated = true; break; }
                var root = e.Root;
                bool isCar = root != null && root.car != null;
                if (seated ? pass != 0 : isCar ? pass != 1 : pass != 2) continue;
                var record = root == null ? TelemetryRecord(e.RootId, seated) : null;
                if (root == null && record == null && !e.FarDirty) { s.Dropped++; continue; }
                var pos = root != null ? mainscript.GlobalFromUnityPos(root.transform.position) : e.FarDirty ? e.FarPos : record != null ? record.transform.pos : e.FarPos;
                double dx = pos.x - center.x, dy = pos.y - center.y, dz = pos.z - center.z;
                if (!seated && (!s.HasCenter || dx * dx + dy * dy + dz * dz > 300 * 300)) { s.Outside++; continue; }
                int prefab = root != null ? root.id : -1;
                var data = savedatascript.s != null ? savedatascript.s.data?.itemData : null;
                if (record != null) prefab = record.prefabID;
                var prefabGo = root == null && itemdatabase.s != null && prefab >= 0 ? itemdatabase.s.Item(prefab) : null;
                var prefabRoot = prefabGo != null ? prefabGo.GetComponent<tosaveitemscript>() : null;
                var car = root != null ? root.car : prefabRoot != null ? prefabRoot.car : null;
                var v = e.Proxy ? e.LastVel : root != null && root.RB != null ? root.RB.velocity : Vector3.zero;
                var radio = root != null ? root.radio : null;
                if (radio == null && car != null && e.Items != null) foreach (var it in e.Items) if (it != null && it.radio != null) { radio = it.radio; break; }
                var row = new StateEntity { Net = e.NetId, Epoch = e.Epoch, Owner = e.OwnerId, Proxy = e.Proxy, Prefab = prefab,
                    Name = TelemetryContent.ItemName(prefab, root != null ? root.name : ""), X = pos.x, Y = pos.y, Z = pos.z,
                    PoseSt = e.Proxy && RidesParent(e) ? double.NaN : e.Proxy && e.Ip != null && e.Ip.Ready ? e.Ip.RenderTime : e.Proxy ? double.NaN : Time.unscaledTimeAsDouble - (Time.timeAsDouble - Time.fixedTimeAsDouble),
                    Vx = v.x, Vy = v.y, Vz = v.z, Speed = v.magnitude, Yaw = root != null ? root.transform.eulerAngles.y : e.FarRot.eulerAngles.y,
                    Stored = e.Proxy ? e.Stored : root != null && IsStored(root), Dormant = root == null, Parent = TelemetryParent(e), Part = e.PartIndex, Car = car != null,
                    Fuel = root != null && car != null && car.Tank != null && car.Tank.F != null ? car.Tank.F.GetAmount() : float.NaN,
                    Engine = car != null && (e.Proxy ? e.EngRunning : car.Engine != null && car.Engine.running), Ignition = car != null && car.ignition,
                    Key = car != null && car.ignitionUsable != null ? car.ignitionUsable.currentTurnState : float.NaN,
                    Radio = radio != null ? radio.FR : float.NaN, RadioOn = radio != null && radio.ON, RadioAm = radio != null && radio.amfm };
                if (!s.Add(row)) continue;
                if (car == null || e.Items == null) continue;
                if (root == null) { CaptureStoredSlots(s, e, data); continue; }
                var seats = VehicleSeats(root);
                for (int i = 0; i < seats.Length; i++) if (seats[i] != null)
                    s.Add(new StateSeat { Net = e.NetId, Index = i, Name = seats[i].name, InUse = seats[i].inUse, InUseMulti = seats[i].inUseMulti });
                for (int i = 0; i < e.Items.Count; i++)
                {
                    var it = e.Items[i]; if (it == null || it.partslotscripts == null || (e.Split != null && e.Split.Contains(i))) continue;
                    for (int j = 0; j < it.partslotscripts.Count; j++)
                    {
                        var slot = it.partslotscripts[j]; if (slot == null) continue;
                        if (slot.parts == null || slot.parts.Count == 0) CaptureSlot(s, e, i, j, slot, null);
                        else foreach (var part in slot.parts) CaptureSlot(s, e, i, j, slot, part);
                    }
                }
            }
            CaptureUsables(s);
        }
        private static void CaptureCarried(TelemetryState s, fpscontroller pl)
        {
            for (int i = 0; i < s.PlayerCount; i++)
            {
                ref var player = ref s.Players[i];
                if (player.Id == MyId && pl != null)
                {
                    if (pl.inHandP != null) CaptureCarry(s, player.Id, pl.inHandP, 0);
                    if (pl.pickedUp != null && pl.pickedUp != pl.inHandP) CaptureCarry(s, player.Id, pl.pickedUp, 0);
                    player.InventoryComplete = pl.inventory != null;
                    if (pl.inventory != null) for (int j = 0; j < pl.inventory.Count; j++) if (pl.inventory[j] != null)
                    { player.InventoryCount++; CaptureCarry(s, player.Id, pl.inventory[j], 2, j); }
                }
                else foreach (var e in ByNet.Values) if (e.OwnerId == player.Id && (e.Held || e.Stored))
                {
                    var record = e.Root == null ? TelemetryRecord(e.RootId) : null;
                    int prefab = e.Root != null ? e.Root.id : record != null ? record.prefabID : -1;
                    s.Add(new StateCarry { Player = player.Id, Net = e.NetId, Item = 0, Prefab = prefab, Name = TelemetryContent.ItemName(prefab, e.Root != null ? e.Root.name : ""), Kind = e.Stored ? 2 : 0, Slot = -1 });
                    if (e.Stored) player.InventoryCount++;
                }
            }
        }
        private static void CaptureCarry(TelemetryState s, int player, pickupable pickup, int kind, int slot = -1)
        {
            var it = pickup.tosave != null ? pickup.tosave : pickup.GetComponentInParent<tosaveitemscript>();
            var e = it != null ? SharedGroupOf(it) : null;
            s.Add(new StateCarry { Player = player, Net = e != null ? e.NetId : 0, Item = e != null ? e.Items.IndexOf(it) : -1, Prefab = it != null ? it.id : -1, Name = TelemetryContent.ItemName(it != null ? it.id : -1, pickup.name), Kind = kind, Slot = slot });
        }
        private static StateUsable CaptureUsable(usablescript u) => new StateUsable { Name = u.name, XRot = u.xRot, YRot = u.yRot, Slide = u.slideValue, Turn = u.currentTurnState, Tuned = u.tunedFloat };
        private static void CaptureUsables(TelemetryState s)
        {
            for (int i = 0; i < s.EntityCount; i++)
            {
                var row = s.Entities[i];
                if (row.Dormant || !row.Car || !ByNet.TryGetValue(row.Net, out var e) || e.Items == null) continue;
                for (int j = 0; j < e.Items.Count; j++)
                {
                    var it = e.Items[j]; if (it == null || e.Split != null && e.Split.Contains(j)) continue;
                    var list = ItemUsables(it);
                    for (int k = 0; k < list.Length; k++) if (list[k] != null)
                    { var u = CaptureUsable(list[k]); u.Net = e.NetId; u.Item = j; u.Index = k; s.Add(u); }
                    var bus = it.busdoorscript;
                    if (bus != null && bus.doors != null) for (int k = 0; k < bus.doors.Length; k++)
                    {
                        var door = bus.doors[k]; if (door == null) continue;
                        float fraction = 0; int n = 0;
                        if (door.doors != null) foreach (var leaf in door.doors) if (leaf != null)
                        {
                            var span = leaf.opened - leaf.closed;
                            if (span.sqrMagnitude > .0001f) { fraction += Mathf.Clamp01(Vector3.Dot(leaf.current - leaf.closed, span) / span.sqrMagnitude); n++; }
                        }
                        s.Add(new StateUsable { Net = e.NetId, Item = j, Index = k, BusDoor = true, Name = "bus door", Slide = n > 0 ? fraction / n : float.NaN, Turn = door.Bopened ? 1 : 0 });
                    }
                }
            }
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (!s.HasCenter || map == null || map.poiGens == null) return;
            foreach (var gen in map.poiGens) if (gen != null && gen.chunks != null)
                foreach (var chunk in gen.chunks.Values) if (chunk.pois != null)
                    foreach (var poi in chunk.pois.Values)
                    {
                        var p = poi.pobj; if (p == null || !p.started || p.usables == null) continue;
                        var g = mainscript.GlobalFromUnityPos(p.transform.position);
                        double dx = g.x - s.X, dy = g.y - s.Y, dz = g.z - s.Z;
                        if (dx * dx + dy * dy + dz * dz > 90000) continue;
                        for (int i = 0; i < p.usables.Count; i++) if (p.usables[i] != null)
                        {
                            var u = CaptureUsable(p.usables[i]); u.Building = true; u.Gen = p.poigenid; u.X = p.posID.x; u.Y = p.posID.y; u.Z = p.posID.z; u.Index = i; s.Add(u);
                        }
                    }
        }
        private static uint TelemetryParent(Ent e)
        {
            var at = e.Root != null ? e.Root.attachable : null;
            if (at != null && at.attached)
            {
                var parent = SharedGroupOf(Fixes.SlotOwnerSave.Owner(at));
                return parent != null ? parent.NetId : 0;
            }
            if (e.Root == null && savedatascript.s?.data?.itemData != null)
                foreach (var rec in savedatascript.s.data.itemData.attachable)
                    if (rec != null && rec.id == e.RootId && rec.attachType == 1)
                        foreach (var parent in ByNet.Values) if (parent.RootId == rec.parentid || (parent.ItemIds != null && parent.ItemIds.Contains(rec.parentid))) return parent.NetId;
            return 0;
        }
        private static void CaptureStoredSlots(TelemetryState s, Ent car, itemDataClass data)
        {
            if (data == null || car.ItemIds == null || itemdatabase.s == null) return;
            for (int i = 0; i < car.ItemIds.Count; i++)
            {
                uint id = car.ItemIds[i];
                if (car.Split != null && car.Split.Contains(i)) continue;
                var record = TelemetryRecord(id, true); if (record == null) { s.Dropped++; continue; }
                var go = itemdatabase.s.Item(record.prefabID);
                var prefab = go != null ? go.GetComponent<tosaveitemscript>() : null;
                if (prefab == null || prefab.partslotscripts == null) continue;
                for (int j = 0; j < prefab.partslotscripts.Count; j++)
                {
                    var slot = prefab.partslotscripts[j]; if (slot == null) continue;
                    bool found = false;
                    foreach (var at in data.attachable)
                    {
                        if (at == null || at.attachType != 1 || at.indexType != 2 || at.parentid != id || at.index != j) continue;
                        var rec = TelemetryRecord(at.id, true); if (rec == null) { s.Dropped++; continue; }
                        Ent group = null; int member = -1;
                        foreach (var e in ByNet.Values)
                            if (e.RootId == at.id) { group = e; member = 0; break; }
                            else if (e.ItemIds != null && e.ItemIds.IndexOf(at.id) >= 0) { group = e; member = e.ItemIds.IndexOf(at.id); }
                        int condition = -1, condition2 = -1, condition3 = -1;
                        foreach (var pc in data.partconditions) if (pc != null && pc.id == at.id) { condition = pc.state; condition2 = pc.state2; condition3 = pc.state3; break; }
                        s.Add(new StateSlot { Net = car.NetId, Item = i, Index = j, Name = slot.name, Type = slot.tipus != null && slot.tipus.Length > 0 ? slot.tipus[0] : "",
                            ContentNet = group != null ? group.NetId : 0, ContentItem = member, LocalItem = rec.id, Prefab = rec.prefabID, Content = TelemetryContent.ItemName(rec.prefabID), Attached = true, Condition = condition, Condition2 = condition2, Condition3 = condition3 });
                        found = true;
                    }
                    if (!found) CaptureSlot(s, car, i, j, slot, null);
                }
            }
        }
        private static void CaptureSlot(TelemetryState s, Ent car, int item, int index, partslotscript slot, attachablescript at)
        {
            var it = at != null ? at.tosave : null;
            var group = it != null ? OwnEntity(it) ?? SharedGroupOf(it) : null;
            s.Add(new StateSlot { Net = car.NetId, Item = item, Index = index, Name = slot.name,
                Type = slot.tipus != null && slot.tipus.Length > 0 ? slot.tipus[0] : "", ContentNet = group != null ? group.NetId : 0,
                ContentItem = it != null && group != null ? group.Items.IndexOf(it) : -1, LocalItem = it != null ? it.idInSave : 0, Prefab = it != null ? it.id : -1,
                Content = it != null ? TelemetryContent.ItemName(it.id, it.name) : "", Attached = at != null && at.attached, Condition = at != null && at.condition != null ? at.condition.state : -1,
                Condition2 = at != null && at.condition != null ? at.condition.state2 : -1, Condition3 = at != null && at.condition != null ? at.condition.state3 : -1 });
        }
        private static void CaptureStations(TelemetryState s, System.Collections.Generic.List<mainscript.radioCH> list, bool am)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && !list[i].custom)
                s.Add(new StateStation { Am = am, Index = i, Clip = list[i].GetCurrentIndex(), Time = list[i].cTime });
        }
    }
    public sealed partial class MpServer
    {
        private bool MarkCommand(int from, string text)
        {
            if (!text.Equals("/mark", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/mark ", StringComparison.OrdinalIgnoreCase)) return false;
            ulong who = ById(from, out var p) ? p.SaveId : 0;
            if (!TelemetryMarks.Server(from, who, text.Length > 5 ? text.Substring(6) : "")) Tell(from, "One /mark per player every 10 seconds.");
            return true;
        }
        internal void MarkRequest(int from, ulong account, NetReader r)
        {
            string note = r.Str();
            if (!r.Bad && !TelemetryMarks.Server(from, account, note)) Tell(from, "One /mark per player every 10 seconds.");
        }
        internal void SendMark(int from, string note, double st)
        {
            foreach (var p in _peers.Values) if (p.Ready)
            {
                _w.Reset(); _w.U8(TelemetryMarks.Message); _w.Str(note); _w.F64(st); _w.U8(from == 0 ? (byte)0 : (byte)1); _w.U8(p.Id == from ? (byte)1 : (byte)0);
                T.Send(p.Conn, _w, SteamTransport.SendReliable);
            }
        }
    }
}
