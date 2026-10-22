"""Target-side test launch/ownership guard. No shell or Steam fallback.

The source is bundled by launchguard for SSH, so both machines execute identical checks.
Leases persist across controller processes; PID start ticks prevent reuse after a crash.
"""
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import shlex
import shutil
import signal
import socket
import subprocess
import sys
import time
import uuid

import testdisplay

# The admission broker's overrides: the broker reads its gauges from these paths,
# and the compositor's dispatch does not pass the environment on, so the launcher
# sets them inside the command line.
ADMIT_VARS = ('TLD_ADMIT_RUNTIME_DIR', 'TLD_ADMIT_MEMINFO', 'TLD_ADMIT_WATCHED_CGROUP',
              'TLD_ADMIT_PSI', 'TLD_ADMIT_VMSTAT', 'TLD_ADMIT_TEST_NO_WATCH')


def canonical(value):
    value = os.path.expandvars(os.path.expanduser(str(value or '')))
    if not value or not pathlib.Path(value).is_absolute() or '$' in value:
        raise RuntimeError('explicit absolute test paths required')
    return pathlib.Path(value).resolve(strict=True)


def identity(spec):
    game, compat, steam = (canonical(spec[k]) for k in ('game', 'compat', 'steam'))
    port = spec.get('port')
    if type(port) is not int or not 1024 <= port <= 65535:
        raise RuntimeError('explicit bridge port (1024..65535) required')
    owner = steam / 'steamapps/common/The Long Drive'
    owner_prefix = steam / 'steamapps/compatdata/1017180'
    for path in (game, compat):
        if any(path == p.resolve() or p.resolve() in path.parents for p in (owner, owner_prefix)):
            raise RuntimeError('owner install/prefix refused')
        if ('/steamapps/common/' in str(path) or '/steamapps/compatdata/1017180' in str(path)
                or path.name.lower() == 'test' or any(part.lower() in ('shared', 'tld-revamp-testprefix') for part in path.parts)
                or '/test/game' in str(path)):
            raise RuntimeError('shared test resources refused')
    if game == compat or game in compat.parents or compat in game.parents:
        raise RuntimeError('game and prefix must be separate')
    if not (game / 'TheLongDrive.exe').is_file() or not (compat / 'pfx').is_dir():
        raise RuntimeError('dedicated game and initialized prefix required')
    if canonical(spec.get('prefix', str(compat / 'pfx'))) != compat / 'pfx':
        raise RuntimeError('configured saves prefix must match the dedicated Proton prefix')
    return dict(spec, game=str(game), compat=str(compat), steam=str(steam))


def bridge_args(args, port):
    args = list(args)
    if any(a.lower().startswith(('-screen-', '-fullscreen', '-window-mode', '-batchmode', '-nographics')) for a in args):
        raise RuntimeError('display arguments are owned by the launch profile')
    flags = [i for i, a in enumerate(args) if a.lower().startswith('-tldbridge')]
    if flags:
        if len(flags) != 1 or args[flags[0]] != '-tldbridge' or flags[0]+1 >= len(args) or args[flags[0]+1] != str(port):
            raise RuntimeError('bridge arguments must match the reserved port exactly')
        i = flags[0]; del args[i:i+2]
    return args + ['-tldbridge', str(port)]


import os


def game_argument(argv):
    """Return the actual game argument, or an empty string for wrapper processes."""
    if not argv:
        return ''

    def basename(value):
        return str(value).replace('\\', '/').rsplit('/', 1)[-1]

    if basename(argv[0]) == 'TheLongDrive.exe':
        return argv[0]

    wine_names = {
        'wine', 'wine64', 'wine-preloader', 'wine64-preloader'
    }
    if (basename(argv[0]) in wine_names and len(argv) > 1
            and basename(argv[1]) == 'TheLongDrive.exe'):
        return argv[1]

    return ''


def processes():
    result = []
    for proc in pathlib.Path('/proc').iterdir():
        if not proc.name.isdigit():
            continue
        try:
            if proc.stat().st_uid != os.getuid():
                continue
            start = (proc / 'stat').read_text().rsplit(')', 1)[1].split()[19]
            env = dict(x.split(b'=', 1) for x in (proc / 'environ').read_bytes().split(b'\0') if b'=' in x)
            argv = (proc / 'cmdline').read_bytes().decode(errors='replace').split('\0')
            actual_game = game_argument(argv)
            if actual_game:
                actual_game = os.path.realpath(os.path.dirname(actual_game)) if actual_game.startswith('/') else os.path.realpath(proc / 'cwd')
            def env_path(name):
                value = os.fsdecode(env.get(name, b''))
                return os.path.realpath(value) if value else ''
            if start != (proc / 'stat').read_text().rsplit(')', 1)[1].split()[19]:
                raise RuntimeError('process identity changed during scan')
            result.append({'pid': int(proc.name), 'start': start,
                           'game': env_path(b'STEAM_COMPAT_INSTALL_PATH'),
                           'compat': env_path(b'STEAM_COMPAT_DATA_PATH'), 'wineprefix': env_path(b'WINEPREFIX'),
                           'executable_game': actual_game,
                           'token': os.fsdecode(env.get(b'TLD_TEST_LAUNCH_TOKEN', b'')), 'argv': argv})
        except (OSError, ValueError):
            continue
    return result


def matching(spec, procs):
    return [p for p in procs if p['game'] == spec['game'] or p['compat'] == spec['compat']
            or p.get('executable_game') == spec['game'] or p.get('wineprefix') == spec['compat']+'/pfx']


def owned(spec, lease, procs):
    return [p for p in procs if p['game'] == spec['game'] and p['compat'] == spec['compat']
            and p['token'] == lease['token'] and [p['pid'], p['start']] in lease['pids']]


@contextlib.contextmanager
def registry():
    root = pathlib.Path(f'/run/user/{os.getuid()}/tld-test-launches')
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (root / 'lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        yield root


def key(spec):
    return hashlib.sha256(json.dumps([spec[k] for k in ('game', 'compat', 'port')]).encode()).hexdigest()


def free_port(port):
    with socket.socket() as sock:
        # a listener refuses this bind; the TIME_WAIT/FIN_WAIT of a game's closed bridge connections does not
        # (the port stayed "occupied" for a minute after every quit)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(('127.0.0.1', port))
        except OSError as exc:
            raise RuntimeError('bridge port occupied; refusing reuse') from exc


def lease_for(root, spec):
    path = root / (key(spec) + '.json')
    if not path.exists():
        raise RuntimeError('no guarded launch lease; foreign/visible reuse refused')
    lease = json.loads(path.read_text())
    if any(lease[k] != spec[k] for k in ('game', 'compat', 'port')):
        raise RuntimeError('lease identity mismatch')
    return path, lease


def forward_owner(root, port):
    """Which lease owns a forwarded local port, if any.

    The registry is the shared record of who owns what on this machine.  A forward
    port that another lease already claims is a *known* foreign listener and is
    refused differently from an unknown one: the first is another lane's tunnel
    that we must not connect to, the second is simply occupied.
    """
    for path in root.glob('*.json'):
        try:
            lease = json.loads(path.read_text())
        except (OSError, ValueError):
            continue
        forwards = lease.get('forwards') or {}
        if str(port) in {str(p) for p in forwards}:
            return lease
    return None


def claim_forward(root, spec, port):
    """Record that this lease owns a forwarded local port; refuse a foreign one."""
    owner = forward_owner(root, port)
    path = root / (key(spec) + '.json')
    if owner is not None:
        # The same lease (this copy, prefix and port: the same registry file) re-claims its own port. A bridge that
        # reconnects claims with the copy's spec, which carries no launch token, so the file decides, not the token.
        if owner.get('token') == spec.get('token') or key(owner) == key(spec):
            return owner
        raise RuntimeError('the forwarding port belongs to another lease; refused')
    lease = json.loads(path.read_text()) if path.exists() else dict(spec)
    forwards = dict(lease.get('forwards') or {})
    forwards[str(port)] = {'remote': spec.get('remote'), 'destination': spec.get('port')}
    lease['forwards'] = forwards
    path.write_text(json.dumps(lease))
    return lease


def release_forwards(root, spec):
    """Drop this lease's forwarding claims; other leases' claims are untouched."""
    path = root / (key(spec) + '.json')
    if not path.exists():
        return
    try:
        lease = json.loads(path.read_text())
    except (OSError, ValueError):
        return
    if not lease.get('forwards'):
        return
    lease['forwards'] = {}
    path.write_text(json.dumps(lease))


def check_windows(spec, env, pids):
    testdisplay.preflight(env)
    clients = testdisplay.query('clients', env)
    windows = [c for c in clients if c.get('pid') in pids]
    if spec.get('headless'):
        # A headless launch draws into gamescope's own nested display: no window of it may reach this desktop.
        if windows:
            raise RuntimeError('headless launch has a window on the desktop; refused')
        return
    if not windows:
        raise RuntimeError('no owned hidden window proven')
    for c in windows:
        if ((c.get('workspace') or {}).get('name') != testdisplay.WORKSPACE
                or c.get('focusHistoryID') == 0 or c.get('fullscreen') or not c.get('floating')
                or c.get('size') != list(testdisplay.profile(spec['profile']).values())):
            raise RuntimeError('visible/focused/foreign window refused; no post-launch move permitted')
    active = testdisplay.query('activewindow', env)
    if active.get('pid') in pids:
        raise RuntimeError('test window is active')


def check_compositor_token(pids):
    """The launched process must still carry the compositor's per-launch token.

    Hyprland matches a per-launch rule on the window process's environment, so a
    process without the token was never covered by the hidden/no-focus rule.  This
    is the proof that the launch-time containment reached the game itself and not
    only the command that started it."""
    for pid in pids:
        try:
            raw = (pathlib.Path('/proc') / str(pid) / 'environ').read_bytes()
        except OSError:
            raise RuntimeError('the launched process environment is not readable')
        env = dict(x.split(b'=', 1) for x in raw.split(b'\0') if b'=' in x)
        if not env.get(b'HL_EXEC_RULE_TOKEN'):
            raise RuntimeError('the launched process carries no compositor rule token')
    return True


def check_listener(port, pids):
    inodes = set()
    for name in ('tcp', 'tcp6'):
        for line in pathlib.Path('/proc/net', name).read_text().splitlines()[1:]:
            fields = line.split()
            if int(fields[1].split(':')[1], 16) == port and fields[3] == '0A':
                inodes.add('socket:[' + fields[9] + ']')
    for pid in pids:
        try:
            fds = list(pathlib.Path('/proc', str(pid), 'fd').iterdir())
        except OSError:
            continue
        for fd in fds:
            # one descriptor closed while the game's descriptors are read (a Wine game opens and closes them all the
            # time) failed the whole check: the bridge refused mid-test, its listener still there (2026-10-22, 4 runs)
            try:
                if os.readlink(fd) in inodes:
                    return
            except OSError:
                continue
    raise RuntimeError('bridge listener is not owned by the leased game PID')


def validate(spec, root, require_window=True):
    _, lease = lease_for(root, spec)
    procs = processes()
    have = owned(spec, lease, procs)
    if matching(spec, procs) != have:
        raise RuntimeError('resource has foreign or changed process identity')
    # The game itself, not its wrappers: under Proton the game's argv[0] is a Windows path (Z:\\...\\TheLongDrive.exe)
    # and its window belongs to that process, while Proton's steam.exe wrapper carries the Unix path as an argument.
    spec = dict(spec, headless=bool(lease.get('headless')))
    games = [p for p in have if game_argument(p['argv'])
             and '-tldbridge' in p['argv'] and p['argv'].count('-tldbridge') == 1
             and p['argv'][p['argv'].index('-tldbridge')+1] == str(spec['port'])]
    if not games:
        raise RuntimeError('no leased game process')
    if require_window:
        check_windows(spec, testdisplay.hypr_env(), [p['pid'] for p in games])
        check_compositor_token([p['pid'] for p in games])
        # Under Wine the listening socket is held by the prefix's wineserver, which is part of this same lease
        # (same token, game directory and prefix), so the listener may belong to the game or to that server.
        servers = [p['pid'] for p in have if p['argv'] and str(p['argv'][0]).replace('\\', '/').rsplit('/', 1)[-1] == 'wineserver']
        check_listener(spec['port'], [p['pid'] for p in games] + servers)
    return have


def signal_owned(spec, root, action):
    path, lease = lease_for(root, spec)
    procs = processes()
    have = owned(spec, lease, procs)
    if matching(spec, procs) != have:
        raise RuntimeError('cleanup refused: foreign or changed PID/start identity')
    if action != 'cleanup':
        have = [p for p in have if game_argument(p['argv'])]
        if not have:
            raise RuntimeError('no owned game to signal')
    sig = {'cleanup': signal.SIGTERM, 'crash': signal.SIGKILL,
           'pause': signal.SIGSTOP, 'resume': signal.SIGCONT}[action]
    for p in have:
        # A pidfd pins the process across the final start-time check and signal.
        fd = os.pidfd_open(p['pid'])
        try:
            current = owned(spec, lease, processes())
            if not any(q['pid'] == p['pid'] and q['start'] == p['start'] for q in current):
                raise RuntimeError('process identity changed before cleanup')
            signal.pidfd_send_signal(fd, sig)
        finally:
            os.close(fd)
    return {'signalled': len(have)}


def broker_path():
    """The admission broker on this machine: an explicit override, the built one, or PATH.

    The broker is the memory/admission barrier of the admission lane; a launch
    that cannot be admitted is not a launch this tooling may perform."""
    override = os.environ.get('TLD_ADMIT_BIN')
    candidates = [pathlib.Path(override)] if override else []
    here = pathlib.Path(__file__).resolve().parent
    candidates.append(here / 'tld-admit')
    # The broker belongs to the admission lane; it may live in another checkout.
    candidates.extend(sorted(here.parent.parent.glob('tld-revamp-free-*/tools/tld-admit')))
    search = os.environ.get('TLD_ADMIT_SEARCH_DIR')
    if search:
        candidates.append(pathlib.Path(search) / 'tld-admit')
    # The target looks in its own worktrees: the broker is built in the admission lane.
    for base in sorted(pathlib.Path.home().glob('tld-revamp-free-*/tools/tld-admit')):
        candidates.append(base)
    for candidate in candidates:
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return candidate
    found = shutil.which('tld-admit')
    if found:
        return pathlib.Path(found)
    raise RuntimeError('no admission broker; a raw launch is not an admitted launch')


def broker_name(spec, token):
    """The broker job name: the required `game-<short-unique-token>` form.

    The spec pins this prefix: every admission unit must be tld-job-game-*, so a
    stray non-game unit is visible and the one-game-unit rule is checkable. The
    token half is a 64-bit truncation of the per-launch UUID, which is unique per
    dispatch and never reused from the port or the directory."""
    name = 'game-%s' % token[:16]
    if len(name) > 64 or not all(c.isalnum() or c in '._-' for c in name):
        raise RuntimeError('the broker job name is not a plain identifier')
    return name


def shim_path():
    """The shim that carries the compositor token across the systemd boundary.

    On the local machine it sits next to this module; on a remote target the
    controller ships it and names it here."""
    override = globals().get('SHIM_PATH')
    path = pathlib.Path(override) if override else pathlib.Path(__file__).resolve().parent / 'launchshim.py'
    if not path.is_file():
        raise RuntimeError('the launch shim is missing; refusing an uncontained launch')
    return path


def compose(spec, token, env):
    """compositor dispatch -> admission broker -> systemd unit -> the game.

    The dispatch wraps the broker, so the compositor's per-launch rule covers the
    whole tree.  The broker starts the game in its own systemd unit; the shim
    carries the compositor's rule token and the launch token across that boundary,
    because systemd-run does not pass the caller's environment into the unit.
    """
    # The synthetic proof injects its own harmless GUI process here; the game
    # launch always uses the Proton command below.
    if spec.get('command'):
        game_command = list(spec['command'])
        inner = [sys.executable, str(shim_path()), '--inner', spec['tokenfile'],
                 '--cwd', spec['game'], '--log', spec['compat'] + '/launch.log', '--'] + game_command
        broker = [str(broker_path()), 'run', '--profile', 'game', '--name', broker_name(spec, token), '--'] + inner
        assignments = ['TLD_TEST_LAUNCH_TOKEN=' + token]
        for name in ADMIT_VARS:
            value = env.get(name)
            if value:
                assignments.append(name + '=' + value)
        return ['env'] + assignments + [
            sys.executable, str(shim_path()), '--outer', spec['tokenfile'],
            '--exitfile', spec['exitfile'], '--'] + broker
    game_command = ['env'] + [
        'STEAM_COMPAT_DATA_PATH=' + spec['compat'],
        'STEAM_COMPAT_INSTALL_PATH=' + spec['game'],
        'STEAM_COMPAT_CLIENT_INSTALL_PATH=' + spec['steam'],
        'SteamAppId=1017180', 'SteamGameId=1017180', 'STEAM_COMPAT_APP_ID=1017180',
        'STEAM_COMPAT_LIBRARY_PATHS=' + spec['steam'] + '/steamapps',
        'STEAM_COMPAT_TOOL_PATHS=' + spec['proton'] + ':' + spec['slr'],
        'STEAM_COMPAT_MOUNTS=' + spec['steam'] + '/steamapps/common/Steamworks Shared:' + spec['proton'] + ':' + spec['slr'],
        'STEAM_COMPAT_PROTON=1', 'SteamClientLaunch=1', 'SteamEnv=1',
        'SteamOverlayGameId=1017180', 'WINEDLLOVERRIDES=winhttp=n,b',
        'STEAM_COMPAT_FLAGS=search-cwd', 'TLD_TEST_LAUNCH_TOKEN=' + token,
        str(canonical(spec['slr']) / '_v2-entry-point'), '--verb=waitforexitandrun', '--',
        str(canonical(spec['proton']) / 'proton'), 'waitforexitandrun',
        spec['game'] + '/TheLongDrive.exe'] + list(spec.get('flags', [])) + list(spec.get('args', []))
    if spec.get('headless'):
        # Inside the unit, so the nested compositor and its Xwayland belong to the same contained job.
        game_command = testdisplay.headless_prefix(spec['profile']) + game_command
    inner = [sys.executable, str(shim_path()), '--inner', spec['tokenfile'],
             '--cwd', spec['game'], '--log', spec['compat'] + '/launch.log', '--'] + game_command
    broker = [str(broker_path()), 'run', '--profile', 'game', '--name', broker_name(spec, token), '--'] + inner
    # The compositor's dispatch runs the command through its own shell and passes no
    # environment on, so everything the child needs is set inside the command line:
    # the launch token, and the admission broker's own overrides.
    assignments = ['TLD_TEST_LAUNCH_TOKEN=' + token]
    for name in ADMIT_VARS:
        value = env.get(name)
        if value:
            assignments.append(name + '=' + value)
    outer = ['env'] + assignments + [
             sys.executable, str(shim_path()), '--outer', spec['tokenfile'],
             '--exitfile', spec['exitfile'], '--'] + broker
    return outer


def command_line(outer):
    """The composed launch as one shell command line for the compositor dispatch.

    Every argument is quoted; the line carries no environment value of its own."""
    return shlex.join(outer)


def read_verdict(exitfile):
    """The broker's exit code, once it has written one; None while it still runs."""
    try:
        text = pathlib.Path(exitfile).read_text().strip()
    except OSError:
        return None
    try:
        return int(text)
    except ValueError:
        return None


def launch(spec, root):
    env = testdisplay.hypr_env()
    args = bridge_args(spec.get('args', []), spec['port'])
    for path in root.glob('*.json'):
        other = json.loads(path.read_text())
        if any(other[k] == spec[k] for k in ('game', 'compat', 'port')):
            if any(other[k] != spec[k] for k in ('game', 'compat', 'port')):
                raise RuntimeError('game directory, prefix and port are permanently bound to another identity')
    if matching(spec, processes()):
        raise RuntimeError('already-running resources refused')
    free_port(spec['port'])
    flags = testdisplay.prepare(spec['game'], spec['compat'], spec['profile'])
    token = uuid.uuid4().hex
    tokenfile = root / ('token-%s' % token)
    exitfile = root / ('exit-%s' % token)
    spec = dict(spec, flags=list(flags), args=list(args), tokenfile=str(tokenfile), exitfile=str(exitfile),
                headless=testdisplay.headless_wanted(spec['profile'] == 'remote'))
    # The compositor's dispatch runs the command through a shell of its own and does
    # not pass the caller's environment on, so the launch token is set inside the
    # command line, not in the dispatcher's environment.
    command = command_line(compose(spec, token, env))
    lease = dict(spec, token=token, pids=[])
    path = root / (key(spec) + '.json')
    path.write_text(json.dumps(lease))
    # Dispatch is the only process launch boundary. Recheck exactly this compositor immediately before dispatch.
    wrapped = testdisplay.background_command(command, spec['profile'], env)
    try:
        result = subprocess.run(wrapped, shell=True, env=env, capture_output=True, text=True, timeout=15)
        if result.returncode or result.stdout.strip() != 'ok':
            raise RuntimeError('hidden dispatch failed; inspect the reserved copy, no fallback permitted')
        # Proton needs longer than the dispatch itself on a loaded machine: the real guarded smoke returned
        # "proof timed out" after 20 s while the launch had in fact succeeded. The timeout is a bound on the WAIT,
        # not a licence to reuse a copy: the lease is retained and the fail-closed refusal is unchanged.
        deadline = time.monotonic() + float(os.environ.get('TLD_LAUNCH_PROOF_TIMEOUT', '120'))
        while time.monotonic() < deadline:
            have = [p for p in processes() if p['token'] == token and p['game'] == spec['game'] and p['compat'] == spec['compat']]
            lease['pids'] = [[p['pid'], p['start']] for p in have]
            path.write_text(json.dumps(lease))
            try:
                validate(spec, root)
                return {'launched': True}
            except RuntimeError:
                # The dispatch returns before the broker decided: an admission refusal
                # is a refusal, not a slow start.
                verdict = read_verdict(exitfile)
                if verdict is not None and verdict != 0:
                    raise RuntimeError('the admission broker refused the launch')
                time.sleep(.25)
        raise RuntimeError('hidden PID/window/listener proof timed out; lease retained, no reuse allowed')
    except Exception:
        # Recover only this dispatch's exact token + directory + prefix identities, then signal via pidfds.
        have = [p for p in processes() if p['token'] == token and p['game'] == spec['game'] and p['compat'] == spec['compat']]
        lease['pids'] = [[p['pid'], p['start']] for p in have]
        path.write_text(json.dumps(lease))
        try:
            signal_owned(spec, root, 'cleanup')
        except (OSError, RuntimeError):
            pass  # retain the lease for review; never broaden cleanup to another process
        raise


def operate(spec, action):
    spec = identity(spec)
    if action == 'identity':
        return {'spec': spec}
    with registry() as root:
        if action == 'launch':
            return launch(spec, root)
        if action == 'status':
            procs = processes()
            have = matching(spec, procs)
            if have:
                _, lease = lease_for(root, spec)
                if have != owned(spec, lease, procs):
                    raise RuntimeError('running resource is foreign or has changed PID/start identity')
            return {'running': bool(have)}
        if action == 'verify':
            return {'spec': spec, 'pids': [[p['pid'], p['start']] for p in validate(spec, root)]}
        if action == 'claim-forward':
            port = spec.get('forward_port')
            if type(port) is not int or not 1024 <= port <= 65535:
                raise RuntimeError('an explicit forwarded port is required')
            lease = claim_forward(root, spec, port)
            return {'forward': port, 'owner': lease.get('token')}
        if action == 'release-forward':
            release_forwards(root, spec)
            return {'released': True}
        if action in ('cleanup', 'crash', 'pause', 'resume'):
            result = signal_owned(spec, root, action)
            release_forwards(root, spec)
            return result
        raise RuntimeError('unknown guarded action')
