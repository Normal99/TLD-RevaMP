using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Teleport requests (TPA): a player far down the road asks to come to another one. In chat:
    ///   /tpa <name>   ask to come to that player (they have 60 s to answer)
    ///   /y  /n        accept or refuse the last request
    /// On accept the asker is placed on the ground next to them. Driving their own car, the car comes along â€” with what
    /// lies loose in it and anyone sitting in it â€” through the game's own teleport (fpscontroller.TeleportWithParent:
    /// the root a seated player is in moves), held still until the ground there is generated, then set down on it.
    /// The host chooses on its own card in the multiplayer screen: off, or on with a cooldown per asker (the host is
    /// never limited). Server side: MpServer.TpaCommand; this side: Tpa.Go.
    public static partial class Tpa
    {
        public static bool Enabled = true;
        public static bool FloorAware = true;   // A/B: false = before v0.69.2 (the lowest surface with room at the spot: a roof, the ground under a floor)
        public static int CooldownS = 300;
        public static readonly int[] Choices = { -1, 0, 60, 300, 900 };   // -1 = off; also used by Settings → Revamp
        public const float AnswerS = 60f;
        public const float AskGapS = 15f;   // between two requests of one player (asked, not accepted: CooldownS is that)
        public const byte GoMsg = 69;   // server â†’ client: f64 gx, f64 gy, f64 gz, f32 yaw â€” go there (accepted)
        public static long Asked, Accepted, Refused, Gone, CarsMoved, CarsSetDown, CarsGaveUp;
        public static string Last = "";
        internal static string CommandKind(string text)
        {
            if (text.Equals("/tpa", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/tpa ", StringComparison.OrdinalIgnoreCase)) return "tpa-request-sent";
            if (text.Equals("/y", StringComparison.OrdinalIgnoreCase) || text.Equals("/tpaccept", StringComparison.OrdinalIgnoreCase)) return "tpa-accept-sent";
            if (text.Equals("/n", StringComparison.OrdinalIgnoreCase) || text.Equals("/tpdeny", StringComparison.OrdinalIgnoreCase)) return "tpa-refuse-sent";
            return null;
        }

        public static string SettingText() => !Enabled ? "TPA: off" : CooldownS <= 0 ? "TPA: on, no wait" : $"TPA: on, every {CooldownS / 60} min";

        internal static void OnSettingsChanged(SessionSettingsSnapshot old, SessionSettingsSnapshot current)
        {
            if (old == null || current == null || Mp.Server == null) return;
            if (old.TpaEnabled && (!current.TpaEnabled || old.TpaCooldownS != current.TpaCooldownS)) Mp.Server.ClearTpaRequests();
        }

        /// Settings → Revamp's choice, and compatibility for test tools. The in-session panel edits its snapshot directly.
        public static void Cycle()
        {
            int cur = Enabled ? CooldownS : -1, i = Array.IndexOf(Choices, cur);
            int next = Choices[(i + 1) % Choices.Length];
            Enabled = next >= 0; if (next >= 0) CooldownS = next;
            RevampSettings.Save();   // remembered for the next time this player hosts (Settings â†’ Revamp â†’ Multiplayer)
        }

        // ------------------------------------------------------------------ the asker's side

        internal static void GoIn(NetReader r)
        {
            double gx = r.F64(), gy = r.F64(), gz = r.F64(); float yaw = r.F32();
            if (r.Bad || !Entities.Finite(gx) || !Entities.Finite(gy) || !Entities.Finite(gz)) { SessionLog.Event("tpa-failure", result: "invalid destination"); return; }
            Go(gx, gy, gz, yaw);
        }

        private sealed class Moving
        {
            public tosaveitemscript Car; public readonly List<Transform> Roots = new List<Transform>();
            public readonly List<(Rigidbody rb, bool kin)> Bodies = new List<(Rigidbody, bool)>();
            public bool PlayerApart; public float Until;
        }
        private static Moving _moving;
        private static Vector3d _arrivalTarget;
        private static double _arrivalAt, _arrivalStarted;
        private static bool _arrivalPending, _arrivalWaitingGround;
        private static double _worldUntil, _objectsAt;
        private static Vector3d _arrivedOffset;
        private static bool _arrivalGroundReady, _arrivedGroundReady;
        internal static bool WaitingWorld { get; private set; }
        internal static bool RefreshingObjects => _arrivalPending || _objectsAt > 0;
        internal static bool ObjectSeen(uint net) => !RefreshingObjects || _arrivalObjects.Contains(net);
        internal static void ResetTelemetry()
        {
            _arrivalPending = _arrivalWaitingGround = WaitingWorld = false; _arrivalAt = _objectsAt = 0;
            _arrivalObjects.Clear();
        }
        internal static void TelemetryGroundFinished(bool placed)
        {
            if (!_arrivalPending || !_arrivalWaitingGround) return;
            _arrivalWaitingGround = false; _arrivalAt = Time.realtimeSinceStartupAsDouble + .5;
            _arrivalGroundReady = placed;
            if (!placed) SessionLog.Event("tpa-failure", from: Entities.MyId, result: "no ground after 30 s");
        }

        /// Accepted: there we go â€” with our car if we sit in it. gy: the height of the player we go to.
        internal static void Go(double gx, double gy, double gz, float yaw)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) { SessionLog.Event("tpa-failure", result: "no local player"); return; }
            if (_moving != null) Release(_moving);
            Entities.BeginTpaRefresh();
            if (_arrivalPending) SessionLog.Event("tpa-failure", result: "arrival check superseded");
            _arrivalWaitingGround = false;
            _arrivalTarget = new Vector3d(gx, gy, gz); _arrivalPending = true; _arrivalAt = Time.realtimeSinceStartupAsDouble + .5;
            _worldUntil = Time.realtimeSinceStartupAsDouble + 30;
            _arrivalStarted = Time.realtimeSinceStartupAsDouble;
            WaitingWorld = true; _objectsAt = 0; _arrivalObjects.Clear();
            _arrivalGroundReady = false;
            Gone++;
            SessionLog.Event("tpa-applied", from: Entities.MyId);
            var car = SeatCar(pl);
            if (car != null && Entities.RootNetOf(car, out _)) { CarTo(pl, car, gx, gy, gz); return; }
            if (pl.Bsitting) { try { pl.GetOut(pl.transform.position, true); } catch { } }   // someone else's car: on foot
            // on the floor they stand on (gy: their body's position, at or over their feet), not the lowest surface with
            // room at the spot: in the spawn house that was its roof (tpatest: asker 3.6 m over the host), under a raised
            // floor the ground beneath it
            if (FloorAware) DebugBridge.PlaceTpa(gx, gz, gy);
            else DebugBridge.PlaceAt(gx, gz);
            _arrivalWaitingGround = DebugBridge.TpBusy;
            _arrivalGroundReady = !_arrivalWaitingGround;
            Last = "placed on foot";
        }

        private static tosaveitemscript SeatCar(fpscontroller pl)
        {
            if (!pl.Bsitting || pl.seat == null) return null;
            var t = pl.seat.tosave != null ? pl.seat.tosave : pl.seat.GetComponentInParent<tosaveitemscript>();
            if (t == null) return null;
            var root = t.transform.root.GetComponent<tosaveitemscript>();
            if (root != null) t = root;
            return t.car != null ? t : null;
        }

        private static void CarTo(fpscontroller pl, tosaveitemscript car, double gx, double gy, double gz)
        {
            if (_moving != null) Release(_moving);
            var m = new Moving { Car = car, Until = Time.realtimeSinceStartup + 30f };
            m.Roots.Add(car.transform.root);
            foreach (var it in ItemSnapshot.Group(car))
                if (it != null && !m.Roots.Contains(it.transform.root)) m.Roots.Add(it.transform.root);   // parts on bodies of their own (a bus's rear)
            // what lies loose in it: anything with a body inside the car's box, turned with the car (a world-aligned box
            // around a car standing at an angle also takes in what stands next to it)
            var frame = car.transform.root;
            if (LocalBounds(m.Roots, frame, out var lb))
                foreach (var col in Physics.OverlapBox(frame.TransformPoint(lb.center), Vector3.Scale(lb.extents, Abs(frame.lossyScale)) + Vector3.one * 0.2f,
                                                       frame.rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    var rb = col.attachedRigidbody;
                    if (rb == null) continue;
                    var r = rb.transform.root;
                    if (m.Roots.Contains(r) || r == pl.transform.root || r.GetComponentInChildren<tosaveitemscript>() == null) continue;
                    m.Roots.Add(r);
                }
            foreach (var r in m.Roots)
                foreach (var rb in r.GetComponentsInChildren<Rigidbody>())
                {
                    m.Bodies.Add((rb, rb.isKinematic));
                    if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                    rb.isKinematic = true;
                }
            m.PlayerApart = pl.transform.root != car.transform.root;
            // just above where the other player stands: held there until the ground under it exists, then set down
            var to = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy + 4.0, gz));
            var delta = to - car.transform.position;
            Shift(m, pl, delta);
            DebugBridge.TpSettle(pl);
            _moving = m;
            CarsMoved++;
            Last = $"car moved with {m.Roots.Count - 1} more things, waiting for the ground";
        }

        private static void Shift(Moving m, fpscontroller pl, Vector3 delta)
        {
            foreach (var r in m.Roots) if (r != null) r.position += delta;
            if (m.PlayerApart && pl != null) pl.transform.position += delta;
        }

        /// The box around the roots' solid colliders in `frame`'s own axes.
        private static bool LocalBounds(List<Transform> roots, Transform frame, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var r in roots)
                foreach (var c in r.GetComponentsInChildren<Collider>())
                {
                    if (c.isTrigger || !c.enabled) continue;
                    var wb = c.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3((i & 1) == 0 ? wb.min.x : wb.max.x, (i & 2) == 0 ? wb.min.y : wb.max.y, (i & 4) == 0 ? wb.min.z : wb.max.z);
                        var p = frame.InverseTransformPoint(corner);
                        if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                    }
                }
            return any;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// Per frame: a moved car waits for the ground, then is set down on it.
        public static void Tick()
        {
            Mp.Server?.TpaTelemetryTick();
            if (WaitingWorld && (DebugBridge.TpaWorldReady(_arrivalTarget.x, _arrivalTarget.y, _arrivalTarget.z) || Time.realtimeSinceStartupAsDouble >= _worldUntil))
                WaitingWorld = false;
            Entities.DrainTpaInbox();
            if (_arrivalPending && _moving == null && (mainscript.s == null || mainscript.s.player == null))
            { SessionLog.Event("tpa-failure", result: "player lost before arrival"); _arrivalPending = _arrivalWaitingGround = false; }
            if (_arrivalPending && _arrivalWaitingGround && !DebugBridge.TpBusy) TelemetryGroundFinished(false);
            if (_arrivalPending && !_arrivalWaitingGround && _moving == null && Time.realtimeSinceStartupAsDouble >= _arrivalAt)
            {
                var p = mainscript.s != null ? mainscript.s.player : null;
                if (p == null) { _arrivalPending = false; SessionLog.Event("tpa-failure", result: "player lost before arrival"); }
                else
                {
                    var offset = mainscript.GlobalFromUnityPos(p.transform.position) - _arrivalTarget;
                    bool higher = !p.Bsitting && FloorAware && offset.y < -.4 &&
                        TpaPlacement.HigherFloor(_arrivalTarget, p.transform.position);
                    if (!p.Bsitting && FloorAware && TpaArrival.Retry(_arrivalGroundReady, offset.x, offset.y, offset.z, Time.realtimeSinceStartupAsDouble - _arrivalStarted, _worldUntil - Time.realtimeSinceStartupAsDouble, higher))
                    {
                        DebugBridge.PlaceTpa(_arrivalTarget.x, _arrivalTarget.z, _arrivalTarget.y, true);
                        _arrivalWaitingGround = DebugBridge.TpBusy;
                        _arrivalAt = Time.realtimeSinceStartupAsDouble + .5;
                    }
                    else if (!p.Bsitting && FloorAware && offset.y < -.4 && !TpaArrival.Arrived(_arrivalGroundReady, offset.x, offset.y, offset.z) && Time.realtimeSinceStartupAsDouble < _worldUntil)
                        _arrivalAt = Time.realtimeSinceStartupAsDouble + .5;
                    else
                    {
                        _arrivalPending = false; _arrivedOffset = offset; _arrivedGroundReady = _arrivalGroundReady; _objectsAt = Time.realtimeSinceStartupAsDouble + 5;
                        if (!TpaArrival.Arrived(_arrivalGroundReady, offset.x, offset.y, offset.z)) SessionLog.Event("tpa-failure", value: (float)offset.magnitude, result: _arrivalGroundReady ? "outside landing area after ground wait" : "destination floor not ready");
                    }
                }
            }
            if (_objectsAt > 0 && Time.realtimeSinceStartupAsDouble >= _objectsAt && Entities.TpaInboxEmpty)
            {
                _objectsAt = 0; var offset = _arrivedOffset;
                SessionLog.Event("tpa-arrived", from: Entities.MyId, value: (float)offset.magnitude, count: _arrivalObjects.Count,
                    dx: (float)offset.x, dy: (float)offset.y, dz: (float)offset.z,
                    result: TpaArrival.Arrived(_arrivedGroundReady, offset.x, offset.y, offset.z) ? "within landing area; objects applied" : "arrival failed; objects applied");
            }
            var m = _moving;
            if (m == null) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (m.Car == null || pl == null) { SessionLog.Event("tpa-failure", result: "car or player lost"); Release(m); return; }
            if (WaitingWorld) return;
            if (!CarFloor(m, out var hit, out var delta))
            {
                if (Time.realtimeSinceStartup < m.Until) return;
                CarsGaveUp++; Last = "no ground after 30 s: still held safely";
                SessionLog.Event("tpa-failure", result: "no ground after 30 s");
                m.Until = float.PositiveInfinity;
                _arrivalPending = false; _arrivedGroundReady = false;
                _arrivedOffset = mainscript.GlobalFromUnityPos(pl.transform.position) - _arrivalTarget;
                _objectsAt = Time.realtimeSinceStartupAsDouble + 5;
                return;
            }
            Shift(m, pl, delta);
            CarsSetDown++; Last = "car set down on " + hit.collider.name;
            _arrivalGroundReady = true;
            Release(m);
        }

        internal static bool MovingRoot(Transform root) => _moving != null && _moving.Roots.Contains(root);

        internal static bool Holding(Rigidbody body)
        {
            if (_moving == null || body == null) return false;
            foreach (var b in _moving.Bodies) if (b.rb == body) return true;
            return false;
        }

        private static bool CarFloor(Moving m, out RaycastHit hit, out Vector3 delta)
        {
            hit = default; delta = default;
            var frame = m.Car.transform.root;
            if (!LocalBounds(m.Roots, frame, out var lb)) return false;
            var extents = Vector3.Scale(lb.extents, Abs(frame.lossyScale));
            var centreAt = frame.TransformPoint(lb.center);
            float bottomY = centreAt.y - Mathf.Abs(frame.right.y) * extents.x - Mathf.Abs(frame.up.y) * extents.y - Mathf.Abs(frame.forward.y) * extents.z;
            var c = m.Car.transform.position;
            float nearY = mainscript.UnityPosFromGlobal(_arrivalTarget).y;
            // Wider rings find a clear static floor if a parked car or steep slope blocks the pivot.
            for (int radius = 0; radius <= 24; radius++)
                for (int i = 0; i < (radius == 0 ? 1 : 16); i++)
                {
                    float angle = i * Mathf.PI / 8;
                    var offset = new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
                    foreach (float? below in new float?[] { nearY + .5f, null })
                    {
                        var floor = DebugBridge.StandHit(c.x + offset.x, c.z + offset.z, below);
                        if (!floor.HasValue || !TpaArrival.CarLanding(floor.Value.point.y, bottomY, out double shift)) continue;
                        var move = offset + Vector3.up * (float)shift;
                        var centre = centreAt + move;
                        bool clear = true;
                        foreach (var col in Physics.OverlapBox(centre, extents, frame.rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                            if (!m.Roots.Contains(col.transform.root) && col.transform.root != mainscript.s.player.transform.root) { clear = false; break; }
                        if (!clear) continue;
                        hit = floor.Value; delta = move; return true;
                    }
                }
            return false;
        }

        private static void Release(Moving m)
        {
            foreach (var (rb, kin) in m.Bodies)
                if (rb != null) { rb.isKinematic = kin; if (!kin) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } }
            _moving = null;
            _arrivalAt = Time.realtimeSinceStartupAsDouble + .5;
        }

        public static string Stats() => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"cooldownS\":" + CooldownS + ",\"asked\":" + Asked + ",\"accepted\":" + Accepted +
                                        ",\"refused\":" + Refused + ",\"gone\":" + Gone + ",\"carsMoved\":" + CarsMoved + ",\"carsSetDown\":" + CarsSetDown +
                                        ",\"carsGaveUp\":" + CarsGaveUp + ",\"moving\":" + (_moving != null ? "true" : "false") + ",\"last\":" + Json.Str(Last) + "}";
    }

    public sealed partial class MpServer
    {
        private sealed class TpaAsk { public int From; public float Until; }
        private readonly Dictionary<int, TpaAsk> _tpaAsks = new Dictionary<int, TpaAsk>();   // by the player asked
        private readonly Dictionary<ulong, float> _tpaLast = new Dictionary<ulong, float>(); // by the asker's Steam ID
        private readonly Dictionary<ulong, float> _tpaAskedAt = new Dictionary<ulong, float>(); // last request, by Steam ID
        private readonly int[] _tpaExpired = new int[128];
        internal void ClearTpaRequests()
        {
            _tpaAsks.Clear(); _tpaLast.Clear(); _tpaAskedAt.Clear();
        }
        private float _tpaTelemetryAt;
        internal void TpaTelemetryTick()
        {
            Entities.ServerTpaTick();
            if (Time.realtimeSinceStartup < _tpaTelemetryAt) return;
            _tpaTelemetryAt = Time.realtimeSinceStartup + 1;
            int n = 0;
            foreach (var pair in _tpaAsks)
                if ((Time.realtimeSinceStartup > pair.Value.Until || !Present(pair.Value.From) || !Present(pair.Key)) && n < _tpaExpired.Length)
                {
                    SessionLog.Event("tpa-failure", from: pair.Value.From, to: pair.Key, result: !Present(pair.Key) ? "target left" : Present(pair.Value.From) ? "request expired" : "requester left");
                    _tpaExpired[n++] = pair.Key;
                }
            for (int i = 0; i < n; i++) _tpaAsks.Remove(_tpaExpired[i]);
        }

        /// The request waiting for this player, if it is still good (not timed out, the asker still here).
        private bool PendingAsk(int to, float now, out TpaAsk ask)
        {
            if (_tpaAsks.TryGetValue(to, out ask) && now <= ask.Until && Present(ask.From)) return true;
            if (ask != null) SessionLog.Event("tpa-failure", from: ask.From, to: to, result: now > ask.Until ? "request expired" : "requester left");
            _tpaAsks.Remove(to);
            return false;
        }

        /// A line starting with "/" from a player (0 = the host): a command, never shown to the others.
        internal void TpaCommand(int from, string text)
        {
            var parts = text.Trim().Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1].Trim() : "";
            float now = Time.realtimeSinceStartup;
            switch (cmd)
            {
                case "/tpa":
                {
                    SessionLog.Event("tpa-request", from: from);
                    if (!Tpa.Enabled) { SessionLog.Event("tpa-failure", from: from, result: "disabled"); Tell(from, "Teleport requests are off in this game."); return; }
                    if (arg.Length == 0) { SessionLog.Event("tpa-failure", from: from, result: "missing target"); Tell(from, "Type /tpa and the name of the player to go to."); return; }
                    int to = FindPlayer(arg, from);
                    if (to == -2) { SessionLog.Event("tpa-failure", from: from, result: "ambiguous target"); Tell(from, "More than one player matches \"" + arg + "\"."); return; }
                    if (to < 0) { SessionLog.Event("tpa-failure", from: from, result: "target absent"); Tell(from, "No player named \"" + arg + "\" here."); return; }
                    ulong sid = TpaKey(from);
                    if (from != 0 && Tpa.CooldownS > 0 && _tpaLast.TryGetValue(sid, out float last) && now - last < Tpa.CooldownS)
                    { SessionLog.Event("tpa-failure", from: from, to: to, result: "cooldown"); Tell(from, $"You can teleport again in {Mathf.CeilToInt(Tpa.CooldownS - (now - last))} s."); return; }
                    // asking itself is limited too, or one player could flood another's chat with requests
                    if (from != 0 && _tpaAskedAt.TryGetValue(sid, out float asked) && now - asked < Tpa.AskGapS)
                    { SessionLog.Event("tpa-failure", from: from, to: to, result: "ask rate limit"); Tell(from, $"You can ask again in {Mathf.CeilToInt(Tpa.AskGapS - (now - asked))} s."); return; }
                    // one request at a time per player: a second one must not replace it (their /y would then let in
                    // whoever asked last, not the one they meant)
                    if (PendingAsk(to, now, out var waiting) && waiting.From != from)
                    { SessionLog.Event("tpa-failure", from: from, to: to, result: "target busy"); Tell(from, $"{NameOf(to)} already has a request waiting; try again in {Mathf.CeilToInt(waiting.Until - now)} s."); return; }
                    if (from != 0) _tpaAskedAt[sid] = now;
                    _tpaAsks[to] = new TpaAsk { From = from, Until = now + Tpa.AnswerS };
                    Tpa.Asked++;
                    SessionLog.Event("tpa-pending", from: from, to: to);
                    Tell(from, "Asked " + NameOf(to) + " to let you come.");
                    Tell(to, NameOf(from) + " wants to come to you. Type /y to accept or /n to refuse.");
                    return;
                }
                case "/y": case "/tpaccept":
                {
                    SessionLog.Event("tpa-accept-request", from: from);
                    if (!PendingAsk(from, now, out var ask)) { SessionLog.Event("tpa-failure", from: from, result: "no pending request"); Tell(from, "Nobody is waiting to come to you."); return; }
                    _tpaAsks.Remove(from);
                    if (!Tpa.Enabled) { SessionLog.Event("tpa-failure", from: from, result: "disabled"); Tell(from, "Teleport requests are off in this game."); return; }
                    if (!WhereIs(from, out var at, out float yaw, out bool targetSeated)) { SessionLog.Event("tpa-failure", from: ask.From, to: from, result: "target position unknown"); Tell(from, "Couldn't find where you are."); return; }
                    WhereIs(ask.From, out _, out _, out bool askerSeated);
                    // to their right, clear of them: farther when either of them is in a car
                    double side = askerSeated || targetSeated ? 4.0 + (askerSeated ? 5.0 : 0.0) + (targetSeated ? 3.0 : 0.0) : 1.25, r = yaw * Mathf.Deg2Rad;
                    double gx = at.x + Math.Cos(r) * side, gz = at.z - Math.Sin(r) * side;
                    if (ask.From == 0) Tpa.Go(gx, at.y, gz, yaw);
                    else if (ById(ask.From, out var p)) { Plausibility.Grace(p.Id); _w.Reset(); _w.U8(Tpa.GoMsg); _w.F64(gx); _w.F64(at.y); _w.F64(gz); _w.F32(yaw); if (!T.Send(p.Conn, _w, SteamTransport.SendReliable)) SessionLog.Event("tpa-failure", from: ask.From, to: from, result: "teleport send failed"); }
                    else SessionLog.Event("tpa-failure", from: ask.From, to: from, result: "requester left before send");
                    Entities.ServerTpaBegin(ask.From, new Vector3d(gx, at.y, gz));
                    _tpaLast[TpaKey(ask.From)] = now;
                    Tpa.Accepted++;
                    SessionLog.Event("tpa-accepted", from: ask.From, to: from);
                    Tell(ask.From, NameOf(from) + " accepted. On your way.");
                    Tell(from, NameOf(ask.From) + " is on the way.");
                    return;
                }
                case "/n": case "/tpdeny":
                {
                    SessionLog.Event("tpa-refuse-request", from: from);
                    if (!PendingAsk(from, now, out var ask)) { SessionLog.Event("tpa-failure", from: from, result: "no pending request"); Tell(from, "Nobody is waiting to come to you."); return; }
                    _tpaAsks.Remove(from);
                    Tpa.Refused++;
                    SessionLog.Event("tpa-refused", from: ask.From, to: from, result: "declined");
                    Tell(ask.From, NameOf(from) + " said no.");
                    Tell(from, "Refused.");
                    return;
                }
                default:
                    Tell(from, "Commands: /tpa <name> to ask to come to a player, /y to accept, /n to refuse.");
                    return;
            }
        }

        /// One player only (0 = the host): a notice line in their chat.
        private void Tell(int id, string text)
        {
            if (id == 0) { Session.Show(1, "", text); return; }
            if (!ById(id, out var p)) return;
            _w.Reset(); _w.U8(Protocol.Chat); _w.U8(1); _w.Str(""); _w.Str(text);
            T.Send(p.Conn, _w, SteamTransport.SendReliable);
        }

        private bool ById(int id, out Peer p)
        {
            foreach (var o in _peers.Values) if (o.Id == id && o.Ready) { p = o; return true; }
            p = null; return false;
        }

        private bool Present(int id) => id == 0 || ById(id, out _);
        private string NameOf(int id) => id == 0 ? Session.MyName() : ById(id, out var p) ? p.Name : "player " + id;
        // a player without a checked Steam ID gets a slot of their own, not the one every such player would share (0)
        private ulong TpaKey(int id) { ulong s = SteamOf(id); return s != 0 ? s : ulong.MaxValue - (ulong)(uint)id; }
        private ulong SteamOf(int id) => id == 0 ? Mp.MySteamId() : ById(id, out var p) ? p.SteamId : 0;

        /// -1 nobody, -2 more than one: the exact name first, then names starting with it (case ignored).
        private int FindPlayer(string name, int except)
        {
            var all = new List<(int id, string name)> { (0, Session.MyName()) };
            foreach (var o in _peers.Values) if (o.Ready) all.Add((o.Id, o.Name ?? ""));
            all.RemoveAll(x => x.id == except);
            foreach (var x in all) if (string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase)) return x.id;
            int found = -1;
            foreach (var x in all)
                if (x.name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { if (found >= 0) return -2; found = x.id; }
            return found;
        }

        private bool WhereIs(int id, out Vector3d at, out float yaw, out bool seated)
        {
            if (id == 0)
            {
                at = Mp.LocalGlobal(); yaw = Mp.LocalYaw();
                var pl = mainscript.s != null ? mainscript.s.player : null;
                seated = pl != null && pl.Bsitting;
                return pl != null && !DedicatedServer.Enabled;
            }
            if (ById(id, out var p) && p.HasPos) { at = p.Pos; yaw = p.Yaw; seated = p.SeatNet != 0; return true; }
            at = default; yaw = 0; seated = false; return false;
        }
    }
}
