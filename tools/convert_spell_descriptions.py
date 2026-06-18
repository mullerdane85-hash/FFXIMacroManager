#!/usr/bin/env python3
"""
Convert FFXIMissingSpells/libs/spell_info.lua (BG-Wiki scraped spell
descriptions) into FFXIMacroManager/data/descriptions.json.

The output is a single shared file keyed by lowercase action name so
the .exe can look up spells, job abilities, and weapon skills through
one dictionary. This script only fills SPELL entries; the BG-Wiki
JA + WS scraper (scrape_bgwiki_descriptions.py) merges its results
into the same file.

Usage:
    python tools/convert_spell_descriptions.py [--merge]

    --merge: keep any existing entries (e.g. JA/WS) in descriptions.json
             and only insert/replace the spells. Default: overwrite.
"""
import json, sys, os, argparse
from slpp import slpp as lua

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
# spell_info.lua lives in the FFXIMissingSpells addon. We search the
# usual location first, then fall back to env var.
DEFAULT_SRC = r'C:\Users\Jason\Desktop\Windower\addons\FFXIMissingSpells\libs\spell_info.lua'
SRC = os.environ.get('SPELL_INFO_LUA') or DEFAULT_SRC
OUT = os.path.join(ROOT, 'data', 'descriptions.json')

def parse_lua_table(path):
    import re
    with open(path, 'r', encoding='utf-8') as f:
        text = f.read()
    # slpp doesn't tolerate Lua single-line comments; strip them.
    # We only handle `--` to end-of-line; the source doesn't use --[[ ]]--.
    text = re.sub(r'(?m)^\s*--.*$', '', text)
    text = re.sub(r'\s+--[^\n]*$', '', text, flags=re.MULTILINE)
    text = text.lstrip()
    if text.startswith('return'):
        text = text[len('return'):].lstrip()
    return lua.decode(text)

def normalize(entry):
    """Strip empty values and ensure keys exist."""
    out = {}
    for k in ('description', 'type', 'target', 'mp_cost', 'cast_time',
             'recast_time', 'tp_cost', 'duration'):
        v = entry.get(k)
        if v: out[k] = str(v).strip()
    notes = entry.get('notes') or []
    if isinstance(notes, dict):
        notes = list(notes.values())
    notes = [str(n).strip() for n in notes if n]
    if notes: out['notes'] = notes
    out['kind'] = 'spell'
    return out

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--merge', action='store_true',
                    help='Preserve existing non-spell entries.')
    args = ap.parse_args()

    print('Reading', SRC)
    table = parse_lua_table(SRC)
    print(f'Parsed {len(table)} entries from spell_info.lua')

    out = {}
    if args.merge and os.path.exists(OUT):
        with open(OUT, 'r', encoding='utf-8') as f:
            out = json.load(f)
        # Drop existing spells; we're about to repopulate them.
        out = {k: v for k, v in out.items() if v.get('kind') != 'spell'}
        print(f'Merging into existing file, kept {len(out)} non-spell entries')

    for name, entry in table.items():
        if not isinstance(entry, dict): continue
        out[name.lower()] = normalize(entry)
        out[name.lower()]['name'] = name   # preserve original casing for display

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, sort_keys=True, ensure_ascii=False)
    print(f'Wrote {len(out)} total entries to {OUT}')

if __name__ == '__main__':
    main()
