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
# is the test's own shell, not this script. A piped run (`bash < scripts/verify.sh`)
# has no source path at all — the caller's shell feeds the bytes — so the root is
# the repository git names for the directory it was piped from, and the run guard
# at the end of the file calls `main` for that case too.
# Limitation: a piped run's text is never scanned for the disarmed-gate flag.
# bash consumes the script from stdin before any check runs, so no file is left to
# read. CI and every documented caller invoke the script by path, which is scanned.
if [[ -n "${BASH_SOURCE[0]:-}" ]]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
else
  ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi

# Both files are the gate: the stage table below and the guard/selection half in
# verify-guards.sh (split out to keep this file under the 500-line limit). The
# scans read both, and a piped run must start inside the repository, so neither
# file may be missing before anything else is defined.
if [[ ! -f "$ROOT/scripts/verify.sh" ]]; then
  echo "verify: cannot find scripts/verify.sh under '$ROOT': a piped run must start" \
    "inside the repository" >&2
  exit 2
fi
if [[ ! -f "$ROOT/scripts/verify-guards.sh" ]]; then
  echo "verify: cannot find scripts/verify-guards.sh next to scripts/verify.sh under" \
    "'$ROOT'" >&2
  exit 2
fi
# shellcheck source=scripts/verify-guards.sh
. "$ROOT/scripts/verify-guards.sh"

# Canonical stage order: the order every stage list is validated and reported in,
# and the order a bare run follows within its tier. A bare run does not run every
# stage — scripts/gate_scope.py picks the subset the change set can fail, and
# verify-guards.sh consumes that report; `--stage` runs a subset in the order
# given.
ALL_STAGES=(
  restore
  build
  format
  test
  isolation
  fixtures-hostile
  fixtures-fuzz
  parser-no-egress
  tsc-build
  tsc-spec
  vitest
  e2e-a11y
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
  check-review-infra
  check-gate-scope
  image-pins
  images
  licence-scan
  audit-deps
  sbom
)

stage() { echo "== $1"; }

# The test assemblies that carry a trait-selected stage. A stage cannot filter
# the whole solution: every assembly without the trait then runs zero tests, MTP
# reports that as exit code 8 for a run that did execute the trait's tests, and
# the only way to hide it is the flag `refuse_gate_flag` rejects.
# The assemblies are named instead, and `run_trait_stage` (verify-guards.sh) fails
# the stage when a `*.cs` file carrying the trait belongs to a project outside
# them, so a moved or reformatted test cannot silently drop out of the stage.
HOSTILE_ASSEMBLIES=(tests/Tenancy.IntegrationTests)
FUZZ_ASSEMBLIES=(tests/Tenancy.IntegrationTests)

usage() {
  cat <<'EOF'
usage: scripts/verify.sh [--stage <name>]... | --list-stages

Runs the full local gate (FR-01-1). With no --stage the stages run in the order
below, narrowed to the ones the change set can fail: a documentation-only change
runs the formatter and the check stages, a change confined to the review
deployment (infra/review, scripts/review) adds that stack's own checks, a
web/script-only change also skips the .NET and Docker stages, and anything else
still runs everything (the tier is
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
    # Docker daemon, and mounting the host socket would hand it the host. The
    # skip path runs one assembly per call: an assembly whose whole suite is
    # Docker tests runs none there, which MTP reports as exit code 8, and a
    # solution-wide call would turn that into one failed run. So 8 means "no test
    # left in this assembly", anything else is its own verdict, and a stage where
    # no assembly ran a test at all still fails.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "dotnet test (unit + architecture; Docker integration tests skipped)"
      tests_ran=0 && class_timing_begin
      for test_project in tests/*/*.csproj; do
        status=0
        class_timing_flags "$(basename "$(dirname "$test_project")")" # per-class timing reports
        dotnet test --no-build -c Release --project "$test_project" \
          --filter-not-trait "Category=Docker" ${CT_FLAGS[@]+"${CT_FLAGS[@]}"} || status=$?
        case "$status" in
        0) tests_ran=1 ;;
        8) echo "   $test_project: no test runs with Docker skipped (the assembly needs a daemon)" ;;
        *) class_timing_report; exit "$status" ;;
        esac
      done
      class_timing_report
      if [[ "$tests_ran" -eq 0 ]]; then
        echo "verify: the Docker-skipped test stage ran no test in any assembly" >&2
        exit 1
      fi
    else
      stage "dotnet test (unit, architecture, Docker integration; Tenancy sharded)"
      run_parallel_tests
    fi
    ;;
  isolation)
    # 02.9.1-T3.2 (AC6, T48): the M1 isolation gate — the RLS/composite-FK/API
    # isolation suite as its own stage, so CI can point at one job as proof that
    # tenant isolation held for the merge. The assembly is run directly because a
    # project-scoped `dotnet test <csproj>` discovers zero tests under
    # Microsoft.Testing.Platform (01.4.2-T1.6); the xUnit v3 console runner's own
    # `-result-junit` writes the T48 report (MTP `--report-*` is `dotnet test` only).
    # Every class carries [Trait("Category","Docker")], so
    # EVIDGATE_VERIFY_SKIP_DOCKER=1 (smoke-clean-build.sh only) skips the stage
    # exactly as the `test` stage skips its Docker tests.
    # The `test` stage still runs this suite inside the solution: a solution filter
    # would be needed to exclude one project, and a trait filter cannot tell
    # assemblies apart (zero runnable tests, exit 8, the check the skip path relies
    # on). The duplicate run is accepted. Slowest classes: class-timing-isolation.tsv.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "isolation (skipped: EVIDGATE_VERIFY_SKIP_DOCKER=1)"
    else
      stage "isolation (M1 gate: RLS, composite FK, API isolation; JUnit report)"
      status=0 && mkdir -p artifacts/test-results
      # $ROOT-absolute: the console runner resolves the report path against the
      # process cwd, which `dotnet run` does not guarantee to be the repo root.
      dotnet run --project tests/Isolation.Tests -c Release --no-build -- \
        -result-junit "$ROOT/artifacts/test-results/isolation.junit.xml" || status=$?
      class_timing_report_isolation && exit "$status"
    fi
    ;;
  fixtures-hostile)
    # 06.3.2-T1.2/T1.3 (AC2/AC3): the committed hostile-input corpus and its mutation smoke, run on the built
    # assembly and selected by [Trait("Category","Hostile")]. The corpus drives the parser profile in-process
    # (XML reports, archives) and the completion's media sniff/size cap with fakes, so the stage needs no Docker
    # daemon and the assembly's Docker suites never start. Only the trait-bearing assemblies run (see
    # `run_trait_stage`): a trait that selects nothing fails the stage instead of passing it.
    stage "dotnet test (hostile-input fixtures and mutation smoke)"
    run_trait_stage Hostile "${HOSTILE_ASSEMBLIES[@]}"
    ;;
  fixtures-fuzz)
    # Seeded-mutation fuzz harness over IParsing.ParseXmlReport/EnumerateArchive, selected by
    # [Trait("Category","Fuzz")] on the built assembly (no Docker). The ten-minute budget is set HERE so the `test`
    # stage keeps the few-second smoke default (a caller's exported budget wins); the seed is one per run (CI run
    # id, else the clock). `run_fuzz_shards` (verify-parallel-tests.sh) splits the work over concurrent shards and
    # prints each shard's seed and how to replay it; findings go under artifacts/fuzz-findings/shard-N (uploaded).
    export EVIDGATE_FUZZ_BUDGET_SECONDS="${EVIDGATE_FUZZ_BUDGET_SECONDS:-600}"
    export EVIDGATE_FUZZ_FINDINGS_DIR="${EVIDGATE_FUZZ_FINDINGS_DIR:-$ROOT/artifacts/fuzz-findings}"
    export EVIDGATE_FUZZ_SEED="${EVIDGATE_FUZZ_SEED:-${GITHUB_RUN_ID:-$(date +%s)}}"
    stage "dotnet test (fuzz harness, ${EVIDGATE_FUZZ_BUDGET_SECONDS}s budget)"
    echo "   EVIDGATE_FUZZ_SEED=${EVIDGATE_FUZZ_SEED}"
    run_fuzz_shards "${FUZZ_ASSEMBLIES[@]}"
    ;;
  tsc-build)
    # tsc -b of the root tsconfig.json references: packages/* AND apps/web — the
    # --workspaces variant skips apps/web (no build script) and would let it escape
    # the strict gate.
    stage "tsc -b (solution references)"
    npm run build
    ;;
  parser-no-egress)
    # The parser child's no-egress test skips on any host that can connect out, so it is run here from the
    # built Release output inside a container started with `--network none` and must report passed, not
    # skipped (scripts/parser-no-egress.sh). Needs the `build` stage and a Docker daemon.
    if [[ "${EVIDGATE_VERIFY_SKIP_DOCKER:-}" == "1" ]]; then
      stage "parser-no-egress (skipped: EVIDGATE_VERIFY_SKIP_DOCKER=1)"
    else
      stage "parser-no-egress (parser child in a --network none container)"
      scripts/parser-no-egress.sh
    fi
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
  e2e-a11y)
    # The browser axe suite (WCAG 2.2 AA over every built flow, en and vi) on the
    # fixtures build. The Vitest axe specs (`*.a11y.spec.ts`) need no stage of their
    # own: the `vitest` stage's `ng test` already runs them. No retries and no
    # ignore flag; chrome must be installed (`npx playwright install chrome`).
    stage "e2e-a11y (browser axe suite, fixtures build)"
    npm run test:e2e:a11y -w apps/web
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
  check-review-infra)
    # The review deployment's own checks, none of which needs Docker: the nginx
    # header generator and web-start tests, the pin, inlined-file, nginx-phase,
    # restart and container-policy checks (`--static-only` leaves out the two
    # that render compose with docker), then the deploy scripts' offline suite.
    stage "check-review-infra"
    bash infra/review/check.sh --static-only
    bash scripts/review/tests/run.sh
    ;;
  check-gate-scope)
    # The path-scoped tier classifier's fixtures (test_gate_scope.py): which
    # stage subset a change set needs, the allow-lists, the fail-closed paths and
    # the mirror of ALL_STAGES; the reason and report lines it prints
    # (test_gate_scope_reasons.py); the consumer's (test_verify_scope.py): only a
    # docs/no-dotnet tier with a known, non-empty list may narrow a bare run, and
    # the scope line a full tier prints; the gate's own refusals
    # (test_verify_refusal.py): a script or its sourced guard helper that hands
    # dotnet the ignore flag, and a trait stage whose assemblies do not cover the
    # trait, both fail; the sharded test stage (test_verify_shards.py): a failing
    # shard or a count short of discovery fails it; and the clean-build smoke's temp path
    # (test_smoke_mktemp.py).
    # Bytecode is off so the run leaves no .pyc in the tracked test tree; the
    # files are named rather than discovered, because `unittest` with nothing to
    # run prints "Ran 0 tests ... OK" and a rename would turn this stage green.
    stage "check-gate-scope"
    PYTHONPATH="${ROOT}/scripts:${ROOT}/scripts/tests${PYTHONPATH:+:${PYTHONPATH}}" \
      PYTHONDONTWRITEBYTECODE=1 python3 -m unittest \
      scripts/tests/test_gate_scope.py scripts/tests/test_gate_scope_reasons.py \
      scripts/tests/test_verify_scope.py \
      scripts/tests/test_verify_refusal.py scripts/tests/test_verify_shards.py \
      scripts/tests/test_verify_test_parallel.py scripts/tests/test_verify_stage_time.py scripts/tests/test_verify_fuzz_shards.py \
      scripts/tests/test_smoke_mktemp.py scripts/tests/test_class_timing.py
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

# --- run ---------------------------------------------------------------------
# Argument parsing, the disarmed-gate refusal, the trait-stage runner and the
# gate-scope report consumer live in scripts/verify-guards.sh (sourced above):
# this file keeps the stage table they drive. The parsed report is state of the
# run, so it is initialised here beside the loop that fills and reads it.
selected=() scope_tier="" scope_reason="" scope_changed="" scope_base=""
scope_stages="" scope_skipped="" scope_notes=()

main() {
  local report
  cd "$ROOT"
  refuse_gate_flag "$@"
  parse_args "$@"
  if [[ "${#selected[@]}" -eq 0 ]]; then
    report="$(PYTHONDONTWRITEBYTECODE=1 python3 scripts/gate_scope.py)" || report=""
    read_scope "$report"
    plan_selection
  else
    echo "== gate scope: explicit (--stage given: no path filtering, every listed stage runs)"
  fi
  run_selected_stages
  report_result
}

# Run only when executed: a sourced copy stops after defining the selection. A
# piped run has no source path at all, so it is the other case that must run —
# `bash < scripts/verify.sh` is how the gate is quoted into a shell, and running
# nothing there would read as a pass.
if [[ -z "${BASH_SOURCE[0]:-}" || "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
