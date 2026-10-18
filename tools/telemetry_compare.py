#!/usr/bin/env python3
"""Compare local telemetry: python tools/telemetry_compare.py <folder or files...> [-o out_dir].

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
        return [Capture(head, rows, source, owner, number(head.get("momentSt")), True)]
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


def window(machine, moment):
    """Return (x, record) pairs. x is session time, or honest record-order offsets."""
    captures = machine.captures
    boxes = [c for c in captures if c.blackbox]
    if boxes:
        boxes = [c for c in boxes if c.moment is None or moment.st is None or abs(c.moment - moment.st) <= 2]
        # One complete snapshot per machine/moment. Millisecond timestamps can repeat at high FPS.
        boxes = sorted(boxes, key=lambda c: (abs((c.moment or 0) - (moment.st or 0)), -len(c.records), c.source))[:1]
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


def findings(moment):
    lines, views = [], []
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", help="report zips, mark JSON, play logs, or folders (recursive)")
    parser.add_argument("-o", "--output", default="telemetry-comparison", help="output folder (default: %(default)s)")
    args = parser.parse_args(argv)
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
            for line in highlights[:10]:
                print("  " + line)
            print("  Full findings and charts are in the HTML page.")
    except OSError as exc:
        print(f"Cannot write comparison: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
