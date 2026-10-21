#!/usr/bin/env python3
"""The death test's own rules, checked offline (no game, no machines): which net ids belong to a death, which
corpse is the tested player's, what the host's player record must say, what the record counters must be after
each death of a sequence, and the bound on the records a death screen may produce.

  python3 -m unittest tools.test_deathlogic -v
"""
import os, sys, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import deathlogic as D


class NewIdsTest(unittest.TestCase):
    def test_only_what_is_new_counts(self):
        before = {1: {"name": "Car"}, 2: {"name": "Rock"}}
        after = {1: {"name": "Car"}, 2: {"name": "Rock"}, 3: {"name": "PlayerRagdol"}}
        self.assertEqual(D.new_net_ids(before, after), {3})
        self.assertEqual(D.new_net_ids(after, after), set())
        self.assertEqual(D.new_net_ids(after, before), set())

    def test_net_ids_from_lists(self):
        self.assertEqual(D.new_net_ids([1, 2], [2, 3, 4]), {3, 4})

    def test_model_ids(self):
        self.assertEqual(D.new_model_ids({5: (0, 0, 0)}, {5: (0, 0, 0), 6: (1, 1, 1)}), {6})

    def test_a_nearby_item_that_existed_before_is_not_this_deaths(self):
        before = {7: (10.0, 0.0, 10.0)}
        after = {7: (10.0, 0.0, 10.0)}
        self.assertEqual(D.new_model_ids(before, after), set())


class CorpseTest(unittest.TestCase):
    def test_only_player_ragdolls_count(self):
        ents = {10: {"name": "PlayerRagdol", "owner": 2},
                11: {"name": "PlayerBody", "owner": 2},
                12: {"name": "Car", "owner": 0}}
        self.assertEqual([n for n, _ in D.corpse_candidates(ents)], [10])

    def test_the_corpse_must_be_new_and_owned_by_the_dead_player(self):
        ents = {10: {"name": "PlayerRagdol", "owner": 2}, 11: {"name": "PlayerRagdol", "owner": 3}}
        net, row, why = D.pick_corpse(ents, before_nets=set(), joiner_id=2)
        self.assertEqual(net, 10)
        self.assertIn("owned by the player that died", why)

    def test_an_old_corpse_is_not_this_deaths(self):
        ents = {10: {"name": "PlayerRagdol", "owner": 2}}
        net, row, why = D.pick_corpse(ents, before_nets={10}, joiner_id=2)
        self.assertIsNone(net)
        self.assertIn("already there", why)

    def test_a_corpse_of_somebody_else_is_not_the_dead_players(self):
        ents = {10: {"name": "PlayerRagdol", "owner": 3}}
        net, row, why = D.pick_corpse(ents, before_nets=set(), joiner_id=2)
        self.assertIsNone(net)
        self.assertIn("owned by somebody else", why)

    def test_no_corpse_at_all(self):
        net, row, why = D.pick_corpse({}, before_nets=set(), joiner_id=2)
        self.assertIsNone(net)
        self.assertIn("no PlayerRagdol", why)

    def test_the_corpse_must_be_the_only_new_one_owned(self):
        # two new corpses: the one that is not owned by the dead player must not be picked
        ents = {10: {"name": "PlayerRagdol", "owner": 5},
                11: {"name": "PlayerRagdol", "owner": 2}}
        net, row, why = D.pick_corpse(ents, before_nets=set(), joiner_id=2)
        self.assertEqual(net, 11)


class RecordTest(unittest.TestCase):
    def test_the_record_of_the_tested_player_is_found_by_steam_id(self):
        records = [{"steam": 1, "name": "a"}, {"steam": 2, "name": "b"}]
        self.assertEqual(D.record_of(records, 2)["name"], "b")
        self.assertIsNone(D.record_of(records, 3))

    def test_a_record_must_be_the_tested_players(self):
        # the host's record of somebody else: it must not pass as the tested player's
        other = {"steam": 1, "name": "a", "dead": True, "gx": 10.0, "gz": 20.0}
        ok, findings = D.check_record(other, 2, anchor=(10.0, 0.0, 20.0))
        self.assertFalse(ok)
        self.assertTrue(any("somebody else" in f for f in findings))
        # and the lookup by Steam ID finds nothing when only somebody else's record is there
        records = [other]
        self.assertIsNone(D.record_of(records, 2))

    def test_a_dead_record_at_the_death_point_passes(self):
        records = [{"steam": 2, "name": "b", "dead": True, "gx": 10.0, "gz": 20.0}]
        ok, findings = D.check_record(D.record_of(records, 2), 2, anchor=(10.0, 0.0, 20.0))
        self.assertTrue(ok, findings)

    def test_a_record_that_does_not_say_dead_fails(self):
        records = [{"steam": 2, "name": "b", "dead": False, "gx": 10.0, "gz": 20.0}]
        ok, findings = D.check_record(D.record_of(records, 2), 2, anchor=(10.0, 0.0, 20.0), dead_expected=True)
        self.assertFalse(ok)
        self.assertTrue(any("not say the player is dead" in f for f in findings))

    def test_non_finite_coordinates_fail(self):
        for gx in (float("nan"), float("inf"), None, "x"):
            records = [{"steam": 2, "name": "b", "dead": True, "gx": gx, "gz": 20.0}]
            ok, findings = D.check_record(D.record_of(records, 2), 2, anchor=(10.0, 0.0, 20.0))
            self.assertFalse(ok, f"gx={gx!r} was accepted")
            self.assertTrue(any("not finite" in f for f in findings))

    def test_a_record_far_from_the_death_point_fails(self):
        records = [{"steam": 2, "name": "b", "dead": True, "gx": 100.0, "gz": 200.0}]
        ok, findings = D.check_record(D.record_of(records, 2), 2, anchor=(10.0, 0.0, 20.0))
        self.assertFalse(ok)
        self.assertTrue(any("from the death point" in f for f in findings))

    def test_a_missing_record_fails(self):
        ok, findings = D.check_record(None, 2, anchor=(0.0, 0.0, 0.0))
        self.assertFalse(ok)
        self.assertIn("no record for the tested player", findings)

    def test_an_alive_record_is_refused_when_a_death_is_expected(self):
        # the negative case of the death record: after the respawn the record must not still say dead
        records = [{"steam": 2, "name": "b", "dead": True, "gx": 10.0, "gz": 20.0}]
        ok, findings = D.check_record(D.record_of(records, 2), 2, anchor=(10.0, 0.0, 20.0), dead_expected=False)
        self.assertFalse(ok)
        self.assertTrue(any("still says the player is dead" in f for f in findings))


class LimbTest(unittest.TestCase):
    def test_matching_limbs_pass(self):
        limbs = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
        ok, worst, n = D.worst_limb_distance(limbs, limbs)
        self.assertTrue(ok)
        self.assertEqual(worst, 0.0)
        self.assertEqual(n, 3)

    def test_limbs_that_are_too_far_apart_fail(self):
        host = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
        owner = [[0, 0, 0], [1, 0, 0], [0, 1, 3.0]]
        ok, worst, n = D.worst_limb_distance(host, owner, limit_m=0.75)
        self.assertFalse(ok)
        self.assertAlmostEqual(worst, 3.0)

    def test_missing_limbs_fail(self):
        self.assertFalse(D.worst_limb_distance([], [[0, 0, 0]])[0])
        self.assertFalse(D.worst_limb_distance([[0, 0, 0]], [])[0])
        self.assertFalse(D.worst_limb_distance([[0, 0]], [[0, 0]])[0])


class ExpectationTest(unittest.TestCase):
    def test_one_death_one_anchored_record_and_one_after_the_respawn(self):
        e = D.DeathExpectation()
        self.assertEqual(e.death(), 1)
        self.assertEqual(e.respawn(), 1)
        ok, problems = e.check(1, 1)
        self.assertTrue(ok, problems)

    def test_a_duplicate_death_adds_no_record(self):
        e = D.DeathExpectation()
        e.death()
        self.assertEqual(e.death(duplicate=True), 0)
        self.assertEqual(e.respawn(), 1)
        ok, problems = e.check(1, 1)
        self.assertTrue(ok, problems)

    def test_a_repeated_death_adds_its_own_record(self):
        e = D.DeathExpectation()
        e.death(); e.respawn()
        e.death(); e.respawn()
        ok, problems = e.check(2, 2)
        self.assertTrue(ok, problems)

    def test_the_host_death_keeps_no_record(self):
        e = D.DeathExpectation()
        self.assertEqual(e.death(host=True), 0)
        self.assertEqual(e.respawn(host=True), 0)
        ok, problems = e.check(0, 0)
        self.assertTrue(ok, problems)

    def test_a_reconnect_starts_the_counters_again(self):
        e = D.DeathExpectation()
        e.death(); e.respawn()
        e.reconnect()
        ok, problems = e.check(0, 0)
        self.assertTrue(ok, problems)
        e.death(); e.respawn()
        ok, problems = e.check(1, 1)
        self.assertTrue(ok, problems)

    def test_wrong_counters_are_reported(self):
        e = D.DeathExpectation()
        e.death(); e.respawn()
        ok, problems = e.check(2, 1)
        self.assertFalse(ok)
        self.assertEqual(len(problems), 1)
        ok, problems = e.check(1, 0)
        self.assertFalse(ok)
        self.assertEqual(len(problems), 1)

    def test_a_flooded_counter_is_reported(self):
        # the negative case of the whole lane: a death screen that sent more than one record must be caught
        e = D.DeathExpectation()
        e.death(); e.respawn()
        ok, problems = e.check(37, 1)
        self.assertFalse(ok)
        self.assertTrue(any("anchored death record" in p for p in problems))


class PolicyTest(unittest.TestCase):
    def test_the_policy_matrix(self):
        self.assertTrue(D.policy_allows("everyone", False))
        self.assertTrue(D.policy_allows("everyone", True))
        self.assertFalse(D.policy_allows("hostonly", False))
        self.assertTrue(D.policy_allows("hostonly", True))
        self.assertFalse(D.policy_allows("disabled", True))
        self.assertFalse(D.policy_allows("disabled", False))

    def test_an_unknown_policy_allows_nobody(self):
        for p in ("", "Everyone", "host", "none"):
            self.assertFalse(D.policy_allows(p, True), p)
            self.assertFalse(D.policy_allows(p, False), p)


class DistanceTest(unittest.TestCase):
    def test_the_distance_is_horizontal(self):
        self.assertAlmostEqual(D.distance2d((0, 0, 0), (3, 100, 4)), 5.0)
        self.assertEqual(D.distance2d(None, (0, 0, 0)), float("inf"))
        self.assertEqual(D.distance2d((0, 0, 0), (float("nan"), 0, 0)), float("inf"))

    def test_the_three_dimensional_distance(self):
        self.assertAlmostEqual(D.distance3d((0, 0, 0), (3, 4, 0)), 5.0)
        self.assertEqual(D.distance3d((0, 0, 0), None), float("inf"))


class RecordBudgetTest(unittest.TestCase):
    """The bound the lane exists for: a death screen of any length sends exactly one death record."""

    def test_a_short_death_screen_sends_one_death_record(self):
        b = D.RecordBudget(0.0, 30.0)
        self.assertTrue(b.death())
        for i in range(60):
            b.tick(i / 60.0)
        self.assertEqual(b.sent, 1)
        self.assertEqual(b.death_record_sent, True)

    def test_a_minute_of_death_screen_still_sends_one_record(self):
        b = D.RecordBudget(0.0, 30.0)
        b.death()
        for i in range(3600):
            b.tick(i / 60.0)
        self.assertEqual(b.sent, 1, "the death screen sent more than one record")
        self.assertGreater(b.suppressed, 0, "the suppressed periodic records were not counted")

    def test_ten_minutes_of_death_screen_still_sends_one_record(self):
        b = D.RecordBudget(0.0, 30.0)
        b.death()
        for i in range(36000):
            b.tick(i / 60.0)
        self.assertEqual(b.sent, 1)

    def test_the_second_death_record_is_refused(self):
        b = D.RecordBudget(0.0, 30.0)
        self.assertTrue(b.death())
        self.assertFalse(b.death())
        self.assertEqual(b.sent, 1)
        self.assertEqual(b.suppressed, 1)

    def test_the_respawn_sends_one_record_and_restarts_the_timer(self):
        b = D.RecordBudget(0.0, 30.0)
        b.death()
        self.assertTrue(b.respawn(100.0))
        self.assertEqual(b.sent, 2)
        self.assertFalse(b.respawn(100.0))
        self.assertEqual(b.sent, 2)
        # the ordinary record is a full period away again
        self.assertIsNone(b.tick(110.0))
        self.assertEqual(b.sent, 2)
        self.assertEqual(b.tick(130.0), "send")
        self.assertEqual(b.sent, 3)

    def test_the_abandoned_death_releases_the_budget(self):
        # the repair: a death that never respawns must not hold the budget forever
        b = D.RecordBudget(0.0, 30.0)
        b.death()
        for i in range(3600):
            b.tick(i / 60.0)
        self.assertTrue(b.abandon(100.0))
        self.assertFalse(b.dead)
        self.assertEqual(b.sent, 1)
        # the ordinary record is due again after a full period
        self.assertIsNone(b.tick(110.0))
        self.assertEqual(b.tick(130.0), "send")

    def test_the_abandon_of_a_live_death_does_nothing(self):
        b = D.RecordBudget(0.0, 30.0)
        self.assertFalse(b.abandon(10.0))
        self.assertFalse(b.dead)

    def test_a_second_death_after_the_abandon_gets_its_own_record(self):
        b = D.RecordBudget(0.0, 30.0)
        b.death()
        b.abandon(100.0)
        self.assertTrue(b.death())
        self.assertEqual(b.sent, 2)

    def test_the_ordinary_record_is_periodic_while_alive(self):
        b = D.RecordBudget(0.0, 30.0)
        self.assertIsNone(b.tick(10.0))
        self.assertEqual(b.tick(30.0), "send")
        self.assertIsNone(b.tick(50.0))
        self.assertEqual(b.tick(60.0), "send")
        self.assertEqual(b.sent, 2)

    def test_the_interval_is_clamped(self):
        b = D.RecordBudget(0.0, 0.0)
        self.assertGreaterEqual(b.interval, 0.1)
        c = D.RecordBudget(0.0, -5.0)
        self.assertGreaterEqual(c.interval, 0.1)

    def test_the_bound_helper_counts_the_whole_death(self):
        b = D.record_budget_bound(3600)
        self.assertEqual(b.sent, 2, "a minute of death screen produced more than the death and the respawn records")
        self.assertGreater(b.suppressed, 0)


if __name__ == "__main__":
    unittest.main()
