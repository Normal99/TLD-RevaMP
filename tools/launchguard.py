"""Controller for the identical local/remote target guard (source shipped through SSH stdin)."""
import hashlib
import json
import os
import pathlib
import shlex
import subprocess
import sys

import tldenv


def specification(remote=False, game_dir=None, compat=None, port=None):
    raw_port = port if port is not None else os.environ.get('TLD_REMOTE_BRIDGE_PORT' if remote else 'TLD_BRIDGE_PORT')
    if raw_port is None:
        raise RuntimeError('set an explicit unique TLD_BRIDGE_PORT / TLD_REMOTE_BRIDGE_PORT')
    selected_compat = str(compat if compat is not None else (tldenv.REMOTE_TEST_COMPAT if remote else tldenv.TEST_COMPAT))
    return {'prefix': selected_compat+'/pfx' if compat is not None else str(tldenv.REMOTE_PREFIX if remote else tldenv.PREFIX),
            'game': str(game_dir or (tldenv.REMOTE_GAME if remote else tldenv.GAME_DIR)),
            'compat': selected_compat,
            'steam': str(tldenv.REMOTE_STEAM if remote else tldenv.STEAM),
            'proton': str(tldenv.REMOTE_PROTON if remote else tldenv.PROTON),
            'slr': str(tldenv.REMOTE_SLR if remote else tldenv.SLR),
            'profile': 'remote' if remote else 'desktop', 'port': int(raw_port),
            'desktop': tldenv.REMOTE_DESKTOP if remote else 'session'}


BROKER_STAGE_SCRIPT = """
import hashlib as _hashlib

def _stage_broker(_src, _root):
    _src = pathlib.Path(_src)
    if not _src.is_file():
        return None
    _data = _src.read_bytes()
    _digest = _hashlib.sha256(_data).hexdigest()
    _root = pathlib.Path(_root)
    _root.mkdir(parents=True, exist_ok=True)
    try:
        os.chmod(_root, 0o700)
    except OSError:
        pass
    _target = _root / _digest / _src.name
    _target.parent.mkdir(parents=True, exist_ok=True)
    _tmp = _target.with_suffix('.staging')
    _tmp.write_bytes(_data)
    os.chmod(_tmp, 0o755)
    os.replace(_tmp, _target)
    if _hashlib.sha256(_target.read_bytes()).hexdigest() != _digest:
        raise RuntimeError('the staged broker does not match its digest')
    return str(_target)

def _find_broker():
    _candidates = []
    _override = os.environ.get('TLD_ADMIT_BIN')
    if _override:
        _candidates.append(pathlib.Path(_override))
    _candidates.append(pathlib.Path(__file__).parent / 'tld-admit')
    for _base in sorted(pathlib.Path.home().glob('tld-revamp-free-*/tools/tld-admit')):
        _candidates.append(_base)
    _root = os.path.join(os.environ.get('XDG_RUNTIME_DIR', '/tmp'), 'tld-admit')
    for _path in _candidates:
        if _path.is_file() and os.access(_path, os.X_OK):
            _staged = _stage_broker(_path, _root)
            if _staged:
                return _staged
    return None

_found = _find_broker()
if _found:
    os.environ['TLD_ADMIT_BIN'] = _found
"""


def stage_broker(source, destination_root):
    """Stage the admission broker at a digest-addressed path on the target.

    The staged file's path carries the SHA-256 of its own bytes, so the target
    can verify what it is about to execute without trusting the transfer: the
    digest is recomputed from the staged bytes and must equal the directory
    name.  A mismatch is a refusal, never a run.
    """
    import hashlib
    source = pathlib.Path(source)
    if not source.is_file():
        raise RuntimeError('no admission broker to stage')
    data = source.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    destination_root = pathlib.Path(destination_root)
    destination_root.mkdir(parents=True, exist_ok=True)
    try:
        os.chmod(destination_root, 0o700)
    except OSError:
        pass
    target = destination_root / digest / source.name
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix('.staging')
    tmp.write_bytes(data)
    os.chmod(tmp, 0o755)
    os.replace(tmp, target)
    # verify what was written, not what was sent
    staged = target.read_bytes()
    if hashlib.sha256(staged).hexdigest() != digest:
        raise RuntimeError('the staged broker does not match its digest; refusing to run it')
    return str(target)


def program(spec, action):
    # Minimal dependencies rather than importing target-side local.env (which could point at an owner copy).
    from testprefix import LOW_GFX
    folder = pathlib.Path(__file__).parent
    sources = {name: (folder / (name+'.py')).read_text() for name in ('testdisplay', 'testlaunch')}
    shim = folder / 'launchshim.py'
    if not shim.is_file():
        raise RuntimeError('the launch shim is missing; the target cannot compose a contained launch')
    # The broker may live in another checkout on the target; the guard looks for it there.
    broker_dir = str(folder)
    return ('import sys, types, pathlib, json, os, tempfile\n'
            'import tempfile as _tf\n'
            '_dir=_tf.mkdtemp(prefix="tld-launch-")\n'
            'os.chmod(_dir,0o700)\n'
            '__file__=os.path.join(_dir,"tld-guard-program.py")\n'
            't=types.ModuleType("tldenv"); t.APP_ID="1017180"; '
            f't.STEAM=pathlib.Path(os.path.expanduser(os.path.expandvars({spec["steam"]!r}))); sys.modules["tldenv"]=t\n'
            f't=types.ModuleType("testprefix"); t.LOW_GFX={LOW_GFX!r}; sys.modules["testprefix"]=t\n'
            + ''.join(f'm=types.ModuleType({name!r}); sys.modules[{name!r}]=m; m.__dict__["__file__"]=os.path.join(_dir,{name!r}+".py"); exec({src!r},m.__dict__)\n' for name,src in sources.items())
            + f'shim_src={shim.read_text()!r}\n'
            + f'os.environ["TLD_ADMIT_SEARCH_DIR"]={broker_dir!r}\n'
            + BROKER_STAGE_SCRIPT
            + 'import sys as _sys\n'
            + 'shim_path=os.path.join(_dir,"launchshim.py")\n'
            + 'open(shim_path,"w",encoding="utf-8").write(shim_src)\n'
            + 'os.chmod(shim_path,0o700)\n'
            + '_sys.modules["testlaunch"].__dict__["SHIM_PATH"]=shim_path\n'
            + f'try:\n    result=sys.modules["testlaunch"].operate({spec!r}, {action!r})\n    print(json.dumps(result))\n'
            + 'except Exception as exc:\n    print(json.dumps({"guard_error": str(exc) if isinstance(exc, RuntimeError) else "target identity/preflight unavailable: " + type(exc).__name__ + ": " + str(exc)[:200]}))\n    sys.exit(64)\n')


def command(spec, action):
    return 'python3 -c ' + shlex.quote(program(spec, action))


def run(spec, action, remote=None):
    source = program(spec, action)
    argv = tldenv.ssh_args(remote) + ['python3 -'] if remote else [sys.executable, '-']
    out = subprocess.run(argv, input=source, capture_output=True, text=True)
    if out.returncode:
        # Never echo stderr or an environment dump. Guard errors are fixed policy messages.
        try:
            reason = json.loads(out.stdout).get("guard_error", "target unavailable")
        except ValueError:
            reason = "target unavailable"
        raise RuntimeError('target guard refused '+action+': '+reason)
    return json.loads(out.stdout)


def fault(action, remote=None, seconds=0):
    """Fault injection uses the same exact PID/start lease as cleanup."""
    import time
    spec = specification(bool(remote))
    run(spec, "verify", remote)
    if action == "pause":
        try:
            run(spec, "pause", remote)
            time.sleep(seconds)
        finally:
            run(spec, "resume", remote)
    elif action == "crash":
        run(spec, "crash", remote)
    else:
        raise RuntimeError("unsupported fault action")
