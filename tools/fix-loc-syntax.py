#!/usr/bin/env python3
"""Convert {loc:Loc A.B} → {loc:Loc Key=A.B} for all multi-segment keys (have dots).

Single-segment keys (no dots) stay as positional form. This matches the
LocExtension design comment: 'positional ctor ... Multi-segment keys with
dots need Key= syntax because the WPF XAML parser interprets dots after
the positional arg as member access.'
"""
import os, re

PAT = re.compile(r'\{loc:Loc\s+([A-Za-z][A-Za-z0-9]*\.[A-Za-z0-9_.]+)\b')

def fix(p):
    with open(p, encoding='utf-8') as fp:
        s = fp.read()
    new = PAT.sub(lambda m: f'{{loc:Loc Key={m.group(1)}', s)
    if new != s:
        with open(p, 'w', encoding='utf-8') as fp:
            fp.write(new)
        return True
    return False

changed = []
for root, _, files in os.walk('.'):
    if 'obj' in root or 'bin' in root:
        continue
    for f in files:
        if not f.endswith('.xaml'):
            continue
        p = os.path.join(root, f)
        try:
            if fix(p):
                changed.append(p)
        except (UnicodeDecodeError, OSError):
            pass

print(f'Fixed {len(changed)} files:')
for p in changed:
    print(f'  {p}')
