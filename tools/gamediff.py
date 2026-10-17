#!/usr/bin/env python3
"""Read-only game DLL version guard; derived decompiles stay outside git."""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tldenv

GAME = tldenv.GAME_DIR
CACHE = tldenv.CACHE / 'decomp'
REPO = Path(__file__).resolve().parents[1]
ILSPY = tldenv.ILSPY
DLLS = ('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll')
MODS = r'(?:public|private|protected|internal|static|virtual|override|sealed|abstract|new|async|extern|unsafe|partial|readonly|implicit|explicit)'
# A member header up to its opening parenthesis: modifiers, an optional return type (generic, array, nullable, tuple),
# the name (a generic method's type parameters, an operator). Parameters, constraints and the body are scanned by hand
# (balanced parentheses: default values and attributes inside parameter lists).
HEAD = re.compile(r'(?m)^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:' + MODS + r'\s+)+(?:(?!operator\b)[\w.<>,?\[\]() ]+?\s+)?(?P<name>operator\s*(?:true|false|[^\s(\w]+|\w+)|\w+)\s*(?:<[^;{}()]*>)?\s*\(')
TYPE = re.compile(r'\b(?:class|struct|interface|enum|record)\s+(\w+)[^{;=()]*\{')
PATCH = re.compile(r'\[\s*(?:HarmonyLib\.)?HarmonyPatch\s*\(\s*typeof\s*\(\s*([\w.]+)\s*\)\s*,\s*(?:"(\w+)"|nameof\s*\(\s*[\w.]+\.(\w+)\s*\))')
NAMEOF = re.compile(r'\bnameof\s*\(\s*([\w.]+)\.(\w+)\s*\)')
ACCESS = re.compile(r'\bAccessTools\.(Method|Field|Property|DeclaredMethod|DeclaredField|FieldRefAccess)\s*(?:<\s*([\w.]+)[^>]*>)?\s*\(\s*(?:typeof\s*\(\s*([\w.]+)\s*\)\s*,\s*)?"(\w+)"')
FIELD = re.compile(r'(?m)^\s*(?:public|private|protected|internal)\s+(?:(?:static|readonly|const|volatile|new)\s+)*[\w.<>,?\[\] ]+\s+(\w+)\s*(?:=[^;]*)?;')


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1048576), b''):
            h.update(block)
    return h.hexdigest()


def norm(value):
    # Ignore layout/comments, but keep quoted values byte-for-byte: a changed
    # string constant in a patched method is behavior, not whitespace noise.
    tokens = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
    result, last = [], 0
    for match in tokens.finditer(value):
        result.append(re.sub(r'\s+', '', value[last:match.start()]))
        if not match.group().startswith(('//', '/*')):
            result.append(match.group())
        last = match.end()
    result.append(re.sub(r'\s+', '', value[last:]))
    return ''.join(result)


def mask(text):
    pattern = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
    return pattern.sub(lambda m: ''.join('\n' if c == '\n' else ' ' for c in m.group()), text)


def close(text, i, opening, closing):
    """index just past the bracket matching text[i] (masked text: no strings or comments)"""
    depth = 0
    while i < len(text):
        depth += (text[i] == opening) - (text[i] == closing)
        i += 1
        if depth == 0:
            return i
    return len(text)


def parse(text):
    """{type path: {member: {argument key: {signature, body}}, '__fields__': {...}}} for one decompiled file — nested
    types under their own dotted path ('' = the file's own type), so Outer.Inner.M never collides with Outer.M."""
    original = text
    text = mask(text)
    spans = []
    for m in TYPE.finditer(text):
        brace = m.end() - 1
        spans.append((brace, close(text, brace, '{', '}'), m.group(1)))

    def owner(pos):
        path = [name for begin, finish, name in spans if begin < pos < finish]
        return '.'.join(path[1:])   # the outermost one is the file's own type

    result = defaultdict(lambda: defaultdict(dict))
    for m in HEAD.finditer(text):
        name = re.sub(r'\s+', '', m.group('name'))
        if name in {'if', 'for', 'foreach', 'while', 'switch', 'catch', 'lock', 'using', 'return', 'new', 'nameof', 'typeof', 'sizeof', 'default'}:
            continue
        typ = owner(m.start())
        if not any(begin < m.start() < finish for begin, finish, _ in spans):
            continue
        args_end = close(text, m.end() - 1, '(', ')')
        j = args_end
        while j < len(text) and text[j] not in '{;' and not text.startswith('=>', j):
            j += 1   # where-constraints, a constructor's base(...) call
        if j >= len(text) or text[j] == ';':
            continue   # abstract, extern, interface member: no body
        if text[j] == '{':
            body_end = close(text, j, '{', '}')
            body = original[j + 1:body_end - 1]
        else:
            body_end = j + 2
            depth = 0
            while body_end < len(text) and not (text[body_end] == ';' and depth == 0):
                depth += (text[body_end] in '({[') - (text[body_end] in ')}]')
                body_end += 1
            body = original[j + 2:body_end]
        key = norm(original[m.end():args_end - 1])
        result[typ][name][key] = {'signature': norm(original[m.start():j]), 'body': norm(body)}
    for m in FIELD.finditer(text):
        if '(' in m.group() or '=>' in m.group() or not any(begin < m.start() < finish for begin, finish, _ in spans):
            continue
        result[owner(m.start())].setdefault('__fields__', {})[m.group(1)] = norm(original[m.start():m.end()])
    return {typ: dict(members) for typ, members in result.items()}


def tree(folder):
    if not folder.is_dir():
        raise ValueError(f'missing decompile {folder}')
    result = {}
    for path in sorted(folder.rglob('*.cs')):
        if path.name.startswith('AssemblyInfo'):
            continue
        typ = path.relative_to(folder).with_suffix('').as_posix().replace('/', '.')
        for inner, members in parse(path.read_text(errors='replace')).items():
            members.setdefault('__fields__', {})
            result[typ + '.' + inner if inner else typ] = members
    if not result:
        raise ValueError(f'empty decompile {folder}')
    return result


def dependencies(src, types):
    """types: {game type: its member and field names} (both builds)"""
    short = defaultdict(set)
    for typ in types:
        short[typ.split('.')[-1]].add(typ)
    hits, unknown = defaultdict(set), set()

    def add(typ, member, why, file, text, pos):
        if typ.startswith(('System.', 'UnityEngine.', 'Steamworks.', 'HarmonyLib.')):
            return
        candidates = short.get(typ.split('.')[-1], set())
        if len(candidates) > 1 and '.' in typ:   # mainscript.mirror: the nested type of that name
            candidates = {c for c in candidates if c == typ or c.endswith('.' + typ)} or candidates
        if len(candidates) > 1:   # several nested types of that name: the one(s) that have the member
            candidates = {c for c in candidates if member in types[c]} or candidates
        if why in ('direct type access', 'declared instance access', 'nameof') and not any(member in types[c] for c in candidates):
            return   # not a game member (an engine type's, a local's): nothing to track
        loc = f'{file}:{text.count(chr(10), 0, pos) + 1}'
        if len(candidates) == 1:
            hits[(next(iter(candidates)), member)].add(loc + ' ' + why)
        elif candidates and all(member in types[c] for c in candidates):   # each could be meant: all tracked
            for c in candidates:
                hits[(c, member)].add(loc + ' ' + why + ' (one of ' + ', '.join(sorted(candidates)) + ')')
        else:
            unknown.add(loc + f' ambiguous game type {typ}.{member}')

    for path in sorted(src.rglob('*.cs')):
        text = path.read_text(errors='replace')
        file = path.resolve().relative_to(REPO).as_posix()
        for m in PATCH.finditer(text):
            add(m.group(1), m.group(2) or m.group(3), 'HarmonyPatch', file, text, m.start())
        for m in NAMEOF.finditer(text):
            add(m.group(1), m.group(2), 'nameof', file, text, m.start())
        for m in ACCESS.finditer(text):
            typ = m.group(2) or m.group(3)
            if typ:
                add(typ, m.group(4), 'AccessTools.' + m.group(1), file, text, m.start())
            else:
                unknown.add(f'{file}:{text.count(chr(10), 0, m.start()) + 1} dynamic AccessTools {m.group(4)}')
        clean = mask(text)
        for m in re.finditer(r'\b([A-Za-z_]\w*)\.([A-Za-z_]\w*)\b', clean):
            if m.group(1) in short:
                add(m.group(1), m.group(2), 'direct type access', file, text, m.start())
        if short:
            alternatives = '|'.join(re.escape(k) for k in sorted(short, key=len, reverse=True))
            variables = defaultdict(set)
            for m in re.finditer(r'\b(' + alternatives + r')\s+(\w+)\s*(?:[=;,)]|\[)', clean):
                variables[m.group(2)].add(m.group(1))
            for var, candidates in variables.items():
                if len(candidates) == 1:
                    for m in re.finditer(r'\b' + re.escape(var) + r'\s*(?:\?\.)?\.\s*(\w+)', clean):
                        add(next(iter(candidates)), m.group(1), 'declared instance access', file, text, m.start())
        if 'TargetMethods()' in text or 'Transpiler(' in text:
            unknown.add(f'{file}: inspect TargetMethods and transpiler operands manually')
    return hits, sorted(unknown)


def compare(old, new, uses):
    result = []
    for typ in sorted(old.keys() | new.keys()):
        before, after = old.get(typ, {}), new.get(typ, {})
        field_old, field_new = before.get('__fields__', {}), after.get('__fields__', {})
        for field in sorted(field_old.keys() | field_new.keys()):
            if field_old.get(field) == field_new.get(field):
                continue
            match = sorted(uses.get((typ, field), []))
            change = 'field removed' if field not in field_new else 'field added' if field not in field_old else 'field declaration changed'
            level = 'high' if match and change != 'field added' else 'same-type' if any(t == typ for t, _ in uses) else 'other'
            result.append({'type': typ, 'member': field, 'change': change, 'severity': level, 'uses': match})
        for name in sorted((before.keys() | after.keys()) - {'__fields__'}):
            left, right = before.get(name, {}), after.get(name, {})
            if left == right:
                continue
            if typ not in old or typ not in new:
                change = 'type added' if typ not in old else 'type removed'
            elif not left or not right:
                change = 'method added' if not left else 'method removed'
            elif left.keys() != right.keys() or any(left[k]['signature'] != right[k]['signature'] for k in left.keys() & right.keys()):
                change = 'signature changed'
            else:
                change = 'body changed'
            match = sorted(uses.get((typ, name), []))
            level = 'high' if match and change not in ('type added', 'method added') else 'same-type' if any(t == typ for t, _ in uses) else 'other'
            result.append({'type': typ, 'member': name, 'change': change, 'severity': level, 'uses': match})
    result.sort(key=lambda row: (('high', 'same-type', 'other').index(row['severity']), row['type'], row['member']))
    return result


def buildid(game):
    """Steam's build id of this install (its app manifest two levels up), or None for a copy outside a library"""
    manifest = game.parent.parent / 'appmanifest_1017180.acf'
    try:
        m = re.search(r'"buildid"\s+"(\d+)"', manifest.read_text(errors='replace'))
        return m.group(1) if m else None
    except OSError:
        return None


def decompile(dll, out, ilspy):
    out.mkdir(parents=True, exist_ok=False)
    subprocess.run([str(ilspy), '-p', '-o', str(out), str(dll)], check=True, stdout=subprocess.DEVNULL)
    tree(out)


def outside(path, game):
    path = path.expanduser().resolve()
    for root in (REPO.resolve(), game):
        if path == root or root in path.parents:
            raise ValueError('output must be outside game install and worktree')
    return path


def run(args):
    game = args.game.expanduser().resolve()
    cache = outside(args.cache, game)
    reportpath = outside(args.report if args.report else cache / 'report.json', game)
    managed = game / 'TheLongDrive_Data/Managed'
    hashes = {name: sha(managed / name) for name in DLLS}
    cache.mkdir(parents=True, exist_ok=True)
    indexpath = cache / 'index.json'
    index = json.loads(indexpath.read_text()) if indexpath.exists() else {'versions': []}
    versions = index['versions']
    current = next((row for row in versions if row['hashes'] == hashes), None)
    rollback = current is not None and current is not versions[-1]
    if rollback:
        # an older known build again (a rollback, a branch switch): compared with the build it replaces, and the newest
        # entry from now on
        previous = versions[-1]
        versions.remove(current)
        current['seen_again'] = datetime.now(timezone.utc).isoformat()
        versions.append(current)
        indexpath.write_text(json.dumps(index, indent=2) + '\n')
    else:
        previous = (versions[-1] if versions and current is None else
                    versions[-2] if len(versions) > 1 and current == versions[-1] else None)
    folder = cache / (current['directory'] if current else hashes[DLLS[0]][:12])
    if current is None:
        known_main = any(v['hashes'][DLLS[0]] == hashes[DLLS[0]] for v in versions)
        if folder.exists() and not known_main:
            raise ValueError('decompile exists without matching main DLL index entry: ' + str(folder))
        if not folder.exists():
            decompile(managed / DLLS[0], folder, args.ilspy)
        else:
            tree(folder)
        current = {'hashes': hashes, 'date': datetime.now(timezone.utc).isoformat(), 'buildid': buildid(game), 'directory': folder.name}
        versions.append(current)
        indexpath.write_text(json.dumps(index, indent=2) + '\n')
    before, after = (tree(cache / previous['directory']) if previous else {}), tree(folder)
    members = defaultdict(set)
    for side in (before, after):
        for typ, entry in side.items():
            members[typ] |= set(entry) | set(entry.get('__fields__', {}))
    uses, unknown = dependencies(REPO / 'src/TLDRevamp', members)
    changes = compare(before, after, uses) if previous else []
    limits = unknown + ['Static heuristic misses inferred/chained member types, aliases, reflection, delegates, virtual dispatch, dynamic targets, some transpiler operands, properties and constructors.', 'Method parser is heuristic; manually inspect unparsed or overloaded members and IL before relying on compatibility.', 'First snapshot has no historical baseline; firstpass-only changes require manual review.']
    firstpass_changed = bool(previous and previous['hashes'][DLLS[1]] != hashes[DLLS[1]])
    if firstpass_changed:
        limits.append('Assembly-CSharp-firstpass.dll changed; its methods are not decompiled by this guard. Manual review required.')
    others = []
    for extra in args.also:
        extra = extra.expanduser()
        if not (extra / 'TheLongDrive_Data/Managed' / DLLS[0]).exists():
            continue
        theirs = {name: sha(extra / 'TheLongDrive_Data/Managed' / name) for name in DLLS}
        others.append({'game': str(extra), 'same': theirs == hashes, 'hashes': theirs, 'buildid': buildid(extra)})
    differing = [o for o in others if not o['same']]
    report = {'current': current, 'previous': previous, 'rollback': rollback, 'changes': changes, 'firstpass_changed': firstpass_changed, 'other_installs': others, 'counts': {x: sum(row['severity'] == x for row in changes) for x in ('high', 'same-type', 'other')}, 'tracked_members': len(uses), 'static_analysis_limits': limits}
    reportpath.parent.mkdir(parents=True, exist_ok=True)
    reportpath.write_text(json.dumps(report, indent=2) + '\n')
    lines = [f'Current Assembly-CSharp.dll: {hashes[DLLS[0]]} (Steam build {buildid(game) or "unknown"})', f'Previous: {previous["hashes"][DLLS[0]] if previous else "none (baseline)"}' + (' — ROLLBACK to an older known build' if rollback else ''), f'Method changes: {report["counts"]}; tracked members: {len(uses)}']
    for o in others:
        lines.append(f'Other install {o["game"]}: ' + ('same DLLs' if o['same'] else f'DIFFERENT DLLs (Assembly-CSharp {o["hashes"][DLLS[0]][:12]}, build {o["buildid"] or "unknown"}) — run with --game there'))
    for row in changes:
        lines.append(f'{row["severity"].upper()} {row["type"]}.{row["member"]}: {row["change"]}')
        lines.extend('  ' + use for use in row['uses'][:10])
    lines.extend(['Coverage limits:'] + ['  ' + line for line in limits])
    text = '\n'.join(lines) + '\n'
    print(text, end='')
    reportpath.with_suffix('.txt').write_text(text)
    return 1 if report['counts']['high'] or firstpass_changed or differing or (rollback and changes) else 0


def selftest(args):
    dll = args.game / 'TheLongDrive_Data/Managed/Assembly-CSharp.dll'
    with tempfile.TemporaryDirectory(prefix='gamediff-', dir=Path.home()) as temp:
        a, b = Path(temp) / 'a', Path(temp) / 'b'
        decompile(dll, a, args.ilspy)
        decompile(dll, b, args.ilspy)
        old, new = tree(a), tree(b)
        assert old == new, 'identical DLL decompiles differ'
        typ = next(t for t in old if t.endswith('itemPlaceRemoveScript'))
        assert 'PlaceStuff' in old[typ], 'patched method not parsed'
        uses = {(typ, 'PlaceStuff'): {'synthetic patched member'}}
        target = b / 'itemPlaceRemoveScript.cs'
        pristine = target.read_text()
        method = re.search(r'public void PlaceStuff\s*\(\s*\)\s*\{', pristine)
        assert method, 'missing patched method in copied decompile'
        target.write_text(pristine[:method.end()] + '\nint syntheticBodyChange = 17;\n' + pristine[method.end():])
        assert compare(old, tree(b), uses)[0]['change'] == 'body changed'
        assert compare(old, tree(b), uses)[0]['severity'] == 'high'
        target.write_text(pristine.replace(method.group(), 'public void RemovedPlaceStuff()\n{', 1))
        assert compare(old, tree(b), uses)[0]['change'] == 'method removed'
        target.write_text(pristine.replace(method.group(), 'public void PlaceStuff(int syntheticParameter)\n{', 1))
        assert compare(old, tree(b), uses)[0]['change'] == 'signature changed'
        target.write_text(pristine)
        assert tree(a) == tree(b)
        # Changes to string payloads must not disappear as "whitespace".
        target.write_text(pristine[:method.end()] + '\nvar syntheticString = "a b";\n' + pristine[method.end():])
        assert compare(old, tree(b), uses)[0]['change'] == 'body changed'
        target.write_text(pristine)
        # a nested type's method is its own entry: an edit of objGenScript.categoryClass.UpdGen is reported there, and
        # objGenScript.UpdGen (same name, same arguments) stays unchanged
        nested = b / 'objGenScript.cs'
        npristine = nested.read_text()
        cls = re.search(r'class categoryClass\b', npristine)
        inner = re.compile(r'public void UpdGen\s*\([^)]*\)\s*\{').search(npristine, cls.end())
        nested.write_text(npristine[:inner.end()] + '\nint syntheticNested = 3;\n' + npristine[inner.end():])
        rows = compare(old, tree(b), {})
        assert [(r['type'], r['member'], r['change']) for r in rows] == [('objGenScript.categoryClass', 'UpdGen', 'body changed')], rows
        nested.write_text(npristine)
        # the mod's [HarmonyLib.HarmonyPatch(typeof(X), "Y")] and nested-type patch targets are tracked
        members = defaultdict(set)
        for typ, entry in old.items():
            members[typ] |= set(entry) | set(entry.get('__fields__', {}))
        found, _ = dependencies(REPO / 'src/TLDRevamp', members)
        for want in (('enginescript', 'Update'), ('mainscript.mirror', 'Turn')):
            assert want in found, f'{want} not tracked'
        # a rollback to an older known build is compared with the build it replaces
        cache = Path(temp) / 'cache'
        cache.mkdir()
        shutil.copytree(a, cache / 'old')
        shutil.copytree(b, cache / 'new')
        newer = cache / 'new' / 'itemPlaceRemoveScript.cs'
        newer.write_text(pristine[:method.end()] + '\nint syntheticNewer = 5;\n' + pristine[method.end():])
        now = {name: sha(args.game / 'TheLongDrive_Data/Managed' / name) for name in DLLS}
        index = {'versions': [{'hashes': now, 'date': 'then', 'directory': 'old'},
                              {'hashes': {name: 'f' * 64 for name in DLLS}, 'date': 'later', 'directory': 'new'}]}
        (cache / 'index.json').write_text(json.dumps(index))
        rb = argparse.Namespace(game=args.game, cache=cache, report=cache / 'report.json', ilspy=args.ilspy, also=[])
        code = run(rb)
        report = json.loads((cache / 'report.json').read_text())
        assert report['rollback'] and any(r['type'] == 'itemPlaceRemoveScript' and r['member'] == 'PlaceStuff' for r in report['changes']), report['changes'][:3]
        assert code == 1 and json.loads((cache / 'index.json').read_text())['versions'][-1]['directory'] == 'old'
    print('selftest passed: identical DLL twice; modified patched body, removed method, changed signature; nested-type edit; '
          'HarmonyLib attribute and nested patch targets tracked; rollback compared')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selftest', action='store_true')
    parser.add_argument('--game', type=Path, default=GAME)
    parser.add_argument('--cache', type=Path, default=CACHE)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--ilspy', type=Path, default=ILSPY)
    parser.add_argument('--also', type=Path, action='append', default=None,
                        help='another install to compare DLL hashes with (repeatable; default: TLD_ALSO, os.pathsep-separated)')
    args = parser.parse_args()
    if args.also is None:
        args.also = [Path(p).expanduser() for p in os.environ.get('TLD_ALSO', '').split(os.pathsep) if p]
    try:
        if args.selftest:
            selftest(args)
            return 0
        return run(args)
    except (OSError, ValueError, subprocess.CalledProcessError, AssertionError, KeyError, StopIteration) as exc:
        print('gamediff: ' + str(exc), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
