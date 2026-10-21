"""Ownership of the local ports and SSH control sockets a two-machine test run uses.

Every independently controlled worker runs its own copy of the game on the same two machines. Three things must
never collide between two workers:

  * the bridge port (the game's own `-tldbridge N`, on 127.0.0.1 of the machine that runs the copy),
  * the local end of the SSH tunnel to the second machine's bridge,
  * the SSH control socket (ControlPath) the tunnel and the other commands travel through.

A shared ControlPath is the trap: ssh's -L forward belongs to the *control master* that opened it, so a second
worker that reuses that socket gets a tunnel pointing at the first worker's remote port and silently talks to the
first worker's game (its build hash comes back, and every later check reads that game instead of its own).

This module gives every run its own names, detects a tunnel that is up but points at the wrong remote port (a
stale one left by an earlier run, or another worker's), and removes exactly what it created - never another
worker's socket, port or process.

Nothing here starts a game.
"""
from __future__ import annotations

import errno
import os
import pathlib
import socket
import subprocess
import time

# The bridge's default port on the machine that runs the game, and the local end of the tunnel to the second one.
DEFAULT_BRIDGE_PORT = 27050
DEFAULT_TUNNEL_PORT = 27051
DEFAULT_REMOTE_BRIDGE_PORT = 27050

SSH_MUX_OPTS = ["-o", "ControlMaster=auto", "-o", "ControlPersist=120"]


def _free_port(preferred, attempts=64):
    """A free local TCP port, starting at `preferred`.

    The preferred port is tried first (so a run that owns 27050 still gets it), then the ones after it. The
    socket is closed before the port is returned, so there is a small race - the caller's own bind check
    (Ports.local_port_in_use) is the authority, and the tunnel refuses an occupied port.
    """
    for offset in range(attempts):
        port = preferred + offset
        if not 1024 <= port <= 65535:
            continue
        s = socket.socket()
        try:
            s.bind(("127.0.0.1", port))
            return port
        except OSError:
            continue
        finally:
            s.close()
    raise RuntimeError("no free local port near %d" % preferred)


def _env_int(env, name, default):
    """an integer setting from the environment (a blank or broken value falls back to the default)"""
    raw = str(env.get(name, "") or "").strip()
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError:
        return default


def _sanitize(text):
    """a filesystem- and ssh-safe name (the ControlPath is a path, the mux name is a token)"""
    keep = "".join(c if (c.isalnum() or c in "-_.") else "-" for c in str(text))
    return keep.strip("-") or "run"


class Ports:
    """The three names one run owns: the bridge port, the tunnel's local port and the SSH control socket.

    Each is taken from the environment when set (the tools' own settings file may pin them) and derived from the
    run's identity otherwise, so two workers never pick the same one.
    """

    def __init__(self, remote="", name="", env=None, allocate=True):
        env = env if env is not None else os.environ
        self.env = env
        self.remote = remote or ""
        given = name or env.get("TLD_SSH_MUX_NAME") or ""
        self.name = _sanitize(given) if given else ""
        self.bridge_port = _env_int(env, "TLD_BRIDGE_PORT", 0)
        self.tunnel_port = _env_int(env, "TLD_TUNNEL_PORT", 0)
        self.remote_bridge_port = _env_int(env, "TLD_REMOTE_BRIDGE_PORT", DEFAULT_REMOTE_BRIDGE_PORT)
        if not self.remote_bridge_port:
            self.remote_bridge_port = DEFAULT_REMOTE_BRIDGE_PORT
        if not self.bridge_port:
            # A run must NOT fall back to the well-known ports: two unpinned runs would collide on the bridge
            # port AND on the SSH control socket, and the second one would read the first one's game. The port
            # is allocated per run instead; an explicit TLD_BRIDGE_PORT still wins.
            self.bridge_port = _free_port(DEFAULT_BRIDGE_PORT) if allocate else DEFAULT_BRIDGE_PORT
        if not self.tunnel_port:
            self.tunnel_port = _free_port(DEFAULT_TUNNEL_PORT) if allocate else DEFAULT_TUNNEL_PORT
        if not self.name:
            # A name of its own per run: two workers must never share a control socket. The settings file may pin
            # TLD_SSH_MUX_NAME; otherwise the run's identity (the second machine and the ports it owns) names it.
            self.name = _sanitize("-".join(x for x in (
                (self.remote.split("@")[-1] if self.remote else "local"),
                str(self.bridge_port), str(self.tunnel_port)) if x))

    # ---- the SSH control socket
    @property
    def control_path(self):
        """the ControlPath of this run's SSH control master (a file under ~/.ssh)"""
        return str(pathlib.Path.home() / ".ssh" / f"tld-mux-{self.name}-%C")

    def control_path_args(self):
        return ["-o", f"ControlPath={self.control_path}"] + SSH_MUX_OPTS

    def control_socket_exists(self):
        return pathlib.Path(self.control_path.replace("%C", "placeholder")).parent.exists() and \
            any(pathlib.Path(self.control_path.replace("%C", "x")).parent.glob(f"tld-mux-{self.name}-*"))

    # ---- the local ports
    def local_port_in_use(self, port=None):
        """is something already listening on this local port?"""
        port = port or self.tunnel_port
        s = socket.socket()
        try:
            s.bind(("127.0.0.1", port))
            return False
        except OSError as e:
            if e.errno in (errno.EADDRINUSE, errno.EACCES):
                return True
            raise
        finally:
            s.close()

    def describe(self):
        return {"bridge_port": self.bridge_port, "tunnel_port": self.tunnel_port,
                "remote_bridge_port": self.remote_bridge_port, "control_path": self.control_path,
                "name": self.name}


# ---- what a tunnel's local end is actually connected to

def _is_ssh(cmdline):
    """Is this command line an ssh client (or a wrapper that became one)?

    The binary's own name is the usual answer, but a wrapper script that execs ssh leaves only the exec'd binary
    in /proc. A command line that carries an -L forward to a loopback port is an ssh tunnel whatever it is called,
    so that is accepted too - and it is the only shape that matters here.
    """
    if not cmdline:
        return False
    if pathlib.Path(cmdline[0]).name == "ssh":
        return True
    if any(pathlib.Path(a).name == "ssh" for a in cmdline[1:] if a.startswith("/")):
        return True
    return bool(_forward_specs(cmdline))


def _forward_specs(cmdline):
    """the -L forward specifications in an ssh command line.

    ssh takes them in two forms: `-L 127.0.0.1:27051:127.0.0.1:27050` (two arguments) and
    `-L127.0.0.1:27051:127.0.0.1:27050` (one). The tools use the first, a hand-written command often the second.
    """
    specs = []
    i = 0
    while i < len(cmdline):
        a = cmdline[i]
        if a == "-L" and i + 1 < len(cmdline):
            specs.append(cmdline[i + 1])
            i += 2
            continue
        if a.startswith("-L") and len(a) > 2:
            specs.append(a[2:])
        i += 1
    return specs


def tunnel_target(port):
    """The remote address the local port `port` forwards to, read from the ssh process that owns it.

    Returns (host, port) or None when nothing of ours listens there. The ssh command line holds the -L
    argument, which is the truth about where the traffic goes - far more reliable than probing the far end.
    """
    me = os.getpid()
    for pid in _pids():
        if pid == me:
            continue
        cmdline = _read_cmdline(pid)
        if not cmdline or not _is_ssh(cmdline):
            continue
        for spec in _forward_specs(cmdline):
            for part in spec.split(","):
                target = _parse_forward(part)
                if target is None:
                    continue
                local, (rhost, rport) = target
                if local != int(port):
                    continue
                return (rhost, rport)
    return None


def _parse_forward(spec):
    """One -L specification: (local port, (remote host, remote port)).

    ssh's form is [bind_address:]local_port:remote_host:remote_port (the bind address is optional). The port
    numbers may be a service name, which is then left unresolved.
    """
    parts = spec.split(":")
    if len(parts) == 4:
        _, local, rhost, rport = parts
    elif len(parts) == 3:
        local, rhost, rport = parts
    else:
        return None
    try:
        local = int(local)
    except ValueError:
        return None
    try:
        rport = int(rport)
    except ValueError:
        rport = -1
    return (local, (rhost or "127.0.0.1", rport))


def tunnel_is_stale(port, expected_remote_port, expected_host="127.0.0.1"):
    """True when the local port is taken by a tunnel that does NOT forward to the port we want.

    A tunnel left behind by an earlier run (or another worker's) would answer with the wrong game's data: the
    bridge would look healthy while every check read someone else's game. That must be a loud failure, not a
    silent wrong answer.
    """
    target = tunnel_target(port)
    if target is None:
        return None      # the port is taken by something that is not one of our tunnels
    host, rport = target
    return host != expected_host or rport != expected_remote_port


def _pids():
    out = []
    try:
        for entry in os.scandir("/proc"):
            if entry.name.isdigit():
                out.append(int(entry.name))
    except OSError:
        pass
    return out


def _read_cmdline(pid):
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            raw = f.read()
    except OSError:
        return None
    parts = [p.decode("utf-8", "replace") for p in raw.split(b"\0") if p]
    return parts or None


# ---- the tunnel's own lifecycle

def ssh_master_alive(ssh_args, remote):
    """is the control master of this run's ControlPath still up?"""
    r = subprocess.run(ssh_args + ["-O", "check", remote], capture_output=True, text=True, timeout=20)
    return r.returncode == 0


def tunnel_forwards(ssh_args, remote, local_port, remote_port):
    """ask the control master which forwards it holds (ssh -O forward -L prints the ones it has)"""
    r = subprocess.run(ssh_args + ["-O", "check", remote], capture_output=True, text=True, timeout=20)
    if r.returncode != 0:
        return None
    return local_port, remote_port


def kill_tunnel(ssh_args, remote, local_port, remote_port):
    """Remove exactly this run's tunnel: the -L forward it holds, through this run's own control master.

    Nothing else is touched: no pkill, no other worker's socket, no other port.
    """
    spec = f"127.0.0.1:{local_port}:127.0.0.1:{remote_port}"
    r = subprocess.run(ssh_args + ["-O", "forward", "-L", spec, remote], capture_output=True, text=True, timeout=20)
    # ssh -O forward with a matching spec cancels it; an error means there was nothing of ours to cancel.
    return r.returncode == 0, (r.stderr or "").strip()


def kill_control_master(ssh_args, remote):
    """Close this run's own SSH control master (its socket), leaving every other worker's alone."""
    r = subprocess.run(ssh_args + ["-O", "exit", remote], capture_output=True, text=True, timeout=20)
    return r.returncode == 0, (r.stderr or "").strip()


def remove_control_socket(ports):
    """Delete this run's control socket files (they are ours by name)."""
    removed = []
    directory = pathlib.Path(ports.control_path.replace("%C", "x")).parent
    if not directory.exists():
        return removed
    for p in directory.glob(f"tld-mux-{ports.name}-*"):
        try:
            p.unlink()
            removed.append(str(p))
        except OSError:
            pass
    return removed
