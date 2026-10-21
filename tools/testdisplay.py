#!/usr/bin/env python3
"""Hidden, muted, low-graphics background launches of test copies of the game (Hyprland).

An automated copy of the game must never open visibly or in the foreground. Every launch of a test
copy goes through this module and tools/game.py:

  * its own game directory, its own Proton prefix, its own bridge port
  * on Hyprland the window is placed on the hidden workspace special:tld-tests, silent, no_initial_focus
  * muted (the game's own sound setting and AudioListener.volume), running in the background
  * quality level 0, vsync off, the game's own FPS limiter at 30

A plain `&`, nohup or setsid does NOT hide a window: only the compositor's per-launch rules do. The
rules belong to the launch itself (hyprctl dispatch exec), never to a global window rule.

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
GRAPHICS = {"low": GRAPHICS_LOW, "screenshot": GRAPHICS_SCREENSHOT}


def profile(name=None):
    """The test profile: the windowed size of the machine the copy runs on."""
    name = name or os.environ.get("TLD_TEST_PROFILE") or DEFAULT_PROFILE
    if name not in PROFILES:
        raise ValueError(f"unknown test profile {name!r} (known: {', '.join(sorted(PROFILES))})")
    return dict(PROFILES[name])


def settings_for(profile_name=None, graphics="low"):
    """The game's own settings (settings.tldc) for a background test copy: windowed at the profile's
    size, no vsync, the game's own FPS limiter at 30, muted, low graphics."""
    pr = profile(profile_name)
    gfx = GRAPHICS.get(graphics, GRAPHICS_LOW)
    return {**gfx,
            "IFullscreen": 3, "IResolutionX": pr["width"], "IResolutionY": pr["height"],
            "FDrawDistance": 500.0, "FMirrorDrawDistance": 10.0,
            "BVsync": False, "BEnableFpsLimit": True, "FFPSLimit": 30.0, "FSound": 0.0}


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


def prepare(game_dir, compat, profile_name=None, graphics="low"):
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


def configure(bridge, profile_name=None, graphics="low", strict=True):
    """Apply the profile to the running game through the bridge (the game's own settings win at start,
    so they are applied again as soon as the bridge answers)."""
    pr = profile(profile_name)
    errors = []

    def call(cmd):
        try:
            return bridge(cmd)
        except Exception as e:                       # noqa: BLE001 - reported, not swallowed
            errors.append(f"{cmd}: {e}")
            return None

    call("set UnityEngine.AudioListener.volume 0")
    call("set settingsscript.s.S.FSound 0")
    call("quality 0")
    call("set UnityEngine.QualitySettings.vSyncCount 0")
    call("set UnityEngine.Application.targetFrameRate 30")
    call("bg on")
    # The game's own settings: windowed at the profile's size, no vsync, 30 FPS, muted, low graphics.
    for key, value in settings_for(profile_name, graphics).items():
        call(f"set settingsscript.s.S.{key} {value}")
    # RefreshInMain dereferences the player; at the menu use Screen.SetResolution directly.
    menu = call("get DataFromMenuScript.s.mainmenu")
    if menu and menu.get("value") is False:
        call("call settingsscript.s.RefreshInMain True")
    call(f"call UnityEngine.Screen.SetResolution {pr['width']} {pr['height']} Windowed")
    # Unity's own: the lowest quality level (it applies that level's vSync, so vsync comes after it),
    # the FPS cap, running in the background, and no sound at all.
    call("quality 0")
    call("set UnityEngine.QualitySettings.vSyncCount 0")
    call("set UnityEngine.Application.targetFrameRate 30")
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


def check(state, profile_name=None):
    """Raise unless the live state is the profile (what the guard checks, plus the game's own vsync)."""
    pr = profile(profile_name)
    problems = []
    expected = {"width": pr["width"], "height": pr["height"], "fullscreen": False,
                "quality": 0, "vsync": 0, "game_vsync": False, "fps_limit_enabled": True,
                "fps_limit": 30, "target_fps": 30, "run_in_background": True,
                "audio_listener": 0, "sound": 0}
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
    expected = {"UnityEngine.AudioListener.volume": 0, "settingsscript.s.S.FSound": 0,
                "UnityEngine.QualitySettings.vSyncCount": 0, "settingsscript.s.S.BVsync": False,
                "settingsscript.s.S.BEnableFpsLimit": True, "settingsscript.s.S.FFPSLimit": 30,
                "UnityEngine.Application.targetFrameRate": 30, "UnityEngine.Application.runInBackground": True,
                "UnityEngine.Screen.fullScreen": False, "settingsscript.s.S.IFullscreen": 3,
                "settingsscript.s.S.IResolutionX": pr["width"], "settingsscript.s.S.IResolutionY": pr["height"]}
    if words[0] == "set" and len(words) >= 2 and words[1] in expected:
        value = expected[words[1]]
        try:
            actual = words[2].lower() == "true" if type(value) is bool else float(words[2])
            if type(value) is bool and words[2].lower() not in ("true", "false"):
                raise ValueError()
            if len(words) != 3 or actual != value:
                raise ValueError()
        except (ValueError, IndexError):
            raise RuntimeError("test command would violate the mandatory display/audio/FPS profile") from None
    if words[0] == "quality" and len(words) > 1 and words[1:] != ["0"]:
        raise RuntimeError("tests require quality 0")
    if words[0] == "bg" and len(words) > 1 and words[1:] != ["on"]:
        raise RuntimeError("tests require background execution")
    if words[:2] == ["call", "UnityEngine.Screen.SetResolution"]:
        if words[2:] != [str(pr["width"]), str(pr["height"]), "Windowed"]:
            raise RuntimeError("test resolution is fixed by the launch profile")


def normalise_path(path):
    """A path as Unity (Windows, under Proton) and as Linux see it: 'Z:\\dir\\game' and '/dir/game'
    are the same directory."""
    p = str(path or "").strip().strip('"').replace("\\", "/")
    if p[:2].lower() == "z:":
        p = p[2:]
    return os.path.normpath(p).rstrip("/")


def data_path_matches(data_path, game_dir):
    """True when the running game really is the copy we meant: its data folder is inside that copy's
    own game directory (never the owner's install, never another copy)."""
    d = normalise_path(data_path)
    g = normalise_path(game_dir)
    if not d or not g:
        return False
    return d == g + "/TheLongDrive_Data"


def hide_existing(*args, **kwargs):
    raise RuntimeError("post-launch movement cannot establish hidden launch; visible reuse refused")
