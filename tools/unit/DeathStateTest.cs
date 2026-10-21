// The production death state machine (src/TLDRevamp/Net/DeathState.cs) compiled into this project: the test
// drives the shipped object, not a copy of it. Every rule the death sync relies on is checked here.
using System;
using TLDRevamp.Net;

static class DeathStateTest
{
    static int checks, failures;

    static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) { failures++; Console.WriteLine("FAIL " + name); }
    }

    static DeathState New(double now = 0, double every = 30.0) => new DeathState(now, every);

    static void Main()
    {
        // ---------------------------------------------------------------- the anchor
        {
            var d = New();
            Check("a fresh machine has no anchor", !d.HasAnchor);
            Check("a fresh machine is alive", d.Phase == DeathPhase.Alive);
            d.Accept(10, 20, 30, 90, 5.0, true, false, 7);
            Check("an accepted death captures the anchor", d.HasAnchor);
            Check("the anchor is the death point", d.AnchorX == 10 && d.AnchorY == 20 && d.AnchorZ == 30);
            Check("the anchor keeps the facing", Math.Abs(d.AnchorYaw - 90) < 1e-9);
            Check("the anchor belongs to its session", d.AnchorSession == 7);
            Check("one death was accepted", d.Deaths == 1);
            Check("one anchor was captured", d.AnchorsCaptured == 1);
            Check("the phase is the death screen", d.Phase == DeathPhase.DeathScreen);
        }

        // ---------------------------------------------------------------- the anchor path off
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, false, false, 1);
            Check("a death without the anchor path has no anchor", !d.HasAnchor);
            Check("but it is still a death", d.Deaths == 1 && d.Phase == DeathPhase.DeathScreen);
            Check("and no anchor was captured", d.AnchorsCaptured == 0);
        }

        // ---------------------------------------------------------------- a seated death
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, true, 1);
            Check("a seated death is counted as such", d.SeatedDeaths == 1);
            Check("a seated death keeps its anchor", d.HasAnchor);
            Check("and the anchor says it is a seated one", d.AnchorSeated);
        }

        // ---------------------------------------------------------------- the rejections
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Reject(DeathReject.AlreadyDead);
            d.Reject(DeathReject.AlreadyDead);
            d.Reject(DeathReject.Immortal);
            d.Reject(DeathReject.NotInSession);
            d.Reject(DeathReject.NotFinite);
            Check("every rejection is counted", d.Rejected == 5);
            Check("the duplicate rejections are counted apart", d.RejectedAlreadyDead == 2);
            Check("the immortal rejection is counted apart", d.RejectedImmortal == 1);
            Check("the not-in-session rejection is counted apart", d.RejectedNotInSession == 1);
            Check("the not-finite rejection is counted apart", d.RejectedNotFinite == 1);
            Check("a rejected death does not change the phase", d.Phase == DeathPhase.DeathScreen);
            Check("a rejected death does not disturb the anchor", d.HasAnchor && d.AnchorX == 1);
            Check("a rejected death does not add a death", d.Deaths == 1);
            Check("the last rejection is named", d.LastReject == DeathReject.NotFinite);
        }

        // ---------------------------------------------------------------- the record budget: the flood bound
        {
            // The named defect: while a death anchor is held, the periodic record must not go out per frame.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            int sent = 0;
            // 3600 frames at 60 fps: a minute of the death screen.
            for (int i = 0; i < 3600; i++) { if (d.DecidePeriodic(i / 60.0) == DeathState.RecordDecision.Send) { d.PeriodicSent(i / 60.0); sent++; } }
            Check("a minute of the death screen sends no periodic record", sent == 0);
            Check("the death screen refuses the periodic record", d.Suppressed >= 1);
            Check("the periodic record is not due on the death screen", !d.AllowPeriodicRecord);
        }
        {
            // The death record: exactly one, and the second attempt is refused.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            Check("the death record is allowed once", d.TryDeathRecord()); d.DeathRecordSentOk();
            Check("the death record is refused a second time", !d.TryDeathRecord());
            Check("and a third time", !d.TryDeathRecord());
            Check("one death record was counted", d.DeathRecords == 1);
            Check("the two refusals were counted as suppressed", d.Suppressed == 2);
        }
        {
            // The periodic record resumes only after the respawn.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord();
            for (int i = 0; i < 3600; i++) d.DecidePeriodic(i / 60.0);
            d.Respawn(AnchorOutcome.Anchored, 50.0);
            d.TryRespawnRecord();
            Check("the periodic record is allowed again after the respawn", d.AllowPeriodicRecord);
            // the timer was moved by the respawn: the next periodic record is a full period away
            Check("the periodic record is not due right after the respawn", d.DecidePeriodic(70.0) == DeathState.RecordDecision.Wait);
            Check("but it is due a period later", d.DecidePeriodic(80.0) == DeathState.RecordDecision.Send);
        }
        {
            // A second death gets its own records.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord(); d.DeathRecordSentOk();
            d.Respawn(AnchorOutcome.Anchored, 50.0);
            d.TryRespawnRecord(); d.RespawnRecordSentOk();
            d.Accept(4, 5, 6, 0, 0, true, false, 1);
            Check("a second death may send its own death record", d.TryDeathRecord()); d.DeathRecordSentOk();
            Check("two death records were counted", d.DeathRecords == 2);
            Check("the respawn record was counted too", d.RespawnRecords == 1);
            Check("the second death has its own anchor", d.HasAnchor && d.AnchorX == 4);
        }

        // ---------------------------------------------------------------- the respawn
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.Anchored, 50.0);
            Check("an anchored respawn is counted as an anchored drop", d.AnchorDrops == 1);
            Check("and not as a fallback", d.AnchorFallbacks == 0);
            Check("the anchor is spent", !d.HasAnchor);
            Check("the phase left the death screen", d.Phase == DeathPhase.Respawned);
        }
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.NotRestorable, 50.0);
            Check("a fallback is never claimed as an anchored drop", d.AnchorDrops == 0);
            Check("the fallback is counted", d.AnchorFallbacks == 1);
        }
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.PlacementFailed, 50.0);
            Check("a failed placement is a fallback", d.AnchorFallbacks == 1 && d.AnchorDrops == 0);
        }
        {
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.NoAnchor, 50.0);
            Check("no anchor is its own outcome", d.AnchorAbsent == 1);
            Check("and never an anchored drop", d.AnchorDrops == 0);
            Check("and never a fallback", d.AnchorFallbacks == 0);
        }
        {
            // A respawn that did not take: the death screen keeps holding.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord(); d.DeathRecordSentOk();
            d.RespawnFailed();
            Check("a respawn that did not take is counted", d.RespawnFailures == 1);
            Check("the death screen still holds", d.Phase == DeathPhase.DeathScreen);
            Check("the anchor survives for the respawn that will", d.HasAnchor);
            Check("the periodic record is still refused", !d.AllowPeriodicRecord);
            Check("and no second death record is sent", !d.TryDeathRecord());
            Check("the death record count did not grow", d.DeathRecords == 1);
        }

        // ---------------------------------------------------------------- the abandoned death (the repair)
        {
            // The hole this lane repairs: a death that never respawns (the player leaves, the world reloads, the
            // death screen is dismissed) must not hold the record budget forever.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord(); d.DeathRecordSentOk();
            for (int i = 0; i < 3600; i++) d.DecidePeriodic(i / 60.0);
            d.DeathAbandoned(50.0);
            Check("the abandoned death is counted", d.DeathsAbandoned == 1);
            Check("the anchor is given up", !d.HasAnchor);
            Check("the phase leaves the death screen", d.Phase == DeathPhase.Alive);
            Check("the periodic record is allowed again", d.AllowPeriodicRecord);
            Check("and the record really goes out again", d.DecidePeriodic(1000.0) == DeathState.RecordDecision.Send);
        }
        {
            // The abandon must not disturb a death that is running.
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.DeathAbandoned(50.0);
            d.DeathAbandoned(50.0);
            Check("the abandon is counted once", d.DeathsAbandoned == 1);
            Check("a second abandon does nothing", d.DeathsAbandoned == 1);
        }
        {
            // A death that was abandoned can be abandoned again by a new death, and the new death is its own.
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            Check("the first death sent its record", d.TryDeathRecord()); d.DeathRecordSentOk();
            d.DeathAbandoned(50.0);
            d.Accept(9, 9, 9, 0, 0, true, false, 1);
            Check("the new death has its own anchor", d.HasAnchor && d.AnchorX == 9);
            Check("the new death may send its own record", d.TryDeathRecord()); d.DeathRecordSentOk();
            Check("two death records in total", d.DeathRecords == 2);
        }

        // ---------------------------------------------------------------- the session reset
        {
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            // The record must be SENT before the reset, or the reset's clearing of the flag is not tested at
            // all: Accept() also clears it, so a flag that was never set would pass either way.
            Check("the death record is allowed", d.TryDeathRecord());
            d.DeathRecordSentOk();
            Check("the death record went out before the reset", d.DeathRecordSent);
            Check("the respawn record is allowed", d.TryRespawnRecord());
            d.RespawnRecordSentOk();
            Check("the respawn record went out before the reset", d.RespawnRecordSent);
            d.Reset();
            Check("the reset leaves no anchor", !d.HasAnchor);
            Check("the reset leaves the machine alive", d.Phase == DeathPhase.Alive);
            Check("the reset leaves no session on the anchor", d.AnchorSession == -1);
            Check("the reset keeps the session's history", d.Deaths == 1 && d.AnchorsCaptured == 1);
            Check("the reset allows the periodic record again", d.AllowPeriodicRecord);
            // The death and respawn RECORD budgets are part of the session reset as well: a machine that
            // rejoined and inherited a stale "sent" flag would silently drop its next death record.
            Check("the reset clears the death-record budget", !d.DeathRecordSent);
            Check("the reset clears the respawn-record budget", !d.RespawnRecordSent);
            Check("the reset lets a new death record after it", d.TryDeathRecord());
            Check("the reset lets a new respawn record after it", d.TryRespawnRecord());
            // the timer is moved to a full interval from now: with the timer at 0 the next periodic record would
            // be due immediately, so a machine that rejoined would send a record on its first frame.
            Check("the reset does not make the record due at once", d.DecidePeriodic(0.0) == DeathState.RecordDecision.Wait);
            Check("the record comes round a period later", d.DecidePeriodic(30.0) == DeathState.RecordDecision.Send);
        }
        {
            // A decision that is never carried out does not use up the record: the counter says what went out.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            Check("the death record is allowed", d.TryDeathRecord());
            Check("but it is not counted before it went out", d.DeathRecords == 0);
            d.DeathRecordSentOk();
            Check("and it is counted once it went out", d.DeathRecords == 1);
            Check("and the total counts it too", d.Sent == 1);
        }
        {
            // A send that failed must not spend the one record of the death: the next attempt may still send it.
            // (Without this the death screen would be silent for the rest of its life when the game was briefly
            // not ready — the record was spent on an attempt that never left the machine.)
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            Check("the death record is allowed", d.TryDeathRecord());
            d.DeathRecordFailed();
            Check("the failed send is counted as suppressed", d.Suppressed == 1);
            Check("and it is not counted as a sent record", d.DeathRecords == 0);
            Check("the record may still be sent", d.TryDeathRecord());
            d.DeathRecordSentOk();
            Check("now it is counted", d.DeathRecords == 1);
        }
        {
            // The same for the respawn record.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord(); d.DeathRecordSentOk();
            d.Respawn(AnchorOutcome.Anchored, 50.0);
            Check("the respawn record is allowed", d.TryRespawnRecord());
            d.RespawnRecordFailed();
            Check("the failed send is counted as suppressed", d.Suppressed == 1);
            Check("and it is not counted as a sent record", d.RespawnRecords == 0);
            Check("the record may still be sent", d.TryRespawnRecord());
            d.RespawnRecordSentOk();
            Check("now it is counted", d.RespawnRecords == 1);
        }
        {
            // A new session's budget starts over.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.TryDeathRecord();
            d.ResetBudget(100.0, 30.0);
            Check("the new budget has sent nothing", d.Sent == 0);
            Check("the new budget has suppressed nothing", d.Suppressed == 0);
            Check("the new budget may send a death record again", d.TryDeathRecord());
        }
        {
            // The anchor of a previous session can never be used by the next one.
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 42);
            Check("the anchor names its session", d.AnchorSession == 42);
        }

        // ---------------------------------------------------------------- the periodic timer
        {
            var d = New(0, 30.0);
            Check("the first periodic record is a period away", d.DecidePeriodic(29.9) == DeathState.RecordDecision.Wait);
            Check("and due at the period", d.DecidePeriodic(30.0) == DeathState.RecordDecision.Send); d.PeriodicSent(30.0);
            Check("the timer moved", d.DecidePeriodic(59.0) == DeathState.RecordDecision.Wait);
            Check("and comes round again", d.DecidePeriodic(60.0) == DeathState.RecordDecision.Send); d.PeriodicSent(60.0);
            Check("two records were sent", d.Sent == 2);
        }
        {
            // A suppressed window does not shorten the next interval.
            var d = New(0, 30.0);
            d.Accept(1, 2, 3, 0, 0, true, false, 1);   // the death screen holds the budget
            Check("the due periodic record is suppressed", d.DecidePeriodic(30.0) == DeathState.RecordDecision.Suppressed);
            Check("the suppression is counted", d.Suppressed == 1);
            Check("the timer moved past the due moment", d.DecidePeriodic(45.0) == DeathState.RecordDecision.Wait);
            Check("and comes round a full period later", d.DecidePeriodic(60.0) == DeathState.RecordDecision.Suppressed);
            Check("the second suppression is counted too", d.Suppressed == 2);
        }
        {
            // The interval is clamped: a zero or negative interval must not become a per-frame record.
            var d = New(0, 0.0);
            Check("a zero interval is clamped", d.Interval >= 0.1);
            var e = New(0, -5.0);
            Check("a negative interval is clamped", e.Interval >= 0.1);
        }
        {
            // The interval changed (the setting that says how often the ordinary record goes out). The timer keeps
            // its moment: changing the interval must not make the record due at once.
            var d = New(0, 30.0);
            d.SchedulePeriodic(20.0);
            d.SetInterval(10.0);
            Check("the interval changed", Math.Abs(d.Interval - 10.0) < 1e-9);
            Check("the timer kept its moment", d.DecidePeriodic(20.0) == DeathState.RecordDecision.Send);
            d.PeriodicSent(20.0);
            Check("the next one is a new interval away", d.DecidePeriodic(25.0) == DeathState.RecordDecision.Wait);
            Check("and comes round at the new interval", d.DecidePeriodic(30.0) == DeathState.RecordDecision.Send);
        }
        {
            // A joiner that just became ready wants its first record soon, not a full period.
            var d = New(0, 30.0);
            d.SchedulePeriodic(5.0);
            Check("the first record is scheduled soon", d.DecidePeriodic(5.0) == DeathState.RecordDecision.Send);
        }
        {
            // The interval can be changed (the settings' record period).
            var d = New(0, 30.0);
            d.RestartPeriodic(100.0, 10.0);
            Check("the new interval is used", d.DecidePeriodic(105.0) == DeathState.RecordDecision.Wait && d.DecidePeriodic(110.0) == DeathState.RecordDecision.Send);
        }

        // ---------------------------------------------------------------- the outcome of a death
        {
            // A new death must not read the previous death's outcome.
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.Anchored, 50.0);
            Check("the outcome of the last respawn is kept", d.LastOutcome == AnchorOutcome.Anchored);
            d.Accept(4, 5, 6, 0, 0, true, false, 1);
            Check("a new death clears the previous outcome", d.LastOutcome == AnchorOutcome.None);
        }
        {
            // A rejected death must not clear the running one's outcome either.
            var d = New();
            d.Accept(1, 2, 3, 0, 0, true, false, 1);
            d.Respawn(AnchorOutcome.PlacementFailed, 50.0);
            d.Accept(4, 5, 6, 0, 0, true, false, 1);
            d.Reject(DeathReject.AlreadyDead);
            Check("the rejected death did not change the outcome", d.LastOutcome == AnchorOutcome.None);
            Check("and the phase is the new death's", d.Phase == DeathPhase.DeathScreen);
        }

        Console.WriteLine(failures == 0
            ? $"PASS DeathState: {checks} checks"
            : $"FAIL DeathState: {failures} of {checks} checks failed");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
