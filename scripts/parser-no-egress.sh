#!/usr/bin/env bash
# The parser child's no-egress test (Parser.Tests ChildNetworkTests) only asserts anything where the process has no
# network, and a plain test run on a networked host skips it. This runs that one class from the Release output a
# prior `build` stage produced, inside a container started with `--network none`, with EVIDGATE_REQUIRE_NO_EGRESS=1
# so a precondition that would skip fails the run, and then requires the JUnit report to show the test passed
# and nothing skipped.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Plain (non-chiseled) runtime: the test's scripted child needs /bin/bash, which the chiseled images lack.
# 10.0.12-noble (aspnet: the Evidence module needs Microsoft.AspNetCore.App) multi-arch index digest, read from mcr.microsoft.com on 2026-11-12; the tag matches the Api runtime.
IMAGE="mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4"
TEST_CLASS="Parser.Tests.ChildNetworkTests"
# Overridable so the negative proof (a networked container must fail, not skip) can be run without editing the file.
NETWORK="${EVIDGATE_PARSER_EGRESS_NETWORK:-none}"

TEST_DLL="$ROOT/tests/Parser.Tests/bin/Release/net10.0/Parser.Tests.dll"
if [[ ! -f "$TEST_DLL" ]]; then
  echo "parser-no-egress: $TEST_DLL is missing: run the build stage first" >&2
  exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
  echo "parser-no-egress: docker is required" >&2
  exit 2
fi

# `docker cp` instead of a bind mount: it works against a remote or VM-backed daemon whose host does not share
# the checkout path. Only what the test reads is staged: the solution file (the repo-root marker), the fixtures,
# and the Release output of the test project and of the child it launches.
work="$(mktemp -d)"
cid=""
cleanup() {
  [[ -z "$cid" ]] || docker rm -f "$cid" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
stage_dir="$work/repo"
mkdir -p "$stage_dir/tests/Parser.Tests/bin" "$stage_dir/src/Evidgate.Parser/bin"
cp "$ROOT/Evidgate.slnx" "$stage_dir/"
cp -R "$ROOT/fixtures" "$stage_dir/fixtures"
cp -R "$ROOT/tests/Parser.Tests/bin/Release" "$stage_dir/tests/Parser.Tests/bin/Release"
cp -R "$ROOT/src/Evidgate.Parser/bin/Release" "$stage_dir/src/Evidgate.Parser/bin/Release"

echo "   image: $IMAGE  network: $NETWORK"
cid="$(docker create --network "$NETWORK" -w /repo \
  -e EVIDGATE_REQUIRE_NO_EGRESS=1 -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  "$IMAGE" dotnet exec /repo/tests/Parser.Tests/bin/Release/net10.0/Parser.Tests.dll \
  -class "$TEST_CLASS" -result-junit /tmp/parser-no-egress.junit.xml)"
docker cp "$stage_dir/." "$cid:/repo"
docker start -a "$cid"
results="$work/results"
mkdir -p "$results"
docker cp "$cid:/tmp/parser-no-egress.junit.xml" "$results/" >/dev/null 2>&1 || true

report="$results/parser-no-egress.junit.xml"
if [[ ! -f "$report" ]]; then
  echo "parser-no-egress: the run wrote no JUnit report" >&2
  exit 1
fi
# xUnit v3 writes one <testsuite> carrying the totals; passed = tests - failures - errors - skipped.
read -r tests failures errors skipped < <(python3 - "$report" <<'PY'
import sys, xml.etree.ElementTree as ET
t = tf = te = ts = 0
for s in ET.parse(sys.argv[1]).getroot().iter("testsuite"):
    t += int(s.get("tests", 0)); tf += int(s.get("failures", 0))
    te += int(s.get("errors", 0)); ts += int(s.get("skipped", 0))
print(t, tf, te, ts)
PY
)
echo "   $TEST_CLASS: tests=$tests failures=$failures errors=$errors skipped=$skipped"
if [[ "$tests" -lt 1 || "$failures" -ne 0 || "$errors" -ne 0 || "$skipped" -ne 0 ]]; then
  echo "parser-no-egress: the class must report at least one passed test and none skipped or failed" >&2
  exit 1
fi
