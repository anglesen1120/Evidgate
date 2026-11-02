#!/usr/bin/env bash
# shellcheck disable=SC2016 # $review_health_allowed is an nginx variable, matched literally
# Guards the one nginx phase trap this configuration can still fall into.
#
# `return` runs in the rewrite phase, before nginx evaluates the access phase, so
# `allow`/`deny` (and `auth_basic`) inside a location that returns are dead
# config: the location answers everyone. The health probe is exactly that shape,
# so its loopback-only rule is written as a rewrite-phase map guard instead. This
# script fails when the directives come back, or when the guard disappears.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONF="$SCRIPT_DIR/nginx/nginx.conf"

[ -r "$CONF" ] || {
  echo "check-nginx-phases.sh: cannot read $CONF" >&2
  exit 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail=0
note() {
  echo "check-nginx-phases.sh: $*" >&2
  fail=1
}

printf '== nginx directive phases\n'

# Comments carry prose about the trap, so they have to be dropped before the
# directive scan or the explanation trips the check it explains.
directives="$(sed -E 's/(^|[[:space:]])#.*$//' "$CONF")"

# An address or auth directive in this file is always one location away from a
# `return`, which makes it a rule nobody enforces.
if grep -nE '^[[:space:]]*(allow|deny|satisfy)[[:space:]]' <<<"$directives" >/dev/null; then
  grep -nE '^[[:space:]]*(allow|deny|satisfy)[[:space:]]' <<<"$directives" >&2
  note 'allow/deny/satisfy never execute in a location that returns; gate the
      condition in the rewrite phase (map + if), as /nginx-health does'
fi

# The map that replaces allow/deny must name both loopback forms: a container
# that ever listens on [::] would otherwise answer 403 to its own healthcheck.
mapblock="$(awk '/^[[:space:]]*map \$remote_addr \$review_health_allowed[[:space:]]*\{/,/^[[:space:]]*\}/' "$CONF")"
if [ -z "$mapblock" ]; then
  note 'map $remote_addr $review_health_allowed is missing: /nginx-health has no
      working address check'
else
  grep -qE "\b127\.0\.0\.1\b" <<<"$mapblock" || note 'the health map does not allow 127.0.0.1'
  grep -qE "::1" <<<"$mapblock" || note 'the health map does not allow ::1'
  grep -qE '^[[:space:]]*default[[:space:]]+0[[:space:]]*;' <<<"$mapblock" ||
    note 'the health map default must be 0 (deny), not allow'
fi

# The guard has to come before the 200, in the same phase, and the location must
# still turn auth off: the probe cannot present a password.
health="$(awk '/location = \/nginx-health[[:space:]]*\{/,/^[[:space:]]{4}\}/' "$CONF")"
if [ -z "$health" ]; then
  note 'location = /nginx-health is missing'
else
  grep -qE '^[[:space:]]*if \(\$review_health_allowed = 0\)' <<<"$health" ||
    note 'the health location lost its $review_health_allowed guard'
  grep -qE '^[[:space:]]*return 403;' <<<"$health" ||
    note 'the health location no longer refuses a non-loopback caller'
  grep -qE '^[[:space:]]*auth_basic off;' <<<"$health" ||
    note 'the health location must keep auth_basic off'
  # The refusal must be reachable at all: a guard after the 200 is dead code.
  guard_line="$(grep -nE 'if \(\$review_health_allowed = 0\)' <<<"$health" | cut -d: -f1 || true)"
  ok_line="$(grep -nE "return 200" <<<"$health" | cut -d: -f1 || true)"
  if [ -n "$guard_line" ] && [ -n "$ok_line" ] && [ "$guard_line" -gt "$ok_line" ]; then
    note 'the health guard sits after the 200, so the 200 always wins'
  fi
fi

if [ "$fail" -eq 0 ]; then
  printf '== ok: the health probe is loopback-gated in the rewrite phase\n'
fi

# A 2xx/3xx `return` outside a named location answers before the access phase,
# so basic auth never runs for that path. Content is not leaked (it is a status
# line plus headers), but it contradicts the rule that a stranger sees 401, and
# it is the same trap as the health probe. Named locations are exempt: they are
# reached through a precontent try_files, i.e. after auth.
awk '
  /^[[:space:]]*location[[:space:]]/ { blk = $0; inb = 1; next }
  inb {
    blk = blk "\n" $0
    if ($0 ~ /^    \}[[:space:]]*$/) { print blk "\n@@END@@"; inb = 0 }
  }
' "$CONF" >"$work/blocks"

block=""
while IFS= read -r line; do
  if [ "$line" = '@@END@@' ]; then
    case "$block" in
      *'location @'*) : ;;
      *'return 2'* | *'return 3'*)
        if ! grep -q 'review_health_allowed' <<<"$block"; then
          note "a 3xx/2xx return inside a non-named location answers before
      basic auth: $(grep -m1 'location' <<<"$block" | sed 's/^[[:space:]]*//')
      move the return into a named location reached by try_files"
        fi
        ;;
    esac
    block=""
    continue
  fi
  block="$block$line"$'\n'
done <"$work/blocks"

if [ "$fail" -eq 0 ]; then
  printf '== ok: no redirect answers before the access phase\n'
fi
exit "$fail"
