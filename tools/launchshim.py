#!/usr/bin/env python3
"""The seam between the compositor dispatch and the admitted systemd unit.

Hyprland gives a dispatched process HL_EXEC_RULE_TOKEN, and the per-launch rule
(hidden workspace, silent, float, size, no_initial_focus) applies to every window
whose process carries that token -- the rule is matched on the environment, not on
process ancestry, so it survives a re-parent.  The admission broker, however,
starts the game in its own systemd unit, and systemd-run does not pass the
caller's environment into that unit: the token would be lost exactly where it is
needed, and the window would map without the launch-time rules.

Outer mode runs inside the compositor dispatch: the compositor token is still in
the environment there.  It writes the token to a private file and hands over to
the admission broker, which starts the unit that runs inner mode.

Inner mode runs inside the unit: it reads the file, restores the compositor token
and the launch token, and execs the game.

No token means no launch: if the compositor applied no per-launch rule, the outer
mode refuses and nothing is started.  No token value is ever printed.
"""
import os
import sys

TOKEN_VARS = ('HL_EXEC_RULE_TOKEN', 'TLD_TEST_LAUNCH_TOKEN')

# The admission broker's own overrides.  The compositor's dispatch runs the
# command through its own shell and passes no environment on, so these have to
# be set inside the command line to reach the broker at all.
ADMIT_VARS = ('TLD_ADMIT_RUNTIME_DIR', 'TLD_ADMIT_MEMINFO', 'TLD_ADMIT_WATCHED_CGROUP',
              'TLD_ADMIT_PSI', 'TLD_ADMIT_VMSTAT', 'TLD_ADMIT_TEST_NO_WATCH')


def die(message):
    # Never echo an environment value, a token or a path from the environment.
    sys.stderr.write('tld launch shim: ' + message + '\n')
    raise SystemExit(75)


def write_pairs(path, pairs):
    directory = os.path.dirname(path) or '.'
    try:
        os.makedirs(directory, mode=0o700, exist_ok=True)
        info = os.stat(directory)
        if info.st_uid != os.getuid() or (info.st_mode & 0o077):
            die('token directory is not private')
        tmp = path + '.tmp'
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w', encoding='utf-8') as handle:
            for key in sorted(pairs):
                handle.write(key + '=' + pairs[key] + '\n')
        os.replace(tmp, path)
    except OSError:
        die('token file is unwritable')


def read_pairs(path):
    pairs = {}
    try:
        with open(path, encoding='utf-8') as handle:
            for line in handle:
                line = line.rstrip('\n')
                if not line or '=' not in line:
                    continue
                key, value = line.split('=', 1)
                pairs[key] = value
    except OSError:
        die('token file is unreadable')
    return pairs


def outer(argv):
    """Inside the compositor dispatch: keep the token, then run the broker."""
    if len(argv) < 2 or argv[0] != '--outer':
        die('bad invocation')
    tokenfile = argv[1]
    rest = argv[2:]
    exitfile = None
    if rest and rest[0] == '--exitfile':
        if len(rest) < 2:
            die('bad invocation')
        exitfile = rest[1]
        rest = rest[2:]
    if not rest or rest[0] != '--':
        die('bad invocation')
    broker = rest[1:]
    if not broker:
        die('no admission broker')
    # The broker's overrides must be in the environment the broker inherits.
    for name in ADMIT_VARS:
        value = os.environ.get(name)
        if value:
            os.environ[name] = value
    token = os.environ.get('HL_EXEC_RULE_TOKEN')
    if not token and os.environ.get('TLD_TEST_DESKTOP') == 'none':
        # a target with no desktop session: the guard started this headless, no compositor rule exists to carry
        token = 'desktopless'
    if not token:
        die('the compositor applied no per-launch rule')
    launch_token = os.environ.get('TLD_TEST_LAUNCH_TOKEN')
    if not launch_token:
        die('no launch token')
    write_pairs(tokenfile, {'HL_EXEC_RULE_TOKEN': token, 'TLD_TEST_LAUNCH_TOKEN': launch_token})
    # The broker's own environment is the one the compositor gave us, plus the
    # launch token: the broker passes its environment on to the unit's command.
    os.environ['TLD_TEST_LAUNCH_TOKEN'] = launch_token
    # The broker runs the game to its end; wait for it and record the verdict, so
    # the controller can tell "admission refused" from "the game did not start".
    pid = os.fork()
    if pid == 0:
        try:
            os.execv(broker[0], broker)
        except OSError:
            os._exit(75)
    _, status = os.waitpid(pid, 0)
    code = os.waitstatus_to_exitcode(status) if hasattr(os, 'waitstatus_to_exitcode') else 75
    if exitfile:
        try:
            with open(exitfile, 'w', encoding='utf-8') as handle:
                handle.write(str(code) + '\n')
        except OSError:
            pass
    raise SystemExit(code if 0 <= code <= 255 else 75)


def inner(argv):
    """Inside the admitted unit: restore the tokens, then exec the game."""
    if len(argv) < 2 or argv[0] != '--inner':
        die('bad invocation')
    tokenfile = argv[1]
    rest = argv[2:]
    cwd = None
    log = None
    while rest and rest[0] != '--':
        if rest[0] in ('--cwd', '--log'):
            if len(rest) < 2:
                die('bad invocation')
            if rest[0] == '--cwd':
                cwd = rest[1]
            else:
                log = rest[1]
            rest = rest[2:]
            continue
        die('bad invocation')
    if not rest or rest[0] != '--':
        die('bad invocation')
    game = rest[1:]
    if not game:
        die('no game command')
    pairs = read_pairs(tokenfile)
    for name in TOKEN_VARS:
        if not pairs.get(name):
            die('token file is incomplete')
    for name in TOKEN_VARS:
        os.environ[name] = pairs[name]
    if cwd:
        try:
            os.chdir(cwd)
        except OSError:
            die('the game directory is not reachable')
    if log:
        try:
            handle = os.open(log, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        except OSError:
            die('the launch log is not writable')
        os.dup2(handle, 1)
        os.dup2(handle, 2)
        os.close(handle)
    try:
        null = os.open(os.devnull, os.O_RDONLY)
    except OSError:
        null = 0
    os.dup2(null, 0)
    os.execvpe(game[0], game, os.environ)
    die('the game command could not be started')


def main(argv):
    if not argv:
        die('bad invocation')
    if argv[0] == '--outer':
        return outer(argv)
    if argv[0] == '--inner':
        return inner(argv)
    die('bad invocation')


if __name__ == '__main__':
    main(sys.argv[1:])
