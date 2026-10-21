"""Machine-specific settings of the test tools, in one place (see tools/README.md).

Every value is an environment variable. Unset ones are read from tools/local.env, then ~/.config/tld-revamp/tools.env
(KEY=value lines; copy tools/local.env.example); otherwise the defaults below (a Linux Steam + Proton install) apply.
"""
import os, pathlib, socket, sys

TOOLS = pathlib.Path(__file__).resolve().parent
_LOCAL_PATHS = {"TLD_STEAM", "TLD_DIR", "TLD_PREFIX", "TLD_BACKUPS", "TLD_CACHE", "TLD_ILSPY", "TLD_SSH_KEY", "TLD_PLUGIN_DLL",
                "TLD_TEST_COMPAT", "TLD_PROTON", "TLD_SLR"}


def _load(path):
    try: lines = path.read_text().splitlines()
    except OSError: return
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line: continue
        k, v = line.split("=", 1)
        if k.startswith("export "): k = k[7:]
        k, v = k.strip(), v.strip().strip('"').strip("'")
        if k in _LOCAL_PATHS: v = os.path.expanduser(v)   # also for the shell scripts the tools run (build.sh)
        os.environ.setdefault(k, v)


for _f in (TOOLS / "local.env", pathlib.Path.home() / ".config/tld-revamp/tools.env"):
    _load(_f)


def _path(key, default):
    return pathlib.Path(os.path.expanduser(os.environ.get(key) or str(default)))


APP_ID = "1017180"
STEAM = _path("TLD_STEAM", "~/.local/share/Steam")
# this machine's game install and Proton prefix
GAME_DIR = _path("TLD_DIR", STEAM / "steamapps/common/The Long Drive")
# Automated launches require a unique initialized test prefix, a separate dedicated GAME_DIR and an explicit port.
# tools/game.py invokes the target-side hidden-display guard; unset/owner/shared resources have no launch fallback.
TEST_COMPAT = os.path.expanduser(os.environ.get("TLD_TEST_COMPAT", ""))
PROTON = _path("TLD_PROTON", STEAM / "steamapps/common/Proton - Experimental")
SLR = _path("TLD_SLR", STEAM / "steamapps/common/SteamLinuxRuntime_4")
PREFIX = _path("TLD_PREFIX", pathlib.Path(TEST_COMPAT) / "pfx" if TEST_COMPAT else STEAM / f"steamapps/compatdata/{APP_ID}/pfx")
DOCS = PREFIX / "drive_c/users/steamuser/Documents/TheLongDrive"
PLAYER_LOG = PREFIX / "drive_c/users/steamuser/AppData/LocalLow/Genesz/TheLongDrive/Player.log"
LOG = GAME_DIR / "BepInEx/LogOutput.log"
# test results and screenshots (gitignored in the public repo)
BENCH = TOOLS.parent / "bench"
BENCH.mkdir(exist_ok=True)
# save backups of test runs, and local caches (decompiles, game diffs): never inside the repo or the game
BACKUPS = _path("TLD_BACKUPS", "~/tld-revamp-backups")
CACHE = _path("TLD_CACHE", "~/tld-revamp-cache")
ILSPY = _path("TLD_ILSPY", "~/.dotnet/tools/ilspycmd")

# The second machine of two-machine tests, as ssh's user@host (empty: single-machine tests only), and the key ssh uses
# for it (empty: ssh's own default and ~/.ssh/config).
SECOND = os.environ.get("TLD_SECOND", "")
SSH_KEY = os.path.expanduser(os.environ.get("TLD_SSH_KEY", ""))
# Paths on the second machine, as its shell sees them ($HOME expands there).
REMOTE_GAME = os.environ.get("TLD_REMOTE_DIR") or "$HOME/.local/share/Steam/steamapps/common/The Long Drive"
REMOTE_STEAM = os.environ.get("TLD_REMOTE_STEAM") or "$HOME/.local/share/Steam"
REMOTE_TEST_COMPAT = os.environ.get("TLD_REMOTE_TEST_COMPAT", "")   # as TLD_TEST_COMPAT, there
REMOTE_PROTON = os.environ.get("TLD_REMOTE_PROTON") or REMOTE_STEAM + "/steamapps/common/Proton - Experimental"
REMOTE_SLR = os.environ.get("TLD_REMOTE_SLR") or REMOTE_STEAM + "/steamapps/common/SteamLinuxRuntime_4"
REMOTE_PREFIX = os.environ.get("TLD_REMOTE_PREFIX") or (REMOTE_TEST_COMPAT + "/pfx" if REMOTE_TEST_COMPAT else f"{REMOTE_STEAM}/steamapps/compatdata/{APP_ID}/pfx")
REMOTE_DOCS = REMOTE_PREFIX + "/drive_c/users/steamuser/Documents/TheLongDrive"
REMOTE_LOG = REMOTE_GAME + "/BepInEx/LogOutput.log"
# the second machine's LAN address, for tests that join it by IP (default: the host part of TLD_SECOND)
SECOND_IP = os.environ.get("TLD_SECOND_IP") or SECOND.rpartition("@")[2]


def lan_ip():
    """This machine's LAN address (what the second machine joins by IP): TLD_HOST_IP, or the interface of the default
    route (no packet is sent)."""
    if os.environ.get("TLD_HOST_IP"): return os.environ["TLD_HOST_IP"]
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("198.51.100.1", 1)); return s.getsockname()[0]
    except OSError: return "127.0.0.1"
    finally: s.close()


def need_second():
    """The second machine's user@host, or exit with how to set it."""
    if not SECOND:
        sys.exit("this test needs a second machine: set TLD_SECOND=user@host (tools/local.env, see tools/README.md)")
    return SECOND


def ssh_args(host=None):
    """ssh with the configured key, never prompting, to `host` (default: the second machine)."""
    return ["ssh"] + (["-i", SSH_KEY] if SSH_KEY else []) + ["-o", "BatchMode=yes", host or need_second()]


def rsync_path(path):
    """A second-machine path for rsync/scp (no remote shell expands it there): relative to that machine's home."""
    for pre in ("$HOME/", "~/"):
        if path.startswith(pre): return path[len(pre):]
    return path


def remote_quote(path):
    """A remote path for a shell command line: $HOME expands, spaces don't split."""
    return '"' + path.replace('"', '\\"') + '"'
