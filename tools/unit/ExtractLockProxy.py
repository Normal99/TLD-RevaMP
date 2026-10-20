"""Compile the production role transition against the lock tests' game surfaces."""
from pathlib import Path
import sys

source = (Path(__file__).resolve().parents[2] / 'src/TLDRevamp/Net/Entities.cs').read_text(encoding='utf-8')


def method(name, result='void'):
    start = source.index('        private static ' + result + ' ' + name + '(')
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
roles = method('SetProxy') + '\n' + method('ApplyProxyBodies')
roles += '\n' + method('RefuseClaim')
owner = source.split('                case Owner:\n                {', 1)[1].split('\n                }', 1)[0]
roles += '\n        static void ApplyOwner(NetReader r) {\n' + owner.replace('                    break;', '                    return;') + '\n        }\n'
control = source.split('                    bool control = ', 1)[1].split(';', 1)[0].replace('e.SentMoving', 'sentMoving')
roles += '\n        private static bool SampleControl(bool lockChange, bool wStored, bool wHeld, bool wSig, bool wSpray, bool wDriver, bool velocityMoving, bool sentMoving) => ' + control + ';\n'
source = (Path(__file__).resolve().parents[2] / 'src/TLDRevamp/Net/Entities.Cargo.cs').read_text(encoding='utf-8')
cargo = source[source.index('        private static bool CargoHeld('):source.index('        private static bool ClaimCargo(')]
roles += '\n' + cargo + method('ClaimCargo', 'bool') + '\n' + method('CargoNearReady', 'bool') + '\n' + method('IsHeld', 'bool')
source = (Path(__file__).resolve().parents[2] / 'src/TLDRevamp/Net/Entities.Attach.cs').read_text(encoding='utf-8')

target.write_text('using System;\nusing UnityEngine;\nusing System.Collections.Generic;\nnamespace TLDRevamp.Net { public static partial class Entities {\n' +
                  roles + '\n' + method('CascadeOwner') + '\n} }\n', encoding='utf-8', newline='\n')
