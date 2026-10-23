using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class SessionRules
    {
        /// A/B: record the accepted local death before the corpse starts moving and put the inventory down there on
        /// respawn. False reproduces the old behaviour: periodic records sample the moving death camera and Respawn
        /// drops from wherever the ragdoll ended up.
        public static bool DeathDropAnchor = true;

        /// The death state machine (DeathState.cs): the phase, the anchor, the record budget and every rejection
        /// reason. The hooks below only talk to the game; the rules live in the machine, so they can be tested
        /// without it. One object holds the phase AND the budget on purpose: when they were two, they could
        /// disagree and the death screen would either flood the record or hold it forever.
        internal static readonly DeathState Death = new DeathState();

        /// The id of the session the current anchor belongs to. The anchor is only usable inside that session: a
        /// death that never respawned (or a session that ended) must never drop the next death's things where the
        /// last one fell.
        /// It starts at 0, which is a valid session id: a death before any host or join still gets a usable
        /// anchor (the anchor's own session is the one it was captured in).
        internal static int DeathSession = 0;

        /// The id of the session this machine is in. It changes on every host, join, stop and rehost.
        private static int _sessionCounter;
        internal static int NextSessionId() { _sessionCounter++; return _sessionCounter; }

        internal static bool HasDeathAnchor => Death.HasAnchor;
        internal static Vector3d DeathAnchor => new Vector3d(Death.AnchorX, Death.AnchorY, Death.AnchorZ);
        internal static float DeathYaw => (float)Death.AnchorYaw;
        internal static double DeathAt => Death.AnchorAt;

        /// A new session (or a rehost): the death state and the record budget are gone with the old session. A
        /// stale anchor would otherwise drop the next death's things where the last one fell.
        internal static void DeathReset()
        {
            Death.Reset(Time.unscaledTimeAsDouble);
        }

        /// The anchor is usable only inside the session that captured it.
        internal static bool AnchorIsCurrent(int session) => session >= 0 && session == Death.AnchorSession;

        /// The death screen was given up without a respawn: the player left the session, the world was reloaded, or
        /// the death screen was dismissed some other way. The anchor is given up with it and the periodic record
        /// resumes. Without this the death screen held the record budget forever and the joiner's save never
        /// resumed.
        internal static void DeathAbandoned()
        {
            Death.DeathAbandoned(Time.unscaledTimeAsDouble);
        }

        /// The self-heal, called every frame by PlayerSave: if the machine says the player is alive again but the
        /// machine still holds a death screen, the death was given up somewhere the mod did not see (the game's own
        /// revive, a scene load, a join that was refused, the session's own teardown). The anchor is given up and the
        /// periodic record resumes, so the anchor can never outlive the death it belongs to.
        internal static void DeathSyncWithGame()
        {
            if (Death.Phase != DeathPhase.DeathScreen) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;                       // no player yet: the death screen is not ours to judge
            bool dead = pl.died || (mainscript.s != null && mainscript.s.died);
            if (dead) return;                             // still dead: the death screen is legitimately open
            // A fresh death is never abandoned by the heal: the game's own flags are set inside Death2, and a
            // death that was accepted a moment ago is a death in progress, not a death that was given up.
            if (Time.unscaledTimeAsDouble - Death.DiedAt < SelfHealAfterS) return;
            Death.DeathAbandoned(Time.unscaledTimeAsDouble);
        }

        /// How long after a death the self-heal may give it up. Long enough that a death in progress is never
        /// mistaken for a death that was given up, short enough that a real abandonment does not hold the record
        /// budget for long.
        internal const double SelfHealAfterS = 2.0;

        /// The public Death method is the last place where the accepted player pose is still the death point. It is
        /// private game code after that: Death2 creates and launches the ragdoll and Update moves the player transform
        /// to its head for the death screen.
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]
        private static class CaptureDeath
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, out bool __state)
            {
                __state = false;
                if (!InSession) { Death.Reject(DeathReject.NotInSession); return; }
                if (mainscript.s == null || __instance == null || __instance != mainscript.s.player) { Death.Reject(DeathReject.NotInSession); return; }
                // A duplicate Death (the game's own second call, or a test calling it twice) must not capture a second
                // anchor and must not send a second death record.
                if (__instance.died || (mainscript.s != null && mainscript.s.died)) { Death.Reject(DeathReject.AlreadyDead); return; }
                if (__instance.godModeTillGrounded) { Death.Reject(DeathReject.Immortal); return; }
                try { if (kaposztaleves.s != null && (kaposztaleves.s.settings.god || kaposztaleves.s.settings.fly)) { Death.Reject(DeathReject.Immortal); return; } } catch { }
                // A seated death is a death like any other (counted as SeatedDeaths) and keeps its anchor; the game
                // leaves the seat itself. The anchor is the GROUND under the vehicle, not the seat's position: the
                // seat is several metres above the ground and moves with the car, so a seat anchor would drop the
                // things in mid-air inside the vehicle.
                bool seated = __instance.Bsitting;
                bool anchor = DeathDropAnchor;
                Vector3d g = new Vector3d();
                double yaw = 0;
                if (anchor)
                {
                    try { g = DeathAnchorPoint(__instance, seated); } catch { Death.Reject(DeathReject.NotFinite); return; }
                    if (!Entities.Finite(g)) { Death.Reject(DeathReject.NotFinite); return; }
                    yaw = __instance.BodyRot != null ? __instance.BodyRot.eulerAngles.y : __instance.transform.eulerAngles.y;
                    if (double.IsNaN(yaw) || double.IsInfinity(yaw)) yaw = 0;
                }
                Death.Accept(g.x, g.y, g.z, yaw, Time.realtimeSinceStartupAsDouble, anchor, seated, DeathSession);
                // What the player carries now. The game may drop the held thing during the death itself (the body
                // flies off, and its hold check lets go once the thing is too far from it), long before Respawn.
                CarriedAtDeath.Clear();
                if (__instance.pickedUp != null) CarriedAtDeath.Add(__instance.pickedUp);
                if (__instance.inHandP != null && !CarriedAtDeath.Contains(__instance.inHandP)) CarriedAtDeath.Add(__instance.inHandP);
                if (__instance.inventory != null)
                    foreach (var p in __instance.inventory) if (p != null && !CarriedAtDeath.Contains(p)) CarriedAtDeath.Add(p);
                __state = true;
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(bool __state)
            {
                // mainscript.Died runs inside Death2, so this is the first complete dead snapshot. It also stops a
                // periodic save from storing a pose the following frames moved under the death camera.
                if (__state && mainscript.s != null && mainscript.s.died) PlayerSave.SendDeathRecord();
            }
        }

        /// The things the player carried when the death was accepted (held, in the right hand, stored).
        private static readonly System.Collections.Generic.List<pickupable> CarriedAtDeath = new System.Collections.Generic.List<pickupable>();

        /// Runner (Session.Tick): the things carried at death, watched for a few seconds after the respawn.
        internal static void WatchDeathDrops() => PutDropsAtDeath.WatchDrops();

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Respawn))]
        private static class PutDropsAtDeath
        {
            /// What the prefix managed to do. The postfix commits it only if the respawn really took: a respawn
            /// that failed keeps the death screen (and the anchor) alive.
            private static AnchorOutcome _pending = AnchorOutcome.None;
            /// The things the player carried at death (held, in the right hand, stored), the anchor in Unity
            /// coordinates, and whether the anchored respawn's own DropAll is still to come.
            private static readonly System.Collections.Generic.List<pickupable> _carried = new System.Collections.Generic.List<pickupable>();
            private static Vector3 _at;
            private static bool _releasing;
            /// The carry frames' own local poses before they were moved. The game never moves TpickedUpPos or TInv
            /// itself (they are fixed points on the player), so they are put back after the release.
            private static readonly System.Collections.Generic.List<(Transform t, Vector3 p, Quaternion r)> _frames = new System.Collections.Generic.List<(Transform, Vector3, Quaternion)>();

            private static Transform[] Frames(fpscontroller pl) => new[] { pl.TpickedUpPos, pl.TpickedUpParent, pl.TpickedUpParent2, pl.TInv };

            /// Every carry frame and every carried thing at the anchor, half a metre up. Stored things hang off
            /// the inventory frame (TInv), which the respawn used to leave where it was: the game released them
            /// there, metres or tens of metres from the body (the katana 15.8 m away in the #31 test).
            private static void PlaceCarriedAtAnchor(fpscontroller pl)
            {
                var q = Quaternion.Euler(0f, DeathYaw, 0f);
                Vector3 up = _at + Vector3.up * 0.5f;
                foreach (var t in Frames(pl))
                    if (t != null) { t.position = up; t.rotation = q; }
                for (int i = 0; i < _carried.Count; i++) if (_carried[i] != null) _carried[i].transform.SetPositionAndRotation(Spot(i), q);
            }

            /// The i-th carried thing's place: half a metre above the anchor, a little apart from the others so they
            /// do not start inside each other (the game itself keeps every stored thing at the same point).
            private static Vector3 Spot(int i)
            {
                if (i == 0) return _at + Vector3.up * 0.5f;
                float a = i * 2.4f;
                return _at + new Vector3(Mathf.Cos(a) * (0.3f + 0.1f * i), 0.5f, Mathf.Sin(a) * (0.3f + 0.1f * i));
            }

            /// After the release: the drop keeps the thing's own carry velocity and may add a throw, so every
            /// thing carried at death is stopped. One the release still put more than 2 m from the anchor is put
            /// back there, a little apart from the others so they do not start inside each other.
            private static void SettleCarriedAtAnchor()
            {
                int moved = 0, count = 0;
                for (int i = 0; i < _carried.Count; i++)
                {
                    var p = _carried[i];
                    if (p == null) continue;
                    count++;
                    if ((p.transform.position - _at).magnitude > 2f) { p.transform.position = Spot(i); moved++; }
                    var rb = p.RB != null ? p.RB : p.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic) { rb.position = p.transform.position; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                    Plugin.Log.LogInfo("death anchor: released " + p.name + " " + (p.transform.position - _at).magnitude.ToString("F2") + " m from the anchor" +
                                       (p.transform.parent != null ? ", under " + p.transform.parent.name : "") + (rb == null ? ", no body" : rb.isKinematic ? ", kinematic" : ""));
                }
                if (moved > 0) Plugin.Log.LogInfo("death anchor: " + moved + " of " + count + " carried things released away from the anchor, put back");
                // They fall from half a metre up and may still bounce or slide off: watched until they rest.
                _watch.Clear(); _watch.AddRange(_carried);
                _watchAt = _at;
                _watchFixes = new int[_watch.Count];
                _watchFrom = Time.unscaledTime;
                _watchUntil = _watchFrom + WatchSeconds;
            }

            private const float WatchSeconds = 5f;
            /// How far (along the ground) from the anchor a thing the death let go of may lie and still be one of the
            /// things carried at death: a fall carries it some metres, not across the map.
            private const float DroppedInDeathRange = 60f;
            private const int WatchMaxFixes = 3;
            private static readonly System.Collections.Generic.List<pickupable> _watch = new System.Collections.Generic.List<pickupable>();
            private static Vector3 _watchAt;
            private static int[] _watchFixes = new int[0];
            private static float _watchFrom, _watchUntil, _watchFixedAt;

            /// Runner (Session.Tick): for a few seconds after the release, a thing carried at death that ended more
            /// than 2 m (along the ground) from the anchor is put back at its spot and stopped, at most three times
            /// each. The ground may be a slope, and a long thing landing on its end can tip a metre or two away.
            internal static void WatchDrops()
            {
                if (_watch.Count == 0) return;
                if (Time.unscaledTime > _watchUntil) { _watch.Clear(); return; }
                bool moving = false;
                for (int i = 0; i < _watch.Count; i++)
                {
                    var p = _watch[i];
                    if (p == null || p.transform.parent != null) continue;   // picked up again, or stored
                    Vector3 d = p.transform.position - _watchAt; d.y = 0f;
                    var rb = p.RB;
                    if (d.magnitude > 2f && _watchFixes[i] < WatchMaxFixes)
                    {
                        _watchFixes[i]++;
                        Vector3 back = _watchAt + Vector3.up * 0.3f;
                        if (i > 0) { float a = i * 2.4f; back += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (0.3f + 0.1f * i); }
                        p.transform.position = back;
                        if (rb != null && !rb.isKinematic) { rb.position = back; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                        _watchFixedAt = Time.unscaledTime;
                        Plugin.Log.LogInfo("death anchor: " + p.name + " ended " + d.magnitude.ToString("F2") + " m from the anchor, put back (" + _watchFixes[i] + ")");
                    }
                    if (rb != null && !rb.isKinematic && !rb.IsSleeping() && rb.velocity.sqrMagnitude > 0.01f) moving = true;
                }
                // At rest: done. Not in the first second and a half (they are still falling, or a physics step
                // has not run since they were stopped), nor within a second of a put-back.
                float now = Time.unscaledTime;
                if (!moving && now > _watchFrom + 1.5f && now > _watchFixedAt + 1f)
                {
                    var sb = new System.Text.StringBuilder("death anchor: at rest after " + (now - _watchFrom).ToString("F1") + " s:");
                    foreach (var p in _watch) if (p != null) { Vector3 d = p.transform.position - _watchAt; d.y = 0f; sb.Append(' ').Append(p.name).Append(' ').Append(d.magnitude.ToString("F2")).Append(" m"); }
                    Plugin.Log.LogInfo(sb.ToString());
                    _watch.Clear();
                }
            }

            /// Respawn's own DropAll, after Revive: the frames are put at the anchor once more, right before the
            /// release. Any other DropAll is the game's own and is left alone.
            [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.DropAll))]
            private static class AtTheRelease
            {
                [HarmonyLib.HarmonyPrefix]
                private static void Prefix(fpscontroller __instance)
                {
                    if (!_releasing || __instance == null || mainscript.s == null || __instance != mainscript.s.player) return;
                    _releasing = false;
                    try { PlaceCarriedAtAnchor(__instance); }
                    catch (System.Exception e) { Plugin.Log.LogWarning("death anchor: release placement failed: " + e.Message); }
                }
            }

            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance)
            {
                _pending = AnchorOutcome.None;
                _releasing = false;
                _carried.Clear();
                _frames.Clear();
                if (!InSession || !DeathDropAnchor || !Death.HasAnchor || !AnchorIsCurrent(DeathSession) ||
                    mainscript.s == null || __instance == null || __instance != mainscript.s.player)
                {
                    // No anchor to use: the drop falls back to where the body ended up. That is recorded, not
                    // claimed as an anchored drop.
                    _pending = AnchorOutcome.NoAnchor;
                    return;
                }
                Vector3 at;
                try { at = mainscript.UnityPosFromGlobal(DeathAnchor); } catch (System.Exception e)
                {
                    _pending = AnchorOutcome.NotRestorable;
                    Plugin.Log.LogWarning("death anchor: not restorable: " + e.Message);
                    return;
                }
                if (!Entities.Finite(at)) { _pending = AnchorOutcome.NotRestorable; return; }

                // Respawn runs Revive then DropAll. Put the player and every carry frame at the anchor first; the
                // original DropAll then performs the only item release. Revive resets the ragdoll in between, so
                // the frames are put there once more right before the release (PlaceCarriedAtAnchor).
                try
                {
                    __instance.Teleport(at, new Vector3(0f, DeathYaw, 0f));
                    if (__instance.RB != null) { __instance.RB.position = at; __instance.RB.velocity = Vector3.zero; __instance.RB.angularVelocity = Vector3.zero; }
                    _carried.Clear();
                    if (__instance.pickedUp != null) _carried.Add(__instance.pickedUp);
                    if (__instance.inHandP != null && !_carried.Contains(__instance.inHandP)) _carried.Add(__instance.inHandP);
                    if (__instance.inventory != null)
                        foreach (var p in __instance.inventory) if (p != null && !_carried.Contains(p)) _carried.Add(p);
                    // A thing the death itself let go of: still lying free (nobody picked it up or stored it since)
                    // and near, so it was let go during the fall. It goes to the anchor with the rest.
                    foreach (var p in CarriedAtDeath)
                    {
                        if (p == null || _carried.Contains(p)) continue;
                        Vector3 d = p.transform.position - at; d.y = 0f;
                        if (!p.pickedUp && p.transform.parent == null && d.magnitude <= DroppedInDeathRange) _carried.Add(p);
                        else Plugin.Log.LogInfo("death anchor: " + p.name + " carried at death is left where it is (" + d.magnitude.ToString("F1") + " m away" +
                                                (p.pickedUp ? ", picked up" : "") + (p.transform.parent != null ? ", under " + p.transform.parent.name : "") + ")");
                    }
                    CarriedAtDeath.Clear();
                    _at = at;
                    foreach (var t in Frames(__instance)) if (t != null) _frames.Add((t, t.localPosition, t.localRotation));
                    PlaceCarriedAtAnchor(__instance);
                    _releasing = true;
                    _pending = AnchorOutcome.Anchored;
                }
                catch (System.Exception e)
                {
                    _pending = AnchorOutcome.PlacementFailed;
                    Plugin.Log.LogWarning("death anchor: drop placement failed: " + e.Message);
                }
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(fpscontroller __instance, bool __runOriginal = true)
            {
                _releasing = false;
                if (__runOriginal && _pending == AnchorOutcome.Anchored) SettleCarriedAtAnchor();
                foreach (var f in _frames) if (f.t != null) { f.t.localPosition = f.p; f.t.localRotation = f.r; }
                _frames.Clear();
                _carried.Clear();
                if (!__runOriginal || __instance == null || mainscript.s == null || __instance != mainscript.s.player) return;
                // The respawn did not take: the player is still dead, so the death screen keeps holding the record
                // budget and the anchor survives for the respawn that will.
                if (__instance.died || (mainscript.s != null && mainscript.s.died))
                {
                    if (Death.Phase == DeathPhase.DeathScreen) Death.RespawnFailed();
                    _pending = AnchorOutcome.None;
                    return;
                }
                // The anchor belongs to the death that just ended: it is spent here, whether the drop was anchored
                // or fell back. The phase leaves the death screen, so the periodic record timer runs again.
                bool wasSessionDeath = Death.Phase == DeathPhase.DeathScreen;
                if (wasSessionDeath) Death.Respawn(_pending == AnchorOutcome.None ? AnchorOutcome.NoAnchor : _pending, Time.unscaledTimeAsDouble);
                _pending = AnchorOutcome.None;
                // A fresh alive record at the new respawn point. It is counted as the death's respawn record only
                // when there really was a session death: a respawn whose death was refused (or given up) still
                // tells the host the player is alive, but it must not inflate the death counters.
                if (wasSessionDeath) PlayerSave.SendRespawnRecord();
                else PlayerSave.SendAliveRecord();
            }
        }

        /// The point the death is anchored to, in global coordinates. A seated player is anchored to the GROUND
        /// under the vehicle: the seat's own position is several metres above the ground and moves with the car, so
        /// a seat anchor would drop the things in mid-air inside the vehicle. A standing player is anchored to their
        /// own position.
        internal static Vector3d DeathAnchorPoint(fpscontroller pl, bool seated)
        {
            Vector3 p = pl.transform.position;
            if (seated)
            {
                // The GROUND under the vehicle, not the seat. The seat's own position is the player's position
                // (the game's GetIn puts the player exactly there and parents it to the seat), so its x and z are
                // already under the vehicle; only the height is wrong — the seat is a metre or two above the
                // ground. The ground is found with a downward ray that ignores the vehicle and the player (the
                // bridge's own GroundHit skips the player and every collider with a rigidbody, so the car itself
                // is never the answer).
                p.y = GroundHeight(p);
            }
            return mainscript.GlobalFromUnityPos(p);
        }

        /// The height of the ground under a point, in Unity coordinates. The terrain if there is one, the point
        /// itself when the ground is not generated (the world is streamed: the ground may not exist yet).
        internal static float GroundHeight(Vector3 p)
        {
            try
            {
                var hit = DebugBridge.GroundHit(p.x, p.z);
                if (hit != null) return hit.Value.point.y;
            }
            catch { }
            return p.y;
        }

        public static string DeathStats()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var d = Death;
            return "{\"dropAnchor\":" + (DeathDropAnchor ? "true" : "false") + ",\"hasAnchor\":" + (d.HasAnchor ? "true" : "false") +
                   ",\"phase\":" + (int)d.Phase + ",\"respawns\":" + Respawns + ",\"anchors\":" + d.AnchorsCaptured + ",\"anchorDrops\":" + d.AnchorDrops + ",\"fallbacks\":" + d.AnchorFallbacks +
                   ",\"anchorAbsent\":" + d.AnchorAbsent + ",\"deaths\":" + d.Deaths + ",\"seatedDeaths\":" + d.SeatedDeaths + ",\"abandoned\":" + d.DeathsAbandoned +
                   ",\"rejected\":" + d.Rejected + ",\"rejectedNotInSession\":" + d.RejectedNotInSession +
                   ",\"rejectedAlreadyDead\":" + d.RejectedAlreadyDead + ",\"rejectedImmortal\":" + d.RejectedImmortal + ",\"rejectedNotFinite\":" + d.RejectedNotFinite +
                   // deathRecords counts the records that REALLY went out, not the deaths: the host has no
                   // per-player record to send, so a host's death raises `deaths` but not `deathRecords`.
                   ",\"deathRecords\":" + d.DeathRecords + ",\"respawnRecords\":" + d.RespawnRecords + ",\"respawnFailures\":" + d.RespawnFailures +
                   ",\"recordsSent\":" + d.Sent + ",\"recordsSuppressed\":" + d.Suppressed +
                   ",\"lastOutcome\":" + (int)d.LastOutcome + ",\"lastReject\":" + (int)d.LastReject +
                   (d.HasAnchor ? ",\"anchor\":[" + d.AnchorX.ToString("F3", ic) + "," + d.AnchorY.ToString("F3", ic) + "," + d.AnchorZ.ToString("F3", ic) +
                                      "],\"yaw\":" + d.AnchorYaw.ToString("F3", ic) + ",\"at\":" + d.AnchorAt.ToString("F3", ic) +
                                      ",\"seated\":" + (d.AnchorSeated ? "true" : "false") : "") + "}";
        }
    }
}
