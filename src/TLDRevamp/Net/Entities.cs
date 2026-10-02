using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Shared objects (milestone 2: cars). docs/MULTIPLAYER-ARCHITECTURE.md §4b–4d.
    ///  - A car becomes shared when its owner sits in a driver seat (or a scenario shares it): its record group (car +
    ///    parts + locked items, ItemSnapshot/RecordCodec) goes to the server once; the server assigns a network id,
    ///    keeps the record (for later joiners) and sends it to everyone, who spawn it through the game's own load path
    ///    with local ids (ids differ per machine; the network id maps them).
    ///  - Exactly one machine simulates it: the owner. It sends the car's state 20×/s; everyone else shows the car
    ///    kinematic, through PoseInterpolator — never simulating it (the official MP's teleport + dynamic body fight).
    ///  - Ownership changes only through the server, with an epoch: states from an older epoch are ignored. Sitting in
    ///    the driver seat of someone else's car claims it.
    public static partial class Entities
    {
        public const byte Share = 20, Assigned = 21, Add = 22, State = 23, Claim = 24, Owner = 25, PartOff = 26, PartDetached = 27;
        public const byte RemoveItem = 35;

        public sealed class Ent
        {
            public uint NetId, Epoch;
            public int OwnerId;
            public tosaveitemscript Root;
            public List<tosaveitemscript> Items = new List<tosaveitemscript>();
            public bool Proxy;
            public PoseInterpolator Ip;
            public ushort SeqOut, SeqIn; public int Recv, WhyAwake, WhyPos, WhyRot, WhyStored;   // Recv: states this copy received (diagnostics)
            public float SendAcc;
            public bool Driven;
            public bool Stored, SentStored;   // in the owner's inventory (state flag 2); what this owner last sent
            public readonly List<Rigidbody> MadeKinematic = new List<Rigidbody>();
            public Rag Rag;   // a body of limbs (Entities.Ragdoll)
            public Vector3 LastVel;
            public Vector3d ShownPos;
            public Vector3d LastSentPos; public Quaternion LastSentRot = Quaternion.identity; public bool SentAtRest;
            public wheelgraphicsscript[] Wheels;           // car wheel graphics, hierarchy order (same prefab everywhere)
            public float[] WTravel, WSteer, WRpm, WSpin, WBrake, WMotor; public bool HasTorques;   // proxies: latest from the owner (+ spin integrated here)
            public bool EngRunning, EngStart; public float EngRpm;   // proxies: the owner's engine
            public bool SigHas, SigIgnition, SigBrake, SigHandbrake; public int SigGear; public float SigHorn;   // proxies: the owner's car signals (CarSignals)
            public float ResyncAcc; public bool ResyncPhased; public int ResyncCursor = -1; public ulong[] ResyncHashes;
            public Dictionary<int, float> EditHoldUntil;    // items we changed on this copy: sent to the owner
            public Vector3d FarPos; public Quaternion FarRot; public bool FarDirty; public float FarFlushAt;   // dormant: latest pose for the far store   // owner: per item, last record sent
            public uint RootId;                             // local idInSave: survives the game's far-item streaming
            public List<uint> ItemIds;
            public HashSet<int> Split;
            // physical copy (Entities.Physical): a real body near cars simulated here, steered along the owner's path
            public bool Physical, HasServoPrev, GhostOn, CrashTaking, MovedByBody, PartsColOff; public List<Collider> PartCols; public RigidbodyInterpolation OrigInterp; public Transform FreeHit; public RigidbodyInterpolation PhysInterp; public float FreeFrom = -10f, FreeUntil = -10f;
            public Vector3d ServoPrevPos; public Quaternion ServoPrevRot;
            public readonly List<Rigidbody> JointBodies = new List<Rigidbody>();      // group members that came off: their own entity now — the group's resync skips them
            public uint ParentNet;          // a part that came off car ParentNet
            public int PartIndex = -1;
            // what a viewer sees: per-frame deviation from smooth motion (as VehicleLab), visible jump = > 20 cm
            public Vector3d P1, P2; public float Dt1; public int Frames, Jumps, ExtrapFrames; public float MaxDevCm;
            public void Observe(Vector3d p, float dt)
            {
                if (dt <= 0f) return;
                if (Frames >= 2)
                {
                    var v = (Vector3)(P1 - P2) / Mathf.Max(1e-4f, Dt1);
                    var d = (Vector3)(p - P1) - v * dt;
                    float dev = d.magnitude * 100f;
                    if (dev > 20f) { Jumps++; LogJump(this, dev, Mathf.Abs(d.y) * 100f, dt); }
                    if (dev > MaxDevCm) MaxDevCm = dev;
                }
                P2 = P1; P1 = p; Dt1 = dt; Frames++;
            }
        }

        private sealed class SEnt
        {
            public uint NetId, Epoch; public int OwnerId; public byte[] Record; public bool HasState; public Vector3d Pos; public Quaternion Rot;
            public Vector3d Where;                                                   // for interest: last state, else the record's position
            public bool Driven; public float DrivenAt;                                // the owner's player sits in its driver seat
            public bool Stored;                                                       // in the owner's inventory
            public readonly Dictionary<int, byte[]> ItemState = new Dictionary<int, byte[]>(); // latest record per group index, for later joiners
            public uint ParentNet; public int PartIndex = -1;                         // a part that came off
            public readonly List<uint> Parts = new List<uint>();                     // parts that came off this car, in order
            public int CrashReturnTo = -1; public float CrashUntil;                  // crash lease (Entities.CrashAuthority): owner to return to
        }

        private static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        private static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        private static readonly Dictionary<uint, Ent> PendingShare = new Dictionary<uint, Ent>(); // local root id → ent
        private static uint _nextNet = 1;
        private static readonly NetWriter W = new NetWriter();
        private static readonly NetWriter WS = new NetWriter();
        private static float _driverCheck;
        public static long StatesSent, StatesIn, StatesStale, Spawned, OwnerChanges;

        private static bool IsHost => Mp.Server != null;
        internal static bool InSession => Mp.Server != null || (Mp.Client != null && Mp.Client.MyId > 0);
        public static int MyId => Mp.Server != null ? 0 : Mp.Client != null ? Mp.Client.MyId : -1;

        /// Session over. The host keeps the shared world: display copies become its real cars where the others left
        /// them, and the hidden local duplicates (stale versions of cars that moved on) are removed. A client goes back
        /// to its own world: the network copies disappear and its hidden cars come back where they were.
        public static void Reset(bool wasHost = true)
        {
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || !e.Proxy) continue;
                if (wasHost) SetProxy(e, false);
                else
                {
                    foreach (var it in e.Items) if (it != null) UnityEngine.Object.Destroy(it.gameObject);   // OnDestroy takes it off the save list
                    UnityEngine.Object.Destroy(e.Root.gameObject);
                }
            }
            ProxyItems.Clear(); SignalCars.Clear(); RagdollCopies.Clear(); RagdollLimbOwner.Clear();
            HostPhysicsLock = -1; _physLockLast = -1;   // a client: its own setting again
            ProxyWheelOwner.Clear(); ProxyWheelIndex.Clear(); ProxyEngineOwner.Clear();
            foreach (var kv in DisabledBuiltIn)
                foreach (var go in kv.Value) if (go != null) go.SetActive(true);
            DisabledBuiltIn.Clear();
            ByNet.Clear(); Server.Clear(); PendingShare.Clear(); _nextNet = 1;
            Leases.Clear(); LeaseAsked.Clear(); PendingGrants.Clear(); _leasesDone.Clear(); _captureKnown = null; Captures.Clear();
            ShotgunReset();
            AiReset();
            PoiUsablesReset();
        }

        // ------------------------------------------------------------------ local side

        /// Load test: a car's shareable record (the same bytes a Share carries), for bots that "drive" copies of it.
        public static byte[] CaptureRecord(tosaveitemscript root) =>
            RecordCodec.Encode(ItemSnapshot.CaptureGroup(ItemSnapshot.Group(root), new List<itemDataClass>()));

        /// Share the car (group root) — owner = this machine.
        public static string ShareCar(tosaveitemscript root)
        {
            if (root == null || root.car == null) return "{\"error\":\"not a car\"}";
            return ShareItem(root, null);
        }

        /// Share an item or car with everything that belongs to it (parts, locked items) — owner = this machine.
        public static string ShareItem(tosaveitemscript root, List<tosaveitemscript> group)
        {
            if (!InSession) return "{\"error\":\"not in a session\"}";
            if (root == null) return "{\"error\":\"nothing\"}";
            foreach (var e0 in ByNet.Values) if (e0.Root == root) return "{\"already\":" + e0.NetId + "}";
            if (PendingShare.ContainsKey(root.idInSave)) return "{\"pending\":true}";
            var items = group ?? ItemSnapshot.Group(root);
            var perItem = new List<itemDataClass>();
            var cap = ItemSnapshot.CaptureGroup(items, perItem);
            // one record per member, or the copies' indices drift from ours (parts go by index): a member the game
            // can't save (SaveToDictionary returns nothing) stays out of the shared group
            bool aligned = true;
            for (int i = 0; i < items.Count; i++) if (perItem[i] == null || perItem[i].items.Count != 1) { aligned = false; break; }
            if (!aligned)
            {
                var keep = new List<tosaveitemscript>();
                for (int i = 0; i < items.Count; i++)
                {
                    if (perItem[i] != null && perItem[i].items.Count == 1) { keep.Add(items[i]); continue; }
                    ShareMembersDropped++;
                    if (ShareMembersDropped <= 30) Plugin.Log.LogWarning($"share {root.name}: member {i} {(items[i] != null ? items[i].name : "null")} gives {(perItem[i] != null ? perItem[i].items.Count : 0)} records - left out of the group");
                }
                items = keep; perItem = new List<itemDataClass>();
                cap = ItemSnapshot.CaptureGroup(items, perItem);
            }
            byte[] rec = RecordCodec.Encode(cap);
            var ent = new Ent { Root = root, Items = items, OwnerId = MyId, ResyncHashes = StateHashes(items, perItem) };
            PendingShare[root.idInSave] = ent;
            W.Reset(); W.U8(Share); W.U32(root.idInSave); W.VarU32((uint)rec.Length); W.Bytes(rec, 0, rec.Length);
            ToServer(W, true);
            return "{\"shared\":true,\"items\":" + items.Count + ",\"bytes\":" + rec.Length + "}";
        }

        private static void ToServer(NetWriter w, bool reliable)
        {
            if (IsHost)
            {
                var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len);
                byte type = r.U8();
                ServerReceive(0, type, r);
            }
            else Mp.Client?.SendRaw(w, reliable ? SteamTransport.SendReliable : SteamTransport.SendUnreliable);
        }

        private static bool _wasSleeping;

        /// In the local player's inventory with its model hidden (fpscontroller.InvStore: parented to the inventory point,
        /// disableThisWhenStored off). The copies elsewhere showed it floating in the middle of that player (v0.64.4,
        /// first play session with a friend).
        private static bool IsStored(tosaveitemscript it)
        {
            var p = it != null ? it.P : null;
            return p != null && p.disableThisWhenStored != null && !p.disableThisWhenStored.gameObject.activeSelf && p.inInventory() > 0;
        }

        /// A copy follows its owner's inventory: hidden the way the game hides a stored item, shown again when it comes out.
        private static void ShowStored(Ent e, bool stored)
        {
            e.Stored = stored;
            var p = e.Root != null ? e.Root.P : null;
            if (p != null && p.disableThisWhenStored != null) p.disableThisWhenStored.gameObject.SetActive(!stored);
            StoredShown++;
        }
        public static long StoredShown;

        /// Per frame (after Mp.Tick): owner states out, proxies shown, driver-seat claims.
        private static float _lastDriveClaimTry;
        /// below these a loose, awake body counts as at rest (m/s, rad/s)
        public static float RestSpeed = 0.05f, RestSpin = 0.5f;

        /// Cars change hands by driving only: while the local player sits in the DRIVER seat (mainseat) of a
        /// proxy car, ask the server for it. Retried: the server refuses while the current owner is actively
        /// driving it (their state marks it Driven); parked or exited, the claim goes through and the Owner
        /// message converts this machine's copy to a real, drivable car (and theirs to a proxy).
        private static void DriveClaimTick()
        {
            if (Time.unscaledTime - _lastDriveClaimTry < 1.5f) return;
            _lastDriveClaimTry = Time.unscaledTime;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null) return;
            if (!pl.seat.driverSeat0) return;   // passengers never claim — incl. shotgun (driverSeat1 = the front passenger)
            var root = pl.seat.transform.root;
            foreach (var e in ByNet.Values)
                if (e.Proxy && e.Root != null && e.Root.transform.root == root)
                {
                    W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
                    ClaimsSent++;
                    return;
                }
        }

        public static void Tick()
        {
            if (!InSession) return;
            float dt = Time.unscaledDeltaTime;
            DriveClaimTick();
            ShotgunTick(dt);
            PushTick(dt);
            RadioTick(dt);
            PhysLockTick(dt);
            PushGripTick();
            ServerTimeTick(dt);
            ServerInterestTick(dt);
            ServerHandoffTick(dt);
            // E3: a finished sleep races the world clock on the sleeper's machine only — broadcast the jump
            bool sleepingNow = mainscript.s != null && mainscript.s.sleeping;
            if (_wasSleeping && !sleepingNow)
            {
                float wt = mainscript.s != null ? mainscript.s.GetCurrentT() : -1f;
                if (wt >= 0f) { W.Reset(); W.U8(SleepSync); W.F32(wt); ToServer(W, true); }
            }
            _wasSleeping = sleepingNow;
            double now = Time.realtimeSinceStartupAsDouble;
            _driverCheck += dt;
            if (_driverCheck > 0.5f) { _driverCheck = 0; CheckDriverSeat(); }
            LeaseTick();
            EditTick();
            BlastTick();
            AiTick();
            PoiUsableTick();
            foreach (var e in ByNet.Values)
            {
                if (!Resolve(e))   // in the far store: dormant
                {
                    if (e.FarDirty && Time.realtimeSinceStartup >= e.FarFlushAt) { e.FarFlushAt = Time.realtimeSinceStartup + 1f; FlushFarRecords(e); }
                    continue;
                }
                ResyncTick(e, dt);
                if (e.PartIndex >= 0 || e.Items.Count <= 1) { if (RidesParent(e)) continue; }   // bolted in: moves with its parent
                if (!e.Proxy)
                {
                    e.SendAcc += dt;
                    if (e.SendAcc < 1f / Protocol.StateHz) continue;
                    e.SendAcc -= 1f / Protocol.StateHz;
                    if (e.SendAcc > 1f / Protocol.StateHz) e.SendAcc = 0;
                    var rb = e.Root.GetComponent<Rigidbody>();
                    var t = e.Root.transform;
                    var g = mainscript.GlobalFromUnityPos(t.position);
                    var q = t.rotation;
                    // at rest nothing is sent (a house full of items costs nothing); one last state when it stops
                    bool stored = IsStored(e.Root);
                    // awake alone is not motion for a loose item: stacked planks, a shelf of cans can stay awake for minutes
                    // without moving a millimetre (laptop, single player too) - every one sent 20 states a second (leasepair
                    // drift4: ~42 such bodies at one station, 97 % of all states "awake" with no change). Moving means a
                    // real speed (the first frame of a fall or a push) or the pose changed since the last state (1 mm /
                    // 0.1 deg, cumulative: a creep is still sent). Cars stay on awake: engine, steering, wheels change at rest.
                    bool wAwake = rb != null && !rb.isKinematic && !rb.IsSleeping()
                                  && (e.Root.car != null || rb.velocity.sqrMagnitude > RestSpeed * RestSpeed || rb.angularVelocity.sqrMagnitude > RestSpin * RestSpin),
                         wPos = (g - e.LastSentPos).sqrMagnitude > 1e-6,
                         wRot = Quaternion.Angle(q, e.LastSentRot) > 0.1f, wStored = stored != e.SentStored;
                    bool moving = wAwake || wPos || wRot || wStored || RagdollMoved(e);
                    if (wAwake) e.WhyAwake++; if (wPos) e.WhyPos++; if (wRot) e.WhyRot++; if (wStored) e.WhyStored++;   // diagnostics (mp entities)
                    if (!moving && e.SentAtRest) continue;
                    e.SentAtRest = !moving;
                    var v = rb != null && moving ? rb.velocity : Vector3.zero;   // the state at rest: still (as a sleeping body's was)
                    if (e.SeqOut > 0 && ((g - e.LastSentPos).sqrMagnitude > 250000.0 || v.sqrMagnitude > 250000f) && OwnerJumps++ < 30)
                        Plugin.Log.LogWarning($"owner jump: {e.Root.name} net {e.NetId} {System.Math.Sqrt((g - e.LastSentPos).sqrMagnitude):F0} m since the last state, v {v.magnitude:F0} m/s, " +
                                              $"at global {g.x:F0},{g.y:F0},{g.z:F0} parent {(t.parent != null ? t.parent.name : "none")} kin {(rb != null && rb.isKinematic)}");
                    e.LastSentPos = g; e.LastSentRot = q;
                    // pose is from the last physics step: its time on the frame-start clock. `now` (read here, partway
                    // through the frame) was off by however long the frame had run so far — 0–15 ms, varying with
                    // load: at 16 m/s up to 24 cm of timing noise per state, every copy correcting at once (relay
                    // convoy, 16 players: 69 jumps, all copies in the same normal frame, interpolating; v0.57.85)
                    double stamp = Time.unscaledTimeAsDouble - (Time.timeAsDouble - Time.fixedTimeAsDouble);
                    WS.Reset(); WriteStateHead(WS, e.NetId, e.Epoch, ++e.SeqOut, stamp, g, q, v, IsLocalDriver(e.Root), stored);
                    e.SentStored = stored;
                    WriteWheels(WS, e);
                    WriteEngine(WS, e);
                    WriteSignals(WS, e);
                    WriteRagdoll(WS, e);
                    ToServer(WS, false);
                    StatesSent++;
                }
                else if (e.Physical || e.MovedByBody)
                {
                    // moved by physics (FixedTick steers it, or moves the kinematic body): what the viewer sees is the
                    // interpolated body
                    e.Observe(mainscript.GlobalFromUnityPos(e.Root.transform.position), dt);
                }
                else if (e.Ip != null && e.Ip.Ready)
                {
                    e.Ip.Evaluate(now, dt, out var pos, out var rot);
                    e.ShownPos = pos;
                    e.Observe(pos, dt);
                    if (e.Ip.Extrapolating) e.ExtrapFrames++;
                    var t = e.Root.transform;
                    t.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                }
            }
            RagdollTick(dt);   // after the copies moved: their limbs relative to where they are now
        }


        public static long OwnerJumps;

        private static bool IsLocalDriver(tosaveitemscript root)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0) return false;   // driverSeat1 is shotgun
            return pl.seat.transform.root == root.transform.root;
        }

        private static void CheckDriverSeat()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0) { _claimedFor = 0; return; }
            var root = pl.seat.transform.root.GetComponent<tosaveitemscript>();
            if (root == null || root.car == null) return;
            foreach (var e in ByNet.Values)
                if (e.Root == root)
                {
                    if (e.OwnerId != MyId && _claimedFor != e.NetId)
                    {
                        _claimedFor = e.NetId;          // once per sit-down (reset when standing up)
                        W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
                        ClaimsSent++;
                    }
                    return;
                }
            ShareItem(root, null);
        }

        // ------------------------------------------------------------------ server side (host)

        /// Messages from player `from` (0 = the host itself).
        public static void ServerReceive(int from, byte type, NetReader r)
        {
            switch (type)
            {
                case Share:
                {
                    uint localRoot = r.U32(); int n = (int)r.VarU32();
                    if (r.Bad || n > r.Remaining) return;
                    var rec = new byte[n]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, n);
                    var se = new SEnt { NetId = _nextNet++, OwnerId = from, Epoch = 1, Record = rec, Where = RecordRootPos(rec) };
                    Server[se.NetId] = se;
                    // the sharer learns its network id; everyone else gets it by distance (Entities.FarStore: interest)
                    W.Reset(); W.U8(Assigned); W.U32(localRoot); W.U32(se.NetId); W.U32(se.Epoch);
                    ServerSendTo(from, W, true);
                    KnownBy(from).Add(se.NetId);
                    Offer(se, from);
                    break;
                }
                case State:
                {
                    ReadStateHead(r, out uint net, out uint epoch, out _, out var sp, out var sq, out var sv, out bool driven, out bool stored);
                    if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from || se.Epoch != epoch) { StatesStale++; return; }
                    // never thinned away: the state it comes to rest on (owners send one when it stops), a driver change,
                    // going into / out of an inventory
                    bool force = sv.sqrMagnitude < 0.01f || driven != se.Driven || stored != se.Stored || !se.HasState;
                    se.Stored = stored;
                    se.Pos = sp; se.Rot = sq; se.HasState = true; se.Where = sp;
                    se.Driven = driven; se.DrivenAt = Time.realtimeSinceStartup;
                    if (se.PartIndex >= 0 && Server.TryGetValue(se.ParentNet, out var parentCar)) { } // (parts: position kept for later joiners)
                    // relay the same bytes to everyone else, by distance (MpServer.SendCarState); the host applies it locally
                    r.Pos = 0; int len = r.End;
                    WS.Reset(); WS.Bytes(r.Buf, 0, len);
                    if (from != 0) Deliver(WS);
                    Mp.Server?.SendCarState(WS, from, net, se.Pos, force);
                    break;
                }
                case PartOff:
                {
                    uint carNet = r.U32(), epoch = r.U32(); int idx = (int)r.VarU32();
                    var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    SEnt car = null;
                    if (r.Bad || !Server.TryGetValue(carNet, out car) || car.OwnerId != from || car.Epoch != epoch)
                    {
                        PartOffRejected++;
                        PartRejects += "srv(car" + carNet + " from" + from + (car != null ? " owner" + car.OwnerId + " ep" + car.Epoch + "/" + epoch : " none") + ") ";
                        return;
                    }
                    var part = new SEnt { NetId = _nextNet++, OwnerId = from, Epoch = 1, ParentNet = carNet, PartIndex = idx, HasState = true, Pos = pos, Rot = rot };
                    Server[part.NetId] = part;
                    car.Parts.Add(part.NetId);
                    car.ItemState.Remove(idx);   // a later joiner must not get the part's bolted-on record with the car
                    WritePartDetached(W, part);
                    ServerSendAll(W, true, -1);
                    break;
                }
                case LeaseReq:
                {
                    string key = r.Str();
                    if (!r.Bad && key != null) ServerLease(from, key);
                    break;
                }
                case LeaseDone:
                {
                    string key = r.Str();
                    if (!r.Bad && key != null) ServerLeaseDone(from, key);
                    break;
                }
                case Resync: ServerResync(from, r); break;
                case Edit: ServerEdit(from, r); break;
                case ShotgunIn: ServerShotgun(from, r); break;
                case PushIn: ServerPush(from, r); break;
                case PlayerCombat.PlayerDamage: PlayerCombat.ServerReceive(from, r); break;
                case ShotFx: ServerShotFx(from, r); break;
                case ExplodeReq: ServerExplodeReq(from, r); break;
                case ExplosionFx: ServerExplosionFx(from, r); break;
                case BreakHit: ServerBreakHit(from, r); break;
                case BreakFx: ServerFwdAll(from, r, true); break;
                case AiState: ServerFwdAll(from, r, false); break;
                case AiSound: ServerFwdAll(from, r, true); break;
                case PoiUsable: ServerPoiUsable(from, r); break;
                case RemoveItem: { uint net = r.U32(); if (!r.Bad) ServerRemoveItem(from, net); break; }
                case SleepSync: { float wt = r.F32(); if (!r.Bad) ServerSleepSync(from, wt); break; }
                case ContactImpulse:
                {
                    uint net = r.U32();
                    var imp = new Vector3(r.F32(), r.F32(), r.F32());
                    var gp = new Vector3d(r.F64(), r.F64(), r.F64());
                    if (!r.Bad) ServerContactImpulse(from, net, imp, gp);
                    break;
                }
                case DetachReq: { uint net = r.U32(); int idx = (int)r.VarU32(); if (!r.Bad) ServerDetachReq(from, net, idx); break; }
                case AttachSync: ServerAttach(from, r); break;
                case DetachSync: ServerDetachSync(from, r); break;
                case CrashClaim: { uint net = r.U32(), partner = r.U32(); if (!r.Bad) ServerCrashClaim(from, net, partner); break; }
                case CrashRelease: { uint net = r.U32(); if (!r.Bad) ServerCrashRelease(from, net); break; }
                case Claim:
                {
                    uint net = r.U32();
                    if (!Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
                    // a crash is being simulated on another machine: its car comes back when the crash is over
                    if (se.CrashReturnTo >= 0)
                    {
                        ClaimsRefused++;
                        W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                        ServerSendTo(from, W, true);
                        return;
                    }
                    // the first driver keeps the car: no takeover while the owner's player sits in its driver seat
                    // (the owner reports it with every state; a stale report counts as not driving)
                    if (se.Driven && Time.realtimeSinceStartup - se.DrivenAt < 1f)
                    {
                        // refused: the claimer may already simulate it (PickupClaim takes provisional ownership) —
                        // tell it who owns it, or both machines simulate the object and their copies drift apart
                        ClaimsRefused++;
                        W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                        ServerSendTo(from, W, true);
                        return;
                    }
                    se.OwnerId = from; se.Epoch++;
                    W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)from); W.U32(se.Epoch);
                    ServerSendAll(W, true, -1);
                    break;
                }
            }
        }

        private static void WriteAdd(NetWriter w, SEnt se)
        {
            w.Reset(); w.U8(Add); w.U32(se.NetId); w.VarU32((uint)se.OwnerId); w.U32(se.Epoch); w.Bool(se.HasState);
            if (se.HasState) { w.F64(se.Pos.x); w.F64(se.Pos.y); w.F64(se.Pos.z); w.F32(se.Rot.x); w.F32(se.Rot.y); w.F32(se.Rot.z); w.F32(se.Rot.w); }
            w.VarU32((uint)se.Record.Length); w.Bytes(se.Record, 0, se.Record.Length);
        }

        private static void WritePartDetached(NetWriter w, SEnt part)
        {
            w.Reset(); w.U8(PartDetached); w.U32(part.ParentNet); w.VarU32((uint)part.PartIndex); w.U32(part.NetId); w.VarU32((uint)part.OwnerId); w.U32(part.Epoch);
            w.F64(part.Pos.x); w.F64(part.Pos.y); w.F64(part.Pos.z); w.F32(part.Rot.x); w.F32(part.Rot.y); w.F32(part.Rot.z); w.F32(part.Rot.w);
        }

        /// A player joined: every shared car (original record), then the parts that came off it, where they are now.
        /// The world clock (napszakvaltakozas: t = time + tekeres - startTime, wrapping at dt + nt) moved to `wt` by
        /// shifting tekeres — the same quantity the game's own sleep advances. Wrap-aware: 23:59 → 00:01 is +2 min.
        public static long WorldTimeJumps;
        internal static void ApplyWorldTime(float wt, bool force)
        {
            var sw = napszakvaltakozas.s;
            if (sw == null || (mainscript.s != null && mainscript.s.sleeping)) return;   // our own sleep races the clock; it syncs when it ends
            float len = sw.dt + sw.nt, diff = wt - sw.t;
            if (len > 0f) { if (diff > len * 0.5f) diff -= len; else if (diff < -len * 0.5f) diff += len; }
            if (!force && Mathf.Abs(diff) < 1f) return;   // within a second: leave it (no sky jitter)
            sw.tekeres += diff;
            WorldTimeJumps++;
        }

        private static float _timeSyncAcc;
        /// Server: the world clock to everyone every 10 s (the official MP's multiSyncTime) — clocks that stopped (a
        /// slow world load, a hitch, a sleep) come back in line.
        private static void ServerTimeTick(float dt)
        {
            if (Mp.Server == null || napszakvaltakozas.s == null) return;
            _timeSyncAcc += dt;
            if (_timeSyncAcc < 10f) return;
            _timeSyncAcc = 0f;
            if (mainscript.s != null && mainscript.s.sleeping) return;   // the host's sleep: its jump goes out when it ends
            W.Reset(); W.U8(SleepSync); W.F32(napszakvaltakozas.s.t);
            ServerSendAll(W, true, 0);
        }

        public static void ServerOnJoin(int playerId)
        {
            if (napszakvaltakozas.s != null) { W.Reset(); W.U8(SleepSync); W.F32(napszakvaltakozas.s.t); ServerSendTo(playerId, W, true); }   // their world just loaded: the clock now
            // the world's objects follow by distance as soon as their position is known (ServerInterestTick)
            Known[playerId] = new HashSet<uint>();
            ServerPoiUsablesTo(playerId);   // building doors/gates changed this session
        }

        /// A player left: their cars go to the host (it keeps simulating them from the last state).
        /// Leases whose holder confirmed the spawn finished (LeaseDone): safe to keep if the holder leaves, because
        /// everything it spawned was already shared. Undone leases are freed: their buildings can be spawned again.
        private static readonly HashSet<string> _leasesDone = new HashSet<string>();

        public static void ServerOnLeave(int playerId)
        {
            Known.Remove(playerId);
            foreach (var se in Server.Values)
                if (se.OwnerId == playerId)
                {
                    se.OwnerId = 0; se.Epoch++;
                    W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32(0); W.U32(se.Epoch);
                    ServerSendAll(W, true, -1);
                }
            // the leaver's leases: finished ones stay (their content is shared, holder 0 = the server), unfinished
            // ones are freed so the building isn't stuck half-spawned with spawn flags on everywhere
            var keys = new List<string>(Leases.Keys);
            foreach (var key in keys)
            {
                if (Leases[key] != playerId) continue;
                if (_leasesDone.Contains(key)) { Leases[key] = 0; continue; }
                Leases.Remove(key); _leasesDone.Remove(key); LeasesFreed++;
                Plugin.Log.LogInfo($"lease {key}: freed (holder {playerId} left before the spawn was done)");
                W.Reset(); W.U8(LeaseFreed); W.Str(key);
                ServerSendAll(W, true, -1);
            }
        }

        internal static void SendToServer(NetWriter w, bool reliable) => ToServer(w, reliable);

        /// Server: pass a received message on unchanged to one player (0 = this machine).
        internal static void ServerForwardRaw(int playerId, NetReader r)
        {
            var w = new NetWriter(); w.Bytes(r.Buf, 0, r.End);
            ServerSendTo(playerId, w, true);
        }

        private static void ServerSendTo(int playerId, NetWriter w, bool reliable)
        {
            if (playerId == 0) { Deliver(w); return; }
            Mp.Server?.SendToPlayer(playerId, w, reliable);
        }

        private static void ServerSendAll(NetWriter w, bool reliable, int except, Vector3d? near = null, double range = 0)
        {
            if (except != 0) Deliver(w);
            Mp.Server?.SendToAll(w, reliable, except, near, range);
        }

        private static void Deliver(NetWriter w)
        {
            var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len);
            ClientReceive(r.U8(), r);
        }

        // ------------------------------------------------------------------ every machine

        public static void ClientReceive(byte type, NetReader r)
        {
            switch (type)
            {
                case Assigned:
                {
                    uint localRoot = r.U32(), net = r.U32(), epoch = r.U32();
                    if (!PendingShare.TryGetValue(localRoot, out var e)) return;
                    PendingShare.Remove(localRoot);
                    e.NetId = net; e.Epoch = epoch; e.OwnerId = MyId;
                    ByNet[net] = e;
                    Bind(e);
                    break;
                }
                case Add:
                {
                    uint net = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    bool hasState = r.Bool(); Vector3d pos = default; Quaternion rot = Quaternion.identity;
                    if (hasState) { pos = new Vector3d(r.F64(), r.F64(), r.F64()); rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()); }
                    int n = (int)r.VarU32();
                    if (r.Bad || n > r.Remaining || ByNet.ContainsKey(net)) return;
                    var rec = new byte[n]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, n);
                    var d = RecordCodec.Decode(rec);
                    if (d == null || d.items.Count == 0) return;
                    if (FarFromHere(hasState ? pos : d.items[0].transform.pos)) { AddFar(net, owner, epoch, d, hasState, pos, rot); break; }
                    uint rootOld = d.items[0].id;
                    var prefabs = new List<int>(); foreach (var r0 in d.items) prefabs.Add(r0.prefabID);
                    var order = new List<uint>();
                    var map = ItemSnapshot.SpawnGroup(d, order);
                    if (!savedatascript.s.items.TryGetValue(map[rootOld], out var root)) return;
                    var e = new Ent { NetId = net, OwnerId = owner, Epoch = epoch, Root = root };
                    // every record keeps its place, a part that didn't spawn here too (empty): parts are identified by
                    // their index in the group. Before v0.64.5 a missing one was skipped and every part after it moved up
                    // one — the host pulled a van's grille, the owner took off its door (first play session)
                    for (int i = 0; i < order.Count; i++)
                    {
                        if (savedatascript.s.items.TryGetValue(order[i], out var it) && it != null) { e.Items.Add(it); continue; }
                        e.Items.Add(null); CopyMembersMissing++;
                        if (CopyMembersMissing <= 30) Plugin.Log.LogWarning($"copy of net {net} ({root.name}): member {i} ({PrefabName(prefabs[i])}) didn't spawn here - its place stays empty");
                    }
                    ByNet[net] = e;
                    Bind(e);
                    Spawned++;
                    if (hasState) root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                    if (owner != MyId) SetProxy(e, true);
                    break;
                }
                case State:
                {
                    ReadStateHead(r, out uint net, out uint epoch, out double st, out var sp, out var sq, out var sv, out bool driven, out bool stored);
                    if (r.Bad || !ByNet.TryGetValue(net, out var e) || !e.Proxy || epoch != e.Epoch) { StatesStale++; return; }
                    e.Recv++;
                    var s = new PoseInterpolator.Sample { T = st, Pos = sp, Rot = sq, Vel = sv };
                    e.Driven = driven;
                    if (stored != e.Stored) ShowStored(e, stored);
                    ReadWheels(r, e);
                    ReadEngine(r, e);
                    ReadSignals(r, e);
                    ReadRagdoll(r, e);
                    if (r.Bad) return;
                    e.LastVel = s.Vel;
                    e.Ip?.Add(s, Time.realtimeSinceStartupAsDouble);
                    if (e.Root == null) UpdateFarRecord(e, s.Pos, s.Rot);   // stored far away here: it reappears where it is
                    StatesIn++;
                    break;
                }
                case PartDetached:
                {
                    uint carNet = r.U32(); int idx = (int)r.VarU32(); uint partNet = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    if (r.Bad) { PartRejects += "bad "; return; }
                    if (!ByNet.TryGetValue(carNet, out var car)) { PartRejects += "nocar" + carNet + " "; return; }
                    if (idx < 0 || idx >= car.Items.Count) { PartRejects += "idx" + idx + "/" + car.Items.Count + " "; return; }
                    if (ByNet.ContainsKey(partNet)) { PartRejects += "dup" + partNet + " "; return; }
                    // from now on the part's state is its own entity's: a car resync (captured before the crash, or later
                    // by the car's owner, who may no longer simulate it) must not re-bolt or overwrite it
                    (car.Split ?? (car.Split = new HashSet<int>())).Add(idx);
                    var it = car.Items[idx];
                    if (it == null && RebindMembers(car)) it = car.Items[idx];
                    if (it == null)
                    {
                        uint gid = car.ItemIds != null && idx < car.ItemIds.Count ? car.ItemIds[idx] : 0;
                        // our copy of the car is in the far store: the part comes off there — it's placed back loose
                        // where it landed, the car without it
                        if (gid != 0 && DetachStored(gid, pos, rot))
                        {
                            var sp = new Ent { NetId = partNet, OwnerId = owner, Epoch = epoch, ParentNet = carNet, PartIndex = idx, RootId = gid, ItemIds = new List<uint> { gid } };
                            if (owner != MyId) { sp.Proxy = true; sp.Ip = new PoseInterpolator(); }
                            ByNet[partNet] = sp;
                            PartsApplied++; PartsAppliedStored++;
                            break;
                        }
                        PartRejects += "gone" + carNet + "/" + idx + (gid == 0 ? "(noid)" : savedatascript.s.items.ContainsKey(gid) ? "(dead)" : IsStored(gid) ? "(stored)" : "(missing)") + " ";
                        return;
                    }
                    var pe = new Ent { NetId = partNet, OwnerId = owner, Epoch = epoch, Root = it, ParentNet = carNet, PartIndex = idx };
                    pe.Items.Add(it);
                    ByNet[partNet] = pe;
                    Bind(pe);
                    if (owner == MyId) { PartsApplied++; break; }   // the owner already detached it for real
                    // everyone else: the same part comes off their copy, then follows the owner's stream
                    // un-bolted as the owner's was (a bare Detach left it "crafted": wrong layer, colliders off —
                    // the other player couldn't pick it up); fallOff: no smoothing coroutine, the pose is set below
                    if (UnmountTrace.Length < 1500)
                    {
                        var a = it.attachable;
                        UnmountTrace += it.name + "[att " + (a != null && a.attached) + " slot " + (a != null && a.slot != null) + " p0 " + (a != null && a.slot != null && a.slot.part() == a)
                                        + " crafted " + (it.P != null && it.P.crafted) + "] ";
                    }
                    if (it.attachable != null && it.attachable.attached)
                    {
                        _applyingDetach = true;
                        try { if (ReplayBareDetach) it.attachable.Detach(); else Unmount(it.attachable, true); } finally { _applyingDetach = false; }
                    }
                    it.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                    pe.Proxy = false;           // SetProxy(true) below makes its (possibly new) body kinematic
                    ProxyItems.Remove(it.gameObject);
                    SetProxy(pe, true);
                    PartsApplied++;
                    break;
                }
                case LeaseGrant:
                {
                    string key = r.Str();
                    if (!r.Bad && key != null) OnLeaseGrant(key);
                    break;
                }
                case LeaseTaken:
                {
                    string key = r.Str();
                    if (!r.Bad && key != null) OnLeaseTaken(key);
                    break;
                }
                case LeaseFreed:
                {
                    string key = r.Str();
                    if (!r.Bad && key != null) OnLeaseFreed(key);
                    break;
                }
                case Resync: ApplyResync(r); break;
                case Edit: ApplyEdit(r); break;
                case ShotgunIn: ApplyShotgun(r); break;
                case PushIn: ApplyPush(r); break;
                case RadioSync: ApplyRadio(r); break;
                case PhysLockSync: ApplyPhysLock(r); break;
                case AttachSync: ApplyAttachSync(r); break;
                case DetachSync: ApplyDetachSync(r); break;
                case PlayerCombat.PlayerDamage: PlayerCombat.ClientReceive(r); break;
                case ShotFx: ApplyShotFx(r); break;
                case ExplodeReq: ApplyExplodeReq(r); break;
                case ExplosionFx: ApplyExplosionFx(r); break;
                case BreakHit: ApplyBreakHit(r); break;
                case BreakFx: ApplyBreakFx(r); break;
                case AiState: ApplyAiState(r); break;
                case AiSound: ApplyAiSound(r); break;
                case PoiUsable: ApplyPoiUsable(r); break;
                case RemoveItem: { uint net = r.U32(); if (!r.Bad) ApplyRemoveItem(net); break; }
                case DetachReq:
                {
                    uint net = r.U32(); int idx = (int)r.VarU32();
                    if (!r.Bad) ApplyDetachReq(MyId, net, idx);
                    break;
                }
                case SleepSync:
                {
                    float wt = r.F32();
                    if (!r.Bad) ApplyWorldTime(wt, false);   // a sleeper's jump, or the server's periodic clock
                    break;
                }
                case ContactImpulse:
                {
                    uint net = r.U32();
                    Vector3 imp = new Vector3(r.F32(), r.F32(), r.F32());
                    Vector3d gp = new Vector3d(r.F64(), r.F64(), r.F64());
                    if (!r.Bad) ApplyContactImpulse(net, imp, gp);
                    break;
                }
                case Owner:
                {
                    uint net = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    if (!ByNet.TryGetValue(net, out var e)) return;
                    if (owner == MyId) e.SentAtRest = false;   // ours (again, or confirmed after a refused claim): one fresh state out
                    e.OwnerId = owner; e.Epoch = epoch; OwnerChanges++;
                    SetProxy(e, owner != MyId);
                    break;
                }
            }
        }

        /// Proxy: every rigidbody of the group kinematic (display only), interpolator starts from where it is now.
        /// Owner again: bodies back to dynamic with the last known velocity.
        private static void SetProxy(Ent e, bool proxy, bool keepMotion = false)
        {
            if (e.Proxy == proxy) return;
            if (!proxy && e.Stored) ShowStored(e, false);   // ours now: the game's own state rules its model again
            // not loaded here (stored): only the role changes — Resolve sets the bodies up for it when it's placed.
            // (Returning before setting it left an object handed to this machine while stored a frozen copy of nobody's.)
            // A copy needs its interpolator: states only reach a copy through it (handoff run 1: the host's creature,
            // stored when it was handed away, came back frozen where it was placed — every state dropped).
            if (e.Root == null) { e.Proxy = proxy; e.Ip = proxy ? new PoseInterpolator() : null; return; }
            DropPhysical(e);
            StopBodyMotion(e);
            if (e.PartsColOff) SetPartColliders(e, true);
            e.Proxy = proxy;
            if (proxy)
            {
                DetachContact(e);
                e.MadeKinematic.Clear();
                ApplyProxyBodies(e);
                e.Ip = new PoseInterpolator();
            }
            else
            {
                foreach (var it in e.Items) if (it != null) ProxyItems.Remove(it.gameObject);
                ProxyItems.Remove(e.Root.gameObject);
                RagdollRelease(e);   // its limbs: the game's again (made dynamic below with the other bodies)
                if (e.Wheels != null) foreach (var w in e.Wheels) { ProxyWheelOwner.Remove(w); ProxyWheelIndex.Remove(w); }
                foreach (var eng in e.Root.transform.root.GetComponentsInChildren<enginescript>(true)) ProxyEngineOwner.Remove(eng);
                // the whole group moves at the car's speed (not just the root: every body left at rest was yanked by
                // its joints) — and the game's crash detector must see that speed as "last frame's" too: a kinematic
                // copy reads 0 km/h, so the first owned frame looked like 0 → full speed = a crash against nothing
                // (seat swaps while driving: parts off, even a dead driver)
                // keepMotion (a crash taken over mid-contact, Entities.CrashAuthority): the bodies are already moving with
                // the crash — the copy was physical — so nothing is reset, and the game's crash detector judges it
                var root = e.Root.GetComponent<Rigidbody>();
                var crashVel = root != null ? root.velocity : e.LastVel;
                foreach (var rb in e.MadeKinematic)
                    if (rb != null)
                    {
                        bool wasDyn = !rb.isKinematic;
                        rb.isKinematic = false;
                        if (!keepMotion) rb.velocity = e.LastVel;
                        else if (!wasDyn) rb.velocity = crashVel;   // bodies the physical copy kept kinematic move with it
                    }
                if (root != null && !root.isKinematic && !keepMotion) root.velocity = e.LastVel;
                var carS = e.Root.car;
                if (carS != null && !keepMotion)
                {
                    float kmh = Vector3.Project(e.LastVel, carS.transform.forward).magnitude * 3.6f;
                    carS.speed = kmh; carS.lspeed = kmh; carS.LV = e.LastVel;
                    DamageGraceUntil[carS] = Time.time + 0.5f;   // wheels spinning up from the copy's rest
                }
                e.MadeKinematic.Clear();
                e.Ip = null;
                AttachContact(e);   // simulated here: relay contact impulses to the other machine's entity
            }
        }

        /// Every body of every item in the group kinematic, the objects registered as display copies. Jointed parts
        /// (attachablescript.parentAtAttach off) are not children of the car — left dynamic, they were dragged into
        /// crash-level collisions and fell off.
        private static void ApplyProxyBodies(Ent e)
        {
            foreach (var it in e.Items)
            {
                if (it == null) continue;
                ProxyItems.Add(it.gameObject);
                foreach (var rb in it.GetComponentsInChildren<Rigidbody>(true))
                    if (!rb.isKinematic) { rb.isKinematic = true; e.MadeKinematic.Add(rb); }
            }
            foreach (var rb in e.Root.GetComponentsInChildren<Rigidbody>(true))
                if (!rb.isKinematic) { rb.isKinematic = true; e.MadeKinematic.Add(rb); }
            ProxyItems.Add(e.Root.gameObject);
            e.Wheels = null;
            var ws = WheelsOf(e);
            for (int i = 0; i < ws.Length; i++) { ProxyWheelOwner[ws[i]] = e; ProxyWheelIndex[ws[i]] = i; }
            foreach (var eng in e.Root.transform.root.GetComponentsInChildren<enginescript>(true)) ProxyEngineOwner[eng] = e;
        }

        // ---- the game streams items: far from every player they go into the save's far store and their GameObjects are
        // destroyed (itemPlaceRemoveScript); near again, PlaceOne makes new GameObjects with the same idInSave. A shared
        // object is therefore tracked by id: re-bound to the new object when it comes back (display-copy mode re-applied),
        // dormant while stored — states for it then update its far-store record, so it reappears where it is.
        private static void Bind(Ent e)
        {
            if (e.Root == null) return;
            e.RootId = e.Root.idInSave;
            e.ItemIds = new List<uint>();
            foreach (var it in e.Items) e.ItemIds.Add(it != null ? it.idInSave : 0u);   // keep positions: parts and state go by index
            if (!e.Proxy) AttachContact(e);   // simulated here: relay contact impulses (owner flips re-attach via SetProxy)
        }

        /// A single item of a group can be streamed out and placed back on its own (a part lying in the grass, a thing in
        /// the trunk) while the car stays: its slot then holds a destroyed object. Re-bind it by id like the root.
        private static bool RebindMembers(Ent e)
        {
            if (e.ItemIds == null || e.Items == null || savedatascript.s == null) return false;
            bool any = false;
            for (int i = 0; i < e.Items.Count && i < e.ItemIds.Count; i++)
            {
                if (e.Items[i] != null || e.ItemIds[i] == 0) continue;
                if (!savedatascript.s.items.TryGetValue(e.ItemIds[i], out var it) || it == null) continue;
                e.Items[i] = it; any = true; MembersRebound++;
                if (e.Proxy)
                {
                    ProxyItems.Add(it.gameObject);
                    foreach (var rb in it.GetComponentsInChildren<Rigidbody>(true))
                        if (!rb.isKinematic) { rb.isKinematic = true; e.MadeKinematic.Add(rb); }
                }
            }
            if (any) e.Wheels = null;
            return any;
        }

        private static bool Resolve(Ent e)
        {
            if (e.Root != null)
            {
                if (((Time.frameCount + (int)e.NetId) & 31) == 0) RebindMembers(e);
                return true;
            }
            if (e.RootId == 0 || savedatascript.s == null || !savedatascript.s.items.TryGetValue(e.RootId, out var root) || root == null) return false;
            e.Root = root;
            // same positions as before (parts are identified by their index in the group): missing ones stay empty
            var items = new List<tosaveitemscript>();
            if (e.ItemIds != null) foreach (var id in e.ItemIds) items.Add(id != 0 && savedatascript.s.items.TryGetValue(id, out var it) && it != null ? it : null);
            else items.Add(root);
            e.Items = items;
            e.Wheels = null;
            if (e.Proxy) { e.MadeKinematic.Clear(); ApplyProxyBodies(e); }
            Rebound++;
            return true;
        }

        public static long CopyMembersMissing, ShareMembersDropped;
        public static long MembersRebound, Rebound, FarRecordUpdates, FarRechunked, PartsAppliedStored;

        /// A state for a shared object that is in this machine's far store: move its record.
        private static bool IsStored(uint id)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            return data != null && data.itemData != null && savedatascript.IndexOfID(data.itemData.items, id, out _);
        }

        private static void UpdateFarRecord(Ent e, Vector3d pos, Quaternion rot)
        {
            e.FarPos = pos; e.FarRot = rot; e.FarDirty = true;   // written to the far store at most once a second
        }

        /// The group's stored records follow the owner's pose: the root's to the new pose, every stored part rigidly with
        /// it (they are records of their own, placed back by their own position — left behind, a car came back without
        /// them), each re-filed in the game's chunk index (PlaceStuff finds stored items by chunk: a record moved into
        /// another chunk but filed under the old one never comes back).
        private static void FlushFarRecords(Ent e)
        {
            e.FarDirty = false;
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null || e.RootId == 0) return;
            var list = data.itemData.items;
            if (!savedatascript.IndexOfID(list, e.RootId, out int ri)) return;
            var old = list[ri].transform;
            Vector3d oldPos = old.pos;
            Quaternion q = e.FarRot * Quaternion.Inverse(Quaternion.Euler(old.rot.Load()));
            MoveRecord(list, ri, e.FarPos, e.FarRot.eulerAngles);
            if (e.ItemIds != null)
                foreach (var id in e.ItemIds)
                {
                    if (id == 0 || id == e.RootId || !savedatascript.IndexOfID(list, id, out int k)) continue;
                    var t = list[k].transform;
                    Vector3d off = t.pos - oldPos;
                    Vector3 r = q * new Vector3((float)off.x, (float)off.y, (float)off.z);
                    MoveRecord(list, k, e.FarPos + new Vector3d(r.x, r.y, r.z), (q * Quaternion.Euler(t.rot.Load())).eulerAngles);
                }
            FarRecordUpdates++;
        }

        /// A stored item comes off what it was attached to: a detached item simply has no attachable record
        /// (save_attachable.NeedsSave = attached); its record moves to where it landed.
        private static bool DetachStored(uint id, Vector3d pos, Quaternion rot)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null || !savedatascript.IndexOfID(data.itemData.items, id, out int k)) return false;
            data.itemData.attachable.RemoveAll(a => a != null && a.id == id);
            MoveRecord(data.itemData.items, k, pos, rot.eulerAngles);
            return true;
        }

        private static void MoveRecord(List<save_item> list, int k, Vector3d pos, Vector3 euler)
        {
            var rec = list[k];
            var from = savedatascript.SnapChunkPos(rec.transform.pos);
            var to = savedatascript.SnapChunkPos(pos);
            rec.transform = new posRotd(pos, euler);
            if (from.x == to.x && from.y == to.y) return;
            var chunks = savedatascript.s.itemChunks;
            if (chunks.TryGetValue(from, out var ids)) ids.Remove(rec.id);
            savedatascript.s.AddToChunk(pos, rec.id);
            FarRechunked++;
        }

    }
}
