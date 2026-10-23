#!/usr/bin/env bash
# DECISIONS §4 / 00 §5.7 S6 / 01.7.1-T2 (AC4): merge the npm + NuGet licence
# inventories and fail the build when a package carries a licence on the deny
# list (AGPL/SSPL/BUSL/RPL/non-commercial/paid-maintenance EULA — all encoded in
# scripts/licence-policy.json), or a warn-level licence (LGPL, GPL, MPL, EPL,
# unlicensed/unknown) with no ADR-id exception to cover it.
#
# Prerequisites (both are verify.sh stages — run it first or in CI):
#   npm ci                      -> node_modules, read by license-checker-rseidelsohn
#   dotnet restore --locked-mode-> obj/project.assets.json, read by nuget-license
#   dotnet tool restore         -> performed here; tools come from .config/dotnet-tools.json
#
# usage: scripts/licence-scan.sh [--policy <file>] [--report <file>]
#                                [--npm-only | --dotnet-only] [--self-test]
#                                [--verbose] [-h|--help]
#
# Exit 0: every installed package has an allowed licence or an ADR exception.
# Exit 1: names every denied/unexcepted package, plus every exceptions key that
#         is not ^ADR-[0-9]{4}$ or whose docs/adr/<NNNN>-*.md file is missing.
# Exit 2: a scanner failed — including an EMPTY inventory for an enabled
#         ecosystem (a scanner that reports zero packages is a broken scanner,
#         not a clean tree).
# --self-test plants synthetic deny/warn/unknown packages and packs a real
# AGPL/MIT NuGet fixture pair through the actual nuget-license scanner (no repo
# changes, no network beyond the local node_modules scanner and the restored
# tool), asserting the gate detects each denial naming its package and licence —
# the AC4 evidence without a throwaway branch.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

POLICY="scripts/licence-policy.json"
REPORT=""
SELF_TEST=0
VERBOSE=0
DO_NPM=1
DO_DOTNET=1

usage() {
  cat <<'EOF'
usage: scripts/licence-scan.sh [--policy <file>] [--report <file>]
                               [--npm-only | --dotnet-only] [--self-test]
                               [--verbose] [-h|--help]

Merges the npm (license-checker-rseidelsohn) and NuGet (nuget-license) licence
inventories and evaluates them against scripts/licence-policy.json. A denied or
warn-level licence fails (exit 1, naming the package) unless an exceptions
entry keyed by an ADR id covers 'ecosystem:name[@version]'. --self-test proves
the gate on planted fixtures (AC4) without touching the real tree.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --policy)      POLICY="$2"; shift 2 ;;
    --report)      REPORT="$2"; shift 2 ;;
    --npm-only)    DO_DOTNET=0; shift ;;
    --dotnet-only) DO_NPM=0; shift ;;
    --self-test)   SELF_TEST=1; shift ;;
    --verbose|-v)  VERBOSE=1; shift ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "licence-scan: unknown argument '$1'" >&2; usage; exit 2 ;;
  esac
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---------------------------------------------------------------------------
# Evaluator: one python pass turns '<eco>\t<name>\t<ver>\t<licence>' rows into
# verdicts against the policy. Tokens of an SPDX-ish expression are classified
# individually; the worst verdict wins (deny > warn > unknown > allow), so an
# '(MIT OR AGPL-3.0)' union still fails — the permitted side must be picked via
# an ADR exception. warn/unknown pass only when an exceptions[] package pattern
# (fnmatch, case-insensitive) matches.
# ---------------------------------------------------------------------------
evaluate() {
  local rows_file="$1" policy="$2" report_out="$3" verbose="$4"
  python3 - "$rows_file" "$policy" "$report_out" "$verbose" <<'PYEOF'
import glob, json, os, re, sys, fnmatch

rows_file, policy_path, report_out, verbose = sys.argv[1:5]
verbose = verbose == "1"

policy = json.load(open(policy_path))

def compile_list(key):
    return [(re.compile(e["match"], re.IGNORECASE), e["why"]) for e in policy.get(key, [])]

DENY = compile_list("deny")
WARN = compile_list("warn")
ALLOW = compile_list("allow")

# exceptions: { "ADR-NNNN": {"reason": ..., "packages": ["npm:name@ver", ...] } }
# The key is the accepting ADR id — it must look like ADR-NNNN and resolve to a
# real docs/adr/<NNNN>-*.md file, or 'covered by <key>' would cite nothing.
exceptions = []  # (pattern_lower, adr)
bad_keys = []  # (key, why)
for adr, body in (policy.get("exceptions") or {}).items():
    m = re.fullmatch(r"ADR-(\d{4})", adr or "")
    if not m:
        bad_keys.append((adr, "key is not an ADR id (expected ADR-NNNN)"))
        continue
    if not glob.glob(os.path.join("docs", "adr", m.group(1) + "-*.md")):
        bad_keys.append((adr, "no docs/adr/%s-*.md file exists" % m.group(1)))
        continue
    for pat in (body or {}).get("packages", []):
        exceptions.append((pat.lower(), adr))

def find_exception(eco, name, ver):
    for pat in ("{e}:{n}".format(e=eco, n=name), "{e}:{n}@{v}".format(e=eco, n=name, v=ver)):
        for pattern, adr in exceptions:
            if fnmatch.fnmatch(pat.lower(), pattern):
                return adr
    return None

OP = re.compile(r"\s+(?:OR|AND|WITH)\s+", re.IGNORECASE)
PUNCT = re.compile(r"^\W+|\W+$")

def norm_licence(raw):
    # license-checker returns a string, a list [{type|name|url}], or a bare url.
    if isinstance(raw, list):
        parts = []
        for item in raw:
            if isinstance(item, dict):
                parts.append(item.get("type") or item.get("name") or item.get("url") or "")
            else:
                parts.append(str(item))
        raw = " OR ".join(p for p in parts if p)
    if raw is None:
        raw = ""
    raw = str(raw).strip()
    if raw in ("", "UNKNOWN", "?", "NONE"):
        return "UNLICENSED"
    return raw

def classify_token(tok):
    if not tok:
        return "allow", "empty join operand"
    for rx, why in DENY:
        if rx.search(tok):
            return "deny", why
    for rx, why in WARN:
        if rx.search(tok):
            return "warn", why
    for rx, why in ALLOW:
        if rx.search(tok):
            return "allow", why
    return "unknown", "no allow pattern matched"

def classify(expr):
    """Classify a licence expression -> (verdict, reason)."""
    # Whole-expression deny/warn search first so prose like
    # 'Xceed Community Licence' or 'GNU Affero...' is caught even though it is
    # not a valid SPDX expression.
    for rx, why in DENY:
        if rx.search(expr):
            return "deny", why
    for rx, why in WARN:
        if rx.search(expr):
            return "warn", why
    atoms = []
    for part in OP.split(expr):
        part = PUNCT.sub("", part).rstrip("*")
        if part:
            atoms.append(part)
    verdict = "allow"
    reason = ""
    for atom in atoms:
        v, r = classify_token(atom)
        if v == "deny":
            return "deny", r
        if v in ("warn", "unknown"):
            verdict = v
            reason = r
    return verdict, reason

rows = []
with open(rows_file) as fh:
    for line in fh:
        line = line.rstrip("\n")
        if not line:
            continue
        eco, name, ver, lic = line.split("\t", 3)
        rows.append((eco, name, ver, norm_licence(lic)))

if not rows:
    print("licence-scan: merged inventory has no rows — a scanner produced nothing", file=sys.stderr)
    sys.exit(2)

results = []
fails = 0
counts = {"allow": 0, "warn": 0, "deny": 0, "unknown": 0}
# A malformed or dangling exception key is itself a gate failure (exit 1) —
# otherwise 'covered by <key>' would cite an ADR that does not exist.
for key, why in bad_keys:
    fails += 1
    print("  fail licence-policy exceptions key '%s' — %s" % (key, why))
for eco, name, ver, expr in rows:
    verdict, reason = classify(expr)
    counts[verdict] += 1
    adr = find_exception(eco, name, ver)
    hard = verdict in ("deny", "warn", "unknown")
    covered = bool(adr)
    results.append({
        "ecosystem": eco, "package": name, "version": ver,
        "licence": expr, "verdict": verdict, "reason": reason,
        "exception": adr or "",
        "result": ("excepted-" + verdict) if (hard and covered) else verdict,
    })
    if hard and covered:
        print("  warn %s:%s@%s %s — covered by %s (%s)" % (eco, name, ver, expr, adr, reason))
    elif hard:
        fails += 1
        lvl = "fail"
        print("  %s %s:%s@%s %s — %s" % (lvl, eco, name, ver, expr, reason))
    elif verbose:
        print("  ok   %s:%s@%s %s" % (eco, name, ver, expr))

print("licence-scan: %d packages — %d allow, %d warn, %d deny, %d unknown; %d failing"
      % (len(rows), counts["allow"], counts["warn"], counts["deny"], counts["unknown"], fails))
if fails:
    print("licence-scan: add the package to the policy exceptions keyed by an accepted ADR id, or remove it", file=sys.stderr)

if report_out and report_out != "-":
    os.makedirs(os.path.dirname(report_out) or ".", exist_ok=True)
    with open(report_out, "w") as fh:
        json.dump({
            "policy": policy_path,
            "counts": counts,
            "failures": fails,
            "packages": results,
            "policy_errors": [key for key, _why in bad_keys],
        }, fh, indent=2, sort_keys=True)
        fh.write("\n")
    print("licence-scan: merged report -> %s" % report_out)

sys.exit(1 if fails else 0)
PYEOF
}

npm_rows() {
  local checker="node_modules/.bin/license-checker-rseidelsohn"
  if [[ ! -x "$checker" ]]; then
    if [[ -x "node_modules/license-checker-rseidelsohn/bin/license-checker-rseidelsohn.js" ]]; then
      checker="node_modules/license-checker-rseidelsohn/bin/license-checker-rseidelsohn.js"
    else
      echo "licence-scan: license-checker-rseidelsohn not found — run 'npm ci' first" >&2
      return 2
    fi
  fi
  if [[ ! -d node_modules ]]; then
    echo "licence-scan: node_modules missing — run 'npm ci' first" >&2
    return 2
  fi
  if ! "$checker" --json --excludePrivatePackages --start "$1" >"$2"; then
    echo "licence-scan: license-checker failed" >&2
    return 2
  fi
  python3 - "$2" <<'PYEOF'
import json, sys

data = json.load(open(sys.argv[1]))
with open(sys.argv[1] + ".rows", "w") as out:
    for key, info in data.items():
        if "@" in key:
            name, _, ver = key.rpartition("@")
        else:
            name, ver = key, ""
        # 'licenses' may be a str, a dict, or a list of dicts/strings.
        lic = info.get("licenses", "UNKNOWN")
        if isinstance(lic, list):
            parts = [(i.get("type") or i.get("name") or "") if isinstance(i, dict) else str(i) for i in lic]
            lic = " OR ".join(p for p in parts if p)
        elif isinstance(lic, dict):
            lic = lic.get("type") or lic.get("name") or ""
        lic = str(lic).replace("\t", " ").strip() or "UNLICENSED"
        print("npm\t%s\t%s\t%s" % (name, ver, lic), file=out)
PYEOF
  if [[ ! -s "$2.rows" ]]; then
    echo "licence-scan: npm inventory is empty — license-checker-rseidelsohn returned no packages" >&2
    return 2
  fi
  cat "$2.rows"
}

# '<eco>\t<name>\t<ver>\t<licence>' rows from a nuget-license JSON file. Shared
# by the real collector and the self-test's real-scanner case.
nuget_json_rows() {
  python3 - "$1" <<'PYEOF'
import json, sys

data = json.load(open(sys.argv[1]))
if not isinstance(data, list):
    print("licence-scan: unexpected nuget-license JSON shape", file=sys.stderr)
    sys.exit(2)
for p in data:
    name = p.get("PackageId") or p.get("Name") or "?"
    ver = p.get("PackageVersion") or p.get("Version") or ""
    lic = p.get("License") or p.get("LicenseUrl") or "UNLICENSED"
    print("nuget\t%s\t%s\t%s" % (name, ver, lic))
PYEOF
}

dotnet_rows() {
  local out_json="$1"
  if [[ ! -f Evidgate.slnx ]]; then
    echo "licence-scan: Evidgate.slnx not found (cwd must be repo root)" >&2
    return 2
  fi
  if ! find . -name project.assets.json -print -quit | grep -q .; then
    echo "licence-scan: no obj/project.assets.json — run 'dotnet restore --locked-mode Evidgate.slnx' first" >&2
    return 2
  fi
  if ! dotnet tool restore >/dev/null; then
    echo "licence-scan: 'dotnet tool restore' failed" >&2
    return 2
  fi
  local rc=0
  dotnet nuget-license --input Evidgate.slnx --include-transitive \
    --output Json --file-output "$out_json" >/dev/null || rc=$?
  if [[ ! -s "$out_json" ]]; then
    echo "licence-scan: nuget-license produced no JSON (rc=$rc)" >&2
    return 2
  fi
  nuget_json_rows "$out_json" > "$out_json.rows"
  if [[ ! -s "$out_json.rows" ]]; then
    echo "licence-scan: nuget inventory is empty — nuget-license returned no packages (rc=$rc)" >&2
    return 2
  fi
  cat "$out_json.rows"
}

# ---------------------------------------------------------------------------
# Real NuGet-scanner case (11.6.1-T1.1, AC2). The synthetic nuget row in
# self_test proves the evaluator handles a nuget row; this proves the real tool
# chain does: `dotnet pack` fixture packages (AGPL + MIT licence expressions)
# into a local source, restore a consumer against ONLY that source with a
# throwaway NUGET_PACKAGES, run the same `dotnet nuget-license` invocation
# dotnet_rows uses, and feed its rows through the same evaluator. Offline: the
# packages are local; the tool comes from the restored .config manifest.
# Returns 0 pass, 1 fail, 2 a prerequisite (dotnet/tool) is missing.
# ---------------------------------------------------------------------------
nuget_scanner_self_test() {
  local fx="$TMP/selftest/nuget-real" expect=0 rc=0
  mkdir -p "$fx/localsrc" "$fx/consumer" "$fx/pkgs"

  # 1. Pack the two fixture packages into a local source (offline).
  local spec id lic dir
  for spec in "PlantedAgplNuGet.Real AGPL-3.0-only" "PlantedMitNuGet.Real MIT"; do
    id="${spec%% *}"; lic="${spec##* }"; dir="$fx/pkg-$id"
    mkdir -p "$dir"
    cat > "$dir/$id.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>$id</PackageId>
    <Version>1.0.0</Version>
    <PackageLicenseExpression>$lic</PackageLicenseExpression>
    <Authors>self-test</Authors>
    <Description>licence-scan self-test fixture</Description>
  </PropertyGroup>
</Project>
EOF
    if ! dotnet pack "$dir/$id.csproj" -c Release -o "$fx/localsrc" > "$fx/pack.log" 2>&1; then
      echo "self-test: FAIL — 'dotnet pack' of the $id fixture failed" >&2
      tail -5 "$fx/pack.log" >&2
      return 1
    fi
  done

  # 2. Restore a consumer against ONLY the local source, into a throwaway
  #    package folder so the user's NuGet cache is untouched.
  cat > "$fx/consumer/consumer.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PlantedAgplNuGet.Real" Version="1.0.0" />
    <PackageReference Include="PlantedMitNuGet.Real" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
  if ! NUGET_PACKAGES="$fx/pkgs" dotnet restore "$fx/consumer/consumer.csproj" \
       --source "$fx/localsrc" > "$fx/restore.log" 2>&1; then
    echo "self-test: FAIL — restoring the fixture consumer against the local source failed" >&2
    tail -5 "$fx/restore.log" >&2
    return 1
  fi

  # 3. Run the same scanner dotnet_rows uses (tool from .config/dotnet-tools.json).
  if ! dotnet tool restore > "$fx/tool.log" 2>&1; then
    echo "self-test: FAIL — 'dotnet tool restore' failed (nuget-license unavailable)" >&2
    tail -5 "$fx/tool.log" >&2
    return 2
  fi
  if ! dotnet nuget-license --input "$fx/consumer/consumer.csproj" --include-transitive \
       --output Json --file-output "$fx/nuget.json" > "$fx/nl.log" 2>&1; then
    echo "self-test: FAIL — 'dotnet nuget-license' failed on the fixture consumer" >&2
    tail -5 "$fx/nl.log" >&2
    return 1
  fi
  if ! nuget_json_rows "$fx/nuget.json" > "$fx/rows"; then
    echo "self-test: FAIL — nuget-license JSON could not be parsed" >&2
    return 1
  fi

  # 4. Same evaluator, real policy: AGPL must fail naming package + licence,
  #    MIT must pass (positive control).
  set +e
  evaluate "$fx/rows" "$POLICY" "" 1 > "$fx/eval.log" 2>&1
  rc=$?
  set -e
  cat "$fx/eval.log"
  if [[ "$rc" -ne 1 ]]; then
    echo "self-test: FAIL — real nuget-license rows exit $rc, expected 1 (AGPL fixture present)" >&2
    expect=1
  fi
  if ! grep -qF "fail nuget:PlantedAgplNuGet.Real@1.0.0 AGPL-3.0-only" "$fx/eval.log"; then
    echo "self-test: FAIL — the real scanner did not flag PlantedAgplNuGet.Real with its AGPL licence" >&2
    expect=1
  fi
  if grep -q "fail.*PlantedMitNuGet.Real" "$fx/eval.log"; then
    echo "self-test: FAIL — PlantedMitNuGet.Real (MIT) was flagged" >&2
    expect=1
  fi
  if [[ "$expect" -eq 0 ]]; then
    echo "self-test: PASS — real 'dotnet nuget-license' flags PlantedAgplNuGet.Real (AGPL-3.0-only) and passes PlantedMitNuGet.Real (MIT)"
  fi
  return "$expect"
}

self_test() {
  echo "== licence-scan --self-test (AC4: denied licences fail, ADR exceptions pass)"
  local fx="$TMP/selftest"
  mkdir -p "$fx/node_modules/planted-agpl" "$fx/node_modules/planted-lgpl" "$fx/node_modules/planted-nolicense" "$fx/node_modules/planted-mit"

  cat > "$fx/package.json" <<'EOF'
{ "name": "licence-scan-selftest", "private": true, "version": "0.0.0" }
EOF
  cat > "$fx/node_modules/planted-agpl/package.json" <<'EOF'
{ "name": "planted-agpl", "version": "1.0.0", "license": "AGPL-3.0-or-later" }
EOF
  cat > "$fx/node_modules/planted-lgpl/package.json" <<'EOF'
{ "name": "planted-lgpl", "version": "2.1.0", "license": "LGPL-3.0-only" }
EOF
  cat > "$fx/node_modules/planted-nolicense/package.json" <<'EOF'
{ "name": "planted-nolicense", "version": "9.9.9" }
EOF
  cat > "$fx/node_modules/planted-mit/package.json" <<'EOF'
{ "name": "planted-mit", "version": "1.0.0", "license": "MIT" }
EOF

  # npm inventory: real scanner over the synthetic tree — proves the npm half
  # of the gate sees a planted package, not just the evaluator.
  local checker="node_modules/.bin/license-checker-rseidelsohn"
  if [[ ! -x "$checker" ]]; then
    echo "self-test: license-checker-rseidelsohn missing — run 'npm ci' first" >&2
    return 2
  fi
  "$checker" --json --excludePrivatePackages --start "$fx" > "$fx/npm.json"
  python3 - "$fx/npm.json" <<'PYEOF' > "$fx/rows"
import json, sys
data = json.load(open(sys.argv[1]))
for key, info in data.items():
    if "@" in key:
        name, _, ver = key.rpartition("@")
    else:
        name, ver = key, ""
    lic = info.get("licenses", "UNKNOWN")
    if not isinstance(lic, str):
        lic = "UNLICENSED"
    print("npm\t%s\t%s\t%s" % (name, ver, lic))
PYEOF
  # NuGet inventory: a synthetic nuget-license JSON row proves the dotnet half.
  printf 'nuget\tPlantedAgplNuGet\t1.0.0\tAGPL-3.0-only\n' >> "$fx/rows"

  # Self-test policy: real policy + one extra ADR exception covering the LGPL
  # fixture (proves the exception mechanism passes a warn-level licence).
  python3 - "$POLICY" <<'PYEOF' > "$fx/policy.json"
import json, sys
p = json.load(open(sys.argv[1]))
p["exceptions"]["ADR-0001"] = {
    "reason": "self-test exception for planted-lgpl",
    "packages": ["npm:planted-lgpl"],
}
json.dump(p, sys.stdout)
PYEOF

  set +e
  evaluate "$fx/rows" "$fx/policy.json" "$fx/report.json" 1 > "$fx/out.log" 2>&1
  rc=$?
  set -e
  cat "$fx/out.log"

  local expect=0
  # AC2: the log must name the package AND its licence, for both ecosystems —
  # a bare package name would not evidence 'the licence is in the log'.
  local -a denied=(
    "npm:planted-agpl@1.0.0 AGPL-3.0-or-later"
    "nuget:PlantedAgplNuGet@1.0.0 AGPL-3.0-only"
    "npm:planted-nolicense@9.9.9 UNLICENSED"
  )
  for want in "${denied[@]}"; do
    if ! grep -qF "fail $want" "$fx/out.log"; then
      echo "self-test: FAIL — '$want' was not flagged with its package name and licence" >&2
      expect=1
    fi
  done
  if grep -q "fail.*planted-mit" "$fx/out.log"; then
    echo "self-test: FAIL — planted-mit (MIT) was flagged" >&2
    expect=1
  fi
  if grep -q "fail.*planted-lgpl" "$fx/out.log"; then
    echo "self-test: FAIL — planted-lgpl should be covered by the ADR-0001 exception" >&2
    expect=1
  fi
  if [[ "$rc" -ne 1 ]]; then
    echo "self-test: FAIL — evaluator exit $rc, expected 1 (planted denials present)" >&2
    expect=1
  fi
  # Exception-key contract: a key that is not an ADR id (or cites an ADR file
  # that does not exist) must fail the gate, not silently 'cover' packages.
  python3 - "$POLICY" <<'PYEOF' > "$fx/policy-badkey.json"
import json, sys
p = json.load(open(sys.argv[1]))
p["exceptions"]["temp"] = {
    "reason": "not an ADR id",
    "packages": ["npm:planted-agpl"],
}
p["exceptions"]["ADR-9999"] = {
    "reason": "no such ADR file",
    "packages": ["npm:planted-nolicense"],
}
json.dump(p, sys.stdout)
PYEOF
  set +e
  evaluate "$fx/rows" "$fx/policy-badkey.json" "" 0 > "$fx/badkey.log" 2>&1
  rc=$?
  set -e
  if ! grep -q "exceptions key 'temp'.*not an ADR id" "$fx/badkey.log" \
    || ! grep -q "exceptions key 'ADR-9999'.*no docs/adr/9999" "$fx/badkey.log"; then
    echo "self-test: FAIL — bad exception keys not flagged" >&2
    cat "$fx/badkey.log" >&2
    expect=1
  fi
  if [[ "$rc" -ne 1 ]]; then
    echo "self-test: FAIL — bad-key policy exit $rc, expected 1" >&2
    expect=1
  fi

  # Empty inventory fails closed: a stubbed nuget-license that writes '[]' must
  # make the dotnet collector exit 2, never a vacuous pass. (The npm half uses
  # the same -s guard on its rows file.) Run it in a throwaway cwd that carries
  # the two files dotnet_rows guards on (Evidgate.slnx, obj/project.assets.json)
  # so the check never depends on the real tree being restored.
  mkdir -p "$fx/fakebin" "$fx/fakeroot/obj"
  cat > "$fx/fakebin/dotnet" <<'EOF'
#!/usr/bin/env bash
for arg in "$@"; do case "$arg" in --file-output) n=1 ;; *) [[ "$n" -eq 1 ]] && { echo '[]' > "$arg"; n=0; } ;; esac; done
exit 0
EOF
  chmod +x "$fx/fakebin/dotnet"
  : > "$fx/fakeroot/Evidgate.slnx"
  : > "$fx/fakeroot/obj/project.assets.json"
  set +e
  ( cd "$fx/fakeroot" && PATH="$fx/fakebin:$PATH" dotnet_rows "$fx/empty.json" > "$fx/empty-rows" 2> "$fx/empty.log" )
  rc=$?
  set -e
  if [[ "$rc" -ne 2 ]] || ! grep -q "nuget inventory is empty" "$fx/empty.log"; then
    echo "self-test: FAIL — empty nuget inventory exit $rc (expected 2)" >&2
    cat "$fx/empty.log" >&2
    expect=1
  fi

  # 11.6.1-T1.1 (AC2): the real NuGet scanner, not just the evaluator. A missing
  # dotnet/tool is a hard prerequisite failure (exit 2).
  local real_rc=0
  nuget_scanner_self_test || real_rc=$?
  if [[ "$real_rc" -eq 2 ]]; then
    return 2
  elif [[ "$real_rc" -ne 0 ]]; then
    expect=1
  fi

  if [[ "$expect" -eq 0 ]]; then
    echo "self-test: PASS — planted AGPL (npm+nuget) and unlicensed packages fail; LGPL+ADR exception passes; bad exception keys and empty inventory are hard failures; the real nuget-license flags a packed AGPL package"
  fi
  return "$expect"
}

if [[ "$SELF_TEST" -eq 1 ]]; then
  self_test
  exit $?
fi

ROWS="$TMP/rows"
: > "$ROWS"
if [[ "$DO_NPM" -eq 1 ]]; then
  echo "== licence-scan: npm inventory (license-checker-rseidelsohn)"
  npm_rows "$ROOT" "$TMP/npm.json" >> "$ROWS"
fi
if [[ "$DO_DOTNET" -eq 1 ]]; then
  echo "== licence-scan: nuget inventory (nuget-license)"
  dotnet_rows "$TMP/nuget.json" >> "$ROWS"
fi

evaluate "$ROWS" "$POLICY" "$REPORT" "$VERBOSE"
