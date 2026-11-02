#!/usr/bin/env bash
# Container policy of the review stack: bounded logs on every service, the shared
# hardening anchors intact (no capability, no privilege escalation, read-only web
# rootfs), no service overriding them inline, and a tunnel that shares no network
# with the Api, the database or the object store.
#
# Text-level, like check-restart-policy.sh, so it runs under --static-only where a
# deploy can still be stopped before a build. It is a classification: every
# service must appear in the network table and merge the logging anchor, so a new
# service cannot skip the policy by omission, and every check matches an active
# `<<:` line rather than a comment that mentions one.
set -euo pipefail

# shellcheck source=infra/review/lib-check.sh
. "$(cd "$(dirname "$0")" && pwd)/lib-check.sh"
COMPOSE="$REVIEW_COMPOSE"

[ -r "$COMPOSE" ] || {
  echo "check-container-policy.sh: cannot read $COMPOSE" >&2
  exit 1
}

# Processes that parse untrusted input or publish to the internet.
HARDENED="api worker api-debug cloudflared cloudflared-quick"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fail=0
note() {
  echo "check-container-policy.sh: $*" >&2
  fail=1
}

printf '== container policy\n'

review_split_services "$work"
[ -s "$work/.names" ] || {
  echo "check-container-policy.sh: no services found in $COMPOSE" >&2
  exit 1
}

# The body of a top-level key that carries a YAML anchor, so its settings can be
# asserted once instead of on every service that merges it.
anchor_block() { # KEY LINE, e.g. 'x-review-logging: &review-logging'
  awk -v start="$1" '
    $0 == start { inb = 1; print; next }
    inb && /^[a-z]/ { exit }
    inb { print }
  ' "$COMPOSE"
}

logging="$(anchor_block 'x-review-logging: &review-logging')"
if [ -z "$logging" ]; then
  note 'x-review-logging is missing: container logs are unbounded'
else
  grep -qE '^[[:space:]]+driver:[[:space:]]+json-file[[:space:]]*$' <<<"$logging" ||
    note 'x-review-logging must use the json-file driver, the only one with a size cap here'
  grep -qE "^[[:space:]]+max-size:[[:space:]]*'?10m'?[[:space:]]*\$" <<<"$logging" ||
    note 'x-review-logging must cap each log file at 10m'
  grep -qE "^[[:space:]]+max-file:[[:space:]]*'?3'?[[:space:]]*\$" <<<"$logging" ||
    note 'x-review-logging must keep at most 3 log files'
fi

while IFS= read -r svc; do
  grep -qE '^[[:space:]]*<<:.*\*review-logging' "$work/$svc" ||
    note "'$svc' does not merge x-review-logging: its log can grow until the host disk is full"
done <"$work/.names"

hardening="$(anchor_block 'x-review-hardening: &review-hardening')"
if [ -z "$hardening" ]; then
  note 'x-review-hardening is missing: the exposed containers keep the daemon defaults'
else
  grep -qE '^[[:space:]]*-[[:space:]]*no-new-privileges:true[[:space:]]*$' <<<"$hardening" ||
    note 'x-review-hardening must set no-new-privileges:true'
  grep -qE '^[[:space:]]*-[[:space:]]*ALL[[:space:]]*$' <<<"$hardening" ||
    note 'x-review-hardening must drop every capability'
fi

for svc in $HARDENED; do
  if [ ! -f "$work/$svc" ]; then
    note "hardened service '$svc' is not in $COMPOSE; update this list"
    continue
  fi
  grep -qE '^[[:space:]]*<<:.*\*review-hardening' "$work/$svc" ||
    note "'$svc' does not merge x-review-hardening: it runs with the daemon default capabilities"
  if grep -qE '^[[:space:]]*cap_add:' "$work/$svc"; then
    note "'$svc' adds a capability; nothing in this stack needs one"
  fi
done

# The nginx services keep their hardening in their own anchor (they also set a
# read-only rootfs and a tmpfs), so that anchor is what the gate has to assert.
web_anchor="$(anchor_block 'x-review-web: &review-web')"
if [ -z "$web_anchor" ]; then
  note 'x-review-web is missing: the nginx services lose their runtime hardening'
else
  grep -qE '^[[:space:]]+read_only:[[:space:]]*true[[:space:]]*$' <<<"$web_anchor" ||
    note 'x-review-web must set read_only: true, so nginx cannot rewrite its own tree'
  grep -qE '^[[:space:]]*-[[:space:]]*no-new-privileges:true[[:space:]]*$' <<<"$web_anchor" ||
    note 'x-review-web must set no-new-privileges:true'
  grep -qE '^[[:space:]]*-[[:space:]]*ALL[[:space:]]*$' <<<"$web_anchor" ||
    note 'x-review-web must drop every capability'
fi

for svc in web web-local; do
  if [ ! -f "$work/$svc" ]; then
    note "service '$svc' is not in $COMPOSE; update this list"
    continue
  fi
  grep -qE '^[[:space:]]*<<:.*\*review-web' "$work/$svc" ||
    note "'$svc' does not merge x-review-web: it would run without the shared hardening"
done

# An inline copy wins over a merged one, so the keys must live in an anchor only.
while IFS= read -r svc; do
  for key in logging security_opt cap_drop read_only; do
    if grep -qE "^[[:space:]]+${key}:" "$work/$svc"; then
      note "'$svc' sets $key itself instead of inheriting it: an override here is invisible to the service that relies on the anchor"
    fi
  done
done <"$work/.names"

# Membership as written in the service body. The web anchor no longer declares
# `networks:`, so a service without the key falls through to the default bridge
# and is reported instead of being assumed correct.
networks_of() { # SERVICE
  local body="$work/$1" members
  members="$(sed -nE 's/^[[:space:]]+networks:[[:space:]]*\[(.*)\][[:space:]]*$/\1/p' "$body" | head -1)"
  [ -n "$members" ] || return 0
  printf '%s\n' "$members" | tr ',' '\n' |
    sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' | grep -v '^$' || true
}

expect_networks() { # SERVICE NETWORK...
  local svc="$1" expected actual
  shift
  if [ ! -f "$work/$svc" ]; then
    note "service '$svc' is not in $COMPOSE; update the network table"
    return 0
  fi
  actual="$(networks_of "$svc")"
  if [ -z "$actual" ]; then
    note "'$svc' declares no networks: it would join the default bridge"
    return 0
  fi
  expected="$(printf '%s\n' "$@" | sort | tr '\n' ' ')"
  if [ "$(printf '%s\n' "$actual" | sort | tr '\n' ' ')" != "$expected" ]; then
    note "'$svc' joins $(printf '%s\n' "$actual" | tr '\n' ' ')—expected $(printf '%s\n' "$@" | tr '\n' ' ')"
  fi
}

NETWORKED="postgres seaweedfs db-init migrator-base migrator seed bucket-init api-base api worker-base worker probe web cloudflared cloudflared-quick web-local api-debug"
for svc in postgres seaweedfs db-init migrator-base migrator seed bucket-init api-base api worker-base worker probe; do
  expect_networks "$svc" net
done
# The tunnel reaches web over `edge` and reaches Cloudflare over `egress`; it must
# not see the Api, postgres or seaweedfs, so it is not on `net` at all.
expect_networks web net edge
expect_networks cloudflared edge egress
expect_networks cloudflared-quick edge egress
# A published loopback port needs NAT, which an internal network cannot provide.
expect_networks web-local net loopback
expect_networks api-debug net loopback

while IFS= read -r svc; do
  case " $NETWORKED " in
    *" $svc "*) ;;
    *) note "service '$svc' has no network expectation; add it to the table" ;;
  esac
done <"$work/.names"

network_block() { # NAME
  awk -v name="  $1:" '
    $0 == name { inb = 1; print; next }
    inb && /^  [a-z]/ { exit }
    inb { print }
  ' "$COMPOSE"
}

for name in net edge; do
  block="$(network_block "$name")"
  if [ -z "$block" ]; then
    note "network '$name' is missing from $COMPOSE"
    continue
  fi
  grep -qE '^[[:space:]]+internal:[[:space:]]*true[[:space:]]*$' <<<"$block" ||
    note "network '$name' must be internal: a container on it would otherwise reach the host network"
done

if [ "$fail" -eq 0 ]; then
  printf '== ok: every service bounds its logs, the exposed containers drop capabilities, and the tunnel shares only edge with web\n'
fi
exit "$fail"
