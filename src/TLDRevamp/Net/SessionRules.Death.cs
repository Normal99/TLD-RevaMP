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

        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Respawn))]
        private static class PutDropsAtDeath
        {
            /// What the prefix managed to do. The postfix commits it only if the respawn really took: a respawn
            /// that failed keeps the death screen (and the anchor) alive.
            private static AnchorOutcome _pending = AnchorOutcome.None;

            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(fpscontroller __instance)
            {
                _pending = AnchorOutcome.None;
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

                // Respawn runs Revive then DropAll. Put the player and every active carry frame at the anchor first;
                // the original DropAll then performs the only item release, using its normal throw/drop behaviour.
                try
                {
                    __instance.Teleport(at, new Vector3(0f, DeathYaw, 0f));
                    if (__instance.RB != null) { __instance.RB.position = at; __instance.RB.velocity = Vector3.zero; __instance.RB.angularVelocity = Vector3.zero; }
                    var q = Quaternion.Euler(0f, DeathYaw, 0f);
                    foreach (var t in new[] { __instance.TpickedUpPos, __instance.TpickedUpParent, __instance.TpickedUpParent2 })
                        if (t != null) { t.position = at + Vector3.up * 0.5f; t.rotation = q; }
                    if (__instance.pickedUp != null) __instance.pickedUp.transform.SetPositionAndRotation(at + Vector3.up * 0.5f, q);
                    if (__instance.inHandP != null) __instance.inHandP.transform.SetPositionAndRotation(at + Vector3.up * 0.5f, q);
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
