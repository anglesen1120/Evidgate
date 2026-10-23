#!/usr/bin/env bash
# AC6 SHA half (01.7.1-T4.1): every GitHub Action a workflow uses must be pinned
# to a full 40-hex commit SHA — a tag is mutable, so `@v5` is a supply-chain
# hole — and must carry the release tag in a `# vX.Y.Z` comment so the pin stays
# human-auditable. Local actions (`uses: ./…`) need no pin.
# Exit 0: every remote action is SHA-pinned. Exit 1: lists the offending lines.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

shopt -s nullglob
workflows=(.github/workflows/*.yml .github/workflows/*.yaml)

if [[ "${#workflows[@]}" -eq 0 ]]; then
  echo "check-action-pins: no workflow files under .github/workflows/" >&2
  exit 1
fi
# Structural guard before the line-anchored scan: both this gate and the parity
# gate match `uses:`/`run:` only as a first key in column-anchored YAML, so
# equivalent-but-invisible spellings — flow-style steps (`- {uses: x@v7}`) and
# quoted keys (`"uses":`) — would slip through silently. Reject them outright.
python3 - check-action-pins "${workflows[@]}" <<'PYEOF' || exit 1
import re
import sys

label, files = sys.argv[1], sys.argv[2:]
quoted = re.compile(r'["\'](?:uses|run)["\']\s*:')
flow = re.compile(r"\{[^}\n]*\b(?:uses|run)\s*:")
bad = 0
for path in files:
    with open(path, encoding="utf-8") as fh:
        for n, line in enumerate(fh, 1):
            if quoted.search(line) or flow.search(line):
                print("%s: %s:%d: quoted/flow-style 'uses'/'run' key bypasses the line-anchored scan — rewrite as a block-style step"
                      % (label, path, n), file=sys.stderr)
                bad = 1
sys.exit(bad)
PYEOF

fail=0
count=0

for wf in "${workflows[@]}"; do
  while IFS= read -r entry; do
    lineno="${entry%%:*}"
    line="${entry#*:}"
    # quoted scalars still expose uses: the value keeps its quotes, strip them.
    rest="$(printf '%s\n' "$line" | sed -E 's/^[[:space:]]*(-[[:space:]]+)?uses:[[:space:]]*//')"
    target="${rest%%#*}"
    comment=""
    [[ "$rest" == *"#"* ]] && comment="${rest#*#}"
    target="$(printf '%s' "$target" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//; s/^"(.*)"$/\1/; s/^'"'"'(.*)'"'"'$/\1/')"

    # local actions are pinned by the tree itself
    case "$target" in
    ./* | ../* | .) continue ;;
    esac

    if [[ "$target" != *@* ]]; then
      echo "check-action-pins: $wf:$lineno: '$target' has no ref — pin a commit SHA" >&2
      fail=1
      continue
    fi
    ref="${target##*@}"
    if ! printf '%s' "$ref" | grep -qE '^[0-9a-f]{40}$'; then
      echo "check-action-pins: $wf:$lineno: ref '$ref' in '$target' is not a 40-hex commit SHA" >&2
      fail=1
      continue
    fi
    if [[ -z "${comment//[[:space:]]/}" ]]; then
      echo "check-action-pins: $wf:$lineno: '$target' is SHA-pinned but missing the '# vX.Y.Z' version comment" >&2
      fail=1
      continue
    fi
    count=$((count + 1))
  done < <(grep -nE '^[[:space:]]*(-[[:space:]]+)?uses:' "$wf" || true)
done

if [[ "$fail" -eq 0 ]]; then
  echo "check-action-pins: all $count 'uses:' refs are full commit SHAs"
fi
exit "$fail"
