#!/usr/bin/env bash
# The host is stopped every evening and started every morning, so the stack has to
# come back on its own. That is a property of this file: every long-running
# service must be `unless-stopped`, no one-shot may carry a restart policy (a
# restarted migration, seed or bucket bootstrap would act on a live database), and
# the two loopback debug replicas must be explicitly `no` — they are a person's
# `ssh -L` or smoke target, not part of the served stack.
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

# Services that must survive a boot, the jobs that must not be restarted, the
# build-only images that are never started at all (`images` profile), and the
# local-only replicas: a restart policy on one of those would only be a mistake.
LONG_RUNNING="postgres seaweedfs api worker web cloudflared cloudflared-quick"
ONE_SHOT="db-init migrator seed bucket-init probe"
BUILD_ONLY="migrator-base api-base worker-base"
LOCAL_ONLY="api-debug web-local"

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

# `web` takes its policy from the shared anchor, so read it from there; web-local
# overrides it with `no` and is checked as a local-only service.
web_anchor_restart="$(sed -nE '/^x-review-web: &review-web[[:space:]]*$/,/^[a-z]/p' "$COMPOSE" |
  sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' | head -1)"
build_anchor_restart="$(sed -nE '/^x-review-build: &review-build[[:space:]]*$/,/^[a-z]/p' "$COMPOSE" |
  sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' | head -1)"
[ -z "$build_anchor_restart" ] || note "x-review-build (the one-shots) carries restart: $build_anchor_restart"

policy_of() { # SERVICE
  local body="$work/$1" explicit
  # `restart: 'no'` is quoted in the file because bare `no` reads as a boolean.
  explicit="$(sed -nE 's/^[[:space:]]+restart:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' "$body" |
    head -1 | tr -d "'\"")"
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

for svc in $LOCAL_ONLY; do
  [ -f "$work/$svc" ] || {
    note "local-only service '$svc' is not in $COMPOSE; update this list"
    continue
  }
  policy="$(policy_of "$svc")"
  [ "$policy" = no ] ||
    note "'$svc' is a local-only replica: it needs restart: 'no' (found '$policy')"
done

# A service nobody classified would silently keep Compose's default of "no".
while IFS= read -r svc; do
  case " $LONG_RUNNING $ONE_SHOT $BUILD_ONLY $LOCAL_ONLY " in
    *" $svc "*) ;;
    *) note "service '$svc' is in none of the long-running, one-shot, build-only or local-only lists" ;;
  esac
done <"$work/.names"

if [ "$fail" -eq 0 ]; then
  printf '== ok: long-running services restart after a boot, one-shots and local replicas never do\n'
fi
exit "$fail"
