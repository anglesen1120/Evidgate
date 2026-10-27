#!/usr/bin/env bash
# 01.7.1-T4.2 (AC7): produce a CycloneDX SBOM for each ecosystem as build
# artifacts. npm: @cyclonedx/cyclonedx-npm (6.0.1, root devDependency) over the
# workspace tree; NuGet: the CycloneDX .NET tool (6.2.0, .config/dotnet-tools.json)
# over Evidgate.slnx. Both emit spec 1.6 JSON into artifacts/sbom/ for CI upload
# (the workflow owns the upload + retention) and for the 01.10.1 dogfooding
# path, which consumes standard CycloneDX JSON.
#
# Prerequisites — BOTH MUST already have run (they are verify.sh stages):
#   npm ci                        -> node_modules + package-lock.json
#   dotnet restore --locked-mode  -> obj/project.assets.json
# The .NET generator runs with -dpr/--disable-package-restore, so it never
# restores: the resolved package graph must exist beforehand, and an unlocked
# restore can therefore never rewrite packages.lock.json behind the gate's back.
# (Measured: a cold unlocked restore made this stage ~20 s; with -dpr it is ~2 s.)
#
# 10.2.1-T1.3 (AC3) adds the per-image SBOMs below: one CycloneDX document per
# deployable image (evidgate-api, evidgate-worker, evidgate-migrator) generated
# from that image's entry-point project, plus the SBOM↔packages.lock.json
# consistency check (scripts/check-image-sbom-lock.py) that fails when the two
# disagree. The image adds nothing but the digest-pinned chiseled aspnet runtime
# (the Api's `-extra` variant ships ICU + tzdata as OS packages, which CycloneDX
# does not enumerate — `infra/README.md` §Pins records them with their licences),
# so the NuGet graph of the published project is what a reviewer has to compare
# with the lock files.
#
# 06.3.6-T1.3: the Worker image publishes TWO processes — the Worker and its parse
# child (`Evidgate.Parser`, src/Evidgate.Parser) — and the two have different
# dependency graphs (the child links profile sources instead of referencing the
# Evidence module), so the child gets its own document,
# `images/evidgate-worker-parser.cdx.json`: an `Application` component named
# `Evidgate.Parser`, because no image is built from it and it ships inside
# evidgate-worker. One document per published entry point is what the consistency
# check needs — it compares a document against the lock-file closure of exactly one
# entry project.
#
# usage: scripts/sbom.sh [--out <dir>] [--spec-version <1.5|1.6|1.7>]
#                        [--image-version <version>] [-h|--help]
#
# Exit 0: every SBOM written + schema-checked (bomFormat == 'CycloneDX', a
#         specVersion, and at least one component — an empty BOM is a failure),
#         and every image SBOM consistent with the lock files.
# Exit 2: a generator failed, produced invalid/empty output, or an image SBOM
#         disagrees with packages.lock.json.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

OUT_DIR="artifacts/sbom"
SPEC="1.6"
IMAGE_VERSION="dev"

usage() {
  cat <<'EOF'
usage: scripts/sbom.sh [--out <dir>] [--spec-version <1.5|1.6|1.7>]
                       [--image-version <version>] [-h|--help]

Generates CycloneDX JSON SBOMs: <out>/npm.cdx.json (cyclonedx-npm over the npm
workspace lockfile tree), <out>/dotnet.cdx.json (dotnet-CycloneDX over
Evidgate.slnx, test projects excluded) and one
<out>/images/evidgate-<image>[-<role>].cdx.json per published entry point — the
Worker image ships two processes, so it also produces
images/evidgate-worker-parser.cdx.json (06.3.6-T1.3) — each checked against the
committed packages.lock.json by scripts/check-image-sbom-lock.py (10.2.1-T1.3,
AC3). Exit 2 when a generator fails, the output fails the CycloneDX shape check,
or an image SBOM disagrees with the lock files.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)           OUT_DIR="$2"; shift 2 ;;
    --spec-version)  SPEC="$2"; shift 2 ;;
    --image-version) IMAGE_VERSION="$2"; shift 2 ;;
    -h|--help)       usage; exit 0 ;;
    *) echo "sbom: unknown argument '$1'" >&2; usage; exit 2 ;;
  esac
done

mkdir -p "$OUT_DIR"
NPM_BOM="$OUT_DIR/npm.cdx.json"
DOTNET_BOM="$OUT_DIR/dotnet.cdx.json"

check_bom() {
  python3 - "$1" <<'PYEOF'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception as e:
    print("sbom: %s is not valid JSON (%s)" % (sys.argv[1], e), file=sys.stderr)
    sys.exit(2)
if d.get("bomFormat") != "CycloneDX" or not d.get("specVersion"):
    print("sbom: %s lacks the CycloneDX shape (bomFormat/specVersion)" % sys.argv[1], file=sys.stderr)
    sys.exit(2)
components = d.get("components")
if not isinstance(components, list) or not components:
    print("sbom: %s has an empty/missing 'components' array — refusing a vacuous BOM" % sys.argv[1], file=sys.stderr)
    sys.exit(2)
print("sbom: %s ok — %d components (spec %s)" % (sys.argv[1], len(components), d["specVersion"]))
PYEOF
}

# --- npm ---------------------------------------------------------------------
# --package-lock-only: the lockfile is the shipped dependency truth and does
# not need a fully-installed node_modules (a partially-installed tree aborts
# npm ls otherwise). The generator itself still lives in node_modules.
if [[ ! -f package-lock.json ]]; then
  echo "sbom: package-lock.json missing — run 'npm ci' first" >&2
  exit 2
fi
NPM_SBOM="node_modules/.bin/cyclonedx-npm"
[[ -x "$NPM_SBOM" ]] || NPM_SBOM="node_modules/@cyclonedx/cyclonedx-npm/bin/cyclonedx-npm-cli.js"
if [[ ! -x "$NPM_SBOM" ]]; then
  echo "sbom: @cyclonedx/cyclonedx-npm not found — run 'npm ci' first" >&2
  exit 2
fi
echo "== sbom: npm (cyclonedx-npm, spec $SPEC)"
# --output-reproducible: no serial/timestamp jitter, so artifacts diff cleanly.
if ! "$NPM_SBOM" --output-format JSON --spec-version "$SPEC" --output-reproducible \
    --package-lock-only -o "$NPM_BOM"; then
  echo "sbom: cyclonedx-npm failed" >&2
  exit 2
fi
check_bom "$NPM_BOM"

# --- dotnet ------------------------------------------------------------------
if [[ ! -f Evidgate.slnx ]]; then
  echo "sbom: Evidgate.slnx not found (cwd must be repo root)" >&2
  exit 2
fi
if ! dotnet tool restore >/dev/null; then
  echo "sbom: 'dotnet tool restore' failed" >&2
  exit 2
fi
echo "== sbom: nuget (dotnet-CycloneDX, spec $SPEC)"
# -ns: omit the serial number (deterministic output); -t: test projects are not
# shipped code; -F Json: force JSON regardless of extension handling.
# -dpr/--disable-package-restore: consume the graph `dotnet restore
# --locked-mode` produced (see header); otherwise the tool self-restores the
# whole slnx (~19 s) and the unlocked restore may rewrite packages.lock.json.
if ! dotnet dotnet-CycloneDX Evidgate.slnx -o "$OUT_DIR" -fn dotnet.cdx.json \
    --output-format Json --spec-version "$SPEC" -ns -t -dpr; then
  exit 2
fi
check_bom "$DOTNET_BOM"

# --- per-image SBOMs (10.2.1-T1.3, AC3) --------------------------------------
# One CycloneDX document per published entry point, generated from that entry
# point's project: the image adds nothing but the digest-pinned chiseled
# aspnet runtime (pinned in infra/docker/Dockerfile.*; the Api pins the `-extra`
# variant, whose ICU + tzdata are OS packages CycloneDX does not list), so the
# published project's NuGet graph is what a reviewer compares with the lock files. `-st Container`
# marks the metadata component as the image, `-rs` walks the ProjectReferences,
# `-dpr` consumes the graph `dotnet restore --locked-mode` produced (header), and
# `-ns` keeps the document deterministic.
#
# 06.3.6-T1.3: the Worker image ships a second process (its parse child), so it
# gets a second document. Columns: document stem, metadata component name,
# metadata component type, entry project. The child's document takes the image
# name plus its role in the stem (`evidgate-worker-parser`) and describes the
# component as an `Application` named `Evidgate.Parser`, because no image is
# built from the child — it is published into /app/parser of evidgate-worker.
IMAGE_DIR="$OUT_DIR/images"
mkdir -p "$IMAGE_DIR"
while read -r stem component type project; do
  [[ -n "$stem" ]] || continue
  bom="$IMAGE_DIR/$stem.cdx.json"
  echo "== sbom: $stem ($type component '$component', $project)"
  if ! dotnet dotnet-CycloneDX "$project" -o "$IMAGE_DIR" -fn "$stem.cdx.json" \
      --output-format Json --spec-version "$SPEC" -ns -rs -dpr \
      -st "$type" -sn "$component" -sv "$IMAGE_VERSION"; then
    echo "sbom: dotnet-CycloneDX failed for $project" >&2
    exit 2
  fi
  check_bom "$bom"
  if ! python3 "$ROOT/scripts/check-image-sbom-lock.py" --project "$project" --sbom "$bom"; then
    echo "sbom: the SBOM for $stem disagrees with the committed lock files (AC3)" >&2
    exit 2
  fi
done <<'IMAGE_SBOMS'
evidgate-api           evidgate-api       Container   src/Evidgate.Api/Evidgate.Api.csproj
evidgate-worker        evidgate-worker    Container   src/Evidgate.Worker/Evidgate.Worker.csproj
evidgate-migrator      evidgate-migrator  Container   src/Evidgate.Migrator/Evidgate.Migrator.csproj
evidgate-worker-parser Evidgate.Parser    Application src/Evidgate.Parser/Evidgate.Parser.csproj
IMAGE_SBOMS

echo "sbom: artifacts in $OUT_DIR"
