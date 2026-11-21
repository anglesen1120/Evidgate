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
#   --produce   run the three small producers into --src first (needs `npm ci` and the built
#               workspace packages, `npx tsc -b tsconfig.json`: the Playwright fixtures build imports
#               @evidgate/sdk from its dist output and a system Chrome)
#
# Scrub, byte-regex based so only UTF-8 (or its ASCII subset) is accepted: a testsuite hostname
# attribute becomes "host" and this run's checkout path becomes /repo; a report that still names the
# machine, its host name or the home directory is refused, never uploaded.
# Exit 1: a producer's report is missing, is over the 8 MiB cap before or after scrubbing, is not
# well-formed XML with a testsuite and a testcase, is not UTF-8, or declares a DOCTYPE in any
# encoding (a runner never emits one).
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

python3 - "$SRC" "$OUT" "$ROOT" <<'PY'
import hashlib, json, os, re, socket, sys
import xml.etree.ElementTree as ET
from xml.parsers import expat
from pathlib import Path

MAX_BYTES = 8 * 1024 * 1024
# A runner stamps the machine name into every testsuite. Only a real testsuite start tag is rewritten
# and only at attribute positions, so quoted text, CDATA and testcase attributes stay as they are.
TESTSUITE_TAG = re.compile(rb'<testsuite(?=[\s/>])(?:"[^"]*"|\'[^\']*\'|[^>"\'])*>')
HOSTNAME_ATTR = re.compile(rb'''(\s+)([\w:.-]+)(\s*=\s*)("[^"]*"|'[^']*')''')
XML_ENCODING = re.compile(rb'''\A(?:\xef\xbb\xbf)?\s*<\?xml[^>]*\bencoding\s*=\s*["']([^"']+)["']''', re.I)


def refuse_doctype(*_):
    # expat reports the declaration whatever the encoding, unlike a byte search.
    sys.exit("collect.sh: report declares a DOCTYPE")


def refuse_unsupported_encoding(producer, data):
    # The scrub is byte regex based, so anything but UTF-8 and its ASCII subset has no safe rewrite.
    if data.startswith((b"\xff\xfe", b"\xfe\xff")) or b"\x00" in data:
        sys.exit(f"collect.sh: {producer}: report is not UTF-8 (BOM or NUL byte)")
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        sys.exit(f"collect.sh: {producer}: report is not valid UTF-8")
    declared = XML_ENCODING.match(data)
    if declared is not None and declared.group(1).lower() not in (b"utf-8", b"us-ascii"):
        sys.exit(f"collect.sh: {producer}: report declares encoding {declared.group(1).decode('ascii', 'replace')}, not UTF-8")


def markup_end(data, start):
    for opener, closer in ((b"<!--", b"-->"), (b"<![CDATA[", b"]]>"), (b"<?", b"?>")):
        if data.startswith(opener, start):
            end = data.find(closer, start + len(opener))
            return len(data) if end < 0 else end + len(closer)
    quote, index = 0, start + 1
    while index < len(data):
        byte = data[index]
        if quote:
            if byte == quote:
                quote = 0
        elif byte in (0x22, 0x27):
            quote = byte
        elif byte == 0x3E:
            return index + 1
        index += 1
    return len(data)


def rewrite_hostname(match):
    if match.group(2) != b"hostname":
        return match.group(0)
    return match.group(1) + b"hostname" + match.group(3) + b'"host"'


def scrub_hostnames(data):
    # Walk markup so a <testsuite ..> inside a comment, CDATA or another tag's value is never matched.
    result, position = bytearray(), 0
    while position < len(data):
        start = data.find(b"<", position)
        if start < 0:
            result += data[position:]
            break
        result += data[position:start]
        tag = TESTSUITE_TAG.match(data, start)
        if tag is not None:
            result += HOSTNAME_ATTR.sub(rewrite_hostname, tag.group(0))
            position = tag.end()
        else:
            end = markup_end(data, start)
            result += data[start:end]
            position = end
    return bytes(result)


def refuse_residual(producer, data):
    # A report that still names this machine fails the run; a leak never reaches the upload set.
    values = [root, os.path.realpath(root)]
    machine = socket.gethostname()
    if len(machine) >= 4 and machine != "host":
        values.append(machine)
    home = os.environ.get("HOME", "")
    if home not in ("", "/"):
        values.append(home)
    for value in values:
        if value and os.fsencode(value) in data:
            sys.exit(f"collect.sh: {producer}: report still names this machine; refusing to publish it")


src, out, root = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
# The runner's checkout path (an absolute path that names a user) becomes the fixed /repo.
PATH_PREFIXES = [
    re.compile(rb"(?<![\w.-])" + re.escape(os.fsencode(path)) + rb"(?![\w.-])")
    for path in dict.fromkeys([root, os.path.realpath(root)])
]
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
        doc = ET.fromstring(data)
    except expat.ExpatError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")
    except ET.ParseError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")
    refuse_unsupported_encoding(producer, data)
    suites = [doc] if doc.tag == "testsuite" else doc.findall("testsuite")
    cases = doc.findall(".//testcase")
    if doc.tag not in ("testsuites", "testsuite") or not suites or not cases:
        sys.exit(f"collect.sh: {producer}: no testsuite/testcase in the report")
    data = scrub_hostnames(data)
    for prefix in PATH_PREFIXES:
        data = prefix.sub(b"/repo", data)
    if len(data) > MAX_BYTES:
        sys.exit(f"collect.sh: {producer}: report exceeds {MAX_BYTES} bytes")
    refuse_residual(producer, data)
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
