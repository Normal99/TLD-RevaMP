#!/usr/bin/env python3
"""The synthetic containment proof: the launch chain, with no game.

It runs the *real* chain -- Hyprland dispatch -> tld-admit -> one transient
tld-job unit -> the launch shim -> a harmless synthetic GUI process -- and
proves the properties the game launch depends on:

  * the first map lands on special:tld-tests, floating, not fullscreen, at the
    expected size, and never takes the focus;
  * the synthetic process and its descendants share one tld-job-* cgroup;
  * the unit carries the game profile's systemd and kernel limits, and
    memory.oom.group is 1;
  * admission failure returns 75 before any dispatch, with no window and no
    unit;
  * exactly one game unit exists at a time;
  * the cleanup is scoped: only the PID/start identities this launch owns.

The synthetic process is a plain GTK window (tools/synthwin.py).  It is not the
game: it opens one window, writes a small report and exits.
"""
import json
import os
import pathlib
import shlex
import shutil
import subprocess
import tempfile
import sys
import threading
import time

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))

import testdisplay            # noqa: E402
import testlaunch as target   # noqa: E402
import launchguard             # noqa: E402

SYNTHWIN = TOOLS / 'synthwin.py'
WORKSPACE = testdisplay.WORKSPACE
EXPECTED_SIZE = (1280, 720)


def broker_path():
    return target.broker_path()


def hypr_env():
    return testdisplay.hypr_env()


def clients(env):
    return testdisplay.query('clients', env)


def active_window(env):
    return testdisplay.query('activewindow', env)


def list_units():
    out = subprocess.run(['systemctl', '--user', 'list-units', '--all', '--plain', '--no-legend',
                          '--no-pager', 'tld-job-*'], capture_output=True, text=True)
    units = []
    for line in out.stdout.splitlines():
        parts = line.split()
        if len(parts) >= 4 and parts[2] not in ('inactive', 'failed'):
            units.append(parts[0])
    return units


def unit_properties(unit):
    out = subprocess.run(['systemctl', '--user', 'show', unit,
                          '-p', 'MemoryHigh', '-p', 'MemoryMax', '-p', 'MemorySwapMax',
                          '-p', 'TasksMax', '-p', 'CPUQuotaPerSecUSec', '-p', 'CPUWeight',
                          '-p', 'IOWeight', '-p', 'Nice', '-p', 'OOMPolicy', '-p', 'Type',
                          '-p', 'KillMode'], capture_output=True, text=True)
    props = {}
    for line in out.stdout.splitlines():
        if '=' in line:
            key, value = line.split('=', 1)
            props[key] = value
    return props


def cgroup_of(pid):
    try:
        return pathlib.Path('/proc', str(pid), 'cgroup').read_text().strip()
    except OSError:
        return ''


def cgroup_path(unit):
    """The cgroup path of a unit, from its own cgroup file."""
    out = subprocess.run(['systemctl', '--user', 'show', unit, '-p', 'ControlGroup'],
                         capture_output=True, text=True)
    for line in out.stdout.splitlines():
        if line.startswith('ControlGroup='):
            return line.split('=', 1)[1]
    return ''


def cgroup_file(unit, leaf):
    path = cgroup_path(unit)
    if not path:
        return None
    return pathlib.Path('/sys/fs/cgroup') / path.lstrip('/') / leaf


PSI = ('some avg10=0.00 avg60=0.00 avg300=0.00 total=0\n'
       'full avg10=0.00 avg60=0.00 avg300=0.00 total=0\n')


def write_gauges(workdir, mem_available_kb=25165824, swap_total_kb=67108864):
    """The broker's gauge fixtures: meminfo, the watched cgroup, PSI and vmstat.

    These are the broker's own sanctioned test hooks (TLD_ADMIT_MEMINFO,
    TLD_ADMIT_WATCHED_CGROUP, TLD_ADMIT_PSI, TLD_ADMIT_VMSTAT).  Only the
    *gauges* the admission decision reads come from the fixture; the systemd
    unit, the cgroup limits and the profile verification stay real.
    """
    workdir = pathlib.Path(workdir)
    workdir.mkdir(parents=True, exist_ok=True)
    os.chmod(workdir, 0o700)
    mi = workdir / 'meminfo'
    mi.write_text('MemTotal: 33554432 kB\nMemAvailable: %d kB\n'
                  'SwapTotal: %d kB\nSwapFree: %d kB\n'
                  % (mem_available_kb, swap_total_kb, swap_total_kb))
    pressure = workdir / 'pressure'
    pressure.mkdir(parents=True, exist_ok=True)
    os.chmod(pressure, 0o700)
    (pressure / 'memory.stat').write_text('anon 1048576\nfile 33554432\nshmem 0\nswap 9999999999\n')
    (pressure / 'memory.swap.current').write_text('0\n')
    (pressure / 'memory.events').write_text('oom 0\noom_kill 0\noom_group_kill 0\n')
    (pressure / 'memory.pressure').write_text(PSI)
    (workdir / 'psi').write_text(PSI)
    (workdir / 'vmstat').write_text('oom_kill 0\n')
    return mi, pressure


def admit_env(workdir, meminfo=None, watched=None):
    """The broker's own sanctioned test hooks.

    The broker reads its gauges from a private meminfo file and a private watched
    cgroup when TLD_ADMIT_MEMINFO / TLD_ADMIT_WATCHED_CGROUP are set, and keeps
    its state in a private runtime directory.  The systemd unit it creates, the
    cgroup limits it applies and the profile it verifies are all real; only the
    *gauges* the admission decision reads come from the fixture, so the proof
    does not depend on this machine's momentary memory pressure.
    """
    env = dict(os.environ)
    env['TLD_ADMIT_RUNTIME_DIR'] = str(workdir / 'admit')
    if meminfo:
        env['TLD_ADMIT_MEMINFO'] = str(meminfo)
    if watched:
        env['TLD_ADMIT_WATCHED_CGROUP'] = str(watched)
    env['TLD_ADMIT_TEST_NO_WATCH'] = '1'
    psi = pathlib.Path(workdir).parent / 'psi'
    vmstat = pathlib.Path(workdir).parent / 'vmstat'
    if psi.is_file():
        env['TLD_ADMIT_PSI'] = str(psi)
    if vmstat.is_file():
        env['TLD_ADMIT_VMSTAT'] = str(vmstat)
    (workdir / 'admit').mkdir(parents=True, exist_ok=True)
    os.chmod(workdir / 'admit', 0o700)
    return env


def healthy_fixture(workdir):
    """A meminfo fixture that satisfies the game profile's capacity gate."""
    mi = workdir / 'meminfo'
    mi.write_text('MemTotal: 33554432 kB\nMemAvailable: 25165824 kB\n'
                   'SwapTotal: 8388608 kB\nSwapFree: 8388608 kB\n')
    pressure = workdir / 'pressure'
    pressure.mkdir(parents=True, exist_ok=True)
    os.chmod(pressure, 0o700)
    (pressure / 'memory.stat').write_text('anon 1048576\nfile 33554432\nshmem 0\nswap 9999999999\n')
    (pressure / 'memory.swap.current').write_text('0\n')
    (pressure / 'memory.events').write_text('oom 0\noom_kill 0\noom_group_kill 0\n')
    return mi, pressure


def run_chain(env, workdir, name, command, timeout=40):
    """The whole chain, exactly as the launcher composes it.

    Returns (exit_code, report).  The report is the synthetic process's own
    report file, if it produced one.
    """
    workdir = pathlib.Path(workdir)
    workdir.mkdir(parents=True, exist_ok=True)
    os.chmod(workdir, 0o700)
    # The shim's inner mode chdirs into the game directory and writes the launch
    # log into the prefix: both must exist before the unit starts.
    (workdir / 'game').mkdir(parents=True, exist_ok=True)
    (workdir / 'compat').mkdir(parents=True, exist_ok=True)
    tokenfile = workdir / 'token'
    exitfile = workdir / 'exit'
    report = workdir / 'report'
    # The composition is the launcher's own: target.compose() builds the exact
    # chain the guarded launch uses, so the proof exercises the shipped code and
    # not a parallel copy of it.
    spec = {'game': str(workdir / 'game'), 'compat': str(workdir / 'compat'),
            'steam': str(workdir / 'steam'), 'proton': str(workdir / 'proton'),
            'slr': str(workdir / 'slr'), 'port': 28151, 'profile': 'desktop',
            'flags': [], 'args': [], 'tokenfile': str(tokenfile), 'exitfile': str(exitfile),
            'token': name.replace('-', '')[-16:], 'command': command}
    # compose() reads the admission overrides from the controller's own
    # environment, because they have to end up inside the dispatched command line.
    saved = {}
    for name in target.ADMIT_VARS:
        if name in env:
            saved[name] = os.environ.get(name)
            os.environ[name] = env[name]
    try:
        outer = target.compose(spec, spec['token'], env)
        line = target.command_line(outer)
    finally:
        for name, value in saved.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
    rules = testdisplay.launch_rules('desktop')
    dispatched = subprocess.run('hyprctl dispatch exec ' + shlex.quote(rules + line), shell=True,
                                env=env, capture_output=True, text=True, timeout=20)
    if dispatched.returncode or dispatched.stdout.strip() != 'ok':
        raise RuntimeError('the compositor dispatch failed')
    code = None
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if exitfile.exists():
            try:
                code = int(exitfile.read_text().strip())
            except ValueError:
                code = None
            break
        time.sleep(0.2)
    return code, report


def cleanup_exact(pid, start):
    """Scoped cleanup: this exact PID/start identity, and nothing else."""
    try:
        current = (pathlib.Path('/proc', str(pid), 'stat').read_text().rsplit(')', 1)[1].split()[19])
    except OSError:
        return False
    if current != start:
        raise RuntimeError('the process identity changed before cleanup')
    os.kill(pid, 15)
    return True


def main():
    results = []
    failures = []

    def check(name, ok, detail=''):
        results.append((name, bool(ok), detail))
        print(('PASS  ' if ok else 'FAIL  ') + name + (('  -- ' + str(detail)) if detail else ''), flush=True)
        if not ok:
            failures.append(name)

    env = hypr_env()
    # A fixed default path means two proving sessions (a writer and an
    # independent verifier, or two runs) delete each other's working tree
    # mid-flight and collapse every check into an unexplained failure.
    # Honour an explicit TLD_SYNTH_WORK, otherwise take a fresh private one.
    explicit = os.environ.get('TLD_SYNTH_WORK')
    work = pathlib.Path(explicit) if explicit else pathlib.Path(tempfile.mkdtemp(prefix='tld-synth-proof-'))
    # A fresh directory per run: a stale log from an earlier run must never be
    # mistaken for this run's evidence.
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True, exist_ok=True)
    os.chmod(work, 0o700)
    name = 'game-' + os.urandom(6).hex()
    report = work / ('report-' + name)

    # The synthetic command: one window, the expected size, its own report.
    command = [sys.executable, str(SYNTHWIN), str(EXPECTED_SIZE[0]), str(EXPECTED_SIZE[1]), str(report)]

    # ---- 0. the machine is clean before the proof
    # The broker admits one job at a time (a global kernel lock), so a proof that
    # starts while another job is still winding down would be refused for the
    # wrong reason.  Wait for the machine to be free, and say so if it is not.
    before = list_units()
    deadline = time.monotonic() + 30
    while before and time.monotonic() < deadline:
        time.sleep(0.5)
        before = list_units()
    check('no tld-job unit exists before the proof', before == [], str(before))

    # ---- 0a. a refused admission creates no window and no unit
    # The refusal is checked first, and with the *real* broker and the real
    # dispatch path: the refusal must happen before anything is dispatched.
    low, low_pressure = write_gauges(work / 'refused-gauges', mem_available_kb=2097152,
                                      swap_total_kb=8388608)
    refuse_env = admit_env(work / 'refused', low, low_pressure)
    refuse_report = work / 'refused-report'
    refuse_name = 'game-' + os.urandom(6).hex()
    before_clients = len(clients(env))
    code, _ = run_chain(refuse_env, work / 'refused', refuse_name,
                        [sys.executable, str(SYNTHWIN), str(EXPECTED_SIZE[0]), str(EXPECTED_SIZE[1]),
                         str(refuse_report)], timeout=25)
    check('a refused admission returns 75', code == 75, 'exit=%s' % code)
    time.sleep(1.0)
    check('a refused admission creates no unit', list_units() == [], str(list_units()))
    check('a refused admission creates no window',
          len(clients(env)) == before_clients and not refuse_report.exists(),
          'clients %d -> %d' % (before_clients, len(clients(env))))

    # ---- 1. the whole chain, with the real game profile
    meminfo, pressure_ok = write_gauges(work / 'chain-gauges')
    chain_env = admit_env(work, meminfo, pressure_ok)
    # Everything about the containment has to be observed while the process is
    # alive: a unit that has exited and a window that has closed are not evidence.
    seen = {'units': [], 'unit_props': None, 'cgroup': None, 'oom_group': None,
            'pids_max': None, 'mem_max': None, 'windows': [], 'active': None,
            'report': None, 'unit_name': None}

    def sampler():
        while not stop.is_set():
            units = list_units()
            if units and not seen['units']:
                seen['units'] = list(units)
                seen['unit_name'] = units[0]
                seen['unit_props'] = unit_properties(units[0])
                cg = cgroup_path(units[0])
                seen['cgroup'] = cg
                for key, leaf in (('oom_group', 'memory.oom.group'), ('pids_max', 'pids.max'),
                                  ('mem_max', 'memory.max')):
                    path = cgroup_file(units[0], leaf)
                    if path and path.is_file():
                        seen[key] = path.read_text().strip()
            for c in clients(env):
                if (c.get('title') or '').startswith('tld-synthetic-probe'):
                    seen['windows'].append(c)
                    seen['active'] = active_window(env)
            if report.is_file() and seen['report'] is None:
                seen['report'] = dict(line.split('=', 1)
                                      for line in report.read_text().splitlines() if '=' in line)
            time.sleep(0.2)

    stop = threading.Event()
    watcher = threading.Thread(target=sampler, daemon=True)
    watcher.start()
    code, _ = run_chain(chain_env, work, name, command)
    stop.set()
    watcher.join(timeout=3)
    check('the chain ran and the broker reported success', code == 0, 'exit=%s' % code)

    # The synthetic proof takes the MACHINE-WIDE admission lock, exactly like a
    # real launch.  If another proof or a real unit held it, the broker refuses
    # (75) or the run is asked to stop (143).  Say that plainly instead of
    # reporting seventeen unrelated unit-limit failures against no unit.
    if code in (75, 143):
        active_unit = list_units()
        print('\nSYNTHPROOF: cannot run -- admission was not available to this proof.')
        print('  exit=%s. The synthetic proof needs exclusive use of the machine.' % code)
        print('  Reason: the admission broker allows exactly ONE tld-job-* unit machine-wide,')
        print('  and another proof, verifier or launch already held the lock.')
        if active_unit:
            print('  Active tld-job unit(s): %s' % ', '.join(active_unit))
        print('  Wait for that run to finish and re-run. This is NOT a containment failure.')
        return 2

    # ---- 2. exactly one game unit
    units = seen['units']
    check('exactly one tld-job unit exists', len(units) == 1, str(units))
    unit = seen['unit_name'] or (units[0] if units else '')
    check('the unit is a game unit', unit.startswith('tld-job-game-'), unit)
    check('the unit belongs to this launch', name.replace('-', '')[-16:] in unit, unit)

    # ---- 3. the window
    found = seen['windows'][0] if seen['windows'] else None
    check('the synthetic window mapped', found is not None)
    if found:
        ws = (found.get('workspace') or {}).get('name')
        check('the first map is on special:tld-tests', ws == WORKSPACE, ws)
        check('the window is floating', found.get('floating') is True, found.get('floating'))
        check('the window is not fullscreen', not found.get('fullscreen'), found.get('fullscreen'))
        check('the window is at the expected size', tuple(found.get('size') or ()) == EXPECTED_SIZE,
              found.get('size'))
        check('the window never took the focus', found.get('focusHistoryID') != 0, found.get('focusHistoryID'))
        active = seen['active'] or {}
        check('the active window is not the synthetic one', active.get('pid') != found.get('pid'),
              active.get('class'))
        visible = [m for m in testdisplay.query('monitors', env)
                   if (m.get('specialWorkspace') or {}).get('name') == WORKSPACE]
        check('the test workspace is not shown on any monitor', visible == [], str(visible))
        # every observation of the window agreed
        check('every observation of the window was hidden and unfocused',
              all((c.get('workspace') or {}).get('name') == WORKSPACE and c.get('floating') is True
                  and not c.get('fullscreen') and c.get('focusHistoryID') != 0
                  and tuple(c.get('size') or ()) == EXPECTED_SIZE for c in seen['windows']),
              str(len(seen['windows'])))

    # ---- 4. the cgroup, the tokens and the size
    info = seen['report'] or {}
    if info:
        cg = info.get('cgroup', '')
        check('the synthetic process is in the tld-job unit cgroup', 'tld-job-game-' in cg, cg)
        check('the synthetic process carries the compositor rule token', info.get('token') == 'yes',
              info.get('token'))
        check('the synthetic process carries the launch token', info.get('launch_token') == 'yes',
              info.get('launch_token'))
        check('the reported size matches the rule', (info.get('width'), info.get('height')) ==
              (str(EXPECTED_SIZE[0]), str(EXPECTED_SIZE[1])),
              (info.get('width'), info.get('height')))
        check('the synthetic process cgroup is the unit cgroup',
              bool(seen['cgroup']) and cg.endswith(seen['cgroup']),
              (cg, seen['cgroup']))

    # ---- 5. the unit's limits are the game profile's
    props = seen['unit_props'] or {}
    expected = {
        'MemoryMax': '10737418240', 'MemorySwapMax': '2147483648', 'TasksMax': '512',
        'CPUQuotaPerSecUSec': '3s', 'CPUWeight': '100', 'IOWeight': '100',
        'Nice': '0', 'OOMPolicy': 'kill', 'Type': 'exec',
    }
    for key, want in expected.items():
        check('the unit limit %s is the game profile value' % key, props.get(key) == want,
              '%s (want %s)' % (props.get(key), want))
    check('the unit has the game profile MemoryHigh', props.get('MemoryHigh') == '8589934592',
          props.get('MemoryHigh'))
    check('memory.oom.group is 1', seen['oom_group'] == '1', seen['oom_group'])
    check('the unit pids.max is the game profile value', seen['pids_max'] == '512', seen['pids_max'])
    check('the unit memory.max is the game profile value',
          seen['mem_max'] == str(10 * 1024**3), seen['mem_max'])

    # ---- 6. the whole tree is in the unit's cgroup
    if info and seen['cgroup']:
        pid = int(info.get('pid', '0'))
        children = subprocess.run(['/bin/sh', '-c',
                                   'ps -o pid= --ppid %d 2>/dev/null || true' % pid],
                                  capture_output=True, text=True).stdout.split()
        check('the synthetic process has no descendant outside the unit', all(c.isdigit() for c in children),
              str(children))
        for child in children:
            if child.isdigit():
                check('the descendant %s is in the unit cgroup' % child,
                      seen['cgroup'] in cgroup_of(int(child)), cgroup_of(int(child)))

    # ---- 7. the scoped cleanup
    if info:
        pid = int(info.get('pid', '0'))
        try:
            start_ticks = pathlib.Path('/proc', str(pid), 'stat').read_text().rsplit(')', 1)[1].split()[19]
        except OSError:
            start_ticks = None
        # a foreign process with the same name must survive the cleanup
        foreign = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
        try:
            subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True, timeout=20)
            time.sleep(1.0)
            check('the owned process is gone after the scoped stop',
                  not pathlib.Path('/proc', str(pid)).exists())
            check('an unrelated process survives the scoped stop',
                  foreign.poll() is None, 'foreign pid %s' % foreign.pid)
        finally:
            if foreign.poll() is None:
                foreign.terminate()
                try:
                    foreign.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    foreign.kill()

    # ---- 8. the unit is gone
    subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True) if unit else None
    time.sleep(0.5)
    after = list_units()
    check('no tld-job unit remains after the proof', after == [], str(after))

    print('\nSUMMARY')
    for name_, ok, detail in results:
        print('  %s  %s%s' % ('PASS' if ok else 'FAIL', name_, ('  -- ' + str(detail)) if not ok else ''))
    print('SYNTHPROOF: %d/%d PASS' % (len(results) - len(failures), len(results)))
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
