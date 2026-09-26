#!/usr/bin/env bash
# 01.7.1-T4.2 / 00 §5.7 S6 (AC6): dependency-vulnerability gate for both
# ecosystems — `npm audit --omit=dev` (production deps only; dev-only advisories
# are reviewed in the PR instead) plus `dotnet list package --vulnerable
# --include-transitive` over Evidgate.slnx. Every finding must either fail the
# gate or be listed in scripts/exceptions.json keyed by advisory id (GHSA-… /
# CVE-…), with the accepting ADR id — an expired exception no longer exempts.
#
# Prerequisites (verify.sh stages):
#   npm ci                       -> package-lock.json / node_modules for npm audit
#   dotnet restore --locked-mode -> obj/project.assets.json for dotnet list
#
# usage: scripts/audit-deps.sh [--exceptions <file>] [--npm-only|--dotnet-only]
#                              [--self-test] [-h|--help]
#
# Exit 0: no unexcepted vulnerabilities. Exit 1: lists every unexcepted finding.
# Exit 2: a scanner failed (offline registry, missing assets, malformed JSON).
# --self-test feeds synthetic audit JSON through the same evaluator and asserts
# unlisted advisories fail while ADR-keyed exceptions pass (expired ones fail).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

EXCEPTIONS="scripts/exceptions.json"
SELF_TEST=0
DO_NPM=1
DO_DOTNET=1

usage() {
  cat <<'EOF'
usage: scripts/audit-deps.sh [--exceptions <file>]
                             [--npm-only | --dotnet-only]
                             [--self-test] [-h|--help]

Runs npm audit --omit=dev and dotnet list package --vulnerable
--include-transitive, then drops findings allowlisted in
scripts/exceptions.json keyed by advisory id. Exit 1 names every unexcepted
vulnerability; exit 2 means a scanner itself failed. --self-test proves the
gate offline against planted fixtures.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --exceptions)  EXCEPTIONS="$2"; shift 2 ;;
    --npm-only)    DO_DOTNET=0; shift ;;
    --dotnet-only) DO_NPM=0; shift ;;
    --self-test)   SELF_TEST=1; shift ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "audit-deps: unknown argument '$1'" >&2; usage; exit 2 ;;
  esac
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---------------------------------------------------------------------------
# Evaluator: rows '<eco>\t<pkg>\t<ver>\t<severity>\t<advisory>' vs exceptions
# keyed by advisory id. An exception must carry an ADR id; an 'expires' date in
# the past revokes it. Exit 1 iff any finding has no live exception.
# ---------------------------------------------------------------------------
evaluate() {
  local rows_file="$1" exceptions="$2"
  python3 - "$rows_file" "$exceptions" <<'PYEOF'
import glob, json, os, re, sys
from datetime import date

rows_file, exceptions_path = sys.argv[1:3]
exc = (json.load(open(exceptions_path)).get("exceptions") or {})
today = date.today().isoformat()

fails = []
exempted = 0
total = 0
with open(rows_file) as fh:
    for line in fh:
        line = line.rstrip("\n")
        if not line:
            continue
        eco, pkg, ver, sev, adv = line.split("\t", 4)
        total += 1
        entry = exc.get(adv)
        if not entry:
            fails.append((eco, pkg, ver, sev, adv, "not allowlisted"))
            continue
        adr = entry.get("adr") or ""
        # The adr field must be a real ADR id AND resolve to a docs/adr file,
        # matching the licence-scan contract — 'excepted by <adr>' otherwise
        # cites nothing auditable.
        m = re.fullmatch(r"ADR-(\d{4})", adr)
        if not m:
            fails.append((eco, pkg, ver, sev, adv,
                          "exception adr '%s' is not an ADR id (expected ADR-NNNN)" % adr))
            continue
        if not glob.glob(os.path.join("docs", "adr", m.group(1) + "-*.md")):
            fails.append((eco, pkg, ver, sev, adv,
                          "exception adr %s has no docs/adr/%s-*.md file" % (adr, m.group(1))))
            continue
        exp = entry.get("expires")
        if exp and exp < today:
            fails.append((eco, pkg, ver, sev, adv, "exception expired %s" % exp))
            continue
        exempted += 1
        print("  warn %s:%s@%s %s %s — exempted by %s" % (eco, pkg, ver, sev, adv, adr))

for eco, pkg, ver, sev, adv, why in fails:
    print("  fail %s:%s@%s %s %s — %s" % (eco, pkg, ver, sev, adv, why))

print("audit-deps: %d findings — %d exempted by exceptions.json, %d failing"
      % (total, exempted, len(fails)))
if fails:
    print("audit-deps: upgrade the dependency or add an exceptions.json entry keyed by the advisory id with its ADR", file=sys.stderr)
sys.exit(1 if fails else 0)
PYEOF
}

npm_rows() {
  # npm audit exits non-zero when vulnerabilities exist — capture, don't die.
  local out="$1" rc=0
  npm audit --omit=dev --json > "$out" 2>"$out.err" || rc=$?
  python3 - "$out" <<'PYEOF' || {
import json, sys
try:
    data = json.load(open(sys.argv[1]))
except Exception as e:
    print("audit-deps: npm audit output is not JSON (%s)" % e, file=sys.stderr)
    sys.exit(2)
vulns = data.get("vulnerabilities")
if vulns is None:
    print("audit-deps: npm audit output has no 'vulnerabilities' key", file=sys.stderr)
    sys.exit(2)
for pkg, v in sorted(vulns.items()):
    ver = v.get("range", "?")
    for via in v.get("via", []):
        if not isinstance(via, dict):
            continue  # via entries can be plain strings naming the source pkg
        adv = (via.get("url") or "").rstrip("/").rsplit("/", 1)[-1]
        if not adv:
            adv = "npm:%s:%s" % (via.get("source", pkg), via.get("title", ""))
        print("npm\t%s\t%s\t%s\t%s" % (pkg, ver, via.get("severity", "?"), adv))
PYEOF
    echo "audit-deps: npm audit parser failed" >&2; return 2; }
}

dotnet_rows() {
  local out="$1" rc=0
  if ! find . -name project.assets.json -print -quit | grep -q .; then
    echo "audit-deps: no obj/project.assets.json — run 'dotnet restore --locked-mode Evidgate.slnx' first" >&2
    return 2
  fi
  # dotnet list takes the solution as a positional BEFORE 'package'; it writes
  # JSON to stdout and may exit non-zero when findings exist; treat a
  # non-JSON/missing 'projects' payload as a scanner failure.
  dotnet list Evidgate.slnx package --vulnerable --include-transitive --format json > "$out" 2>"$out.err" || rc=$?
  python3 - "$out" <<'PYEOF' || {
import json, sys
try:
    data = json.load(open(sys.argv[1]))
except Exception as e:
    print("audit-deps: dotnet list output is not JSON (%s)" % e, file=sys.stderr)
    sys.exit(2)
projects = data.get("projects")
if projects is None:
    problems = "; ".join(p.get("text", "?") for p in data.get("problems", []))
    print("audit-deps: dotnet list failed: %s" % (problems or "no 'projects' key"), file=sys.stderr)
    sys.exit(2)
seen = set()
for proj in projects:
    for fw in proj.get("frameworks", []):
        for bucket in ("topLevelPackages", "transitivePackages"):
            for p in fw.get(bucket, []):
                name = p.get("id") or p.get("name") or "?"
                ver = p.get("resolvedVersion") or p.get("requestedVersion") or "?"
                for vuln in p.get("vulnerabilities", []):
                    adv = (vuln.get("advisoryurl") or "").rstrip("/").rsplit("/", 1)[-1]
                    if not adv:
                        adv = "nuget:%s:%s" % (name, vuln.get("severity", "?"))
                    key = (name, ver, adv)
                    if key in seen:
                        continue
                    seen.add(key)
                    print("nuget\t%s\t%s\t%s\t%s" % (name, ver, vuln.get("severity", "?"), adv))
PYEOF
    echo "audit-deps: dotnet list parser failed" >&2; return 2; }
}

self_test() {
  echo "== audit-deps --self-test (unlisted advisories fail; ADR-keyed exceptions pass)"
  local fx="$TMP/selftest"
  mkdir -p "$fx"

  cat > "$fx/rows" <<'EOF'
npm	left-pad	1.3.0	high	GHSA-aaaa-bbbb-cccc
npm	right-pad	2.0.0	critical	GHSA-dddd-eeee-ffff
nuget	Newtonsoft.Json	12.0.1	moderate	CVE-2024-21907
nuget	Old.Package	1.0.0	high	GHSA-zzzz-yyyy-xxxx
npm	tiny-pkg	0.0.1	low	GHSA-qqqq-wwww-eeee
npm	other-pkg	0.0.2	low	GHSA-pppp-oooo-rrrr
EOF
  cat > "$fx/exceptions.json" <<'EOF'
{
  "version": 1,
  "exceptions": {
    "GHSA-aaaa-bbbb-cccc": {
      "adr": "ADR-0001",
      "package": "left-pad",
      "reason": "self-test: excepted advisory"
    },
    "GHSA-zzzz-yyyy-xxxx": {
      "adr": "ADR-0001",
      "package": "Old.Package",
      "reason": "self-test: expired exception must fail",
      "expires": "2000-01-01"
    },
    "GHSA-qqqq-wwww-eeee": {
      "adr": "ADR-9999",
      "package": "tiny-pkg",
      "reason": "self-test: ADR file that does not exist must fail"
    },
    "GHSA-pppp-oooo-rrrr": {
      "adr": "not-an-adr",
      "package": "other-pkg",
      "reason": "self-test: malformed adr value must fail"
    }
  }
}
EOF

  set +e
  evaluate "$fx/rows" "$fx/exceptions.json" > "$fx/out.log" 2>&1
  rc=$?
  set -e
  cat "$fx/out.log"

  local expect=0
  grep -q "fail npm:right-pad" "$fx/out.log" || { echo "self-test: FAIL — unlisted GHSA-dddd-eeee-ffff not flagged" >&2; expect=1; }
  grep -q "fail nuget:Newtonsoft.Json" "$fx/out.log" || { echo "self-test: FAIL — unlisted CVE-2024-21907 not flagged" >&2; expect=1; }
  grep -q "fail nuget:Old.Package.*expired" "$fx/out.log" || { echo "self-test: FAIL — expired exception not revoked" >&2; expect=1; }
  grep -q "warn npm:left-pad.*ADR-0001" "$fx/out.log" || { echo "self-test: FAIL — live exception not honoured" >&2; expect=1; }
  grep -q "fail npm:tiny-pkg.*ADR-9999.*no docs/adr" "$fx/out.log" || { echo "self-test: FAIL — exception citing a missing ADR file not flagged" >&2; expect=1; }
  grep -q "fail npm:other-pkg.*not an ADR id" "$fx/out.log" || { echo "self-test: FAIL — malformed adr value not flagged" >&2; expect=1; }
  [[ "$rc" -eq 1 ]] || { echo "self-test: FAIL — exit $rc, expected 1" >&2; expect=1; }
  if [[ "$expect" -eq 0 ]]; then
    echo "self-test: PASS — unlisted advisories fail, live ADR exception passes, expired exception revokes"
  fi
  return "$expect"
}

if [[ "$SELF_TEST" -eq 1 ]]; then
  self_test
  exit $?
fi

if [[ ! -f "$EXCEPTIONS" ]]; then
  echo "audit-deps: $EXCEPTIONS missing" >&2
  exit 2
fi

ROWS="$TMP/rows"
: > "$ROWS"
if [[ "$DO_NPM" -eq 1 ]]; then
  echo "== audit-deps: npm audit --omit=dev"
  npm_rows "$TMP/npm-audit.json" >> "$ROWS"
fi
if [[ "$DO_DOTNET" -eq 1 ]]; then
  echo "== audit-deps: dotnet list package --vulnerable --include-transitive"
  dotnet_rows "$TMP/dotnet-list.json" >> "$ROWS"
fi

evaluate "$ROWS" "$EXCEPTIONS"
