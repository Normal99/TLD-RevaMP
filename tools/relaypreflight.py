"""The fail-fast preflight of a two-machine Steam relay run.

Everything a two-machine test needs to mean anything is checked BEFORE the long part runs, and every failure is
reported with what is actually wrong, not with a symptom later on:

  * each machine runs the copy and the Proton prefix this run asked for (never the owner's play prefix),
  * each machine's bridge is on the port this run owns (a bridge answering on another port is another copy's),
  * both machines report the same mod build and the same game build,
  * the two machines are on different Steam accounts (a Steam relay join needs that),
  * the tunnel to the second machine's bridge is ours and points where we want it (a stale tunnel would read
    somebody else's game),
  * the copies run in the background, windowed and small, and tick fast enough to be usable.

The checks are pure functions over "what the machine says", so they can be fake-tested without a game. Nothing
here launches, stops or configures anything.
"""
from __future__ import annotations

import os
import subprocess


class Report:
    """The preflight's findings: a list of failures and a list of notes (things that are fine but worth saying)."""

    def __init__(self):
        self.bad = []
        self.notes = []
        self.checks = []

    def fail(self, name, detail):
        self.bad.append(f"{name}: {detail}")
        self.checks.append({"check": name, "pass": False, "detail": str(detail)[:400]})
        return False

    def ok(self, name, detail=""):
        self.checks.append({"check": name, "pass": True, "detail": str(detail)[:400]})
        return True

    def note(self, text):
        self.notes.append(str(text)[:400])

    @property
    def passed(self):
        return not self.bad

    def text(self):
        lines = []
        if self.bad:
            lines.append("PREFLIGHT FAILED:")
            lines += [f"  {b}" for b in self.bad]
        if self.notes:
            lines.append("preflight: " + "; ".join(self.notes))
        return "\n".join(lines)

    def raise_if_failed(self):
        if self.bad:
            raise PreflightFailed(self.text())
        return True


class PreflightFailed(RuntimeError):
    """the run cannot mean anything: the reasons are in the message."""


# ---- the individual checks (pure: they take what the machine said)

def check_prefix(label, want, have):
    """the copy on this machine runs in the prefix this run asked for"""
    if not want:
        return None, f"no test prefix set for the {label} (TLD_{{REMOTE_}}TEST_COMPAT): the game runs in its own"
    have = list(have or [])
    if have == [want]:
        return True, f"{label}: {want}"
    return False, (f"the {label}'s game is not in the test prefix {want} "
                   f"(it runs in {have or 'no Proton prefix'})")


def check_bridge_port(label, want_port, reported_port):
    """the machine's bridge answers on the port this run owns.

    `reported_port` has to be what the BRIDGE ITSELF says (`ping` carries the port it listens on), not
    the port this run intended: when both were the same expression the two were always equal and this
    check could never fail, which is exactly the failure it exists to catch (a bridge that answers on
    another port is another copy's).
    """
    if want_port is None:
        return False, f"this run owns no port for the {label}"
    if reported_port is None:
        return False, f"the {label}'s bridge port is unknown"
    if int(reported_port) != int(want_port):
        return False, (f"the {label}'s bridge is on port {reported_port}, not on this run's port {want_port}: "
                       f"it is another copy's bridge")
    return True, f"{label}: bridge port {reported_port}"


def check_same_build(label, host_build, client_build):
    """both machines run the same mod build"""
    if not host_build or not client_build:
        return False, f"the {label}'s mod build is unknown (host={host_build} client={client_build})"
    if host_build != client_build:
        return False, f"the two machines run different mod builds (host={host_build} client={client_build})"
    return True, f"{label}: {host_build}"


def check_game_build(label, build, expected=None):
    """the game build is the one the mod was verified on"""
    if not build:
        return False, f"the {label}'s game build is unknown"
    if expected and not str(build).startswith(str(expected)):
        return False, f"the {label}'s game build is {build}, not the verified one ({expected}…)"
    return True, f"{label}: game build {build}"


def check_distinct_accounts(host_id, client_id):
    """a Steam relay join needs two different Steam accounts"""
    if not host_id or not client_id:
        return False, f"a Steam id is unknown (host={host_id} client={client_id})"
    if host_id == client_id:
        return False, "the laptop is on the host's Steam account (sign it in to the second one)"
    return True, f"host={host_id} client={client_id}"


def check_tunnel(local_port, expected_remote_port, target):
    """the local tunnel is ours and points at the port we want.

    `target` is what the tunnel actually forwards to (host, port) or None when the local port is free.
    """
    if target is None:
        return True, f"no tunnel on the local port {local_port} yet"
    host, port = target
    if host != "127.0.0.1" or int(port) != int(expected_remote_port):
        return False, (f"the local port {local_port} carries a stale tunnel to {host}:{port}, not to the second "
                       f"machine's bridge port {expected_remote_port}: it would answer with another copy's game")
    return True, f"tunnel {local_port} → {host}:{port}"


def check_data_path(label, want_path, reported_path):
    """the running game is EXACTLY the copy this run asked for.

    The comparison is exact: the game's own `UnityEngine.Application.dataPath` has to equal the wanted
    copy's data folder. A prefix or a substring would also accept the owner's install and another copy,
    which is the whole point of the check.
    """
    if not want_path:
        return False, f"no game copy is set for the {label}"
    if not reported_path:
        return False, f"the {label}'s game did not report its data path"
    if normalise_path(reported_path) != normalise_path(want_path):
        return False, (f"the {label}'s game runs in {reported_path}, not in this run's copy {want_path}: "
                       f"it is somebody else's game")
    return True, f"{label}: {want_path}"


def normalise_path(path):
    """A path as Unity (Windows, under Proton) and as Linux see it: 'Z:\\dir\\game' and '/dir/game'
    are the same directory."""
    p = str(path or "").strip().strip('"').replace("\\", "/")
    if p[:2].lower() == "z:":
        p = p[2:]
    return os.path.normpath(p).rstrip("/")


def check_background(label, run_in_background):
    """the copy keeps ticking when its window is not focused"""
    if run_in_background is None:
        return False, f"the {label}'s background state is unknown"
    if not run_in_background:
        return False, f"the {label}'s game stops when unfocused (bg off)"
    return True, f"{label}: bg on"


def check_vsync(label, bridge_vsync, game_vsync):
    """vsync off: a hidden window with vsync on renders at 1 fps"""
    if str(bridge_vsync) != "0" or (game_vsync is not None and str(game_vsync) != "0"):
        return False, f"vsync on on the {label} (bridge={bridge_vsync} game={game_vsync}): 1 fps unfocused"
    return True, f"{label}: vsync off"


def check_windowed(label, fullscreen, width, height):
    """the copy runs windowed and small (a fullscreen copy that is not focused stops ticking)"""
    if fullscreen not in (0, "0", False):
        return False, f"the {label}'s copy runs fullscreen ({fullscreen})"
    if width and height and (int(width) > 1024 or int(height) > 600):
        return False, f"the {label}'s copy's window is {width}x{height} (too big for a test copy)"
    return True, f"{label}: windowed {width or '?'}x{height or '?'}"


def check_fps(label, fps, floor=27):
    """the copy runs fast enough to be usable"""
    if fps is None:
        return False, f"the {label} runs at an unknown frame rate (< {floor:.0f})"
    if fps < floor:
        return False, f"the {label} runs at {fps:.0f} fps (< {floor:.0f})"
    return True, f"{label}: {fps:.0f} fps"


# ---- the whole preflight, over what the machines said

def run(facts):
    """Check every fact and return the Report.

    `facts` is a plain dict, so the whole preflight can be fake-tested without a game:
      host_prefix / client_prefix : (want, [have])
      host_bridge_port / client_bridge_port : the port each machine's bridge is on
      host_bridge_port_reported / client_bridge_port_reported : the port each bridge SAYS it listens on
      host_game_data_path / client_game_data_path : the data folder of the copy this run asked for
      host_game_data_path_reported / client_game_data_path_reported : the game's own Application.dataPath
      host_build / client_build : the mod build each machine reports
      host_game_build / client_game_build : the game build each machine reports
      host_steam_id / client_steam_id : the Steam accounts
      tunnel_target : (host, port) the local tunnel forwards to, or None
      host_bg / client_bg : runInBackground on each machine
      host_vsync / client_vsync : the bridge's vsync count
      host_window / client_window : (fullscreen, width, height)
      host_fps / client_fps : the measured frame rate
    """
    r = Report()
    for label, key in (("host", "host"), ("client", "client")):
        want, have = facts.get(f"{key}_prefix", (None, []))
        passed, detail = check_prefix(label, want, have)
        if passed is None:
            r.note(detail)
        elif passed:
            r.ok(f"{label} prefix", detail)
        else:
            r.fail(f"{label} prefix", detail)

        passed, detail = check_bridge_port(label, facts.get(f"{key}_bridge_port"),
                                           facts.get(f"{key}_bridge_port_reported"))
        if passed:
            r.ok(f"{label} bridge port", detail)
        else:
            r.fail(f"{label} bridge port", detail)

        # the copy this run asked for, exactly: the game's own dataPath, read back from the bridge
        passed, detail = check_data_path(label, facts.get(f"{key}_game_data_path"),
                                         facts.get(f"{key}_game_data_path_reported"))
        if passed:
            r.ok(f"{label} game copy", detail)
        else:
            r.fail(f"{label} game copy", detail)

    passed, detail = check_same_build("both machines", facts.get("host_build"), facts.get("client_build"))
    (r.ok if passed else r.fail)("mod build", detail)

    expected_game = facts.get("expected_game_build")
    for label, key in (("host", "host"), ("client", "client")):
        passed, detail = check_game_build(label, facts.get(f"{key}_game_build"), expected_game)
        (r.ok if passed else r.fail)(f"{label} game build", detail)

    passed, detail = check_distinct_accounts(facts.get("host_steam_id"), facts.get("client_steam_id"))
    (r.ok if passed else r.fail)("Steam accounts", detail)

    passed, detail = check_tunnel(facts.get("tunnel_port", 27051), facts.get("remote_bridge_port", 27050),
                                  facts.get("tunnel_target"))
    (r.ok if passed else r.fail)("tunnel", detail)

    for label, key in (("host", "host"), ("client", "client")):
        passed, detail = check_background(label, facts.get(f"{key}_bg"))
        (r.ok if passed else r.fail)(f"{label} background", detail)
        passed, detail = check_vsync(label, facts.get(f"{key}_vsync"), facts.get(f"{key}_game_vsync"))
        (r.ok if passed else r.fail)(f"{label} vsync", detail)
        passed, detail = check_windowed(label, *(facts.get(f"{key}_window") or (0, 640, 360)))
        (r.ok if passed else r.fail)(f"{label} window", detail)
        # The floor is a PROGRESS check, not a speed check: a copy at its deliberate 30 FPS cap must pass
        # (the cap is the policy), so the floor is 0.9x the cap. A copy that does not progress still fails.
        passed, detail = check_fps(label, facts.get(f"{key}_fps"),
                                  float(os.environ.get("TLD_PREFLIGHT_FPS", "30")) * 0.9)
        (r.ok if passed else r.fail)(f"{label} fps", detail)
    return r
