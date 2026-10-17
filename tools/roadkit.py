"""Shared setup for tests on the beta's road with two machines (twocars, crashtest): a clear stretch of main road, a
test car on it, the player in its driver seat. All positions are GLOBAL — each machine has its own floating origin."""
import time, math

CAR01 = 2   # itemdatabase id (as spawnpile's car01)

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False


def tp(side, gx, gz, gy=None, timeout=35):
    """stand at GLOBAL (gx, gz) — on the ground there, or the floor nearest below global height gy. Where the ground
    isn't generated yet the mod holds the player at the spot (no hop, no fall) and sets it down when it is: wait for
    that. Returns the final status (on: the surface)."""
    r = side(f"tpground {gx} {gz}" + (f" {gy}" if gy is not None else ""))
    if r.get("tpground") == "waiting":
        until(lambda: not side("tpground status")["waiting"], timeout)
    st = side("tpground status")
    return {"placed": not st["waiting"], "on": st["on"], "first": r.get("tpground")}


def g(side, path):
    try: return side("get " + path)["value"]
    except Exception: return None


def road_at(side, gx, gz):
    """the nearest main-road point to GLOBAL (gx, gz): gx/gz global, yaw = the road's heading there. All positions in this
    test are global — each machine has its own floating origin, so Unity coordinates don't carry between machines."""
    side(f"tpground {gx} {gz}"); time.sleep(2)
    return side("roadpoint")


def clear_road(side, x, z, fx, fz, tries=15, step=30.0, look=None):
    """the first main-road point from (x, z) onwards along (fx, fz) whose first ~25 m of road are free of generated static
    objects (rocks, trees, poles: no rigidbody, within 10 m of the road centre, not road/terrain) — a car spawned among
    rocks gets boxed in on its first metres ("no way around the block"). `near` names the object itself (ko01…), not its
    generator container, so the test is by kind of object, not by name."""
    r, px, pz = None, x, z
    for i in range(tries):
        # follow the road: each step goes on along the road's own heading at the last point found (a straight line
        # leaves a curvy road within a few hundred metres: "no road near")
        try: rr = road_at(side, px, pz)
        except RuntimeError as e:
            if r is None: raise
            print(f"    road point {i}: {e} — back to the last road point", flush=True)
            px, pz = r["gx"] + fx * step * 0.5, r["gz"] + fz * step * 0.5
            continue
        r = rr
        hy = (math.sin(math.radians(r["yaw"])), math.cos(math.radians(r["yaw"])))
        if hy[0] * fx + hy[1] * fz < 0: hy = (-hy[0], -hy[1])
        if look is not None and look[0] * fx + look[1] * fz > 0 or look is None:
            look_now = hy
        else:
            look_now = (-hy[0], -hy[1])
        fx, fz = hy
        px, pz = r["gx"] + fx * step, r["gz"] + fz * step
        # look from the middle of the first 25 m, after the world generator has placed its objects around us (it spawns
        # them progressively after the player arrives: a quick look found nothing and the car started among rocks)
        lx, lz = look_now   # the stretch the car will DRIVE (B searches outward, the cars drive inward): along the road here
        side(f"tpground {r['gx'] + lx * 12} {r['gz'] + lz * 12}"); time.sleep(6)
        near = side("near 16")["near"]
        print(f"    road point {i}: near {[(n['root'], n['fromRoadCentre'], n['rb']) for n in near][:8]}", flush=True)
        blockers = [n for n in near if not n["rb"] and n["fromRoadCentre"] < 10
                    and not any(k in n["root"] for k in ("Road", "Terrain", "Player", "Car"))]
        if not any("Road" in n["root"] for n in near):   # nothing seen, not even the road: not standing there yet
            print(f"    road point {i}: the road isn't in view yet, looking again", flush=True); time.sleep(3)
            near = side("near 16")["near"]
            blockers = [n for n in near if not n["rb"] and n["fromRoadCentre"] < 10 and not any(k in n["root"] for k in ("Road", "Terrain", "Player", "Car"))]
            if not any("Road" in n["root"] for n in near): continue
        if not blockers: return r
        print(f"    road point {i}: {[(n['root'], n['fromRoadCentre']) for n in blockers][:4]} on the road, moving on", flush=True)
    return r


def seat_idx(side):
    seats = side("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and s["d0"])
    return next(s["i"] for s in seats if s["main"] == f"#{target}")


def car_ready(side, gx, gz, yaw, who):
    """a new car01 on the ground at GLOBAL (gx, gz) facing yaw (right lane); the player steps next to it and sits"""
    print(f"  {who} spawns:", side(f"spawncarg {CAR01} 0 {gx} {gz} {yaw}"), flush=True); time.sleep(3)
    side(f"tpground {gx + math.cos(math.radians(yaw)) * 3} {gz - math.sin(math.radians(yaw)) * 3}"); time.sleep(2)
    for _ in range(4):
        try: side(f"sitseat {seat_idx(side)} redirect")
        except Exception as e: print(f"  {who}: {e}", flush=True)
        time.sleep(1)
        if g(side, "mainscript.s.player.Bsitting") is True: break
    side("fuelup")
    ok = g(side, "mainscript.s.player.Bsitting") is True
    print(f"  {who} in the driver seat, fuelled: {ok}", flush=True)
    return ok
