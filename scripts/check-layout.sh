#!/usr/bin/env bash
# AC5 (01.1.1): the working tree must match the repository layout contract of
# plans/00-overview.md §5.2 exactly:
#   top-level dirs  = contracts apps docs fixtures infra migrations packages scripts src tests
#                     (+ the documented planning exception `plans`)
#   root files      = allow-list below (required files must exist, the rest may exist)
#   dot entries     = explicit allow-list of top-level dot files/dirs
#   Evidgate.slnx   = exactly the 8 M0 projects, and every *.csproj on disk
#                     must be listed in the slnx (and vice versa)
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
AGENTS.md
DECISIONS.md
README.md
RECOMMENDATIONS.md
SESSION-SUMMARY.md
tsconfig.json
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
cat > "$TMP/expected-proj" <<'EOF'
src/BuildingBlocks/Canonicalization/Evidgate.BuildingBlocks.Canonicalization.csproj
src/BuildingBlocks/Contracts.Validation/Evidgate.BuildingBlocks.Contracts.Validation.csproj
src/BuildingBlocks/Observability/Evidgate.BuildingBlocks.Observability.csproj
src/BuildingBlocks/Persistence/Evidgate.BuildingBlocks.Persistence.csproj
src/BuildingBlocks/SharedKernel/Evidgate.BuildingBlocks.SharedKernel.csproj
src/Evidgate.Api/Evidgate.Api.csproj
src/Evidgate.Migrator/Evidgate.Migrator.csproj
src/Evidgate.Worker/Evidgate.Worker.csproj
EOF

grep -oE 'Path="[^"]+\.csproj"' Evidgate.slnx \
  | sed -E 's/Path="([^"]+)"/\1/' \
  | sort -u > "$TMP/actual-proj"

if ! diff -u "$TMP/expected-proj" "$TMP/actual-proj"; then
  echo "check-layout: Evidgate.slnx project list differs from the M0 contract" >&2
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
