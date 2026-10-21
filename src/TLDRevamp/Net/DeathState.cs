using System;

namespace TLDRevamp.Net
{
    /// What a multiplayer death is doing right now. The pure state machine behind SessionRules.Death (the Harmony
    /// hooks) and PlayerSave's record budget: every transition is decided here, so it can be tested without the game.
    public enum DeathPhase : byte
    {
        /// No death is in progress: the player is alive and the periodic record timer runs as usual.
        Alive = 0,
        /// The player is dead: the death screen is up, the body is falling. Exactly one record goes out (the one that
        /// names the death point) and the periodic record timer is silent until the respawn.
        DeathScreen = 1,
        /// The respawn ran: the anchor is spent, the player is alive again, one fresh alive record is due.
        Respawned = 2,
    }

    /// Why a death was not accepted as a session death. Every rejection is counted and reported, never silently
    /// ignored. A rejected death does not change the phase: the running one (if any) is untouched.
    public enum DeathReject : byte
    {
        None = 0,
        /// No session: nothing to sync.
        NotInSession = 1,
        /// The player was already dead when Death was called (a duplicate death): no second anchor, no second record.
        AlreadyDead = 2,
        /// God mode, fly mode or the ground immunity was on: the game itself ignores this death, so there is no
        /// death to record.
        Immortal = 3,
        /// The anchor position was not finite, so nothing could be anchored.
        NotFinite = 4,
    }

    /// Why a respawn did not put the dropped things where the player fell. Every outcome is counted; a fallback is
    /// always visible in the diagnostics and never claimed as an anchored drop.
    public enum AnchorOutcome : byte
    {
        /// No respawn happened yet (or none with an anchor).
        None = 0,
        /// The player and their carried frames were put at the anchor; the game's own drop released the things there.
        Anchored = 1,
        /// The anchor could not be turned into a world position: the drop fell back to where the body ended up.
        NotRestorable = 2,
        /// The placement itself failed: the drop fell back to where the body ended up.
        PlacementFailed = 3,
        /// There was no anchor to use: the anchor path was off, the death was never captured, or it was already spent.
        NoAnchor = 4,
    }

    /// The death state machine, without Unity and without the game: what a death does to the record budget, when an
    /// anchor exists, and what the caller should do next. The Harmony hooks in SessionRules.Death ask it; PlayerSave
    /// asks it whether it may send a record. Every rule that used to live in a hook (or in a comment) is here, so a
    /// test can check it exactly.
    ///
    /// The machine and the record budget are ONE object on purpose. When they were two, a bug in one (the phase saying
    /// "death screen" while the budget said "record spent", or the other way round) silently produced either a record
    /// flood or a save that never resumed. Here the phase decides and the budget spends, from the same fields.
    public sealed class DeathState
    {
        /// The phase the machine is in.
        public DeathPhase Phase { get; private set; } = DeathPhase.Alive;

        // ---------------------------------------------------------------- the anchor

        /// The death point in global coordinates (the player's accepted pose, before the body was flung).
        public double AnchorX, AnchorY, AnchorZ;
        /// The body's facing at the death point, degrees.
        public double AnchorYaw;
        /// When the anchor was captured (the game's realtime clock, seconds).
        public double AnchorAt { get; private set; }
        /// When this death was accepted (the game's realtime clock, seconds). The self-heal uses it: a death that
        /// was accepted a moment ago is a death in progress, not a death that was given up.
        public double DiedAt { get; private set; }
        /// Is there an anchor to drop at right now? Only a death with the anchor path on has one.
        public bool HasAnchor { get; private set; }
        /// The anchor was captured while the player sat in a vehicle. The anchor is then the ground under the vehicle,
        /// not the seat: the seat's own position moves with the car and is several metres above the ground.
        public bool AnchorSeated { get; private set; }
        /// The anchor of this death belongs to this session: a session id that a later session cannot match. A stale
        /// anchor (a death that never respawned, a session that ended) can never be used by the next session.
        public int AnchorSession { get; private set; } = -1;
        /// The anchor could not be turned back into a world position, or the placement of the drops failed: the drops
        /// fell back to where the body ended up and nothing claims an anchored drop.
        public bool Fallback { get; private set; }
        /// Why the anchor was refused or fell back: "" while it is good.
        public string Refusal { get; private set; } = "";

        // ---------------------------------------------------------------- the counters

        /// How many deaths were accepted in total.
        public int Deaths { get; private set; }
        /// How many of them captured an anchor (the anchor path was on and the position was finite).
        public int AnchorsCaptured { get; private set; }
        /// How many of them happened while the player sat in a vehicle.
        public int SeatedDeaths { get; private set; }
        /// How many anchored drops were placed.
        public int AnchorDrops { get; private set; }
        /// How many respawns fell back to the body's endpoint.
        public int AnchorFallbacks { get; private set; }
        /// How many respawns ran with no anchor at all (the anchor path was off, or the anchor was already spent).
        public int AnchorAbsent { get; private set; }
        /// How many deaths were rejected, and why (see DeathReject).
        public int Rejected, RejectedNotInSession, RejectedAlreadyDead, RejectedImmortal, RejectedNotFinite;
        /// How many times a record was sent because of a death (the death record and the respawn record).
        public int DeathRecords, RespawnRecords;
        /// How many respawns ran while the player was still dead (the respawn did not take).
        public int RespawnFailures { get; private set; }
        /// How many times a death screen was given up without a respawn (the death was abandoned: the player left the
        /// session, the world was reloaded, or the death screen was dismissed). The anchor is given up with it, so
        /// the save resumes and the next death is a fresh one.
        public int DeathsAbandoned { get; private set; }
        /// The last rejection's reason (None = none).
        public DeathReject LastReject { get; private set; } = DeathReject.None;
        /// The last respawn's outcome.
        public AnchorOutcome LastOutcome { get; private set; } = AnchorOutcome.None;

        // ---------------------------------------------------------------- the record budget

        /// The periodic interval in seconds: how often the ordinary record goes out while the player is alive.
        public double Interval { get; private set; } = 30.0;
        /// The periodic record timer: when the next periodic record is due (the game's unscaled clock).
        public double NextPeriodicAt { get; private set; }
        /// The death record was sent (the one record that names the death point).
        public bool DeathRecordSent { get; private set; }
        /// The respawn record was sent (the fresh alive record).
        public bool RespawnRecordSent { get; private set; }
        /// How many records were sent in total (every kind).
        public int Sent { get; private set; }
        /// How many records were suppressed because the death screen held the budget.
        public int Suppressed { get; private set; }

        public DeathState(double now = 0, double everySeconds = 30.0)
        {
            Interval = Math.Max(0.1, everySeconds);
            NextPeriodicAt = now + Interval;
        }

        // ---------------------------------------------------------------- the death

        /// A death was accepted. The anchor is captured when the anchor path is on (hasAnchor); without it the death
        /// still holds the record budget, so no periodic record samples the moving death camera.
        ///
        /// `session` is the id of the session the death belongs to: the anchor is only usable inside that session.
        public bool Accept(double x, double y, double z, double yaw, double at, bool hasAnchor, bool seated, int session)
        {
            Phase = DeathPhase.DeathScreen;
            Deaths++;
            DiedAt = at;
            LastReject = DeathReject.None;
            LastOutcome = AnchorOutcome.None;   // the previous death's outcome must not be read as this one's
            Fallback = false;
            Refusal = "";
            HasAnchor = hasAnchor;
            AnchorSeated = seated;
            if (seated) SeatedDeaths++;
            if (hasAnchor)
            {
                AnchorX = x; AnchorY = y; AnchorZ = z; AnchorYaw = yaw; AnchorAt = at;
                AnchorSession = session;
                AnchorsCaptured++;
            }
            else { AnchorX = AnchorY = AnchorZ = AnchorYaw = 0; AnchorAt = 0; AnchorSession = -1; }
            // a new death gets its own records: the death record and the respawn record of THIS death.
            DeathRecordSent = false;
            RespawnRecordSent = false;
            return true;
        }

        /// The death was not accepted. The anchor (if any) stays as it was: a rejected death never disturbs the
        /// running one. Returns false so the caller can skip its own work.
        public bool Reject(DeathReject why)
        {
            Rejected++;
            LastReject = why;
            switch (why)
            {
                case DeathReject.NotInSession: RejectedNotInSession++; break;
                case DeathReject.AlreadyDead: RejectedAlreadyDead++; break;
                case DeathReject.Immortal: RejectedImmortal++; break;
                case DeathReject.NotFinite: RejectedNotFinite++; break;
            }
            return false;
        }

        /// The respawn took: the death is over and the anchor is spent. A fallback is counted as a fallback, never
        /// as an anchored drop.
        ///
        /// `now` is the game's clock. The periodic timer is moved here, not in the caller: the death screen held it
        /// for as long as it lasted, and the respawn is the moment the ordinary record starts again. Keeping the
        /// timer in the machine is what stops the phase and the timer from disagreeing.
        public AnchorOutcome Respawn(AnchorOutcome outcome, double now)
        {
            Phase = DeathPhase.Respawned;
            RestartPeriodic(now, Interval);
            HasAnchor = false;
            Fallback = false;
            Refusal = "";
            AnchorSession = -1;
            switch (outcome)
            {
                case AnchorOutcome.Anchored: AnchorDrops++; break;
                case AnchorOutcome.NotRestorable:
                case AnchorOutcome.PlacementFailed: AnchorFallbacks++; break;
                case AnchorOutcome.NoAnchor: AnchorAbsent++; break;
            }
            LastOutcome = outcome;
            return outcome;
        }

        /// The respawn did not take: the player is still dead. The phase stays on the death screen, so the record
        /// budget is still held, no alive record is claimed, and the anchor survives for the respawn that will.
        public void RespawnFailed()
        {
            RespawnFailures++;
            LastOutcome = AnchorOutcome.None;
        }

        /// The death was given up: the death screen was left without a respawn (the player left the session, the
        /// world was reloaded, the death screen was dismissed). The anchor is given up with it, so the periodic
        /// record resumes and the next death is a fresh one. Without this the death screen held the budget forever
        /// and the joiner's save never resumed.
        public void DeathAbandoned()
        {
            if (Phase != DeathPhase.DeathScreen) return;
            DeathsAbandoned++;
            Phase = DeathPhase.Alive;
            HasAnchor = false;
            Fallback = false;
            Refusal = "";
            AnchorSession = -1;
            AnchorX = AnchorY = AnchorZ = AnchorYaw = 0;
            AnchorAt = 0;
            // the death record of this death already went out; a new death gets its own.
            DeathRecordSent = false;
            RespawnRecordSent = false;
        }

        /// The abandon moves the periodic timer as well: the record that was held back during the death screen is
        /// due a full interval from the moment the death was given up.
        public void DeathAbandoned(double now)
        {
            if (Phase != DeathPhase.DeathScreen) return;
            DeathAbandoned();
            RestartPeriodic(now, Interval);
        }

        /// A new session (or a rehost): everything about the last death is gone. The counters that describe the
        /// session's history stay; the anchor and the phase do not.
        ///
        /// `now` is the game's clock. The periodic timer is moved to a full interval from now: with the timer at 0
        /// the next periodic record would be due **immediately**, so a machine that rejoined would send a record on
        /// its first frame.
        public void Reset(double now = 0)
        {
            Phase = DeathPhase.Alive;
            HasAnchor = false;
            Fallback = false;
            Refusal = "";
            AnchorSession = -1;
            AnchorX = AnchorY = AnchorZ = AnchorYaw = 0;
            AnchorAt = 0;
            DiedAt = 0;
            LastOutcome = AnchorOutcome.None;
            LastReject = DeathReject.None;
            DeathRecordSent = false;
            RespawnRecordSent = false;
            // the periodic timer is the session's: it starts again from now.
            NextPeriodicAt = now + Interval;
        }

        /// May a periodic record be sent now? The death screen sends exactly one record (the anchored one) and
        /// then the periodic timer is silent until the respawn.
        public bool AllowPeriodicRecord => Phase != DeathPhase.DeathScreen;

        // ---------------------------------------------------------------- the record budget
        //
        // The budget only *decides*; the sending path commits what it really sent. A decision that is never carried
        // out must not use up a record: the host has no per-player record to send at all, and its counter must say
        // so (a counter that counted attempts would claim a record that never left the machine).

        /// What the ordinary timer decided for this frame.
        public enum RecordDecision
        {
            /// Not yet: the periodic timer has not come round.
            Wait,
            /// The ordinary record may go out now.
            Send,
            /// The death screen holds the budget: nothing goes out.
            Suppressed,
        }

        /// The decision for this frame. While the death screen holds the budget the ordinary record is refused and
        /// counted, and the timer moves on by one interval: the count is then the number of periodic records the
        /// death screen swallowed, not the number of frames it lasted.
        public RecordDecision DecidePeriodic(double now)
        {
            if (!AllowPeriodicRecord)
            {
                if (now < NextPeriodicAt) return RecordDecision.Wait;
                Suppressed++;
                NextPeriodicAt = now + Interval;
                return RecordDecision.Suppressed;
            }
            if (now < NextPeriodicAt) return RecordDecision.Wait;
            return RecordDecision.Send;
        }

        /// The ordinary record really went out: the timer moves on, and the count of sent records grows.
        public void PeriodicSent(double now)
        {
            Sent++;
            NextPeriodicAt = now + Interval;
        }

        /// The one record of the death screen may go out. A second attempt is refused: the budget is one record,
        /// not one per frame. A new death clears the flag (Accept), so a second death gets its own record.
        ///
        /// The flag is only set when the record really goes out (DeathRecordSentOk): a send that failed (no player
        /// yet, the game not ready) must not spend the one record of this death, or the death screen would be
        /// silent for the rest of its life.
        public bool TryDeathRecord()
        {
            if (DeathRecordSent) { Suppressed++; return false; }
            return true;
        }

        /// The death record really went out: the one record of this death is spent now.
        public void DeathRecordSentOk()
        {
            DeathRecordSent = true;
            Sent++;
            DeathRecords++;
        }

        /// The death record could not go out (the game was not ready): the record is NOT spent, so the next attempt
        /// may still send it. The attempt is counted as suppressed so the diagnostics show it.
        public void DeathRecordFailed()
        {
            Suppressed++;
        }

        /// The fresh alive record after the respawn may go out. A second attempt is refused.
        public bool TryRespawnRecord()
        {
            if (RespawnRecordSent) { Suppressed++; return false; }
            return true;
        }

        /// The respawn record really went out: the one record of this respawn is spent now.
        public void RespawnRecordSentOk()
        {
            RespawnRecordSent = true;
            Sent++;
            RespawnRecords++;
        }

        /// The respawn record could not go out: the record is NOT spent, so the next attempt may still send it.
        public void RespawnRecordFailed()
        {
            Suppressed++;
        }

        /// The periodic timer is set again (after the respawn, or when a new session starts): the next periodic
        /// record is one full interval from now.
        public void RestartPeriodic(double now, double everySeconds)
        {
            Interval = Math.Max(0.1, everySeconds);
            NextPeriodicAt = now + Interval;
        }

        /// The interval changed (the setting that says how often the ordinary record goes out). The timer keeps
        /// its moment: changing the interval must not make the record due at once.
        public void SetInterval(double everySeconds)
        {
            Interval = Math.Max(0.1, everySeconds);
        }

        /// The periodic record is scheduled for a given moment (a joiner that just became ready wants its first
        /// record in a few seconds, not a full interval).
        public void SchedulePeriodic(double at) { NextPeriodicAt = at; }

        /// A new session: the budget starts over.
        public void ResetBudget(double now, double everySeconds)
        {
            RestartPeriodic(now, everySeconds);
            DeathRecordSent = false;
            RespawnRecordSent = false;
            Sent = 0;
            Suppressed = 0;
        }
    }
}
