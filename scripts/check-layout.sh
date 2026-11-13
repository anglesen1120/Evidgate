#!/usr/bin/env bash
# AC5 (01.1.1): the working tree must match the repository layout contract of
# plans/00-overview.md §5.2 exactly:
#   top-level dirs  = contracts apps docs fixtures infra migrations packages scripts src tests
#                     (+ the documented planning exception `plans`)
#   root files      = allow-list below (required files must exist, the rest may exist)
#   dot entries     = explicit allow-list of top-level dot files/dirs
#   Evidgate.slnx   = exactly the M0 projects (8 src + tests/Architecture.Tests,
#                     01.2.1-T1.2), and every *.csproj on disk
# Compares git ls-files (tracked + untracked-not-ignored), so ignored build output
# (bin/, obj/, node_modules/, dist/, .DS_Store) and ignored local tool dirs
# (e.g. .omp, .vscode/*) never count. Exit 1 prints the diff.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail=0

git ls-files --cached --others --exclude-standard > "$TMP/all"

# ---- top-level directories (non-dot; dot entries are allow-listed below) ----
cat > "$TMP/expected-dirs" <<'EOF'
apps
contracts
docs
fixtures
infra
migrations
packages
plans
scripts
src
tests
EOF

grep / "$TMP/all" | cut -d/ -f1 | grep -v '^\.' | sort -u > "$TMP/actual-dirs"

if ! diff -u "$TMP/expected-dirs" "$TMP/actual-dirs"; then
  echo "check-layout: top-level directories differ from 00 §5.2" >&2
  fail=1
fi

# ---- top-level dot entries (files AND directories) ----
# Only these may exist at the repo root; anything else (e.g. .tools, .vscode,
# .idea — when not git-ignored) is an unexpected layout addition.
allowed_dot_entry() {
  case "$1" in
    .github|.gitignore|.gitattributes|.editorconfig|.nvmrc|.npmrc) return 0 ;;
    .prettierignore|.husky|.spectral.yaml|.config) return 0 ;;
    .env.dev.template) return 0 ;;
    .prettierrc*) return 0 ;;
    *) return 1 ;;
  esac
}

cut -d/ -f1 "$TMP/all" | grep '^\.' | sort -u > "$TMP/actual-dots"

while IFS= read -r entry; do
  if ! allowed_dot_entry "$entry"; then
    echo "check-layout: unexpected top-level dot entry: $entry" >&2
    fail=1
  fi
done < "$TMP/actual-dots"

# ---- root files ----
# Required: must exist. Allowed: may exist but is not required (owned by other stories).
cat > "$TMP/required-files" <<'EOF'
.gitignore
.npmrc
.nvmrc
Directory.Build.props
Directory.Packages.props
Evidgate.slnx
global.json
nuget.config
package-lock.json
package.json
tsconfig.base.json
EOF
cat > "$TMP/allowed-extra-files" <<'EOF'
.editorconfig
.env.dev.template
.prettierignore
.prettierrc.json
.spectral.yaml
AGENTS.md
DECISIONS.md
README.md
RECOMMENDATIONS.md
REVIEW.md
SESSION-SUMMARY.md
STATUS.md
eslint.config.js
openapi-ts.config.mjs
tsconfig.json
tsconfig.spec.json
vitest.config.base.mts
vitest.config.ts
EOF
cat "$TMP/required-files" "$TMP/allowed-extra-files" | sort -u > "$TMP/allowed-files"

grep -v / "$TMP/all" | sort -u > "$TMP/actual-files"

# unexpected: actual minus allowed
comm -23 "$TMP/actual-files" "$TMP/allowed-files" > "$TMP/unexpected-files"
if [ -s "$TMP/unexpected-files" ]; then
  echo "check-layout: unexpected root files:" >&2
  sed 's/^/  /' "$TMP/unexpected-files" >&2
  fail=1
fi

# missing: required minus actual
comm -23 "$TMP/required-files" "$TMP/actual-files" > "$TMP/missing-files"
if [ -s "$TMP/missing-files" ]; then
  echo "check-layout: missing required root files:" >&2
  sed 's/^/  /' "$TMP/missing-files" >&2
  fail=1
fi

# ---- solution project list ----
# 01.2.1-T1.2: tests/Architecture.Tests is part of the expected project list.
# M1 wave 1-2: 01.1.2-T2.1 module pairs; test projects of 01.4.1, 01.1.2, 01.8.1-T1.
# M1 wave 3: test projects of 01.3.2-T4, 01.4.2-T1, 02.1.1-T1.
# M1 wave 4: test projects of 01.6.1-T2 (Foundation) and 01.5.1 (Tasks).
# 06.3.6-T1.2: the parser child (src/Evidgate.Parser) and its child-protocol tests.
cat > "$TMP/expected-proj" <<'EOF'
src/BuildingBlocks/Canonicalization/Evidgate.BuildingBlocks.Canonicalization.csproj
src/BuildingBlocks/Contracts.Validation/Evidgate.BuildingBlocks.Contracts.Validation.csproj
src/BuildingBlocks/GitHub/Evidgate.BuildingBlocks.GitHub.csproj
src/BuildingBlocks/ObjectStorage/Evidgate.BuildingBlocks.ObjectStorage.csproj
src/BuildingBlocks/Observability/Evidgate.BuildingBlocks.Observability.csproj
src/BuildingBlocks/Persistence/Evidgate.BuildingBlocks.Persistence.csproj
src/BuildingBlocks/Pipeline/Evidgate.BuildingBlocks.Pipeline.csproj
src/BuildingBlocks/SharedKernel/Evidgate.BuildingBlocks.SharedKernel.csproj
src/Evidgate.Api/Evidgate.Api.csproj
src/Evidgate.Evaluation/Evidgate.Evaluation.csproj
src/Evidgate.Migrator/Evidgate.Migrator.csproj
src/Evidgate.Parser/Evidgate.Parser.csproj
src/Evidgate.Worker/Evidgate.Worker.csproj
src/Modules/Evidgate.Modules.Audit.Contracts/Evidgate.Modules.Audit.Contracts.csproj
src/Modules/Evidgate.Modules.Audit/Evidgate.Modules.Audit.csproj
src/Modules/Evidgate.Modules.Authoring.Contracts/Evidgate.Modules.Authoring.Contracts.csproj
src/Modules/Evidgate.Modules.Authoring/Evidgate.Modules.Authoring.csproj
src/Modules/Evidgate.Modules.Catalog.Contracts/Evidgate.Modules.Catalog.Contracts.csproj
src/Modules/Evidgate.Modules.Catalog/Evidgate.Modules.Catalog.csproj
src/Modules/Evidgate.Modules.Evidence.Contracts/Evidgate.Modules.Evidence.Contracts.csproj
src/Modules/Evidgate.Modules.Evidence/Evidgate.Modules.Evidence.csproj
src/Modules/Evidgate.Modules.Healing.Contracts/Evidgate.Modules.Healing.Contracts.csproj
src/Modules/Evidgate.Modules.Healing/Evidgate.Modules.Healing.csproj
src/Modules/Evidgate.Modules.Identity.Contracts/Evidgate.Modules.Identity.Contracts.csproj
src/Modules/Evidgate.Modules.Identity/Evidgate.Modules.Identity.csproj
src/Modules/Evidgate.Modules.Ingestion.Contracts/Evidgate.Modules.Ingestion.Contracts.csproj
src/Modules/Evidgate.Modules.Ingestion/Evidgate.Modules.Ingestion.csproj
src/Modules/Evidgate.Modules.Notifications.Contracts/Evidgate.Modules.Notifications.Contracts.csproj
src/Modules/Evidgate.Modules.Notifications/Evidgate.Modules.Notifications.csproj
src/Modules/Evidgate.Modules.Orchestrator.Contracts/Evidgate.Modules.Orchestrator.Contracts.csproj
src/Modules/Evidgate.Modules.Orchestrator/Evidgate.Modules.Orchestrator.csproj
src/Modules/Evidgate.Modules.Policy.Contracts/Evidgate.Modules.Policy.Contracts.csproj
src/Modules/Evidgate.Modules.Policy/Evidgate.Modules.Policy.csproj
src/Modules/Evidgate.Modules.Tenancy.Contracts/Evidgate.Modules.Tenancy.Contracts.csproj
src/Modules/Evidgate.Modules.Tenancy/Evidgate.Modules.Tenancy.csproj
src/Modules/Evidgate.Modules.Usage.Contracts/Evidgate.Modules.Usage.Contracts.csproj
src/Modules/Evidgate.Modules.Usage/Evidgate.Modules.Usage.csproj
src/Tools/Evidgate.GoldenHarness/Evidgate.GoldenHarness.csproj
tests/Architecture.Tests/Architecture.Tests.csproj
tests/Canonicalization.UnitTests/Canonicalization.UnitTests.csproj
tests/Catalog.UnitTests/Catalog.UnitTests.csproj
tests/Contract.Tests/Contract.Tests.csproj
tests/Evaluation.UnitTests/Evaluation.UnitTests.csproj
tests/Foundation.IntegrationTests/Foundation.IntegrationTests.csproj
tests/GoldenHarness.UnitTests/GoldenHarness.UnitTests.csproj
tests/Hosts.IntegrationTests/Hosts.IntegrationTests.csproj
tests/Identity.IntegrationTests/Identity.IntegrationTests.csproj
tests/Isolation.Tests/Isolation.Tests.csproj
tests/Migrator.IntegrationTests/Migrator.IntegrationTests.csproj
tests/ObjectStorage.IntegrationTests/ObjectStorage.IntegrationTests.csproj
tests/Parser.Tests/Parser.Tests.csproj
tests/Persistence.IntegrationTests/Persistence.IntegrationTests.csproj
tests/Tasks.IntegrationTests/Tasks.IntegrationTests.csproj
tests/Tenancy.IntegrationTests/Tenancy.IntegrationTests.csproj
EOF

grep -oE 'Path="[^"]+\.csproj"' Evidgate.slnx \
  | sed -E 's/Path="([^"]+)"/\1/' \
  | sort -u > "$TMP/actual-proj"

if ! diff -u "$TMP/expected-proj" "$TMP/actual-proj"; then
  echo "check-layout: Evidgate.slnx project list differs from the expected list" >&2
  fail=1
fi

# Every *.csproj on disk (tracked + untracked-not-ignored) must be in the slnx,
# and every slnx entry must exist on disk. A one-sided diff fails both ways.
git ls-files --cached --others --exclude-standard -- '*.csproj' \
  | sort -u > "$TMP/ondisk-proj"

if ! diff -u "$TMP/actual-proj" "$TMP/ondisk-proj"; then
  echo "check-layout: *.csproj files on disk do not match Evidgate.slnx Path= entries" >&2
  fail=1
fi

if [ "$fail" -eq 0 ]; then
  echo "check-layout: tree and solution match 00 §5.2"
fi
exit "$fail"
