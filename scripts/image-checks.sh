# Per-image checks for scripts/build-images.sh.
#
# Sourced by scripts/build-images.sh after it defines its identity helpers
# (prefix, assembly_of, child_assembly_of, child_directory_of, is_r2r), die and
# image_size, and after it sets CHECK_PLATFORM for the platform under test. Each
# check reads the built image and fails the run with a non-zero exit; none of
# them builds anything. The chiseled runtime has no shell, so image contents are
# read with `docker cp`/`docker export` from the host.
#
# A check that copies files out of an image runs its body in a subshell with an
# EXIT trap for the scratch copy, so a failed copy or assertion removes it too;
# a trap set in the driver's shell would replace its own EXIT cleanup trap.
#
# Ownership of the /app tree is asserted here too: the runtime user must not be
# able to replace the assemblies it is about to execute.
# Where the runtime base actually came from: the pinned layers must be the
# built image's first layers, because a stage can only add on top of its FROM.
#
# The comparison target is read from the *registry* (the pinned index digest ->
# the platform's manifest -> its config -> rootfs.diff_ids) rather than from the
# local store: with a multi-platform pin the local name resolves to one platform
# image (measured 2026-09-27: `docker image inspect` on the pinned index digest
# answered the arm64 variant while the image under test was amd64), so a name-based
# comparison silently compares the wrong platform. Offline (`--offline`, or a
# registry that cannot be reached) the layer proof degrades to the inherited
# config markers and says so.
check_runtime_base() {
  local reference="$1" platform="$2"
  python3 "$ROOT/scripts/image-base-layers.py" "$reference" "$platform" "$3" \
    "$(docker image inspect "$reference" --format '{{json .RootFS.Layers}}')" \
    "$(docker image inspect "$reference" --format '{{json .Config.Env}}')" \
    "$OFFLINE"
}

check_user() {
  local reference="$1" user
  user="$(docker image inspect "$reference" --format '{{.Config.User}}')"
  [[ -n "$user" ]] || die "$reference has an empty Config.User: AC1 requires a non-root User"
  case "$user" in
    0 | 0:* | root | root:*) die "$reference runs as root (Config.User='$user'): AC1 requires non-root" ;;
  esac
  echo "   user: Config.User='$user' (non-root)"
}

# The container filesystem is read with `docker cp`: the chiseled runtime has no shell.
copy_from_image() {
  local reference="$1" path="$2" destination="$3" container
  container="$(docker create --platform "$CHECK_PLATFORM" "$reference")"
  docker cp "$container:$path" "$destination" >/dev/null
  docker rm -f "$container" >/dev/null
}

# 00 §5.7 P7 + 13.1.1-T2 (D-S55-Q1): every long-lived image carries Server GC, and the
# globalization-invariant switch is per image — Worker keeps it (nothing it hosts
# composes text), the Api must NOT have it, because the authoring module stores NFC
# text and `string.Normalize(FormC)` is a silent no-op under invariant mode (13.1.1
# AC14). `System.Globalization.Invariant` is a runtimeconfig property, but the *base
# image* can force it with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true — the plain
# chiseled base does exactly that — and the environment wins over the file, so the
# Api's image environment is checked too.
invariant_of() { case "$(prefix "$1")" in api) echo 0 ;; *) echo 1 ;; esac; }

check_p7() (
  local reference="$1" name="$2" assembly scratch
  assembly="$(assembly_of "$name")"
  scratch="$(mktemp -d)"
  trap 'rm -rf "$scratch"' EXIT
  copy_from_image "$reference" "/app/$assembly" "$scratch/$assembly"
  copy_from_image "$reference" "/app/${assembly%.dll}.runtimeconfig.json" "$scratch/runtimeconfig.json"
  python3 - "$scratch/runtimeconfig.json" "$(invariant_of "$name")" \
    "$(docker image inspect "$reference" --format '{{json .Config.Env}}')" <<'PYEOF'
import json, sys

document = json.load(open(sys.argv[1], encoding="utf-8"))
invariant_expected = sys.argv[2] == "1"
environment = json.loads(sys.argv[3]) or []
properties = document.get("runtimeOptions", {}).get("configProperties", {})
if properties.get("System.GC.Server") is not True:
    sys.exit("build-images: runtimeconfig.json misses the 00 §5.7 P7 process setting "
             "System.GC.Server")
invariant_actual = properties.get("System.Globalization.Invariant") is True
if invariant_actual is not invariant_expected:
    sys.exit("build-images: runtimeconfig.json carries System.Globalization.Invariant=%s, "
             "expected %s for this image (00 §5.7 P7 — invariant is Worker-only; the Api "
             "needs ICU for NFC, 13.1.1-T2 D-S55-Q1)" % (invariant_actual, invariant_expected))
if not invariant_expected:
    forced = [entry for entry in environment
              if entry in ("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
                           "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true")]
    if forced:
        sys.exit("build-images: the runtime base sets %s, which overrides the runtimeconfig "
                 "and would make NFC normalisation a no-op: the Api must run on the `-extra` "
                 "chiseled base (13.1.1-T2 D-S55-Q1)" % forced[0])
frameworks = [entry.get("name") for entry in document.get("runtimeOptions", {}).get("frameworks", [])]
print("   P7: runtimeconfig.json has Server GC and System.Globalization.Invariant=%s "
      "(frameworks=%s)" % (invariant_actual, ", ".join(str(name) for name in frameworks)))
PYEOF
  if [[ "$(is_r2r "$name")" == "1" ]]; then
    python3 - "$scratch/$assembly" <<'PYEOF'
import sys

data = open(sys.argv[1], "rb").read(1 << 20)
if b"RTR\x00" not in data:
    sys.exit("build-images: %s carries no ReadyToRun header (00 §5.7 P7)" % sys.argv[1])
print("   P7: ReadyToRun header present in %s" % sys.argv[1].rsplit("/", 1)[-1])
PYEOF
  fi
)

# 06.3.6-T1.3: the Worker image's second entry point. The parent runner launches
# the child by path (`dotnet /app/parser/Evidgate.Parser.dll`), so three things
# must hold in the image and none of them is visible from the Worker's own dll:
# the child's assembly is there, it ships its own runtimeconfig.json and
# deps.json (a publish that landed in /app instead of /app/parser would find the
# Worker's files of the same name and silently bind the wrong graph), and its
# runtime options match the group `Directory.Build.props` puts it in — the Worker's
# InvariantGlobalization group, because this image carries no ICU (06.3.6-T1.2).
# Server GC and a ReadyToRun header are deliberately NOT asserted: 00 §5.7 P7
# names the long-lived Api/Worker processes for both, and the child is a
# short-lived process (the same reasoning Dockerfile.migrator states for its
# one-shot tool) — the child's runtimeconfig.json carries neither.
check_parse_child() (
  local reference="$1" name assembly directory stem scratch
  name="$(prefix "$2")"
  assembly="$(child_assembly_of "$name")"
  directory="$(child_directory_of "$name")"
  [[ -n "$assembly" ]] || die "$name has no parse child: check_parse_child is called for the wrong image"
  stem="${assembly%.dll}"
  scratch="$(mktemp -d)"
  trap 'rm -rf "$scratch"' EXIT
  copy_from_image "$reference" "/app/$directory/$assembly" "$scratch/$assembly"
  copy_from_image "$reference" "/app/$directory/$stem.runtimeconfig.json" "$scratch/runtimeconfig.json"
  copy_from_image "$reference" "/app/$directory/$stem.deps.json" "$scratch/deps.json"
  python3 - "$scratch/runtimeconfig.json" "$scratch/deps.json" "$scratch/$assembly" "$assembly" "$(invariant_of "$name")" <<'PYEOF'
import json, sys

runtimeconfig, dependencies, binary = sys.argv[1], sys.argv[2], sys.argv[3]
assembly, invariant = sys.argv[4], sys.argv[5] == "1"
document = json.load(open(runtimeconfig, encoding="utf-8"))
options = document.get("runtimeOptions", {})
properties = options.get("configProperties", {})
actual = properties.get("System.Globalization.Invariant") is True
if actual is not invariant:
    sys.exit("build-images: /app/parser/%s's runtimeconfig.json carries "
             "System.Globalization.Invariant=%s, expected %s (06.3.6-T1.2 puts the child in the "
             "Worker's invariant group; this chiseled base ships no ICU)" % (assembly, actual, invariant))
if not options.get("framework"):
    sys.exit("build-images: /app/parser/%s's runtimeconfig.json declares no framework: the child "
             "was published self-contained, but the runner launches it with the image's own "
             "`dotnet` and its base is the framework-dependent aspnet runtime" % assembly)
library = json.load(open(dependencies, encoding="utf-8")).get("libraries", {})
if not any(entry.split("/")[0] == assembly[:-4] for entry in library):
    sys.exit("build-images: /app/parser/%s.deps.json does not describe %s itself: the publish "
             "output is not the child's own dependency graph" % (assembly[:-4], assembly))
# The other half of the R2R policy, asserted rather than left to a Dockerfile
# comment: the child is published without crossgen (00 §5.7 P7 names the
# long-lived Api/Worker processes; Dockerfile.migrator states the same rule for
# its one-shot tool), so a ReadyToRun header here means a publish flag drifted.
if b"RTR\x00" in open(binary, "rb").read(1 << 20):
    sys.exit("build-images: /app/parser/%s carries a ReadyToRun header, but the parse child is "
             "published without crossgen (00 §5.7 P7 names the long-lived Api/Worker processes; "
             "06.3.6-T1.3)" % assembly)
print("   parse child: /app/parser/%s + runtimeconfig.json + deps.json "
      "(System.Globalization.Invariant=%s, framework=%s, no ReadyToRun header)"
      % (assembly, actual, options["framework"].get("name")))
PYEOF
  echo "   published: $(child_project_of "$name") -> /app/$directory (no crossgen, 00 §5.7 P7)"
)

# The end-to-end proof an "image missing the child" defect is caught by: run the
# child the way the parent runner does, as the image's own non-root user, with no
# arguments. It answers the usage contract before it reads stdin — exit 2, its
# fixed usage line on stderr and nothing on stdout — so this needs no artifact,
# no shell and no elevated permissions, and an unreadable dll, a missing
# runtimeconfig.json or a dll that is not the child all fail here.
check_parse_child_runs() {
  local reference="$1" name assembly path output status stderr_file
  name="$(prefix "$2")"
  assembly="$(child_assembly_of "$name")"
  [[ -n "$assembly" ]] || die "$name has no parse child: check_parse_child_runs is called for the wrong image"
  path="/app/$(child_directory_of "$name")/$assembly"
  stderr_file="$(mktemp)"
  set +e
  output="$(docker run --rm --platform "$CHECK_PLATFORM" --entrypoint /usr/bin/dotnet \
    "$reference" "$path" 2>"$stderr_file")"
  status=$?
  set -e
  if [[ "$status" -ne 2 ]]; then
    sed 's/^/   | /' "$stderr_file"
    rm -f "$stderr_file"
    die "'docker run --entrypoint /usr/bin/dotnet $reference $path' exited $status, expected the " \
      "child's usage error 2: the image does not carry a runnable parse child (06.3.6-T1.3)"
  fi
  [[ -z "$output" ]] || {
    rm -f "$stderr_file"
    die "'$path' wrote to stdout for a usage error: the child's contract keeps stdout for the " \
      "NDJSON stream and its final marker (06.3.6 §7)"
  }
  grep -q '^evidgate-parser: ' "$stderr_file" || {
    sed 's/^/   | /' "$stderr_file"
    rm -f "$stderr_file"
    die "'$path' printed no fixed 'evidgate-parser: ' line on stderr for a usage error"
  }
  rm -f "$stderr_file"
  echo "   parse child entry point: 'dotnet $path' exits 2 with the usage line, as Config.User runs it"
}

# No application file the runtime user can replace: a compromised request handler (Api), parse child (Worker) or
# one-shot migration job (Migrator) runs as $APP_UID, and a /app it can write lets it unlink and replace an assembly
# for the next start or a sibling process. So every entry under /app must be root-owned with no group/other write
# bit — file and directory alike, since a root-owned file in a directory the app uid owns is just as replaceable,
# which is why the Dockerfiles create the WORKDIR while USER is root — and each directory the runtime launches from
# is exactly 0755 so it can still be read. The chiseled base has no shell or coreutils, so `docker export` is read.
check_app_tree_ownership() (
  local reference="$1" image="$2" name archive container
  local -a required
  name="$(prefix "$image")"
  required=("d app 0755" "f app/$(assembly_of "$name")")
  case "$name" in
    worker) required+=("d app/parser 0755" "f app/parser/$(child_assembly_of "$name")") ;;
    migrator) required+=("d app/migrations 0755" "s app/migrations") ;;
  esac
  archive="$(mktemp)"
  trap 'rm -f "$archive"' EXIT
  container="$(docker create --platform "$CHECK_PLATFORM" "$reference")"
  docker export --output "$archive" "$container" >/dev/null
  docker rm -f "$container" >/dev/null
  python3 - "$archive" "${required[@]}" <<'PYEOF'
import sys, tarfile

archive = sys.argv[1]
required = [entry.split() for entry in sys.argv[2:]]
dir_modes, files, script_dirs = {}, [], []
for kind, path, *rest in required:
    if kind == "d":
        dir_modes[path] = int(rest[0], 8)
    elif kind == "s":
        script_dirs.append(path)
    else:
        files.append(path)
members, directories, problems = {}, {}, []
with tarfile.open(archive) as tar:
    for member in tar:
        name = member.name
        while name.startswith("./"):
            name = name[2:]
        name = name.rstrip("/")
        if name != "app" and not name.startswith("app/"):
            continue
        members[name] = member
        if member.isdir():
            directories[name] = member
        if member.uid != 0 or member.gid != 0:
            problems.append("%s is owned by %d:%d, not root" % (name, member.uid, member.gid))
        if member.mode & 0o022:
            problems.append("%s has mode %04o: a non-root writer can change it" % (name, member.mode))
for directory, mode in dir_modes.items():
    member = directories.get(directory)
    if member is None:
        problems.append("%s is missing from the image" % directory)
    elif member.mode != mode:
        problems.append("%s has mode %04o, expected %04o" % (directory, member.mode, mode))
problems.extend("%s is missing from the image" % path for path in files if path not in members)
problems.extend("%s carries no *.sql script" % directory for directory in script_dirs
                if not any(name.startswith(directory + "/") and name.endswith(".sql") for name in members))
if problems:
    sys.exit("build-images: the image's /app tree is not root-owned and read-only to the app uid: %s"
             % "; ".join(problems))
summary = ["%s at %04o" % (d, m) for d, m in dir_modes.items()] + files + ["%s with *.sql" % d for d in script_dirs]
print("   app tree: %d entries under /app, root-owned, no group/other write; required: %s"
      % (len(members), ", ".join(summary)))
PYEOF
)

# 01.1.2-T1.3: BuildingBlocks.Persistence globs migrations/*.sql at build time and
# bakes the greatest file name into assembly metadata as the expected journal head.
# A build context without migrations/ does not fail — it bakes the "(none)"
# sentinel and every ready probe would then answer unready — so the baked value is
# checked against the repository's head.
check_migration_head() (
  local reference="$1" head scratch
  head="$(ls migrations/*.sql | sort | tail -1)"
  head="${head##*/}"
  scratch="$(mktemp -d)"
  trap 'rm -rf "$scratch"' EXIT
  copy_from_image "$reference" "/app/Evidgate.BuildingBlocks.Persistence.dll" "$scratch/persistence.dll"
  python3 - "$scratch/persistence.dll" "$head" <<'PYEOF'
import sys

dll, head = sys.argv[1], sys.argv[2]
data = open(dll, "rb").read()
if head.encode() not in data:
    sys.exit("build-images: %s does not carry the expected migration head '%s' — the build "
             "context is missing migrations/*.sql, so the head baked into the image is "
             "degraded (01.1.2-T1.3)" % (dll, head))
print("   expected migration head baked in: %s" % head)
PYEOF
)

check_migrator_cli() {
  local reference="$1" output status
  set +e
  output="$(docker run --rm --platform "$CHECK_PLATFORM" "$reference" --help 2>&1)"
  status=$?
  set -e
  [[ "$status" -eq 0 ]] || die "'docker run $reference --help' exited $status, expected 0 (AC5)"
  printf '%s\n' "$output" | grep -q 'usage: Evidgate.Migrator' \
    || die "'docker run $reference --help' did not print the CLI contract"
  printf '%s\n' "$output" | grep -q -- '--status' \
    || die "the Migrator usage text no longer documents --status"
  echo "   entry point: 'docker run $reference --help' exits 0 and prints the usage text"
}
