#!/usr/bin/env python3
"""Verify every key referenced in XAML/C# has a zh + en entry."""
import json, os, re

def read_all(glob_pattern):
    out = []
    for root, _, files in os.walk('.'):
        for f in files:
            if not (glob_pattern == '*.xaml' and f.endswith('.xaml')
                    or glob_pattern == '*.cs' and f.endswith('.cs')):
                continue
            p = os.path.join(root, f)
            try:
                with open(p, encoding='utf-8') as fp:
                    out.append(fp.read())
            except (UnicodeDecodeError, OSError):
                pass
    return '\n'.join(out)

xaml_text = read_all('*.xaml')
cs_text = read_all('*.cs')

xaml_keys = set()
xaml_keys.update(re.findall(r'loc:Loc\s+([A-Za-z0-9_.]+)', xaml_text))
xaml_keys.update(re.findall(r'loc:Loc\s+Key=([A-Za-z0-9_.]+)', xaml_text))
xaml_keys.update(re.findall(r'Path=\[([A-Za-z0-9_.]+)\]', xaml_text))
xaml_keys.update(re.findall(r'LocalizationService\.Instance\["([A-Za-z][A-Za-z0-9_.]*)"\]', cs_text))

with open('i18n/source.zh.json', encoding='utf-8') as f:
    zh = json.load(f)
with open('i18n/source.en.json', encoding='utf-8') as f:
    en = json.load(f)
defined_zh = set(k for k in zh if not k.startswith('_'))
defined_en = set(k for k in en if not k.startswith('_'))

print(f'referenced: {len(xaml_keys)}')
print(f'defined zh: {len(defined_zh)}')
print(f'defined en: {len(defined_en)}')
print(f'missing zh: {sorted(xaml_keys - defined_zh)}')
print(f'missing en: {sorted(xaml_keys - defined_en)}')
print(f'unused:     {sorted(defined_zh - xaml_keys)}')

assert defined_zh == defined_en, 'zh and en have different keys'
assert xaml_keys <= defined_zh, 'missing zh translations'
assert xaml_keys <= defined_en, 'missing en translations'
print('\n✓ all referenced keys translated in both languages')
