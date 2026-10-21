#!/usr/bin/env python3
"""Talk to the verified hidden test copy. Usage: tools/tld.py stats"""
import json
import pathlib
import sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from bench import Bridge

bridge = Bridge()
for command in sys.argv[1:] or ["ping"]:
    print(json.dumps(bridge(command)))
