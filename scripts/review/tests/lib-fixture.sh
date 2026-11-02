#!/usr/bin/env bash
# Shared fixture for the review-deployment tests. Each test gets a throwaway git
# repository (the operator's checkout), a fake HOME (the review host's home) and
# stubs for ssh, docker and curl that record every call. `ssh host CMD` runs CMD
# in this shell, so the fake HOME is the "remote" home and the deployed layout is
# exactly the one the real scripts create.
#
# Plain bash, no framework: a failed assertion prints FAIL and exits non-zero.
set -euo pipefail

FX_TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FX_SCRIPTS="$(cd "$FX_TEST_DIR/.." && pwd)"
FX_KEEP="${FX_KEEP:-0}"
SANDBOX=""
FX_REPO=""
# shellcheck disable=SC2034 # read by the test files that source this fixture
FX_FIRST_SHA=""
# shellcheck disable=SC2034 # read by the test files that source this fixture
FX_SECOND_SHA=""

fx_ok() { printf '  ok: %s\n' "$*"; }

fx_fail() {
  printf '  FAIL: %s\n' "$*" >&2
  exit 1
}

fx_assert_eq() { # GOT WANT LABEL
  [ "$1" = "$2" ] || fx_fail "$3: expected '$2', got '$1'"
  fx_ok "$3"
}

fx_assert_contains() { # HAYSTACK NEEDLE LABEL
  case "$1" in
    *"$2"*) fx_ok "$3" ;;
    *) fx_fail "$3: '$2' is not in the output" ;;
  esac
}

fx_assert_not_contains() { # HAYSTACK NEEDLE LABEL
  case "$1" in
    *"$2"*) fx_fail "$3: '$2' must not appear" ;;
    *) fx_ok "$3" ;;
  esac
}

fx_assert_file() { [ -f "$1" ] || fx_fail "missing file $1"; fx_ok "exists $1"; }

fx_assert_absent() { [ ! -e "$1" ] || fx_fail "unexpected $1"; fx_ok "absent $1"; }

fx_assert_log_has() { # LOG NEEDLE LABEL
  grep -qF -- "$2" "$1" || fx_fail "$3: '$2' is not recorded in ${1##*/}"
  fx_ok "$3"
}

fx_assert_log_lacks() { # LOG NEEDLE LABEL
  ! grep -qF -- "$2" "$1" || fx_fail "$3: '$2' must not be recorded in ${1##*/}"
  fx_ok "$3"
}

fx_sha() { # FILE
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

fx_mode() { # FILE — octal permissions, on BSD stat and GNU stat
  if stat -f '%Lp' "$1" >/dev/null 2>&1; then
    stat -f '%Lp' "$1"
  else
    stat -c '%a' "$1"
  fi
}

fx_teardown() {
  if [ "$FX_KEEP" = 1 ]; then
    printf '  kept %s\n' "$SANDBOX"
  else
    rm -rf "$SANDBOX"
  fi
}

fx_write_stubs() {
  local bin="$SANDBOX/bin"

  cat >"$bin/ssh" <<'STUB'
#!/bin/sh
# A port forward has to stay in the foreground so the caller can kill it.
case "$1" in
  -N) exec sleep 300 ;;
esac
host="$1"
shift
if [ "$host" != "review-host" ]; then
  echo "stub ssh: unexpected host '$host'" >&2
  exit 255
fi
printf '%s\n' "$*" >>"$SSH_LOG"
# Host facts are faked so a test can describe the host it deploys to.
case "$*" in
  *"/proc/meminfo"*)
    case "$*" in
      *SwapTotal*) echo "SwapTotal:      2097148 kB" ;;
      *) echo "MemTotal:       4030000 kB" ;;
    esac
    exit 0
    ;;
  "uname -m") echo "aarch64" ; exit 0 ;;
esac
exec /bin/sh -c "$*"
STUB

  cat >"$bin/docker" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$DOCKER_LOG"
case "$*" in
  "compose version --short"*) echo "v2.32.0" ;;
  "compose version"*) echo "Docker Compose version v2.32.0" ;;
  "info"*) exit 0 ;;
  *seed-dev*) cat "$SEED_STDOUT" ;;
  *logs*cloudflared-quick*)
    echo '2026-11-02T10:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...'
    echo '2026-11-02T10:00:01Z INF |  https://bright-river-1234.trycloudflare.com  |'
    ;;
  *State.Running*) echo true ;;
esac
exit 0
STUB

  cat >"$bin/curl" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$CURL_LOG"
case "$*" in
  *"%{http_code}"*) printf '200' ;;
esac
exit 0
STUB

  chmod +x "$bin/ssh" "$bin/docker" "$bin/curl"
}

# A commit carries the whole contract the scripts read: the compose file, the env
# template with the secret placeholders, and a marker file whose content proves
# which sha was shipped.
fx_make_repo() {
  mkdir -p "$FX_REPO/scripts/review" "$FX_REPO/infra/review"
  cp "$FX_SCRIPTS"/*.sh "$FX_REPO/scripts/review/"

  cat >"$FX_REPO/infra/review/compose.review.yml" <<'YAML'
services:
  postgres:
    image: postgres@sha256:fixture
YAML

  # The real deploy runs these two gates from the release tree; the fixture only
  # has to prove the deploy calls them, so they record their arguments.
  for gate in check.sh check-web-image.sh; do
    cat >"$FX_REPO/infra/review/$gate" <<'GATE'
#!/usr/bin/env bash
printf '%s %s\n' "$0" "$*" >>"$GATE_LOG"
GATE
  done

  cat >"$FX_REPO/infra/review/env.review.template" <<'TEMPLATE'
COMPOSE_PROJECT_NAME=evidgate-review
EVIDGATE_REVIEW_SHA=__EVIDGATE_REVIEW_SHA__
EVIDGATE_DB_SUPERUSER_PASSWORD=__EVIDGATE_DB_SUPERUSER_PASSWORD__
EVIDGATE_OWNER_PASSWORD=__EVIDGATE_OWNER_PASSWORD__
EVIDGATE_APP_PASSWORD=__EVIDGATE_APP_PASSWORD__
EVIDGATE_OPERATOR_PASSWORD=__EVIDGATE_OPERATOR_PASSWORD__
EVIDGATE_WORKER_SCHED_PASSWORD=__EVIDGATE_WORKER_SCHED_PASSWORD__
EVIDGATE_S3_ACCESS_KEY=__EVIDGATE_S3_ACCESS_KEY__
EVIDGATE_S3_SECRET_KEY=__EVIDGATE_S3_SECRET_KEY__
EVIDGATE_S3_BUCKET=evidgate-review
EVIDGATE_S3_ENDPOINT=http://seaweedfs:8333
EVIDGATE__CI__PEPPER=__EVIDGATE__CI__PEPPER__
EVIDGATE__CI__SIGNINGKEY=__EVIDGATE__CI__SIGNINGKEY__
EVIDGATE__DEVLOGIN__ENABLED=true
EVIDGATE__DEVLOGIN__SECRET=__EVIDGATE__DEVLOGIN__SECRET__
EVIDGATE__DATABASE__CONNECTIONSTRING='Host=postgres;Port=5432;Database=evidgate;Username=evidgate_app;Password=__EVIDGATE_APP_PASSWORD__'
EVIDGATE_REVIEW_API_PORT=18080
EVIDGATE_REVIEW_LOCAL_PORT=8088
EVIDGATE_REVIEW_PUBLIC_ORIGIN=
CLOUDFLARE_TUNNEL_TOKEN=
EVIDGATE_REVIEW_TUNNEL_MODE=__EVIDGATE_REVIEW_TUNNEL_MODE__
EVIDGATE_REVIEW_BASIC_AUTH_USER=__EVIDGATE_REVIEW_BASIC_AUTH_USER__
EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD=__EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD__
EVIDGATE_REVIEW_BASIC_AUTH_HASH=__EVIDGATE_REVIEW_BASIC_AUTH_HASH__
TEMPLATE

  printf 'first release\n' >"$FX_REPO/VERSION"
  git -C "$FX_REPO" init -q
  git -C "$FX_REPO" -c user.name=test -c user.email=test@example.com add -A
  git -C "$FX_REPO" -c user.name=test -c user.email=test@example.com commit -qm one
  # shellcheck disable=SC2034 # read by the test files that source this fixture
  FX_FIRST_SHA="$(git -C "$FX_REPO" rev-parse HEAD)"

  printf 'second release\n' >"$FX_REPO/VERSION"
  git -C "$FX_REPO" -c user.name=test -c user.email=test@example.com commit -qam two
  # shellcheck disable=SC2034 # read by the test files that source this fixture
  FX_SECOND_SHA="$(git -C "$FX_REPO" rev-parse HEAD)"
}

fx_setup() { # NAME
  SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/evidgate-review-$1.XXXXXX")"
  FX_REPO="$SANDBOX/repo"
  export HOME="$SANDBOX/home"
  mkdir -p "$HOME" "$SANDBOX/bin" "$FX_REPO"
  export SSH_LOG="$SANDBOX/ssh.log"
  export DOCKER_LOG="$SANDBOX/docker.log"
  export CURL_LOG="$SANDBOX/curl.log"
  export GATE_LOG="$SANDBOX/gate.log"
  export SEED_STDOUT="$SANDBOX/seed.out"
  : >"$SSH_LOG"
  : >"$DOCKER_LOG"
  : >"$CURL_LOG"
  : >"$GATE_LOG"
  cat >"$SEED_STDOUT" <<'SEED'
seed-dev: manifest inputs suite_revision_id=0198a000-0000-7000-8000-00000000d101 case_revision_id=0198a000-0000-7000-8000-00000000d102
seed-dev: manifest inputs environment_revision_id=0198a000-0000-7000-8000-00000000d103 environment_key=dev
EVIDGATE_DEV_TOKEN=fixture-token-value
SEED
  fx_write_stubs
  export PATH="$SANDBOX/bin:$PATH"
  fx_make_repo
  trap fx_teardown EXIT
}
