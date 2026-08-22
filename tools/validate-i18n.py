#!/usr/bin/env python3
"""Verify every key referenced in XAML and via LocalizationService.Instance has a zh + en entry.

Keys referenced through `_loc["..."]` / `loc["..."]` (services / view-models) are listed
informatively but not asserted — translating every widget-internal string is a separate
scope decision. Add to the strict set when you're ready to commit to that scope.
"""
import json, os, re

def read_all(glob_pattern):
    out = []
    for root, _, files in os.walk('.'):
        for f in files:
            if not (glob_pattern == '*.xaml' and f.endswith('.xaml')
                    or glob_pattern == '*.xaml.cs' and f.endswith('.xaml.cs')
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
xaml_cs_text = read_all('*.xaml.cs')
cs_text = read_all('*.cs')

# Strict: keys that MUST be translated (XAML markup + the canonical singleton accessor)
strict = set()
strict.update(re.findall(r'loc:Loc\s+([A-Za-z0-9_.]+)', xaml_text))
strict.update(re.findall(r'loc:Loc\s+Key=([A-Za-z0-9_.]+)', xaml_text))
strict.update(re.findall(r'Path=\[([A-Za-z0-9_.]+)\]', xaml_text))
strict.update(re.findall(r'LocalizationService\.Instance\["([A-Za-z][A-Za-z0-9_.]*)"\]', cs_text))

# Informational: other localizable references
informational = set()
informational.update(re.findall(r'_?loc\["([A-Za-z][A-Za-z0-9_.]*)"\]', cs_text + xaml_cs_text))
informational.update(re.findall(r'_?loc\.Get\("([A-Za-z][A-Za-z0-9_.]*)"', cs_text + xaml_cs_text))
informational -= strict  # don't double-count

with open('i18n/source.zh.json', encoding='utf-8') as f:
    zh = json.load(f)
with open('i18n/source.en.json', encoding='utf-8') as f:
    en = json.load(f)
def_zh = set(k for k in zh if not k.startswith('_'))
def_en = set(k for k in en if not k.startswith('_'))

print(f'strict refs:        {len(strict)}')
print(f'informational refs: {len(informational)}')
print(f'defined zh:         {len(def_zh)}')
print(f'defined en:         {len(def_en)}')
print(f'strict missing zh:  {sorted(strict - def_zh)}')
print(f'strict missing en:  {sorted(strict - def_en)}')
print(f'info missing zh:    {sorted(informational - def_zh)}')
print(f'info missing en:    {sorted(informational - def_en)}')
print(f'unused:             {sorted(def_zh - strict - informational)}')

assert def_zh == def_en, 'zh and en have different keys'
assert strict <= def_zh, 'strict keys missing from zh'
assert strict <= def_en, 'strict keys missing from en'
print('\n[OK] all strict (XAML + LocalizationService.Instance) keys translated in both languages')
