#!/usr/bin/env bash
# Convert Windower's spells.lua / job_abilities.lua / auto_translates.lua
# into JSON files the .exe loads at runtime. Run once from a Bash shell
# (Git Bash on Windows) when you upgrade Windower's resource data.
#
# Usage:
#   ./tools/generate-data.sh "<path to Windower res folder>"
#
# Example:
#   ./tools/generate-data.sh "D:/FFXI/Windower/res"

set -euo pipefail

if [ $# -lt 1 ]; then
    RES="D:/FFXI/Windower/res"
else
    RES="$1"
fi

if [ ! -f "$RES/spells.lua" ]; then
    echo "spells.lua not found at $RES" >&2
    exit 1
fi

OUT="$(cd "$(dirname "$0")/.." && pwd)/data"
mkdir -p "$OUT"

echo "Reading $RES, writing $OUT ..."

# ---------------------------------------------------------------------------
# spells.json — fields: id, en, prefix, type, unlearnable, levels{job_id:lv}
# ---------------------------------------------------------------------------
awk '
function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t]+$/, "", s); return s }
function jescape(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }

/^[[:space:]]*\[[0-9]+\][[:space:]]*=[[:space:]]*\{/ {
    line = $0
    # strip trailing },
    sub(/^[[:space:]]+/, "", line)

    # id
    match(line, /\[([0-9]+)\][[:space:]]*=/, a); id = a[1]

    # en="..."
    en = ""
    if (match(line, /en="([^"]*)"/, a)) en = a[1]

    # prefix="..."
    pre = ""
    if (match(line, /prefix="([^"]*)"/, a)) pre = a[1]

    # type="..."
    ty = ""
    if (match(line, /type="([^"]*)"/, a)) ty = a[1]

    # unlearnable=true
    unl = "false"
    if (line ~ /unlearnable=true/) unl = "true"

    # levels={[job]=lv, ...}
    lv = ""
    if (match(line, /levels=\{([^}]*)\}/, a)) {
        lvbody = a[1]
        n = split(lvbody, parts, ",")
        first = 1
        for (i = 1; i <= n; i++) {
            p = trim(parts[i])
            if (match(p, /\[([0-9]+)\]=([0-9]+)/, b)) {
                if (!first) lv = lv ","
                lv = lv "\"" b[1] "\":" b[2]
                first = 0
            }
        }
    }

    rec[id] = sprintf("\"%s\":{\"id\":%s,\"en\":\"%s\",\"prefix\":\"%s\",\"type\":\"%s\",\"unlearnable\":%s,\"levels\":{%s}}",
                      id, id, jescape(en), jescape(pre), jescape(ty), unl, lv)
    order[++count] = id
}
END {
    printf "{"
    sep = ""
    for (i = 1; i <= count; i++) {
        printf "%s%s", sep, rec[order[i]]
        sep = ","
    }
    printf "}"
}
' "$RES/spells.lua" > "$OUT/spells.json"
echo "  spells.json:        $(wc -c < "$OUT/spells.json") bytes"

# ---------------------------------------------------------------------------
# job_abilities.json
# ---------------------------------------------------------------------------
awk '
function jescape(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }
/^[[:space:]]*\[[0-9]+\][[:space:]]*=[[:space:]]*\{/ {
    line = $0
    match(line, /\[([0-9]+)\][[:space:]]*=/, a); id = a[1]
    en = ""
    if (match(line, /en="([^"]*)"/, a)) en = a[1]
    pre = ""
    if (match(line, /prefix="([^"]*)"/, a)) pre = a[1]
    ty = ""
    if (match(line, /type="([^"]*)"/, a)) ty = a[1]
    rec[id] = sprintf("\"%s\":{\"id\":%s,\"en\":\"%s\",\"prefix\":\"%s\",\"type\":\"%s\"}",
                      id, id, jescape(en), jescape(pre), jescape(ty))
    order[++count] = id
}
END {
    printf "{"
    sep = ""
    for (i = 1; i <= count; i++) {
        printf "%s%s", sep, rec[order[i]]
        sep = ","
    }
    printf "}"
}
' "$RES/job_abilities.lua" > "$OUT/job_abilities.json"
echo "  job_abilities.json: $(wc -c < "$OUT/job_abilities.json") bytes"

# ---------------------------------------------------------------------------
# auto_translates.json — used to decode FD-wrapped codes inside macro lines
# ---------------------------------------------------------------------------
awk '
function jescape(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }
/^[[:space:]]*\[[0-9]+\][[:space:]]*=[[:space:]]*\{/ {
    line = $0
    match(line, /\[([0-9]+)\][[:space:]]*=/, a); id = a[1]
    en = ""
    if (match(line, /en="([^"]*)"/, a)) en = a[1]
    rec[id] = sprintf("\"%s\":\"%s\"", id, jescape(en))
    order[++count] = id
}
END {
    printf "{"
    sep = ""
    seen[""] = 1
    for (i = 1; i <= count; i++) {
        if (seen[order[i]]) continue
        seen[order[i]] = 1
        printf "%s%s", sep, rec[order[i]]
        sep = ","
    }
    printf "}"
}
' "$RES/auto_translates.lua" > "$OUT/auto_translates.json"
echo "  auto_translates.json: $(wc -c < "$OUT/auto_translates.json") bytes"

# ---------------------------------------------------------------------------
# weapon_skills.json — per-job skill_type for filtering (we don't tag by job
# in the schema; the UI provides a search box to narrow down)
# ---------------------------------------------------------------------------
awk '
function jescape(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }
/^[[:space:]]*\[[0-9]+\][[:space:]]*=[[:space:]]*\{/ {
    line = $0
    match(line, /\[([0-9]+)\][[:space:]]*=/, a); id = a[1]
    en = ""
    if (match(line, /en="([^"]*)"/, a)) en = a[1]
    skl = "0"
    if (match(line, /skill=([0-9]+)/, a)) skl = a[1]
    rec[id] = sprintf("\"%s\":{\"id\":%s,\"en\":\"%s\",\"skill\":%s}",
                      id, id, jescape(en), skl)
    order[++count] = id
}
END {
    printf "{"
    sep = ""
    for (i = 1; i <= count; i++) {
        printf "%s%s", sep, rec[order[i]]
        sep = ","
    }
    printf "}"
}
' "$RES/weapon_skills.lua" > "$OUT/weapon_skills.json"
echo "  weapon_skills.json: $(wc -c < "$OUT/weapon_skills.json") bytes"

# ---------------------------------------------------------------------------
# jobs.json — id -> ens (e.g. {"3":"WHM"})
# ---------------------------------------------------------------------------
awk '
function jescape(s) { gsub(/\\/, "\\\\", s); gsub(/"/, "\\\"", s); return s }
/^[[:space:]]*\[[0-9]+\][[:space:]]*=[[:space:]]*\{/ {
    line = $0
    match(line, /\[([0-9]+)\][[:space:]]*=/, a); id = a[1]
    ens = ""
    if (match(line, /ens="([^"]*)"/, a)) ens = a[1]
    en = ""
    if (match(line, /en="([^"]*)"/, a)) en = a[1]
    rec[id] = sprintf("\"%s\":{\"ens\":\"%s\",\"en\":\"%s\"}",
                      id, jescape(ens), jescape(en))
    order[++count] = id
}
END {
    printf "{"
    sep = ""
    for (i = 1; i <= count; i++) {
        printf "%s%s", sep, rec[order[i]]
        sep = ","
    }
    printf "}"
}
' "$RES/jobs.lua" > "$OUT/jobs.json"
echo "  jobs.json:          $(wc -c < "$OUT/jobs.json") bytes"

echo "Done."
