#!/usr/bin/env bash
# Shared helpers for the review-infra checks (check.sh runs them all).
#
# The point of these helpers is that every check renders the compose file with a
# SENTINEL environment built from env.review.template: the checks never read the
# host's real .env.review, so none of them can print a review credential.
set -uo pipefail

REVIEW_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REVIEW_COMPOSE="$REVIEW_DIR/compose.review.yml"
# The quick tunnel is always rendered with its override, because the pair is what
# a deploy runs: profile tunnel-quick plus compose.quick.yml.
REVIEW_QUICK_COMPOSE="$REVIEW_DIR/compose.quick.yml"
REVIEW_TEMPLATE="$REVIEW_DIR/env.review.template"
REVIEW_PROJECT=evidgate-review

review_die() {
  printf '%s\n' "$*" >&2
  exit 1
}

review_step() {
  printf '== %s\n' "$*"
}

review_require() {
  command -v "$1" >/dev/null 2>&1 ||
    review_die "$2: '$1' is required on this host (it runs here, not in a container); install it and re-run"
}

# Writes the template with every __NAME__ placeholder replaced by SENTINEL-NAME.
# One sentinel per key, so a leak names the key it came from. Empty values (the
# tunnel token, the public origin) stay empty: they are optional by design.
review_write_sentinel_env() {
  local target="$1"
  python3 - "$REVIEW_TEMPLATE" "$target" <<'PY'
import re
import sys

source, target = sys.argv[1], sys.argv[2]
placeholder = re.compile(r'__([A-Z0-9_]+)__')
rendered = []
count = 0
for line in open(source, encoding='utf-8').read().splitlines(keepends=True):
    key, separator, value = line.partition('=')
    # Only the value is substituted: key names carry double underscores of their
    # own (EVIDGATE__CI__PEPPER), and matching those would corrupt the file.
    value, replaced = placeholder.subn(lambda match: f'SENTINEL-{match.group(1)}', value)
    count += replaced
    rendered.append(key + separator + value)
if count == 0:
    sys.exit('no __NAME__ placeholder found in the template')
open(target, 'w', encoding='utf-8').write(''.join(rendered))
PY
}

# review_render <sentinel-env> <output.json> [profiles...]
# Interpolated render: compose resolves every ${...}, so this output carries the
# sentinel values. It is used for structure only (which service publishes what),
# never quoted anywhere.
review_render() {
  local env_file="$1" output="$2"
  shift 2
  docker compose --project-name "$REVIEW_PROJECT" --env-file "$env_file" \
    -f "$REVIEW_COMPOSE" "$@" config --format json >"$output"
}

# review_render_redacted <sentinel-env> <output.json> [profiles...]
# The pasteable render: compose does not interpolate, so the output carries the
# file's own ${...} references and no value at all.
review_render_redacted() {
  local env_file="$1" output="$2"
  shift 2
  docker compose --project-name "$REVIEW_PROJECT" --env-file "$env_file" \
    -f "$REVIEW_COMPOSE" "$@" config --no-interpolate --format json >"$output"
}

# review_render_quick <sentinel-env> <output.json> [profiles...]
# The quick-tunnel deployment exactly as deploy.sh runs it: base file plus
# compose.quick.yml, so the render proves the override actually applies.
review_render_quick() {
  local env_file="$1" output="$2"
  shift 2
  docker compose --project-name "$REVIEW_PROJECT" --env-file "$env_file" \
    -f "$REVIEW_COMPOSE" -f "$REVIEW_QUICK_COMPOSE" "$@" config --format json >"$output"
}

# review_require_no_interpolate <check name>
review_require_no_interpolate() {
  docker compose config --help 2>/dev/null | grep -q -- '--no-interpolate' ||
    review_die "$1: this docker compose has no 'config --no-interpolate'; a redacted render is not possible, upgrade compose v2"
}
