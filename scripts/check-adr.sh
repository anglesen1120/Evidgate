#!/usr/bin/env bash
# check-adr.sh — enforce ADR completeness (plan 01.9.1, AC1).
#
# Every docs/adr/*.md file other than README.md and 0000-template.md must be an
# ADR file named NNNN-slug.md. For each ADR:
#   a. the file name must appear as a link in README.md's index table;
#   b. all eight required "## <Section>" headings must be present;
#   c. the first non-empty line of "## Status" must be
#      proposed|accepted|deprecated|superseded-by NNNN (optional "(…)" suffix);
#   d. "## Date" must contain a YYYY-MM-DD date.
# Also: every index-table row must point at an existing file.
#
# Usage: scripts/check-adr.sh [ADR_DIR]   (default: <repo>/docs/adr)
# An explicit ADR_DIR is resolved against the current working directory.
# Prints "E <file>: <problem>" per failure; exits 1 if any, else
# prints "check-adr: N ADRs ok" and exits 0.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ $# -ge 1 ]; then
    dir="$1"
else
    dir="$ROOT/docs/adr"
fi
readme="$dir/README.md"
errors=0
count=0

err() {
    printf 'E %s: %s\n' "$1" "$2"
    errors=$((errors + 1))
}

# Print the body of "## <section>" (lines up to the next "## " heading).
# "###" subsections do NOT terminate the slice: '^## ' needs a space at col 3.
section_body() {
    awk -v sec="## $2" '
        $0 == sec { in_sec = 1; next }
        /^## / { in_sec = 0 }
        in_sec { print }
    ' "$1"
}

has_section() {
    grep -qE "^## $2[[:space:]]*$" "$1"
}

# Emit the raw link targets of README's index-table rows, one per line
# (read line-by-line downstream so file names with spaces survive).
index_links() {
    grep -E '^\|' "$readme" | grep -oE '\]\([^)]+\)' | sed 's/^](//; s/)$//' || true
}

# Normalise an index link target: strip a leading "./" and any "#fragment".
normalise_link() {
    local t="$1"
    t="${t%%#*}"
    t="${t#./}"
    printf '%s\n' "$t"
}

if [ ! -d "$dir" ]; then
    printf 'E %s: directory not found\n' "$dir"
    exit 1
fi

if [ ! -f "$readme" ]; then
    printf 'E %s: ADR index not found\n' "$readme"
    exit 1
fi

for file in "$dir"/*.md; do
    [ -e "$file" ] || continue          # empty glob stays literal
    base="$(basename "$file")"
    case "$base" in
        README.md|0000-template.md) continue ;;
    esac
    if ! [[ "$base" =~ ^[0-9]{4}-.+\.md$ ]]; then
        err "$file" "not an ADR file name (NNNN-slug.md)"
        continue
    fi
    count=$((count + 1))

    # (a) file must appear as a link in the README index (normalised targets)
    linked=0
    while IFS= read -r target; do
        [ "$(normalise_link "$target")" = "$base" ] && linked=1 && break
    done < <(index_links)
    if [ "$linked" -eq 0 ]; then
        err "$base" "not linked in index table"
    fi

    # (b) required sections (listed one per line; section names contain spaces)
    while IFS= read -r sec; do
        if ! has_section "$file" "$sec"; then
            err "$base" "missing section: $sec"
        fi
    done <<'SECTIONS'
Status
Date
Context
Decision
Alternatives rejected
Consequences
Evidence
Revisit trigger
SECTIONS

    # (c) status line (only if the heading exists — absence is already an error)
    if has_section "$file" "Status"; then
        status_line="$(section_body "$file" "Status" \
            | grep -E '[^[:space:]]' | head -n 1 || true)"
        status_line="${status_line#"${status_line%%[![:space:]]*}"}"   # ltrim
        status_line="${status_line%"${status_line##*[![:space:]]}"}"   # rtrim
        if ! printf '%s\n' "$status_line" \
            | grep -qE '^(proposed|accepted|deprecated|superseded-by [0-9]{4})([[:space:]]*\(.*\))?$'; then
            err "$base" "bad status line: '${status_line:-<empty>}' (expected proposed|accepted|deprecated|superseded-by NNNN)"
        fi
    fi

    # (d) date
    if has_section "$file" "Date"; then
        if ! section_body "$file" "Date" \
            | grep -qE '[0-9]{4}-[0-9]{2}-[0-9]{2}'; then
            err "$base" "missing YYYY-MM-DD in Date section"
        fi
    fi
done

# Every index-table row must point at an existing file (relative links only).
while IFS= read -r target; do
    [ -n "$target" ] || continue
    case "$target" in
        http*|//*|'#'*) continue ;;
    esac
    target="$(normalise_link "$target")"
    [ -n "$target" ] || continue
    if [ ! -f "$dir/$target" ]; then
        err "$target" "index row has no file"
    fi
done < <(index_links)

if [ "$errors" -gt 0 ]; then
    exit 1
fi
printf 'check-adr: %d ADRs ok\n' "$count"
exit 0
