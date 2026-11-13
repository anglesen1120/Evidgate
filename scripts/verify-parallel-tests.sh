# The parallel `test` stage (`run_parallel_tests` and its helpers). Split out of
# verify-guards.sh by responsibility to keep each file under the 500-line limit.
# Sourced by verify-guards.sh, never executed: no shebang, no shell options of its
# own (verify.sh sets `set -euo pipefail`).

# --- parallel test stage ------------------------------------------------------
# The `test` stage's Docker half. One Tenancy.IntegrationTests process is the
# critical path of the whole stage (5.6k tests, one PostgreSQL container, at most
# four classes at a time), so its classes are split over EVIDGATE_TENANCY_SHARDS
# processes (default 3), each with its own container, and the other test projects
# run beside them. A class belongs to the shard its name hashes to, so a new class
# lands in exactly one shard without a manifest to forget. The shard totals must
# add up to the discovered total, and every process's verdict is kept: one failure
# anywhere fails the stage after all logs are shown.
TENANCY_PROJECT=tests/Tenancy.IntegrationTests

# `tenancy_classes <listing>`: the fully qualified test classes of a --list-tests listing.
tenancy_classes() {
  printf '%s\n' "$1" | sed -n -E '/^  [A-Za-z]/{s/\(.*$//;s/^  //;s/\.[^.]*$//;p;}' | sort -u
}

# `test_log_total <log>`: the "total:" count of one run's summary, 0 when absent.
test_log_total() {
  sed -n 's/^  total: \([0-9][0-9]*\)$/\1/p' "$1" | tail -n 1 | grep . || echo 0
}

# Background runs of this stage: parallel arrays of pid, name, log file and start
# second. A reaped run's pid is cleared; PT_CAP is the most runs alive at once
# (0 = no cap).
PT_PIDS=() PT_NAMES=() PT_FILES=() PT_STARTS=() PT_LOGS= PT_CAP=0 PT_FAILED=0

# Per-class timing (scripts/class-timing.mjs). While CT_DIR is set, every run started by
# `start_test_run` also writes a JUnit report there; `class_timing_report` prints the slowest
# classes and writes the TSV. Reporting only: it never changes a stage's exit code.
CT_DIR="" CT_FLAGS=() CT_SCRIPT="$(dirname "${BASH_SOURCE[0]}")/class-timing.mjs"
class_timing_begin() {
  CT_DIR="${ROOT:-$PWD}/artifacts/test-results/junit"
  rm -rf "$CT_DIR"
  mkdir -p "$CT_DIR"
}
# `class_timing_flags <run name>`: sets CT_FLAGS to the report options (empty when off).
class_timing_flags() {
  CT_FLAGS=()
  [[ -z "$CT_DIR" ]] || CT_FLAGS=(--report-xunit-junit --report-xunit-junit-filename "$1.junit.xml" --results-directory "$CT_DIR")
}
# `class_timing_report [<report path> [<tsv>]]`: the summary of CT_DIR, or of the given report.
class_timing_report() {
  local source="${1:-$CT_DIR}" tsv="${2:-${ROOT:-$PWD}/artifacts/test-results/class-timing.tsv}"
  [[ -z "$source" ]] || node "$CT_SCRIPT" --out "$tsv" "$source" ||
    echo "   class timing summary unavailable" >&2
  CT_DIR=""
}
class_timing_report_isolation() {
  class_timing_report "$ROOT/artifacts/test-results/isolation.junit.xml" "$ROOT/artifacts/test-results/class-timing-isolation.tsv"
}

# `format_duration <seconds>`: "<m>m<ss>s".
format_duration() { printf '%dm%02ds' "$(($1 / 60))" "$(($1 % 60))"; }

# `reap_test_runs`: reports every run that has finished (one the shell no longer
# lists as running is already reaped, so `wait` returns its saved status at once) and sets
# PT_FAILED when one failed. Echoes nothing else.
reap_test_runs() {
  local i status elapsed alive
  # The shell's own list of running children: a recycled pid of a reaped child
  # never matches, which `kill -0` alone could not tell.
  alive=" $(jobs -pr | tr '\n' ' ') "
  for i in "${!PT_PIDS[@]}"; do
    [[ -n "${PT_PIDS[$i]}" ]] || continue
    [[ "$alive" == *" ${PT_PIDS[$i]} "* ]] && continue
    status=0
    wait "${PT_PIDS[$i]}" || status=$?
    elapsed="$(format_duration "$((SECONDS - PT_STARTS[i]))")"
    if [[ "$status" -eq 0 ]]; then
      echo "== ${PT_NAMES[$i]}: passed ($(test_log_total "${PT_FILES[$i]}") tests, $elapsed)"
    else
      echo "== ${PT_NAMES[$i]}: FAILED (exit $status, $elapsed)"
      PT_FAILED=1
      cat "${PT_FILES[$i]}"
    fi
    PT_PIDS[$i]=""
  done
}

# `running_test_runs`: how many background runs are still alive.
running_test_runs() {
  local pid n=0
  for pid in ${PT_PIDS[@]+"${PT_PIDS[@]}"}; do [[ -z "$pid" ]] || n=$((n + 1)); done
  echo "$n"
}

# `start_test_run <name> <log> <dotnet test args...>`: one background run. With a
# cap it first waits, polling (no `wait -n` in macOS bash 3.2), for a free slot.
start_test_run() {
  local name="$1" log="$2"
  shift 2
  while [[ "$PT_CAP" -gt 0 && "$(running_test_runs)" -ge "$PT_CAP" ]]; do
    sleep 0.2
    reap_test_runs
  done
  class_timing_flags "$name"
  dotnet test --no-build -c Release --no-progress "$@" ${CT_FLAGS[@]+"${CT_FLAGS[@]}"} >"$log" 2>&1 &
  PT_PIDS+=("$!") PT_NAMES+=("$name") PT_FILES+=("$log") PT_STARTS+=("$SECONDS")
}

# `start_tenancy_shards <shards> <logs dir> <classes>`: one run per non-empty shard.
start_tenancy_shards() {
  local shards="$1" logs="$2" class index i
  local -a members
  while IFS= read -r class; do
    index="$(( $(printf '%s' "$class" | cksum | cut -d' ' -f1) % shards ))"
    printf '%s\n' "$class" >>"$logs/classes.$index"
  done <<<"$3"
  for ((i = 0; i < shards; i++)); do
    [[ -f "$logs/classes.$i" ]] || continue
    members=()
    while IFS= read -r class; do members+=("$class"); done <"$logs/classes.$i"
    echo "   tenancy shard $i: ${#members[@]} classes"
    start_test_run "tenancy-$i" "$logs/tenancy-$i.log" \
      --project "$TENANCY_PROJECT" --filter-class "${members[@]}"
  done
}

# Stops the runs still going and drops the logs (they hold container credentials).
# Called on every exit path of run_parallel_tests, including an interrupt: a run
# left behind would keep loading the host after the gate gave up its turn.
stop_parallel_tests() {
  local pid
  for pid in ${PT_PIDS[@]+"${PT_PIDS[@]}"}; do [[ -z "$pid" ]] || kill "$pid" 2>/dev/null || true; done
  PT_PIDS=()
  [[ -z "$PT_LOGS" ]] || rm -rf "$PT_LOGS"
  PT_LOGS=
}

run_parallel_tests() {
  local shards="${EVIDGATE_TENANCY_SHARDS:-3}" logs listing discovered classes project name
  local total=0 failed=0 i saved_traps cap="${EVIDGATE_TEST_PARALLEL:-0}"
  PT_PIDS=() PT_NAMES=() PT_FILES=() PT_STARTS=() PT_FAILED=0
  if ! [[ "$shards" =~ ^[1-9][0-9]?$ ]] || [[ "$shards" -gt 16 ]]; then
    echo "verify: EVIDGATE_TENANCY_SHARDS must be an integer from 1 to 16, got '$shards'" >&2
    return 2
  fi
  if [[ -n "${EVIDGATE_TEST_PARALLEL:-}" ]] && { ! [[ "$cap" =~ ^[1-9][0-9]?$ ]] || [[ "$cap" -gt 16 ]]; }; then
    echo "verify: EVIDGATE_TEST_PARALLEL must be an integer from 1 to 16, got '$cap'" >&2
    return 2
  fi
  PT_CAP="$cap"
  [[ "$cap" -eq 0 ]] || echo "   test processes capped at $cap at a time"
  listing="$(dotnet test --no-build -c Release --project "$TENANCY_PROJECT" --list-tests 2>&1)" || {
    printf '%s\n' "$listing" >&2
    return 1
  }
  discovered="$(printf '%s\n' "$listing" | sed -n 's/^Discovered \([0-9][0-9]*\) tests in assembly.*/\1/p' | tail -n 1)"
  classes="$(tenancy_classes "$listing")"
  if [[ -z "$discovered" || "$discovered" -eq 0 || -z "$classes" ]]; then
    echo "verify: test discovery of $TENANCY_PROJECT found no test" >&2
    return 1
  fi
  logs="$(mktemp -d)"
  PT_LOGS="$logs"
  saved_traps="$(trap -p EXIT INT TERM)"
  trap stop_parallel_tests EXIT
  trap 'exit 143' INT TERM
  # The compiler server and MSBuild nodes outlive the build stage and would hold
  # memory next to the test processes; a failed shutdown only costs that memory.
  # It stops every build server of this user, so run one gate per host.
  if dotnet build-server shutdown >/dev/null 2>&1; then
    echo "   build servers stopped before the tests"
  else
    echo "   build-server shutdown failed; continuing"
  fi
  class_timing_begin
  start_tenancy_shards "$shards" "$logs" "$classes"
  for project in tests/*/*.csproj; do
    [[ "$project" == "$TENANCY_PROJECT"/* ]] && continue
    name="$(basename "$(dirname "$project")")"
    start_test_run "$name" "$logs/$name.log" --project "$project" --minimum-expected-tests 1
  done
  while [[ "$(running_test_runs)" -gt 0 ]]; do
    sleep 0.2
    reap_test_runs
  done
  failed="$PT_FAILED"
  for i in "${!PT_NAMES[@]}"; do
    if [[ "${PT_NAMES[$i]}" == tenancy-* ]]; then
      total=$((total + $(test_log_total "${PT_FILES[$i]}")))
    fi
  done
  echo "   tenancy: $total tests run in $shards shard(s), $discovered discovered"
  if [[ "$total" -ne "$discovered" ]]; then
    echo "verify: the tenancy shards ran $total tests but discovery found $discovered" >&2
    failed=1
  fi
  class_timing_report
  stop_parallel_tests
  trap - EXIT INT TERM
  eval "$saved_traps"
  return "$failed"
}

# --- sharded fuzz stage -------------------------------------------------------
# The harness is one single-threaded loop bounded by a wall budget, so 600 s
# leaves the other cores idle. The work is split over N processes
# (N = min(4, cores), or EVIDGATE_FUZZ_SHARDS). Concurrent shards share cores,
# caches and memory (LAN host: ~91k inputs/s per shard at N=4 against ~149k/s for
# one), so a shard runs ceil(budget * 9 / (5 * N)) seconds for N >= 2: N=4 gives
# 270 s each and ~10 % more inputs than the serial 600 s; N=1 keeps the budget. Shard i
# drives seed base+i and writes under <findings dir>/shard-i, inside the path the
# nightly job uploads. Replay a finding from its shard's printed seed alone:
#   EVIDGATE_FUZZ_SEED=<shard seed> EVIDGATE_FUZZ_SHARDS=1 \
#     EVIDGATE_FUZZ_BUDGET_SECONDS=<shard budget> scripts/verify.sh --stage fixtures-fuzz
# A failed shard or a finding fails the stage; shard iteration counts are summed.

# `fuzz_shard_count`: the shard count, 1 to 16; prints nothing and fails on a bad override.
fuzz_shard_count() {
  local n="${EVIDGATE_FUZZ_SHARDS:-}" cores
  if [[ -z "$n" ]]; then
    cores="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)"
    [[ "$cores" =~ ^[1-9][0-9]*$ ]] || cores=1
    n=$((cores < 4 ? cores : 4))
  fi
  if ! [[ "$n" =~ ^[1-9][0-9]?$ ]] || [[ "$n" -gt 16 ]]; then
    echo "verify: EVIDGATE_FUZZ_SHARDS must be an integer from 1 to 16, got '$n'" >&2
    return 1
  fi
  echo "$n"
}

# `fuzz_shard_seed <base> <index>`: shard 0 keeps the base verbatim (the harness takes any
# unsigned 64-bit seed); further shards add the index, which bash can only do below 10^18.
fuzz_shard_seed() {
  if [[ "$2" -eq 0 ]]; then
    echo "$1"
  elif [[ "$1" =~ ^[0-9]{1,18}$ ]]; then
    echo $((10#$1 + $2))
  else
    echo "verify: EVIDGATE_FUZZ_SEED must be a decimal below 10^18 to run more than one shard" >&2
    return 1
  fi
}

# `claim_fuzz_findings <root>`: makes the findings root and clears the shard dirs of an
# earlier run, but only where this stage left its marker: a directory it did not create
# is never emptied.
claim_fuzz_findings() {
  mkdir -p "$1"
  if [[ -f "$1/.fuzz-shards" ]]; then
    rm -rf "$1"/shard-*
  elif compgen -G "$1/shard-*" >/dev/null; then
    echo "verify: $1 already holds shard-* entries this stage did not create; point" \
      "EVIDGATE_FUZZ_FINDINGS_DIR at an empty or new directory" >&2
    return 1
  else
    : >"$1/.fuzz-shards"
  fi
}

# `run_fuzz_shards <assembly>...`: the Fuzz-trait run of each assembly, once per shard.
run_fuzz_shards() {
  local shards total per base project i seed dir iterations sum=0 saved_traps
  shards="$(fuzz_shard_count)" || return 2
  total="${EVIDGATE_FUZZ_BUDGET_SECONDS:?}"
  base="${EVIDGATE_FUZZ_SEED:?}"
  if ! [[ "$total" =~ ^[0-9]{1,9}$ && "$base" =~ ^[0-9]{1,20}$ ]] || [[ "$((10#$total))" -eq 0 ]]; then
    echo "verify: EVIDGATE_FUZZ_BUDGET_SECONDS must be a positive decimal and EVIDGATE_FUZZ_SEED a decimal" >&2
    return 2
  fi
  total=$((10#$total))
  if [[ "$shards" -eq 1 ]]; then per=$total; else per=$(((total * 9 + 5 * shards - 1) / (5 * shards))); fi
  claim_fuzz_findings "$EVIDGATE_FUZZ_FINDINGS_DIR" || return 2
  for ((i = 0; i < shards; i++)); do fuzz_shard_seed "$base" "$i" >/dev/null || return 2; done
  check_trait_coverage Fuzz "$@"
  PT_PIDS=() PT_NAMES=() PT_FILES=() PT_STARTS=() PT_FAILED=0 PT_CAP=0
  PT_LOGS="$(mktemp -d)"
  saved_traps="$(trap -p EXIT INT TERM)"
  trap stop_parallel_tests EXIT
  trap 'exit 143' INT TERM
  echo "   $shards fuzz shard(s) of ${per}s each ($total s budget in total), base seed $base"
  for project in "$@"; do
    for ((i = 0; i < shards; i++)); do
      seed="$(fuzz_shard_seed "$base" "$i")"
      dir="$EVIDGATE_FUZZ_FINDINGS_DIR/shard-$i"
      echo "   fuzz shard $i: seed $seed budget ${per}s findings $dir"
      EVIDGATE_FUZZ_SEED="$seed" EVIDGATE_FUZZ_BUDGET_SECONDS="$per" EVIDGATE_FUZZ_FINDINGS_DIR="$dir" \
        start_test_run "fuzz-$i" "$PT_LOGS/fuzz-$i.log" \
        --project "$project" --filter-trait "Category=Fuzz" --minimum-expected-tests 1
    done
  done
  while [[ "$(running_test_runs)" -gt 0 ]]; do
    sleep 0.2
    reap_test_runs
  done
  for ((i = 0; i < shards; i++)); do
    iterations="$(sed -n 's/^ *"Iterations": *\([0-9][0-9]*\),\{0,1\}$/\1/p' \
      "$EVIDGATE_FUZZ_FINDINGS_DIR/shard-$i/run.json" 2>/dev/null | tail -n 1)" || iterations=""
    if [[ -z "$iterations" ]]; then
      echo "verify: fuzz shard $i wrote no run.json with an iteration count" >&2
      PT_FAILED=1
      continue
    fi
    echo "   fuzz shard $i: $iterations inputs"
    sum=$((sum + iterations))
  done
  echo "   fuzz: $sum inputs in total over $shards shard(s)"
  stop_parallel_tests
  trap - EXIT INT TERM
  eval "$saved_traps"
  return "$PT_FAILED"
}
