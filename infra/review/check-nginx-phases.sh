#!/usr/bin/env bash
# shellcheck disable=SC2016 # $review_health_allowed is an nginx variable, matched literally
# Guards the nginx phase trap this configuration can still fall into, and the two
# proxy properties of /v1/ that a parse cannot show (overwritten client address,
# streamed request body) plus the conditional Authorization strip.
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

# The /v1/ proxy carries properties no `nginx -t` can show: the client address is
# overwritten rather than appended, a body is streamed instead of spooled to the
# tmpfs, the response is not buffered, the body cap is still nginx's, and the
# Authorization header is emptied only when the start script says basic auth is on.
# The block keeps its comments, which explain the traps, so they are stripped
# before matching: a commented-out directive must not satisfy any check here.
v1="$(awk '/location \/v1\/[[:space:]]*\{/,/^[[:space:]]{4}\}/' "$CONF")"
if [ -z "$v1" ]; then
  note 'location /v1/ is missing: the bundle has no Api proxy'
else
  v1_active="$(sed -E 's/(^|[[:space:]])#.*$//' <<<"$v1")"
  grep -qE '^[[:space:]]*proxy_set_header X-Forwarded-For \$remote_addr;' <<<"$v1_active" ||
    note 'location /v1/ must overwrite X-Forwarded-For with $remote_addr: an
      appended header lets a caller choose the address the Api sees'
  grep -qE '^[[:space:]]*proxy_request_buffering off;' <<<"$v1_active" ||
    note 'location /v1/ buffers request bodies to the tmpfs before the Api sees
      them; an upload then needs the body twice and starts late'
  grep -qE '^[[:space:]]*proxy_buffering off;' <<<"$v1_active" ||
    note 'location /v1/ buffers the response, so a download waits for its first
      disk write and the tmpfs holds a second copy'
  grep -qE '^[[:space:]]*include /tmp/evidgate/proxy-auth\.conf;' <<<"$v1_active" ||
    note 'location /v1/ no longer includes the start script proxy-auth.conf, so
      the Authorization header can no longer be emptied when basic auth is on'
fi

# A zero cap leaves only Kestrel's limit, and the Api's 413 then arrives after the
# proxy has accepted a body it was supposed to refuse.
grep -qE '^[[:space:]]*client_max_body_size[[:space:]]+32m;' <<<"$directives" ||
  note 'client_max_body_size 32m is missing from the http block: the proxy stops
    being the smaller of the two limits and an oversized upload is proxied'

# The strip is conditional by design: written here it would also empty the header
# in a deployment without basic auth, where it is the client's bearer token. Header
# names are case-insensitive in nginx, so the match is too.
if grep -qiE '^[[:space:]]*proxy_set_header[[:space:]]+authorization' <<<"$directives"; then
  note 'nginx.conf sets proxy_set_header Authorization directly; it belongs in the
      auth-on file the start script writes, or bearer tokens are lost when auth is off'
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
  printf '== ok: directives run in the phases the design needs, and the /v1/ proxy streams, overwrites the client address and strips only a Basic credential\n'
fi
exit "$fail"
