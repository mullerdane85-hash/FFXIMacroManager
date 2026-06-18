#!/usr/bin/env python3
"""
Scrape BG-Wiki job-ability and weapon-skill descriptions via the
MediaWiki API and merge them into data/descriptions.json.

Strategy:
  1. Read the existing job_abilities.json + weapon_skills.json (the
     ones FFXIMacroManager already ships, generated from Windower's
     resources) to get the canonical name list.
  2. For each name, fetch wikitext via MediaWiki API in batches of 50
     using prop=extracts (already-rendered plain-text intros).
  3. Also pull the infobox via prop=revisions&rvprop=content so we can
     extract structured fields (mp_cost, recast, type, target, ...).
  4. Merge into descriptions.json keyed by lowercase name with
     kind='ja' or 'ws'.

Run with no args. Cached HTML/JSON snapshots land in tools/_cache/ so
re-runs are nearly instant. Delete that folder to force a fresh fetch.

Network: ~ (200 JAs / 50 per batch) + (100 WSs / 50 per batch)
        = 4 extracts calls + 4 revisions calls = ~8 requests total.
"""
import os, sys, json, urllib.request, urllib.parse, re, time, argparse

HERE  = os.path.dirname(os.path.abspath(__file__))
ROOT  = os.path.dirname(HERE)
DATA  = os.path.join(ROOT, 'data')
CACHE = os.path.join(HERE, '_cache')
OUT   = os.path.join(DATA, 'descriptions.json')

API = 'https://www.bg-wiki.com/api.php'
UA  = {'User-Agent': 'FFXIMacroManager/1.0 description scraper (educational)'}

def http_get_json(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode('utf-8', 'replace'))

def cached_extracts(titles, batch_label):
    """Fetch plain-text intros for a list of titles. Batched, cached."""
    out = {}
    BATCH = 20  # API caps depend on server config; 20 is safe.
    for i in range(0, len(titles), BATCH):
        chunk = titles[i:i+BATCH]
        cache_file = os.path.join(CACHE, f'extracts_{batch_label}_{i:04d}.json')
        if os.path.exists(cache_file):
            with open(cache_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
        else:
            q = urllib.parse.urlencode({
                'action':       'query',
                'format':       'json',
                'prop':         'extracts',
                'explaintext':  '1',
                'exintro':      '1',
                'exsectionformat': 'plain',
                'titles':       '|'.join(t.replace(' ', '_') for t in chunk),
                'redirects':    '1',
            })
            url = f'{API}?{q}'
            print(f'  fetch {batch_label} batch {i}..{i+len(chunk)-1}')
            try:
                data = http_get_json(url)
            except Exception as ex:
                print(f'    error: {ex}')
                time.sleep(2.0)
                continue
            os.makedirs(CACHE, exist_ok=True)
            with open(cache_file, 'w', encoding='utf-8') as f:
                json.dump(data, f, indent=1)
            time.sleep(0.5)
        pages = (data.get('query') or {}).get('pages') or {}
        for pid, pdata in pages.items():
            if int(pid) < 0:  # missing page
                continue
            title = pdata.get('title', '')
            extract = pdata.get('extract', '') or ''
            extract = extract.strip()
            # Trim to first paragraph (BG-Wiki intros are often 1-2 sentences).
            extract = re.split(r'\n\s*\n', extract, 1)[0].strip()
            out[title] = extract
    return out

INFOBOX_RE = re.compile(
    r'\{\{\s*(?:Job\s*Ability|Weapon\s*Skill|Infobox[^|}\n]*)\b(.*?)(?=^\}\}|\Z)',
    re.DOTALL | re.MULTILINE | re.IGNORECASE)

def cached_wikitext(titles, batch_label):
    """Fetch raw wikitext for a list of titles. Used for infobox fields."""
    out = {}
    BATCH = 20
    for i in range(0, len(titles), BATCH):
        chunk = titles[i:i+BATCH]
        cache_file = os.path.join(CACHE, f'wikitext_{batch_label}_{i:04d}.json')
        if os.path.exists(cache_file):
            with open(cache_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
        else:
            q = urllib.parse.urlencode({
                'action':    'query',
                'format':    'json',
                'prop':      'revisions',
                'rvprop':    'content',
                'rvslots':   'main',
                'titles':    '|'.join(t.replace(' ', '_') for t in chunk),
                'redirects': '1',
            })
            url = f'{API}?{q}'
            print(f'  wikitext {batch_label} batch {i}..{i+len(chunk)-1}')
            try:
                data = http_get_json(url)
            except Exception as ex:
                print(f'    error: {ex}')
                time.sleep(2.0)
                continue
            os.makedirs(CACHE, exist_ok=True)
            with open(cache_file, 'w', encoding='utf-8') as f:
                json.dump(data, f, indent=1)
            time.sleep(0.5)
        pages = (data.get('query') or {}).get('pages') or {}
        for pid, pdata in pages.items():
            if int(pid) < 0:
                continue
            title = pdata.get('title', '')
            revs = pdata.get('revisions') or []
            if not revs: continue
            slots = revs[0].get('slots') or {}
            main = slots.get('main') or {}
            content = main.get('*') or revs[0].get('*') or ''
            out[title] = content
    return out

# Map BG-Wiki infobox field name -> our normalized key.
# Both Job Ability and Weapon Skill templates are flat key=value pairs.
INFOBOX_KEYS = {
    'desc':        'description',
    'description': 'description',
    'type':        'type',
    'family':      'type',
    'target':      'target',
    'targets':     'target',
    'mp':          'mp_cost',
    'mp cost':     'mp_cost',
    'tp':          'tp_cost',
    'tp cost':     'tp_cost',
    'cast time':   'cast_time',
    'casting time':'cast_time',
    'recast':      'recast_time',
    'recast time': 'recast_time',
    'duration':    'duration',
    'range':       'range',
    'skill':       'skill',
    'level':       'level',
    'weapon':      'weapon',
    'element':     'element',
    'job':         'job',
    'notes':       'notes_raw',
    'effect':      'effect',
    'command':     'command',
}

def _clean_wiki(s):
    """Strip wiki markup: links, bold/italic, HTML, templates."""
    if not s: return s
    s = re.sub(r'\[\[([^\]|]+\|)?([^\]]+)\]\]', r'\2', s)
    s = re.sub(r"'''([^']+)'''", r'\1', s)
    s = re.sub(r"''([^']+)''", r'\1', s)
    s = re.sub(r'<!--.*?-->', '', s, flags=re.DOTALL)
    s = re.sub(r'<[^>]+>', '', s)
    s = re.sub(r'\{\{[^}]*\}\}', '', s)
    return s.strip()

def parse_infobox(wikitext):
    """Return a dict of normalized infobox fields, or {}.

    We walk pipe-delimited key=value pairs at brace-depth 1. Multi-line
    `notes` blocks (which contain * bullets) are captured up to the next
    top-level pipe or the closing }}.
    """
    if not wikitext: return {}
    # Find the start of an Infobox-style template at depth 0.
    m = re.search(
        r'\{\{\s*(?:Job\s*Ability|Weapon\s*Skill|Standard\s*WS|Skillchain\s*WS|Infobox[^|}\n]*)\b',
        wikitext, re.IGNORECASE)
    if not m: return {}
    i = m.end()
    # Walk to matching }} at depth 1 -> 0 to find the template body.
    depth = 1
    body_start = i
    while i < len(wikitext) and depth > 0:
        c = wikitext[i]
        if c == '{' and wikitext[i:i+2] == '{{':
            depth += 1; i += 2
        elif c == '}' and wikitext[i:i+2] == '}}':
            depth -= 1
            if depth == 0: break
            i += 2
        else:
            i += 1
    body = wikitext[body_start:i]

    # Now split body on pipes that are at depth 0 of nested templates,
    # so values can span multiple lines (notes block, equipment list).
    parts = []
    depth = 0
    cur = []
    for ch in body:
        if ch == '{' or ch == '[':
            depth += 1; cur.append(ch)
        elif ch == '}' or ch == ']':
            depth = max(0, depth - 1); cur.append(ch)
        elif ch == '|' and depth == 0:
            parts.append(''.join(cur)); cur = []
        else:
            cur.append(ch)
    parts.append(''.join(cur))

    out = {}
    for p in parts:
        p = p.strip()
        if not p or '=' not in p: continue
        k, _, v = p.partition('=')
        k = k.strip().lower()
        v = _clean_wiki(v)
        nk = INFOBOX_KEYS.get(k)
        if not nk or not v: continue
        if nk == 'notes_raw':
            notes = []
            for ln in v.split('\n'):
                ln = ln.strip()
                if ln.startswith('*'): ln = ln[1:].strip()
                if ln: notes.append(ln)
            if notes: out['notes'] = notes
        else:
            out[nk] = v.split('\n')[0].strip()  # 1-line fields keep only first line
    return out

def load_name_list(json_path):
    """Pull canonical 'en' names from one of the existing data JSONs."""
    with open(json_path, 'r', encoding='utf-8') as f:
        d = json.load(f)
    return [v.get('en') for v in d.values() if v.get('en')]

def merge_into(out, names, extracts, wikitexts, kind):
    for name in names:
        info = {'kind': kind, 'name': name}
        ex = extracts.get(name)
        if ex: info['description'] = ex
        wt = wikitexts.get(name)
        if wt:
            ib = parse_infobox(wt)
            for k, v in ib.items():
                info.setdefault(k, v)
        if len(info) <= 2:  # only kind+name, nothing useful scraped
            continue
        out[name.lower()] = info

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--no-cache', action='store_true', help='wipe cache first')
    args = ap.parse_args()

    if args.no_cache and os.path.exists(CACHE):
        import shutil; shutil.rmtree(CACHE)

    ja_names = load_name_list(os.path.join(DATA, 'job_abilities.json'))
    ws_names = load_name_list(os.path.join(DATA, 'weapon_skills.json'))
    # De-dupe while preserving order.
    ja_names = list(dict.fromkeys(ja_names))
    ws_names = list(dict.fromkeys(ws_names))
    print(f'JAs to fetch: {len(ja_names)}')
    print(f'WSs to fetch: {len(ws_names)}')

    print('Fetching JA extracts...')
    ja_ex = cached_extracts(ja_names, 'ja')
    print('Fetching JA wikitext...')
    ja_wt = cached_wikitext(ja_names, 'ja')
    print('Fetching WS extracts...')
    ws_ex = cached_extracts(ws_names, 'ws')
    print('Fetching WS wikitext...')
    ws_wt = cached_wikitext(ws_names, 'ws')

    # Merge into existing descriptions.json so we don't blow away spells.
    out = {}
    if os.path.exists(OUT):
        with open(OUT, 'r', encoding='utf-8') as f:
            out = json.load(f)
    # Drop existing JA/WS entries so we replace them cleanly.
    out = {k: v for k, v in out.items() if v.get('kind') == 'spell'}
    print(f'Kept {len(out)} spell entries')

    merge_into(out, ja_names, ja_ex, ja_wt, 'ja')
    merge_into(out, ws_names, ws_ex, ws_wt, 'ws')

    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, sort_keys=True, ensure_ascii=False)
    print(f'Wrote {len(out)} total entries to {OUT}')

if __name__ == '__main__':
    main()
