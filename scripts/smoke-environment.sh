#!/usr/bin/env bash
# 10.1.1-T1.2 per-environment smoke test (story 10.1.1, AC2).
#
# Reads infra/<env>/environment.json (the same file the environment-config lint
# validates) and exercises what it declares, on the real process:
#
#   1. GET <process>/internal/health/live        must answer 200;
#   2. GET <process>/internal/health/ready       200 = ready; 503 = the process
#      is up but a dependency (database, migration journal head) is not — the
#      configured per-environment behaviour of 00 §9.1. A 503 exits 3 unless
#      --allow-503, because a deploy smoke must not pass with a down dependency;
#   3. no listener of the probed ports is bound to a non-loopback address, and
#      (when a non-loopback address of this host is known) the internal health
#      path is refused off-loopback — the dev-observable half of "operational
#      endpoints are never on the public ingress" (00 §9.1). The provider-side
#      half is the load-balancer configuration of 10.2.1-T1.
#
# usage: scripts/smoke-environment.sh [--env <dev|staging|prod>]
#                                    [--base-url <url>] [--worker-base-url <url>]
#                                    [--timeout <seconds>] [--allow-503]
#
#   --env            environment whose infra/<env>/environment.json is used (default dev)
#   --base-url       override probes.api.baseUrl     (e.g. on a remote deploy host)
#   --worker-base-url override probes.worker.baseUrl
#   --timeout        curl timeout per request, seconds (default: the probe's own timeout)
#   --allow-503      treat a 503 readiness as a pass (dependency intentionally down)
#
# Run it against the dev stack after scripts/dev/up.sh. For staging/prod the
# probe addresses are loopback, so run it on the instance/sidecar itself (or pass
# --base-url with a reachable internal address).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

ENVIRONMENT=dev
NOT_READY=0
BASE_URL_OVERRIDE=""
WORKER_BASE_URL_OVERRIDE=""
TIMEOUT_OVERRIDE=""
ALLOW_503=0
FAILED=0
SKIPPED=0

usage() {
  sed -n '2,28p' "$0" | sed 's/^# \{0,1\}//'
}

die() {
  printf 'smoke-environment: %s\n' "$*" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
  --env)
    [[ $# -ge 2 ]] || die "--env needs a value"
    ENVIRONMENT="$2"
    shift 2
    ;;
  --base-url)
    [[ $# -ge 2 ]] || die "--base-url needs a value"
    BASE_URL_OVERRIDE="$2"
    shift 2
    ;;
  --worker-base-url)
    [[ $# -ge 2 ]] || die "--worker-base-url needs a value"
    WORKER_BASE_URL_OVERRIDE="$2"
    shift 2
    ;;
  --timeout)
    [[ $# -ge 2 ]] || die "--timeout needs a value"
    TIMEOUT_OVERRIDE="$2"
    shift 2
    ;;
  --allow-503) ALLOW_503=1; shift ;;
  -h | --help)
    usage
    exit 0
    ;;
  *) die "unexpected argument '$1' (try --help)" ;;
  esac
done

command -v curl >/dev/null 2>&1 || die "curl is required"
command -v python3 >/dev/null 2>&1 || die "python3 is required (it reads infra/<env>/environment.json)"

ENV_FILE="infra/${ENVIRONMENT}/environment.json"
[[ -f "$ENV_FILE" ]] || die "missing $ENV_FILE (environments: dev, staging, prod)"

# The probe configuration is read from the environment file itself, so a change
# to infra/<env>/environment.json changes this smoke without a code change.
{
  read -r API_BASE API_LIVE API_READY API_LIVE_TIMEOUT API_READY_TIMEOUT
  read -r WORKER_BASE WORKER_LIVE WORKER_READY WORKER_LIVE_TIMEOUT WORKER_READY_TIMEOUT
} < <(
  python3 - "$ENV_FILE" <<'PY'
import json
import sys

probes = json.load(open(sys.argv[1], encoding="utf-8"))["probes"]
# one line per process: baseUrl, liveness path, readiness path, liveness timeout,
# readiness timeout — in the order the shell reads them
for process in ("api", "worker"):
    probe = probes[process]
    fields = [probe["baseUrl"]]
    for kind in ("liveness", "readiness"):
        fields.append(probe[kind]["path"])
    for kind in ("liveness", "readiness"):
        fields.append(str(probe[kind]["timeoutSeconds"]))
    print(" ".join(fields))
PY
)

[[ -n "${API_BASE}" && -n "${WORKER_BASE}" && -n "${API_LIVE}" && -n "${WORKER_READY}" ]] \
  || die "could not read the probe configuration from $ENV_FILE (is the file valid JSON with probes.api/probes.worker?)"

[[ -n "${BASE_URL_OVERRIDE}" ]] && API_BASE="$BASE_URL_OVERRIDE"
[[ -n "${WORKER_BASE_URL_OVERRIDE}" ]] && WORKER_BASE="$WORKER_BASE_URL_OVERRIDE"

printf 'smoke-environment: env=%s api=%s worker=%s (from %s)\n' "$ENVIRONMENT" "$API_BASE" "$WORKER_BASE" "$ENV_FILE"

port_of() { printf '%s' "${1##*:}" | tr -d '/'; }

# 1 + 2: liveness must be 200 and readiness must be 200 (or 503, the configured
# "a dependency is down" answer).
check_probe() {
  local process="$1" base="$2" live_path="$3" ready_path="$4" live_timeout="$5" ready_timeout="$6" timeout status
  timeout="$live_timeout"
  [[ -n "${TIMEOUT_OVERRIDE}" ]] && timeout="$TIMEOUT_OVERRIDE"

  if ! status="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$timeout" "${base}${live_path}" 2>/dev/null)"; then
    printf 'FAIL  %-6s GET %s%s -> no response (is the %s running?)\n' "$process" "$base" "$live_path" "$process"
    FAILED=1
    return
  fi
  if [[ "$status" != "200" ]]; then
    printf 'FAIL  %-6s GET %s%s -> %s (liveness must be 200 while the process is up)\n' "$process" "$base" "$live_path" "$status"
    FAILED=1
  else
    printf 'ok    %-6s GET %s%s -> 200\n' "$process" "$base" "$live_path"
  fi

  timeout="$ready_timeout"
  [[ -n "${TIMEOUT_OVERRIDE}" ]] && timeout="$TIMEOUT_OVERRIDE"
  if ! status="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$timeout" "${base}${ready_path}" 2>/dev/null)"; then
    printf 'FAIL  %-6s GET %s%s -> no response\n' "$process" "$base" "$ready_path"
    FAILED=1
    return
  fi
  case "$status" in
  200) printf 'ok    %-6s GET %s%s -> 200 (ready)\n' "$process" "$base" "$ready_path" ;;
  503)
    printf 'BUSY  %-6s GET %s%s -> 503 (a dependency is down: database or migration-journal head)\n' "$process" "$base" "$ready_path"
    NOT_READY=1
    [[ "$ALLOW_503" -eq 1 ]] || FAILED=1
    ;;
  *)
    printf 'FAIL  %-6s GET %s%s -> %s (expected 200, or 503 while a dependency is down)\n' "$process" "$base" "$ready_path" "$status"
    FAILED=1
    ;;
  esac
}

check_probe api "$API_BASE" "$API_LIVE" "$API_READY" "$API_LIVE_TIMEOUT" "$API_READY_TIMEOUT"
check_probe worker "$WORKER_BASE" "$WORKER_LIVE" "$WORKER_READY" "$WORKER_LIVE_TIMEOUT" "$WORKER_READY_TIMEOUT"

# 3a: the internal ports must be loopback-only on this host.
check_loopback_bindings() {
  local label="$1" url="$2" port line address offenders=0
  port="$(port_of "$url")"
  if ! command -v lsof >/dev/null 2>&1; then
    printf 'SKIP  %-6s port %s: lsof is not available, binding address not checked\n' "$label" "$port"
    SKIPPED=$((SKIPPED + 1))
    return
  fi
  local listing
  listing="$(lsof -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null || true)"
  if [[ -z "$listing" ]]; then
    printf 'SKIP  %-6s port %s: no listener found (process not started?)\n' "$label" "$port"
    SKIPPED=$((SKIPPED + 1))
    return
  fi
  while IFS= read -r line; do
    address="$(printf '%s' "$line" | sed -nE 's/.*TCP ([^ ]+):[0-9]+ \(LISTEN\).*/\1/p')"
    [[ -n "$address" ]] || continue
    case "$address" in
    127.0.0.1 | "[::1]" | localhost) ;;
    *)
      printf 'FAIL  %-6s port %s is bound to %s — an operational port must be loopback-only (00 §9.1)\n' "$label" "$port" "$address"
      offenders=$((offenders + 1))
      ;;
    esac
  done <<<"$listing"
  if [[ "$offenders" -eq 0 ]]; then
    printf 'ok    %-6s port %s: every listener is loopback-only\n' "$label" "$port"
  else
    FAILED=1
  fi
}

check_loopback_bindings api "$API_BASE"
check_loopback_bindings worker "$WORKER_BASE"

# 3b: the internal path must not answer on this host's non-loopback address.
non_loopback_address() {
  local candidates=""
  if command -v ipconfig >/dev/null 2>&1; then
    candidates="$(ipconfig getifaddr en0 2>/dev/null || true) $(ipconfig getifaddr en1 2>/dev/null || true)"
  fi
  if [[ -z "${candidates// /}" ]] && command -v hostname >/dev/null 2>&1; then
    candidates="$(hostname -I 2>/dev/null || true)"
  fi
  for candidate in $candidates; do
    case "$candidate" in
    127.* | "") ;;
    *)
      printf '%s' "$candidate"
      return 0
      ;;
    esac
  done
  return 1
}

# A connection refused/never answered is the pass: the listener is not reachable
# on that address. Any HTTP status means it answered on a public address.
off_loopback() {
  local base="$1" path="$2" scheme="${1%%://*}" rest="${1#*://}" port status
  port="${rest##*:}"
  if status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "${scheme}://${address}:${port}${path}" 2>/dev/null)" \
    && [[ -n "$status" && "$status" != "000" ]]; then
    printf 'FAIL  GET %s://%s:%s%s -> %s off-loopback: an operational path answered on a public address (00 §9.1)\n' "$scheme" "$address" "$port" "$path" "$status"
    FAILED=1
  else
    printf 'ok    GET %s://%s:%s%s -> refused off-loopback (internal path is not on the public ingress)\n' "$scheme" "$address" "$port" "$path"
  fi
}

if address="$(non_loopback_address)"; then
  off_loopback "$API_BASE" "$API_LIVE"
  off_loopback "$WORKER_BASE" "$WORKER_LIVE"
else
  printf 'SKIP  no non-loopback address found on this host, off-loopback reachability not probed\n'
  SKIPPED=$((SKIPPED + 1))
fi

if [[ "$FAILED" -ne 0 ]]; then
  printf 'smoke-environment: FAILED (env=%s, %s skipped assertion(s))\n' "$ENVIRONMENT" "$SKIPPED" >&2
  exit 1
fi
printf 'smoke-environment: PASSED (env=%s, %s skipped assertion(s))\n' "$ENVIRONMENT" "$SKIPPED"

# exit 3 marks "the internal paths answered, but a dependency is down", so a
# deploy pipeline can tell a broken release from a busy database (10.2.2-T1).
if [[ "$NOT_READY" -eq 1 ]]; then
  exit 3
fi
exit 0
