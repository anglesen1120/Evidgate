#!/usr/bin/env bash
# AC2 (01.7.1-T1.4): CI must run `scripts/verify.sh` stages, never a parallel
# copy of their commands. This gate reads .github/workflows/ci.yml (override with
# --workflow <file>) and asserts, for every shell command it runs:
#
#   1. every stage of `verify.sh --list-stages` is invoked by the workflow (a
#      stage added locally but not wired into CI is a divergence);
#   2. a verify.sh call may only carry `--stage <name>` flags, and those stages
#      must appear in canonical order (never repeated);
#   3. no command duplicates a stage's work: raw `dotnet`, `npm`, `npx`, `tsc`
#      or `vitest` outside a verify.sh call fails — except the small
#      ALLOWED_RAW list below, where each entry is a command verify.sh cannot
#      own (installs) or a scaffold that activates with a later story.
#
# Exit 0: parity holds. Exit 1: every divergence is listed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

WORKFLOW="$ROOT/.github/workflows/ci.yml"

while [[ $# -gt 0 ]]; do
  case "$1" in
  --workflow)
    if [[ $# -lt 2 ]]; then
      echo "check-ci-parity: --workflow needs a path" >&2
      exit 2
    fi
    WORKFLOW="$2"
    shift 2
    ;;
  -h | --help)
    echo "usage: scripts/check-ci-parity.sh [--workflow <file>]"
    exit 0
    ;;
  *)
    echo "check-ci-parity: unknown argument '$1'" >&2
    echo "usage: scripts/check-ci-parity.sh [--workflow <file>]" >&2
    exit 2
    ;;
  esac
done

if [[ ! -f "$WORKFLOW" ]]; then
  echo "check-ci-parity: workflow '$WORKFLOW' not found" >&2
  exit 1
fi

# Structural guard over every workflow (not only --workflow): the run-extractor
# below matches `run:` only as a first key in column-anchored YAML, and the
# action-pins gate has the same limitation for `uses:`. Equivalent-but-invisible
# spellings — flow-style steps (`- {run: npm test}`) and quoted keys
# (`"run":`) — would slip through both gates, so reject them outright.
shopt -s nullglob
python3 - check-ci-parity .github/workflows/*.yml .github/workflows/*.yaml "$WORKFLOW" <<'PYEOF' || exit 1
import re
import sys

label, files = sys.argv[1], dict.fromkeys(sys.argv[2:])
quoted = re.compile(r'["\'](?:uses|run)["\']\s*:')
flow = re.compile(r"\{[^}\n]*\b(?:uses|run)\s*:")
bad = 0
for path in files:
    with open(path, encoding="utf-8") as fh:
        for n, line in enumerate(fh, 1):
            if quoted.search(line) or flow.search(line):
                print("%s: %s:%d: quoted/flow-style 'uses'/'run' key bypasses the line-anchored scan — rewrite as a block-style step"
                      % (label, path, n), file=sys.stderr)
                bad = 1
sys.exit(bad)
PYEOF
shopt -u nullglob

# Raw commands the workflow may run without going through verify.sh. Anything
# matching one of these lines — with a comment-free exact match — is allowed;
# anything else starting with dotnet/npm/npx/tsc/vitest duplicates a stage.
ALLOWED_RAW=(
  # Dependency install: verify.sh never installs (its header is the contract),
  # so every job runs npm ci itself. --ignore-scripts: lifecycle scripts
  # (husky prepare, scarf telemetry postinstall, fsevents/libxmljs2 optional
  # native builds) are never needed in CI — PR-template review owns that risk.
  'npm ci --ignore-scripts'
  # Contract drift gate is a verify.sh stage (check-codegen-drift) — no raw
  # npm invocation is needed or allowed for it.
  # Web build: activates when apps/web gains a build script (09.1.1). Deliberately
  # the --workspaces variant — `npm run build` at the root is the tsc-build stage.
  'npm run build --workspaces --if-present'
  # Playwright e2e: not a verify.sh stage (non-blocking until 09, T1.3).
  'npm run e2e -w apps/web'
  # Playwright shell suite on the fixtures build (09.1.1-T4 hand-over): not a
  # verify.sh stage, non-blocking like the production e2e job.
  'npm run e2e:fixtures -w apps/web'
  # The one workspace the e2e jobs need compiled before `ng build`
  # (`@evidgate/sdk` resolves to its `dist/`). The tsc-build stage may appear
  # only once in the workflow (rule 2) and runs in the web job, so the e2e jobs
  # compile this single package directly.
  'npm run build -w packages/sdk'
  # Runner browser installs, not repo commands (the fixtures config launches
  # `channel: chrome`).
  'npx playwright install --with-deps chromium'
  'npx playwright install --with-deps chrome'
)

# Canonical stage list — the workflow's coverage is diffed against this, so the
# list lives in exactly one place (verify.sh).
STAGES=()
while IFS= read -r line; do
  STAGES+=("$line")
done < <(scripts/verify.sh --list-stages)

if [[ "${#STAGES[@]}" -eq 0 ]]; then
  echo "check-ci-parity: scripts/verify.sh --list-stages returned nothing" >&2
  exit 1
fi

# Extract the shell commands the workflow runs. Prints "lineno<TAB>segment" —
# `run:` scalars are read with their YAML semantics (literal `|` = one command
# per line, folded `>` = one command per block) and each command is split on
# && / || / ; so a raw command cannot hide behind a legitimate neighbour.
extract_commands() {
  python3 - "$WORKFLOW" <<'PY'
import re
import sys

path = sys.argv[1]
lines = open(path, encoding="utf-8").read().splitlines()

run_re = re.compile(r"^(\s*)(-\s+)?run:\s*(.*)$")


def emit(lineno, cmd):
    for seg in re.split(r"\s*(?:&&|\|\||;)\s*", cmd):
        seg = seg.strip()
        if seg:
            print(f"{lineno}\t{seg}")


i = 0
while i < len(lines):
    m = run_re.match(lines[i])
    if not m:
        i += 1
        continue
    indent = len(m.group(1))
    scalar = m.group(3).strip()
    if scalar and not re.fullmatch(r"[|>][-+0-9]*", scalar):
        emit(i + 1, scalar)
        i += 1
        continue
    # block scalar: collect lines indented deeper than the run: key
    block = []
    lineno = i + 1
    i += 1
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        cur = len(line) - len(line.lstrip())
        if cur <= indent:
            break
        block.append(line.strip())
        i += 1
    if scalar.startswith("|") or scalar == "":
        # literal: one command per line; a trailing backslash continues it
        pending = ""
        for raw in block:
            pending += raw
            if pending.endswith("\\"):
                pending = pending[:-1].rstrip() + " "
                continue
            emit(lineno, pending)
            pending = ""
        if pending:
            emit(lineno, pending)
    else:
        # folded: YAML joins lines with a single space — one command
        emit(lineno, " ".join(block))
PY
}

fail=0
invoked=()

note_error() {
  echo "check-ci-parity: $1" >&2
  fail=1
}

allowed_raw() {
  local candidate="$1" allowed
  for allowed in "${ALLOWED_RAW[@]}"; do
    [[ "$candidate" == "$allowed" ]] && return 0
  done
  return 1
}

while IFS=$'\t' read -r lineno segment; do
  [[ -z "$segment" ]] && continue
  case "$segment" in
  *scripts/verify.sh*)
    # A verify call may carry only `--stage <name>` flags.
    args="${segment#*scripts/verify.sh}"
    if ! [[ "$args" =~ ^([[:space:]]+--stage[[:space:]]+[a-z0-9-]+)+[[:space:]]*$ ]]; then
      note_error "line $lineno: verify.sh invocation must be only '--stage <name>' flags: '$segment'"
      continue
    fi
    call_stages=()
    while IFS= read -r st; do
      [[ -z "$st" ]] && continue
      call_stages+=("$st")
      invoked+=("$st")
    done < <(printf '%s\n' "$args" | grep -oE -- '--stage[[:space:]]+[a-z0-9-]+' | sed -E 's/--stage[[:space:]]+//')
    # The call's stages must follow the canonical order — CI must not reorder
    # the gate verify.sh runs locally (AC2, same order).
    expected=""
    for stage_name in "${STAGES[@]}"; do
      for st in ${call_stages[@]+"${call_stages[@]}"}; do
        [[ "$st" == "$stage_name" ]] && expected="$expected$st "
      done
    done
    actual="$(printf '%s ' ${call_stages[@]+"${call_stages[@]}"})"
    if [[ "$expected" != "$actual" ]]; then
      note_error "line $lineno: stages out of canonical order: '${actual:-none}' (expected '${expected:-none}')"
    fi
    ;;
  *)
    # Strip leading VAR=value environment assignments, then check the binary.
    probe="$(printf '%s\n' "$segment" | sed -E 's/^([A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*[[:space:]]+)+//')"
    case "$probe" in
    dotnet* | npm* | npx* | tsc* | vitest*)
      if ! allowed_raw "$probe"; then
        note_error "line $lineno: raw command bypasses verify.sh — use './scripts/verify.sh --stage …' instead of '$probe'"
      fi
      ;;
    esac
    ;;
  esac
done < <(extract_commands)

# Coverage: every canonical stage must be invoked by the workflow.
for stage_name in "${STAGES[@]}"; do
  covered=0
  for inv in ${invoked[@]+"${invoked[@]}"}; do
    [[ "$inv" == "$stage_name" ]] && covered=1
  done
  [[ "$covered" -eq 1 ]] || note_error "verify.sh stage '$stage_name' is not wired into the workflow"
done

# Every invoked stage must exist and appear exactly once — except the
# environment-setup stages a job needs before its real stages can run
# (`restore` produces obj/project.assets.json; a second job that also runs
# `licence-scan`/`audit-deps`/`sbom` must restore first, and repeating it is
# harmless: locked mode is idempotent). `build` is the same: the isolation
# (M1 gate) job builds the solution in Release before running the isolation
# suite, and a second Release build is idempotent.
REPEATABLE_STAGES=' restore build '
seen=""
for inv in ${invoked[@]+"${invoked[@]}"}; do
  canonical=0
  for stage_name in "${STAGES[@]}"; do
    [[ "$inv" == "$stage_name" ]] && canonical=1
  done
  [[ "$canonical" -eq 1 ]] || note_error "workflow passes unknown stage '$inv'"
  case "$REPEATABLE_STAGES" in
  *" $inv "*) continue ;;
  esac
  case " $seen " in
  *" $inv "*) note_error "workflow passes stage '$inv' more than once" ;;
  esac
  seen="$seen $inv"
done

if [[ "$fail" -eq 0 ]]; then
  echo "check-ci-parity: workflow runs all ${#STAGES[@]} verify.sh stages; no raw command copies"
fi
exit "$fail"
