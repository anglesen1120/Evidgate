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
  dotnet test --no-build -c Release --no-progress "$@" >"$log" 2>&1 &
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
  stop_parallel_tests
  trap - EXIT INT TERM
  eval "$saved_traps"
  return "$failed"
}
