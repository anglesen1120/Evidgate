#!/usr/bin/env bash
# 02.2.1-T1.1: the permission-catalogue drift gate.
#
# `src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs` is a committed
# build product of `contracts/permissions.json` (see scripts/generate-permissions.py).
# This script regenerates it and fails when the tracked tree does not match
# byte-for-byte, so a string added to the catalogue without regenerating — or a
# hand-edited constant — cannot reach develop.
#
# Precondition (the same one check-codegen-drift.sh documents): the script regenerates
# BEFORE diffing and `git diff` compares the regenerated file against the git index, so
# it guards committed/staged drift. An unstaged hand-edit to the generated file is
# silently overwritten by the regeneration below and the gate exits 0; stage or commit
# the edit (or run the gate from a clean tree) for the check to see it. What the gate
# catches:
#
# - a staged/committed hand-edited generated file      -> `git diff` sees the change
# - a catalogue edit without re-running the generator  -> regeneration changes the file
# - a regenerated file that was never `git add`ed      -> the untracked check below
set -euo pipefail
cd "$(dirname "$0")/.."

python3 scripts/generate-permissions.py

if ! git diff --exit-code -- src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs; then
  echo "check-permissions-drift: Permissions.g.cs is stale — commit the generator output together with contracts/permissions.json" >&2
  exit 1
fi

untracked=$(git ls-files --others --exclude-standard -- src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs)
if [[ -n "$untracked" ]]; then
  echo "check-permissions-drift: the generated file is untracked — git add it:" >&2
  echo "$untracked" >&2
  exit 1
fi

echo "check-permissions-drift: Permissions.g.cs is in sync with contracts/permissions.json"
