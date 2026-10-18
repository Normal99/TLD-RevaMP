#!/usr/bin/env python3
"""Compare local telemetry: python tools/telemetry_compare.py <folder or files...> [-o out_dir].
   Run vs run: python tools/telemetry_compare.py --runs <runA>[,<repeat>...] <runB>[,...] [--names A B] [-o out_dir].

Python 3.9+, standard library only. No files are extracted from report archives.
Header (sid, role, start, hw) identifies a machine across its log and snapshots;
without start, the containing report/directory is the identity fallback.
"""
import argparse
from collections import defaultdict
from dataclasses import dataclass, field
import hashlib
import html
import json
import math
from pathlib import Path
import re
import statistics
import sys
import zipfile
import zlib


def number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def obj(value):
    return value if isinstance(value, dict) else {}


def pretty(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True) if isinstance(value, (dict, list)) else str(value)


def esc(value):
    return html.escape(re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f]", "", str(value)), quote=True)


def flatten(value, prefix=""):
    result = {}
    entries = enumerate(value) if isinstance(value, list) else obj(value).items()
    for key, val in entries:
        name = prefix + str(key)
        if isinstance(val, (dict, list)):
            result.update(flatten(val, name + "."))
        else:
            result[name] = val
    return result


def phases(record):
    result = dict(obj(record.get("phases")))
    for key, value in record.items():
        if key.startswith(("phase.", "phase:", "phases.")):
            result[key.split("." if "." in key else ":", 1)[1]] = value
    return {str(k): v for k, v in result.items() if number(v) is not None and v > 0}


def gc(record):
    value = record.get("gc")
    if isinstance(value, dict):
        values = value.values()
    elif isinstance(value, list):
        values = value
    else:
        values = [1 if value is True else value, record.get("gc0"), record.get("gc1"), record.get("gc2")]
    return (any(number(v) is not None and v > 0 for v in values)
            or phases(record).get("GC.Collect", 0) > 0)


def backlog(record):
    if obj(record.get("genDetails")):
        return {str(k): v for k, v in record["genDetails"].items() if k != "sampleAgeS" and number(v) is not None and v >= 0}
    value = record.get("gen", record.get("genTotal"))
    if isinstance(value, dict):
        return {str(k): v for k, v in value.items() if k != "sampleAgeS" and number(v) is not None and v >= 0}
    return {"total": value} if number(value) is not None and value >= 0 else {}


FRAME_FIELDS = set("st t ms phases gc gc0 gc1 gc2 rebase stream saving save loading gen genTotal bytesSent bytesReceived sentBytes recvBytes txBytes rxBytes sent received speedKmh ping pingMs rtt rttMs loss lossPct lossPercent".split())


@dataclass
class Capture:
    head: dict
    records: list
    source: str
    owner: str
    moment: object = None
    blackbox: bool = False
    state: dict = field(default_factory=dict)
    events: dict = field(default_factory=dict)

    @property
    def identity(self):
        h = self.head
        return (str(h.get("sid") or ""), str(h.get("role") or "unknown"),
                str(h.get("start") or self.owner), pretty(h.get("hw", {})))


@dataclass
class Machine:
    identity: tuple
    captures: list = field(default_factory=list)
    label: str = ""

    @property
    def head(self):
        result = {}
        for capture in self.captures:
            result.update(capture.head)
        return result


@dataclass
class Moment:
    sid: str
    st: object
    machines: list
    notes: list = field(default_factory=list)


def parse_text(text, source, owner, warnings):
    """Accept black boxes and JSONL, retaining valid lines of interrupted logs."""
    captures = []
    try:
        value = json.loads(text)
    except (ValueError, RecursionError):
        value = None
    if isinstance(value, dict) and isinstance(value.get("rows"), list):
        cols = value.get("cols")
        if not isinstance(cols, list) or not all(isinstance(c, str) for c in cols):
            warnings.append(source + ": invalid black-box columns; skipped")
            return []
        rows = []
        for row in value["rows"]:
            if isinstance(row, list):
                record = dict(zip(cols, row))
                record["k"] = "frame"
                # The writer uses the profiler marker names directly as columns.
                raw_phases = {k: v for k, v in record.items() if k not in FRAME_FIELDS
                              and k != "k" and not k.startswith(("phase.", "phase:", "phases.", "net."))
                              and number(v) is not None}
                record["phases"] = dict(raw_phases, **obj(record.get("phases")))
                rows.append(record)
            else:
                warnings.append(source + ": invalid black-box row; skipped")
        head = obj(value.get("head"))
        return [Capture(head, rows, source, owner, number(head.get("momentSt")), True,
                        obj(value.get("state")), obj(value.get("events")))]
    # Other JSON report attachments (errors, fixes, breakdown) are not timelines.
    if isinstance(value, dict) and "k" not in value:
        return []
    head, records = {}, []
    for lineno, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            continue
        try:
            record = json.loads(line)
        except (ValueError, RecursionError):
            warnings.append(f"{source}:{lineno}: invalid/truncated JSON line; skipped")
            continue
        if not isinstance(record, dict):
            continue
        if record.get("k") == "head":
            if records:
                captures.append(Capture(head, records, source, owner))
                records = []
            head = record
        else:
            records.append(record)
    if records or head:
        captures.append(Capture(head, records, source, owner))
    return captures


def load_inputs(paths):
    captures, warnings, files = [], [], set()
    for arg in paths:
        path = Path(arg)
        if path.is_dir():
            files.update(p.resolve() for p in path.rglob("*") if p.suffix.lower() in (".zip", ".json", ".jsonl"))
        elif path.is_file():
            files.add(path.resolve())
        else:
            warnings.append(str(path) + ": input does not exist")
    for path in sorted(files):
        try:
            if path.suffix.lower() == ".zip":
                with zipfile.ZipFile(path) as archive:
                    for member in sorted(archive.infolist(), key=lambda m: m.filename):
                        if Path(member.filename).suffix.lower() not in (".json", ".jsonl"):
                            continue
                        source = path.name + "!" + member.filename
                        try:
                            text = archive.read(member).decode("utf-8-sig")
                            captures.extend(parse_text(text, source, str(path), warnings))
                        except (OSError, ValueError, RuntimeError, EOFError, zipfile.BadZipFile, zlib.error, NotImplementedError) as exc:
                            warnings.append(f"{source}: unreadable member; skipped ({exc})")
            else:
                captures.extend(parse_text(path.read_text(encoding="utf-8-sig"), path.name, str(path.parent), warnings))
        except (OSError, ValueError, RuntimeError, EOFError, zipfile.BadZipFile) as exc:
            warnings.append(f"{path.name}: unreadable/truncated input; skipped ({exc})")
    return captures, warnings


def group_moments(captures):
    machines = {}
    for capture in captures:
        key = capture.identity
        machines.setdefault(key, Machine(key)).captures.append(capture)
    sessions = defaultdict(list)
    for key, machine in sorted(machines.items()):
        # Missing sid must never imply that unrelated solo clocks are synchronized.
        session = key[0] or "local-" + hashlib.sha256(repr(key).encode()).hexdigest()[:10]
        sessions[session].append(machine)
    result = []
    for sid, members in sorted(sessions.items()):
        roles = defaultdict(int)
        candidates = []
        for machine in members:
            role = str(machine.head.get("role") or "unknown")
            roles[role] += 1
            machine.label = role + (f" {roles[role]}" if role == "client" or roles[role] > 1 else "")
            for capture in machine.captures:
                if capture.moment is not None:
                    candidates.append((capture.moment, str(capture.head.get("note") or "")))
                candidates.extend((number(r.get("st")), str(r.get("note") or ""))
                                  for r in capture.records if r.get("k") == "mark" and number(r.get("st")) is not None)
        buckets = []
        for stamp, note in sorted(candidates):
            # Bound the complete cluster to 2 seconds; do not chain distant moments.
            if not buckets or stamp - buckets[-1][0][0] > 2:
                buckets.append([])
            buckets[-1].append((stamp, note))
        if not buckets:
            stamps = [number(r.get("st")) for m in members for c in m.captures for r in c.records]
            stamps = [s for s in stamps if s is not None]
            buckets = [[(max(stamps) if stamps else None, "End of log (no report/mark moment available)")]]
        for bucket in buckets:
            stamps = [s for s, _ in bucket if s is not None]
            result.append(Moment(sid, statistics.median(stamps) if stamps else None, members,
                                 sorted({note for _, note in bucket if note})))
    return result


def shared_clock(moment):
    return (len(moment.machines) <= 1 or
            (len({m.head.get("sid") for m in moment.machines}) == 1
             and all(m.head.get("sid") and m.head.get("stSource") != "local" for m in moment.machines)))


def selected_box(machine, moment):
    boxes = [c for c in machine.captures if c.blackbox
             and (c.moment is None or moment.st is None or abs(c.moment - moment.st) <= 2)]
    return min(boxes, key=lambda c: (abs((c.moment or 0) - (moment.st or 0)), -len(c.records), c.source), default=None)


def window(machine, moment):
    """Return (x, record) pairs. x is session time, or honest record-order offsets."""
    captures = machine.captures
    box = selected_box(machine, moment)
    boxes = [box] if box else []
    selected = [c for c in captures if not c.blackbox] + boxes
    raw = [(r, c) for c in selected for r in c.records]
    synced = (moment.st is not None and shared_clock(moment)
              and all(number(r.get("st")) is not None for r, _ in raw))
    pairs, seen = [], set()
    if synced:
        hitches = {r["st"]: r for r, _ in raw if r.get("k") == "hitch" and number(r.get("st")) is not None}
        for record, capture in raw:
            stamp = record["st"]
            # The selected box already covers its actual 30+5 second window, including a crossing freeze.
            if not capture.blackbox and not moment.st - 30 <= stamp <= moment.st + 5:
                continue
            key = (record.get("k"), stamp)
            if capture.blackbox or key not in seen:
                seen.add(key)
                if record.get("k") == "frame" and obj(hitches.get(stamp, {}).get("gen")):
                    record = dict(record, genDetails=hitches[stamp]["gen"])
                pairs.append((stamp, record))
        pairs.sort(key=lambda pair: pair[0])
    else:
        capture = boxes[0] if boxes else (selected[0] if selected else None)
        records = capture.records if capture else []
        anchors = [i for i, r in enumerate(records) if r.get("k") == "mark"]
        anchor = anchors[-1] if anchors else max(0, len(records) - 1)
        pairs = [(i - anchor, r) for i, r in enumerate(records)]
    return pairs, synced


def frame_records(pairs):
    frames = [(x, r) for x, r in pairs if r.get("k") == "frame" and number(r.get("ms")) is not None and r["ms"] >= 0]
    # Log hitch records duplicate black-box frames. Prefer the complete ring.
    stamps = {x for x, _ in frames}
    return sorted(frames + [(x, r) for x, r in pairs if r.get("k") == "hitch" and x not in stamps
                            and number(r.get("ms")) is not None and r["ms"] >= 0], key=lambda pair: pair[0])


NET_ALIASES = {
    "ping (ms)": ("pingMs", "ping", "rttMs", "rtt"),
    "loss (%)": ("lossPct", "lossPercent", "loss"),
    "sent bytes/frame": ("sentBytes", "bytesSent", "txBytes", "sent"),
    "received bytes/frame": ("recvBytes", "bytesReceived", "rxBytes", "received"),
}


def network_label(key):
    parts = key.split(".")
    name = parts[-1]
    names = {"ping": "ping (ms)", "pingMs": "ping (ms)", "rtt": "ping (ms)", "rttMs": "ping (ms)",
             "lossPct": "loss (%)", "lossPercent": "loss (%)", "loss": "loss (reported units)",
             "lossPctLocal": "local loss from quality (%)", "lossPctRemote": "remote loss from quality (%)",
             "bytesOut": "sent bytes (cumulative)", "bytesIn": "received bytes (cumulative)",
             "outBps": "sent bytes/s", "inBps": "received bytes/s"}
    prefix = ".".join(parts[:-1])
    prefix = re.sub(r"conns\.(\d+)", lambda match: "connection " + str(int(match[1]) + 1), prefix)
    return (prefix + " " if prefix else "") + names.get(name, name)


def network(record):
    # Retain nested peer/transport names instead of averaging unrelated peers.
    result = {}
    flat = flatten(record.get("net"))
    for key, value in record.items():
        if key.startswith("net."):
            flat[key[4:]] = value
    for key, value in flat.items():
        if number(value) is not None and value >= 0:
            # Current NetStats gives Steam quality fractions rather than loss percent.
            if key.endswith(("qualityLocal", "qualityRemote")) and value <= 1:
                result[network_label(key.replace("quality", "lossPct"))] = round((1 - value) * 100, 3)
            else:
                result[network_label(key)] = value
    for label, aliases in NET_ALIASES.items():
        for alias in aliases:
            if number(record.get(alias)) is not None and record[alias] >= 0:
                result[label] = record[alias]
                break
    return result


def entries(value):
    return [v for v in value if isinstance(v, dict)] if isinstance(value, list) else []


def position(value):
    return value if isinstance(value, list) and len(value) == 3 and all(number(v) is not None for v in value) else None


def state_findings(moment, position_m=1.0):
    """Observed disagreement, with unknown/truncated coverage kept distinct from an empty slot."""
    lines, affected, views = [], set(), []
    for machine in moment.machines:
        capture = selected_box(machine, moment)
        if capture and capture.state:
            state = capture.state
            entities = {e["net"]: e for e in entries(state.get("entities")) if number(e.get("net")) is not None}
            views.append((machine, capture, state, entities))
            flags = []
            if state.get("failed"):
                flags.append("capture failed")
            if state.get("dropped", 0):
                flags.append(f"{state['dropped']} entries omitted by bounds")
            if flags:
                lines.append(f"State {machine.label}: {', '.join(flags)}; absence is unknown.")
            if number(state.get("st")) is not None and number(capture.moment) is not None:
                delay = state["st"] - capture.moment
                if abs(delay) > .05:
                    lines.append(f"State {machine.label}: captured {delay:+.3f} s from the host mark; this is arrival-time state.")
        elif any(c.state for m in moment.machines for c in m.captures):
            lines.append(f"State {machine.label}: no state snapshot for this moment (older files remain usable).")
    if not views:
        return []
    if not shared_clock(moment):
        return lines + ["State timeline not synced; cross-machine state and network events cannot be aligned."]
    views.sort(key=lambda v: (v[0].head.get("role") not in ("host", "dedicated"), v[0].label))

    def report(text, net=None):
        lines.append("State " + text)
        if net is not None:
            affected.add(net)

    def complete(s):
        return not s.get("failed") and not s.get("dropped", 0)

    def covers(s, e):
        # Nearby captures cover different areas; a seated car is requested everywhere.
        if any(p.get("net") == e.get("net") for p in entries(s.get("players"))):
            return True
        p, c = position(e.get("posCm")), position(s.get("centerCm"))
        radius = number(s.get("radiusM"))
        return bool(s.get("hasCenter") and p and c and radius is not None
                    and math.dist(p, c) <= max(0, radius - 2) * 100)

    for machine, _, state, _ in views:
        seats = defaultdict(set)
        for p in entries(state.get("players")):
            if p.get("net") and number(p.get("seat")) is not None:
                seats[(p["net"], p["seat"])].add(p.get("id"))
        for (net, seat), ids in seats.items():
            if len(ids) > 1:
                report(f"{machine.label}: seat occupancy conflict on net {net} seat {seat}: players "
                       + ", ".join(str(i) for i in sorted(ids, key=str)) + " claim one seat.", net)
    self_seats = defaultdict(set)
    for _, _, state, _ in views:
        for p in entries(state.get("players")):
            if p.get("id") == state.get("localPlayer") and p.get("net"):
                self_seats[(p["net"], p.get("seat"))].add(p["id"])
    for (net, seat), ids in self_seats.items():
        if len(ids) > 1:
            report(f"seat occupancy conflict on net {net} seat {seat}: players "
                   + ", ".join(str(i) for i in sorted(ids, key=str)) + " each claim this seat on their own machine (capture times may differ).", net)

    def slot_rows(s, net):
        result = defaultdict(list)
        for slot in entries(s.get("slots")):
            if slot.get("net") == net:
                result[(slot.get("item"), slot.get("index"))].append(slot)
        return result

    def slot_key(s):
        # Local save ids are deliberately absent: net + canonical member identifies the part across machines.
        return (s.get("contentNet", 0), s.get("contentItem", -1), s.get("prefab", -1), s.get("attached", False))

    def contents(slots):
        parts = [s.get("content") or f"prefab {s.get('prefab')}" for s in slots
                 if s.get("content") or (number(s.get("prefab")) is not None and s["prefab"] >= 0)]
        return ", ".join(parts) if parts else "empty"

    for ai, (a, _, sa, ea) in enumerate(views):
        for b, _, sb, eb in views[ai + 1:]:
            for net in sorted(set(ea) | set(eb)):
                x, y = ea.get(net), eb.get(net)
                present = x or y
                title = f"net {net} {present.get('name') or 'object'}"
                if x is None or y is None:
                    state, label = (sa, a.label) if x is None else (sb, b.label)
                    owner = b.label if x is None else a.label
                    if complete(state) and covers(state, present):
                        report(f"{title}: object present on {owner} only; {label} is missing it within capture coverage.", net)
                    else:
                        report(f"{title}: recorded on {owner} only; {label}'s absence is unknown (radius, bounds or failed capture).")
                    continue
                for field, description in (("owner", "owner disagreement"), ("epoch", "epoch disagreement"),
                                           ("stored", "stored mismatch"), ("parent", "attachment parent mismatch"), ("part", "part index mismatch")):
                    if field in x and field in y and x[field] != y[field]:
                        report(f"{title}: {description}: {a.label} {x[field]}, {b.label} {y[field]}.", net)
                px, py = position(x.get("posCm")), position(y.get("posCm"))
                if px and py:
                    adjusted, note, tolerance = list(py), "", position_m
                    # poseSt is the owning sender's unscaled clock, not session st. Compare only the same owner/epoch.
                    tx, ty = number(x.get("poseSt")), number(y.get("poseSt"))
                    same_owner = x.get("owner") is not None and x.get("epoch") is not None and x.get("owner") == y.get("owner") and x.get("epoch") == y.get("epoch")
                    velocity = position(y.get("vel")) or position(x.get("vel"))
                    if same_owner and tx is not None and ty is not None and velocity:
                        dt = tx - ty
                        if abs(dt) <= 1:
                            adjusted = [p + v * dt * 100 for p, v in zip(py, velocity)]
                            note = f" (sender-clock correction {dt:+.3f} s, constant velocity estimate)"
                        else:
                            report(f"{title}: pose clocks are {abs(dt):.3f} s apart; stale pose or handover, position comparison uncertain.", net)
                            adjusted = None
                    else:
                        skew = abs((number(sa.get("st")) or 0) - (number(sb.get("st")) or 0))
                        speed = max(number(x.get("speed")) or 0, number(y.get("speed")) or 0)
                        tolerance += speed * (skew + .15)
                        note = " (sender clock unavailable; tolerance includes 150 ms interpolation and capture skew)"
                    if adjusted is not None:
                        distance = math.dist(px, adjusted) / 100
                        if distance > tolerance:
                            report(f"{title}: position differs by {distance:.2f} m between {a.label} and {b.label}, tolerance {tolerance:.2f} m{note}.", net)
                for field, description, tolerance in (("fuel", "fuel mismatch", .1), ("engine", "engine mismatch", 0),
                                                       ("ignition", "ignition mismatch", 0), ("key", "ignition/key mismatch", 0),
                                                       ("radio", "radio tuning mismatch", .05), ("radioOn", "radio power mismatch", 0), ("radioAm", "radio band mismatch", 0)):
                    if field in x and field in y and x[field] is not None and y[field] is not None:
                        vx, vy = x[field], y[field]
                        differs = abs(vx - vy) > tolerance if isinstance(vx, (int, float)) and isinstance(vy, (int, float)) else vx != vy
                        if differs:
                            report(f"{title}: {description}: {a.label} {vx}, {b.label} {vy}.", net)
                ax, bx = slot_rows(sa, net), slot_rows(sb, net)
                for key in sorted(set(ax) | set(bx), key=str):
                    left, right = ax.get(key, []), bx.get(key, [])
                    name = (left or right)[0].get("name") or str(key[1])
                    if not left or not right:
                        if complete(sa) and complete(sb) and not x.get("dormant") and not y.get("dormant"):
                            report(f"{title}: slot {name} (item {key[0]}, index {key[1]}) is recorded on {a.label if left else b.label} only; slot data missing, contents unknown.", net)
                        continue
                    if sorted(map(slot_key, left), key=str) != sorted(map(slot_key, right), key=str):
                        report(f"{title}: {a.label} has {contents(left)} in slot {name}, {b.label}'s slot {name} has {contents(right)}.", net)
                    else:
                        for field in ("condition", "condition2", "condition3"):
                            if all(number(s.get(field)) is not None and s[field] >= 0 for s in left + right) and sorted(s[field] for s in left) != sorted(s[field] for s in right):
                                report(f"{title}: {field} differs in slot {name}: {a.label} {[s[field] for s in left]}, {b.label} {[s[field] for s in right]}.", net)
            pa = {p.get("id"): p for p in entries(sa.get("players"))}
            pb = {p.get("id"): p for p in entries(sb.get("players"))}
            for pid in sorted(set(pa) & set(pb), key=str):
                x, y = pa[pid], pb[pid]
                if (x.get("net"), x.get("seat")) != (y.get("net"), y.get("seat")):
                    report(f"player {pid} seat mismatch: {a.label} net {x.get('net')} seat {x.get('seat')}, {b.label} net {y.get('net')} seat {y.get('seat')}.")
                    affected.update(n for n in (x.get("net"), y.get("net")) if n)
            for field, description, tolerance in (("time", "time-of-day mismatch", 2), ("day", "day-count mismatch", 0),
                                                   ("cloud", "weather/cloud mismatch", .1), ("sand", "sandstorm exposure mismatch", .1)):
                x, y = number(sa.get(field)), number(sb.get(field))
                if x is not None and y is not None:
                    delta = abs(x - y)
                    if field == "time":
                        delta = min(delta % 86400, 86400 - delta % 86400)
                    if delta > tolerance:
                        report(f"{description}: {a.label} {x:g}, {b.label} {y:g}.")
            sta = {(s.get("am"), s.get("index")): s for s in entries(sa.get("stations"))}
            stb = {(s.get("am"), s.get("index")): s for s in entries(sb.get("stations"))}
            for key in sorted(set(sta) & set(stb), key=str):
                x, y = sta[key], stb[key]
                if x.get("clip") != y.get("clip") or (number(x.get("time")) is not None and number(y.get("time")) is not None and abs(x["time"] - y["time"]) > 2):
                    report(f"radio station {'AM' if key[0] else 'FM'} {key[1]} mismatch: {a.label} clip {x.get('clip')} at {x.get('time')} s, {b.label} clip {y.get('clip')} at {y.get('time')} s.")
    if not lines:
        lines.append("State: no disagreements beyond tolerance in recorded fields." if len(views) > 1 else "State: one snapshot; no cross-machine state comparison.")
    events = []
    for machine, capture, _, _ in views:
        if capture.events.get("overwritten", 0):
            lines.append(f"State {machine.label}: network-event ring overwrote {capture.events['overwritten']} records since reset; older history may be incomplete.")
        for e in entries(capture.events.get("rows")):
            st = number(e.get("st"))
            if st is not None and (moment.st is None or moment.st - 10 <= st <= moment.st + 5) and (e.get("net") in affected or e.get("other") in affected):
                events.append((st, machine.label, e))
    for st, label, e in sorted(events, key=lambda v: (v[0], v[1])):
        lines.append(f"Event st {st:.3f} {label}: {e.get('kind')} net {e.get('net')} other {e.get('other', 0)} "
                     f"item {e.get('item', -1)} slot {e.get('slot', -1)} from {e.get('from', -1)} to {e.get('to', -1)} "
                     f"epoch {e.get('epoch', 0)} count {e.get('count', 0)}: {e.get('result', 'unknown')}.")
    return lines


def findings(moment):
    lines, views = state_findings(moment), []
    for machine in moment.machines:
        pairs, synced = window(machine, moment)
        frames = frame_records(pairs)
        slow = [r for _, r in frames if number(r.get("ms")) is not None and r["ms"] > 50]
        views.append((machine, pairs, frames, slow))
        if not synced:
            lines.append(f"{machine.label}: " + ("session time unavailable; " if shared_clock(moment) else "timeline not synced; ")
                         + "shown in file order, so simultaneous events cannot be compared.")
    affected = [m.label for m, _, _, slow in views if slow]
    if len(affected) == 1 and len(views) > 1 and all(frames for _, _, frames, _ in views):
        lines.append(f"In recorded frames, only {affected[0]} had frames over 50 ms.")
    for machine, pairs, frames, slow in views:
        label = machine.label
        if frames:
            text = f"{label}: {len(slow)} recorded frames over 50 ms out of {len(frames)}"
            if all(r.get("k") == "hitch" for _, r in frames):
                text += " hitch records (rate limited; this is a lower bound)"
            lines.append(text + ".")
        else:
            lines.append(f"{label}: no per-frame data; absence of hitch records does not prove smooth frames.")
        ticks = [r for _, r in pairs if r.get("k") == "tick"]
        counts = [r["hitches50"] for r in ticks if number(r.get("hitches50")) is not None]
        if counts:
            lines.append(f"{label}: sampled intervals report {sum(counts):g} frames over 50 ms (intervals can cross the window boundary).")
        dropped = sum(r.get("hitchesDropped", 0) for r in ticks if number(r.get("hitchesDropped", 0)) is not None)
        if dropped:
            lines.append(f"{label}: {dropped:g} hitch details were dropped by the rate limit.")
        if slow:
            associations = []
            for name, predicate in (("a garbage collection", gc), ("a floating-origin rebase", lambda r: r.get("rebase") is True or r.get("rebase") == 1),
                                    ("streaming", lambda r: r.get("stream") is True or r.get("stream") == 1),
                                    ("saving", lambda r: r.get("saving", r.get("save")) is True or r.get("saving", r.get("save")) == 1),
                                    ("loading", lambda r: r.get("loading") is True or r.get("loading") == 1)):
                count = sum(bool(predicate(r)) for r in slow)
                if count:
                    associations.append(f"{'all' if count == len(slow) else str(count) + ' of ' + str(len(slow))} with {name}")
            if associations:
                lines.append(label + ": " + "; ".join(associations) + ". These events coincided with the slow frames; they do not establish a cause.")
            gens = [backlog(r) for r in slow]
            for key in sorted({k for gen in gens for k in gen}):
                values = [g[key] for g in gens if key in g]
                if values and max(values) > 0:
                    span = f"{min(values):g}" if min(values) == max(values) else f"{min(values):g}–{max(values):g}"
                    lines.append(f"{label}: {span} {key} generation backlog on {len(values)} of {len(slow)} slow frames (unknown fields excluded).")
            phase_values = defaultdict(list)
            for record in slow:
                for name, value in phases(record).items():
                    phase_values[name].append(value)
            for name, values in sorted(phase_values.items(), key=lambda kv: -sum(kv[1]))[:5]:
                lines.append(f"{label}: phase {name} took {statistics.mean(values):g} ms on average on {len(values)} slow frames with that phase recorded.")
    # Same metric and same scope only: frame bytes are never summed with log counters.
    metrics = defaultdict(list)
    for machine, pairs, _, _ in views:
        samples = defaultdict(list)
        for _, record in pairs:
            for key, value in network(record).items():
                samples[(record.get("k"), key)].append(value)
        for (kind, key), values in samples.items():
            metrics[(kind, key)].append((machine.label, statistics.mean(values)))
    for (kind, key), values in sorted(metrics.items(), key=lambda item: str(item[0])):
        report = "; ".join(f"{label} {value:g}" for label, value in values)
        comparison = " differs" if len({value for _, value in values}) > 1 else ""
        lines.append(f"Network {key}{comparison} ({kind} samples, mean): {report}.")
    for category in ("settings", "hw"):
        fields = sorted({k for m in moment.machines for k in flatten(m.head.get(category))})
        for key in fields:
            values = [(m.label, flatten(m.head.get(category)).get(key, "unknown")) for m in moment.machines]
            if len({pretty(v) for _, v in values}) > 1:
                lines.append(f"{'Hardware' if category == 'hw' else 'Setting'} {key} differs: "
                             + "; ".join(f"{label}: {pretty(v)}" for label, v in values)
                             + ". This may affect performance; the recording does not prove it explains a stutter.")
    if len(views) == 1:
        lines.append("One machine only: there is no other machine to compare.")
    return lines


COLORS = ("#007e9e", "#b64e00", "#8660c9", "#148447", "#c04480", "#827400")


def svg_chart(title, series, lo, hi, unit, threshold=None, stacks=False, marker=None, points_only=False):
    """Every chart uses the same x mapping; SVG titles retain exact sample values."""
    if not any(points for _, points in series):
        return f"<p>{esc(title)}: no data recorded.</p>"
    maximum = max([1, threshold or 0] + [sum(v.values()) if isinstance(v, dict) else v for _, pts in series for _, v in pts]) * 1.1
    x = lambda stamp: 58 + (stamp - lo) / max(1, hi - lo) * 820
    y = lambda value: 160 - value / maximum * 132
    parts = [f'<figure><figcaption>{esc(title)} ({esc(unit)})</figcaption><div class="plot">',
             f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 205" role="img" aria-label="{esc(title)}">',
             f'<title>{esc(title)}</title><path d="M58 25 V160 H878" fill="none" stroke="currentColor"/>']
    for value in (0, maximum / 2, maximum):
        parts.append(f'<text x="2" y="{y(value):.2f}" font-size="12">{value:.0f}</text>')
    for stamp in (lo, lo + (hi - lo) / 2, hi):
        parts.append(f'<text x="{x(stamp):.2f}" y="185" text-anchor="middle" font-size="12">{stamp:.2f}</text>')
    parts.append('<text x="470" y="202" text-anchor="middle" font-size="12">Shared session time (s)</text>' if lo >= 0 else
                 '<text x="470" y="202" text-anchor="middle" font-size="12">Record order relative to marker/end (not seconds)</text>')
    if threshold is not None:
        parts.append(f'<path d="M58 {y(threshold):.2f} H878" stroke="currentColor" stroke-dasharray="4 4"/><text x="880" y="{y(threshold):.2f}" font-size="11">{threshold:g}</text>')
    if marker is not None:
        parts.append(f'<path d="M{x(marker):.2f} 25 V160" stroke="currentColor" stroke-dasharray="2 4"/><text x="{x(marker):.2f}" y="18" text-anchor="middle" font-size="12">moment</text>')
    phase_names = sorted({k for _, points in series for _, value in points if isinstance(value, dict) for k in value})
    for index, (label, points) in enumerate(series):
        color = COLORS[index % len(COLORS)]
        if stacks:
            for stamp, values in points:
                base = 0
                for name, value in values.items():
                    height = value / maximum * 132
                    parts.append(f'<rect x="{x(stamp):.2f}" y="{y(base + value):.2f}" width="5" height="{height:.2f}" fill="{COLORS[phase_names.index(name) % len(COLORS)]}"><title>{esc(label)}: {stamp:g}, {esc(name)} {value:g} ms</title></rect>')
                    base += value
        else:
            coords = " ".join(f"{x(stamp):.2f},{y(value):.2f}" for stamp, value in points)
            if not points_only:
                parts.append(f'<polyline points="{coords}" fill="none" stroke="{color}" stroke-width="2"/>')
            for stamp, value in points:
                parts.append(f'<circle cx="{x(stamp):.2f}" cy="{y(value):.2f}" r="2.5" fill="{color}"><title>{esc(label)}: {stamp:g}, {value:g} {esc(unit)}</title></circle>')
    parts.append('</svg></div><p class="legend">')
    labels = phase_names if stacks else [label for label, _ in series]
    parts.extend(f'<span style="border-color:{COLORS[i % len(COLORS)]}">{esc(label)}</span> ' for i, label in enumerate(labels))
    parts.append('</p></figure>')
    return "".join(parts)


STYLE = """
:root { color-scheme: light dark; font-family: system-ui, sans-serif; }
body { margin: auto; padding: 1rem; max-width: 75rem; line-height: 1.5; }
h1 { font-size: 1.6rem; } h2 { font-size: 1.3rem; }
table { width: 100%; border-collapse: collapse; } th, td { text-align: left; vertical-align: top;
padding: .6rem; border-bottom: 1px solid #888; overflow-wrap: anywhere; }
figure { margin: 1rem 0; padding: .6rem; border: 1px solid #888; border-radius: .4rem; }
.plot { overflow-x: auto; } svg { width: 100%; min-width: 900px; height: auto; } svg text { fill: currentColor; }
figcaption { font-weight: bold; } .legend { font-size: .85rem; margin: .2rem; }
.legend span { display: inline-block; border-left: .8rem solid; padding-left: .3rem; margin-right: .8rem; }
li { margin-bottom: .5rem; } .warning { border-left: .3rem solid #b64e00; padding-left: .8rem; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; }
@media(max-width: 600px) { table, tbody, tr, th, td { display: block; } thead { display: none; }
td { padding: .35rem 0; } tr { margin-bottom: 1rem; } body { padding: .7rem; } }
"""


def render_html(moment, warnings=()):
    title = f"Telemetry comparison — {moment.sid} — " + (f"{moment.st:.3f} s" if moment.st is not None else "file order")
    out = ['<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/>',
           '<meta name="viewport" content="width=device-width, initial-scale=1"/>',
           '<meta http-equiv="Content-Security-Policy" content="default-src &#x27;none&#x27;; style-src &#x27;unsafe-inline&#x27;"/>',
           f'<title>{esc(title)}</title><style>{STYLE}</style></head><body><main>', f'<h1>{esc(title)}</h1>']
    out.extend(f'<p>{esc(note)}</p>' for note in moment.notes)
    out.append('<h2>Differences</h2><p>Findings describe the recorded window: 30 seconds before and 5 seconds after the moment. Missing fields are unknown. Loss from Steam quality is calculated as (1 − quality) × 100. Cumulative byte counters are compared as samples, not window traffic totals. Phase markers may overlap; stacked heights are not total frame time.</p><ul>')
    out.extend(f'<li>{esc(line)}</li>' for line in findings(moment))
    out.append('</ul><h2>Machines</h2><table><thead><tr><th>Machine / role</th><th>Hardware</th><th>Settings</th><th>Sources</th></tr></thead><tbody>')
    for machine in moment.machines:
        out.append(f'<tr><th scope="row">{esc(machine.label)}</th><td>{esc(pretty(machine.head.get("hw", "unknown")))}</td>'
                   f'<td>{esc(pretty(machine.head.get("settings", "unknown")))}</td><td>{esc(", ".join(sorted({c.source for c in machine.captures})))}</td></tr>')
    out.append('</tbody></table><h2>Timelines</h2>')
    synced_series, other = [], []
    for machine in moment.machines:
        pairs, synced = window(machine, moment)
        (synced_series if synced else other).append((machine, pairs))
    groups = [(synced_series, True)] if synced_series else []
    groups.extend(([(machine, pairs)], False) for machine, pairs in other)
    for members, synced in groups:
        lo, hi = (moment.st - 30, moment.st + 5) if synced else (-30, 5)
        if not synced:
            out.append(f'<p class="warning">{esc(members[0][0].label)}: {"session time unavailable" if shared_clock(moment) else "timeline not synced"}. File order only; this axis is not elapsed time.</p>')
        frames = [(m.label, [(x, r["ms"]) for x, r in frame_records(p) if number(r.get("ms")) is not None and r["ms"] >= 0]) for m, p in members]
        # Explicit axis wording must not depend on the numeric sign of session time.
        def chart(title, series, unit, threshold=None, stacks=False, points_only=False):
            content = svg_chart(title, series, lo, hi, unit, threshold, stacks, moment.st if synced else 0, points_only)
            if synced:
                content = content.replace("Record order relative to marker/end (not seconds)", "Shared session time (s)")
            return content
        out.append(chart("Frame times", frames, "ms", 50))
        sampled = [(m.label + " interval max", [(x, r["maxms"]) for x, r in p if r.get("k") == "tick" and number(r.get("maxms")) is not None and r["maxms"] >= 0]) for m, p in members]
        out.append(chart("Play-log interval maxima (not individual frames)", sampled, "ms", 50))
        for machine, pairs in members:
            slow = [(x, phases(r)) for x, r in frame_records(pairs) if number(r.get("ms")) is not None and r["ms"] > 50 and phases(r)]
            out.append(chart(machine.label + " slow-frame phases (stacked)", [(machine.label, slow)], "ms", stacks=True))
            marks = []
            for lane, (flag, predicate) in enumerate((("GC", gc), ("rebase", lambda r: r.get("rebase")), ("stream", lambda r: r.get("stream")), ("save", lambda r: r.get("saving", r.get("save")) or r.get("k") == "save")), 1):
                # Tick GC counts describe an interval; do not place them on a frame.
                marks.append((flag, [(x, lane) for x, r in pairs if r.get("k") != "tick" and predicate(r)]))
            out.append(chart(machine.label + " GC / rebase / stream / save events", marks, "event lane", points_only=True))
            gens = defaultdict(list)
            nets = defaultdict(list)
            for x, record in pairs:
                for key, value in backlog(record).items():
                    gens[key].append((x, value))
                for key, value in network(record).items():
                    nets[(record.get("k"), key)].append((x, value))
            out.append(chart(machine.label + " generation backlog", sorted(gens.items()), "pending"))
            for (kind, key), points in sorted(nets.items(), key=lambda item: str(item[0])):
                out.append(chart(machine.label + " network " + str(key) + " (" + str(kind) + ")", [(machine.label, points)], "recorded units"))
    if warnings:
        out.append('<h2>Input warnings</h2><ul>')
        out.extend(f'<li>{esc(warning)}</li>' for warning in warnings)
        out.append('</ul>')
    out.append('</main></body></html>')
    return "\n".join(out)


# ---- run-vs-run mode -------------------------------------------------------------------------------------------
# Two or more runs (e.g. base vs RevaMP, or a fix off vs on), each a folder or file list holding a TLDBench result
# JSON and/or RevaMP play logs (*.jsonl). TLDBench scenarios carry realtimeStart/realtimeEnd (Unity
# realtimeSinceStartup, TLDBench 1.2.0+), the same clock as a solo play log's `st`, so each scenario gets the play-log
# ticks it covers. Repeats of one configuration are joined with commas and averaged.

SCENARIO_METRICS = (("meanFps", "FPS", True), ("p50Ms", "p50 ms", False), ("p95Ms", "p95 ms", False), ("p99Ms", "p99 ms", False),
                    ("maxMs", "max ms", False), ("over33Ms", "frames >33 ms", False), ("over50Ms", "frames >50 ms", False),
                    ("positiveManagedDeltaBytesPerSecond", "alloc B/s", False), ("gen0CollectionsPerSecond", "GC/s", False))
TICK_METRICS = (("busyMs", "main busy ms"), ("mainThreadMs", "main thread ms"), ("renderThreadMs", "render thread ms"),
                ("gpuMs", "GPU ms"), ("gpuShare", "GPU-bound frames %"), ("drawCalls", "draw calls"), ("batches", "batches"),
                ("setPass", "SetPass calls"), ("trianglesK", "triangles (k)"), ("shadowCasters", "shadow casters"),
                ("allocKBps", "heap growth KB/s"), ("gcPerS", "GC/s (telemetry)"), ("selfMs", "telemetry ms/frame"),
                ("scriptsMs", "timed scripts ms/frame"))


def run_files(spec):
    files = []
    for part in [p for p in str(spec).split(",") if p]:
        path = Path(part)
        if path.is_dir():
            files += sorted(p for p in path.rglob("*") if p.is_file() and (p.suffix == ".jsonl" or (p.name.startswith("TLDBench-") and p.suffix == ".json")))
        elif path.is_file():
            files.append(path)
    return files


def read_jsonl(path):
    out = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            record = json.loads(line)
        except ValueError:
            continue
        if isinstance(record, dict):
            out.append(record)
    return out


def tick_window(ticks, start, end, interval=5.0):
    """Ticks whose 5 s interval lies at least half inside [start, end]."""
    chosen = []
    for t in ticks:
        st = number(t.get("st"))
        if st is None:
            continue
        lo = st - interval
        overlap = min(st, end) - max(lo, start)
        if overlap >= interval / 2:
            chosen.append(t)
    return chosen


def summarize_ticks(ticks):
    """Frame-weighted averages of the tick fields, and per-script ms/calls per frame."""
    frames = sum(number(t.get("frames")) or 0 for t in ticks)
    out = {"ticks": len(ticks), "frames": frames}
    if not frames:
        return out, {}
    def avg(getter):
        num = den = 0.0
        for t in ticks:
            f = number(t.get("frames")) or 0
            v = getter(t)
            if v is not None and f:
                num += v * f
                den += f
        return num / den if den else None
    for key in ("busyMs", "mainThreadMs", "renderThreadMs", "gpuMs"):
        out[key] = avg(lambda t, k=key: number(obj(t.get("frame")).get(k)))
    for key in ("drawCalls", "batches", "setPass", "trianglesK", "shadowCasters"):
        out[key] = avg(lambda t, k=key: number(obj(t.get("render")).get(k)))
    gpu = sum(number(obj(t.get("bound")).get("gpu")) or 0 for t in ticks)
    bound_frames = sum((number(obj(t.get("bound")).get("gpu")) or 0) + (number(obj(t.get("bound")).get("cpu")) or 0) for t in ticks)
    out["gpuShare"] = 100.0 * gpu / bound_frames if bound_frames else None
    out["selfMs"] = avg(lambda t: number(t.get("selfMs")))
    out["scriptsMs"] = avg(lambda t: number(obj(t.get("scripts")).get("totalMsPerFrame")))
    seconds = 0.0
    alloc = gcs = 0.0
    for t in ticks:
        a = number(t.get("allocKBps"))
        if a is not None:
            alloc += a * 5.0
            seconds += 5.0
        gcs += sum(number(t.get(f"gc{i}")) or 0 for i in range(3))
    out["allocKBps"] = alloc / seconds if seconds else None
    out["gcPerS"] = gcs / (5.0 * len(ticks)) if ticks else None
    scripts = defaultdict(lambda: [0.0, 0.0, 0.0, 0.0])  # ms*frames, calls*frames, kbPerS*5, max
    script_frames = 0
    for t in ticks:
        s = obj(t.get("scripts"))
        f = number(s.get("frames")) or 0
        if not f:
            continue
        script_frames += f
        for row in s.get("top") or []:
            if not isinstance(row, dict) or not isinstance(row.get("m"), str):
                continue
            acc = scripts[row["m"]]
            acc[0] += (number(row.get("msPerFrame")) or 0) * f
            acc[1] += (number(row.get("callsPerFrame")) or 0) * f
            acc[2] += (number(row.get("kbPerS")) or 0) * 5.0
            acc[3] = max(acc[3], number(row.get("maxMs")) or 0)
    per = {}
    if script_frames:
        for m, (ms, calls, kb, mx) in scripts.items():
            per[m] = {"msPerFrame": ms / script_frames, "callsPerFrame": calls / script_frames, "kbPerS": kb / (5.0 * len(ticks)), "maxMs": mx}
    loop, loop_frames = defaultdict(float), 0
    for t in ticks:
        lp = obj(t.get("loop"))
        f = number(lp.get("frames")) or 0
        loop_frames += f
        for row in lp.get("top") or []:
            if isinstance(row, dict) and isinstance(row.get("p"), str):
                loop[row["p"]] += (number(row.get("msPerFrame")) or 0) * f
    out["loop"] = {p: v / loop_frames for p, v in loop.items()} if loop_frames else {}
    fixes = defaultdict(float)
    for t in ticks:
        for k, v in obj(t.get("fixes")).items():
            if number(v) is not None:
                fixes[k] += v
    out["fixes"] = dict(fixes)
    return out, per


def load_run(spec):
    """One run: scenario name -> {"bench": TLDBench metrics, "ticks": tick summary, "scripts": per method}."""
    files = run_files(spec)
    bench = [f for f in files if f.suffix == ".json"]
    logs = [f for f in files if f.suffix == ".jsonl"]
    per_log = []
    for log in logs:
        recs = read_jsonl(log)
        per_log.append((log, [r for r in recs if r.get("k") == "tick"], [r for r in recs if r.get("k") == "head"]))
    benches = []
    for path in bench:
        try:
            benches.append((path, json.loads(path.read_text(encoding="utf-8-sig"))))
        except (OSError, ValueError):
            continue
    # Solo play logs of different game launches share a clock that restarts at 0: with a TLDBench result, use only
    # the log whose ticks fall inside its scenarios most often (the log of that same launch).
    windows = [(number(sc.get("realtimeStart")), number(sc.get("realtimeEnd"))) for _, d in benches for sc in d.get("scenarios") or []
               if isinstance(sc, dict) and number(sc.get("realtimeStart")) is not None and number(sc.get("realtimeEnd")) is not None]
    if windows and len(per_log) > 1:
        def overlap(entry):
            return sum(1 for t in entry[1] for lo, hi in windows if number(t.get("st")) is not None and lo <= t["st"] <= hi)
        per_log = [max(per_log, key=overlap)]
    ticks = [t for _, ts, _ in per_log for t in ts]
    heads = [h for _, _, hs in per_log for h in hs]
    used = [str(f) for f in bench] + [str(entry[0]) for entry in per_log]
    scenarios, meta = {}, {"files": used, "plugins": [], "label": None}
    for path, data in benches:
        meta["label"] = data.get("label")
        meta["plugins"] = sorted(f"{p.get('name')} {p.get('version')}" for p in data.get("plugins") or [] if isinstance(p, dict))
        for sc in data.get("scenarios") or []:
            if not isinstance(sc, dict) or sc.get("status") != "ok":
                continue
            entry = {"bench": {k: number(sc.get(k)) for k, _, _ in SCENARIO_METRICS}}
            start, end = number(sc.get("realtimeStart")), number(sc.get("realtimeEnd"))
            if start is not None and end is not None and start >= 0 and end > start and ticks:
                summary, per = summarize_ticks(tick_window(ticks, start, end))
                entry["ticks"], entry["scripts"] = summary, per
            scenarios[sc.get("name")] = entry
    if not bench and ticks:
        summary, per = summarize_ticks(ticks)
        scenarios["all"] = {"bench": {}, "ticks": summary, "scripts": per}
    meta["timers"] = any(obj(h.get("telemetry")).get("scriptTimers") for h in heads)
    return scenarios, meta


def merge_repeats(runs):
    """Average several repeats of one configuration; keep min/max for the spread."""
    merged = {}
    names = set().union(*(set(r) for r in runs)) if runs else set()
    for name in names:
        present = [r[name] for r in runs if name in r]
        entry = {"n": len(present), "bench": {}, "ticks": {}, "scripts": {}, "spread": {}}
        for part in ("bench", "ticks"):
            keys = set().union(*(set(p.get(part) or {}) for p in present))
            for k in keys:
                values = [p[part][k] for p in present if isinstance((p.get(part) or {}).get(k), (int, float))]
                if values:
                    entry[part][k] = statistics.mean(values)
                    entry["spread"][part + "." + k] = (min(values), max(values))
        methods = set().union(*(set(p.get("scripts") or {}) for p in present))
        for m in methods:
            rows = [(p.get("scripts") or {}).get(m) for p in present]
            entry["scripts"][m] = {k: statistics.mean([(r or {}).get(k, 0.0) for r in rows]) for k in ("msPerFrame", "callsPerFrame", "kbPerS", "maxMs")}
        phases = set().union(*(set((p.get("ticks") or {}).get("loop") or {}) for p in present))
        entry["loop"] = {ph: statistics.mean([((p.get("ticks") or {}).get("loop") or {}).get(ph, 0.0) for p in present]) for ph in phases}
        # ticks list only non-zero counter deltas: a repeat without a counter contributed 0
        with_ticks = [p for p in present if p.get("ticks")]
        keys = set().union(*(set((p.get("ticks") or {}).get("fixes") or {}) for p in with_ticks)) if with_ticks else set()
        entry["fixes"] = {k: statistics.mean([((p.get("ticks") or {}).get("fixes") or {}).get(k, 0.0) for p in with_ticks]) for k in keys}
        merged[name] = entry
    return merged


def pct(a, b):
    if a is None or b is None:
        return None
    if a == 0:
        return 0.0 if b == 0 else None
    return (b / a - 1.0) * 100.0


def fmt(v, digits=2):
    return "n/a" if v is None else f"{v:,.{digits}f}"


def compare_runs(configs, names, top=25):
    """Rows for every scenario in the first run: frame metrics, tick metrics and scripts, each against the first run."""
    report = []
    order = ["load", "idle", "stream30", "stream60", "carpark", "items", "all"]
    scenarios = sorted(set(configs[0]), key=lambda s: (order.index(s) if s in order else 99, s))
    for sc in scenarios:
        entries = [c.get(sc) for c in configs]
        section = {"scenario": sc, "runs": [e["n"] if e else 0 for e in entries], "metrics": [], "scripts": [], "fixes": []}
        for key, label, higher in SCENARIO_METRICS:
            vals = [(e or {}).get("bench", {}).get(key) for e in entries]
            if all(v is None for v in vals):
                continue
            spreads = [(e or {}).get("spread", {}).get("bench." + key) for e in entries]
            section["metrics"].append({"key": key, "label": label, "higherBetter": higher, "values": vals, "spread": spreads,
                                       "pct": [pct(vals[0], v) for v in vals]})
        for key, label in TICK_METRICS:
            vals = [(e or {}).get("ticks", {}).get(key) for e in entries]
            if all(v is None for v in vals):
                continue
            section["metrics"].append({"key": key, "label": label, "higherBetter": False, "values": vals, "spread": [None] * len(vals),
                                       "pct": [pct(vals[0], v) for v in vals]})
        methods = set().union(*(set((e or {}).get("scripts", {})) for e in entries))
        rows = []
        for m in methods:
            ms = [((e or {}).get("scripts", {}).get(m) or {}).get("msPerFrame", 0.0) for e in entries]
            calls = [((e or {}).get("scripts", {}).get(m) or {}).get("callsPerFrame", 0.0) for e in entries]
            kb = [((e or {}).get("scripts", {}).get(m) or {}).get("kbPerS", 0.0) for e in entries]
            rows.append({"m": m, "ms": ms, "calls": calls, "kb": kb, "delta": ms[-1] - ms[0], "peak": max(ms)})
        rows.sort(key=lambda r: (-abs(r["delta"]), -r["peak"]))
        section["scripts"] = rows[:top]
        section["scriptsByCost"] = sorted(rows, key=lambda r: -r["ms"][0])[:top]
        phases = set().union(*(set((e or {}).get("loop", {})) for e in entries))
        prow = [{"p": ph, "ms": [((e or {}).get("loop") or {}).get(ph, 0.0) for e in entries]} for ph in phases]
        for r in prow:
            r["delta"] = r["ms"][-1] - r["ms"][0]
        section["loop"] = sorted(prow, key=lambda r: -max(r["ms"]))[:15]
        keys = set().union(*(set((e or {}).get("fixes", {})) for e in entries))
        for k in sorted(keys):
            vals = [(e or {}).get("fixes", {}).get(k) for e in entries]
            section["fixes"].append({"k": k, "values": vals})
        report.append(section)
    return report


def runs_text(report, names):
    lines = []
    w = max(12, *(len(n) for n in names))
    for section in report:
        lines.append(f"== {section['scenario']} (repeats: {', '.join(f'{n} {c}' for n, c in zip(names, section['runs']))})")
        lines.append(f"  {'metric':24}" + "".join(f"{n:>{w + 10}}" for n in names))
        for m in section["metrics"]:
            cells = []
            for i, v in enumerate(m["values"]):
                p = m["pct"][i]
                cells.append(f"{fmt(v):>{w}}" + ("" if i == 0 else f" {('n/a' if p is None else f'{p:+.1f}%'):>8}") + ("" if i else " " * 9))
            lines.append(f"  {m['label']:24}" + " ".join(cells))
        if section.get("loop"):
            lines.append("  engine phases (player loop), ms/frame:")
            for r in section["loop"][:8]:
                lines.append(f"    {r['p'][:44]:44}" + "".join(f"{v:>10.3f}" for v in r["ms"]) + f"   d {r['delta']:+.3f}")
        if section["scripts"]:
            lines.append("  scripts, ms/frame (largest change first):")
            for r in section["scripts"][:15]:
                lines.append(f"    {r['m'][:44]:44}" + "".join(f"{v:>10.4f}" for v in r["ms"]) + f"   d {r['delta']:+.4f}   calls/frame " + "/".join(f"{c:.1f}" for c in r["calls"]))
    return "\n".join(lines)


def runs_html(report, names, metas):
    def cell(v, digits=3):
        return f"<td>{esc(fmt(v, digits))}</td>"
    parts = ["<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>",
             "<title>Run comparison</title><style>",
             ":root{--bg:#fff;--fg:#1d1d1f;--mut:#666;--line:#ddd;--good:#1a7f37;--bad:#c62828}",
             "@media (prefers-color-scheme:dark){:root{--bg:#141414;--fg:#eee;--mut:#aaa;--line:#333;--good:#5fd17a;--bad:#ff7b72}}",
             "body{background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif;margin:16px;max-width:1200px}",
             "table{border-collapse:collapse;margin:8px 0 20px;font-variant-numeric:tabular-nums}td,th{border-bottom:1px solid var(--line);padding:3px 8px;text-align:right}",
             "td:first-child,th:first-child{text-align:left}.good{color:var(--good)}.bad{color:var(--bad)}.mut{color:var(--mut)}",
             ".wrap{overflow-x:auto}</style></head><body><h1>Run comparison</h1>"]
    parts.append("<p class='mut'>Percentages compare each run with the first. Script times come from RevaMP's script timers in the play log "
                 "(top methods per 5 s tick, frame-weighted; a method missing from a tick counts as 0 there).</p><ul>")
    for n, meta in zip(names, metas):
        parts.append(f"<li><b>{esc(n)}</b>: {esc(', '.join(meta.get('plugins') or []) or 'no TLDBench result')}; "
                     f"script timers {'on' if meta.get('timers') else 'off'}; {len(meta.get('files') or [])} file(s)</li>")
    parts.append("</ul>")
    for section in report:
        parts.append(f"<h2>{esc(section['scenario'])}</h2><div class='wrap'><table><tr><th>metric</th>" + "".join(f"<th>{esc(n)}</th>" for n in names) + "</tr>")
        for m in section["metrics"]:
            row = f"<tr><td>{esc(m['label'])}</td>"
            for i, v in enumerate(m["values"]):
                p = m["pct"][i]
                cls = "" if i == 0 or p is None or abs(p) < 1 else ("good" if (p > 0) == m["higherBetter"] else "bad")
                spread = m["spread"][i]
                tip = f" title='min {fmt(spread[0])} max {fmt(spread[1])}'" if spread else ""
                row += f"<td{tip}>{esc(fmt(v))}" + ("" if i == 0 or p is None else f" <span class='{cls}'>({p:+.1f}%)</span>") + "</td>"
            parts.append(row + "</tr>")
        parts.append("</table></div>")
        if section.get("loop"):
            parts.append("<h3>Engine phases (player loop, ms per frame)</h3><div class='wrap'><table><tr><th>phase</th>" +
                         "".join(f"<th>{esc(n)}</th>" for n in names) + "<th>change</th></tr>")
            for r in section["loop"]:
                parts.append(f"<tr><td>{esc(r['p'])}</td>" + "".join(cell(v, 3) for v in r["ms"]) + f"<td>{r['delta']:+.3f}</td></tr>")
            parts.append("</table></div>")
        if section["scripts"]:
            parts.append("<h3>Scripts: largest change (ms per frame)</h3><div class='wrap'><table><tr><th>method</th>" +
                         "".join(f"<th>{esc(n)}</th>" for n in names) + "<th>change</th><th>calls/frame</th></tr>")
            for r in section["scripts"]:
                cls = "good" if r["delta"] < -0.005 else "bad" if r["delta"] > 0.005 else ""
                parts.append(f"<tr><td>{esc(r['m'])}</td>" + "".join(cell(v, 4) for v in r["ms"]) +
                             f"<td class='{cls}'>{r['delta']:+.4f}</td><td>{esc(' / '.join(f'{c:.1f}' for c in r['calls']))}</td></tr>")
            parts.append("</table></div><h3>Scripts: most expensive in the first run</h3><div class='wrap'><table><tr><th>method</th>" +
                         "".join(f"<th>{esc(n)}</th>" for n in names) + "<th>heap growth KB/s</th></tr>")
            for r in section["scriptsByCost"]:
                parts.append(f"<tr><td>{esc(r['m'])}</td>" + "".join(cell(v, 4) for v in r["ms"]) + f"<td>{esc(' / '.join(f'{k:.1f}' for k in r['kb']))}</td></tr>")
            parts.append("</table></div>")
        if section["fixes"]:
            parts.append("<details><summary>Fix counters (sum over the scenario)</summary><div class='wrap'><table><tr><th>counter</th>" +
                         "".join(f"<th>{esc(n)}</th>" for n in names) + "</tr>")
            for f in section["fixes"]:
                parts.append(f"<tr><td>{esc(f['k'])}</td>" + "".join(cell(v, 0) for v in f["values"]) + "</tr>")
            parts.append("</table></div></details>")
    parts.append("</body></html>")
    return "\n".join(parts)


def main_runs(specs, names, output):
    if len(specs) < 2:
        print("--runs needs at least two runs", file=sys.stderr)
        return 1
    names = names or [f"run{i + 1}" for i in range(len(specs))]
    if len(names) != len(specs):
        print("--names needs one name per run", file=sys.stderr)
        return 1
    configs, metas = [], []
    for spec in specs:
        repeats = [load_run(part) for part in spec.split(",") if part]
        if not any(r[0] for r in repeats):
            print(f"No TLDBench scenarios or play-log ticks in {spec}", file=sys.stderr)
            return 1
        configs.append(merge_repeats([r[0] for r in repeats]))
        meta = repeats[0][1]
        meta["files"] = [f for r in repeats for f in r[1]["files"]]
        meta["timers"] = any(r[1]["timers"] for r in repeats)
        metas.append(meta)
    report = compare_runs(configs, names)
    print(runs_text(report, names))
    out = Path(output)
    try:
        out.mkdir(parents=True, exist_ok=True)
        (out / "runs.html").write_text(runs_html(report, names, metas), encoding="utf-8")
        (out / "runs.json").write_text(json.dumps({"names": names, "report": report}, indent=1), encoding="utf-8")
    except OSError as exc:
        print(f"Cannot write comparison: {exc}", file=sys.stderr)
        return 1
    print(f"{out / 'runs.html'}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="*", help="report zips, mark JSON, play logs, or folders (recursive)")
    parser.add_argument("-o", "--output", default="telemetry-comparison", help="output folder (default: %(default)s)")
    parser.add_argument("--runs", nargs="+", metavar="RUN", help="run-vs-run mode: two or more runs, each a folder (or files) with a "
                        "TLDBench result and/or RevaMP play logs; join repeats of one configuration with commas")
    parser.add_argument("--names", nargs="+", help="a name per run for --runs (default run1, run2, ...)")
    args = parser.parse_args(argv)
    if args.runs:
        return main_runs(args.runs, args.names, args.output)
    if not args.inputs:
        parser.error("give inputs, or --runs")
    captures, warnings = load_inputs(args.inputs)
    moments = group_moments(captures)
    for warning in warnings:
        print("Warning: " + warning, file=sys.stderr)
    if not moments:
        print("No usable telemetry found.", file=sys.stderr)
        return 1
    hitches = {(c.identity, r.get("st"), r.get("ms")) for c in captures for r in c.records if r.get("k") == "hitch"}
    print(f"Read {len(captures)} captures across {len({c.identity for c in captures})} machine timeline(s); "
          f"{len(hitches)} distinct play-log hitch records (whole logs).")
    output = Path(args.output)
    try:
        output.mkdir(parents=True, exist_ok=True)
        for index, moment in enumerate(moments, 1):
            sid = re.sub(r"[^a-zA-Z0-9_-]", "_", moment.sid)[:50]
            stamp = f"{moment.st:.3f}" if moment.st is not None else "file-order"
            target = output / f"{index:03d}-{sid}-{stamp}.html"
            target.write_text(render_html(moment, warnings), encoding="utf-8")
            print(f"{target}: {len(moment.machines)} machine(s), moment {stamp}")
            # Keep stdout brief; the complete evidence and settings comparison is on the page.
            lines = findings(moment)
            highlights = [line for line in lines if "timeline not synced" in line or "session time unavailable" in line or "only " in line
                          or "recorded frames over" in line or "all with" in line
                          or "generation backlog on" in line or "One machine only" in line]
            highlights += [line for line in lines if line.startswith("Network ") and " differs" in line
                           and any(metric in line for metric in ("ping", "local loss", "sent bytes/frame"))]
            state_lines = [line for line in lines if line.startswith("State")]
            event_lines = [line for line in lines if line.startswith("Event")]
            highlights = [line for line in highlights if not line.startswith(("State", "Event"))]
            for line in state_lines + event_lines[:40] + highlights[:10]:
                print("  " + line)
            if len(event_lines) > 40:
                print(f"  {len(event_lines) - 40} more related events in the HTML page.")
            print("  Full findings and charts are in the HTML page.")
    except OSError as exc:
        print(f"Cannot write comparison: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
