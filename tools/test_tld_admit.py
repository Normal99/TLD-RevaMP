#!/usr/bin/env python3
"""Bounded native broker boundary tests. No games, daemons or real OOM allocation.

All pressure/OOM counters are private fixtures; admitted children still enter real
systemd cgroups. Cleanup only ever addresses units/PIDs created by this suite.
"""
import fcntl
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest

ADMIT = str(Path(__file__).resolve().with_name('tld-admit'))
GLOBAL_LOCK = Path(f'/run/user/{os.getuid()}/tld-admission/admission.lock')


class Boundary(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='tld-guard-test-', dir=Path(__file__).resolve().parent)
        self.root = Path(self.tmp.name)
        self.pressure = self.root / 'pressure'
        self.pressure.mkdir()
        self.mi = self.root / 'meminfo'
        self.healthy()
        self.env = dict(os.environ, TLD_ADMIT_RUNTIME_DIR=str(self.root),
                        TLD_ADMIT_MEMINFO=str(self.mi), TLD_ADMIT_WATCHED_CGROUP=str(self.pressure),
                        TLD_ADMIT_PSI=str(self.root / 'psi'), TLD_ADMIT_VMSTAT=str(self.root / 'vmstat'),
                        GUARDFINISH_PASSWORD='fixture-private-env-value', TLD_ADMIT_TEST_NO_WATCH='1')
        self.processes = []
        self.units = set()

    def tearDown(self):
        watch = self.root / 'watch-unit'
        if watch.exists():
            unit = watch.read_text().strip()
            self.assertRegex(unit, r'^tld-admission-watch-[0-9a-f]{16}\.service$')
            self.units.add(unit)
        for unit in self.units:
            subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True, timeout=8)
            subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True, timeout=8)
        for p in self.processes:
            if p.poll() is None:
                p.kill()
            p.communicate(timeout=8)
        self.tmp.cleanup()

    def healthy(self):
        self.mi.write_text('MemTotal: 33554432 kB\nMemAvailable: 16777216 kB\n'
                           'SwapTotal: 67108864 kB\nSwapFree: 67108864 kB\n')
        (self.pressure / 'memory.stat').write_text('anon 1048576\nfile 33554432\nshmem 0\nswap 9999999999\n')
        (self.pressure / 'memory.swap.current').write_text('0\n')
        (self.pressure / 'memory.events').write_text('oom 0\noom_kill 0\noom_group_kill 0\n')

        (self.root / 'vmstat').write_text('oom_kill 0\n')
        self.set_psi()

    def set_psi(self, some='0.00', full='0.00', session=False):
        path = self.pressure / 'memory.pressure' if session else self.root / 'psi'
        path.write_text(f'some avg10={some} avg60=0.00 avg300=0.00 total=100\n'
                        f'full avg10={full} avg60=0.00 avg300=0.00 total=10\n')
        if not session:
            self.set_psi(session=True)

    def call(self, *args, rc=0, env=None):
        r = subprocess.run([ADMIT, *args], env=env or self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(r.returncode, rc, f'{args}: {r.stdout} {r.stderr}')
        return r

    def status(self, rc=0, env=None):
        return json.loads(self.call('status', '--json', rc=rc, env=env).stdout)

    def run_child(self, *argv, rc=0, env=None, name='boundary'):
        return self.call('run', '--profile', 'test', '--name', name, '--', *argv, rc=rc, env=env)

    def expire_timer(self):
        (self.root / 'healthy-since').write_text(str(int(time.monotonic()) - 61) + '\n')
        (self.root / 'healthy-last').write_text(str(int(time.monotonic())) + '\n')

    def test_high_swap_positive_and_profile_headroom(self):
        self.mi.write_text(self.mi.read_text().replace('SwapFree: 67108864', 'SwapFree: 56623104').replace('16777216', '18874368'))
        (self.pressure / 'memory.stat').write_text('anon 12884901888\nfile 4294967296\nshmem 0\n')
        (self.pressure / 'memory.swap.current').write_text(str(9 * 1024**3))
        self.set_psi('1.00', '0.90')
        self.assertEqual(self.status()['state'], 'healthy')
        self.run_child('/bin/true')
        self.call('run', '--profile', 'agent', '--name', 'agent-positive', '--', '/bin/true')
        self.call('run', '--profile', 'game', '--name', 'game-positive', '--', '/bin/true')
        state = json.loads((self.root / 'state.json').read_text())
        self.assertTrue(state['limits']['verified'])
        self.assertEqual(state['limits']['memoryHigh'], str(8 * 1024**3))
        self.assertEqual(state['limits']['memoryMax'], str(10 * 1024**3))
        self.assertEqual(state['limits']['memorySwapMax'], str(2 * 1024**3))
        self.call('run', '--profile', 'vr', '--name', 'vr-insufficient', '--', '/bin/true', rc=75)
        self.assertEqual(json.loads((self.root / 'state.json').read_text())['pauseKind'], 'capacity')
        self.assertFalse((self.root / 'auto-pause').exists(), 'capacity rejection must not poison other profiles')
        self.mi.write_text(self.mi.read_text().replace('18874368', '29360128'))
        self.call('run', '--profile', 'vr', '--name', 'vr-synthetic', '--', '/bin/true')
        state = json.loads((self.root / 'state.json').read_text())
        self.assertTrue(state['limits']['verified'])
        self.assertEqual(state['limits']['memoryHigh'], str(10 * 1024**3))
        self.assertEqual(state['limits']['memoryMax'], str(12 * 1024**3))
        self.assertEqual(state['limits']['memorySwapMax'], str(4 * 1024**3))
        self.assertEqual(state['limits']['tasksMax'], '768')

    def test_psi_debounce_reentry_gap_and_hysteresis(self):
        self.set_psi('10.00', '1.00')
        self.assertIn('debounce', self.status()['reason'])
        self.assertFalse((self.root / 'auto-pause').exists())
        self.run_child('/bin/true', rc=75)
        since = (self.root / 'psi-since').read_text()
        self.assertEqual(self.status()['state'], 'paused')
        self.assertEqual((self.root / 'psi-since').read_text(), since, 'polls are not seconds')
        self.set_psi()
        self.assertEqual(self.status()['state'], 'healthy', 'short spike clears without latched cooldown')
        self.set_psi('10.00', '1.00')
        self.status()
        (self.root / 'psi-since').write_text(str(int(time.monotonic()) - 5))
        (self.root / 'psi-last').write_text(str(int(time.monotonic()) - 4))
        self.assertIn('debounce', self.status()['reason'], 'unobserved gap must restart debounce')
        self.assertFalse((self.root / 'auto-pause').exists())
        (self.root / 'psi-since').write_text(str(int(time.monotonic()) - 5))
        (self.root / 'psi-last').write_text(str(int(time.monotonic())))
        self.assertIn('sustained', self.status()['reason'])
        self.assertTrue((self.root / 'auto-pause').exists())
        self.set_psi('4.99', '1.00')
        self.status()
        self.assertFalse((self.root / 'healthy-since').exists(), 'full PSI recovery boundary holds')
        self.set_psi('5.00', '0.99')
        self.status()
        self.assertFalse((self.root / 'healthy-since').exists(), 'some PSI recovery boundary holds')
        self.set_psi('4.99', '0.99')
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        self.mi.write_text(self.mi.read_text().replace('16777216', '8388607'))
        self.status()
        self.assertFalse((self.root / 'healthy-since').exists(), 'memory recovery hysteresis resets cooldown')
        self.healthy()
        self.status()
        self.expire_timer()
        self.assertEqual(self.status()['state'], 'healthy')
        self.set_psi('10.00', '10.00', session=True)
        self.assertIn('severe', self.status()['reason'], 'severe session full stalls bypass debounce')

    def test_automatic_watch_samples_pending_psi(self):
        live_watch = Path(f'/run/user/{os.getuid()}/tld-admission/watch-unit')
        live_before = live_watch.read_bytes() if live_watch.is_file() else None
        env = dict(self.env)
        env.pop('TLD_ADMIT_TEST_NO_WATCH')
        self.set_psi('10.00', '1.00')
        self.assertIn('debounce', self.status(env=env)['reason'])
        self.assertTrue((self.root / 'watch-unit').exists())
        self.assertEqual(live_watch.read_bytes() if live_watch.is_file() else None, live_before,
                         'a private watcher changed the live watch-unit marker')
        deadline = time.monotonic() + 8
        while not (self.root / 'auto-pause').exists() and time.monotonic() < deadline:
            time.sleep(.1)
        self.assertTrue((self.root / 'auto-pause').exists(), 'watch must latch sustained PSI without CLI polling')
        self.assertIn('sustained', (self.root / 'auto-pause').read_text())

    def test_global_oom_increment_reset_and_unknown(self):
        self.status()
        (self.root / 'vmstat').write_text('oom_kill 1\n')
        self.assertIn('global OOM', self.status()['reason'])
        self.status()
        self.expire_timer()
        (self.root / 'vmstat').write_text('oom_kill 0\n')
        self.assertIn('reset', self.status()['reason'])
        self.assertFalse((self.root / 'healthy-since').exists())
        self.status()
        self.expire_timer()
        (self.root / 'vmstat').unlink()
        self.assertEqual(self.status()['state'], 'unavailable')
        self.assertFalse((self.root / 'healthy-since').exists())

    def test_new_gauges_malformed_missing_and_duplicates(self):
        bad_psi = [None, '', 'some avg10=nan\n',
                   'some avg10=0.00 avg60=0.00 avg300=0.00 total=0\n',
                   'some avg10=1.00 avg60=0.00 avg300=0.00 total=0\nfull avg10=2.00 avg60=0.00 avg300=0.00 total=0\n',
                   'some avg10=1.00 avg60=0.00 avg300=0.00 total=0\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\njunk']
        for path in [self.root / 'psi', self.pressure / 'memory.pressure']:
            for value in bad_psi:
                with self.subTest(path=path, value=value):
                    self.healthy()
                    if value is None: path.unlink()
                    else: path.write_text(value)
                    self.assertEqual(self.status()['state'], 'unavailable')
                    self.run_child('/bin/true', rc=75)
        for leaf, text in [('memory.stat', 'anon 1\nfile 2\n'),
                           ('memory.stat', 'anon 1\nfile 2\nshmem 3\n'),
                           ('memory.stat', 'anon 1\nanon 2\nfile 3\nshmem 0\n'),
                           ('memory.events', 'oom_kill 0\noom_kill 1\n'),
                           ('memory.swap.current', '0oops\n'),
                           ('memory.swap.current', '0\njunk\n'),
                           ('memory.swap.current', '0\x00bad\n')]:
            with self.subTest(leaf=leaf, text=text):
                self.healthy()
                (self.pressure / leaf).write_text(text)
                self.assertEqual(self.status()['state'], 'unavailable')
                self.run_child('/bin/true', rc=75)
        self.healthy()
        self.mi.write_text(self.mi.read_text() + 'MemAvailable: 1 kB\n')
        self.assertEqual(self.status()['state'], 'unavailable')

    def test_automatic_watch_continuous_real_cooldown(self):
        env = dict(self.env)
        env.pop('TLD_ADMIT_TEST_NO_WATCH')
        self.mi.write_text(self.mi.read_text().replace('16777216', '6291455'))
        self.assertEqual(self.status(env=env)['pauseKind'], 'threshold')
        self.assertTrue((self.root / 'watch-unit').exists())
        self.healthy()
        deadline = time.monotonic() + 66
        while time.monotonic() < deadline:
            s = json.loads((self.root / 'state.json').read_text())
            if s['state'] == 'healthy': break
            time.sleep(.2)
        self.assertEqual(s['state'], 'healthy', 'transient guard monitor must resume without CLI polling')
        self.assertFalse((self.root / 'auto-pause').exists())

    def test_oom_cooldown_and_repeat_crossing(self):
        self.assertEqual(self.status()['state'], 'healthy')
        (self.pressure / 'memory.events').write_text('oom 999\noom_kill 1\noom_group_kill 999\n')
        s = self.status()
        self.assertEqual(s['pauseKind'], 'threshold')
        self.assertIn('OOM-kill counter increased', s['reason'])
        self.assertTrue((self.root / 'auto-pause').is_file(), 'automatic pressure pause persists until healthy cooldown')
        self.assertFalse((self.root / 'healthy-since').exists(), 'crossing itself cannot count as healthy')
        self.assertEqual(self.status()['state'], 'paused')
        timer = (self.root / 'healthy-since').read_text()
        self.assertEqual(self.status()['state'], 'paused')
        self.assertEqual((self.root / 'healthy-since').read_text(), timer, 'unchanged OOM counter cannot restart cooldown')
        (self.root / 'healthy-since').write_text(str(int(time.monotonic()) - 58) + '\n')
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        (self.pressure / 'memory.events').write_text('oom_kill 2\n')
        self.assertEqual(self.status()['state'], 'paused')
        self.assertFalse((self.root / 'healthy-since').exists(), 'another OOM resets even an expired cooldown')
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        self.assertEqual(self.status()['state'], 'healthy')
        self.assertFalse((self.root / 'auto-pause').exists())
        self.assertEqual((self.root / 'oom-baseline').read_text().strip(), '2')

    def test_threshold_reentry_resets_cooldown(self):
        self.mi.write_text(self.mi.read_text().replace('16777216', '6291455'))
        self.assertIn('MemAvailable', self.status()['reason'])
        self.healthy()
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        self.mi.write_text(self.mi.read_text().replace('SwapFree: 67108864', 'SwapFree: 1024'))
        self.assertIn('remaining swap', self.status()['reason'])
        self.assertFalse((self.root / 'healthy-since').exists())
        self.healthy()
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        self.assertEqual(self.status()['state'], 'healthy')

    def test_manual_precedence_in_both_orders(self):
        self.status()
        self.call('pause', 'operator hold')
        (self.pressure / 'memory.events').write_text('oom_kill 1\n')
        self.assertEqual(self.status()['pauseKind'], 'manual')
        self.assertTrue((self.root / 'auto-pause').exists(), 'OOM must be remembered during manual hold')
        self.call('resume', rc=75)
        self.assertEqual(self.status()['pauseKind'], 'threshold')
        self.call('pause', 'hold again')
        self.expire_timer()
        self.assertEqual(self.status()['pauseKind'], 'manual', 'automatic resume cannot clear a manual hold')
        self.assertTrue((self.root / 'manual-pause').exists())
        self.call('resume')
        self.assertEqual(self.status()['state'], 'healthy')

    def test_unknown_resets_timer_and_never_uses_stat_swap(self):
        self.assertEqual(self.status()['sessionSwap'], 0)
        self.mi.write_text(self.mi.read_text().replace('16777216', '6291455'))
        self.status()
        self.healthy()
        self.status()
        self.expire_timer()
        (self.pressure / 'memory.swap.current').unlink()
        s = self.status()
        self.assertEqual(s['state'], 'unavailable')
        self.assertEqual(s['sessionSwap'], -1)
        self.assertFalse((self.root / 'healthy-since').exists())
        self.run_child('/bin/true', rc=75)
        self.healthy()
        self.assertEqual(self.status()['state'], 'paused')
        self.expire_timer()
        self.assertEqual(self.status()['state'], 'healthy')

    def test_missing_and_malformed_gauges_fail_closed(self):
        for leaf, value in [('memory.stat', 'anon -1\nfile 999\n'),
                            ('memory.stat', 'anon 1oops\n'),
                            ('memory.swap.current', '1oops\n'),
                            ('memory.events', 'oom_kill 999999999999999999999\n')]:
            with self.subTest(leaf=leaf, value=value):
                self.healthy()
                (self.pressure / leaf).write_text(value)
                self.assertEqual(self.status()['state'], 'unavailable')
                self.run_child('/bin/true', rc=75)
        self.healthy()
        self.mi.write_text('MemTotal: 33554432 kB\nMemAvailable: 16777216 kB\n')
        self.assertEqual(self.status()['state'], 'unavailable')

    def test_saved_state_filters_unknown_fields(self):
        (self.root / 'state.json').write_text(json.dumps({
            'state': 'idle', 'name': 'PASSWORD=fixture-private-saved-name',
            'limits': {'memoryMax': '2147483648', 'unexpected': 'fixture-private-saved-extra'},
            'environment': 'fixture-private-saved-environment'}))
        s = self.status()
        self.assertEqual(s['limits']['memoryMax'], '2147483648')
        serialized = json.dumps(s)
        self.assertNotIn('fixture-private', serialized)
        self.assertNotIn('environment', s)

    def test_a_private_runtime_never_touches_the_live_state(self):
        # A test runtime and the fleet's default runtime share only the global admission lock. Every state
        # leaf, marker and baseline belongs to the selected runtime directory, so a test can never create a
        # live manual hold, pressure pause, watcher name or state snapshot.
        live = Path(f'/run/user/{os.getuid()}/tld-admission')
        leaves = ('state.json', 'status.txt', 'manual-pause', 'auto-pause',
                  'watch-unit', 'oom-baseline', 'oom-last-seen',
                  'global-oom-baseline', 'global-oom-last-seen')
        def snapshots():
            return {leaf: ((live / leaf).read_bytes() if (live / leaf).is_file() else None)
                    for leaf in leaves}
        # Do not hold the global admission lock while invoking the child: that lock is the
        # broker's concurrency barrier and would make the child block. With all other guard
        # suites quiesced, the before/after snapshot is stable and proves isolation directly.
        before = snapshots()
        state = self.status()
        self.assertTrue((self.root / 'state.json').is_file())
        self.assertTrue((self.root / 'oom-baseline').is_file())
        self.call('pause', 'private test pause')
        self.assertTrue((self.root / 'manual-pause').exists())
        self.call('resume')
        self.assertFalse((self.root / 'manual-pause').exists())
        self.healthy()
        self.mi.write_text(self.mi.read_text().replace('16777216', '6291455'))
        low = self.status()
        self.assertEqual(low['pauseKind'], 'threshold')
        self.assertTrue((self.root / 'auto-pause').exists())
        self.assertEqual(snapshots(), before, 'a private runtime changed live admission state')

    def test_the_default_runtime_publishes_and_resumes_in_its_own_directory(self):
        # The real/default path is the directory the overview reads. Use a fake XDG_RUNTIME_DIR so this test
        # exercises exactly that branch without changing the live session.
        state = self.root / 'default-admission'
        env = dict(self.env)
        env.pop('TLD_ADMIT_RUNTIME_DIR')
        env['TLD_ADMIT_TEST_DEFAULT_DIR'] = str(state)
        self.status(env=env)
        self.assertTrue((state / 'state.json').is_file())
        self.assertTrue((state / 'status.txt').is_file())
        self.call('pause', 'default path test', env=env)
        self.assertTrue((state / 'manual-pause').is_file())
        self.assertEqual(json.loads(self.call('status', '--json', env=env).stdout)['pauseKind'], 'manual')
        self.call('resume', env=env)
        self.assertFalse((state / 'manual-pause').exists())
        self.assertEqual(json.loads(self.call('status', '--json', env=env).stdout)['state'], 'healthy')

    def test_unobserved_gap_cannot_count_as_continuous_health(self):
        self.mi.write_text(self.mi.read_text().replace('16777216', '6291455'))
        self.status()
        self.healthy()
        self.status()
        self.expire_timer()
        (self.root / 'healthy-last').write_text(str(int(time.monotonic()) - 5))
        self.assertEqual(self.status()['state'], 'paused')
        self.assertGreaterEqual(int((self.root / 'healthy-since').read_text()), int(time.monotonic()) - 1)

    def test_runtime_and_publication_failure_never_executes(self):
        (self.root / 'state.json').mkdir()
        marker = self.root / 'must-not-exist'
        self.run_child('/usr/bin/touch', str(marker), rc=75)
        self.assertFalse(marker.exists())
        self.assertFalse(list(self.root.glob('.publish-*')))
        link = self.root / 'symlink'
        link.symlink_to(self.root, target_is_directory=True)
        self.call('status', '--json', rc=75, env=dict(self.env, TLD_ADMIT_RUNTIME_DIR=str(link)))

    def test_unavailable_manager_is_unknown_and_rejects(self):
        env = dict(self.env, XDG_RUNTIME_DIR=str(self.root), DBUS_SESSION_BUS_ADDRESS=f'unix:path={self.root}/absent-bus')
        s = self.status(rc=75, env=env)
        self.assertIsNone(s['active'])
        self.assertEqual(s['pauseKind'], 'unavailable')
        self.run_child('/bin/true', rc=75, env=env)

    def test_invalid_arguments_and_internal_argv(self):
        for args in [('status', '--bad'), ('resume', 'extra'), ('pause', 'one', 'two'),
                     ('run', '--profile', 'other', '--name', 'bad', '--', '/bin/true'),
                     ('run', '--profile', 'test', '--name', 'x;touch-bad', '--', '/bin/true'),
                     ('run', '--profile', 'test', '--name', 'x', '--'),
                     ('--internal-supervise', str(self.root), 'x;touch-bad', 'test', 'x', '1', 'v2', '--', '/bin/true')]:
            with self.subTest(args=args):
                self.call(*args, rc=64)

    def test_manual_reason_privacy_and_no_env_dump(self):
        self.call('pause', 'operator PASSWORD=fixture-private-reason-value\x1b[31m')
        combined = (self.root / 'state.json').read_text() + (self.root / 'status.txt').read_text()
        self.assertNotIn('fixture-private-reason-value', combined)
        self.assertNotIn('fixture-private-env-value', combined)
        self.assertNotIn('\x1b', combined)
        self.assertEqual(self.status()['pauseKind'], 'manual')

    def test_kernel_lock_rejection_and_exact_stale_recovery(self):
        self.status()
        with GLOBAL_LOCK.open('r+') as fd:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.run_child('/bin/true', rc=75)
        (self.root / 'state.json').write_text('{"state":"running","active":true,"unit":"tld-job-dead.service"}')
        self.run_child('/bin/true')
        s = json.loads((self.root / 'state.json').read_text())
        self.assertEqual(s['exitStatus'], 0)
        self.assertEqual(s['result'], 'success')
        self.assertFalse(s['active'])

    def test_profile_mismatch_cannot_execute(self):
        unit = f'tld-job-mismatch-{os.getpid()}-{time.time_ns()}.service'
        self.units.add(unit)
        marker = self.root / 'must-not-execute'
        command = ['systemd-run', '--user', '--quiet', '--wait', '--pipe', '--expand-environment=no',
                   '--unit=' + unit, '--property=Type=exec', '--property=MemoryMax=64M',
                   '--property=MemoryHigh=32M', '--property=MemorySwapMax=0', '--property=TasksMax=32',
                   '--property=CPUQuota=10%', '--property=OOMPolicy=kill']
        for key in ['TLD_ADMIT_MEMINFO', 'TLD_ADMIT_WATCHED_CGROUP', 'TLD_ADMIT_TEST_NO_WATCH', 'TLD_ADMIT_PSI', 'TLD_ADMIT_VMSTAT']:
            command.append('--setenv=' + key + '=' + self.env[key])
        command += ['--', ADMIT, '--internal-supervise', str(self.root), unit, 'test', 'mismatch',
                    str(int(time.time())), 'v2', '--', '/usr/bin/touch', str(marker)]
        result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 75, result.stderr)
        self.assertFalse(marker.exists())
        state = json.loads((self.root / 'state.json').read_text())
        self.assertFalse(state['limits']['verified'])
        self.assertEqual(state['result'], 'profile-verification-failed')

    def test_each_profile_rejects_one_changed_control(self):
        # Real service creation with one plausible but wrong control. A successful
        # exec would touch the marker, even though it never allocates game RAM.
        for profile, high, maximum, swap, tasks, quota, nice, mutation in [
            ('agent', '1536M', '2G', '1G', '256', '200%', '10', 'MemorySwapMax=0'),
            ('test', '1536M', '2G', '1G', '256', '200%', '10', 'CPUWeight=49'),
            ('game', '8G', '10G', '2G', '512', '300%', '0', 'MemoryHigh=7G'),
            ('vr', '10G', '12G', '4G', '768', '300%', '0', 'TasksMax=767')]:
            with self.subTest(profile=profile):
                unit = f'tld-job-mutated-{profile}-{os.getpid()}-{time.time_ns()}.service'
                self.units.add(unit)
                marker = self.root / ('must-not-execute-' + profile)
                props = dict(Type='exec', MemoryHigh=high, MemoryMax=maximum, MemorySwapMax=swap,
                             TasksMax=tasks, CPUQuota=quota, CPUWeight='50' if nice == '10' else '100',
                             IOWeight='50' if nice == '10' else '100', Nice=nice, OOMPolicy='kill',
                             KillMode='control-group')
                key, value = mutation.split('=')
                props[key] = value
                command = ['systemd-run', '--user', '--quiet', '--wait', '--pipe', '--expand-environment=no', '--unit=' + unit]
                command += ['--property=' + key + '=' + value for key, value in props.items()]
                command += ['--setenv=' + key + '=' + self.env[key] for key in
                            ['TLD_ADMIT_MEMINFO', 'TLD_ADMIT_WATCHED_CGROUP', 'TLD_ADMIT_TEST_NO_WATCH', 'TLD_ADMIT_PSI', 'TLD_ADMIT_VMSTAT']]
                command += ['--', ADMIT, '--internal-supervise', str(self.root), unit, profile, 'mutated',
                            str(int(time.time())), 'v2', '--', '/usr/bin/touch', str(marker)]
                result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 75, result.stderr)
                self.assertFalse(marker.exists())
                state = json.loads((self.root / 'state.json').read_text())
                self.assertFalse(state['limits']['verified'])
                self.assertEqual(state['result'], 'profile-verification-failed')

    def test_background_descendant_is_cleaned_with_unit(self):
        pidfile = self.root / 'descendant'
        script = 'import subprocess,sys; p=subprocess.Popen(["/bin/sleep","20"]);open(sys.argv[1],"w").write(str(p.pid));raise SystemExit(23)'
        self.run_child('/usr/bin/python3', '-c', script, str(pidfile), rc=23)
        pid = pidfile.read_text()
        stat = Path('/proc') / pid / 'stat'
        if stat.exists():
            self.assertEqual(stat.read_text().rsplit(')', 1)[1].split()[0], 'Z')
        self.assertFalse(self.status()['active'])

    def test_exact_exit_signal_argv_and_privacy(self):
        for rc in [0, 19, 64, 75, 127, 137]:
            with self.subTest(exit=rc):
                self.run_child('/usr/bin/python3', '-c', f'raise SystemExit({rc})', rc=rc)
                s = json.loads((self.root / 'state.json').read_text())
                self.assertEqual(s['exitStatus'], rc)
                self.assertFalse(s['oomKilled'])
        self.run_child('/usr/bin/python3', '-c', 'import os,signal;os.kill(os.getpid(),signal.SIGKILL)', rc=137)
        s = json.loads((self.root / 'state.json').read_text())
        self.assertEqual(s['exitStatus'], 137)
        self.assertFalse(s['oomKilled'], 'SIGKILL/137 alone is not OOM evidence')
        values = ['$HOME', '${USER}', '%n', 'a b', 'quote\'"', 'x; touch nope', '$(false)', 'line\nbreak', 'fixture-private-argv-value']
        result = self.run_child('/usr/bin/python3', '-c', 'import json,sys;print(json.dumps(sys.argv[1:]))', *values)
        self.assertEqual(json.loads(result.stdout), values, 'systemd must preserve literal child argv')
        combined = (self.root / 'state.json').read_text() + (self.root / 'status.txt').read_text()
        self.assertNotIn('fixture-private-argv-value', combined)
        self.assertNotIn('fixture-private-env-value', combined)

    def test_tree_pressure_controller_death_and_systemd_barrier(self):
        ready = self.root / 'ready'
        go = self.root / 'go'
        script = """import os,json,sys,time
from pathlib import Path
ready,go=map(Path,sys.argv[1:])
child=os.fork()
if child==0:
    allocation=bytearray(16*1024*1024)
    Path(str(ready)+'.child').write_text(json.dumps({'pid':os.getpid(),'cgroup':Path('/proc/self/cgroup').read_text()}))
    deadline=time.monotonic()+15
    while not go.exists() and time.monotonic()<deadline: time.sleep(.02)
    os._exit(0)
ready.write_text(json.dumps({'pid':os.getpid(),'cgroup':Path('/proc/self/cgroup').read_text()}))
deadline=time.monotonic()+15
while not go.exists() and time.monotonic()<deadline: time.sleep(.02)
os.waitpid(child,0)
raise SystemExit(19 if go.exists() else 84)
"""
        p = subprocess.Popen([ADMIT, 'run', '--profile', 'test', '--name', 'tree', '--', '/usr/bin/python3', '-c', script, str(ready), str(go)], env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.processes.append(p)
        try:
            deadline = time.monotonic() + 6
            while time.monotonic() < deadline:
                if (self.root / 'state.json').exists():
                    state = json.loads((self.root / 'state.json').read_text())
                    if state.get('unit'): self.units.add(state['unit'])
                if ready.exists() and Path(str(ready) + '.child').exists(): break
                self.assertIsNone(p.poll(), 'job must reach ready')
                time.sleep(.03)
            self.assertTrue(ready.exists() and Path(str(ready) + '.child').exists())
            parent = json.loads(ready.read_text())
            child = json.loads(Path(str(ready) + '.child').read_text())
            self.assertEqual(parent['cgroup'], child['cgroup'])
            self.assertIn('/tld-job-tree-', parent['cgroup'])
            s = self.status()
            self.assertEqual(s['limits']['memoryHigh'], '1610612736')
            self.assertEqual(s['limits']['memoryMax'], '2147483648')
            self.assertEqual(s['limits']['memorySwapMax'], '1073741824')
            self.assertEqual(s['limits']['tasksMax'], '256')
            self.assertEqual(s['limits']['cpuQuotaSystemd'], '2s')
            self.assertEqual(s['limits']['cpuWeight'], '50')
            self.assertEqual(s['limits']['ioWeight'], '50')
            self.assertEqual(s['limits']['nice'], 10)
            self.assertTrue(s['limits']['memoryOOMGroup'])
            self.assertEqual(Path(s['cgroup'], 'memory.oom.group').read_text().strip(), '1')
            self.run_child('/bin/true', rc=75)
            other = self.root / 'other'
            other.mkdir()
            self.run_child('/bin/true', rc=75, env=dict(self.env, TLD_ADMIT_RUNTIME_DIR=str(other)))
            self.call('pause', 'preserve running job')
            self.assertEqual(self.status()['pauseKind'], 'manual')
            self.call('resume')
            os.kill(parent['pid'], 0)
            (self.pressure / 'memory.events').write_text('oom_kill 1\n')
            s = self.status()
            self.assertTrue(s['active'])
            self.assertEqual(s['pauseKind'], 'threshold')
            self.assertFalse(s['oomKilled'], 'watched-session OOM does not label job OOM')
            os.kill(child['pid'], 0)
            waiter = int(Path(f'/proc/{p.pid}/task/{p.pid}/children').read_text().split()[0])
            runner = int(Path(f'/proc/{waiter}/task/{waiter}/children').read_text().split()[0])
            p.kill()
            p.wait(timeout=3)
            self.assertTrue(self.status()['active'])
            with GLOBAL_LOCK.open('r+') as fd:
                with self.assertRaises(BlockingIOError): fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.run_child('/bin/true', rc=75)
            # Remove only verified wrapper PIDs created for this test. The actual
            # service must survive and be rejected by the systemd barrier alone.
            for pid in [waiter, runner]:
                self.assertEqual(Path(f'/proc/{pid}/comm').read_text().strip(), 'tld-admit' if pid == waiter else 'systemd-run')
                os.kill(pid, signal.SIGKILL)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                try:
                    with GLOBAL_LOCK.open('r+') as fd: fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError: time.sleep(.02)
            self.run_child('/bin/true', rc=75)
            os.kill(parent['pid'], 0)
            go.touch()
            deadline = time.monotonic() + 6
            while time.monotonic() < deadline:
                s = json.loads((self.root / 'state.json').read_text())
                if s['exitStatus'] == 19: break
                time.sleep(.04)
            self.assertEqual(s['exitStatus'], 19)
            self.assertFalse(s['oomKilled'])
            self.assertFalse(self.status()['active'])
        finally:
            go.touch()


if __name__ == '__main__':
    unittest.main(verbosity=2)
