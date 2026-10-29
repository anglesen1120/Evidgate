#!/usr/bin/env bash
# FR-01-1 (plans/01-foundation.md §2): the same checks CI runs (01.7.1), in the
# same order. Stops at the first failure.
# Prerequisites only: .NET 10 SDK + Node 26 on PATH, dependencies restored once
# (dotnet restore/npm ci — this script never installs anything itself).
#
# AC2 (01.7.1-T1): CI does not copy these commands, it calls them —
#   ./scripts/verify.sh --stage restore --stage build ...
# `--list-stages` prints the canonical stage names, which is what
# scripts/check-ci-parity.sh diffs the workflow against, so a stage added here
# and not wired into ci.yml fails the parity gate.
set -euo pipefail

# BASH_SOURCE, not $0: the stage-selection tests source this file, and $0 there
# is the test's own shell, not this script.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Canonical stage order: the order every stage list is validated and reported in,
# and the order a bare run follows within its tier. A bare run does not run every
# stage — scripts/gate_scope.py picks the subset the change set can fail (see the
# "path-scoped tiers" block below); `--stage` runs a subset in the order given.
ALL_STAGES=(
  restore
  build
  format
  test
  isolation
  fixtures-hostile
  fixtures-fuzz
  tsc-build
  tsc-spec
  vitest
  eslint
  prettier
  check-layout
  check-sources
  check-adr-matrix
  check-adr
  check-links
  check-contract-conventions
  check-env-matrix
  check-dor-security
  check-threat-model
  check-ci-parity
  check-action-pins
  check-ci-recipe
  check-codegen-drift
  check-permissions-drift
  check-rls
  check-review-blockers
  check-gate-scope
  image-pins
  images
  licence-scan
  audit-deps
  sbom
)

stage() { echo "== $1"; }

usage() {
  cat <<'EOF'
usage: scripts/verify.sh [--stage <name>]... | --list-stages

Runs the full local gate (FR-01-1). With no --stage the stages run in the order
below, narrowed to the ones the change set can fail: a documentation-only change
runs the formatter and the check stages, a web/script-only change also skips the
.NET and Docker stages, and anything else still runs everything (the tier is
computed by scripts/gate_scope.py, fail closed; EVIDGATE_GATE_SCOPE may widen it,
never narrow it). An explicit --stage list is never narrowed.
CI (01.7.1) passes --stage explicitly so both run one command list.

stages (in canonical order):
EOF
  printf '  %s\n' "${ALL_STAGES[@]}"
}

run_stage() {
  case "$1" in
  restore)
    stage "restore (locked mode)"
    dotnet restore --locked-mode Evidgate.slnx
    ;;
  build)
    stage "build (Release, warnings as errors)"
    dotnet build -c Release --no-restore Evidgate.slnx
    ;;
  format)
    stage "dotnet format (verify only)"
    dotnet format Evidgate.slnx --verify-no-changes --no-restore
    ;;
  test)
    # EVIDGATE_VERIFY_SKIP_DOCKER=1 skips the Category=Docker integration tests
    # (Testcontainers). Only smoke-clean-build.sh sets it: its container has no
    # Docker daemon, and mounting the host socket would hand it the host. Exit code
    # 8 means an assembly holds only Docker tests, so none ran there.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "dotnet test (unit + architecture; Docker integration tests skipped)"
      dotnet test --no-build -c Release --solution Evidgate.slnx \
        --filter-not-trait "Category=Docker" --ignore-exit-code 8
    else
      stage "dotnet test (unit, architecture, Docker integration)"
      dotnet test --no-build -c Release --solution Evidgate.slnx
    fi
    ;;
  isolation)
    # 02.9.1-T3.2 (AC6, T48): the M1 isolation gate — the RLS/composite-FK/API
    # isolation suite as its own stage, so CI can point at one job as proof that
    # tenant isolation held for the merge. The assembly is run directly because a
    # project-scoped `dotnet test <csproj>` discovers zero tests under
    # Microsoft.Testing.Platform (01.4.2-T1.6); the built runner is the xUnit v3
    # in-process console runner, whose own `-result-junit` option writes the T48
    # JUnit report (the MTP `--report-*` flags exist only under `dotnet test`).
    # Every class carries [Trait("Category","Docker")], so
    # EVIDGATE_VERIFY_SKIP_DOCKER=1 (smoke-clean-build.sh only) skips the stage
    # exactly as the `test` stage skips its Docker tests.
    # The `test` stage stays unchanged and still runs this suite inside the
    # solution: excluding one project from `dotnet test --solution` needs a
    # solution filter, and a trait filter cannot tell assemblies apart — it would
    # leave the assembly with zero runnable tests (exit 8), the very check the
    # skip path relies on. The duplicate run is accepted rather than weakening it.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "isolation (skipped: EVIDGATE_VERIFY_SKIP_DOCKER=1)"
    else
      stage "isolation (M1 gate: RLS, composite FK, API isolation; JUnit report)"
      mkdir -p artifacts/test-results
      # $ROOT-absolute: the console runner resolves the report path against the
      # process cwd, which `dotnet run` does not guarantee to be the repo root.
      dotnet run --project tests/Isolation.Tests -c Release --no-build -- \
        -result-junit "$ROOT/artifacts/test-results/isolation.junit.xml"
    fi
    ;;
  fixtures-hostile)
    # 06.3.2-T1.2/T1.3 (AC2/AC3): the committed hostile-input corpus and its mutation smoke, run on the built
    # assembly and selected by [Trait("Category","Hostile")]. The corpus drives the parser profile in-process
    # (XML reports, archives) and the completion's media sniff/size cap with fakes, so the stage needs no Docker
    # daemon and the assembly's Docker suites never start. `--ignore-exit-code 8` follows the `test` stage's skip
    # path: every other assembly of the solution holds no Hostile test, which MTP reports as exit code 8.
    stage "dotnet test (hostile-input fixtures and mutation smoke)"
    dotnet test --no-build -c Release --solution Evidgate.slnx \
      --filter-trait "Category=Hostile" --ignore-exit-code 8
    ;;
  fixtures-fuzz)
    # 06.3.2-T2.1 (AC4): the seeded-mutation fuzz harness (HostileFuzzTests, FuzzPromotionTests) over
    # IParsing.ParseXmlReport/EnumerateArchive, selected by [Trait("Category","Fuzz")] and run on the built
    # assembly, so the stage needs no Docker daemon. The ten-minute budget is set HERE, so the `test` stage — the
    # only other stage that selects the Fuzz trait — runs the harness's few-second smoke default (a caller that
    # already exported EVIDGATE_FUZZ_BUDGET_SECONDS keeps its value); the `fixtures-hostile` stage filters
    # Category=Hostile only and never runs this harness. The seed is set HERE too: one per run, from the CI run id
    # (or the clock outside CI), so consecutive nights do not replay one PRNG stream; it is echoed below so a red
    # run can be replayed with EVIDGATE_FUZZ_SEED=<value>. Findings go under artifacts/fuzz-findings, which the
    # nightly job uploads. `--ignore-exit-code 8` follows the `test` stage's skip path: every other assembly of the
    # solution holds no Fuzz test, which MTP reports as exit code 8.
    export EVIDGATE_FUZZ_BUDGET_SECONDS="${EVIDGATE_FUZZ_BUDGET_SECONDS:-600}"
    export EVIDGATE_FUZZ_FINDINGS_DIR="${EVIDGATE_FUZZ_FINDINGS_DIR:-$ROOT/artifacts/fuzz-findings}"
    export EVIDGATE_FUZZ_SEED="${EVIDGATE_FUZZ_SEED:-${GITHUB_RUN_ID:-$(date +%s)}}"
    stage "dotnet test (fuzz harness, ${EVIDGATE_FUZZ_BUDGET_SECONDS}s budget)"
    echo "   EVIDGATE_FUZZ_SEED=${EVIDGATE_FUZZ_SEED}"
    dotnet test --no-build -c Release --solution Evidgate.slnx \
      --filter-trait "Category=Fuzz" --ignore-exit-code 8
    ;;
  tsc-build)
    # tsc -b of the root tsconfig.json references: packages/* AND apps/web — the
    # --workspaces variant skips apps/web (no build script) and would let it escape
    # the strict gate.
    stage "tsc -b (solution references)"
    npm run build
    ;;
  tsc-spec)
    # 01.6.1-T1.2: `tsc -b` covers the emitting workspaces only (every tsconfig.json excludes
    # the specs and the Vitest configs) and Vitest does not type-check, so the no-emit spec
    # projects are the only gate for `*.test.ts` and the Vitest configs. Add a line whenever a
    # workspace gains a tsconfig.spec.json.
    stage "tsc -p (spec and vitest config projects)"
    for spec_tsconfig in tsconfig.spec.json apps/web/tsconfig.spec.json packages/*/tsconfig.spec.json; do
      echo "   $spec_tsconfig"
      npx tsc -p "$spec_tsconfig"
    done
    ;;
  vitest)
    stage "vitest"
    npm run test
    ;;
  eslint)
    stage "eslint"
    npm run lint
    ;;
  prettier)
    stage "prettier"
    npm run format:check
    ;;
  check-layout)
    stage "check-layout"
    scripts/check-layout.sh
    ;;
  check-sources)
    stage "check-sources"
    scripts/check-sources.sh
    ;;
  check-adr-matrix)
    stage "check-adr-matrix"
    scripts/check-adr-matrix.sh
    ;;
  check-adr)
    stage "check-adr"
    scripts/check-adr.sh
    ;;
  check-links)
    stage "check-links"
    scripts/check-links.sh
    ;;
  check-contract-conventions)
    stage "check-contract-conventions"
    scripts/check-contract-conventions.sh
    ;;
  check-env-matrix)
    # AC1 (10.1.1-T1.1): every consumed EVIDGATE_* key has a matrix row and a
    # per-environment origin; the self-test proves the lint still rejects.
    # AC3 (10.1.1-T2.1): no literal secret in source, Dockerfiles or infra/;
    # the self-test proves the scan still rejects an injected dummy.
    stage "check-env-matrix"
    node scripts/check-env-matrix.mjs --self-test
    node scripts/check-env-matrix.mjs
    scripts/check-no-secret-literals.sh --self-test
    scripts/check-no-secret-literals.sh
    ;;
  check-dor-security)
    # AC4 (11.1.1-T2): feature plans added or changed in the change set answer
    # the four DoR security questions.
    stage "check-dor-security"
    scripts/check-dor-security.sh
    ;;
  check-threat-model)
    # AC1/AC2/AC3/AC7 (11.1.1-T1.3): the threat-model completeness lint. The
    # self-test proves every negative fixture still fails and the ok fixture
    # passes; then the real model (docs/security/threat-model-v1.md) must be green.
    stage "check-threat-model"
    scripts/check-threat-model.sh --self-test
    scripts/check-threat-model.sh
    ;;
  check-ci-parity)
    # AC2 (01.7.1-T1.4): the workflow must run these stages and nothing else from
    # the same set — a raw `dotnet test` next to a `--stage test` call fails here.
    stage "check-ci-parity"
    scripts/check-ci-parity.sh
    ;;
  check-action-pins)
    # AC6 (01.7.1-T4.1, SHA half): no `uses:` may reference a mutable tag.
    stage "check-action-pins"
    scripts/check-action-pins.sh
    ;;
  check-ci-recipe)
    # 11.6.1-T2 (hand-over from 11.2.1-T1): the customer fork-safe recipe lint.
    # The self-test proves every rule still rejects its negative and bypass
    # fixtures and the shipped recipe passes; then the `evidgate quickstart`
    # GitHub template (kept equal to the renderer by a vitest test) must pass.
    stage "check-ci-recipe"
    scripts/lint-ci-recipe.sh --self-test
    scripts/lint-ci-recipe.sh docs/quickstart/github-actions.yml
    ;;
  check-codegen-drift)
    # AC1 (01.3.2-T1.3): regenerate the SDK and fail on any diff — the spec is
    # the only thing humans edit.
    stage "check-codegen-drift"
    scripts/check-codegen-drift.sh
    ;;
  check-permissions-drift)
    # 02.2.1-T1: Permissions.g.cs is regenerated from contracts/permissions.json.
    stage "check-permissions-drift"
    scripts/check-permissions-drift.sh
    ;;
  check-rls)
    # 02.6.1-T2: every migrated table is tenant-isolated or on the exception list,
    # every SECURITY DEFINER function is hardened; the lint's negative fixtures run too.
    stage "check-rls"
    python3 scripts/check-rls.py
    python3 -m unittest scripts/tests/test_check_rls.py
    ;;
  check-review-blockers)
    # The REVIEW.md blocking rules a grep can see, read on the lines a change set
    # ADDS, so debt already on the base never fails a local run. It reads commits
    # (`git diff base...HEAD`), never the working tree: commit before running it,
    # or the change is not the one being scanned. The base is
    # EVIDGATE_REVIEW_BASE when CI sets it (the merge commit's first parent, or
    # the ref a push started from) and otherwise the first of origin/develop,
    # develop, origin/main, main that resolves — the clean-clone smoke build has
    # only a local `develop`. No candidate at all makes the scan exit 2, which
    # fails this stage: a shallow clone is a red gate, never a green one.
    # EVIDGATE_REVIEW_DIFF=off skips the diff scan and keeps the ratchet: CI sets
    # it for a landing that is not on develop, because develop's own debt (the
    # 0053/0054 D1 policies) is not a promotion's change set and would fail every
    # promotion for it.
    # The U1 half is a ratchet over the whole head tree against a committed
    # baseline, because those comment ids predate the rule. Bytecode is off so
    # the fixture run leaves no .pyc beside the tracked test tree.
    stage "check-review-blockers"
    if [[ "${EVIDGATE_REVIEW_DIFF:-on}" == "off" ]]; then
      echo "check-review-blockers: diff scan off (EVIDGATE_REVIEW_DIFF=off): not a landing on develop, so the ratchet runs alone"
    else
      PYTHONDONTWRITEBYTECODE=1 python3 scripts/check-review-blockers.py "${EVIDGATE_REVIEW_BASE:-}" HEAD
    fi
    PYTHONDONTWRITEBYTECODE=1 python3 scripts/check-review-blockers.py --u1-ratchet HEAD
    # Name the fixture files instead of discovering them: `unittest discover`
    # with no match prints "Ran 0 tests ... OK", so a renamed split would turn
    # this stage into a silent green. The pattern must not match the shared
    # helper, which is deliberately not named test_*.
    shopt -s nullglob
    blockers_fixtures=(scripts/tests/test_*blockers*.py)
    shopt -u nullglob
    if (( ${#blockers_fixtures[@]} == 0 )); then
      echo "check-review-blockers: no scripts/tests/test_*blockers*.py fixtures found; the split was renamed or deleted" >&2
      exit 2
    fi
    # PYTHONPATH names both directories a fixture imports from — the
    # `review_blockers` package and the shared helper — because `unittest` given
    # file paths imports them by module name from cwd; else the verdict would
    # depend on import order or fail outright.
    PYTHONPATH="${ROOT}/scripts:${ROOT}/scripts/tests${PYTHONPATH:+:${PYTHONPATH}}" \
      PYTHONDONTWRITEBYTECODE=1 python3 -m unittest "${blockers_fixtures[@]}"
    ;;
  check-gate-scope)
    # The path-scoped tier classifier's fixtures (test_gate_scope.py): which
    # stage subset a change set needs, the allow-lists, the fail-closed paths and
    # the mirror of ALL_STAGES; and the consumer's (test_verify_scope.py): only a
    # docs/no-dotnet tier with a known, non-empty list may narrow a bare run.
    # Bytecode is off so the run leaves no .pyc in the tracked test tree; the
    # files are named rather than discovered, because `unittest` with nothing to
    # run prints "Ran 0 tests ... OK" and a rename would turn this stage green.
    stage "check-gate-scope"
    PYTHONPATH="${ROOT}/scripts:${ROOT}/scripts/tests${PYTHONPATH:+:${PYTHONPATH}}" \
      PYTHONDONTWRITEBYTECODE=1 python3 -m unittest \
      scripts/tests/test_gate_scope.py scripts/tests/test_verify_scope.py
    ;;
  image-pins)
    # 10.2.1-T1.1: builder tag == global.json SDK, digest-pinned bases, the three
    # Dockerfiles and ignore lists agree. No Docker.
    stage "image-pins"
    scripts/build-images.sh --check-pins
    ;;
  images)
    # AC1/AC5 (10.2.1-T1.1/T1.2): build api/worker/migrator for the host platform,
    # then migrate a throwaway PostgreSQL with the Migrator image and probe the Api.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "images (skipped: EVIDGATE_VERIFY_SKIP_DOCKER=1)"
    else
      stage "images"
      scripts/build-images.sh --with-db
    fi
    ;;
  licence-scan)
    # AC4 (01.7.1-T2): deny-list enforced for npm + NuGet; exceptions are ADR ids.
    stage "licence-scan"
    # --report writes artifacts/licence-report.json, which the ci.yml
    # `Upload licence report` step consumes (QA D1: an unargumented call left
    # that upload empty while the job stayed green).
    # 11.6.1-T1 (AC2): the self-test first proves planted AGPL/unlicensed npm and
    # NuGet packages still fail with name and licence in the log.
    scripts/licence-scan.sh --self-test
    scripts/licence-scan.sh --report artifacts/licence-report.json
    ;;
  audit-deps)
    # AC6 (01.7.1-T4.2): vulnerability gates, exceptions.json is ADR-keyed.
    stage "audit-deps"
    # 11.6.1-T1 (AC1): the self-test also proves a stale package-lock.json fails
    # `npm ci` and a stale packages.lock.json fails `dotnet restore --locked-mode`.
    scripts/audit-deps.sh --self-test
    scripts/audit-deps.sh
    ;;
  sbom)
    # AC7 (01.7.1-T4.2): CycloneDX SBOMs for both ecosystems into artifacts/sbom/.
    stage "sbom"
    scripts/sbom.sh
    ;;
  esac
}

# --- argument parsing ---------------------------------------------------------
# Everything below is a function and the run is guarded at the end of the file,
# so sourcing this script defines the selection without executing a stage.
selected=() scope_tier="" scope_reason="" scope_changed="" scope_base=""
scope_stages="" scope_skipped="" scope_notes=()

# `in_list <name> <names...>`: membership over an explicit list.
in_list() {
  local needle="$1" item
  shift
  for item in "$@"; do [[ "$item" == "$needle" ]] && return 0; done
  return 1
}

fail_usage() {
  echo "verify: $1" >&2
  usage >&2
  exit 2
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
    --stage)
      if [[ $# -lt 2 ]]; then fail_usage "--stage needs a stage name"; fi
      # An explicit list is the caller's own verdict: every named stage runs,
      # whatever the tier would have chosen; an unknown name is a caller typo.
      if ! in_list "$2" "${ALL_STAGES[@]}"; then fail_usage "unknown stage '$2'"; fi
      selected+=("$2")
      shift 2
      ;;
    --list-stages) printf '%s\n' "${ALL_STAGES[@]}"; exit 0 ;;
    -h | --help) usage; exit 0 ;;
    *) fail_usage "unknown argument '$1'" ;;
    esac
  done
}

# --- path-scoped tiers --------------------------------------------------------
# No --stage means "whatever this change set can fail": scripts/gate_scope.py is
# the only decider (and where it is tested), this block just consumes its report.
# A classifier that crashes, prints nothing or omits the tier is the full gate.
#
# The FIRST line for a key wins: a duplicate may repeat a fact, never replace it.
read_scope() {
  local line
  while IFS= read -r line; do
    case "$line" in
    "tier: "*) if [[ -z "$scope_tier" ]]; then scope_tier="${line#tier: }"; fi ;;
    "reason: "*) if [[ -z "$scope_reason" ]]; then scope_reason="${line#reason: }"; fi ;;
    "changed: "*) if [[ -z "$scope_changed" ]]; then scope_changed="${line#changed: }"; fi ;;
    "base: "*) if [[ -z "$scope_base" ]]; then scope_base="${line#base: }"; fi ;;
    "stages: "*) if [[ -z "$scope_stages" ]]; then scope_stages="${line#stages: }"; fi ;;
    "skipped: "*) if [[ -z "$scope_skipped" ]]; then scope_skipped="${line#skipped: }"; fi ;;
    "note: "*) scope_notes+=("${line#note: }") ;;
    esac
  done <<<"$1"
}

# Turn the parsed report into the run list. Only a `docs` or `no-dotnet` tier
# with a non-empty list of known stages may narrow a bare run; an unknown tier,
# an empty or invented stage list, or a missing key is the full gate.
plan_selection() {
  local name narrowed="" candidates=()
  case "$scope_tier" in
  docs | no-dotnet) if [[ -n "$scope_stages" ]]; then narrowed=1; fi ;;
  esac
  if [[ -n "$narrowed" ]]; then
    read -r -a candidates <<<"$scope_stages" || true
    for name in ${candidates[@]+"${candidates[@]}"}; do
      if ! in_list "$name" "${ALL_STAGES[@]}"; then narrowed=""; fi
    done
    if [[ "${#candidates[@]}" -eq 0 ]]; then narrowed=""; fi
  fi
  if [[ -n "$narrowed" ]]; then
    selected=("${candidates[@]}")
    echo "== gate scope: ${scope_tier} (${scope_reason}, ${scope_changed} changed path(s), base ${scope_base})"
    if [[ -n "$scope_skipped" ]]; then echo "   skipped by scope: ${scope_skipped}"; fi
    for note in ${scope_notes[@]+"${scope_notes[@]}"}; do echo "   note: ${note}"; done
  else
    scope_tier="full"
    echo "== gate scope: no usable tier/stage list (full gate)"
    selected=("${ALL_STAGES[@]}")
  fi
}

# The success line is a claim about the whole gate, so it is made only when every
# canonical stage is in the run list.
report_result() {
  local name
  for name in "${ALL_STAGES[@]}"; do
    if ! in_list "$name" ${selected[@]+"${selected[@]}"}; then
      echo "== verify: selected stages passed: ${selected[*]}"
      return 0
    fi
  done
  echo "== verify: all stages passed"
}

main() {
  local name report
  cd "$ROOT"
  parse_args "$@"
  if [[ "${#selected[@]}" -eq 0 ]]; then
    report="$(PYTHONDONTWRITEBYTECODE=1 python3 scripts/gate_scope.py)" || report=""
    read_scope "$report"
    plan_selection
  else
    echo "== gate scope: explicit (--stage given: no path filtering, every listed stage runs)"
  fi
  for name in "${selected[@]}"; do
    run_stage "$name"
  done
  report_result
}

# Run only when executed: a sourced copy stops after defining the selection.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
