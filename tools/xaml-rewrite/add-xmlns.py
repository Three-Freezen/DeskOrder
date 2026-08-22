#!/usr/bin/env python3
"""Add xmlns:loc declaration to any XAML file using {loc:Loc ...}."""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
NS_DECL = ' xmlns:loc="clr-namespace:DesktopZones.Helpers"'

files = list(REPO.glob("Views/**/*.xaml")) + list(REPO.glob("Views/*.xaml"))
fixed = 0
for f in files:
    text = f.read_text(encoding="utf-8")
    if "{loc:Loc" not in text:
        continue
    if 'xmlns:loc=' in text:
        continue
    # Find first tag, insert after the existing xmlns:x="..." line (or at first line if not found)
    new = re.sub(
        r'(xmlns:x="http://schemas\.microsoft\.com/winfx/2006/xaml")',
        r'\1' + NS_DECL,
        text,
        count=1,
    )
    if new == text:
        # Fallback: insert after the first opening tag's opening angle
        m = re.search(r'(<\w[^>]*?)\s*xmlns="http', text)
        if m:
            new = text[:m.start()] + m.group(1) + NS_DECL + text[m.start() + len(m.group(1)):]
    if new != text:
        f.write_text(new, encoding="utf-8")
        fixed += 1
        print(f"  added xmlns:loc: {f.relative_to(REPO)}")
print(f"\nTotal: {fixed} files updated")
