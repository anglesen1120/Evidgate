#!/usr/bin/env bash
# 01.7.1-T4.2 / 00 §5.7 S6 (AC6): dependency-vulnerability gate for both
# ecosystems — `npm audit --omit=dev` (dev advisories stay in PR review) plus
# `dotnet list package --vulnerable` over Evidgate.slnx. A finding fails the
# gate unless scripts/exceptions.json lists its advisory with an accepting ADR;
# a scoped npm entry exempts only production paths inside its declared chain.
# Prerequisites: `npm ci`, `dotnet restore --locked-mode`. Exit 0 clean, 1
# unexcepted findings, 2 scanner failure. --self-test proves both offline.
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
gate offline against planted fixtures and proves the locked installs it reads
reject a drifted lock (npm + NuGet).
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
# the past revokes it, and a scoped npm entry must reach its package only
# through the declared (workspace, direct dependency) pairs. Exit 1 iff any
# finding has no live exception, 2 when a scope cannot be evaluated.
# ---------------------------------------------------------------------------
evaluate() {
  local rows_file="$1" exceptions="$2" scope_dir="${3:-}"
  python3 - "$rows_file" "$exceptions" "$scope_dir" <<'PYEOF'
import glob, json, os, re, subprocess, sys, urllib.parse
from datetime import date

rows_file, exceptions_path, scope_dir = sys.argv[1:4]


class ScopeError(Exception): pass


class ScannerFailure(Exception): pass


# One cache per run, so npm ls is read once per scoped package, never per row.
cache = {"ws": None, "paths": {}}


# A scope narrows an exemption to the assessed first two hops of a production
# path. Anything unreadable or unstated is a violation, never an exemption.
def npm_paths(pkg, scope_dir):
    try:
        if scope_dir:
            with open(os.path.join(scope_dir, urllib.parse.quote(pkg, safe="") + ".json")) as fh:
                tree = json.load(fh)
        else:
            run = subprocess.run(["npm", "ls", pkg, "--all", "--omit=dev", "--package-lock-only",
                                  "--workspaces", "--include-workspace-root", "--json"],
                                 capture_output=True, text=True)
            if run.returncode:
                raise ScannerFailure("'npm ls %s' failed (exit %d): %s"
                                     % (pkg, run.returncode, run.stderr.strip()[:300]))
            tree = json.loads(run.stdout)
        found, todo = [], [(tree, [])]
        while todo:
            node, prefix = todo.pop()
            for name, child in node.get("dependencies", {}).items():
                if name == pkg:
                    found.append(prefix + [name])
                todo.append((child, prefix + [name]))
        if not found:
            raise ScannerFailure("npm ls reports no production path to %s" % pkg)
        return found
    except ScannerFailure:
        raise
    except Exception as e:
        raise ScannerFailure("npm ls output for %s is unusable (%s)" % (pkg, e))


def scope_violation(entry, eco, pkg, scope_dir):
    scope = entry.get("scope")
    items = scope.get("npm") if isinstance(scope, dict) else None
    pairs = {(i.get("workspace"), i.get("via")) for i in items or [] if isinstance(i, dict)
             and set(i) == {"workspace", "via"}
             and all(isinstance(i.get(k), str) and i.get(k) for k in ("workspace", "via"))}
    if (eco != "npm" or entry.get("package") != pkg or not isinstance(scope, dict)
            or set(scope) != {"npm"} or not isinstance(items, list) or not items
            or len(pairs) != len(items)):
        raise ScopeError("exception scope must be npm-only, name this package, and hold "
                         "non-empty 'workspace'/'via' pairs")
    if cache["ws"] is None:
        try:
            recs = json.load(open(os.path.join(scope_dir, "package-lock.json")))["packages"]
            cache["ws"] = {r["name"] for p, r in recs.items() if p
                           and "node_modules/" not in p and not r.get("link") and r.get("name")}
        except Exception as e:
            raise ScannerFailure("the workspace list is unreadable (%s)" % e)
    if not cache["ws"]:
        raise ScannerFailure("the workspace list is empty")
    if pkg not in cache["paths"]:
        cache["paths"][pkg] = npm_paths(pkg, scope_dir)
    bad = [p for p in cache["paths"][pkg]
           if not (len(p) > 1 and p[0] in cache["ws"] and (p[0], p[1]) in pairs)]
    if not bad:
        return None
    return "exception scope does not cover production path %s" % " ; ".join(
        " > ".join(p if p[0] in cache["ws"] else ["root"] + p) for p in bad)


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
        scope = ""
        if "scope" in entry:
            try:
                why = scope_violation(entry, eco, pkg, scope_dir)
            except ScopeError as e:
                why = str(e)
            except ScannerFailure as e:
                print("audit-deps: %s" % e, file=sys.stderr)
                sys.exit(2)
            if why:
                fails.append((eco, pkg, ver, sev, adv, why))
                continue
            scope = " (scope: %s)" % ", ".join(
                "%s > %s" % (i["workspace"], i["via"]) for i in entry["scope"]["npm"])
        exempted += 1
        print("  warn %s:%s@%s %s %s — exempted by %s%s" % (eco, pkg, ver, sev, adv, adr, scope))

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

# ---------------------------------------------------------------------------
# Stale-lock negative check (11.6.1-T1.1, AC1). The gate above is only as
# trustworthy as the locked install it reads: `npm ci` must refuse a
# package-lock.json that does not match package.json, and
# `dotnet restore --locked-mode` must refuse a packages.lock.json that does not
# match the csproj. Both are proven here in a throwaway temp tree (never a
# tracked file) against an EMPTY local source, so the check is offline. Each
# negative case carries a positive control — the same fixture with a CONSISTENT
# lock — so a fixture that fails for an unrelated reason cannot pass vacuously.
# Returns 0 pass, 1 fail, 2 a prerequisite (npm/dotnet) is missing.
# ---------------------------------------------------------------------------
lockfile_self_test() {
  if ! command -v npm >/dev/null 2>&1 || ! command -v dotnet >/dev/null 2>&1; then
    echo "self-test: FAIL — the stale-lock check needs 'npm' and 'dotnet' on PATH" >&2
    return 2
  fi

  local fx="$TMP/lockfiles" expect=0 out rc

  # --- npm: package-lock.json stale relative to package.json ---------------
  local npm="$fx/npm"
  mkdir -p "$npm/dep"
  cat > "$npm/dep/package.json" <<'EOF'
{ "name": "lock-drift-fixture-dep", "version": "1.0.0", "license": "MIT" }
EOF
  cat > "$npm/package.json" <<'EOF'
{ "name": "lock-drift-fixture", "version": "1.0.0", "private": true,
  "dependencies": { "lock-drift-fixture-dep": "file:./dep" } }
EOF
  # Stale lock: the manifest declares the dependency, the lock omits it.
  cat > "$npm/package-lock.json" <<'EOF'
{ "name": "lock-drift-fixture", "version": "1.0.0", "lockfileVersion": 3, "requires": true,
  "packages": { "": { "name": "lock-drift-fixture", "version": "1.0.0",
    "dependencies": { "lock-drift-fixture-dep": "file:./dep" } } } }
EOF
  rc=0
  out="$(cd "$npm" && npm ci --ignore-scripts --offline 2>&1)" || rc=$?
  if [[ "$rc" -eq 0 ]] || ! grep -q "Missing: lock-drift-fixture-dep@1.0.0 from lock file" <<<"$out"; then
    echo "self-test: FAIL — 'npm ci' accepted a stale package-lock.json (exit $rc)" >&2
    printf '%s\n' "$out" >&2
    expect=1
  else
    echo "self-test: PASS — stale package-lock.json fails 'npm ci' (exit $rc): $(grep -m1 'Missing: lock-drift-fixture-dep' <<<"$out")"
  fi

  # Positive control: same fixture, lock consistent with the manifest.
  cat > "$npm/package-lock.json" <<'EOF'
{ "name": "lock-drift-fixture", "version": "1.0.0", "lockfileVersion": 3, "requires": true,
  "packages": {
    "": { "name": "lock-drift-fixture", "version": "1.0.0",
      "dependencies": { "lock-drift-fixture-dep": "file:./dep" } },
    "dep": { "version": "1.0.0", "license": "MIT" },
    "node_modules/lock-drift-fixture-dep": { "resolved": "dep", "link": true } } }
EOF
  rc=0
  out="$(cd "$npm" && npm ci --ignore-scripts --offline 2>&1)" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    echo "self-test: FAIL — 'npm ci' rejected a consistent package-lock.json (exit $rc)" >&2
    printf '%s\n' "$out" >&2
    expect=1
  else
    echo "self-test: PASS — consistent package-lock.json installs offline (exit 0)"
  fi

  # --- NuGet: packages.lock.json stale relative to the csproj --------------
  local nu="$fx/nuget"
  mkdir -p "$nu/emptysrc"
  cat > "$nu/fx.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LockDriftFixture.Package" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
  # Stale lock: resolved 0.9.0 while the csproj asks for 1.0.0.
  cat > "$nu/packages.lock.json" <<'EOF'
{ "version": 1, "dependencies": { "net10.0": { "LockDriftFixture.Package": {
  "type": "Direct", "requested": "[0.9.0, )", "resolved": "0.9.0", "contentHash": "AAAA" } } } }
EOF
  rc=0
  out="$(cd "$nu" && dotnet restore --locked-mode --source "$nu/emptysrc" 2>&1)" || rc=$?
  if [[ "$rc" -eq 0 ]] || ! grep -q "NU1004" <<<"$out" || ! grep -q "LockDriftFixture.Package" <<<"$out"; then
    echo "self-test: FAIL — 'dotnet restore --locked-mode' accepted a stale packages.lock.json (exit $rc)" >&2
    printf '%s\n' "$out" >&2
    expect=1
  else
    echo "self-test: PASS — stale packages.lock.json fails 'dotnet restore --locked-mode' (exit $rc): $(grep -m1 -o 'NU1004: The package reference [^ ]*' <<<"$out")"
  fi

  # Positive control: same project, lock consistent with the csproj (1.0.0).
  cat > "$nu/packages.lock.json" <<'EOF'
{ "version": 1, "dependencies": { "net10.0": { "LockDriftFixture.Package": {
  "type": "Direct", "requested": "[1.0.0, )", "resolved": "1.0.0", "contentHash": "AAAA" } } } }
EOF
  rc=0
  out="$(cd "$nu" && dotnet restore --locked-mode --source "$nu/emptysrc" 2>&1)" || rc=$?
  if grep -q "NU1004" <<<"$out"; then
    echo "self-test: FAIL — a consistent packages.lock.json still hit NU1004" >&2
    printf '%s\n' "$out" >&2
    expect=1
  else
    echo "self-test: PASS — consistent packages.lock.json clears the lock check (no NU1004; $(grep -m1 -o 'error NU[0-9]*' <<<"$out" || echo 'restore succeeded'))"
  fi

  return "$expect"
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
npm	target2	1.0.0	high	GHSA-ssss-ssss-ssss
nuget	Newtonsoft.Json	12.0.1	moderate	GHSA-nnnn-nnnn-nnnn
npm	tinyglobby	0.2.17	high	GHSA-mmmm-mmmm-mmmm
npm	bad-scope	1.0.0	low	GHSA-bbbb-bbbb-bbbb
npm	empty-scope	1.0.0	low	GHSA-eeee-eeee-eeee
EOF
  cat > "$fx/exceptions.json" <<'EOF'
{
  "version": 1,
  "exceptions": {
    "GHSA-aaaa-bbbb-cccc": {
      "adr": "ADR-0001",
      "package": "left-pad",
      "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] },
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
    },
    "GHSA-ssss-ssss-ssss": { "adr": "ADR-0001", "package": "target2", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] } },
    "GHSA-nnnn-nnnn-nnnn": { "adr": "ADR-0001", "package": "Newtonsoft.Json", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] } },
    "GHSA-mmmm-mmmm-mmmm": { "adr": "ADR-0001", "package": "picomatch", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] } },
    "GHSA-bbbb-bbbb-bbbb": { "adr": "ADR-0001", "package": "bad-scope", "scope": { "nuget": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] } },
    "GHSA-eeee-eeee-eeee": { "adr": "ADR-0001", "package": "empty-scope", "scope": { "npm": [] } }
  }
}
EOF

  # Scoped exceptions: only the declared (workspace, direct dependency) prefix
  # passes; another workspace, a root branch or bad data must fail.
  mkdir -p "$fx/scope"; printf '%s\n' '{"packages": {"": {}, "apps/web": {"name": "@evidgate/web"}, "packages/other": {"name": "@evidgate/other"}, "node_modules/@evidgate/web": {"resolved": "apps/web", "link": true}}}' > "$fx/scope/package-lock.json"
  echo '{"dependencies":{"@evidgate/web":{"dependencies":{"@angular/localize":{"dependencies":{"left-pad":{}}}}}}}' > "$fx/scope/left-pad.json"
  echo '{"dependencies":{"@evidgate/web":{"dependencies":{"@angular/localize":{"dependencies":{"target2":{}}}}},"@evidgate/other":{"dependencies":{"mid":{"dependencies":{"target2":{}}}}},"left-pad":{"dependencies":{"target2":{}}}}}' > "$fx/scope/target2.json"
  set +e
  evaluate "$fx/rows" "$fx/exceptions.json" "$fx/scope" > "$fx/out.log" 2>&1
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
  grep -q "warn npm:left-pad.*exempted by ADR-0001 (scope: @evidgate/web > @angular/localize)" "$fx/out.log" || { echo "self-test: FAIL — in-scope finding not labelled with its scope" >&2; expect=1; }
  grep -q "fail npm:target2.*@evidgate/other > mid > target2" "$fx/out.log" || { echo "self-test: FAIL — a second workspace branch not flagged" >&2; expect=1; }
  grep -q "fail npm:target2.*root > left-pad > target2" "$fx/out.log" || { echo "self-test: FAIL — a root-package branch not flagged" >&2; expect=1; }
  grep -q "fail nuget:Newtonsoft.Json.*npm-only" "$fx/out.log" || { echo "self-test: FAIL — a scope on a NuGet finding not flagged" >&2; expect=1; }
  grep -q "fail npm:tinyglobby.*scope" "$fx/out.log" || { echo "self-test: FAIL — mismatched exception package not flagged" >&2; expect=1; }
  grep -q "fail npm:bad-scope.*scope" "$fx/out.log" || { echo "self-test: FAIL — malformed scope not flagged" >&2; expect=1; }
  grep -q "fail npm:empty-scope.*scope" "$fx/out.log" || { echo "self-test: FAIL — empty scope not flagged" >&2; expect=1; }
  [[ "$rc" -eq 1 ]] || { echo "self-test: FAIL — exit $rc, expected 1" >&2; expect=1; }
  echo 'not json' > "$fx/scope/left-pad.json"
  set +e; evaluate "$fx/rows" "$fx/exceptions.json" "$fx/scope" > "$fx/bad.log" 2>&1; rc=$?; set -e
  if [[ "$rc" -ne 2 ]] || ! grep -q "npm ls output for left-pad is unusable" "$fx/bad.log"; then echo "self-test: FAIL — unparseable npm ls JSON must exit 2" >&2; expect=1; fi

  # 11.6.1-T1.1 (AC1): the locked installs this gate reads must refuse a
  # drifted lock. A missing npm/dotnet is a hard prerequisite failure (exit 2).
  local lock_rc=0
  lockfile_self_test || lock_rc=$?
  if [[ "$lock_rc" -eq 2 ]]; then
    return 2
  elif [[ "$lock_rc" -ne 0 ]]; then
    expect=1
  fi

  if [[ "$expect" -eq 0 ]]; then
    echo "self-test: PASS — unlisted advisories fail, live ADR exception passes, expired exception revokes; stale npm + NuGet locks fail their locked installs"
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
