#!/usr/bin/env python3
"""A harmless synthetic GUI window for the containment proof.

It maps one window, reports its own pid and size, and exits on its own.
It is not the game and it touches nothing but the compositor.
"""
import os, sys, threading, time
import gi
gi.require_version('Gtk', '3.0')
from gi.repository import Gtk, Gdk

width = int(sys.argv[1]) if len(sys.argv) > 1 else 1280
height = int(sys.argv[2]) if len(sys.argv) > 2 else 720
out = sys.argv[3] if len(sys.argv) > 3 else None

win = Gtk.Window(title='tld-synthetic-probe')
win.set_default_size(width, height)
win.set_size_request(width, height)
win.set_decorated(False)

def report():
    time.sleep(0.6)
    if out:
        with open(out, 'w', encoding='utf-8') as handle:
            handle.write('pid=%d\n' % os.getpid())
            handle.write('cgroup=%s\n' % open('/proc/self/cgroup').read().strip())
            handle.write('token=%s\n' % ('yes' if 'HL_EXEC_RULE_TOKEN' in os.environ else 'no'))
            handle.write('launch_token=%s\n' % ('yes' if 'TLD_TEST_LAUNCH_TOKEN' in os.environ else 'no'))
            handle.write('width=%d\n' % win.get_allocated_width())
            handle.write('height=%d\n' % win.get_allocated_height())

threading.Thread(target=report, daemon=True).start()
GLib_timeout = None
from gi.repository import GLib
GLib.timeout_add(8000, lambda: (win.destroy(), False)[1])
win.connect('destroy', lambda *_: Gtk.main_quit())
win.show_all()
Gtk.main()
