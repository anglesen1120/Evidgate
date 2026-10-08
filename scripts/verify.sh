#!/usr/bin/env bash
# FR-01-1 (plans/01-foundation.md §2): the full local gate — the same checks CI
# runs (01.7.1), in the same order. Stops at the first failure.
# Prerequisites only: .NET 10 SDK + Node 26 on PATH, dependencies restored once
# (dotnet restore/npm ci — this script never installs anything itself).
#
# AC2 (01.7.1-T1): CI does not copy these commands, it calls them —
#   ./scripts/verify.sh --stage restore --stage build ...
# `--list-stages` prints the canonical stage names, which is what
# scripts/check-ci-parity.sh diffs the workflow against, so a stage added here
# and not wired into ci.yml fails the parity gate.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Canonical stage order. No flag runs every stage in this order (the historical
# behaviour, FR-01-1); `--stage` runs a subset in the order given.
ALL_STAGES=(
  restore
  build
  format
  test
  isolation
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
  check-ci-parity
  check-action-pins
  check-codegen-drift
  check-permissions-drift
  check-rls
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

Runs the full local gate (FR-01-1). With no --stage every stage below runs, in
this order; CI (01.7.1) passes --stage explicitly so both run one command list.

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
    scripts/licence-scan.sh --report artifacts/licence-report.json
    ;;
  audit-deps)
    # AC6 (01.7.1-T4.2): vulnerability gates, exceptions.json is ADR-keyed.
    stage "audit-deps"
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
full_run=1
selected=()

while [[ $# -gt 0 ]]; do
  case "$1" in
  --stage)
    if [[ $# -lt 2 ]]; then
      echo "verify: --stage needs a stage name" >&2
      usage >&2
      exit 2
    fi
    selected+=("$2")
    full_run=0
    shift 2
    ;;
  --list-stages)
    printf '%s\n' "${ALL_STAGES[@]}"
    exit 0
    ;;
  -h | --help)
    usage
    exit 0
    ;;
  *)
    echo "verify: unknown argument '$1'" >&2
    usage >&2
    exit 2
    ;;
  esac
done

if [[ "${#selected[@]}" -eq 0 ]]; then
  selected=("${ALL_STAGES[@]}")
fi

for name in "${selected[@]}"; do
  case " ${ALL_STAGES[*]} " in
  *" $name "*) ;;
  *)
    echo "verify: unknown stage '$name'" >&2
    usage >&2
    exit 2
    ;;
  esac
done

for name in "${selected[@]}"; do
  run_stage "$name"
done

if [[ "$full_run" -eq 1 ]]; then
  echo "== verify: all stages passed"
else
  echo "== verify: selected stages passed: ${selected[*]}"
fi
