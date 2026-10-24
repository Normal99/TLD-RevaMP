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
        public const byte RemoveItem = 35, CompactState = 98;

        public sealed class Ent
        {
            public Vector3d PlacedPos; public bool HasPlaced;                       // where our copy was last placed (Entities.Anomaly)
            public double LockOffSince;                                             // our copy stored in a container here, its owner's pose elsewhere since (CopyLockRelease)
            public bool ReleaseGhost; public float ReleaseClearSince = -1f;         // taken out of a container here: no collisions until clear of every car (CopyLockSafe)
            public uint NetId, Epoch;
            internal double TelemetryReceived;
            internal double TelemetryReceiptAt;
            internal double TelemetrySentAt;
            internal double TelemetryPhysicsAt;
            internal uint TelemetrySentEpoch;
            internal float TelemetryInterval;
            internal bool TelemetryPhysics;
            internal uint TelemetryEpoch;
            internal int TelemetrySnapFrame = -1;
            internal float TelemetrySnapDistance, TelemetryServoCorrection;
            internal Vector3 TelemetryVelocity;
            internal Vector3d TelemetryPosition;
            internal tosaveitemscript TelemetryRoot;
            internal int TelemetryCategory = 7;
            public int OwnerId;
            public tosaveitemscript Root;
            public List<tosaveitemscript> Items = new List<tosaveitemscript>();
            public bool Proxy;
            public float ScanTracedAt;   // diagnostics: the cargo scan's last trace line for it (Entities.ClaimTrace)
            public PoseInterpolator Ip;
            internal double RidingPreviousClock;
            public ushort SeqOut, SeqIn; public int Recv, WhyAwake, WhyPos, WhyRot, WhyStored;   // Recv: states this copy received (diagnostics)
            public float SendAcc;
            internal bool StateAudience;
            internal double InterestSentAt;
            public bool Driven;
            internal bool SentDriven;
            internal readonly byte[] SentState = new byte[1536];
            internal int SentStateLength;
            internal double LastStateAt;
            internal double LastPoseStamp;
            internal BandwidthRules.Tail QuietTail;
            internal PhysLockPose LockPose;
            internal bool PoseLocked;
            internal uint LockFrame, LockContainer, SentLockContainer;
            internal int LockItem = -1, LockMount = -1;
            internal mountStuff PoseLockMount;
            internal Vector3 LockLocalPos; internal Quaternion LockLocalRot;
            internal double ReceivedStateAt; internal uint ReceivedStateEpoch;
            internal byte[] SettledState, RabbitSnapshot;
            internal uint LastStateEpoch;
            internal bool SentMoving;
            internal bool ReleaseMoving;
            internal double CompactStamp; internal uint CompactEpoch; public Vector3d CompactPos;
            public bool Stored, SentStored;   // in the owner's inventory (state flag 2); what this owner last sent
            internal bool ReceivedAtRest;
            public bool Held, SentHeld;       // in the owner's player's hands (state flag 4); what this owner last sent
            internal bool HandPose, SentHandPose;
            internal uint HandEpoch; internal int HandOwner;
            internal Vector3 HandPos, HandFrom, SentHandPos;
            internal Quaternion HandRot = Quaternion.identity, HandFromRot = Quaternion.identity, SentHandRot = Quaternion.identity;
            internal double HandSince;
            internal Vector3d HandWorld; internal Quaternion HandWorldRot = Quaternion.identity;
            internal readonly HandItemGlide HandDraw = new HandItemGlide();
            public int WornBy = -1, WornSlot = -1; public Vector3 WornLp; public Quaternion WornLq = Quaternion.identity;   // copy: on this player's head (Entities.Worn)
            public tosaveitemscript WornOffFor;   // copy: the object whose collisions were switched off for that
            public int SentWornSlot = -1;     // owner: the slot last announced (-1 = not worn)
            public uint CarriedBy;            // copy: held by a player seated in this car — its states are in the car's frame (Entities.Carried)
            public Vector3d CarriedGlobal; public Quaternion CarriedGlobalRot;   // the last state's world pose (the car not here)
            public bool Spraying, SentSpraying; public int SprayRepeat;   // a spray can firing (state flag 8): copies / owner
            public bool HitchHasVelocity; public Vector3 HitchVelocity;
            public readonly HashSet<uint> HitchAnnounced = new HashSet<uint>();
            public int HitchAuthorityOwner = -1;
            public bool HitchWaiting; public float HitchUntil = -100f;
            public readonly List<Rigidbody> HitchBodies = new List<Rigidbody>();
            public float OwnerAt = -100f;     // realtime of the last owner change / cargo claim try (Entities.Cargo)
            public bool CargoGhost; public int CargoGhostTick;   // in our car's zone, not ours yet: no collisions here (Entities.Cargo)
            public float LetGoAt = -100f;     // realtime its owner's states went from held to not: a car here may take it at once (Entities.Cargo)
            public readonly List<Rigidbody> MadeKinematic = new List<Rigidbody>();
            public Rag Rag;   // a body of limbs (Entities.Ragdoll)
            public Vector3 LastVel;
            public Vector3d ShownPos;
            public Vector3d LastSentPos; public Quaternion LastSentRot = Quaternion.identity; public bool SentAtRest;
            public wheelgraphicsscript[] Wheels;           // car wheel graphics, hierarchy order (same prefab everywhere)
            public float[] WTravel, WSteer, WRpm, WSpin, WBrake, WMotor; public bool HasTorques;   // proxies: latest from the owner (+ spin integrated here)
            public bool EngRunning, EngStart, EngWasRunning, EngHasTemp; public float EngRpm, EngTemp, EngStarterAt = -999f;   // proxies: the owner's engine (+ local sound timing)
            public int SentSigKey = -1, SigRepeat;                    // owner: engine/signal flags last sent (CarSignalKey)
            public bool SigHas, SigIgnition, SigBrake, SigHandbrake; public int SigGear; public float SigHorn, SigSteer, SteerWheelShown;   // proxies: the owner's car signals (CarSignals)
            public float ResyncAcc; public bool ResyncPhased; public int ResyncCursor = -1; public ulong[] ResyncHashes;
            public Dictionary<int, float> EditHoldUntil;    // items we changed on this copy: sent to the owner
            public Vector3d FarPos; public Quaternion FarRot; public bool FarDirty; public float FarFlushAt;   // dormant: latest pose for the far store   // owner: per item, last record sent
            public uint RootId;                             // local idInSave: survives the game's far-item streaming
            public List<uint> ItemIds;
            public HashSet<int> Split;
            // physical copy (Entities.Physical): a real body near cars simulated here, steered along the owner's path
            public float StepSpeed, StepTurn; public bool StepExtrap;   // diagnostics: the copy body's last physics-step move (m/s), extrapolated
            public bool Physical, HasServoPrev, GhostOn, CrashTaking, MovedByBody, PartsColOff; public List<Collider> PartCols; public RigidbodyInterpolation OrigInterp; public Transform FreeHit; public RigidbodyInterpolation PhysInterp; public float FreeFrom = -10f, FreeUntil = -10f;
            public Vector3d ServoPrevPos; public Quaternion ServoPrevRot;
            public float RestOffSince = -1f;                                         // a physical copy off its owner's resting pose since (RestSnap)
            public readonly List<Rigidbody> JointBodies = new List<Rigidbody>();      // group members that came off: their own entity now — the group's resync skips them
            public uint ParentNet;          // a part that came off car ParentNet
            public int PartIndex = -1;
            // what a viewer sees: per-frame deviation from smooth motion (as VehicleLab), visible jump = > 20 cm
            public Vector3d P1, P2; public float Dt1; public int Frames, Jumps, ExtrapFrames; public float MaxDevCm;
            public readonly float[] ShownSpeed = new float[600]; public int ShownN;   // shown horizontal speed per frame (mp copymotion)
            public void Observe(Vector3d p, float dt)
            {
                if (dt <= 0f) return;
                if (Frames >= 1) { var dh = p - P1; ShownSpeed[ShownN++ % ShownSpeed.Length] = (float)Math.Sqrt(dh.x * dh.x + dh.z * dh.z) / dt; }
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

        public static long StateTailMisses;   // States whose tail StateTail could not size (they still go on)
        private sealed partial class SEnt
        {
            internal int TelemetryCategory = 7;
            /// Every owner change on the server goes through here: the one who had it may send the object's last state
            /// once (HandoverState, Entities.Resync) — nobody else, and not long after.
            public int OwnerId
            {
                get => _owner;
                set
                {
                    if (value == _owner) return;
                    if (_owner >= 0) { PrevOwnerId = _owner; PrevOwnerAt = Time.realtimeSinceStartup; HandoverOpen = true; }
                    _owner = value;
                }
            }
            private int _owner = -1;
            public int PrevOwnerId = -1; public float PrevOwnerAt = -100f; public bool HandoverOpen; public uint RabbitHandoverEpoch;
            public uint NetId;
            /// Every ownership path increments Epoch; release its old anchor before any replay can see it.
            public uint Epoch
            {
                get => _epoch;
                set { if (value == _epoch) return; _epoch = value; RagdollKeyState = null; RagdollKeyEpoch = 0; }
            }
            private uint _epoch;
            public byte[] Record; public bool HasState; public Vector3d Pos; public Quaternion Rot;
            public Vector3d Where;                                                   // for interest: last state, else the record's position
            public bool Driven; public float DrivenAt;                                // the owner's player sits in its driver seat
            public int ClaimedBy = -1; public float ClaimedAt = -100f;               // the last granted claim (Entities.Seat: DriverSeatGuard)
            public uint ClaimedFromEpoch;                                             // the epoch that claim replaced (ClaimEpochOk)
            public bool Stored;                                                       // in the owner's inventory
            internal bool AtRest;
            public bool Held;                                                         // in the owner's player's hands: nobody takes it over by touch
            public int WornBy = -1, WornSlot = -1; public Vector3 WornLp; public Quaternion WornLq;   // on its owner's head (Entities.Worn)
            public float Speed;                                                       // its last state's speed (m/s)
            public readonly Dictionary<int, byte[]> ItemState = new Dictionary<int, byte[]>(); // latest record per group index, for later joiners
            public uint ParentNet; public int PartIndex = -1;                         // a part that came off
            public readonly List<uint> Parts = new List<uint>();                     // parts that came off this car, in order
            public readonly HashSet<int> SplitOff = new HashSet<int>();             // kept even if the detached entity is later removed
            public int CrashReturnTo = -1; public float CrashUntil;                  // crash lease (Entities.CrashAuthority): owner to return to
            public bool Hitch; public uint HitchCar; public int HitchSlot;
            public byte[] LastAttach; public uint AttachParent;                    // its last AttachSync (until a DetachSync), for later joiners
            public HashSet<uint> AttachKids;                                         // objects whose last attach is onto this one
            public double CompactStamp; public uint CompactEpoch;
            internal double ReceivedStateAt; internal uint ReceivedStateEpoch;
            internal byte[] SettledState, RabbitSnapshot;
            public bool Rabbit, Car; public float RabbitHoldUntil;                   // what the record's root is (Entities.Rabbits)
            internal byte[] LatestState;
            internal uint AiSnapshotEpoch;
            internal readonly Dictionary<int, byte[]> AiSnapshots = new Dictionary<int, byte[]>();
            internal uint LockFrame, LockContainer;
            internal Vector3 LockLocalPos; internal Quaternion LockLocalRot;
            internal bool Locked;
            public HashSet<int> Gone;                                                // members destroyed on the owner's machine (Entities.MemberGone)
        }

        private static readonly Dictionary<uint, Ent> ByNet = new Dictionary<uint, Ent>();
        private static readonly Dictionary<uint, SEnt> Server = new Dictionary<uint, SEnt>();
        private static readonly Dictionary<uint, Ent> PendingShare = new Dictionary<uint, Ent>(); // local root id → ent
        private static uint _nextNet = 1;
        private static readonly NetWriter W = new NetWriter();
        private static readonly NetWriter WS = new NetWriter();
        private static float _driverCheck;
        public static long StatesSent, StatesIn, StatesStale, StatesNonFinite, Spawned, OwnerChanges;
        public static bool NewOwnerNewClock = true;   // A/B: false = before (a copy kept the old owner's interpolator)
        public static long OwnerClockResets;

        internal static bool IsHost => Mp.Server != null;
        internal static bool InSession => Mp.Server != null || (Mp.Client != null && Mp.Client.MyId > 0);
        public static int MyId => Mp.Server != null ? 0 : Mp.Client != null ? Mp.Client.MyId : -1;

        /// Session over. The host keeps the shared world: display copies become its real cars where the others left
        /// them, and the hidden local duplicates (stale versions of cars that moved on) are removed. A client goes back
        /// to its own world: the network copies disappear and its hidden cars come back where they were.
        public static void Reset(bool wasHost = true)
        {
            // a session that ends is a reconnect's other half: the VR state of the old player must not survive it
            // (the held grab, the open UI press, the latched stick, the seat). A read-only call: it changes no state
            // on the wire and touches no protocol field.
            try { Fixes.VrContractBridge.SanitizeReconnect(); } catch { }
            // the death's corpse and item ids belong to the session that made them
            DeathReset();
            Tpa.ResetTelemetry();
            ResetTpaSync();
            ItemTraffic.Reset();   // session ids start at 1 again; no old object's traffic belongs to the next one
            if (_standInterp) StandInterpOff(mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.RB : null);
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
            ProxyItems.Clear(); SignalCars.Clear(); _rideCars.Clear(); CargoGhostNets.Clear(); RagdollCopies.Clear(); RagdollLimbOwner.Clear(); _spraying.Clear();
            HostPhysicsLock = -1; _physLockLast = -1;   // a client: its own setting again
            StormReset();   // a client keeps the storms it has: its own from now
            ProxyWheelOwner.Clear(); ProxyWheelIndex.Clear(); ProxyEngineOwner.Clear();
            foreach (var kv in DisabledBuiltIn)
                foreach (var go in kv.Value) if (go != null) go.SetActive(true);
            DisabledBuiltIn.Clear();
            HitchReset(wasHost);
            ByNet.Clear(); Server.Clear(); PendingShare.Clear(); _nextNet = 1;
            // keyed by copies and network ids (which start at 1 again next session). (Known, the server's "sent to player N"
            // sets, needs nothing here: ServerOnJoin starts every joiner with an empty one - rehost.py)
            PhysicalCars.Clear(); _refetchedAt.Clear();
            ResetPoiCensus(); Leases.Clear(); LeaseTypes.Clear(); LeaseUntil.Clear(); LeaseAsked.Clear(); _leaseRetryAt.Clear(); PendingGrants.Clear(); _leasesDone.Clear(); _captureKnown = null; Captures.Clear();
            ShotgunReset();
            AiReset();
            RabbitReset();
            PoiUsablesReset(); LiveUsablesReset();
            InterestReset(); EditCheckReset();
            WornReset();
            _playerDeleted.Clear();
            ClockReset();
            _hitchOps.Clear(); _hitchTransfers.Clear(); _hitchCommands.Clear(); _hitchDeparted.Clear();
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
            RabbitBeforeShare(root);
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
            WriteShareWorn(W, root);   // [u8 slot, pose]: already on the local player's head (Entities.Worn)
            ToServer(W, true);
            return "{\"shared\":true,\"items\":" + items.Count + ",\"bytes\":" + rec.Length + "}";
        }

        private static void ToServer(NetWriter w, bool reliable)
        {
            if (w.Len >= 5 && (w.Buf[0] == Claim || w.Buf[0] == RemoveItem))
            {
                uint net = (uint)(w.Buf[1] | w.Buf[2] << 8 | w.Buf[3] << 16 | w.Buf[4] << 24);
                ByNet.TryGetValue(net, out var telemetryEntity);
                SessionLog.Event(w.Buf[0] == Claim ? "claim-sent" : "remove-sent", net, from: MyId, epoch: telemetryEntity != null ? telemetryEntity.Epoch : 0, count: w.Len, result: w.Buf[0] == Claim && w.Len >= 6 ? ClaimName((ClaimReason)w.Buf[5]) : "ok");
            }
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
            ShowCarriedCollisions(e);
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
        /// The car itself only: anything else under the car's root is in it, not the car — another player's lamp whose
        /// copy a container of our car held was claimed the moment we sat at the wheel, and taken from where its owner
        /// had left it (copyrelease.py, 2026-10-14).
        public static bool DriveClaimRootOnly = true;   // A/B: false = before v0.66.8
        private static void DriveClaimTick()
        {
            if (Time.unscaledTime - _lastDriveClaimTry < 1.5f) return;
            _lastDriveClaimTry = Time.unscaledTime;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null) return;
            if (!pl.seat.driverSeat0) return;   // passengers never claim — incl. shotgun (driverSeat1 = the front passenger)
            var root = pl.seat.transform.root;
            foreach (var e in ByNet.Values)
                if (e.Proxy && e.Root != null && e.Root.transform.root == root && (!DriveClaimRootOnly || e.Root.transform == root))
                {
                    W.Reset(); WriteClaim(W, e.NetId, ClaimReason.DriverRetry); ToServer(W, true);
                    ClaimsSent++;
                    return;
                }
        }

        public static void Tick()
        {
            CopyLodTick();
            if (!InSession) return;
            WatchTick();
            AnomalyTick();
            PoiCensusTick();
            float dt = Time.unscaledDeltaTime;
            HostClockTick();
            DriveClaimTick();
            ShotgunTick(dt);
            PushTick(dt);
            RadioTick(dt);
            KeyTick(); KeyTestTick();
            HitchTick();
            PhysLockTick(dt);
            PushGripTick();
            StormTick(dt);
            ServerTimeTick(dt);
            ServerInterestTick(dt);
            ServerHandoffTick(dt);
            ServerRabbitTick(dt);
            // E3: a finished sleep races the world clock on the sleeper's machine only — broadcast the jump
            bool sleepingNow = mainscript.s != null && mainscript.s.sleeping;
            if (_wasSleeping && !sleepingNow)
            {
                _wokeAt = Time.realtimeSinceStartup;
                _worldWakeStamp = ModObjects.ClockTime; _worldStamp = -1; _worldFollowing = false;
                _worldAwaitingWake = !IsHost && Fixes.SessionClocks.WorldEnabled;
                float wt = Fixes.SessionClocks.WorldEnabled ? WorldTimeNow() : mainscript.s != null ? mainscript.s.GetCurrentT() : -1f;
                if (wt >= 0f) { W.Reset(); W.U8(SleepSync); W.F32(wt); W.F64(ModObjects.ClockTime); ToServer(W, true); }
            }
            // while asleep, the clock goes out as it runs (the game turns it ~8 hours in a few seconds): the others' sky
            // followed only once the sleep was over, and the host's periodic clock pauses while it sleeps (playtest
            // 2026-10-18, "daytime sync takes longer for client")
            if (sleepingNow && SleepClockLive)
            {
                _sleepClockAcc += dt;
                if (_sleepClockAcc >= SleepClockS)
                {
                    _sleepClockAcc = 0f;
                    float wt = Fixes.SessionClocks.WorldEnabled ? WorldTimeNow() : mainscript.s.GetCurrentT();
                    // the same wire form as the wake-up one above: SleepSync carries the session stamp since protocol 34
                    if (wt >= 0f && Finite(wt)) { W.Reset(); W.U8(SleepSync); W.F32(wt); W.F64(ModObjects.ClockTime); ToServer(W, true); SleepClocksSent++; }
                }
            }
            else _sleepClockAcc = SleepClockS;   // the first one at once
            _wasSleeping = sleepingNow;
            double now = Time.realtimeSinceStartupAsDouble;
            _driverCheck += dt;
            if (_driverCheck > 0.5f) { _driverCheck = 0; CheckDriverSeat(); }
            LeaseTick();
            LiveUsableTick();
            EditTick();
            ReshareTick();
            BlastTick();
            MemberGoneTick();
            if (IsHost) ShareQuotaTick();
            AiTick();
            RabbitTick();
            WornTick();
            GentleTick();
            ClaimTraceTick();
            ResyncFrameTick(dt);
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null)   // ResyncFrameTick resolved the group; still in the far store: dormant
                {
                    if (e.Proxy && e.PoseLocked && e.ReceivedStateEpoch == e.Epoch && LockedWorld(e, out var lockedPos, out var lockedRot)) UpdateFarRecord(e, lockedPos, lockedRot);
                    if (e.FarDirty && Time.realtimeSinceStartup >= e.FarFlushAt) { e.FarFlushAt = Time.realtimeSinceStartup + 1f; FlushFarRecords(e); }
                    continue;
                }
                // bolted in: moves with its parent — after one last state if the last one said "in my hands" (held, then
                // bolted on: that state was the server's last word on it)
                // a copy stored in a container here while its owner has it elsewhere (every kind of copy: shown by its
                // interpolator, simulated, at rest, bolted on — before the skip below: a part bolted on in a container)
                bool wornRides = WornRides(e);   // on its wearer's head here: placed by WornLate
                TickPoseLock(e);
                if (e.Proxy && !e.PoseLocked && e.CarriedBy == 0 && !wornRides) CheckCopyLock(e, now);
                if (e.ReleaseGhost && (e.Proxy || GhostEndsOnOwn)) ReleaseGhostTick(e);
                bool attachedRides = RidesBolted(e) || AttachedCarPart(e);
                if (attachedRides && e.Proxy) continue;
                if (!e.Proxy)
                {
                    if (e.HitchWaiting) continue;
                    var mount = e.Root.P != null ? e.Root.P.physlock : null;
                    bool locked = mount != null;
                    uint lockFrame = LockedFrame(e, mount, RagdollFrame(e), out var lockPos, out var lockRot, out int lockItem, out int lockMount);
                    uint lockContainer = locked && lockFrame == 0 && lockPos.sqrMagnitude <= 1e4f ? WorldLockContainer(e) : 0;
                    bool lockChange = e.LockPose.Transition(locked, mount, lockFrame, e.Epoch) || lockContainer != e.SentLockContainer;
                    e.SendAcc += dt;
                    if (!lockChange && e.SendAcc < 1f / Protocol.StateHz) continue;
                    e.SendAcc = Mathf.Max(0f, e.SendAcc - 1f / Protocol.StateHz);
                    if (e.SendAcc > 1f / Protocol.StateHz) e.SendAcc = 0;
                    if (attachedRides && !lockChange && (!ClearHeldOnAttach || !(e.SentHeld || e.SentStored))) { StatesSkippedAttached++; continue; }
                    var rb = e.Root.GetComponent<Rigidbody>();
                    var t = RagdollFrame(e);
                    var g = mainscript.GlobalFromUnityPos(t.position);
                    var q = t.rotation;
                    bool stored = IsStored(e.Root), wStored = stored != e.SentStored;
                    bool held = IsHeld(e.Root), wHeld = held != e.SentHeld;
                    bool handPose = LocalHandPose(e, held, stored, locked, out var handPos, out var handRot);
                    bool wHand = handPose != e.SentHandPose || handPose && ((handPos - e.SentHandPos).sqrMagnitude > .001f * .001f || Quaternion.Angle(handRot, e.SentHandRot) > .1f);
                    bool spraying = IsSpraying(e.Root);
                    bool ground = CompactGround(e) && !held && !stored && !spraying;
                    bool compact = (CompactFlyer(e) || ground) && !held && !stored && !spraying;
                    if (compact) q = Quaternion.Euler(0, q.eulerAngles.y, 0);
                    // Resting suspension creep needs 2 cm to wake the stream; moving poses keep the 1 cm threshold.
                    bool wPos = BandwidthRules.PositionChanged((g - e.LastSentPos).sqrMagnitude, WasteFixes && e.Root.car != null && e.SentAtRest && !e.SentDriven);
                    bool wRot = Quaternion.Angle(q, e.LastSentRot) > BandwidthRules.RotationDeg;
                    Vector3d lockBaseline = lockFrame != 0 ? new Vector3d(lockPos.x, lockPos.y, lockPos.z) : g;
                    var lockBaselineRot = lockFrame != 0 ? lockRot : q;
                    if (locked) { wPos = e.LockPose.Moved(lockBaseline, lockBaselineRot); wRot = false; }
                    if (lockChange) { e.QuietTail = default; e.SentAtRest = false; }
                    if (spraying != e.SentSpraying) { e.SentSpraying = spraying; e.SprayRepeat = 3; }
                    bool wSpray = e.SprayRepeat > 0;
                    if (wSpray) e.SprayRepeat--;
                    int sig = CarSignalKey(e);
                    if (sig != e.SentSigKey) { e.SentSigKey = sig; e.SigRepeat = 3; }
                    bool wSig = e.SigRepeat > 0;
                    if (wSig) e.SigRepeat--;
                    bool limbs = RagdollMoved(e);
                    var poseBody = t == e.Root.transform ? rb : t.GetComponent<Rigidbody>();
                    bool wAwake = poseBody != null && !poseBody.isKinematic && !poseBody.IsSleeping() &&
                                  (poseBody.velocity.sqrMagnitude > RestSpeed * RestSpeed || poseBody.angularVelocity.sqrMagnitude > RestSpin * RestSpin);
                    bool corpseRoot = t != e.Root.transform && rb != null && rb.velocity.sqrMagnitude > RestSpeed * RestSpeed;
                    bool driven = IsLocalDriver(e.Root), wDriver = driven != e.SentDriven;
                    bool velocityMoving = poseBody != null && !poseBody.isKinematic && poseBody.velocity.sqrMagnitude > RestSpeed * RestSpeed;
                    bool moving = StateMoving(e, wPos || wRot, limbs, wAwake, ref velocityMoving, ground);
                    bool unlocked = e.LockPose.Locked && !locked;
                    bool released = !held && !stored && (e.SentHeld || e.SentStored || unlocked);
                    bool audience = OwnerAudience(g);
                    bool entered = audience && !e.StateAudience;
                    e.StateAudience = audience;
                    bool first = e.LastStateEpoch != e.Epoch || entered;
                    // Resume across release, ownership and unlock, including the sample before physics starts.
                    // Position/rotation above count even when the solver reports no velocity or sleeps.
                    ReleaseMotion(ref e.ReleaseMoving, released, held, stored, ref moving, first, unlocked);
                    bool control = lockChange || wStored || wHeld || wHand || wSig || wSpray || wDriver;
                    WS.Reset(); WriteWheels(WS, e); WriteEngine(WS, e); WriteSignals(WS, e);
                    bool visual = BandwidthRules.CarVisualChanged(e.SentState, e.SentStateLength, WS.Buf, WS.Len);
                    var quiet = BandwidthRules.StateQuiet(ref e.QuietTail, now, first || released || unlocked, wPos || wRot, limbs, visual, control, moving, e.SentAtRest,
                                                         locked, attachedRides, now - e.LastStateAt, driven, stored, e.ReleaseMoving);
                    if (WasteFixes && quiet != BandwidthRules.Quiet.None && quiet != BandwidthRules.Quiet.Final)
                    {
                        if (quiet == BandwidthRules.Quiet.Locked) StatesSkippedLocked++;
                        else if (e.Rag != null && e.Rag.SentPos != null) { StatesSkippedRagdoll++; if (corpseRoot) StatesSkippedCorpseRoot++; }
                        else { StatesSkippedPose++; if (corpseRoot) StatesSkippedCorpseRoot++; }
                        continue;
                    }
                    if (wAwake) e.WhyAwake++; if (wPos) e.WhyPos++; if (wRot) e.WhyRot++; if (wStored) e.WhyStored++;
                    Buffer.BlockCopy(WS.Buf, 0, e.SentState, 0, WS.Len); e.SentStateLength = WS.Len;
                    bool final = FinalState(locked, lockFrame, WasteFixes, quiet, moving, driven);
                    e.SentAtRest = !moving || final; e.SentMoving = velocityMoving;
                    var v = poseBody != null && moving && !final ? poseBody.velocity : Vector3.zero;
                    // stored in a container (no body of its own): the velocity of the body it rides — with none, its copies
                    // lagged the car 5-6 m at 50 km/h and the release rule took them out of the car (takeover.py A)
                    if (poseBody == null && moving && !final && StoredRidesCar && e.Root.P != null && e.Root.P.physlock != null)
                    {
                        var carrier = t.parent != null ? t.parent.GetComponentInParent<Rigidbody>() : null;
                        if (carrier != null && !carrier.isKinematic) v = carrier.GetPointVelocity(t.position);
                    }
                    if (e.SeqOut > 0 && ((g - e.LastSentPos).sqrMagnitude > 250000.0 || v.sqrMagnitude > 250000f) && OwnerJumps++ < 30)
                        Plugin.Log.LogWarning($"owner jump: {e.Root.name} net {e.NetId} {System.Math.Sqrt((g - e.LastSentPos).sqrMagnitude):F0} m since the last state, v {v.magnitude:F0} m/s, " +
                                              $"at global {g.x:F0},{g.y:F0},{g.z:F0} parent {(t.parent != null ? t.parent.name : "none")} kin {(rb != null && rb.isKinematic)}");
                    // pose is from the last physics step: its time on the frame-start clock. `now` (read here, partway
                    // through the frame) was off by however long the frame had run so far — 0–15 ms, varying with
                    // load: at 16 m/s up to 24 cm of timing noise per state, every copy correcting at once (relay
                    // convoy, 16 players: 69 jumps, all copies in the same normal frame, interpolating; v0.57.85)
                    double stamp = Time.unscaledTimeAsDouble - (Time.timeAsDouble - Time.fixedTimeAsDouble);
                    // A lock/unlock can happen in the same physics step as the previous pose. The receiver orders ms.
                    stamp = PoseStamp(stamp, e.LastPoseStamp, first);
                    e.LastPoseStamp = stamp;
                    Vector3 lp = default; Quaternion lq = Quaternion.identity;
                    uint carriedBy = held ? CarriedBy(e, t, q, out lp, out lq) : 0;
                    if (locked && lockFrame != 0) { carriedBy = lockFrame; lp = lockPos; lq = lockRot; }
                    else if (lockContainer != 0) { lp = lockPos; lq = lockRot; }
                    WS.Reset();
                    bool ragKey = false;
                    if (compact) CompactPoseCodec.Write(WS, e.NetId, e.Epoch, (ushort)(e.SeqOut + 1), stamp, g.x, g.y, g.z, q.eulerAngles.y, moving && !final, final);
                    else
                    {
                        WriteStateHead(WS, e.NetId, e.Epoch, (ushort)(e.SeqOut + 1), stamp, g, q, v, driven, stored, held, spraying, carriedBy, lp, lq, final, locked, lockItem, lockMount, lockContainer, handPose, handPos, handRot);
                        WS.Bytes(e.SentState, 0, e.SentStateLength);
                        int ragAt = WS.Len; ragKey = WriteRagdoll(WS, e, first || final);
                        if (WS.Buf[ragAt] > 0) RagdollStatesSent++;
                    }
                    e.SentStored = stored; e.SentHeld = held;
                    e.SentHandPose = handPose; e.SentHandPos = handPos; e.SentHandRot = handRot;
                    if (lockChange && (locked || e.LockPose.Locked))
                        SessionLog.Event(locked ? "physlock-lock" : "physlock-unlock", e.NetId, lockFrame, from: MyId, epoch: e.Epoch);
                    e.LockPose.Sent(locked, mount, lockFrame, e.Epoch, lockBaseline, lockBaselineRot);
                    e.SentLockContainer = lockContainer;
                    e.LastStateAt = now; e.LastStateEpoch = e.Epoch;
                    e.SentDriven = driven; e.SeqOut++;
                    bool poseSent = OwnerPoseDue(e, audience, first || final || lockChange || wStored || wHeld || wHand || wDriver, now);
                    if (poseSent)
                    {
                        TelemetrySentTeleport(e, g, v);
                        ToServer(WS, final || lockChange || wStored || wHeld || ragKey);   // a ragdoll Key: the Deltas after it need it
                        TelemetryPose(e, 0, stamp, g, q, v);
                        StatesSent++;
                    }
                    RagdollSent(e, poseSent);
                    e.LastSentPos = g; e.LastSentRot = q;
                }
                else if (HandManaged(e)) { }   // HandItemRender, after final hand targets and before IK
                else if (wornRides) { }
                else if (e.Physical || (e.MovedByBody && e.CarriedBy == 0))
                {
                    // moved by physics (FixedTick steers it, or moves the kinematic body): what the viewer sees is the
                    // interpolated body
                    e.Observe(mainscript.GlobalFromUnityPos(e.Root.transform.position), dt);
                }
                else if (RidesOwnersContainer(e)) StoredRides++;   // in its owner's car's container: it rides that car's copy
                else if (e.Ip != null && e.Ip.Ready && !(CopyLockByContainer && LockedAway(e)))   // held in a container here, its owner's far: it rides that
                {
                    EvalShown(e, now, dt, out var pos, out var rot);
                    e.ShownPos = pos;
                    e.Observe(pos, dt);
                    if (e.Ip.Extrapolating) e.ExtrapFrames++;
                    var t = e.Root.transform;
                    t.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                }
            }
            SprayTick(dt);
            RagdollTick(dt);   // after the copies moved: their limbs relative to where they are now
        }


        public static long OwnerJumps;

        private static bool IsLocalDriver(tosaveitemscript root)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null || !pl.seat.driverSeat0) return false;   // driverSeat1 is shotgun
            // the car itself, not what lies in it: a lamp stored on the back seat reported "driven" with every state while
            // the host sat at the wheel, and the server refused anyone's grab of it as a takeover (takeover.py B)
            return pl.seat.transform.root == root.transform.root && (!DrivenOnlyCar || root.transform == root.transform.root);
        }
        public static bool DrivenOnlyCar = true;   // A/B: false = everything in the driven car counts as driven (v0.68.1)

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
                        W.Reset(); WriteClaim(W, e.NetId, ClaimReason.Driver); ToServer(W, true);
                        ClaimsSent++;
                    }
                    else DriverSeatConflictTick(pl, e);   // our claim answered: someone else at this wheel (Entities.Seat)
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
                    if (r.Bad || n < 0 || n > r.Remaining) return;
                    if (!ShareAllowed(from, n)) return;   // Entities.ShareQuota
                    var rec = new byte[n]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, n); r.Pos += n;
                    var se = new SEnt { NetId = _nextNet++, OwnerId = from, Epoch = 1, Record = rec };
                    ShareFacts(rec, se);
                    ReadShareWorn(r, se, from);
                    Server[se.NetId] = se;
                    SessionLog.Event("spawn-add", se.NetId, from: from, epoch: se.Epoch, count: 1);
                    // the sharer learns its network id; everyone else gets it by distance (Entities.FarStore: interest)
                    W.Reset(); W.U8(Assigned); W.U32(localRoot); W.U32(se.NetId); W.U32(se.Epoch);
                    ServerSendTo(from, W, true);
                    KnownBy(from).Add(se.NetId);
                    Offer(se, from);
                    break;
                }
                case CompactState:
                case State:
                {
                    ReadEntityHead(r, type == CompactState, out uint net, out uint epoch, out double st, out var sp, out var sq, out var sv, out bool driven, out bool stored, out bool held, out _);
                    bool final = ReadFinalState;
                    if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from || se.Epoch != epoch) { StatesStale++; return; }
                    // a ragdoll Key: forwarded reliably and never thinned (the Deltas after it need it). A tail this
                    // scanner can't size is only "not a Key" - the state itself goes on as before
                    bool ragdollKey = false;
                    if (type == State)
                    {
                        if (StateTail.TryRead(r.Buf, 0, r.End, out var tail)) ragdollKey = tail.Mode == StateTail.Mode.Key;
                        else if (StateTailMisses++ < 3) Plugin.Log.LogWarning($"[anomaly] state tail unreadable: net {net} from player {from}, {r.End} bytes");
                    }
                    if (type == CompactState)
                    {
                        if (se.CompactEpoch != epoch) { se.CompactStamp = 0; se.CompactEpoch = epoch; }
                        if (st <= se.CompactStamp) { StatesStale++; return; }
                        sv = CompactVelocity(ReadCompactMoving, sp, st, se.Pos, se.CompactStamp);
                        se.CompactStamp = st;
                    }
                    if (!NewerState(st, epoch, ref se.ReceivedStateAt, ref se.ReceivedStateEpoch))
                    {
                        // a ragdoll Key overtaken by a newer Delta: still the one the Deltas need - cached (if newer than the
                        // cached Key) and passed on reliably; the copies keep its Key only (KeepLateKey)
                        if (ragdollKey && ServerLateKey(se, r)) { r.Pos = 0; WS.Reset(); WS.Bytes(r.Buf, 0, r.End); if (from != 0) Deliver(WS); Mp.Server?.SendCarState(WS, from, net, se.Pos, true, true, false); }
                        StatesStale++; return;
                    }
                    // an owner whose physics blew up (NaN): keep the last good state — stored, it broke distances and the far
                    // store's chunk index; relayed, everyone's copy went with it
                    if (!Finite(sp) || !Finite(sv) || !Finite(sq)) { StatesNonFinite++; return; }
                    // the owner's word about its car: speeds and jumps it couldn't have made (Plausibility)
                    // (every root object: the host's own copy of a far car is stored out, so "is it a car" can't be asked here —
                    // plausibility.py C, 2026-10-15: the teleported car wasn't checked)
                    if (se.PartIndex < 0 && !Plausibility.CarState(from, RemotePlayers.NameOf(from) ?? ("player " + from), net, sp, sv.magnitude, se.Speed, stored, held)) return;
                    // never thinned away: ragdoll Keys, the final rest state, a driver change,
                    // going into / out of an inventory
                    bool lockChanged = se.Locked != ReadLockedState || se.LockFrame != (ReadLockedState ? ReadCarriedBy : 0) || se.LockContainer != ReadLockContainer;
                    bool force = final || ragdollKey || lockChanged || driven != se.Driven || stored != se.Stored || held != se.Held || !se.HasState;
                    bool reliable = final || ragdollKey || lockChanged || stored != se.Stored || held != se.Held;
                    se.Stored = stored; se.Held = held; se.Speed = sv.magnitude; se.AtRest = final || stored;
                    se.Pos = sp; se.Rot = sq; se.HasState = true; se.Where = sp;
                    se.LockFrame = ReadLockedState ? ReadCarriedBy : 0;
                    se.LockContainer = ReadLockContainer;
                    se.Locked = ReadLockedState;
                    se.LockLocalPos = ReadLocalPos; se.LockLocalRot = ReadLocalRot;
                    foreach (var child in Server.Values) if (child.LockFrame != 0) ServerLockedPose(child);
                    se.Driven = driven; se.DrivenAt = Time.realtimeSinceStartup;
                    if (se.PartIndex >= 0 && Server.TryGetValue(se.ParentNet, out var parentCar)) { } // (parts: position kept for later joiners)
                    // relay the same bytes to everyone else, by distance (MpServer.SendCarState); the host applies it locally
                    r.Pos = 0; int len = r.End;
                    WS.Reset(); WS.Bytes(r.Buf, 0, len);
                    se.SettledState = FinalSnapshot(r, final || stored || held);
                    CacheLatestPose(se, r);
                    if (from != 0) Deliver(WS);
                    Mp.Server?.SendCarState(WS, from, net, se.Pos, force, reliable, type == CompactState || sv.sqrMagnitude < 0.01f);
                    break;
                }
                case PartOff:
                {
                    ServerPartOff(from, r);
                    break;
                }
                case LeaseReq: ServerLeaseRequest(from, r); break;
                case LeaseDone: ServerLeaseDoneRequest(from, r); break;
                case Resync: ServerResync(from, r); break;
                case Edit: ServerEdit(from, r); break;
                case Protocol.LiveUsable: ServerLiveUsable(from, r); break;
                case IgnitionKey: ServerKey(from, r); break;
                case ShotgunIn: ServerShotgun(from, r); break;
                case PushIn: ServerPush(from, r); break;
                case PlayerCombat.PlayerDamage: PlayerCombat.ServerReceive(from, r); break;
                case ShotFx: ServerShotFx(from, r); break;
                case ExplodeReq: ServerExplodeReq(from, r); break;
                case ExplosionFx: ServerExplosionFx(from, r); break;
                case PlayerSound: ServerPlayerSound(from, r); break;
                case BreakHit: ServerBreakHit(from, r); break;
                case BreakFx: ServerFwdAll(from, r, true); break;
                case AiState:
                {
                    // the creature's owner's stream, like State: a machine that just lost it doesn't keep steering the copies
                    uint net = r.U32();
                    if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId != from) { AiStatesStale++; break; }
                    int item = (int)r.VarU32();
                    if (r.Bad || r.Remaining != 15) { AiStatesStale++; break; }
                    if (se.AiSnapshotEpoch != se.Epoch) { se.AiSnapshots.Clear(); se.AiSnapshotEpoch = se.Epoch; }
                    se.AiSnapshots[item] = FinalSnapshot(r, true);
                    RelayCreature(se, from, r);
                    break;
                }
                case AiSound: ServerFwdAll(from, r, true); break;
                case RabbitState:
                {
                    ServerRabbitState(from, r);
                    break;
                }
                case RabbitFx: ServerFwdAll(from, r, true); break;
                case PoiUsable: ServerPoiUsable(from, r); break;
                case RemoveItem: { uint net = r.U32(); if (!r.Bad) ServerRemoveItem(from, net); break; }
                case Refetch: { uint net = r.U32(); if (!r.Bad) ServerRefetch(from, net); break; }
                case SleepSync: { float wt = r.F32(); double st = r.F64(); if (!r.Bad && Finite(wt) && !double.IsNaN(st) && !double.IsInfinity(st)) ServerSleepSync(from, wt, st); break; }
                case ContactImpulse:
                {
                    uint net = r.U32();
                    var imp = new Vector3(r.F32(), r.F32(), r.F32());
                    var gp = new Vector3d(r.F64(), r.F64(), r.F64());
                    if (!r.Bad && Finite(imp) && Finite(gp)) ServerContactImpulse(from, net, imp, gp);
                    break;
                }
                case DetachReq: { uint net = r.U32(); int idx = (int)r.VarU32(); if (!r.Bad) ServerDetachReq(from, net, idx); break; }
                case AttachSync: ServerAttach(from, r); break;
                case AttachReq: ServerAttachReq(from, r); break;
                case HitchRequest: ServerHitchRequest(from, r); break;
                case HitchFrozen: ServerHitchFrozen(from, r); break;
                case HitchResult: ServerHitchResult(from, r); break;
                case CensusAsk: ServerCensusAsk(from, r); break;
                case PoiCensus: ServerPoiCensus(from, r); break;
                case Worn: ServerWorn(from, r); break;
                case MemberGone: ServerMemberGone(from, r); break;
                case CensusReply: ServerCensusReply(from, r); break;
                case DetachSync: ServerDetachSync(from, r); break;
                case CrashClaim: { uint net = r.U32(), partner = r.U32(); if (!r.Bad) ServerCrashClaim(from, net, partner); break; }
                case CrashRelease: { uint net = r.U32(); if (!r.Bad) ServerCrashRelease(from, net); break; }
                case HandoverState: ServerHandover(from, r); break;
                case Claim: ServerClaim(from, r); break;
            }
        }

        /// A refused claim: the claimer learns who owns it (it may already simulate it — PickupClaim takes provisional
        /// ownership); the owner hears the same, unchanged, which makes it send one fresh state (Owner: ours again). An
        /// item at rest in its owner's hands sends nothing more — the claimer's copy stayed where its own player had
        /// dropped it, 1.5 m from the owner's hands (contested.py A, v0.65.43).
        public static long RefusedCrash, RefusedHeld, RefusedStored, RefusedDriven;
        private static readonly Dictionary<uint, int> _refuseLogged = new Dictionary<uint, int>();
        private static void RefuseClaim(int from, SEnt se, string why, ClaimReason reason = 0)
        {
            SessionLog.Event("claim-refused", se.NetId, from: from, to: se.OwnerId, result: why, epoch: se.Epoch, count: (int)reason);
            ClaimsRefused++;
            if (why.StartsWith("crash")) RefusedCrash++; else if (why == "held") RefusedHeld++; else if (why == "stored") RefusedStored++; else if (why == "worn") WornRefused++; else RefusedDriven++;
            // playtest 2026-10-09: 90 refusals in 7 minutes on the host, "parts locked for both players" — which, and why
            _refuseLogged.TryGetValue(se.NetId, out int n);
            if (n < 3 && _refuseLogged.Count < 60)
            {
                _refuseLogged[se.NetId] = n + 1;
                string nm = ByNet.TryGetValue(se.NetId, out var be) && be.Root != null ? be.Root.name : "not loaded on the host";
                Plugin.Log.LogWarning($"claim refused: net {se.NetId} ({nm}{(se.ParentNet != 0 ? ", part " + se.PartIndex + " of net " + se.ParentNet : "")}) by player {from}, owner {se.OwnerId}: {why} (producer {reason})" +
                                      $" (held {se.Held}, stored {se.Stored}, driven {se.Driven} {(Time.realtimeSinceStartup - se.DrivenAt):F1} s ago, crash {se.CrashReturnTo})");
            }
            W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
            ServerSendTo(from, W, true);
            if (se.OwnerId == from) return;
            W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
            ServerSendTo(se.OwnerId, W, true);
        }

        private static void WriteAdd(NetWriter w, SEnt se)
        {
            ServerLockedPose(se);
            w.Reset(); w.U8(Add); w.U32(se.NetId); w.VarU32((uint)se.OwnerId); w.U32(se.Epoch); w.Bool(se.HasState);
            if (se.HasState) { w.F64(se.Pos.x); w.F64(se.Pos.y); w.F64(se.Pos.z); w.F32(se.Rot.x); w.F32(se.Rot.y); w.F32(se.Rot.z); w.F32(se.Rot.w); }
            w.VarU32((uint)se.Record.Length); w.Bytes(se.Record, 0, se.Record.Length);
        }

        private static void WritePartDetached(NetWriter w, SEnt part)
        {
            w.Reset(); w.U8(PartDetached); w.U32(part.ParentNet); w.VarU32((uint)part.PartIndex); w.U32(part.NetId); w.VarU32((uint)part.OwnerId); w.U32(part.Epoch);
            w.F64(part.Pos.x); w.F64(part.Pos.y); w.F64(part.Pos.z); w.F32(part.Rot.x); w.F32(part.Rot.y); w.F32(part.Rot.z); w.F32(part.Rot.w);
        }

        /// Sleep/load discontinuities shift the same quantity as vanilla sleep; periodic anchors use the follower.
        public static long WorldTimeJumps;
        private static float _wokeAt = -100f;
        public static long WorldTimeStaleIgnored;
        public static bool SleepClockLive = true;   // A/B: false = before v0.69.2 (the others' clock jumps when the sleep ends)
        public static float SleepClockS = 0.25f;
        public static long SleepClocksSent;
        private static float _sleepClockAcc;
        internal static void ApplyWorldTime(float wt, bool force)
        {
            var sw = napszakvaltakozas.s;
            if (sw == null || (mainscript.s != null && mainscript.s.sleeping)) return;   // our own sleep races the clock; it syncs when it ends
            // just woke: a clock sent before our jump reached the server (the periodic one, in flight) would undo the
            // sleep here for up to 10 s — the server's next clock has our jump in it
            if (!force && Time.realtimeSinceStartup - _wokeAt < 3f) { WorldTimeStaleIgnored++; return; }
            float len = sw.dt + sw.nt, diff = wt - (Fixes.SessionClocks.WorldEnabled ? WorldTimeNow() : sw.t);
            if (len > 0f) { if (diff > len * 0.5f) diff -= len; else if (diff < -len * 0.5f) diff += len; }
            if (!force && Mathf.Abs(diff) < 1f) return;   // within a second: leave it (no sky jitter)
            sw.tekeres += diff;
            _worldFollowing = false;
            WorldTimeJumps++;
        }

        private static float _timeSyncAcc;
        /// Small stamped anchors keep local frame clamps and pauses from accumulating into sky drift.
        private static void ServerTimeTick(float dt)
        {
            if (Mp.Server == null || napszakvaltakozas.s == null) return;
            _timeSyncAcc += dt;
            if (Fixes.SessionClocks.WorldEnabled ? ModObjects.ClockTime - _worldSentAt < 2 : _timeSyncAcc < 10f) return;
            _timeSyncAcc = 0f;
            if (mainscript.s != null && mainscript.s.sleeping) return;   // the host's sleep: its jump goes out when it ends
            if (Fixes.SessionClocks.WorldEnabled)
            {
                _worldSentAt = ModObjects.ClockTime;
                WriteWorldClock(false); ServerSendAll(W, false, 0); Fixes.SessionClocks.WorldSent++;
            }
            else
            {
                W.Reset(); W.U8(SleepSync); W.F32(napszakvaltakozas.s.t); W.F64(ModObjects.ClockTime);
                ServerSendAll(W, true, 0);
            }
        }

        public static void ServerOnJoin(int playerId)
        {
            if (napszakvaltakozas.s != null)
            {
                if (Fixes.SessionClocks.WorldEnabled) { WriteWorldClock(true); Fixes.SessionClocks.WorldSent++; }
                else { W.Reset(); W.U8(SleepSync); W.F32(napszakvaltakozas.s.t); W.F64(ModObjects.ClockTime); }
                ServerSendTo(playerId, W, true);
            }
            // the world's objects follow by distance as soon as their position is known (ServerInterestTick)
            Known[playerId] = new HashSet<uint>();
            _hitchDeparted.Remove(playerId);
            ServerRadioTo(playerId);
            ServerPoiUsablesTo(playerId);   // building doors/gates changed this session
        }

        /// A player left: their cars go to the host (it keeps simulating them from the last state).
        /// Leases whose holder confirmed the spawn finished (LeaseDone): safe to keep if the holder leaves, because
        /// everything it spawned was already shared. Undone leases are freed: their buildings can be spawned again.
        private static readonly HashSet<string> _leasesDone = new HashSet<string>();

        public static float SilentTakeoverS = 1.5f;   // Mp.Server.SilentTick; 0 = off (A/B: before v0.65.24)
        public static long SilentTakeovers;

        /// Server: a player went silent — what of theirs is moving (a car driving, an item flying) goes to the host, as
        /// when they leave. At rest, held or stored things stay theirs: their copies are right as they are.
        internal static int ServerTakeSilent(int playerId)
        {
            int n = 0;
            foreach (var se in Server.Values)
                if (se.HitchCar == 0 && se.OwnerId == playerId && se.HasState && !se.Held && !se.Stored && !ServerWornNow(se) && se.Speed > 0.5f)
                {
                    OwnerLog(se, 0, "player " + playerId + " silent");
                    se.OwnerId = 0; se.Epoch++; se.CrashReturnTo = -1;
                    W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32(0); W.U32(se.Epoch);
                    ServerSendAll(W, true, -1);
                    CascadeOwner(se);
                    n++; SilentTakeovers++;
                }
            return n;
        }

        public static void ServerOnLeave(int playerId)
        {
            _hitchDeparted.Add(playerId);
            ShareQuotaForget(playerId);
            Known.Remove(playerId);
            InterestForget(playerId); EditCheckForget(playerId);
            // a car on loan to another player's crash, due back to the leaver: it stays with the machine simulating it.
            // Returned when the lease ran out, it went to a player who was gone, unsimulated until the hand-off pass gave
            // it to the nearest player (≤ 1 s, or whenever someone comes within 100 m) - crashlease.py
            foreach (var se in Server.Values)
                if (se.CrashReturnTo == playerId && CrashLeaveKeep) { OwnerLog(se, se.OwnerId, "crash lease: player " + playerId + " left, kept"); se.CrashReturnTo = -1; CrashLeasesKept++; }
            foreach (var se in Server.Values)
                if (se.OwnerId == playerId && se.HitchCar == 0)
                {
                    OwnerLog(se, 0, "player " + playerId + " left");
                    se.OwnerId = 0; se.Epoch++;
                    W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32(0); W.U32(se.Epoch);
                    ServerSendAll(W, true, -1);
                    CascadeOwner(se);
                }
            foreach (var tr in new List<SEnt>(Server.Values))
                if (tr.OwnerId == playerId && tr.HitchCar != 0 && Server.TryGetValue(tr.HitchCar, out var tow)) BeginHitchTransfer(tr, tow.OwnerId);
            // the leaver's leases: finished ones stay (their content is shared, holder 0 = the server), unfinished
            // ones are freed so the building isn't stuck half-spawned with spawn flags on everywhere
            ServerLeaseLeave(playerId);
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
            bool sent = Mp.Server != null && Mp.Server.SendToPlayer(playerId, w, reliable);
            if (_tpaSending && !sent) _tpaSendFailed = true;
        }

        private static void ServerSendAll(NetWriter w, bool reliable, int except, Vector3d? near = null, double range = 0)
        {
            // the players first, then the host's own copy: what the host does on receiving it (an old owner's hand-over record,
            // HandoverState) goes out after it — delivered first, the record overtook the Owner message and the new owner
            // dropped it as stale (crashtest v0.65.10: every host → client hand-over late, epoch one behind). And the host's
            // handling may reuse the writer `w` is.
            Mp.Server?.SendToAll(w, reliable, except, near, range);
            if (except != 0) Deliver(w);
        }

        private static void Deliver(NetWriter w)
        {
            var r = new NetReader(); r.Load(w.Len); Buffer.BlockCopy(w.Buf, 0, r.Buf, 0, w.Len);
            ClientReceive(r.U8(), r);
        }

        // ------------------------------------------------------------------ every machine

        /// Shared items this machine got but made no copy of, and why (bridge `mp status`: addRejects).
        public static long AddRejects; public static string AddRejectLast = "";
        private static void AddReject(uint net, string why)
        {
            AddRejects++; AddRejectLast = net + ": " + why;
            if (AddRejects <= 30) Plugin.Log.LogWarning("no copy of net " + net + ": " + why);
        }

        public static void ClientReceive(byte type, NetReader r)
        {
            if (DeferTpa(type, r)) return;
            TpaCopyMessage(type, r);
            switch (type)
            {
                case Assigned:
                {
                    uint localRoot = r.U32(), net = r.U32(), epoch = r.U32();
                    if (!PendingShare.TryGetValue(localRoot, out var e)) return;
                    PendingShare.Remove(localRoot);
                    e.NetId = net; e.Epoch = epoch; e.OwnerId = MyId;
                    SessionLog.Event("add-assigned", net, to: MyId, epoch: epoch);
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
                    if (r.Bad || n < 0 || n > r.Remaining) { AddReject(net, r.Bad ? "bad" : "short"); return; }
                    if (TpaDuplicate(net)) return;
                    var rec = new byte[n]; Buffer.BlockCopy(r.Buf, r.Pos, rec, 0, n);
                    var d = RecordCodec.Decode(rec);
                    if (d == null || d.items.Count == 0) { AddReject(net, d == null ? "undecodable" : "empty"); return; }
                    JoinReceived(net, d.items[0].prefabID);
                    if (FarFromHere(hasState ? pos : d.items[0].transform.pos)) { AddFar(net, owner, epoch, d, hasState, pos, rot); Tpa.ObjectApplied(net); break; }
                    uint rootOld = d.items[0].id;
                    var prefabs = new List<int>(); foreach (var r0 in d.items) prefabs.Add(r0.prefabID);
                    var order = new List<uint>();
                    var map = ItemSnapshot.SpawnGroup(d, order);
                    if (!map.TryGetValue(rootOld, out var rootNew) || !savedatascript.s.items.TryGetValue(rootNew, out var root) || root == null)
                    { AddReject(net, "root did not spawn (" + PrefabName(prefabs[0]) + ")"); return; }
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
                    SessionLog.Event("add-applied", net, from: owner, epoch: epoch);
                    Tpa.ObjectApplied(net);
                    if (hasState) root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                    if (owner != MyId) SetProxy(e, true);
                    break;
                }
                case CompactState:
                case State:
                {
                    int msgAt = r.Pos - 1;   // the type byte
                    ReadEntityHead(r, type == CompactState, out uint net, out uint epoch, out double st, out var sp, out var sq, out var sv, out bool driven, out bool stored, out bool held, out bool spraying);
                    bool final = ReadFinalState;
                    Ent e = null;
                    if (r.Bad || !ByNet.TryGetValue(net, out e) || !e.Proxy || epoch != e.Epoch)
                    {
                        if (!r.Bad && e != null && type == State) KeepLateKey(r, msgAt, e, epoch);
                        StatesStale++; return;
                    }
                    if (type == CompactState)
                    {
                        if (e.CompactEpoch != epoch) { e.CompactStamp = 0; e.CompactEpoch = epoch; }
                        if (st <= e.CompactStamp) { StatesStale++; return; }
                        sv = CompactVelocity(ReadCompactMoving, sp, st, e.CompactPos, e.CompactStamp);
                        e.CompactPos = sp; e.CompactStamp = st;
                    }
                    if (!NewerState(st, epoch, ref e.ReceivedStateAt, ref e.ReceivedStateEpoch)) { if (type == State) KeepLateKey(r, msgAt, e, epoch); StatesStale++; return; }
                    e.SettledState = FinalSnapshot(r, final);
                    e.Recv++;
                    TakeHandPose(e, sp, sq);
                    var s = new PoseInterpolator.Sample { T = st, Pos = sp, Rot = sq, Vel = sv };
                    e.LockLocalPos = ReadLocalPos; e.LockLocalRot = ReadLocalRot;
                    ApplyPoseLock(e, ReadLockedState, ReadLockedState ? ReadCarriedBy : 0, ReadLockItem, ReadLockMount, ReadLockContainer);
                    if (TakeCarried(e, ref s, sp, sq, held, stored)) return;   // let go in our car: ours now
                    e.Driven = driven;
                    if (stored != e.Stored) ShowStored(e, stored);
                    e.ReceivedAtRest = ReadFinalState || stored;
                    bool letGo = e.Held && !held;
                    if (letGo) { e.LetGoAt = Time.realtimeSinceStartup; if (LetGoSeen++ < 20) Plugin.Log.LogInfo($"let go: {(e.Root != null ? e.Root.name : "?")} net {e.NetId} by player {e.OwnerId} (its owner since {Time.realtimeSinceStartup - e.OwnerAt:F2} s)"); }
                    if (held || held != e.Held) ShowHeld(e, held);   // every state while held: a copy placed again (streaming) gets it too
                    ShowSpraying(e, spraying);
                    if (type == State)
                    {
                        ReadWheels(r, e);
                        ReadEngine(r, e);
                        ReadSignals(r, e);
                        ReadRagdoll(r, e, sp, sq);
                    }
                    if (r.Bad) return;
                    if (HitchTrailer(e)) e.HitchWaiting = false;
                    e.LastVel = sv;   // the world velocity (a car-frame state's sample has the frame's own)
                    e.Ip?.Add(s, Time.realtimeSinceStartupAsDouble);
                    if (letGo) ClaimLetGoInCar(e, stored);   // let go in our car: ours from where it was let go, before the next physics step
                    TelemetryReceived(e, st, sp, sq, sv);
                    if (e.Root == null) UpdateFarRecord(e, sp, sq);   // records always use world space, including framed states
                    StatesIn++;
                    SessionLog.JoinStage("firstState");
                    break;
                }
                case PartDetached:
                {
                    uint carNet = r.U32(); int idx = (int)r.VarU32(); uint partNet = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    if (r.Bad) { PartRejects += "bad "; return; }
                    SessionLog.Event("part-detached-received", carNet, partNet, item: idx, from: owner, to: MyId, epoch: epoch);
                    if (!ByNet.TryGetValue(carNet, out var car)) { SessionLog.Event("part-off-refused", carNet, partNet, item: idx, to: MyId, result: "car absent"); PartRejects += "nocar" + carNet + " "; return; }
                    if (idx < 0 || idx >= car.Items.Count) { SessionLog.Event("part-off-refused", carNet, partNet, item: idx, to: MyId, result: "item index"); PartRejects += "idx" + idx + "/" + car.Items.Count + " "; return; }
                    if (ByNet.TryGetValue(partNet, out var had))
                    {
                        // the car fetched again (its copy was lost here) after this part came off: the part is still its own
                        // object here (KeepPartsOnRemove), and the car's new copy, made from the car's first record, has it
                        // bolted on a second time — that one goes, and the group's place is the part's again
                        if (KeepPartsOnRemove && had.Root != null && car.Items[idx] != null && car.Items[idx] != had.Root && (car.Split == null || !car.Split.Contains(idx)))
                        {
                            (car.Split ?? (car.Split = new HashSet<int>())).Add(idx);
                            var twin = car.Items[idx];
                            car.Items[idx] = had.Root;
                            if (car.ItemIds != null && idx < car.ItemIds.Count) car.ItemIds[idx] = had.Root.idInSave;
                            ProxyItems.Remove(twin.gameObject);
                            if (twin.attachable != null && twin.attachable.attached)
                            {
                                _applyingDetach = true;
                                try { Unmount(twin.attachable, true); } finally { _applyingDetach = false; }
                            }
                            SuppressDestroyEcho = true;
                            try { twin.JustDestroy(); } finally { SuppressDestroyEcho = false; }
                            PartsDeduped++;
                            SessionLog.Event("part-off-deduped", carNet, partNet, item: idx, to: MyId, result: "car copy had it bolted on twice");
                            break;
                        }
                        SessionLog.Event("part-off-refused", carNet, partNet, item: idx, to: MyId, result: "already exists"); PartRejects += "dup" + partNet + " "; return;
                    }
                    // from now on the part's state is its own entity's: a car resync (captured before the crash, or later
                    // by the car's owner, who may no longer simulate it) must not re-bolt or overwrite it
                    (car.Split ?? (car.Split = new HashSet<int>())).Add(idx);
                    var it = car.Items[idx];
                    LiveUsablesChanged(it);
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
                            JoinReceived(partNet, -1, true);
                            PartsApplied++; PartsAppliedStored++;
                            SessionLog.Event("part-off-applied", carNet, partNet, item: idx, to: MyId, epoch: epoch, result: "far store");
                            break;
                        }
                        SessionLog.Event("part-off-refused", carNet, partNet, item: idx, to: MyId, result: "part absent, not in far store");
                        PartRejects += "gone" + carNet + "/" + idx + (gid == 0 ? "(noid)" : savedatascript.s.items.ContainsKey(gid) ? "(dead)" : IsStored(gid) ? "(stored)" : "(missing)") + " ";
                        return;
                    }
                    var pe = new Ent { NetId = partNet, OwnerId = owner, Epoch = epoch, Root = it, ParentNet = carNet, PartIndex = idx };
                    pe.Items.Add(it);
                    ByNet[partNet] = pe;
                    JoinReceived(partNet, it.id, true);
                    Bind(pe);
                    if (owner == MyId) { PartsApplied++; SessionLog.Event("part-off-applied", carNet, partNet, item: idx, to: MyId, epoch: epoch, result: "owner already detached"); break; }   // the owner already detached it for real
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
                    HandOver(it);   // dismounted by this player: into the hand, as in single player
                    SessionLog.Event("part-off-applied", carNet, partNet, item: idx, to: MyId, epoch: epoch);
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
                    int ht = r.Remaining >= 4 ? r.I32() : int.MinValue; string hn = r.Remaining > 0 ? r.Str() : "";
                    if (key != null && !(r.Bad && ht == int.MinValue)) OnLeaseTaken(key, r.Bad ? int.MinValue : ht, hn ?? "");
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
                case Protocol.LiveUsable: ApplyLiveUsable(r); break;
                case IgnitionKey: ApplyKey(r); break;
                case ShotgunIn: ApplyShotgun(r); break;
                case PushIn: ApplyPush(r); break;
                case RadioSync: ApplyRadio(r); break;
                case PhysLockSync: ApplyPhysLock(r); break;
                case StormSync: ApplyStorms(r); break;
                case AttachSync: ApplyAttachSync(r); break;
                case AttachReq: ApplyAttachReq(r); break;
                case HitchCommand: ApplyHitchCommand(r); break;
                case HitchFreeze: ApplyHitchFreeze(r); break;
                case HitchFrozen: ApplyHitchFrozen(r); break;
                case HitchResult: ApplyHitchResult(r); break;
                case CensusAsk: ApplyCensusAsk(r); break;
                case CensusReply: ApplyCensusReply(r); break;
                case DetachSync: ApplyDetachSync(r); break;
                case PlayerCombat.PlayerDamage: PlayerCombat.ClientReceive(r); break;
                case ShotFx: ApplyShotFx(r); break;
                case ExplodeReq: ApplyExplodeReq(r); break;
                case ExplosionFx: ApplyExplosionFx(r); break;
                case PlayerSound: ApplyPlayerSound(r); break;
                case BreakHit: ApplyBreakHit(r); break;
                case BreakFx: ApplyBreakFx(r); break;
                case AiState: ApplyAiState(r); break;
                case AiSound: ApplyAiSound(r); break;
                case RabbitState: ApplyRabbitState(r); break;
                case RabbitFx: ApplyRabbitFx(r); break;
                case PoiUsable: ApplyPoiUsable(r); break;
                case RemoveItem: { uint net = r.U32(); if (!r.Bad) ApplyRemoveItem(net); break; }
                case MemberGone: ApplyMemberGone(r); break;
                case DetachReq:
                {
                    uint net = r.U32(); int idx = (int)r.VarU32();
                    if (!r.Bad) ApplyDetachReq(MyId, net, idx);
                    break;
                }
                case SleepSync:
                {
                    float wt = r.F32(); r.F64();
                    if (!r.Bad && Finite(wt)) ApplyWorldTime(wt, false);   // a sleeper's jump, or the server's periodic clock
                    break;
                }
                case WorldClock: ReceiveWorldClock(r); break;
                case ContactImpulse:
                {
                    uint net = r.U32();
                    Vector3 imp = new Vector3(r.F32(), r.F32(), r.F32());
                    Vector3d gp = new Vector3d(r.F64(), r.F64(), r.F64());
                    if (!r.Bad && Finite(imp) && Finite(gp)) ApplyContactImpulse(net, imp, gp);
                    break;
                }
                case HandoverState: ApplyHandover(r); break;
                case Worn: ApplyWorn(r); break;
                case Owner:
                {
                    uint net = r.U32(); int owner = (int)r.VarU32(); uint epoch = r.U32();
                    if (r.Bad || !ByNet.TryGetValue(net, out var e) || epoch < e.Epoch) return;
                    HitchBeforeOwner(e, owner, epoch);
                    // ours until now, someone else's from this epoch on: our last word on its state goes to them
                    if (!e.Proxy && e.OwnerId == MyId && owner != MyId && epoch != e.Epoch) { SendRabbitHandover(e, epoch); SendHandover(e, epoch); }
                    if (owner == MyId) { e.SentAtRest = false; e.SentWornSlot = -1; e.SentStateLength = 0; e.LastStateEpoch = 0; e.CompactStamp = 0; }   // ours (again, or confirmed after a refused claim): one fresh state out (and what it is worn as)
                    if (e.WornBy >= 0 && owner != e.WornBy) ClearWorn(e);   // someone else's now: off the head it was on
                    // Lost a grab race (both players picked it up at once; the server gave it to the other — it never takes
                    // an item out of its owner's hands): let go, the game's way, while the body is still ours to drop. Kept,
                    // our player held a copy the owner moves — in two hands, 1.5 m apart on the two screens (contested.py).
                    if (owner != MyId && e.Root != null)
                    {
                        var hp = mainscript.s != null ? mainscript.s.player : null;
                        var limbs = e.Root.ragdollPos != null ? e.Root.ragdollPos.ps : null;
                        if (hp != null && hp.pickedUp != null && (hp.pickedUp == e.Root.P || limbs != null && Array.IndexOf(limbs, hp.pickedUp) >= 0)) { hp.Drop(); GrabsLost++; }
                    }
                    SessionLog.Event("owner-applied", net, from: e.OwnerId, to: owner, epoch: epoch);
                    if (e.Epoch != epoch)
                        for (int i = 0; i < e.Items.Count; i++)
                            if (e.Split == null || !e.Split.Contains(i)) LiveUsablesChanged(e.Items[i]);
                    // a copy that stays one, someone else's now: their states go by their own clock (every owner stamps with
                    // its game's time), and the interpolator drops any sample older than the last one it has — the old
                    // owner's. A new owner whose game started later than the old one's was dropped state by state, the copy
                    // frozen where the old owner left it while everyone else saw it move (lobby3.py C: P2 -> a third player,
                    // the host's copy 134 m behind after 12 s, 1092 states in, none shown)
                    if (NewOwnerNewClock && e.Proxy && owner != MyId && owner != e.OwnerId && e.Ip != null) { e.Ip = new PoseInterpolator(); OwnerClockResets++; }
                    e.OwnerId = owner; e.HitchAuthorityOwner = owner; e.Epoch = epoch; OwnerChanges++; e.OwnerAt = Time.realtimeSinceStartup;
                    SetProxy(e, owner != MyId);
                    if (HitchTrailer(e) && e.HitchWaiting) HitchFreezeBodies(e);
                    // our car now: what it holds and carries comes with it (Entities.Cargo)
                    if (owner == MyId && e.Root != null && e.Root.car != null && e.PartIndex < 0) ContentsOnOwner += ClaimContents(e.Root.car, true);
                    break;
                }
            }
        }

        /// Proxy: every rigidbody of the group kinematic (display only), interpolator starts from where it is now.
        /// Owner again: bodies back to dynamic with the last known velocity.
        public static long GameFrozenReleased, RagdollLimbsRestored, GrabsLost;
        /// How long a car's copy coasts on its last velocity when its states stop (PoseInterpolator.MaxCoast); 0 = as
        /// any object (MaxExtrapolate).
        public static float CarCoastS = 1f;
        internal static void Coast(Ent e)
        {
            if (e.Ip == null) return;
            e.Ip.MaxCoast = CarCoastS > 0f && e.Root != null && e.Root.car != null && e.PartIndex < 0 ? CarCoastS : e.Ip.MaxExtrapolate;
        }
        private static void SetProxy(Ent e, bool proxy, bool keepMotion = false)
        {
            if (e.Proxy == proxy) return;
            if (!proxy && e.WornBy >= 0) ClearWorn(e);   // ours now: loose where it was shown (on the head), the game's again
            if (!proxy && e.PoseLocked) { EnsurePoseLock(e); RecoverWorldLock(e); }
            if (!proxy)
            {
                e.PoseLocked = false; e.LockFrame = e.LockContainer = 0; e.LockItem = e.LockMount = -1; e.PoseLockMount = null;
                e.LockLocalPos = default; e.LockLocalRot = default;
            }
            if (!proxy) { e.LockPose = default; e.QuietTail = default; e.SentAtRest = false; }
            if (!proxy && e.CarriedBy != 0) TakeOverCarried(e);
            if (!proxy && e.Stored) ShowStored(e, false);   // ours now: the game's own state rules its model again
            if (!proxy && e.Held) ShowHeld(e, false);
            if (!proxy && e.ReleaseGhost && GhostEndsOnOwn) EndGhostOnOwn(e);
            if (!proxy && e.CargoGhost) { e.CargoGhost = false; ShowCarriedCollisions(e); }
            if (!proxy) { e.HandPose = false; e.HandDraw.Reset(); }
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
                if (HitchTrailer(e)) ProxyItems.Remove(e.Root.attachable.gameObject);
                RagdollRelease(e);   // its limbs: the game's again (made dynamic below with the other bodies)
                if (e.Wheels != null) foreach (var w in e.Wheels) { ProxyWheelOwner.Remove(w); ProxyWheelIndex.Remove(w); }
                BindProxyEngine(e);
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
                // a ragdoll's limbs as the game has them (ragdollactivatescript.SetRagdol: kinematic unless limp). The copy was
                // made before the creature's Start set them kinematic, so they were "dynamic" in MadeKinematic: handed to this
                // machine alive, its limbs fell under their own physics and lay where the hand-off was while it ran on —
                // 72 m behind it, its head (the eye creatures see from) with them: it never saw another player (handoff run,
                // v0.65.12 diagnostics, mp aihead). Near the camera the animator hid it; culled, the body stayed behind.
                foreach (var rd in e.Root.transform.root.GetComponentsInChildren<ragdollactivatescript>(true))
                    if (rd.RBs != null)
                        foreach (var rb in rd.RBs)
                            if (rb != null && rb.isKinematic == rd.ragdoll) { rb.isKinematic = !rd.ragdoll; RagdollLimbsRestored++; }
                // bodies the GAME froze, not us: every item is placed frozen (itemPlaceRemoveScript.PlaceOne →
                // DistFreezeRB) and unfrozen by its once-a-second pass — which skips copies (NoDistUnfreezeOnCopies). Made
                // ours, such a body stayed immovable until that pass: up to a second of a kinematic lamp in the car it
                // now rode in (buglist2m step 5, physics lock 1: the new driver's start rammed it, damage 12.5).
                // The game's own unfreeze, now, for what it would unfreeze (within its distance; ground check included).
                var ips = itemPlaceRemoveScript.s;
                var plp = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero;
                foreach (var it in e.Items)
                {
                    if (it == null || it.RB == null || !it.RB.isKinematic || it.buried) continue;
                    if (ips != null && (it.transform.position - plp).sqrMagnitude > ips.itemUnFreezeDist * ips.itemUnFreezeDist) continue;
                    it.DistUnFreezeRB();
                    if (!it.RB.isKinematic) { GameFrozenReleased++; if (!keepMotion) it.RB.velocity = e.LastVel; }
                }
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
            RabbitRole(e, proxy);
            if (proxy && e.PoseLocked && e.ReceivedStateEpoch == e.Epoch) EnsurePoseLock(e);
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
            if (HitchTrailer(e))
            {
                ProxyItems.Add(e.Root.attachable.gameObject);
                foreach (var rb in HitchBodies(e)) if (!rb.isKinematic) { rb.isKinematic = true; e.MadeKinematic.Add(rb); }
            }
            e.Wheels = null;
            var ws = WheelsOf(e);
            for (int i = 0; i < ws.Length; i++) { ProxyWheelOwner[ws[i]] = e; ProxyWheelIndex[ws[i]] = i; }
            BindProxyEngine(e);
            if (e.Stored) ShowStored(e, true);
            if (e.Held) ShowHeld(e, true);
        }

        // ---- the game streams items: far from every player they go into the save's far store and their GameObjects are
        // destroyed (itemPlaceRemoveScript); near again, PlaceOne makes new GameObjects with the same idInSave. A shared
        // object is therefore tracked by id: re-bound to the new object when it comes back (display-copy mode re-applied),
        // dormant while stored — states for it then update its far-store record, so it reappears where it is.
        private static void Bind(Ent e)
        {
            e.HitchAuthorityOwner = e.OwnerId;
            if (e.Root == null) return;
            e.RootId = e.Root.idInSave;
            e.ItemIds = new List<uint>();
            foreach (var it in e.Items) e.ItemIds.Add(it != null ? it.idInSave : 0u);   // keep positions: parts and state go by index
            RebindPoseLocks(e);
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
            if (any) { e.Wheels = null; RebindPoseLocks(e); }
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
            // Placed: the live copy carries the pose from now on, and the game writes its record when it streams it out
            // again. A far pose still waiting for its flush was written over that record right after the next stream-out:
            // a car driven away came back at the place it was first seen, inside the range, and flickered in and out
            // (tools/meetagain.py D3, 2026-10-22: placed at -107,-312 while its owner had it 320 m away).
            e.FarDirty = false;
            if (e.HitchAuthorityOwner < 0) e.HitchAuthorityOwner = e.OwnerId;
            // same positions as before (parts are identified by their index in the group): missing ones stay empty
            var items = new List<tosaveitemscript>();
            if (e.ItemIds != null) foreach (var id in e.ItemIds) items.Add(id != 0 && savedatascript.s.items.TryGetValue(id, out var it) && it != null ? it : null);
            else items.Add(root);
            e.Items = items;
            e.Wheels = null;
            if (e.Proxy) { e.MadeKinematic.Clear(); ApplyProxyBodies(e); AnomalyPlaced(e); ClaimIfPlacedInOwnCar(e); }
            if (e.Proxy && StoreOutTrace > 0)
            {
                var gp = mainscript.GlobalFromUnityPos(root.transform.position);
                Plugin.Log.LogInfo($"[placed] {root.name} net {e.NetId} at {gp.x:F1},{gp.y:F1},{gp.z:F1}, far pose {e.FarPos.x:F1},{e.FarPos.z:F1}, frame {Time.frameCount}");
            }
            if (e.Proxy && e.SettledState != null && e.ReceivedStateEpoch == e.Epoch)
            {
                var r = new NetReader { Buf = e.SettledState, Pos = 1, End = e.SettledState.Length };
                ReadEntityHead(r, e.SettledState[0] == CompactState, out _, out _, out _, out var pos, out var rot, out _, out _, out _, out _, out _);
                if (e.CarriedBy == 0 && !e.Stored && !e.Held && !RidesBolted(e) && !AttachedCarPart(e) && !RidesOwnersContainer(e) && !LockedAway(e))
                    root.transform.SetPositionAndRotation(mainscript.UnityPosFromGlobal(pos), rot);
                if (e.SettledState[0] == State) { ReadWheels(r, e); ReadEngine(r, e); ReadSignals(r, e); ReadRagdoll(r, e, pos, rot); }
            }
            RestoreRabbitState(e);
            RebindPoseLocks(e);
            Rebound++;
            return true;
        }

        public static long CopyMembersMissing, ShareMembersDropped;
        public static long MembersRebound, Rebound, FarRecordUpdates, FarRechunked, PartsAppliedStored, PartsDeduped;
        public static bool FarMoveAttached = true;   // A/B: false = before v0.66.2 (meetagain.py)
        public static long FarAttachedMoved;

        /// A state for a shared object that is in this machine's far store: move its record.
        private static bool IsStored(uint id)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            return data != null && data.itemData != null && savedatascript.IndexOfID(data.itemData.items, id, out _);
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
            if (FarPartsByAttach && data.itemData.attachable != null) { MoveBoltedRecords(e, data.itemData, oldPos, q); FarRecordUpdates++; return; }
            if (e.ItemIds != null)
                foreach (var id in e.ItemIds)
                {
                    if (id == 0 || id == e.RootId || !savedatascript.IndexOfID(list, id, out int k)) continue;
                    var t = list[k].transform;
                    Vector3d off = t.pos - oldPos;
                    Vector3 r = q * new Vector3((float)off.x, (float)off.y, (float)off.z);
                    MoveRecord(list, k, e.FarPos + new Vector3d(r.x, r.y, r.z), (q * Quaternion.Euler(t.rot.Load())).eulerAngles);
                }
            // and everything bolted into the group that is an object of its own (a part that came off and went back on, a
            // wheel from another car, a hubcap on that wheel): records of their own too, with the group as their parent —
            // left where they were, the car came back without them (playtest 2026-10-09: "the wheels never right for the
            // other person looking at your car")
            if (FarMoveAttached && data.itemData.attachable != null)
            {
                var moved = new HashSet<uint>();
                moved.Add(e.RootId);
                if (e.ItemIds != null) foreach (var id in e.ItemIds) if (id != 0) moved.Add(id);
                bool more = true;
                for (int pass = 0; more && pass < 8; pass++)
                {
                    more = false;
                    foreach (var a in data.itemData.attachable)
                    {
                        if (a == null || a.attachType != 1 || moved.Contains(a.id) || !moved.Contains(a.parentid)) continue;
                        moved.Add(a.id); more = true;
                        if (!savedatascript.IndexOfID(list, a.id, out int k)) continue;
                        var t = list[k].transform;
                        Vector3d off = t.pos - oldPos;
                        Vector3 r = q * new Vector3((float)off.x, (float)off.y, (float)off.z);
                        MoveRecord(list, k, e.FarPos + new Vector3d(r.x, r.y, r.z), (q * Quaternion.Euler(t.rot.Load())).eulerAngles);
                        FarAttachedMoved++;
                    }
                }
            }
            FarRecordUpdates++;
        }

        /// The group's stored records as the game stores a car: what is bolted on (an attach record, through parts of
        /// parts) goes where the car goes; a member lying loose (it came off: its own object now) stays where it lies —
        /// before v0.68.1 every member moved rigidly with the car, a trunk taken off at one place travelled on with the
        /// car's record. A bolted part whose record is far from the car (left behind before: playtest 2026-10-16, the
        /// joiner's Car06 came back without wheels, doors, hood and engine — their records 1–3.5 km away, still bolted
        /// to it) is put back with the car: the game bolts it into its slot by index when it places it.
        public static bool FarPartsByAttach = true;   // A/B: false = before v0.68.1 (partsback.py --old)
        public static float FarStrayM = 15f;
        public static long FarStraysPulled;
        private static readonly FarBoltIndex<save_attachable> _boltIdx = new FarBoltIndex<save_attachable>(a => a != null && a.attachType == 1 ? a.parentid : 0);
        private static readonly Queue<uint> _boltQ = new Queue<uint>();
        private static void MoveBoltedRecords(Ent e, itemDataClass d, Vector3d oldPos, Quaternion q)
        {
            _boltIdx.Prepare(d.attachable, Time.frameCount);
            var list = d.items;
            var seen = new HashSet<uint> { e.RootId };
            _boltQ.Clear(); _boltQ.Enqueue(e.RootId);
            while (_boltQ.Count > 0)
            {
                if (!_boltIdx.TryGetValue(_boltQ.Dequeue(), out var kids)) continue;
                foreach (var a in kids)
                {
                    if (!seen.Add(a.id)) continue;
                    _boltQ.Enqueue(a.id);
                    if (!savedatascript.IndexOfID(list, a.id, out int k)) continue;
                    var t = list[k].transform;
                    Vector3d off = t.pos - oldPos;
                    if (off.magnitude > FarStrayM)
                    {
                        FarStraysPulled++;
                        if (FarStraysPulled <= 20) Plugin.Log.LogInfo($"far store: {PrefabName(list[k].prefabID)} ({a.id}) bolted to net {e.NetId} lay {off.magnitude:F0} m from it — moved back with it");
                        off = Vector3d.zero;
                    }
                    Vector3 r = q * new Vector3((float)off.x, (float)off.y, (float)off.z);
                    MoveRecord(list, k, e.FarPos + new Vector3d(r.x, r.y, r.z), (q * Quaternion.Euler(t.rot.Load())).eulerAngles);
                    if (!(e.ItemIds != null && e.ItemIds.Contains(a.id))) FarAttachedMoved++;
                }
            }
        }

        /// A stored item comes off what it was attached to: a detached item simply has no attachable record
        /// (save_attachable.NeedsSave = attached); its record moves to where it landed.
        private static bool DetachStored(uint id, Vector3d pos, Quaternion rot)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null || !savedatascript.IndexOfID(data.itemData.items, id, out int k)) return false;
            data.itemData.attachable.RemoveAll(a => a != null && a.id == id);
            _boltIdx.Invalidate();
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
