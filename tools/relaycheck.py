#!/usr/bin/env python3
"""Report (and clean up) what a two-machine relay run owns, without starting anything.

A read-only look at the three things two workers on the same machines must not share - the bridge port, the local
end of the SSH tunnel and the SSH control socket - plus what the running copies report. It launches no game, stops
no game and changes no configuration.

  tools/relaycheck.py            report what this run owns and whether it is free
  tools/relaycheck.py --cleanup   remove exactly this run's tunnel and control socket, then report
  tools/relaycheck.py --json      the same report as JSON

The report is what the preflight refuses to run on: a port held by a tunnel that points somewhere else, a control
socket another run owns.
"""
import sys, os, json, pathlib, subprocess

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import relaynet, tldenv

CLEANUP = "--cleanup" in sys.argv
AS_JSON = "--json" in sys.argv


def main():
    remote = tldenv.SECOND
    ports = relaynet.Ports(remote=remote)

    report = {"ports": ports.describe(), "checks": [], "remote": remote}

    def check(name, ok, detail):
        report["checks"].append({"check": name, "pass": bool(ok), "detail": detail})
        return ok

    # 1. the local ports this run needs
    for label, port in (("tunnel", ports.tunnel_port),):
        in_use = ports.local_port_in_use(port)
        target = relaynet.tunnel_target(port)
        stale = relaynet.tunnel_is_stale(port, ports.remote_bridge_port)
        report[f"{label}_port"] = {"port": port, "in_use": in_use, "target": target, "stale": stale}
        if stale is None and in_use:
            check(f"{label} port {port}", False, "something that is not this run's tunnel listens there")
        elif stale:
            check(f"{label} port {port}", False,
                  f"a stale tunnel to {target[0]}:{target[1]} (this run needs {ports.remote_bridge_port})")
        else:
            check(f"{label} port {port}", True, "free" if not in_use else f"this run's tunnel to {target}")

    # 2. the control socket
    sockets = sorted(str(p) for p in (pathlib.Path(ports.control_path.replace("%C", "x")).parent
                                      ).glob(f"tld-mux-{ports.name}-*")) if pathlib.Path(
        ports.control_path.replace("%C", "x")).parent.exists() else []
    report["control_sockets"] = sockets
    check("control socket", True, f"{ports.name}: " + (", ".join(sockets) if sockets else "not open"))

    # 3. the cleanup, if asked for
    if CLEANUP:
        removed = relaynet.remove_control_socket(ports)
        report["removed_sockets"] = removed
        report["cleanup"] = "this run's control socket files: " + (", ".join(removed) if removed else "none")

    if AS_JSON:
        print(json.dumps(report, indent=2))
        return 0

    print(f"relay check: remote={remote or '(none)'} name={ports.name}")
    print(f"  bridge port (this machine) : {ports.bridge_port}")
    print(f"  bridge port (second machine): {ports.remote_bridge_port}")
    print(f"  tunnel's local port         : {ports.tunnel_port}")
    print(f"  control socket              : {ports.control_path}")
    for c in report["checks"]:
        print(f"  {'OK  ' if c['pass'] else 'FAIL'} {c['check']}: {c['detail']}")
    if CLEANUP:
        print(f"  removed: {report['cleanup']}")
    return 0 if all(c["pass"] for c in report["checks"]) else 1


if __name__ == "__main__":
    sys.exit(main())
