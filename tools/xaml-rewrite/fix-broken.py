#!/usr/bin/env python3
"""Fix broken {loc:Loc X} attribute syntax produced by buggy initial rewrite.
Pattern to fix: Text={loc:Loc Manage.X}  ->  Text="{loc:Loc Manage.X}"
"""
import re
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
ATTRS = ("Text", "Header", "Content", "ToolTip", "Title")

files = list(REPO.glob("Views/**/*.xaml")) + list(REPO.glob("Views/*.xaml"))
fixed = 0
for f in files:
    text = f.read_text(encoding="utf-8")
    orig = text
    # Fix: Attr={loc:Loc Key} -> Attr="{loc:Loc Key}"
    for attr in ATTRS:
        text = re.sub(
            rf'(\b{attr})=(\{{loc:Loc ([^}}]+)\}})',
            rf'\1="\2"',
            text,
        )
    if text != orig:
        f.write_text(text, encoding="utf-8")
        fixed += 1
        print(f"  fixed: {f.relative_to(REPO)}")
print(f"\nTotal: {fixed} files fixed")
