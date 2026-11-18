#!/usr/bin/env bash
# 01.7.1-T4.2 / 00 §5.7 S6 (AC6): dependency-vulnerability gate for both
# ecosystems — `npm audit --omit=dev` plus `dotnet list package --vulnerable`.
# A finding fails unless scripts/exceptions.json lists its advisory with an
# accepting ADR; a scoped npm entry exempts only its declared production chains.
# Prerequisites: `npm ci`, `dotnet restore --locked-mode`. Exit 1 unexcepted
# findings, 2 scanner failure. --self-test proves both offline.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

EXCEPTIONS="scripts/exceptions.json"
SELF_TEST=0
DO_NPM=1
DO_DOTNET=1

usage() {
  cat <<'EOF'
usage: scripts/audit-deps.sh [--exceptions <file>] [--npm-only | --dotnet-only]
                             [--self-test] [-h|--help]

Runs npm audit --omit=dev and dotnet list package --vulnerable, then honours
scripts/exceptions.json (ADR-keyed; a scoped npm entry only inside its declared
workspace > direct-dependency chains). Exit 1 names unexcepted findings, exit 2
is a scanner failure; --self-test proves the gate and the locked installs offline.
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

# The scope policy lives in scripts/audit_deps_eval.py so it stays testable and
# under the function-size cap; this wrapper only fixes where it runs from.
evaluate() {
  PYTHONDONTWRITEBYTECODE=1 python3 scripts/audit_deps_eval.py "$1" "$2" "${3:-.}"
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
        print("npm\t%s\t%s\t%s\t%s\t%s" % (pkg, ver, via.get("severity", "?"), adv,
                                           ",".join(v.get("nodes") or [])))
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
npm	right-pad	2.0.0	critical	GHSA-dddd-eeee-ffff
nuget	Newtonsoft.Json	12.0.1	moderate	CVE-2024-21907
nuget	Old.Package	1.0.0	high	GHSA-zzzz-yyyy-xxxx
npm	tiny-pkg	0.0.1	low	GHSA-qqqq-wwww-eeee
npm	other-pkg	0.0.2	low	GHSA-pppp-oooo-rrrr
npm	target2	1.0.0	high	GHSA-ssss-ssss-ssss	node_modules/target2,node_modules/pm
nuget	Newtonsoft.Json	12.0.1	moderate	GHSA-nnnn-nnnn-nnnn
npm	tinyglobby	0.2.17	high	GHSA-mmmm-mmmm-mmmm
npm	bad-scope	1.0.0	low	GHSA-bbbb-bbbb-bbbb
npm	empty-scope	1.0.0	low	GHSA-eeee-eeee-eeee
npm	no-expiry	1.0.0	low	GHSA-hhhh-hhhh-hhhh
npm	scalar-scope	1.0.0	low	GHSA-iiii-iiii-iiii
npm	list-item	1.0.0	low	GHSA-jjjj-jjjj-jjjj
EOF
  printf 'npm\ttarget2\t1.0.0\thigh\tGHSA-ssss-ssss-ssss\n' > "$fx/rows-t2"
  printf 'npm\ttarget3\t1.0.0\thigh\tGHSA-tttt-tttt-tttt\n' > "$fx/rows-t3"
  cat > "$fx/exceptions.json" <<'EOF'
{
  "version": 1,
  "exceptions": {
    "GHSA-zzzz-yyyy-xxxx": { "adr": "ADR-0001", "package": "Old.Package", "reason": "self-test: expired", "expires": "2000-01-01" },
    "GHSA-qqqq-wwww-eeee": { "adr": "ADR-9999", "package": "tiny-pkg", "reason": "self-test: missing ADR file" },
    "GHSA-pppp-oooo-rrrr": { "adr": "not-an-adr", "package": "other-pkg", "reason": "self-test: malformed adr" },
    "GHSA-ssss-ssss-ssss": { "adr": "ADR-0001", "package": "target2", "expires": "2999-12-31", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] }, "reason": "self-test: out-of-scope chains" },
    "GHSA-nnnn-nnnn-nnnn": { "adr": "ADR-0001", "package": "Newtonsoft.Json", "expires": "2999-12-31", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] }, "reason": "self-test: nuget cannot be scoped" },
    "GHSA-mmmm-mmmm-mmmm": { "adr": "ADR-0001", "package": "picomatch", "expires": "2999-12-31", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] }, "reason": "self-test: package mismatch" },
    "GHSA-bbbb-bbbb-bbbb": { "adr": "ADR-0001", "package": "bad-scope", "expires": "2999-12-31", "scope": { "nuget": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] }, "reason": "self-test: npm-only" },
    "GHSA-eeee-eeee-eeee": { "adr": "ADR-0001", "package": "empty-scope", "expires": "2999-12-31", "scope": { "npm": [] }, "reason": "self-test: empty" },
    "GHSA-hhhh-hhhh-hhhh": { "adr": "ADR-0001", "package": "no-expiry", "scope": { "npm": [ { "workspace": "@evidgate/web", "via": "@angular/localize" } ] }, "reason": "self-test: no expiry" },
    "GHSA-iiii-iiii-iiii": { "adr": "ADR-0001", "package": "scalar-scope", "expires": "2999-12-31", "scope": { "npm": 7 }, "reason": "self-test: scalar scope" },
    "GHSA-jjjj-jjjj-jjjj": { "adr": "ADR-0001", "package": "list-item", "expires": "2999-12-31", "scope": { "npm": [ { "workspace": 1, "via": true } ] }, "reason": "self-test: non-string pair" }
  }
}
EOF
  printf '%s\n' '{"exceptions": {"GHSA-tttt-tttt-tttt": {"adr": "ADR-0001", "package": "target3", "expires": "2999-12-31", "scope": {"npm": [{"workspace": "@evidgate/web", "via": "@angular/localize"}]}}}}' > "$fx/exc-t3.json"

  # Scope fixtures are synthetic lockfiles: which origin, through which direct
  # dependency, reaches the vulnerable package. npm's display tree prunes the
  # shared-intermediate and alias cases these fixtures plant.
  local reg="https://registry.npmjs.org"
  mkdir -p "$fx/scope" "$fx/scope-dev" "$fx/scope-alias" "$fx/scope-ghost" "$fx/scope-bad"
  cat > "$fx/scope/package-lock.json" <<EOF
{"packages": {
  "": {"name": "fx", "workspaces": ["apps/*", "packages/*"], "dependencies": {"@evidgate/web": "file:apps/web", "root-tool": "1", "@evidgate/local": "file:tools/local"}},
  "apps/web": {"name": "@evidgate/web", "dependencies": {"@angular/localize": "1", "wrapper": "1"}},
  "packages/other": {"name": "@evidgate/other", "dependencies": {"mid": "1"}},
  "packages/zsorting": {"name": "@evidgate/zsorting", "dependencies": {"@angular/localize": "1"}},
  "packages/v2": {"name": "@evidgate/v2", "dependencies": {"mid2": "1"}},
  "packages/directws": {"name": "@evidgate/directws", "dependencies": {"target2": "1"}},
  "packages/aliasws": {"name": "@evidgate/aliasws", "dependencies": {"pm": "npm:target2@1.0.0"}},
  "tools/local": {"name": "@evidgate/local", "dependencies": {"inner": "1"}},
  "node_modules/@evidgate/web": {"resolved": "apps/web", "link": true},
  "node_modules/@evidgate/local": {"resolved": "tools/local", "link": true},
  "node_modules/@angular/localize": {"resolved": "$reg/@angular/localize/-/localize-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/wrapper": {"resolved": "$reg/wrapper/-/wrapper-1.0.0.tgz", "dependencies": {"@angular/localize": "1"}},
  "node_modules/mid": {"resolved": "$reg/mid/-/mid-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/mid2": {"resolved": "$reg/mid2/-/mid2-1.0.0.tgz", "dependencies": {"target2": "2"}},
  "node_modules/mid2/node_modules/target2": {"resolved": "$reg/target2/-/target2-2.0.0.tgz"},
  "node_modules/pm": {"name": "target2", "resolved": "$reg/target2/-/target2-1.0.0.tgz"},
  "node_modules/root-tool": {"resolved": "$reg/root-tool/-/root-tool-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/inner": {"resolved": "$reg/inner/-/inner-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/target2": {"resolved": "$reg/target2/-/target2-1.0.0.tgz"}}}
EOF
  cat > "$fx/scope-dev/package-lock.json" <<EOF
{"packages": {"": {"name": "fx", "workspaces": ["apps/*", "packages/*"]},
  "apps/web": {"name": "@evidgate/web", "dependencies": {"@angular/localize": "1"}},
  "packages/other": {"name": "@evidgate/other", "devDependencies": {"dev-mid": "1"}},
  "node_modules/@angular/localize": {"resolved": "$reg/@angular/localize/-/localize-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/dev-mid": {"resolved": "$reg/dev-mid/-/dev-mid-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/target2": {"resolved": "$reg/target2/-/target2-1.0.0.tgz"}}}
EOF
  cat > "$fx/scope-alias/package-lock.json" <<EOF
{"packages": {"": {"name": "fx", "workspaces": ["apps/*"]},
  "apps/web": {"name": "@evidgate/web", "dependencies": {"@angular/localize": "npm:evil@1"}},
  "node_modules/@angular/localize": {"name": "evil", "resolved": "$reg/evil/-/evil-1.0.0.tgz", "dependencies": {"target3": "1"}},
  "node_modules/target3": {"resolved": "$reg/target3/-/target3-1.0.0.tgz"}}}
EOF
  cat > "$fx/scope-ghost/package-lock.json" <<EOF
{"packages": {"": {"name": "fx", "workspaces": ["apps/*"]},
  "apps/web": {"name": "@evidgate/web", "dependencies": {"@angular/localize": "1", "ghost": "1"}},
  "node_modules/@angular/localize": {"resolved": "$reg/@angular/localize/-/localize-1.0.0.tgz", "dependencies": {"target2": "1"}},
  "node_modules/target2": {"resolved": "$reg/target2/-/target2-1.0.0.tgz"}}}
EOF
  mkdir -p "$fx/scope-link"
  cat > "$fx/scope-link/package-lock.json" <<EOF
{"packages": {"": {"name": "fx", "workspaces": ["apps/*"]},
  "apps/web": {"name": "@evidgate/web", "dependencies": {"@angular/localize": "1"}},
  "node_modules/@angular/localize": {"link": true, "resolved": "packages/gone"},
  "node_modules/target2": {"resolved": "$reg/target2/-/target2-1.0.0.tgz"}}}
EOF
  echo 'not json' > "$fx/scope-bad/package-lock.json"
  set +e
  evaluate "$fx/rows" "$fx/exceptions.json" "$fx/scope" > "$fx/out.log" 2>&1
  rc=$?
  set -e
  cat "$fx/out.log"

  local expect=0
  grep -q "fail npm:right-pad" "$fx/out.log" || { echo "self-test: FAIL — unlisted GHSA-dddd-eeee-ffff not flagged" >&2; expect=1; }
  grep -qF "fail nuget:Newtonsoft.Json@12.0.1 moderate CVE-2024-21907 — not allowlisted" "$fx/out.log" || { echo "self-test: FAIL — unlisted CVE-2024-21907 not flagged" >&2; expect=1; }
  grep -q "fail nuget:Old.Package.*expired" "$fx/out.log" || { echo "self-test: FAIL — expired exception not revoked" >&2; expect=1; }
  grep -q "fail npm:tiny-pkg.*ADR-9999.*no docs/adr" "$fx/out.log" || { echo "self-test: FAIL — exception citing a missing ADR file not flagged" >&2; expect=1; }
  grep -q "fail npm:other-pkg.*not an ADR id" "$fx/out.log" || { echo "self-test: FAIL — malformed adr value not flagged" >&2; expect=1; }
  grep -qF "fail npm:target2@1.0.0 high GHSA-ssss-ssss-ssss — exception scope does not cover production path" "$fx/out.log" \
    || { echo "self-test: FAIL — out-of-scope chains not reported at all" >&2; expect=1; }
  for path in \
    "@evidgate/other > mid > target2" \
    "@evidgate/zsorting > @angular/localize > target2" \
    "@evidgate/web > wrapper > @angular/localize > target2" \
    "@evidgate/directws > target2" \
    "@evidgate/aliasws > pm" \
    "@evidgate/v2 > mid2 > target2" \
    "the root package > root-tool > target2" \
    "the root package > @evidgate/web > @angular/localize > target2" \
    "the root package > @evidgate/local > inner > target2"; do
    grep -qF "$path" "$fx/out.log" \
      || { echo "self-test: FAIL — out-of-scope production path '$path' not flagged" >&2; expect=1; }
  done
  grep -q "fail nuget:Newtonsoft.Json.*npm-only" "$fx/out.log" || { echo "self-test: FAIL — a scope on a NuGet finding not flagged" >&2; expect=1; }
  grep -q "fail npm:tinyglobby.*names package 'picomatch'" "$fx/out.log" || { echo "self-test: FAIL — mismatched exception package not flagged" >&2; expect=1; }
  grep -q "fail npm:bad-scope.*must be {\"npm\"" "$fx/out.log" || { echo "self-test: FAIL — malformed scope not flagged" >&2; expect=1; }
  grep -q "fail npm:empty-scope.*lists no chains" "$fx/out.log" || { echo "self-test: FAIL — empty scope not flagged" >&2; expect=1; }
  grep -q "fail npm:no-expiry.*requires an 'expires' date" "$fx/out.log" || { echo "self-test: FAIL — a scoped exception without expires not flagged" >&2; expect=1; }
  grep -q "fail npm:scalar-scope.*must be a non-empty list" "$fx/out.log" || { echo "self-test: FAIL — a scalar scope not flagged" >&2; expect=1; }
  grep -q "fail npm:list-item.*non-empty 'workspace' and 'via'" "$fx/out.log" || { echo "self-test: FAIL — non-string scope pair not flagged" >&2; expect=1; }
  [[ "$rc" -eq 1 ]] || { echo "self-test: FAIL — exit $rc, expected 1" >&2; expect=1; }

  # A dev-only second chain stays exempt: --omit=dev never traverses it.
  set +e; evaluate "$fx/rows-t2" "$fx/exceptions.json" "$fx/scope-dev" > "$fx/dev.log" 2>&1; rc=$?; set -e
  grep -qF "warn npm:target2@1.0.0 high GHSA-ssss-ssss-ssss — exempted by ADR-0001 (scope: @evidgate/web > @angular/localize)" "$fx/dev.log" \
    || { echo "self-test: FAIL — an in-scope finding is not labelled with its scope" >&2; expect=1; }
  [[ "$rc" -eq 0 ]] || { echo "self-test: FAIL — a dev-only chain must not affect the scope" >&2; expect=1; }
  # A re-pointed `via` is not the registry package of that name.
  set +e; evaluate "$fx/rows-t3" "$fx/exc-t3.json" "$fx/scope-alias" > "$fx/alias.log" 2>&1; rc=$?; set -e
  if [[ "$rc" -ne 1 ]] || ! grep -q "not the registry package of that name" "$fx/alias.log"; then echo "self-test: FAIL — a re-pointed direct dependency must fail" >&2; expect=1; fi
  # An unresolvable required edge and an unreadable lock are scanner failures.
  set +e; evaluate "$fx/rows-t2" "$fx/exceptions.json" "$fx/scope-ghost" > "$fx/ghost.log" 2>&1; rc=$?; set -e
  if [[ "$rc" -ne 2 ]] || ! grep -q "cannot resolve ghost from apps/web" "$fx/ghost.log"; then echo "self-test: FAIL — an unresolvable required edge must exit 2" >&2; expect=1; fi
  set +e; evaluate "$fx/rows-t2" "$fx/exceptions.json" "$fx/scope-link" > "$fx/link.log" 2>&1; rc=$?; set -e
  if [[ "$rc" -ne 2 ]] || ! grep -q "cannot resolve @angular/localize from apps/web" "$fx/link.log"; then echo "self-test: FAIL — a link without a target must exit 2" >&2; expect=1; fi
  set +e; evaluate "$fx/rows-t2" "$fx/exceptions.json" "$fx/scope-bad" > "$fx/bad.log" 2>&1; rc=$?; set -e
  if [[ "$rc" -ne 2 ]] || ! grep -q "package-lock.json is unusable" "$fx/bad.log"; then echo "self-test: FAIL — an unreadable package-lock.json must exit 2" >&2; expect=1; fi

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
    echo "self-test: PASS — unlisted advisories fail, live scoped ADR exception passes, out-of-scope/alias/dev-only/edge case rules hold; stale npm + NuGet locks fail their locked installs"
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
