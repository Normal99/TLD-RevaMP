#!/usr/bin/env python3
"""Hidden, muted background launches of test copies of the game (Hyprland).

An automated copy of the game must never open visibly or in the foreground. Every launch of a test
copy goes through this module and tools/game.py:

  * its own game directory, its own Proton prefix, its own bridge port
  * on Hyprland the window is placed on the hidden workspace special:tld-tests, silent, no_initial_focus
  * muted (the game's own sound setting and AudioListener.volume), running in the background
  * vsync off
  * graphics (TLD_TEST_GRAPHICS): "regular" on the desktop, the settings a player plays with (effects on,
    15 km draw distance, the 60 FPS cap, Unity's quality level left as the game sets it); "low" on the second
    machine, or when asked for: quality level 0, the costly effects off, 500 m draw distance, 30 FPS
  * the FPS cap (TLD_TEST_FPS): the graphics' own by default; 0 (or "off") runs uncapped, a number caps there

A plain `&`, nohup or setsid does NOT hide a window: only the compositor's per-launch rules do. The
rules belong to the launch itself (hyprctl dispatch exec), never to a global window rule.

Headless (the default on this desktop when gamescope is installed): the game runs inside
`gamescope --backend headless`, a nested compositor with no output, so no window reaches the desktop at all
and the GPU still renders (screenshots work). Measured 2026-10-21 on Hyprland 0.56.2: the per-launch exec
rule above did not reach the game's window under Proton (it opened on the user's workspace), so the hidden
workspace alone is not enough. TLD_HEADLESS=0 launches without gamescope (the hidden-workspace route), for
runs whose numbers must not include gamescope (performance, VR).

The second machine runs the same rules: the SSH shell has no compositor session, so it is recovered
from the running Steam client before hyprctl is called (remote_background_command).
"""
import json
import os
import pathlib
import shlex
import shutil
import subprocess
import sys

import tldenv
from testprefix import LOW_GFX


# The hidden workspace the test copies live on. A special workspace is never shown in the layout.
WORKSPACE = "special:tld-tests"
# Proton gives the game's window this class (steam_app_<app id>).
GAME_CLASS = "steam_app_" + tldenv.APP_ID

# The two supported test profiles: the desktop (this machine) and the second machine.
PROFILES = {
    "desktop": {"width": 1280, "height": 720},
    "remote": {"width": 800, "height": 450},
}
DEFAULT_PROFILE = "desktop"

# Expensive effects off (the game's own settings). The screenshot preset keeps what a screenshot
# needs to look like the real game (sky, fog, smoke) and leaves the costly ones off.
GRAPHICS_LOW = dict(LOW_GFX)
GRAPHICS_SCREENSHOT = {**LOW_GFX, "BSkyObjs": True, "BFog": True, "BSmoke": True}
# The settings a player plays with (the desktop owner's own, 2026-10-21): what a test copy shows by default on the
# desktop, so a test sees the game the way it is played.
GRAPHICS_REGULAR = {"BEnableShadows": True, "BMirrors": True, "BPostProcessing": True, "BDof": False,
                    "BEnableSunGlassesEffects": True, "BHeatHaze": False, "BReflection": True, "BGrass": True,
                    "BSmoke": True, "BFog": True, "BAntialiasing": True, "IAnisotropic": 1, "BCloudShadow": True,
                    "BSkyObjs": True}
GRAPHICS = {"low": GRAPHICS_LOW, "screenshot": GRAPHICS_SCREENSHOT, "regular": GRAPHICS_REGULAR}
# What each graphics mode sets beside the effects: draw distances, the FPS cap, and whether Unity's quality level is
# forced to 0 (None: left as the game sets it).
MODE = {"low": {"draw": 500.0, "mirror": 10.0, "fps": 30, "quality": 0},
        "screenshot": {"draw": 500.0, "mirror": 10.0, "fps": 30, "quality": 0},
        "regular": {"draw": 15000.0, "mirror": 100.0, "fps": 60, "quality": None}}


def mode_settings(profile_name=None, graphics=None):
    """MODE for this run, with the FPS cap TLD_TEST_FPS asks for: a number caps there, 0 or "off" runs uncapped (fps
    None: the game's limiter off, Unity's targetFrameRate -1), for runs that measure what the game can do."""
    m = dict(MODE[graphics_mode(profile_name, graphics)])
    want = (os.environ.get("TLD_TEST_FPS") or "").strip().lower()
    if want in ("0", "off", "none", "uncapped"):
        m["fps"] = None
    elif want:
        m["fps"] = int(float(want))
    return m


def graphics_mode(profile_name=None, graphics=None):
    """The graphics a test copy runs with: the caller's, else TLD_TEST_GRAPHICS, else regular on the desktop and low
    on the second machine (it is the slower one)."""
    name = graphics or os.environ.get("TLD_TEST_GRAPHICS") or ("low" if (profile_name or os.environ.get("TLD_TEST_PROFILE")
                                                                          or DEFAULT_PROFILE) == "remote" else "regular")
    if name not in GRAPHICS:
        raise ValueError(f"unknown test graphics {name!r} (known: {', '.join(sorted(GRAPHICS))})")
    return name


def profile(name=None):
    """The test profile: the windowed size of the machine the copy runs on."""
    name = name or os.environ.get("TLD_TEST_PROFILE") or DEFAULT_PROFILE
    if name not in PROFILES:
        raise ValueError(f"unknown test profile {name!r} (known: {', '.join(sorted(PROFILES))})")
    return dict(PROFILES[name])


def settings_for(profile_name=None, graphics=None):
    """The game's own settings (settings.tldc) for a background test copy: windowed at the profile's
    size, no vsync, the game's own FPS limiter on, muted, the graphics of graphics_mode."""
    pr = profile(profile_name)
    mode = graphics_mode(profile_name, graphics)
    m = mode_settings(profile_name, graphics)
    return {**GRAPHICS[mode],
            "IFullscreen": 3, "IResolutionX": pr["width"], "IResolutionY": pr["height"],
            "FDrawDistance": m["draw"], "FMirrorDrawDistance": m["mirror"],
            "BVsync": False, "BEnableFpsLimit": m["fps"] is not None, "FFPSLimit": float(m["fps"] or MODE[mode]["fps"]),
            "FSound": 0.0}


def launch_rules(profile_name=None):
    """The compositor rules of one launch: the hidden workspace, silent (no output switch), floating,
    the profile's size, and no initial focus (the window never takes the keyboard)."""
    pr = profile(profile_name)
    return (f"[workspace {WORKSPACE} silent; float; size {pr['width']} {pr['height']}; "
            "no_initial_focus; suppress_event fullscreen maximize] ")


def hypr_env(runtime_dir=None, signature=None):
    """Recover exactly one target-side socket; never guess a session."""
    if not shutil.which("hyprctl"):
        raise RuntimeError("hyprctl unavailable; hidden launch refused")
    env = os.environ.copy()
    runtime = pathlib.Path(runtime_dir or env.get("XDG_RUNTIME_DIR") or f"/run/user/{os.getuid()}")
    base = runtime / "hypr"
    sockets = sorted(p for p in base.iterdir() if (p / ".socket.sock").is_socket()) if base.is_dir() else []
    sig = signature or env.get("HYPRLAND_INSTANCE_SIGNATURE")
    if len(sockets) != 1 or (sig and sockets[0].name != sig):
        raise RuntimeError("missing, ambiguous or stale Hyprland session; hidden launch refused")
    env.update(XDG_RUNTIME_DIR=str(runtime), HYPRLAND_INSTANCE_SIGNATURE=sockets[0].name)
    preflight(env)
    return env


def query(kind, env):
    try:
        return json.loads(subprocess.check_output(["hyprctl", "-j", kind], env=env, timeout=5))
    except Exception as exc:
        raise RuntimeError("Hyprland unreachable; hidden launch refused") from exc


def preflight(env):
    monitors = query("monitors", env)
    if not monitors or any((m.get("specialWorkspace") or {}).get("name") == WORKSPACE for m in monitors):
        raise RuntimeError("test workspace is visible or no monitor route exists")
    query("clients", env)


def headless_wanted(remote=False):
    """Run this launch inside headless gamescope? TLD_HEADLESS=1 forces it, 0 turns it off; by default it is on
    for local launches when gamescope is installed."""
    value = os.environ.get("TLD_HEADLESS", "").strip()
    if value in ("0", "false", "no", "off"):
        return False
    have = shutil.which("gamescope") is not None
    if value in ("1", "true", "yes", "on"):
        if not have:
            raise RuntimeError("TLD_HEADLESS=1 but gamescope is not installed")
        return True
    return have and not remote


def headless_prefix(profile_name=None):
    """The nested compositor a headless launch runs the game in: no window, no output, the profile's size."""
    pr = profile(profile_name)
    exe = shutil.which("gamescope")
    if not exe:
        raise RuntimeError("gamescope is not installed")
    return [exe, "--backend", "headless", "-W", str(pr["width"]), "-H", str(pr["height"]),
            "-w", str(pr["width"]), "-h", str(pr["height"]), "--"]


def background_command(command, profile_name=None, env=None):
    env = env if env is not None else hypr_env()
    preflight(env)
    assignments = " ".join(shlex.quote(k + "=" + env[k]) for k in ("XDG_RUNTIME_DIR", "HYPRLAND_INSTANCE_SIGNATURE"))
    return "env " + assignments + " hyprctl dispatch exec " + shlex.quote(launch_rules(profile_name) + command)


# Remote launch uses the same Python guard on the target, never a shell guess of a session.
REMOTE_RECOVER = ""


def remote_background_command(command, profile_name=None):
    raise RuntimeError("remote launches must use game.prefix_launch_cmd target-side guard")


# The game's own settings schema, so a fresh prefix can be seeded without a game
# run and without a player's file.  It carries the key set and the value types only.
TEMPLATE = pathlib.Path(__file__).resolve().parent / "settings-template.tldc"
SETTINGS_REL = "pfx/drive_c/users/steamuser/Documents/TheLongDrive/settings.tldc"


def template_settings():
    """The checked-in settings template: the game's schema, no player data."""
    try:
        data = json.loads(TEMPLATE.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise RuntimeError("the settings template is missing or unreadable; refusing to start the game") from exc
    if not isinstance(data, dict) or not data:
        raise RuntimeError("the settings template is not a settings object; refusing to start the game")
    return data


def settings_path(compat):
    return pathlib.Path(compat).expanduser().resolve() / SETTINGS_REL


def seed_settings(compat):
    """Create the prefix's settings file from the template when it is missing.

    A fresh prefix has no settings file, and the game would then start with its own
    defaults -- audible, and outside the mandatory profile.  Seeding the template
    gives the launcher something to patch, so the startup state is known before the
    first frame.  The seeded file is marked by a sibling marker, so a later
    `testprefix.py make` cannot silently strip it back to nothing.
    """
    target = settings_path(compat)
    if target.is_file():
        return False
    data = template_settings()
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix(".tldc.tmp")
    tmp.write_text(json.dumps(data, indent=4))
    tmp.replace(target)
    marker = target.parent / "settings.tldc.seeded"
    marker.write_text("seeded from the repository settings template\n")
    return True


def prepare(game_dir, compat, profile_name=None, graphics=None):
    """Apply the profile to the test copy's own prefix before the game can open a fullscreen window.
    Returns the display arguments for the game's command line."""
    prefix = pathlib.Path(compat).expanduser().resolve()
    owner = (tldenv.STEAM / "steamapps/compatdata" / tldenv.APP_ID).resolve()
    if prefix == owner:
        raise RuntimeError("background test copies need their own Proton prefix, not the one you play in")
    settings = settings_path(compat)
    seed_settings(compat)
    if not settings.is_file():
        raise RuntimeError("missing settings.tldc: cannot prove startup mute")
    data = json.loads(settings.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict):
        raise RuntimeError("the prefix's settings file is not a settings object; refusing to start the game")
    backup = prefix / "tld-test-settings-before.json"
    if not backup.exists():
        shutil.copy2(settings, backup)
    data.update(settings_for(profile_name, graphics))
    tmp = settings.with_suffix(".tldc.tmp")
    tmp.write_text(json.dumps(data, indent=4))
    tmp.replace(settings)
    # The flags also cover the first launch, before a settings file exists.
    pr = profile(profile_name)
    return ("-screen-fullscreen", "0", "-screen-width", str(pr["width"]), "-screen-height", str(pr["height"]))


def configure(bridge, profile_name=None, graphics=None, strict=True):
    """Apply the profile to the running game through the bridge (the game's own settings win at start,
    so they are applied again as soon as the bridge answers)."""
    pr = profile(profile_name)
    m = mode_settings(profile_name, graphics)
    errors = []

    def call(cmd):
        try:
            return bridge(cmd)
        except Exception as e:                       # noqa: BLE001 - reported, not swallowed
            errors.append(f"{cmd}: {e}")
            return None

    call("set UnityEngine.AudioListener.volume 0")
    call("set settingsscript.s.S.FSound 0")
    if m["quality"] is not None:
        call(f"quality {m['quality']}")
    call("set UnityEngine.QualitySettings.vSyncCount 0")
    call(f"set UnityEngine.Application.targetFrameRate {m['fps'] or -1}")
    call("bg on")
    # The game's own settings: windowed at the profile's size, no vsync, the FPS cap, muted, the mode's graphics.
    for key, value in settings_for(profile_name, graphics).items():
        call(f"set settingsscript.s.S.{key} {value}")
    # RefreshInMain dereferences the player; at the menu use Screen.SetResolution directly.
    menu = call("get DataFromMenuScript.s.mainmenu")
    if menu and menu.get("value") is False:
        call("call settingsscript.s.RefreshInMain True")
    call(f"call UnityEngine.Screen.SetResolution {pr['width']} {pr['height']} Windowed")
    # Unity's own: the quality level when the mode forces one (it applies that level's vSync, so vsync comes after
    # it), the FPS cap, running in the background, and no sound at all.
    if m["quality"] is not None:
        call(f"quality {m['quality']}")
    call("set UnityEngine.QualitySettings.vSyncCount 0")
    call(f"set UnityEngine.Application.targetFrameRate {m['fps'] or -1}")
    call("bg on")
    call("set UnityEngine.AudioListener.volume 0")
    call("call settingsscript.s.SaveSettings")
    if strict and errors:
        raise RuntimeError("could not apply the test profile: " + "; ".join(errors[:4]))
    return errors


def verify(bridge, profile_name=None):
    """The live state of the game, as the operator's guard reads it."""
    def get(path):
        return bridge(f"get {path}").get("value")

    quality = bridge("quality")
    bg = bridge("bg")
    return {
        "width": get("UnityEngine.Screen.width"),
        "height": get("UnityEngine.Screen.height"),
        "fullscreen": get("UnityEngine.Screen.fullScreen"),
        "quality": quality.get("level"),
        "vsync": get("UnityEngine.QualitySettings.vSyncCount"),
        "game_vsync": get("settingsscript.s.S.BVsync"),
        "fps_limit_enabled": get("settingsscript.s.S.BEnableFpsLimit"),
        "fps_limit": get("settingsscript.s.S.FFPSLimit"),
        "target_fps": quality.get("targetFps"),
        "run_in_background": bg.get("runInBackground"),
        "focused": bg.get("focused"),
        "audio_listener": get("UnityEngine.AudioListener.volume"),
        "sound": get("settingsscript.s.S.FSound"),
    }


def check(state, profile_name=None, graphics=None):
    """Raise unless the live state is the profile (what the guard checks, plus the game's own vsync)."""
    pr = profile(profile_name)
    m = mode_settings(profile_name, graphics)
    problems = []
    expected = {"width": pr["width"], "height": pr["height"], "fullscreen": False,
                "vsync": 0, "game_vsync": False, "fps_limit_enabled": m["fps"] is not None,
                "target_fps": m["fps"] or -1, "run_in_background": True,
                "audio_listener": 0, "sound": 0}
    if m["fps"] is not None:
        expected["fps_limit"] = m["fps"]
    if m["quality"] is not None:
        expected["quality"] = m["quality"]
    for key, value in expected.items():
        if state.get(key) is None or state[key] != value:
            problems.append(f"{key}: expected {value}, got {state.get(key)}")
    if problems:
        raise RuntimeError("the test profile is not in effect: " + "; ".join(problems))
    return state

def validate_command(command, profile_name=None):
    """Do not let a test or an old restore journal undo the mandatory resource profile."""
    if "\n" in command or "\r" in command:
        raise RuntimeError("bridge command must be exactly one line")
    words = command.split()
    if not words:
        raise RuntimeError("empty bridge command")
    pr = profile(profile_name)
    m = mode_settings(profile_name)
    # A cap at or below the mode's is allowed (a benchmark may ask for 30 under the regular 60); a higher one is not.
    capped = ("settingsscript.s.S.FFPSLimit", "UnityEngine.Application.targetFrameRate")
    expected = {"UnityEngine.AudioListener.volume": 0, "settingsscript.s.S.FSound": 0,
                "UnityEngine.QualitySettings.vSyncCount": 0, "settingsscript.s.S.BVsync": False,
                "settingsscript.s.S.BEnableFpsLimit": True, "settingsscript.s.S.FFPSLimit": m["fps"],
                "UnityEngine.Application.targetFrameRate": m["fps"], "UnityEngine.Application.runInBackground": True,
                "UnityEngine.Screen.fullScreen": False, "settingsscript.s.S.IFullscreen": 3,
                "settingsscript.s.S.IResolutionX": pr["width"], "settingsscript.s.S.IResolutionY": pr["height"]}
    if m["fps"] is None:
        # uncapped: any cap a benchmark asks for is lower than none at all
        for key in capped + ("settingsscript.s.S.BEnableFpsLimit",):
            expected.pop(key)
    if words[0] == "set" and len(words) >= 2 and words[1] in expected:
        value = expected[words[1]]
        try:
            actual = words[2].lower() == "true" if type(value) is bool else float(words[2])
            if type(value) is bool and words[2].lower() not in ("true", "false"):
                raise ValueError()
            if len(words) != 3 or not (actual == value or (words[1] in capped and 0 < actual <= value)):
                raise ValueError()
        except (ValueError, IndexError):
            raise RuntimeError("test command would violate the mandatory display/audio/FPS profile") from None
    if words[0] == "quality" and len(words) > 1 and m["quality"] is not None and words[1:] != [str(m["quality"])]:
        raise RuntimeError(f"tests in these graphics require quality {m['quality']}")
    if words[0] == "bg" and len(words) > 1 and words[1:] != ["on"]:
        raise RuntimeError("tests require background execution")
    if words[:2] == ["call", "UnityEngine.Screen.SetResolution"]:
        if words[2:] != [str(pr["width"]), str(pr["height"]), "Windowed"]:
            raise RuntimeError("test resolution is fixed by the launch profile")


def normalise_path(path, root_drives=()):
    """A path as Unity (Windows, under Proton) and as Linux see it: 'Z:\\dir\\game' and '/dir/game'
    are the same directory. root_drives: other drive letters that this prefix maps to '/' (Wine mounts the root
    filesystem's device as D: on some machines, and the game then reports 'D:/dir/game')."""
    p = str(path or "").strip().strip('"').replace("\\", "/")
    if p[:2].lower() == "z:" or (len(p) > 2 and p[1] == ":" and p[0].lower() in root_drives):
        p = p[2:]
    return os.path.normpath(p).rstrip("/")


def drive_letter(path):
    """The drive letter of a Windows path ('d' for 'D:/x'), or None."""
    p = str(path or "").strip().strip('"')
    return p[0].lower() if len(p) > 2 and p[1] == ":" and p[0].isalpha() else None


def data_path_matches(data_path, game_dir, root_drives=()):
    """True when the running game really is the copy we meant: its data folder is inside that copy's
    own game directory (never the owner's install, never another copy). A drive letter other than Z: counts only
    when the caller has read the prefix's own dosdevices link for it and it points at '/'."""
    d = normalise_path(data_path, root_drives)
    g = normalise_path(game_dir)
    if not d or not g:
        return False
    return d == g + "/TheLongDrive_Data"


def hide_existing(*args, **kwargs):
    raise RuntimeError("post-launch movement cannot establish hidden launch; visible reuse refused")
