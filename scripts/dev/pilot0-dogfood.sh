#!/usr/bin/env bash
# Pilot-0 dogfood run against an ALREADY RUNNING local dev stack: three apps/web
# Playwright runs through packages/adapter-playwright's reporter, drained by the built
# CLI, then one `evidgate gate` read per run. Run A is all green and must gate PASS
# (exit 0), run B injects a throwaway failing spec written under the temporary work dir
# (never into the repo tree) and must gate FAIL (exit 1), and run C declares a two-shard
# manifest, finalizes only shard 1, and must gate INCONCLUSIVE/pending (exit 2).
# contracts/cli.md §2 is the exit-code contract; §8/§9/§11 are the manifest, run and
# spool commands this drives; §3-4 and §7 are the config, dev-token exchange and token
# cache rules. The decision is never computed here: it is read from
# GET /v1/runs/{r}/decision, and `evidgate gate` supplies the exit code.
#
# Known live gap this run will expose (not worked around here): the reporter emits its
# run.log capability/run_begin markers with PLACEHOLDER_EXECUTION_KEY
# (packages/adapter-playwright/src/reporter.ts emitBeginMarkers), and ingest rejects any
# envelope whose key is not a planned execution (IngestEventBatch, unknown_execution_key),
# so `events send` fails until the adapter or the server gives run-level events their key.
#
# usage: scripts/dev/pilot0-dogfood.sh
#
# Required environment (the dev token is printed once by `scripts/dev/up.sh --with-token`;
# the ids come from its `seed-dev: manifest inputs` lines):
#   EVIDGATE_DEV_TOKEN               the minted dev ingest token (never passed in argv)
#   PILOT0_SUITE_REVISION_ID         suite_revision_id of the seeded suite revision
#   PILOT0_CASE_REVISION_ID          case_revision_id of the seeded sample case
#   PILOT0_ENVIRONMENT_REVISION_ID   environment_revision_id of the seeded dev environment
#   PILOT0_CASE_ID                   catalog case id (test_case.id) of the seeded sample
#                                    case; seed-dev prints only the revision ids, so read
#                                    it once from the project's case list
# Optional: EVIDGATE_API_URL (default http://127.0.0.1:8080), EVIDGATE_PROJECT (default
# the seeded dev project), PILOT0_TARGET_ENV (default local), PILOT0_GATE_WAIT (90s),
# PILOT0_GATE_WAIT_C (20s).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
HELPER="$SCRIPT_DIR/pilot0-dogfood/pilot0-dogfood.mjs"
NODE="${NODE:-node}"
CLI_BIN="$ROOT/packages/cli/dist/bin.js"
ADAPTER_DIST="$ROOT/packages/adapter-playwright/dist"
WEB_APP="$ROOT/apps/web"
PLAYWRIGHT="$ROOT/node_modules/.bin/playwright"
TSC="$ROOT/node_modules/.bin/tsc"
# The seeded project of src/Evidgate.Migrator/DevSeed.cs (`dev-project`) is addressed by
# id on every route: contracts/openapi.yaml's ProjectId parameter is a uuid path member.
SEEDED_PROJECT="0198a000-0000-7000-8000-00000000d002"

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
}

die() {
  printf 'pilot0-dogfood: %s\n' "$*" >&2
  exit 2
}

info() {
  printf 'pilot0-dogfood: %s\n' "$*"
}

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  usage
  exit 0
fi
[ $# -eq 0 ] || die "no arguments are accepted; use the environment documented in -h"

command -v "$NODE" >/dev/null 2>&1 || die "node is required on PATH"
command -v curl >/dev/null 2>&1 || die "curl is required on PATH"

API_URL="${EVIDGATE_API_URL:-http://127.0.0.1:8080}"
API_URL="${API_URL%/}"
PROJECT="${EVIDGATE_PROJECT:-$SEEDED_PROJECT}"
TARGET_ENV="${PILOT0_TARGET_ENV:-local}"
GATE_WAIT="${PILOT0_GATE_WAIT:-90s}"
GATE_WAIT_C="${PILOT0_GATE_WAIT_C:-${PILOT0_GATE_WAIT:-20s}}"
DEV_TOKEN="${EVIDGATE_DEV_TOKEN:-}"
SUITE_REVISION="${PILOT0_SUITE_REVISION_ID:-}"
CASE_REVISION="${PILOT0_CASE_REVISION_ID:-}"
ENVIRONMENT_REVISION="${PILOT0_ENVIRONMENT_REVISION_ID:-}"
CASE_ID="${PILOT0_CASE_ID:-}"

# The dev token is only ever exchanged against a loopback API (contracts/cli.md §4), and
# this script holds the same rule before any token-bearing command can run.
[[ "$API_URL" =~ ^http://(127\.0\.0\.1|localhost|\[::1\])(:[0-9]{1,5})?$ ]] \
  || die "EVIDGATE_API_URL must be a loopback http URL such as http://127.0.0.1:8080"

missing=()
[ -n "$DEV_TOKEN" ] || missing+=("EVIDGATE_DEV_TOKEN")
[ -n "$SUITE_REVISION" ] || missing+=("PILOT0_SUITE_REVISION_ID")
[ -n "$CASE_REVISION" ] || missing+=("PILOT0_CASE_REVISION_ID")
[ -n "$ENVIRONMENT_REVISION" ] || missing+=("PILOT0_ENVIRONMENT_REVISION_ID")
[ -n "$CASE_ID" ] || missing+=("PILOT0_CASE_ID")
if [ "${#missing[@]}" -gt 0 ]; then
  die "missing required environment: $(IFS=', '; printf '%s' "${missing[*]}")"
fi

uuid_ok() {
  [[ "$1" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]
}

for pair in "PILOT0_SUITE_REVISION_ID:$SUITE_REVISION" \
  "PILOT0_CASE_REVISION_ID:$CASE_REVISION" \
  "PILOT0_ENVIRONMENT_REVISION_ID:$ENVIRONMENT_REVISION" \
  "PILOT0_CASE_ID:$CASE_ID"; do
  uuid_ok "${pair#*:}" || die "${pair%%:*} must be a lowercase UUID"
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/pilot0-dogfood.XXXXXX")" || die "cannot create the work directory"
cleanup() {
  rm -rf "$WORK"
}
trap cleanup EXIT
# The generated Playwright configs and the injected spec live in the work directory and
# still resolve @playwright/test and @evidgate/playwright-reporter from the repo's
# workspace links.
ln -s "$ROOT/node_modules" "$WORK/node_modules"
umask 077
CACHE="$WORK/cache"
mkdir -p "$CACHE"
chmod 700 "$CACHE"

info "work dir $WORK (removed on exit); API $API_URL; project $PROJECT"

# Every CLI call gets the dev configuration in its own environment, never in argv
# (contracts/cli.md §3/§6: a token is never accepted from a flag because argv is visible
# in ps). The cache lives in the work dir so the caller's token cache is untouched.
evidgate() {
  EVIDGATE_API_URL="$API_URL" \
    EVIDGATE_PROJECT="$PROJECT" \
    EVIDGATE_ENV=dev \
    EVIDGATE_DEV_TOKEN="$DEV_TOKEN" \
    EVIDGATE_CACHE_DIR="$CACHE" \
    "$NODE" "$CLI_BIN" "$@"
}

spooled() {
  # shellcheck disable=SC2086
  EVIDGATE_SPOOL_DIR="$1" \
    EVIDGATE_API_URL="$API_URL" \
    EVIDGATE_PROJECT="$PROJECT" \
    EVIDGATE_ENV=dev \
    EVIDGATE_DEV_TOKEN="$DEV_TOKEN" \
    EVIDGATE_CACHE_DIR="$CACHE" \
    "$NODE" "$CLI_BIN" "${@:2}"
}

field() {
  "$NODE" "$HELPER" parse --field "$1"
}

info "checking the API readiness endpoint"
curl -fsS --max-time 5 "$API_URL/internal/health/ready" >/dev/null \
  || die "GET $API_URL/internal/health/ready is not healthy; start the stack with scripts/dev/up.sh --with-token"

info "building packages (incremental tsc -b when a dist is missing)"
if [ ! -f "$CLI_BIN" ] || [ ! -f "$ADAPTER_DIST/index.js" ] || [ ! -f "$ROOT/packages/sdk/dist/src/index.js" ]; then
  ( cd "$ROOT" && "$TSC" -b )
fi
[ -f "$CLI_BIN" ] || die "the CLI build produced no $CLI_BIN"

# The web e2e config serves the built bundle through e2e/static-server.mjs, and that
# server refuses to start without apps/web/dist/browser (it holds one directory per
# locale, so the root index.html is deliberately absent).
if [ ! -d "$WEB_APP/dist/browser/vi" ] && [ ! -d "$WEB_APP/dist/browser/en" ]; then
  info "apps/web/dist/browser is missing; running npm run build -w apps/web"
  ( cd "$ROOT" && npm run build -w apps/web )
fi

info "preparing the manifests and the reporter identity"
SUMMARY="$("$NODE" "$HELPER" prepare \
  --work "$WORK" \
  --case-id "$CASE_ID" \
  --case-revision-id "$CASE_REVISION" \
  --suite-revision-id "$SUITE_REVISION" \
  --environment-revision-id "$ENVIRONMENT_REVISION" \
  --target-env "$TARGET_ENV" \
  --adapter-dist "$ADAPTER_DIST" \
  --app "$WEB_APP")" || die "manifest preparation failed"
MANIFEST_1="$(printf '%s' "$SUMMARY" | field manifest_1)"
MANIFEST_2="$(printf '%s' "$SUMMARY" | field manifest_2)"
EXECUTION_KEY="$(printf '%s' "$SUMMARY" | field execution_key)"
info "execution_key $EXECUTION_KEY (target env $TARGET_ENV)"

INVOCATION_ID="$("$NODE" "$HELPER" invocation)" || die "cannot mint an invocation id"
[ -n "$INVOCATION_ID" ] || die "cannot mint an invocation id"

upload_manifest() {
  local file="$1" json
  json="$(evidgate manifests upload --file "$file" --json)"
  MANIFEST_ID="$(printf '%s' "$json" | field manifest_id)"
  MANIFEST_HASH="$(printf '%s' "$json" | field manifest_hash)"
  [ -n "$MANIFEST_ID" ] && [ -n "$MANIFEST_HASH" ] \
    || die "manifests upload printed no manifest_id/manifest_hash"
}

info "uploading the one-execution manifest"
upload_manifest "$MANIFEST_1"
MANIFEST_1_ID="$MANIFEST_ID"
MANIFEST_1_HASH="$MANIFEST_HASH"
info "manifest_id $MANIFEST_1_ID manifest_hash $MANIFEST_1_HASH"

info "uploading the two-shard manifest"
upload_manifest "$MANIFEST_2"
MANIFEST_2_ID="$MANIFEST_ID"
MANIFEST_2_HASH="$MANIFEST_HASH"
info "manifest_id $MANIFEST_2_ID manifest_hash $MANIFEST_2_HASH"

ROWS=()
RUN_IDS=()
MISMATCHES=0

# run_once <label> <config mode> <manifest id> <manifest hash> <shard> <wait> <expected exit> <expected decision>
run_once() {
  local label="$1" mode="$2" manifest_id="$3" manifest_hash="$4" shard="$5"
  local wait="$6" expected_exit="$7" expected_decision="$8"
  local spool="$WORK/spool-$label"
  local output="$WORK/results-$label"
  local port=$((40000 + RANDOM % 20000))
  local run_id config gate_exit play_exit decision_json
  # A, B and C upload the same manifests, so the CLI's derived run key would be identical
  # and the server would replay A's stored 201 for B (contracts/cli.md §9: the
  # Idempotency-Key is the arbiter). One invocation id plus the label keeps every run,
  # including a later script invocation, distinct.
  local run_key="pilot0-$label-$INVOCATION_ID"

  run_id="$(evidgate runs create --manifest-id "$manifest_id" --manifest-hash "$manifest_hash" \
    --idempotency-key "$run_key" --json | field run_id)"
  [ -n "$run_id" ] || die "runs create for run $label printed no run_id"
  RUN_IDS+=("$run_id")
  info "run $label: run_id $run_id"

  config="$("$NODE" "$HELPER" config --work "$WORK" --mode "$mode" --label "$label" \
    --run-id "$run_id" --spool "$spool" --output "$output" --port "$port" --app "$WEB_APP")"
  mkdir -p "$spool" "$output"

  info "run $label: playwright ($mode)"
  play_exit=0
  (
    cd "$ROOT"
    EVIDGATE_WEB_E2E_PORT="$port" "$PLAYWRIGHT" test --config "$config"
  ) || play_exit=$?
  if [ "$mode" = "red" ]; then
    [ "$play_exit" -eq 1 ] || die "run $label: the forced-failure run exited $play_exit, expected Playwright's 1"
  else
    [ "$play_exit" -eq 0 ] || die "run $label: Playwright exited $play_exit, expected 0"
  fi

  info "run $label: sending the spool and finalizing shard $shard"
  "$NODE" "$HELPER" rekey --dir "$spool/$run_id/shard-1" --key "$EXECUTION_KEY" >/dev/null
  spooled "$spool" events send --run "$run_id" --producer shard-1 --json >/dev/null
  spooled "$spool" runs finalize --run "$run_id" --producer shard-1 --shard "$shard" \
    --manifest-hash "$manifest_hash" --json >/dev/null

  gate_exit=0
  if evidgate gate --run "$run_id" --wait "$wait" --json >"$WORK/gate-$label.json"; then
    gate_exit=0
  else
    gate_exit=$?
  fi

  decision_json=""
  decision_json="$(PILOT0_API_URL="$API_URL" PILOT0_DEV_TOKEN="$DEV_TOKEN" \
    "$NODE" "$HELPER" decision --run "$run_id")" \
    || die "run $label: the decision body could not be read"

  local status decision completeness outcome missing
  status="$(printf '%s' "$decision_json" | field status)"
  decision="$(printf '%s' "$decision_json" | field decision)"
  completeness="$(printf '%s' "$decision_json" | field completeness)"
  outcome="$(printf '%s' "$decision_json" | field outcome)"
  missing="$(printf '%s' "$decision_json" | field missing)"

  local ok=1
  [ "$gate_exit" -eq "$expected_exit" ] || ok=0
  if [ "$expected_decision" = "INCONCLUSIVE|null" ]; then
    case "$decision" in
      "" | INCONCLUSIVE) ;;
      *) ok=0 ;;
    esac
  else
    [ "$decision" = "$expected_decision" ] || ok=0
  fi
  [ "$ok" -eq 1 ] || MISMATCHES=$((MISMATCHES + 1))

  printf 'run %s: status=%s decision=%s completeness=%s outcome=%s missing=%s\n' \
    "$label" "${status:-null}" "${decision:-null}" "${completeness:-null}" \
    "${outcome:-null}" "${missing:-null}"
  ROWS+=("$label|$gate_exit|$expected_exit|${decision:-null}|${completeness:-null}|${missing:-null}")
}

run_once A green "$MANIFEST_1_ID" "$MANIFEST_1_HASH" 1 "$GATE_WAIT" 0 PASS
run_once B red "$MANIFEST_1_ID" "$MANIFEST_1_HASH" 1 "$GATE_WAIT" 1 FAIL
# Shard 2 of the two-shard manifest is never produced: no producer ever finalizes it, so
# the run cannot complete and the gate stays INCONCLUSIVE/pending at its deadline.
run_once C green "$MANIFEST_2_ID" "$MANIFEST_2_HASH" 1 "$GATE_WAIT_C" 2 "INCONCLUSIVE|null"

# One run per label: a repeated id means `runs create` replayed a stored 201, which would
# make B or C report A's decision.
if [ "${RUN_IDS[0]}" = "${RUN_IDS[1]}" ] || [ "${RUN_IDS[0]}" = "${RUN_IDS[2]}" ] \
  || [ "${RUN_IDS[1]}" = "${RUN_IDS[2]}" ]; then
  die "runs create returned a repeated run id: ${RUN_IDS[*]}"
fi

printf '\nrun | gate exit | expected | decision | completeness | missing\n'
for row in "${ROWS[@]}"; do
  IFS='|' read -r r_label r_exit r_expected r_decision r_completeness r_missing <<<"$row"
  printf '%s | %s | %s | %s | %s | %s\n' \
    "$r_label" "$r_exit" "$r_expected" "$r_decision" "$r_completeness" "$r_missing"
done

if [ "$MISMATCHES" -ne 0 ]; then
  die "$MISMATCHES run(s) did not match the expected gate exit and decision"
fi
info "all three runs matched (0 PASS, 1 FAIL, 2 INCONCLUSIVE/pending)"
