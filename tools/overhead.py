#!/usr/bin/env python3
"""Measurement-tool overhead check: standing still, compare quiet vs bridge `get` commands at 20 Hz.
If "spam" is clearly worse than "quiet", the bench itself distorts results. Run after changing the bridge."""
import sys, time, json
sys.path.insert(0, __import__("os").path.dirname(__file__))
from bench import Bridge, apply_changes, restore_settings
b = Bridge()
restore = apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "False"),
                            ("kaposztaleves.s.settings.god", "True"), ("gamemodesettingsscript.s.S.survival", "1")])
b("bg on")
try:
    for label, spam in (("quiet", False), ("spam get 20Hz", True), ("quiet again", False)):
        b("reset"); t_end = time.time() + 15
        while time.time() < t_end:
            if spam: b("get mainscript.s.player.transform.position")
            time.sleep(0.05)
        s = b("stats"); bd = b("breakdown")
        print(f"{label:15} fps {s['fps']:6} p99 {s['p99ms']:6} max {s['maxms']:6} gc {s['gc0']:3} slow>25ms {bd['slowFrames']}")
finally:
    restore_settings(restore)
