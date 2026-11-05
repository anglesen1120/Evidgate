#!/usr/bin/env bash
# Assembles Evidgate's own CI test reports in the shape a customer uploads: one JUnit XML file per
# producer (xUnit v3, Vitest, Playwright) plus manifest.json listing each file with its sha256 and
# counts. Nothing is produced unless --produce is passed, so the gate's output and timings are
# unchanged; the producers' reporter switches below are the same ones the runners already document.
#
#   scripts/dogfood/collect.sh [--produce] [--src DIR] [--out DIR]
#
#   --src DIR   directory holding xunit.junit.xml, vitest.junit.xml, playwright.junit.xml
#               (default artifacts/dogfood/raw; the committed sample is scripts/dogfood/testdata)
#   --out DIR   upload set to write (default artifacts/dogfood/upload)
#   --produce   run the three small producers into --src first (needs a built workspace and the
#               Playwright fixtures bundle prerequisites)
#
# Exit 1: a producer's report is missing, is not well-formed XML with a testsuite and a testcase, or
# declares a DOCTYPE (a runner never emits one). The server-side parse of the same bytes is the
# Evidence parser profile, covered by tests/Tenancy.IntegrationTests (DogfoodReportTests).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/artifacts/dogfood/raw"
OUT="$ROOT/artifacts/dogfood/upload"
PRODUCE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --produce) PRODUCE=1 ;;
    --src) SRC="${2:?--src needs a directory}"; shift ;;
    --out) OUT="${2:?--out needs a directory}"; shift ;;
    *) echo "collect.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

if [[ "$PRODUCE" == "1" ]]; then
  mkdir -p "$SRC"
  # xUnit v3 console runner's own JUnit writer (the MTP --report-* flags exist only under `dotnet test`).
  dotnet run --project "$ROOT/tests/Canonicalization.UnitTests" -c Release -- \
    -result-junit "$SRC/xunit.junit.xml"
  (cd "$ROOT/packages/adapter-junit" && npx vitest run --reporter=junit --outputFile="$SRC/vitest.junit.xml")
  (cd "$ROOT/apps/web" && npx playwright test -c playwright.fixtures.config.ts e2e/shell/settings-policy \
    --reporter=junit >"$SRC/playwright.junit.xml")
fi

mkdir -p "$OUT"
python3 - "$SRC" "$OUT" <<'PY'
import hashlib, json, shutil, sys
import xml.etree.ElementTree as ET
from pathlib import Path

src, out = Path(sys.argv[1]), Path(sys.argv[2])
producers = ["xunit", "vitest", "playwright"]
entries = []
for producer in producers:
    name = f"{producer}.junit.xml"
    path = src / name
    if not path.is_file():
        sys.exit(f"collect.sh: {producer}: missing report {path}")
    data = path.read_bytes()
    if b"<!DOCTYPE" in data:
        sys.exit(f"collect.sh: {producer}: report declares a DOCTYPE")
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")
    suites = [root] if root.tag == "testsuite" else root.findall("testsuite")
    cases = root.findall(".//testcase")
    if root.tag not in ("testsuites", "testsuite") or not suites or not cases:
        sys.exit(f"collect.sh: {producer}: no testsuite/testcase in the report")
    entries.append({
        "producer": producer,
        "file": name,
        "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data),
        "tests": len(cases),
        "failed": sum(1 for c in cases if c.find("failure") is not None or c.find("error") is not None),
        "skipped": sum(1 for c in cases if c.find("skipped") is not None),
    })
    shutil.copyfile(path, out / name)
manifest = {"schema": "dogfood-collect/1", "producers": entries}
(out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest, indent=2))
PY
