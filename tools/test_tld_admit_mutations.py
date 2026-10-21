#!/usr/bin/env python3
"""Prove policy tests detect deliberate safety regressions, without live units."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent
CORE_SOURCE = (ROOT / 'tld_admit_core.c').read_text()
ADMIT_SOURCE = (ROOT / 'tld-admit.c').read_text()
TEST_SOURCE = (ROOT / 'test_tld_admit.py').read_text()
SOURCE = CORE_SOURCE
MUTATIONS = {
    'lost RAM reserve': ('r->mem_available < 6 * GIB', 'r->mem_available < 5 * GIB'),
    'lost swap reserve': ('free_swap < stop_swap', 'free_swap < 0'),
    'ignored some PSI': ('some >= 1000 || full >= 200', 'some >= 1001 || full >= 200'),
    'ignored full PSI': ('some >= 1000 || full >= 200', 'some >= 1000 || full >= 201'),
    'lost recovery hysteresis': ('r->mem_available >= 8 * GIB', 'r->mem_available >= 6 * GIB'),
    'shmem counted as cache': ('r->session_file - r->session_shmem', 'r->session_file'),
    'unknown readings admitted': ('return s && d.all_reading_ok && !d.threshold_paused', 'return s && !d.threshold_paused'),
    'unbounded game growth': ('8 * GIB, 10 * GIB, 2 * GIB', '8 * GIB, 24 * GIB, 2 * GIB'),
    'VR reserve removed': ('4 * GIB, 26 * GIB, "3s"', '4 * GIB, 18 * GIB, "3s"'),
}


def run_checked(argv, timeout, cwd=None, env=None):
    try:
        return subprocess.run(
            argv,
            cwd=cwd,
            env=env,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired as exc:
        raise AssertionError(
            f'command timed out: {argv!r}\n'
            f'stdout:\n{exc.stdout}\n'
            f'stderr:\n{exc.stderr}'
        ) from exc


def show_failure(label, result):
    return (
        f'{label}\n'
        f'returncode: {result.returncode}\n'
        f'stdout:\n{result.stdout}\n'
        f'stderr:\n{result.stderr}'
    )


def assert_passed(result, label):
    output = result.stdout + result.stderr
    assert result.returncode == 0, show_failure(label, result)
    assert 'OK' in output, show_failure(label, result)
    assert 'FAILED' not in output, show_failure(label, result)
    assert 'ERROR' not in output, show_failure(label, result)


def assert_killed(result, label, intended_text):
    output = result.stdout + result.stderr
    assert result.returncode == 1, show_failure(label, result)
    assert 'FAILED (failures=1)' in output, show_failure(label, result)
    assert 'ERROR:' not in output, show_failure(label, result)
    assert 'FAIL:' in output, show_failure(label, result)
    assert 'AssertionError' in output, show_failure(label, result)
    assert intended_text in output, show_failure(label, result)


def compile_broker(source, variant_dir, helpers):
    source_dir = variant_dir / 'source'
    test_dir = variant_dir / 'test'
    source_dir.mkdir(parents=True)
    test_dir.mkdir()
    (source_dir / 'tld-admit.c').write_text(source)
    for helper in helpers:
        shutil.copy2(ROOT / helper, source_dir / helper)
    for header in ROOT.glob('*.h'):
        shutil.copy2(header, source_dir / header.name)
    executable = test_dir / 'tld-admit'
    command = [
        os.environ.get('CC', 'cc'),
        '-std=c11',
        '-O2',
        '-Wall',
        '-Wextra',
        '-Wshadow',
        '-Werror',
        '-I', str(source_dir),
        str(source_dir / 'tld-admit.c'),
        str(source_dir / 'tld_admit_core.c'),
        str(source_dir / 'overview_core.c'),
        str(source_dir / 'native_process.c'),
        '-o', str(executable),
    ]
    result = run_checked(command, timeout=30)
    assert result.returncode == 0, show_failure('broker build failed', result)
    return executable


def main():
    with tempfile.TemporaryDirectory(prefix='tld-mutations-') as temp:
        tmp = Path(temp)

        for name, (old, new) in MUTATIONS.items():
            assert SOURCE.count(old) == 1, (name, 'mutation anchor changed')
            core = tmp / 'core.c'
            core.write_text(SOURCE.replace(old, new))
            exe = tmp / 'test'
            build = run_checked([
                os.environ.get('CC', 'cc'), '-std=c11', '-O2', '-Wall', '-Wextra', '-Wshadow',
                '-Werror', '-I', str(ROOT), str(core), str(ROOT / 'test_tld_admit.c'),
                str(ROOT / 'overview_core.c'), '-o', str(exe),
            ], timeout=20)
            assert build.returncode == 0, show_failure(f'core build failed: {name}', build)
            result = run_checked([exe], timeout=5)
            assert result.returncode == 1 and 'FAIL' in result.stdout, (
                name, result.stdout, result.stderr
            )
            print(f'killed mutation: {name}', flush=True)

        helpers = (
            'tld_admit_core.c',
            'tld_admit_core.h',
            'overview_core.c',
            'overview_core.h',
            'native_process.c',
            'native_process.h',
            'overview_data.h',
        )
        fake_bin = tmp / 'fake-bin'
        fake_bin.mkdir()
        fake_systemctl = fake_bin / 'systemctl'
        fake_systemctl.write_text('#!/bin/sh\nexit 0\n')
        fake_systemctl.chmod(0o700)

        private_xdg = tmp / 'private-runtime'
        private_xdg.mkdir(mode=0o700)
        mock_live = private_xdg / 'tld-admission'
        mock_live.mkdir(mode=0o700)

        systemctl_anchor = '"/usr/bin/systemctl"'
        assert ADMIT_SOURCE.count(systemctl_anchor) == 5, 'systemctl replacement anchor changed'
        live_anchor = "Path(f'/run/user/{os.getuid()}/tld-admission')"
        assert TEST_SOURCE.count(live_anchor) == 1, 'live-state replacement anchor changed'

        broker_source = ADMIT_SOURCE.replace(
            systemctl_anchor,
            json.dumps(str(fake_systemctl)),
        )
        oracle_source = TEST_SOURCE.replace(
            live_anchor,
            f'Path({json.dumps(str(mock_live))})',
        )

        broker_write_anchor = 'return write_file_atomic(runtime_dir, leaf, text);'
        assert broker_source.count(broker_write_anchor) == 1, 'broker mutation anchor changed'

        variants = {
            'baseline': broker_source,
            'noop': broker_source,
            'default-false-publication': broker_source.replace(
                broker_write_anchor,
                'int own = write_file_atomic(runtime_dir, leaf, text); '
                'int shared = 0; return own && shared;',
            ),
            'private-writes-mock-live': broker_source.replace(
                broker_write_anchor,
                'int own = write_file_atomic(runtime_dir, leaf, text); '
                f'int shared = write_file_atomic({json.dumps(str(mock_live))}, leaf, text); '
                'return own && shared;',
            ),
        }

        env = dict(os.environ)
        env['PATH'] = str(fake_bin) + os.pathsep + env.get('PATH', '')
        env['XDG_RUNTIME_DIR'] = str(private_xdg)

        private_test = (
            'test_tld_admit.Boundary.'
            'test_a_private_runtime_never_touches_the_live_state'
        )
        default_test = (
            'test_tld_admit.Boundary.'
            'test_the_default_runtime_publishes_and_resumes_in_its_own_directory'
        )

        for variant_name in ('baseline', 'noop'):
            variant_dir = tmp / ('broker-' + variant_name)
            variant_dir.mkdir()
            executable = compile_broker(variants[variant_name], variant_dir, helpers)
            (variant_dir / 'test' / 'test_tld_admit.py').write_text(oracle_source)
            for test_name in (private_test, default_test):
                result = run_checked(
                    ['python3', '-m', 'unittest', test_name],
                    timeout=60,
                    cwd=variant_dir / 'test',
                    env=env,
                )
                label = f'{variant_name} oracle failed: {test_name}'
                assert_passed(result, label)
                print(f'passed oracle: {variant_name}: {test_name}', flush=True)

        default_dir = tmp / 'broker-default-false-publication'
        default_dir.mkdir()
        default_executable = compile_broker(
            variants['default-false-publication'], default_dir, helpers
        )
        (default_dir / 'test' / 'test_tld_admit.py').write_text(oracle_source)
        result = run_checked(
            ['python3', '-m', 'unittest', default_test],
            timeout=60,
            cwd=default_dir / 'test',
            env=env,
        )
        assert_killed(
            result,
            'default false-publication mutation was not killed',
            '75 != 0',
        )
        print('killed mutation: default publication reports success', flush=True)

        private_dir = tmp / 'broker-private-writes-mock-live'
        private_dir.mkdir()
        private_executable = compile_broker(
            variants['private-writes-mock-live'], private_dir, helpers
        )
        (private_dir / 'test' / 'test_tld_admit.py').write_text(oracle_source)
        result = run_checked(
            ['python3', '-m', 'unittest', private_test],
            timeout=60,
            cwd=private_dir / 'test',
            env=env,
        )
        assert_killed(
            result,
            'private runtime writes mock-live state mutation was not killed',
            'a private runtime changed live admission state',
        )
        print('killed mutation: private runtime writes live state', flush=True)

    print(f'{len(MUTATIONS) + 2} safety mutations detected')


if __name__ == '__main__':
    main()