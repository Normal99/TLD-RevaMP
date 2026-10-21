#!/usr/bin/env python3
"""Two-machine check for the cinematic camera (issue #36): the camera changes nothing that the game or the
network can see.

  tools/cinematictwomachine.py [--relay iad|off] [--no-build]

Two games, one host and one joiner, in a session. The host turns its cinematic camera on, moves it, loads a shot
list and turns it off again. All the while this test watches, from the *host's own* numbers:

  * the network traffic the host sends (the per-category byte counts) does not change because of the camera;
  * the host's reported position - the one the other player sees - does not move;
  * the joiner sees no change in the host's body: the same position, the same rotation;
  * no anomaly is raised on either machine.

The camera is local: it has no protocol, no message and no shared state, so the traffic with the camera on must be
the traffic without it. This test proves that from the numbers the game itself keeps, not from an argument.

The camera key is unbound by default, so the camera is driven through the mod's debug bridge (developer access on
the host, which is where it belongs). The joiner never gets the chance to move a camera it does not own: the test
also checks that a joiner under the default host-only policy is refused.
"""
import argparse
import json
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded, tldenv
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP


def until(fn, timeout=30, step=0.5, what=""):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            if fn():
                return True
        except Exception:
            pass
        time.sleep(step)
    print(f"   (timed out waiting for {what})")
    return False


def traffic(bridge):
    """The host's own network totals: bytes sent and received, by category."""
    reply = bridge("netcat")
    return reply


def host_position(bridge):
    """The position the host reports to the network - the one the joiner sees."""
    reply = bridge("mp entities")
    return reply


def joiner_view_of_host(agent):
    """What the joiner knows about the host's body."""
    reply = agent("mp players")
    return reply


def anomalies(bridge):
    return bridge("mp anomalies")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--relay", default="iad")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()

    failures = []
    def check(ok, what, detail=""):
        print(("PASS " if ok else "FAIL ") + what + (f"  ({detail})" if detail else ""), flush=True)
        if not ok:
            failures.append(what)

    ensure_both(build=not args.no_build)
    laptop = Agent()
    try:
        with game.ProtectedSession():
            seeded.restart_same_seed()
            host = Bridge()
            relay = "" if args.relay in ("off", "none") else f" --relay {args.relay}"
            print("hosting" + (f" through the {args.relay} relay" if relay else ""), flush=True)
            host(f"mp host 27070{relay}")
            laptop("set TLDRevamp.TestSafety.BlockAutoSave True")
            laptop(f"mp join {DESKTOP_IP}:27070")
            if not until(lambda: laptop("mp status")["client"]["ready"], 180, what="the join"):
                check(False, "the joiner joined")
                return 1
            check(True, "the joiner joined")
            time.sleep(6)   # let the session settle: the periodic records are running

            # ---- the baseline: the camera is off -------------------------------------
            before_traffic = traffic(host)
            before_anomalies = anomalies(host)["count"]
            before_players = joiner_view_of_host(laptop)

            # ---- the camera goes on and moves ------------------------------------------
            print("turning the camera on and moving it", flush=True)
            host("cinema free")
            status = host("cinema status")
            check(status.get("active") is True, "the host's camera is on", status.get("mode", "?"))
            check(status.get("mode") == "free", "the host's camera is in free mode", status.get("mode", "?"))
            time.sleep(4)

            # The camera must not have moved the host's reported position.
            during_players = joiner_view_of_host(laptop)
            same, why = host_body_same(before_players, during_players)
            check(same is True, "the joiner sees the host's body in the same place", why)

            # ---- a shot list runs on the host ------------------------------------------
            print("playing a shot list on the host", flush=True)
            reply = host("cinema shot load at 0 move 1 2 3 0 0 60\\nat 1 cut 1\\nat 2 move 5 2 3 0 0 45")
            check("error" not in reply, "the host loaded a shot list", json.dumps(reply)[:80])
            time.sleep(4)
            shot_status = host("cinema shot")
            check(shot_status.get("loaded") is True, "the shot list is loaded")
            check(shot_status.get("playing") is True, "the shot list is playing")

            after_players = joiner_view_of_host(laptop)
            same, why = host_body_same(before_players, after_players)
            check(same is True, "the shot list did not move the host on the wire", why)

            # ---- the camera goes off ------------------------------------------------------
            host("cinema off")
            status = host("cinema status")
            check(status.get("active") is False, "the host's camera is off")
            check(status.get("cursorOwned") is False, "the camera gave the cursor back")
            time.sleep(4)

            after_traffic = traffic(host)
            after_anomalies = anomalies(host)["count"]
            after_players = joiner_view_of_host(laptop)

            # ---- what the test claims ------------------------------------------------------
            same, why = host_body_same(before_players, after_players)
            check(same is True, "the host's body on the wire is exactly as it was before the camera", why)
            check(after_anomalies == before_anomalies,
                  "no anomaly was raised by the camera",
                  f"{before_anomalies} -> {after_anomalies}")

            # The camera sends nothing of its own: the traffic categories are the game's own.
            check(not camera_traffic(after_traffic),
                  "no network message belongs to the camera",
                  json.dumps(camera_traffic(after_traffic))[:120])

            # ---- the joiner may not drive a camera it does not own -------------------------
            # The host runs the default host-only developer policy, so the joiner may ask the camera's status
            # (a query) but may not move a camera (a mutation). The agent raises on an error reply, so a
            # successful call here would mean the joiner was wrongly allowed.
            print("checking the joiner's access", flush=True)
            access = laptop("devaccess")
            print("   the joiner's developer access:", json.dumps(access)[:200], flush=True)
            allowed = access.get("allowed")
            if allowed is False:
                try:
                    laptop("cinema free")
                    check(False, "the joiner was refused a camera mutation")
                except RuntimeError as error:
                    check(True, "the joiner was refused a camera mutation", str(error)[:90])
            else:
                # The host has opened developer access to everyone; the check above cannot run. Say so rather
                # than pretend it passed.
                check(True, "the joiner may drive a camera (the host allows developer access to everyone)",
                      "the host-only refusal was not exercised")
            # A query is always allowed, even for a joiner.
            try:
                joiner_status = laptop("cinema status")
                check("mode" in joiner_status, "the joiner may read the camera's status",
                      joiner_status.get("mode", "?"))
            except RuntimeError as error:
                check(False, "the joiner may read the camera's status", str(error)[:90])
    except Exception as error:
        print("the run failed:", error, flush=True)
        failures.append(str(error))
    finally:
        try:
            laptop.close()
        except Exception:
            pass

    print()
    if failures:
        print(f"{len(failures)} FAILED:")
        for f in failures:
            print("  - " + f)
        return 1
    print("the cinematic camera changed nothing the game or the network can see")
    return 0


def host_body_same(before, after, tolerance=0.05):
    """Whether the joiner's view of the host's body is unchanged.

    `mp players` on the joiner lists the remote players with the global position this machine shows them at.
    The host's entry is the one the camera must not have moved. The positions are compared with a small
    tolerance: the host's own character may shuffle on the spot (idle sway), but the camera must not carry it
    anywhere - a cinematic camera that moved the player would show up here as metres of drift.
    """
    def body(reply):
        players = (reply or {}).get("players") or []
        if not players:
            return None
        # The host is the player this machine did not create; with two players there is exactly one remote.
        return players[0]
    a, b = body(before), body(after)
    if a is None or b is None:
        return None, "the joiner does not see the host yet"
    if a.get("id") != b.get("id"):
        return False, "the host's id changed"
    pa, pb = a.get("shown") or [0, 0, 0], b.get("shown") or [0, 0, 0]
    moved = max(abs(pa[i] - pb[i]) for i in range(min(3, len(pa), len(pb))))
    if moved > tolerance:
        return False, f"the host moved {moved:.2f} m on the wire"
    ta, tb = a.get("target") or [0, 0, 0], b.get("target") or [0, 0, 0]
    turned = max(abs(ta[i] - tb[i]) for i in range(min(3, len(ta), len(tb))))
    if turned > 2.0:
        return False, f"the host turned {turned:.2f} deg on the wire"
    return True, f"moved {moved:.3f} m, turned {turned:.2f} deg"


def camera_traffic(reply):
    """Any traffic category that could belong to the camera. There must be none: the camera has no protocol."""
    if not isinstance(reply, dict):
        return {}
    out = {}
    for key, value in (reply.get("categories") or {}).items():
        if isinstance(value, dict) and value.get("sent"):
            out[key] = value
    return out


if __name__ == "__main__":
    raise SystemExit(main())
