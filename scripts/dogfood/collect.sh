#!/usr/bin/env bash
# Assembles Evidgate's own CI test reports in the shape a customer uploads: one JUnit XML file per
# producer (xUnit v3, Vitest, Playwright) plus manifest.json listing each file with its sha256 and
# counts. No producer runs unless --produce is passed, so the gate's output and timings are
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
# Scrub, on the parsed tree so only UTF-8 (or its ASCII subset) is accepted: a testsuite hostname
# attribute becomes "host" and this run's checkout path becomes /repo in every attribute value and
# text node. The residual check reads element names, attribute names and namespace URIs as well as
# values, text, tail and each element's joined text, and compares the match form
# NFKC(casefold(NFKC(text))) with default-ignorable (Cf) and combining (Mn, Me) characters removed,
# raw and after one strict percent-decoding; a malformed escape refuses rather than decoding
# leniently. A report that still names the machine, its host name (socket.gethostname() or
# $HOSTNAME) or the home directory is refused, never uploaded. Each upload file is written to a
# sibling temporary created exclusively (a planted link there is skipped, never followed), flushed
# and fsynced before any of them is renamed into place, manifest last. The previous manifest.json is
# removed immediately before the first rename, so after any failure in the upload set no manifest
# describes files from two runs; a failure also removes this run's temporaries. A green run deletes
# stale *.junit.xml, manifest.json and .*.tmp-* regular files or links directly in --out. Nothing is
# written to --out unless all three producers pass.
# Exit 1: a producer's report is missing, is over the 8 MiB cap before or after scrubbing, is not
# well-formed XML with a testsuite and a testcase, is not UTF-8, or declares a DOCTYPE in any
# encoding (a runner never emits one), still names this machine, or holds a malformed percent
# escape; or the upload set cannot be renamed into place, or a stale upload file cannot be removed.
# Exit 2: --src and --out resolve to the same directory, and unknown arguments.
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

# Absolute, and physical so a symlink alias of --src cannot pass as a different --out, before any
# producer changes directory.
mkdir -p "$SRC" "$OUT"
SRC="$(cd "$SRC" && pwd -P)"
OUT="$(cd "$OUT" && pwd -P)"
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
import fnmatch, hashlib, json, os, re, socket, sys, unicodedata, urllib.parse
import xml.etree.ElementTree as ET
from xml.parsers import expat
from pathlib import Path

MAX_BYTES = 8 * 1024 * 1024
DECLARATION = b'<?xml version="1.0" encoding="UTF-8"?>\n'
XML_ENCODING = re.compile(rb'''\A(?:\xef\xbb\xbf)?\s*<\?xml[^>]*\bencoding\s*=\s*["']([^"']+)["']''', re.I)
COUNTED_ELEMENTS = ("testsuite", "testcase", "failure", "error", "skipped")


def refuse_unsupported_encoding(producer, data):
    # The tree scrub, residual check and serialisation all assume UTF-8 and its ASCII subset.
    if data.startswith((b"\xff\xfe", b"\xfe\xff")) or b"\x00" in data:
        sys.exit(f"collect.sh: {producer}: report is not UTF-8 (BOM or NUL byte)")
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        sys.exit(f"collect.sh: {producer}: report is not valid UTF-8")
    declared = XML_ENCODING.match(data)
    if declared is not None and declared.group(1).lower() not in (b"utf-8", b"us-ascii"):
        sys.exit(f"collect.sh: {producer}: report declares encoding {declared.group(1).decode('ascii', 'replace')}, not UTF-8")


def parse_report(producer, data):
    def refuse(*_):
        # expat reports the declaration whatever the encoding, unlike a byte search.
        sys.exit(f"collect.sh: {producer}: report declares a DOCTYPE")

    try:
        scanner = expat.ParserCreate()
        scanner.StartDoctypeDeclHandler = refuse
        scanner.Parse(data, True)
        return ET.fromstring(data)
    except expat.ExpatError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")
    except ET.ParseError as error:
        sys.exit(f"collect.sh: {producer}: not well-formed XML ({error})")


def walk(doc):
    # Iterative so a deeply nested report cannot exhaust the interpreter stack.
    stack = [doc]
    while stack:
        node = stack.pop()
        yield node
        stack.extend(node)


def scrubbed(text, prefixes):
    if not text:
        return text
    for pattern in prefixes:
        text = pattern.sub("/repo", text)
    return text


def scrub_tree(doc, prefixes):
    for node in walk(doc):
        if node.tag == "testsuite" and "hostname" in node.attrib:
            node.set("hostname", "host")
        for key in list(node.attrib):
            node.set(key, scrubbed(node.attrib[key], prefixes))
        node.text = scrubbed(node.text, prefixes)
        node.tail = scrubbed(node.tail, prefixes)


MALFORMED_PERCENT = re.compile(rb"%(?![0-9A-Fa-f]{2})")
IGNORABLE = frozenset(("Cf", "Mn", "Me"))
STALE_PATTERNS = ("*.junit.xml", "manifest.json", ".*.tmp-*")


def refuse(producer, reason):
    sys.exit(f"collect.sh: {producer}: {reason}; refusing to publish it")


def without_ignorables(text):
    # NFKC expands or composes what the raw bytes hid; the removed categories render as nothing or
    # as a mark on the previous character, so dropping them cannot make a name stop matching. No
    # ASCII character is Cf, Mn or Me, so the per-character scan only runs on non-ASCII text.
    text = unicodedata.normalize("NFKC", text)
    if text.isascii():
        return text
    return "".join(char for char in text if unicodedata.category(char) not in IGNORABLE)


def match_form(text):
    return without_ignorables(unicodedata.normalize("NFKC", text).casefold())


def decode_once(producer, text):
    # One pass only: unquote_to_bytes leaves a malformed escape literal, and a lenient decoder could
    # hide a name, so a bad escape or a non-UTF-8 payload refuses.
    data = text.encode("utf-8")
    if MALFORMED_PERCENT.search(data):
        refuse(producer, "report has a malformed percent escape")
    try:
        return urllib.parse.unquote_to_bytes(data).decode("utf-8")
    except UnicodeDecodeError:
        refuse(producer, "percent escapes do not decode to UTF-8")


def matching_forms(producer, value):
    # A report can spell a name without spelling it: match the raw text and, only when a percent
    # sign survives normalisation, its once-decoded reading.
    normalised = unicodedata.normalize("NFKC", value)
    forms = [without_ignorables(normalised.casefold())]
    if "%" in normalised:
        forms.append(match_form(decode_once(producer, normalised)))
    return list(dict.fromkeys(forms))


def refuse_residual(producer, doc, values):
    # A report that still names this machine fails the run; a leak never reaches the upload set.
    # Names cannot be scrubbed, so a matching element or attribute name refuses too.
    expected = {match_form(secret) for secret in values}
    expected.discard("")
    for node in walk(doc):
        inspected = [node.text, node.tail, *node.attrib, *node.attrib.values()]
        if isinstance(node.tag, str):
            inspected.append(node.tag)
        if len(node):
            # A child element splits an identifier across text, its own text and a tail.
            inspected.append("".join(node.itertext()))
        for value in inspected:
            if value and any(secret in form for form in matching_forms(producer, value) for secret in expected):
                refuse(producer, "report still names this machine")


def element_counts(doc):
    counts = dict.fromkeys(COUNTED_ELEMENTS, 0)
    for node in walk(doc):
        if node.tag in counts:
            counts[node.tag] += 1
    return counts


def serialise(producer, doc, expected):
    # Serialising the tree normalises quoting, CDATA and comments, which the JUnit mapper does not
    # read; failure message/type, testcase classname/name and system-out text survive as values.
    try:
        payload = DECLARATION + ET.tostring(doc, encoding="utf-8", xml_declaration=False)
    except RecursionError:
        sys.exit(f"collect.sh: {producer}: report nests too deeply to serialise")
    try:
        reparsed = ET.fromstring(payload)
    except ET.ParseError as error:
        sys.exit(f"collect.sh: {producer}: serialised report is not well-formed XML ({error})")
    if element_counts(reparsed) != expected:
        sys.exit(f"collect.sh: {producer}: serialised report changed the element counts")
    return payload


def stage_atomic(out, name, data, created):
    # A sibling temp keeps the rename atomic, and replacing the target removes a link there instead
    # of writing through it. Exclusive creation means a planted link at the temp name is skipped,
    # never followed, and created only lists paths this run owns.
    for attempt in range(100):
        temp = out / f".{name}.tmp-{attempt}"
        try:
            descriptor = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        except FileExistsError:
            continue
        created.append(temp)
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        return temp
    sys.exit(f"collect.sh: {name}: no free temporary name in {out}")


def publish(out, pending, payload, created):
    # Every file is validated and staged before the first rename. The previous manifest goes first,
    # so a failure after that point cannot leave a manifest describing files from two runs.
    staged = [(name, stage_atomic(out, name, data, created)) for name, data in pending]
    manifest_temp = stage_atomic(out, "manifest.json", payload, created)
    (out / "manifest.json").unlink(missing_ok=True)
    for name, temp in staged:
        os.replace(temp, out / name)
    os.replace(manifest_temp, out / "manifest.json")


def remove_stale(out, produced):
    # Direct children only, and only names this script owns; a directory is left alone. An entry
    # that vanished is ignored; any other failure stops the run rather than leave a stale file.
    for entry in out.iterdir():
        if entry.name in produced or not any(fnmatch.fnmatch(entry.name, pattern) for pattern in STALE_PATTERNS):
            continue
        if entry.is_symlink() or entry.is_file():
            try:
                entry.unlink()
            except FileNotFoundError:
                pass
            except PermissionError as error:
                sys.exit(f"collect.sh: cannot remove stale upload file {entry} ({error})")


src, out, root = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
# The runner's checkout path (an absolute path that names a user) becomes the fixed /repo.
checkout = list(dict.fromkeys([root, os.path.realpath(root)]))
prefixes = [
    re.compile(r"(?<![\w.-])" + re.escape(prefix) + r"(?![\w.-])")
    for prefix in sorted(checkout, key=len, reverse=True)
]
residual = [value for value in checkout if value]
# Both the real host name and an injected $HOSTNAME are refused; each is additive, never a
# replacement, so setting one cannot switch the other off.
machines = [socket.gethostname(), os.environ.get("HOSTNAME", "")]
residual.extend(machine for machine in machines if len(machine) >= 4 and machine.casefold() != "host")
home = os.environ.get("HOME", "")
if home not in ("", "/"):
    residual.append(home)
# EVIDGATE_COLLECT_EXTRA_RESIDUAL (comma separated) adds identifiers that are refused as well. It is
# additive only on purpose: it never replaces the real host name, HOME or checkout path, so a caller
# cannot use it to switch the guard off; unset or empty changes nothing.
residual.extend(item.strip() for item in os.environ.get("EVIDGATE_COLLECT_EXTRA_RESIDUAL", "").split(",") if item.strip())
producers = ["xunit", "vitest", "playwright"]
entries = []
pending = []
for producer in producers:
    name = f"{producer}.junit.xml"
    path = src / name
    if not path.is_file():
        sys.exit(f"collect.sh: {producer}: missing report {path}")
    with path.open("rb") as handle:
        data = handle.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        sys.exit(f"collect.sh: {producer}: report exceeds {MAX_BYTES} bytes")
    doc = parse_report(producer, data)
    refuse_unsupported_encoding(producer, data)
    suites = [doc] if doc.tag == "testsuite" else doc.findall("testsuite")
    cases = doc.findall(".//testcase")
    if doc.tag not in ("testsuites", "testsuite") or not suites or not cases:
        sys.exit(f"collect.sh: {producer}: no testsuite/testcase in the report")
    expected = element_counts(doc)
    scrub_tree(doc, prefixes)
    refuse_residual(producer, doc, residual)
    data = serialise(producer, doc, expected)
    if len(data) > MAX_BYTES:
        sys.exit(f"collect.sh: {producer}: report exceeds {MAX_BYTES} bytes")
    entries.append({
        "producer": producer,
        "file": name,
        "sha256": hashlib.sha256(data).hexdigest(),
        "bytes": len(data),
        "tests": len(cases),
        "failed": sum(1 for c in cases if c.find("failure") is not None or c.find("error") is not None),
        "skipped": sum(1 for c in cases if c.find("skipped") is not None),
    })
    pending.append((name, data))
# Every producer passed: only now does anything reach --out, manifest last, so a refusal leaves it
# untouched. A failed rename keeps this run's complete earlier files and publishes no manifest; the
# previous manifest went before the first rename, so a partial set is never mistaken for a finished
# one.
manifest = {"schema": "dogfood-collect/1", "producers": entries}
payload = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
created = []
try:
    publish(out, pending, payload, created)
except Exception as error:
    for temp in created:
        try:
            temp.unlink()
        except FileNotFoundError:
            pass
    sys.exit(f"collect.sh: writing the upload set into {out} failed ({error}); refusing to publish a partial set")
remove_stale(out, {name for name, _ in pending} | {"manifest.json"})
print(json.dumps(manifest, indent=2))
PY
exit "$PRODUCER_STATUS"
