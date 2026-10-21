#!/usr/bin/env python3
"""Horizon diagnostics for issue #37 (bridge `terrainhorizon`).

  tools/terrainhorizon.py [--port 27050] [--x GLOBAL_X --z GLOBAL_Z] [--out bench/horizon.json]

Reads only: near/distant terrain generators, the far mesh generator, camera clipping and the roads around the point.
Without coordinates, the snapshot uses the local player's global position. Run `--compare old.json new.json` to show
whether tile/road coverage moved toward the camera. This is structural evidence, not a visual proof; screenshots and
rendering timings remain required.
"""
import argparse
import datetime
import json
import pathlib
import socket
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def command(port, text):
    with socket.create_connection(("127.0.0.1", port), timeout=35) as sock:
        stream = sock.makefile("rwb")
        stream.write((text + "\n").encode("utf-8"))
        stream.flush()
        line = stream.readline(4 * 1024 * 1024)
    if not line:
        raise RuntimeError("bridge disconnected")
    reply = json.loads(line)
    if not isinstance(reply, dict) or "error" in reply:
        raise RuntimeError("bridge refused terrainhorizon: " + str(reply.get("error") if isinstance(reply, dict) else reply))
    return reply


def covered(snapshot):
    near = snapshot.get("near", [])
    roads = snapshot.get("roads", {})
    far = snapshot.get("farGenerator") or {}
    return {
        "nearPresent": sum(int(g.get("present", 0)) for g in near),
        "nearPending": sum(int(g.get("pending", 0)) for g in near),
        "roadChunks": int(roads.get("chunks", 0)),
        "roadsGenerated": int(roads.get("generated", 0)),
        "roadsPlaced": int(roads.get("placedElements", 0)),
        "farMeshes": int(far.get("meshes", 0)),
        "farVertices": int(far.get("vertices", 0)),
    }


def compare(before, after):
    a, b = covered(before), covered(after)
    print("horizon coverage:")
    for key in a:
        print(f"  {key:16} {a[key]:8} -> {b[key]:8} ({b[key] - a[key]:+})")
    print("visual clipping:")
    for key in ("near", "farGenerator"):
        if key == "farGenerator":
            old = before.get("farGenerator") or {}
            new = after.get("farGenerator") or {}
            print(f"  farViewDistance {old.get('farViewDistance')} -> {new.get('farViewDistance')}")
    old_cam = {c.get("role"): c for c in before.get("cameras", [])}
    new_cam = {c.get("role"): c for c in after.get("cameras", [])}
    for role in sorted(set(old_cam) | set(new_cam)):
        print(f"  {role:8} {old_cam.get(role)} -> {new_cam.get(role)}")
    return a, b


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=27050)
    parser.add_argument("--x", type=float)
    parser.add_argument("--z", type=float)
    parser.add_argument("--out")
    parser.add_argument("--compare", nargs=2, metavar=("BEFORE", "AFTER"))
    args = parser.parse_args()
    if args.compare:
        before = json.loads(pathlib.Path(args.compare[0]).read_text(encoding="utf-8"))
        after = json.loads(pathlib.Path(args.compare[1]).read_text(encoding="utf-8"))
        compare(before, after)
        return 0
    if (args.x is None) != (args.z is None):
        parser.error("give both --x and --z or neither")
    text = "terrainhorizon" if args.x is None else f"terrainhorizon {args.x:.17g} {args.z:.17g}"
    snapshot = command(args.port, text)
    snapshot["capturedAt"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    out = pathlib.Path(args.out) if args.out else ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-horizon.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(snapshot, indent=1), encoding="utf-8")
    print(json.dumps({"out": str(out), "coverage": covered(snapshot)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
