#!/usr/bin/env bash
# The host is stopped every evening and started every morning, so the stack has to
# come back on its own. That is a property of this file: every long-running
# service must be `unless-stopped`, and no one-shot may carry a restart policy
# (a restarted migration, seed or bucket bootstrap would act on a live database).
# `depends_on` orders a `up`, never a boot, which is why the policy is the only
# thing that can bring the stack back unasked.
set -euo pipefail

# shellcheck source=infra/review/lib-check.sh
. "$(cd "$(dirname "$0")" && pwd)/lib-check.sh"
COMPOSE="$REVIEW_COMPOSE"

[ -r "$COMPOSE" ] || {
  echo "check-restart-policy.sh: cannot read $COMPOSE" >&2
  exit 1
}

# Services that must survive a boot, the jobs that must not be restarted, and the
# build-only images that are never started at all (`images` profile): a restart
# policy on one of those would only ever be a mistake.
LONG_RUNNING="postgres seaweedfs api api-debug worker web web-local cloudflared cloudflared-quick"
ONE_SHOT="db-init migrator seed bucket-init probe"
BUILD_ONLY="migrator-base api-base worker-base"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fail=0
note() {
  echo "check-restart-policy.sh: $*" >&2
  fail=1
}

printf '== restart policy per service\n'

review_split_services "$work"

[ -s "$work/.names" ] || {
  echo "check-restart-policy.sh: no services found in $COMPOSE" >&2
  exit 1
}

# The web services take their policy from the shared anchor, so read it from there.
web_anchor_restart="$(sed -nE '/^x-review-web: &review-web[[:space:]]*$/,/^[a-z]/p' "$COMPOSE" |
  sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' | head -1)"
build_anchor_restart="$(sed -nE '/^x-review-build: &review-build[[:space:]]*$/,/^[a-z]/p' "$COMPOSE" |
  sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' | head -1)"
[ -z "$build_anchor_restart" ] || note "x-review-build (the one-shots) carries restart: $build_anchor_restart"

policy_of() { # SERVICE
  local body="$work/$1" explicit
  explicit="$(sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' "$body" | head -1)"
  if [ -n "$explicit" ]; then
    printf '%s' "$explicit"
    return 0
  fi
  case "$(cat "$body")" in
    *'*review-web'*)
      case "$web_anchor_restart" in
        unless-stopped) printf 'unless-stopped' ;;
        '') printf 'none' ;;
        *) printf '%s' "$web_anchor_restart" ;;
      esac
      return 0
      ;;
  esac
  printf 'none'
}

for svc in $LONG_RUNNING; do
  [ -f "$work/$svc" ] || {
    note "long-running service '$svc' is not in $COMPOSE; update this list"
    continue
  }
  policy="$(policy_of "$svc")"
  [ "$policy" = unless-stopped ] ||
    note "'$svc' runs across a reboot: it needs restart: unless-stopped (found '$policy')"
done

for svc in $ONE_SHOT; do
  [ -f "$work/$svc" ] || {
    note "one-shot service '$svc' is not in $COMPOSE; update this list"
    continue
  }
  policy="$(policy_of "$svc")"
  [ "$policy" = none ] ||
    note "'$svc' is a one-shot: it must carry no restart policy (found '$policy')"
done

for svc in $BUILD_ONLY; do
  [ -f "$work/$svc" ] || {
    note "build-only service '$svc' is not in $COMPOSE; update this list"
    continue
  }
  policy="$(policy_of "$svc")"
  [ "$policy" = none ] ||
    note "'$svc' is only built, never run: it must carry no restart policy (found '$policy')"
done

# A service nobody classified would silently keep Compose's default of "no".
while IFS= read -r svc; do
  case " $LONG_RUNNING $ONE_SHOT $BUILD_ONLY " in
    *" $svc "*) ;;
    *) note "service '$svc' is neither in the long-running, the one-shot nor the build-only list" ;;
  esac
done <"$work/.names"

if [ "$fail" -eq 0 ]; then
  printf '== ok: long-running services restart after a boot, one-shots never do\n'
fi
exit "$fail"
