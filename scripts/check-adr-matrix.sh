#!/usr/bin/env bash
# AC6 (01.1.1): every NuGet package name (PackageVersion/GlobalPackageReference in
# Directory.Packages.props, PackageReference in every *.csproj/*.props/*.targets),
# every npm dependency name (dependencies/devDependencies/optionalDependencies/
# peerDependencies of the root and every workspace package.json), and every .NET
# local tool package id (.config/dotnet-tools.json tools.*) must appear as the
# first cell of a matrix-table row in docs/adr/0002-compatibility-and-licence-matrix.md.
# Names listed in the "Alternatives rejected" table are NOT registrations; a
# dependency matching one fails with "rejected in ADR-0002".
# Exit 0: all registered. Exit 1: lists unregistered/rejected package names.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ADR="$ROOT/docs/adr/0002-compatibility-and-licence-matrix.md"
PROPS="$ROOT/Directory.Packages.props"

if [ ! -f "$ADR" ]; then
  echo "check-adr-matrix: missing $ADR" >&2
  exit 1
fi

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
names="$tmpdir/names"
registered="$tmpdir/registered"
rejected="$tmpdir/rejected"
projfiles="$tmpdir/projfiles"

# --- Registered and rejected names: first cell of every markdown table row in
# the ADR whose header's first cell is "Package". Tables under a heading that
# contains "reject" (i.e. "Alternatives rejected") are rejections, not
# registrations. Prose mentions never count.
python3 - "$ADR" "$registered" "$rejected" <<'PYEOF'
import re
import sys

adr_path, reg_path, rej_path = sys.argv[1], sys.argv[2], sys.argv[3]
lines = open(adr_path, encoding="utf-8").read().splitlines()

heading_re = re.compile(r"^#{1,6}\s+(.*)$")
sep_re = re.compile(r"^:?-{2,}:?$")

def cells(line):
    # split a markdown table row into trimmed cell texts
    parts = line.strip().strip("|").split("|")
    return [p.strip().strip("`").strip() for p in parts]

registered, rejected_names = set(), set()
heading = ""
i = 0
n = len(lines)
while i < n:
    line = lines[i]
    m = heading_re.match(line)
    if m:
        heading = m.group(1).strip().lower()
        i += 1
        continue
    if line.lstrip().startswith("|") and i + 1 < n and lines[i + 1].lstrip().startswith("|"):
        header = cells(line)
        sep = cells(lines[i + 1])
        # a table row followed by a separator row => header of a real table
        if sep and all(sep_re.match(c) for c in sep) and header and header[0].lower() == "package":
            is_rejected = "reject" in heading
            i += 2
            while i < n and lines[i].lstrip().startswith("|"):
                row = cells(lines[i])
                if row and row[0] and not sep_re.match(row[0]):
                    (rejected_names if is_rejected else registered).add(row[0].lower())
                i += 1
            continue
    i += 1

with open(reg_path, "w", encoding="utf-8") as f:
    f.write("\n".join(sorted(registered)))
with open(rej_path, "w", encoding="utf-8") as f:
    f.write("\n".join(sorted(rejected_names)))
PYEOF

# --- NuGet package names via XML parsing (ElementTree tolerates multi-line
# elements and any attribute order). Local-name match ignores XML namespaces.
{
  git -C "$ROOT" ls-files -- '*.csproj' '*.props' '*.targets' 2>/dev/null || true
  find "$ROOT" \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' | sed "s|^$ROOT/||"
} | sort -u > "$projfiles"

python3 - "$ROOT" "$PROPS" "$projfiles" <<'PYEOF' >> "$names"
import sys
import xml.etree.ElementTree as ET

root_dir, props_path, list_path = sys.argv[1], sys.argv[2], sys.argv[3]

def local(tag):
    return tag.rsplit("}", 1)[-1]

def collect(path, elements):
    names = set()
    try:
        tree = ET.parse(path)
    except ET.ParseError as e:
        sys.exit(f"check-adr-matrix: cannot parse {path}: {e}")
    for el in tree.iter():
        if local(el.tag) in elements:
            for attr in ("Include", "Update"):
                v = el.get(attr)
                if v and v.strip():
                    names.add(v.strip())
    return names

out = set()
out |= collect(props_path, {"PackageVersion", "GlobalPackageReference"})
with open(list_path, encoding="utf-8") as f:
    for rel in f:
        rel = rel.strip()
        if not rel:
            continue
        out |= collect(f"{root_dir}/{rel}", {"PackageReference"})
for n in sorted(out):
    print(n)
PYEOF

# --- npm package names: root package.json dep sections plus every workspace
# manifest resolved from the root "workspaces" globs.
node - "$ROOT" <<'EOF' >> "$names"
const fs = require("fs");
const path = require("path");
const root = process.argv[2];

const SECTIONS = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

function manifestPaths() {
  const paths = [path.join(root, "package.json")];
  const pkg = JSON.parse(fs.readFileSync(paths[0], "utf8"));
  const ws = pkg.workspaces;
  const globs = Array.isArray(ws) ? ws : (ws && ws.packages) || [];
  for (const g of globs) {
    if (g.endsWith("/**")) {
      const base = path.join(root, g.slice(0, -3));
      const walk = (dir) => {
        if (!fs.existsSync(dir)) return;
        for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
          if (!e.isDirectory() || e.name === "node_modules") continue;
          const d = path.join(dir, e.name);
          if (fs.existsSync(path.join(d, "package.json"))) paths.push(path.join(d, "package.json"));
          walk(d);
        }
      };
      walk(base);
    } else {
      // "dir" or "dir/*" patterns
      const base = g.endsWith("/*") ? path.join(root, g.slice(0, -2)) : path.join(root, g);
      if (!fs.existsSync(base)) continue;
      const direct = path.join(base, "package.json");
      if (fs.existsSync(direct)) paths.push(direct);
      for (const e of fs.readdirSync(base, { withFileTypes: true })) {
        if (!e.isDirectory() || e.name === "node_modules") continue;
        const f = path.join(base, e.name, "package.json");
        if (fs.existsSync(f)) paths.push(f);
      }
    }
  }
  return [...new Set(paths)];
}

const names = new Set();
for (const f of manifestPaths()) {
  const pkg = JSON.parse(fs.readFileSync(f, "utf8"));
  for (const key of SECTIONS) {
    for (const name of Object.keys(pkg[key] ?? {})) names.add(name);
  }
}
for (const n of [...names].sort()) console.log(n);
EOF

# --- .NET local tools (.config/dotnet-tools.json): every entry ships a NuGet
# package into the build pipeline (nuget-license, CycloneDX), so a new or
# bumped tool without an ADR-0002 row must fail this gate like any other
# package. `id` is the NuGet package id when present; the tool name otherwise.
python3 - "$ROOT/.config/dotnet-tools.json" <<'PYEOF' >> "$names"
import json, sys

try:
    data = json.load(open(sys.argv[1], encoding="utf-8"))
except FileNotFoundError:
    sys.exit(0)  # no local tools -> nothing to register
except json.JSONDecodeError as e:
    sys.exit("check-adr-matrix: cannot parse %s: %s" % (sys.argv[1], e))
for tool_name, tool in (data.get("tools") or {}).items():
    print((tool or {}).get("id") or tool_name)
PYEOF

sort -u "$names" -o "$names"

missing=0
while IFS= read -r name; do
  [ -z "$name" ] && continue
  lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  if grep -Fxq -- "$lower" "$rejected"; then
    echo "check-adr-matrix: '$name' is rejected in ADR-0002" >&2
    missing=1
  elif ! grep -Fxq -- "$lower" "$registered"; then
    echo "check-adr-matrix: '$name' has no row in $ADR" >&2
    missing=1
  fi
done < "$names"

if [ "$missing" -eq 0 ]; then
  echo "check-adr-matrix: all packages registered in ADR-0002"
fi
exit "$missing"
