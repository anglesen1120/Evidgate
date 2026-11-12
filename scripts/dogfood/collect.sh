#!/usr/bin/env bash
# Assembles Evidgate's own CI test reports in the shape a customer uploads: one JUnit XML file per
# producer (xUnit v3, Vitest, Playwright) plus manifest.json listing each file with its sha256 and
# counts. Nothing is produced unless --produce is passed, so the gate's output and timings are
# unchanged; the producers' reporter switches below are the same ones the runners already document.
#
#   scripts/dogfood/collect.sh [--produce] [--src DIR] [--out DIR]
#
#   --src DIR   directory holding xunit.junit.xml, vitest.junit.xml, playwright.junit.xml
#               (default artifacts/dogfood/raw; the committed sample is fixtures/reports/own-ci)
#   --out DIR   upload set to write (default artifacts/dogfood/upload)
#   --produce   run the three small producers into --src first (needs a built workspace and the
#               Playwright fixtures bundle prerequisites)
#
# Exit 1: a producer's report is missing, is larger than the 8 MiB report cap, is not well-formed XML
# with a testsuite and a testcase, or declares a DOCTYPE in any encoding (a runner never emits one).
# A producer that exits non-zero because tests failed still has its report collected; the script then
# exits with that producer's status after the manifest is written. The server-side parse of the same bytes is the
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

# Absolute before any producer changes directory.
mkdir -p "$SRC" "$OUT"
SRC="$(cd "$SRC" && pwd)"
OUT="$(cd "$OUT" && pwd)"
if [[ "$SRC" == "$OUT" ]]; then
  echo "collect.sh: --src and --out must differ" >&2
  exit 2
fi

PRODUCER_STATUS=0
if [[ "$PRODUCE" == "1" ]]; then
  # xUnit v3 console runner's own JUnit writer (the MTP --report-* flags exist only under `dotnet test`).
  # An old report must never stand in for a producer that failed to start.
  rm -f "$SRC/xunit.junit.xml" "$SRC/vitest.junit.xml" "$SRC/playwright.junit.xml"
  # A failing test still writes a valid report, so every producer runs and the first non-zero status is
  # returned once the upload set is assembled; a producer that wrote no report fails the collection below.
  dotnet run --project "$ROOT/tests/Canonicalization.UnitTests" -c Release -- \
    -result-junit "$SRC/xunit.junit.xml" || PRODUCER_STATUS=$?
  (cd "$ROOT/packages/adapter-junit" && npx vitest run --reporter=junit --outputFile="$SRC/vitest.junit.xml") \
    || { s=$?; [[ "$PRODUCER_STATUS" != 0 ]] || PRODUCER_STATUS=$s; }
  (cd "$ROOT/apps/web" && npx playwright test -c playwright.fixtures.config.ts e2e/shell/settings-policy \
    --reporter=junit >"$SRC/playwright.junit.xml") \
    || { s=$?; [[ "$PRODUCER_STATUS" != 0 ]] || PRODUCER_STATUS=$s; }
fi

python3 - "$SRC" "$OUT" <<'PY'
import hashlib, json, sys
import xml.etree.ElementTree as ET
from xml.parsers import expat
from pathlib import Path

MAX_BYTES = 8 * 1024 * 1024


def refuse_doctype(*_):
    # expat reports the declaration whatever the encoding, unlike a byte search.
    sys.exit("collect.sh: report declares a DOCTYPE")


src, out = Path(sys.argv[1]), Path(sys.argv[2])
producers = ["xunit", "vitest", "playwright"]
entries = []
for producer in producers:
    name = f"{producer}.junit.xml"
    path = src / name
    if not path.is_file():
        sys.exit(f"collect.sh: {producer}: missing report {path}")
    with path.open("rb") as handle:
        data = handle.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        sys.exit(f"collect.sh: {producer}: report exceeds {MAX_BYTES} bytes")
    try:
        scanner = expat.ParserCreate()
        scanner.StartDoctypeDeclHandler = refuse_doctype
        scanner.Parse(data, True)
        root = ET.fromstring(data)
    except expat.ExpatError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")
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
    (out / name).write_bytes(data)
manifest = {"schema": "dogfood-collect/1", "producers": entries}
(out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest, indent=2))
PY
exit "$PRODUCER_STATUS"
