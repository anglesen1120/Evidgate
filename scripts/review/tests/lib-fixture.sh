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

fx_mode() { # FILE — octal permissions, on GNU stat and BSD stat
  # GNU's `stat -f` reports the filesystem and can exit 0 on the BSD format, so
  # `-c '%a'` (which BSD rejects outright) goes first.
  stat -c '%a' "$1" 2>/dev/null || stat -f '%Lp' "$1"
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
# Options come before the host (-N, -T, -o X, -L spec, ...), so they have to be
# consumed before the command can be found. A port forward stays in the
# foreground so the caller can kill it.
raw="$*"
forward=0
while [ $# -gt 0 ]; do
  case "$1" in
    -N) forward=1; shift ;;
    -L|-R) forward=1; shift 2 ;;
    -o|-i|-p|-l|-F|-E|-J) shift 2 ;;
    -*) shift ;;
    *) break ;;
  esac
done
host="$1"
# The raw argv, identity options included: a test proves the key reaches every
# call without having to replay ssh's option parsing.
printf 'argv: %s\n' "$raw" >>"$SSH_LOG"
shift
if [ "$forward" = 1 ]; then
  exec sleep 300
fi
if [ "$host" != "review-host" ]; then
  echo "stub ssh: unexpected host '$host'" >&2
  exit 255
fi
printf '%s\n' "$*" >>"$SSH_LOG"
# Host facts are faked so a test can describe the host it deploys to. The real
# callers print one field of /proc/meminfo through awk, so the answer must be the
# bare number: a whole line reads as non-numeric and silently disables the check.
case "$*" in
  *"/proc/meminfo"*)
    case "$*" in
      *SwapTotal*) echo "${FX_SWAP_TOTAL_KB:-2097148}" ;;
      *) echo "${FX_MEM_TOTAL_KB:-4030000}" ;;
    esac
    exit 0
    ;;
  # The free-space probe describes the faked host, not the machine running the
  # suite: a tmpfs /tmp with a few GiB would otherwise fail every deploy.
  *"df -Pk"*) echo "${FX_FREE_KB:-209715200}" ; exit 0 ;;
  "uname -m") echo "aarch64" ; exit 0 ;;
esac
exec /bin/sh -c "$*"
STUB

  cat >"$bin/docker" <<'STUB'
#!/bin/sh
# The release tag and the profile switch reach compose as environment
# assignments in the command string, so the log records them the way compose
# sees them.
prefix=""
# A token in the environment of a docker call is a leak: nothing docker runs
# needs the tunnel token, and the deploy keeps it in a shell variable only.
[ -z "${CLOUDFLARE_TUNNEL_TOKEN:-}" ] || printf 'LEAK %s\n' "$*" >>"$DOCKER_LOG"
# An empty value is recorded too: compose refuses a service whose tag is
# required but empty, so a blank assignment is a defect, not an absence.
[ -z "${EVIDGATE_REVIEW_TAG+x}" ] || prefix="$prefix EVIDGATE_REVIEW_TAG=$EVIDGATE_REVIEW_TAG"
[ -z "${COMPOSE_PROFILES:-}" ] || prefix="$prefix COMPOSE_PROFILES=$COMPOSE_PROFILES"
if [ -n "$prefix" ]; then
  printf '%s %s\n' "${prefix# }" "$*" >>"$DOCKER_LOG"
else
  printf '%s\n' "$*" >>"$DOCKER_LOG"
fi
case "$*" in
  # FX_COMPOSE_VERSION lets a test describe a host whose compose is newer (or
  # too old) than the one this stub assumes.
  "compose version --short"*) echo "${FX_COMPOSE_VERSION:-v2.32.0}" ;;
  "compose version"*) echo "Docker Compose version ${FX_COMPOSE_VERSION:-v2.32.0}" ;;
  "info"*) exit 0 ;;
  *seed-dev*) cat "$SEED_STDOUT" ;;
  # FX_PORTS lists the published ports a host reports, so a test can describe a
  # stack that already holds a door open.
  "ps --filter"*"--format"*)
    [ -z "${FX_PORTS:-}" ] || cat "$FX_PORTS"
    ;;
  # FX_IMAGES lists `repo:tag` refs, so a test can describe the images a host
  # already holds and check which ones a prune removes.
  "images --format"*)
    [ -z "${FX_IMAGES:-}" ] || cat "$FX_IMAGES"
    ;;
  *logs*cloudflared-quick*)
    # FX_QUICK_LOG lets a test describe a log that outlived several starts; the
    # default is one session with the service endpoint in the banner.
    if [ -n "${FX_QUICK_LOG:-}" ] && [ -f "${FX_QUICK_LOG}" ]; then
      cat "${FX_QUICK_LOG}"
    else
      echo '2026-11-02T10:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...'
      echo '2026-11-02T10:00:00Z INF +------------------------------------------------------------------------------+'
      echo '2026-11-02T10:00:00Z INF |  https://api.trycloudflare.com/v1/tunnels/9f1c  |'
      echo '2026-11-02T10:00:01Z INF |  https://bright-river-1234.trycloudflare.com  |'
    fi
    ;;
  *State.StartedAt*)
    # The current session's start, so the caller narrows the log with --since.
    [ -z "${FX_STARTED_AT:-}" ] || echo "${FX_STARTED_AT}"
    ;;
  # FX_RUNNING=false describes a connector that exists but is stopped: it
  # publishes nothing, so status must not report it as live.
  *State.Running*) echo "${FX_RUNNING:-true}" ;;
esac
exit 0
STUB

  cat >"$bin/curl" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$CURL_LOG"
case "$*" in
  # The real origin answers 401 without credentials; FX_HTTP_CODE lets a test
  # describe an edge that redirects or serves first instead.
  *"%{http_code}"*) printf '%s' "${FX_HTTP_CODE:-401}" ;;
esac
exit 0
STUB

  # The remote commands run in this shell, so a `sudo` in one of them would touch
  # the real machine (a swap file, /etc/fstab). This stub records the argv and
  # does nothing, which keeps the deploy's command string under test without
  # letting the suite mutate the host it runs on.
  cat >"$bin/sudo" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$SUDO_LOG"
# Only `tee` reads stdin; draining it for every command would block on a
# terminal. What it would have appended is recorded instead.
case "$1" in
  tee) printf 'stdin %s\n' "$(cat)" >>"$SUDO_LOG" ;;
esac
exit 0
STUB

  chmod +x "$bin/ssh" "$bin/docker" "$bin/curl" "$bin/sudo"
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
  # has to prove the deploy calls them, so they record their arguments and the
  # tag the image gate resolves with (compose prefers it over the env file).
  for gate in check.sh check-web-image.sh; do
    cat >"$FX_REPO/infra/review/$gate" <<'GATE'
#!/usr/bin/env bash
printf '%s TAG=%s %s\n' "$0" "${EVIDGATE_REVIEW_TAG:-}" "$*" >>"$GATE_LOG"
GATE
  done

  cat >"$FX_REPO/infra/review/env.review.template" <<'TEMPLATE'
COMPOSE_PROJECT_NAME=evidgate-review
EVIDGATE_REVIEW_TAG=__EVIDGATE_REVIEW_SHA__
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
  export SANDBOX
  FX_REPO="$SANDBOX/repo"
  export HOME="$SANDBOX/home"
  mkdir -p "$HOME" "$SANDBOX/bin" "$FX_REPO"
  export SSH_LOG="$SANDBOX/ssh.log"
  export DOCKER_LOG="$SANDBOX/docker.log"
  export CURL_LOG="$SANDBOX/curl.log"
  export GATE_LOG="$SANDBOX/gate.log"
  export SUDO_LOG="$SANDBOX/sudo.log"
  export SEED_STDOUT="$SANDBOX/seed.out"
  : >"$SSH_LOG"
  : >"$DOCKER_LOG"
  : >"$CURL_LOG"
  : >"$GATE_LOG"
  : >"$SUDO_LOG"
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
