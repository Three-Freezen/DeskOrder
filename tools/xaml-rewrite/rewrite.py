#!/usr/bin/env python3
"""
Bulk XAML rewriter: replaces hardcoded Chinese strings in attribute values
with {loc:Loc Key} bindings, using i18n/source.zh.json as the source of truth.

Usage:
  python rewrite.py [--dry-run] [--target PATTERN]

Pattern: {Text,Header,Content,ToolTip,Title}="..." containing Chinese chars
"""
import json
import re
import sys
import argparse
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
I18N_DIR = REPO / "i18n"
VIEWS_DIR = REPO / "Views"

# Attributes that hold localizable text
ATTRS = ("Text", "Header", "Content", "ToolTip", "Title")

def load_zh_keys() -> dict[str, str]:
    """Returns {chinese_value: key} mapping."""
    src = (I18N_DIR / "source.zh.json").read_text(encoding="utf-8")
    data = json.loads(src)
    return {v: k for k, v in data.items() if not k.startswith("_")}

def rewrite_file(path: Path, zh_map: dict[str, str], dry_run: bool) -> tuple[int, list[str]]:
    """Returns (replacements_count, unmatched_strings)."""
    text = path.read_text(encoding="utf-8")
    unmatched = []
    count = 0

    # Match attr="..." (non-greedy, no newlines)
    pattern = re.compile(rf'({"|".join(ATTRS)})="([^"]*[一-鿿]+[^"]*)"')

    def replacer(m):
        nonlocal count
        attr = m.group(1)
        value = m.group(2)
        if value in zh_map:
            key = zh_map[value]
            count += 1
            return f'{attr}"={{loc:Loc {key}}}"'
        # Try trimming trailing whitespace
        stripped = value.rstrip()
        if stripped in zh_map:
            key = zh_map[stripped]
            count += 1
            return f'{attr}"={{loc:Loc {key}}}"'
        unmatched.append(value)
        return m.group(0)

    new_text = pattern.sub(replacer, text)
    if not dry_run and new_text != text:
        path.write_text(new_text, encoding="utf-8")
    return count, unmatched

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--target", help="Glob pattern (default: all Views XAML)")
    args = ap.parse_args()

    zh_map = load_zh_keys()
    print(f"Loaded {len(zh_map)} Chinese->Key mappings")

    if args.target:
        files = list(REPO.glob(args.target))
    else:
        files = list(VIEWS_DIR.rglob("*.xaml"))

    total = 0
    all_unmatched: dict[str, int] = {}
    for f in files:
        n, unmatched = rewrite_file(f, zh_map, args.dry_run)
        if n:
            print(f"  {n:>3} replacements: {f.relative_to(REPO)}")
        total += n
        for u in unmatched:
            all_unmatched[u] = all_unmatched.get(u, 0) + 1

    print(f"\nTotal: {total} replacements")
    if all_unmatched:
        print(f"\n{len(all_unmatched)} unmatched strings (need manual handling):")
        for s, c in sorted(all_unmatched.items(), key=lambda x: -x[1]):
            print(f"  {c:>3}x  {s!r}")

if __name__ == "__main__":
    main()
