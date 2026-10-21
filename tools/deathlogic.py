"""The rules the death test observes, as pure functions: which ids are *this* death's, which corpse belongs to the
player that died, whether the host's player record is the tested player's, and how the test's own expectations move
from one death to the next. The rules live here (not in the test script) so they can be checked offline.

This module never talks to the game: the test feeds it what the bridge returned and it says what the observation
means. tools/test_deathlogic.py checks the rules, including the negative cases.
"""
import math

CORPSE_PREFIX = "PlayerRagdol"


def finite(*values):
    """True when every value is a finite number (a NaN or an infinity is not a position)."""
    for v in values:
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            return False
        if not math.isfinite(v):
            return False
    return True


def new_net_ids(before, after):
    """The net ids that appeared between two entity listings. `before` and `after` are {net id: row} maps (or
    iterables of net ids). Only what is new counts: an item that was already there is not this death's."""
    b = set(before.keys() if isinstance(before, dict) else before)
    a = set(after.keys() if isinstance(after, dict) else after)
    return a - b


def new_model_ids(before_positions, after_positions):
    """The item model ids that appeared between two position listings ({model id: (x, y, z)})."""
    return set(after_positions.keys()) - set(before_positions.keys())


def distance2d(a, b):
    """The distance between two points, in the horizontal plane (x, z)."""
    if a is None or b is None or len(a) < 3 or len(b) < 3:
        return float("inf")
    if not finite(a[0], a[2]) or not finite(b[0], b[2]):
        return float("inf")
    return math.hypot(float(a[0]) - float(b[0]), float(a[2]) - float(b[2]))


def distance3d(a, b):
    """The distance between two points in space."""
    if a is None or b is None or len(a) < 3 or len(b) < 3:
        return float("inf")
    if not finite(*a[:3]) or not finite(*b[:3]):
        return float("inf")
    return math.dist((float(a[0]), float(a[1]), float(a[2])), (float(b[0]), float(b[1]), float(b[2])))


def corpse_candidates(entities):
    """The entities whose name says they are a player corpse: [(net id, row)].

    A corpse is a PlayerRagdol-owning entity, not the remote player's ordinary body (its name does not start with
    PlayerRagdol)."""
    out = []
    for net, row in entities.items():
        name = (row.get("name") or "") if isinstance(row, dict) else ""
        if name.startswith(CORPSE_PREFIX):
            out.append((net, row))
    out.sort(key=lambda pair: pair[0])
    return out


def pick_corpse(entities, before_nets, joiner_id):
    """The corpse of this death: a PlayerRagdol that is new (not in `before_nets`) and owned by the player that
    died (`joiner_id`).

    Returns (net id, row, reason). The reason says why nothing was picked, so a failed test says what it saw."""
    candidates = corpse_candidates(entities)
    if not candidates:
        return None, None, "no PlayerRagdol entity at all"
    fresh = [(net, row) for net, row in candidates if net not in before_nets]
    if not fresh:
        return None, None, f"only corpses that were already there: {sorted(n for n, _ in candidates)}"
    owned = [(net, row) for net, row in fresh if row.get("owner") == joiner_id]
    if not owned:
        return None, None, ("the new corpse is owned by somebody else: " +
                            str([(net, row.get("owner")) for net, row in fresh]))
    net, row = owned[0]
    return net, row, "new corpse owned by the player that died"


def record_of(records, steam):
    """The host's record of this exact player (by Steam ID), not one of somebody else's."""
    for r in records or []:
        try:
            if int(r.get("steam", 0)) == int(steam):
                return r
        except (TypeError, ValueError):
            continue
    return None


def check_record(record, steam, anchor, dead_expected=True, distance_m=3.0):
    """What the host's record of the tested player says.

    Returns (ok, findings). The record must be the tested player's, its dead flag must be what the death implies,
    its coordinates must be finite, and it must be at the death point."""
    findings = []
    if record is None:
        return False, ["no record for the tested player"]
    findings.append(f"record of steam {record.get('steam')} named {record.get('name')!r}")
    if int(record.get("steam", 0)) != int(steam):
        return False, findings + ["the record belongs to somebody else"]
    dead = record.get("dead")
    if dead_expected and dead is not True:
        return False, findings + [f"the record does not say the player is dead (dead={dead})"]
    if not dead_expected and dead is True:
        return False, findings + ["the record still says the player is dead"]
    gx, gz = record.get("gx"), record.get("gz")
    if not finite(gx, gz):
        return False, findings + [f"the record's coordinates are not finite: gx={gx} gz={gz}"]
    findings.append(f"record at ({gx}, {gz}) dead={dead}")
    if anchor is not None:
        d = distance2d((float(gx), 0.0, float(gz)), anchor)
        findings.append(f"{d:.2f} m from the death anchor")
        if d > distance_m:
            return False, findings + [f"the record is {d:.2f} m from the death point"]
    return True, findings


def worst_limb_distance(host_limbs, owner_limbs, limit_m=0.75):
    """The largest distance between the host's settled limbs and the owner's own settled limbs.

    The two lists are the same limbs in the same order ([x, y, z, ...] each). Returns (ok, worst, count)."""
    if not host_limbs or not owner_limbs:
        return False, float("inf"), 0
    n = min(len(host_limbs), len(owner_limbs))
    if n < 3:
        return False, float("inf"), n
    worst = 0.0
    for i in range(n):
        h, o = host_limbs[i], owner_limbs[i]
        if not finite(h[0], h[1], h[2]) or not finite(o[0], o[1], o[2]):
            return False, float("inf"), n
        worst = max(worst, math.dist((float(h[0]), float(h[1]), float(h[2])),
                                           (float(o[0]), float(o[1]), float(o[2]))))
    return worst <= limit_m, worst, n


class DeathExpectation:
    """What the test expects from the counters after each death. The rules of a death sequence:

    - the first death of a session: one anchored record, the periodic record suppressed, a fresh record on respawn.
    - a duplicate death (the game's own Death called twice for one death): still one anchored record.
    - a repeated death (the player dies again later): a second anchored record and a second fresh record.
    - a host death: the host keeps no per-player record, so no record is expected at all.
    - a reconnect (the joiner leaves and comes back): the counters start again with the new session.
    """
    def __init__(self):
        self.deaths = 0
        self.records = 0
        self.respawns = 0

    def death(self, duplicate=False, host=False):
        """A death (or a duplicate of the one just counted). Returns the records the death is expected to add."""
        if duplicate:
            return 0            # the second Death call of one death adds no record
        self.deaths += 1
        if host:
            return 0            # the host has no per-player record to send
        self.records += 1
        return 1

    def respawn(self, host=False):
        if host:
            return 0
        self.respawns += 1
        return 1

    def reconnect(self):
        """A new session on the other side: the counters belong to the old session."""
        self.deaths = 0
        self.records = 0
        self.respawns = 0

    def check(self, death_records, respawn_records):
        """Whether the counters the bridge reported are the ones this sequence expects."""
        problems = []
        if death_records != self.records:
            problems.append(f"expected {self.records} anchored death record(s), the bridge counted {death_records}")
        if respawn_records != self.respawns:
            problems.append(f"expected {self.respawns} record(s) after the respawn, the bridge counted {respawn_records}")
        return not problems, problems


def policy_allows(policy, is_host):
    """Whether the developer policy lets this side use the debug bridge."""
    return policy == "everyone" or (policy == "hostonly" and is_host)


class RecordBudget:
    """The record budget of a joiner, as the pure rules the game and the test agree on.

    This mirrors the production `DeathState` budget: one record for the death, one for the respawn, and a periodic
    record that is refused while the death screen holds the budget. The test uses it to say what a *long* death
    screen may produce, without needing the game.
    """
    def __init__(self, now=0.0, interval=30.0):
        self.interval = max(0.1, interval)
        self.next_due = now + self.interval
        self.dead = False
        self.anchor_owed = True
        self.death_record_sent = False
        self.respawn_record_sent = False
        self.sent = 0
        self.suppressed = 0

    def tick(self, now):
        """One frame of the ordinary timer. Returns what the frame may send: 'send', 'death' or nothing."""
        if self.dead:
            if now >= self.next_due:
                self.suppressed += 1
                self.next_due = now + self.interval
            return None
        if now < self.next_due:
            return None
        self.sent += 1
        self.next_due = now + self.interval
        return "send"

    def death(self):
        """The death screen's one record. True when it may go out."""
        self.dead = True
        if self.death_record_sent:
            self.suppressed += 1
            return False
        self.death_record_sent = True
        self.sent += 1
        return True

    def respawn(self, now):
        """The respawn's one record and the restart of the ordinary timer."""
        self.dead = False
        self.anchor_owed = True
        self.next_due = now + self.interval
        if self.respawn_record_sent:
            self.suppressed += 1
            return False
        self.respawn_record_sent = True
        self.sent += 1
        return True

    def abandon(self, now):
        """The death was given up without a respawn: the anchor is gone and the ordinary timer starts again."""
        if not self.dead:
            return False
        self.dead = False
        self.death_record_sent = False
        self.respawn_record_sent = False
        self.next_due = now + self.interval
        return True


def record_budget_bound(frames, fps=60.0, interval=30.0, death_screen_frames=None):
    """How many records a death screen of `frames` frames may produce.

    The bound is the point of the whole lane: a death screen of any length produces exactly one death record (and
    one respawn record after it), never one record per frame."""
    b = RecordBudget(0.0, interval)
    if death_screen_frames is None:
        death_screen_frames = frames
    b.death()
    for i in range(death_screen_frames):
        b.tick(i / fps)
    b.respawn(death_screen_frames / fps)
    return b
