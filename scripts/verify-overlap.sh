# The opt-in side lane of verify.sh (`EVIDGATE_VERIFY_OVERLAP=1`). Sourced by
# verify-guards.sh, never executed: no shebang, no shell options of its own
# (verify.sh sets `set -euo pipefail`). Bash 3.2 safe (macOS): no `wait -n`, no
# associative arrays.
#
# While the `test` stage (the Tenancy shards, the longest stage) runs, the stages
# below run one after another in a niced background lane, each with its own log and
# status. A stage is in the list only when it needs neither Docker, nor the `test`
# stage's outputs, nor a .NET build graph, nor the test host's CPU:
#   tsc-build tsc-spec prettier   node only; read sources and node_modules
#   check-*  image-pins           read the tree or git, a few seconds each
#   licence-scan audit-deps sbom  read lock files and obj/project.assets.json,
#                                 which the `build` stage already wrote
# Left on the main line on purpose: restore/build (everything needs them first),
# format (loads MSBuild, ~2 GB), isolation/images/parser-no-egress (Docker),
# fixtures-hostile/fixtures-fuzz (CPU-bound; they would slow the Tenancy shards),
# vitest/e2e-a11y/eslint (browser or multi-GB node), check-review-infra (fails on
# the LAN host). Stage order inside the lane is the canonical order, so tsc-build
# still precedes anything that needs its output.
OVERLAP_STAGES=(
  tsc-build tsc-spec prettier
  check-layout check-sources check-adr-matrix check-adr check-links
  check-contract-conventions check-env-matrix check-dor-security check-threat-model
  check-ci-parity check-action-pins check-ci-recipe check-codegen-drift
  check-permissions-drift check-rls check-review-blockers check-gate-scope
  image-pins licence-scan audit-deps sbom
)

# SIDE_NAMES: the stages that run in the lane; SIDE_DIR: logs and `<name>.rc` files
# ("<rc> <seconds>", written when the stage ends); SIDE_T0: when the run started.
SIDE_NAMES=() SIDE_PID="" SIDE_DIR="" SIDE_T0=0

# `kill_tree <pid>`: TERM to a process and everything under it, children first, so
# a stage's own npm/dotnet children do not outlive the gate.
kill_tree() {
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do kill_tree "$child"; done
  kill -TERM "$1" 2>/dev/null || true
}

# `plan_overlap`: fills SIDE_NAMES from `selected`; only stages after `test` qualify.
plan_overlap() {
  local name seen_test=""
  SIDE_NAMES=() SIDE_T0=$SECONDS
  case "${EVIDGATE_VERIFY_OVERLAP:-0}" in
  0) return 0 ;;
  1) ;;
  *)
    echo "verify: EVIDGATE_VERIFY_OVERLAP must be 0 or 1, got '${EVIDGATE_VERIFY_OVERLAP}'" >&2
    return 2
    ;;
  esac
  for name in "${selected[@]}"; do
    if [[ "$name" == "test" ]]; then
      seen_test=1
    elif [[ -n "$seen_test" ]] && in_list "$name" "${OVERLAP_STAGES[@]}"; then
      SIDE_NAMES+=("$name")
    fi
  done
}

# The lane body, run as a background subshell: one stage at a time, output to its
# own log, stops after the first failed stage like the main line does.
run_side_lane() {
  local name t0 rc child=""
  trap '[[ -z "$child" ]] || kill_tree "$child"; exit 143' INT TERM
  renice -n 10 -p "$(exec sh -c 'echo $PPID')" >/dev/null 2>&1 || true
  for name in "${SIDE_NAMES[@]}"; do
    t0=$SECONDS
    (
      set -e
      run_stage "$name"
    ) >"$SIDE_DIR/$name.log" 2>&1 </dev/null &
    child=$!
    rc=0
    wait "$child" || rc=$?
    child=""
    echo "$rc $((SECONDS - t0))" >"$SIDE_DIR/$name.rc.tmp"
    mv "$SIDE_DIR/$name.rc.tmp" "$SIDE_DIR/$name.rc"
    [[ "$rc" -eq 0 ]] || break
  done
}

start_side_lane() {
  [[ "${#SIDE_NAMES[@]}" -gt 0 ]] || return 0
  SIDE_DIR="$(mktemp -d)"
  echo "== side lane (EVIDGATE_VERIFY_OVERLAP=1): beside test, one at a time: ${SIDE_NAMES[*]}"
  run_side_lane &
  SIDE_PID=$!
  trap stop_side_lane EXIT
}

# Idempotent; also the EXIT trap, so every way out of the gate stops the lane and
# drops its logs.
stop_side_lane() {
  stop_lane_process
  if [[ -n "$SIDE_DIR" ]]; then rm -rf "$SIDE_DIR"; SIDE_DIR=""; fi
}

# `side_lane_failed`: true when a lane stage has already ended non-zero.
side_lane_failed() {
  local name rc secs
  [[ -n "$SIDE_DIR" ]] || return 1
  for name in "${SIDE_NAMES[@]}"; do
    [[ -f "$SIDE_DIR/$name.rc" ]] || continue
    read -r rc secs <"$SIDE_DIR/$name.rc"
    [[ "$rc" -eq 0 ]] || return 0
  done
  return 1
}

# `finish_side_lane <wait>`: with `wait` 1 it first waits for the lane. Prints each
# finished stage's log whole (never interleaved with another stage's output), its
# `== stage` timing line, and returns the first non-zero stage status (0 if none).
finish_side_lane() {
  local name rc secs first=0
  [[ -n "$SIDE_DIR" ]] || return 0
  if [[ "$1" == "1" && -n "$SIDE_PID" ]]; then
    wait "$SIDE_PID" 2>/dev/null || true
    SIDE_PID=""
  else
    stop_lane_process
  fi
  for name in "${SIDE_NAMES[@]}"; do
    if [[ ! -f "$SIDE_DIR/$name.rc" ]]; then
      echo "== side lane: $name did not run to the end (lane stopped)"
      continue
    fi
    read -r rc secs <"$SIDE_DIR/$name.rc"
    echo "== side lane: output of $name"
    cat "$SIDE_DIR/$name.log"
    record_stage_time "$name" "$rc" "$secs"
    if [[ "$rc" -ne 0 && "$first" -eq 0 ]]; then first="$rc"; fi
  done
  return "$first"
}

# Stops the lane process without dropping the logs finish_side_lane still prints.
stop_lane_process() {
  if [[ -n "$SIDE_PID" ]]; then
    kill_tree "$SIDE_PID"
    wait "$SIDE_PID" 2>/dev/null || true
    SIDE_PID=""
  fi
}

# One line under the stage table: stage seconds add up past the wall clock when
# stages overlapped, so the table total alone would mislead.
overlap_summary_note() {
  [[ "${#SIDE_NAMES[@]}" -gt 0 ]] || return 0
  printf '   %-28s %3s %6ss  (side-lane stages overlapped test; the total above counts them in full)\n' \
    "wall" "-" "$((SECONDS - SIDE_T0))"
}
