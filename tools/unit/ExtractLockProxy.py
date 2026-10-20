"""Compile the production role transition against the lock tests' game surfaces."""
from pathlib import Path
import sys

source = (Path(__file__).resolve().parents[2] / 'src/TLDRevamp/Net/Entities.cs').read_text(encoding='utf-8')


def method(name):
    start = source.index('        private static void ' + name + '(')
    brace = source.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


assert '                TickPoseLock(e);' in source, 'loaded proxies must check the game lock every tick'
assert 'e.ReceivedStateEpoch == e.Epoch && LockedWorld(e,' in source, 'dormant locks must respect the epoch'
target = Path(sys.argv[1])
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text('using UnityEngine;\nnamespace TLDRevamp.Net { public static partial class Entities {\n' +
                  method('SetProxy') + '\n' + method('ApplyProxyBodies') + '\n} }\n', encoding='utf-8', newline='\n')
