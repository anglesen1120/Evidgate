#!/usr/bin/env python3
"""The verify.sh stages a change set needs, chosen from the paths it touches.

A full `scripts/verify.sh` run spends most of its time in .NET restore, build,
test, the isolation suite and the Docker phases. A change set that touches no
.NET-relevant path cannot fail those stages, so this module names the subset of
stages that can fail it — and `full` for everything else.

Three properties make the saving safe to take:

- every rule is an allow-list, so anything the classifier cannot place — an
  unknown path, an unresolvable base, a failing git call, an unreadable
  `EVIDGATE_GATE_SCOPE` — is `full` (fail closed);
- a computed tier is a floor: `EVIDGATE_GATE_SCOPE` may widen it, never narrow
  it, so asking for `docs` cannot hide a `.cs` edit;
- a base is a floor too: the default chain is always read and
  `EVIDGATE_REVIEW_BASE` is added to it, so a review base narrower than the
  default cannot hide a change either.

The tier comes from the changed paths and their git state (deleted, untracked),
never from file contents, so a run is cheap and deterministic.
`scripts/verify.sh` reads the `stages:` line this prints; run it on its own to see
what a lane needs before taking the shared test lock.

usage: scripts/gate_scope.py [--help]

Prints, on stdout and in this order: `tier:`, `reason:`, `changed:`, `base:`,
`stages:`, then `skipped:` and any `note:` lines. `changed:` counts the paths the
tier was decided on: untracked harness state is not one of them. Exits 0 even
when the tier is `full` because something went wrong: the caller reads the tier,
never the exit code, and a crash must not look like a narrower gate.
"""

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from fnmatch import fnmatchcase
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from review_blockers import gitio

# The canonical order of `scripts/verify.sh` (`--list-stages`), which is the one
# place the list is authored: the fixture in `scripts/tests/test_gate_scope.py`
# fails when this mirror drifts from it.
CANONICAL_STAGES = (
    "restore",
    "build",
    "format",
    "test",
    "isolation",
    "fixtures-hostile",
    "fixtures-fuzz",
    "tsc-build",
    "tsc-spec",
    "vitest",
    "eslint",
    "prettier",
    "check-layout",
    "check-sources",
    "check-adr-matrix",
    "check-adr",
    "check-links",
    "check-contract-conventions",
    "check-env-matrix",
    "check-dor-security",
    "check-threat-model",
    "check-ci-parity",
    "check-action-pins",
    "check-ci-recipe",
    "check-codegen-drift",
    "check-permissions-drift",
    "check-rls",
    "check-review-blockers",
    "check-gate-scope",
    "image-pins",
    "images",
    "licence-scan",
    "audit-deps",
    "sbom",
)

# The stages that need the .NET SDK's output (a restore and a Release build) and
# the one that needs a Docker daemon: no path outside the safe sets can make
# them fail, and they are the expensive half of a run.
DOTNET_STAGES = frozenset(
    {"restore", "build", "format", "test", "isolation", "fixtures-hostile", "fixtures-fuzz"}
)
DOCKER_STAGES = frozenset({"images"})
SKIPPED_WHEN_NARROWED = DOTNET_STAGES | DOCKER_STAGES

# Ordered narrowest first: a requested scope may only move up this scale.
TIER_ORDER = {"docs": 0, "no-dotnet": 1, "full": 2}

DOC_PREFIXES = ("docs/", "plans/")
# `fixtures/web-api` is deliberately absent: the fixture loader walks every
# fixture directory from the .NET side, so a fixture edit can fail `test`.
SAFE_PREFIXES = ("apps/web/", "packages/", "scripts/")
SAFE_ROOT_FILES = frozenset({"package.json", "package-lock.json", ".prettierignore"})
SAFE_ROOT_PATTERNS = ("tsconfig*.json", "eslint.config.*", ".prettierrc*")
# A documentation page a .NET test reads as a rule table, so editing it can fail
# `test`: prose that a compiled lint asserts on is not prose-only.
DOCS_FULL_PATHS = frozenset({"docs/security/audit-and-deletion-rule.md"})
# ADR pages are resolved by the licence and dependency scans, which read the
# exception ids: they need the web toolchain, not the .NET SDK.
DOCS_NO_DOTNET_PREFIXES = ("docs/adr/",)
# Story files a .NET catalogue test enumerates: it reads every one, so a path the
# change set removes from this tree can fail `test` (an edit cannot).
PLAN_STORY_PREFIX = "plans/features/"
# Build inputs that change what a .NET stage compiles, wherever they sit. The
# allow-lists above never contain one today; naming them keeps a future move
# inside `packages/` from being read as a TypeScript-only change.
FULL_NAME_PATTERNS = (
    "*.cs",
    "*.csproj",
    "*.fsproj",
    "*.vbproj",
    "*.sln",
    "*.slnf",
    "*.slnx",
    "*.props",
    "*.targets",
    "*.ruleset",
    "Directory.*",
    "global.json",
    "nuget.config",
    ".globalconfig",
    ".editorconfig",
    "packages.lock.json",
    "Dockerfile*",
)
# The gate's own files. A change to how the gate selects stages is judged by the
# whole gate, never by the narrowing it is proposing.
GATE_PATHS = frozenset(
    {
        "scripts/verify.sh",
        "scripts/locked-run.sh",
        "scripts/gate_scope.py",
        "scripts/tests/test_gate_scope.py",
        "scripts/tests/test_verify_scope.py",
        "scripts/build-images.sh",
        "scripts/smoke-clean-build.sh",
        "scripts/tests/review_blockers_fixtures.py",
    }
)
GATE_PREFIXES = ("scripts/dev/", "scripts/review_blockers/")
# Local harness state: untracked by design (never committed, by rule), so it can
# never be part of a change set and must not force the full gate on the checkout
# that carries it. Committed, it is an ordinary path and needs the full gate.
LOCAL_ONLY_PREFIXES = (".omp/",)

DOCS_REASON = "every changed path is Markdown under docs/, plans/, or a root *.md"
NO_DOTNET_REASON = (
    "every changed path is documentation, an ADR, apps/web, packages, a non-gate "
    "script, or a root JS/TS tooling config"
)


@dataclass(frozen=True)
class Scope:
    """The tier one change set needs, and the facts the scope line reports."""

    tier: str
    reason: str
    changed: int
    base: str
    paths: tuple[str, ...] = ()
    notes: tuple[str, ...] = ()

    def stages(self) -> list[str]:
        return stages_for(self.tier)


@dataclass(frozen=True)
class ChangeSet:
    """The paths a change set touches, split by the facts no path string carries."""

    paths: tuple[str, ...]
    untracked: frozenset[str] = frozenset()
    deleted: frozenset[str] = frozenset()


def is_local_only(path: str, untracked) -> bool:
    """True for harness state git has only in the working tree: not a change."""
    return path.startswith(LOCAL_ONLY_PREFIXES) and path in untracked


def printable(path: str) -> str:
    """The path as one report line can carry it: control characters escaped."""
    return "".join(char if char.isprintable() else ascii(char)[1:-1] for char in path)


def stages_for(tier: str) -> list[str]:
    """The stages a tier runs, in canonical order."""
    if tier not in TIER_ORDER or tier == "full":
        return list(CANONICAL_STAGES)
    kept = [name for name in CANONICAL_STAGES if name not in SKIPPED_WHEN_NARROWED]
    if tier == "no-dotnet":
        return kept
    # The docs tier: the formatter and the text-only checks. A check stage is
    # cheap and reads files, so it runs even when the change is prose.
    return [name for name in kept if name == "prettier" or name.startswith("check-")]


def _basename(path: str) -> str:
    return path.rsplit("/", 1)[-1]


def is_doc(path: str) -> bool:
    """Markdown in the documentation trees, or a root-level Markdown file."""
    if not path.endswith(".md"):
        return False
    return "/" not in path or path.startswith(DOC_PREFIXES)


def tier_of_path(path: str, *, untracked=(), deleted=()) -> tuple[str, str]:
    """The narrowest tier one repository-relative path allows, with its class.

    `untracked` and `deleted` carry the two facts a path string alone cannot:
    whether git has the path only in the working tree, and whether the change
    set removes it.
    """
    if path.startswith(LOCAL_ONLY_PREFIXES):
        if is_local_only(path, untracked):
            return "skip", "untracked harness state"
        return "full", "harness state committed to the repository"
    if path in GATE_PATHS or path.startswith(GATE_PREFIXES):
        return "full", "gate file"
    if any(fnmatchcase(_basename(path), pattern) for pattern in FULL_NAME_PATTERNS):
        return "full", "build or tool configuration"
    if path in DOCS_FULL_PATHS:
        return "full", "documentation a .NET test reads"
    if path.startswith(PLAN_STORY_PREFIX) and path in deleted:
        return "full", "story file a .NET catalogue test requires"
    if path.startswith(DOCS_NO_DOTNET_PREFIXES):
        return "no-dotnet", "ADR file the licence and dependency scans resolve"
    if is_doc(path):
        return "docs", "documentation"
    if path.startswith(SAFE_PREFIXES):
        return "no-dotnet", "web, package or script"
    if "/" not in path and (
        path in SAFE_ROOT_FILES or any(fnmatchcase(path, pattern) for pattern in SAFE_ROOT_PATTERNS)
    ):
        return "no-dotnet", "tooling configuration"
    return "full", "outside the narrowed allow-lists"


def tier_of_paths(paths, *, untracked=(), deleted=()) -> tuple[str, str]:
    """The tier of a whole change set: the widest tier any one path needs."""
    untracked, deleted = frozenset(untracked), frozenset(deleted)
    judged: list[tuple[str, str, str]] = []
    for path in paths:
        tier, why = tier_of_path(path, untracked=untracked, deleted=deleted)
        if tier != "skip":
            judged.append((path, tier, why))
    if not judged:
        return "full", "no changed path to judge"
    blocked = [(path, why) for path, tier, why in judged if tier == "full"]
    if blocked:
        path, why = blocked[0]
        return "full", f"{printable(path)} ({why}) needs the full gate"
    if any(tier == "no-dotnet" for _path, tier, _why in judged):
        return "no-dotnet", NO_DOTNET_REASON
    return "docs", DOCS_REASON


def apply_request(computed: str, requested: str | None) -> tuple[str, str | None]:
    """The tier after `EVIDGATE_GATE_SCOPE`: it may widen the computed one, never narrow it."""
    requested = (requested or "auto").strip().lower()
    if requested == "auto":
        return computed, None
    if requested not in TIER_ORDER:
        return "full", f"EVIDGATE_GATE_SCOPE={requested!r} is not auto|docs|no-dotnet|full, so the gate runs full"
    if TIER_ORDER[requested] < TIER_ORDER[computed]:
        return computed, f"EVIDGATE_GATE_SCOPE={requested} was refused: the change set needs {computed}"
    if TIER_ORDER[requested] > TIER_ORDER[computed]:
        return requested, f"EVIDGATE_GATE_SCOPE={requested} widened the {computed} tier"
    return computed, None


def _decode(raw: bytes) -> str:
    return raw.decode("utf-8", "surrogateescape")


def _status_paths(raw: bytes) -> tuple[list[str], list[str], list[str]]:
    """(paths, untracked, deleted) from `git status --porcelain=v1 -z`.

    A rename or copy record carries the original path as the next field: the
    change set moved content out of it, so it is part of the change and, for a
    rename, a removal.
    """
    fields = raw.split(b"\0")
    paths: list[str] = []
    untracked: list[str] = []
    deleted: list[str] = []
    index = 0
    while index < len(fields):
        entry = fields[index]
        index += 1
        if len(entry) < 4:
            continue
        code, path = entry[:2], _decode(entry[3:])
        paths.append(path)
        if code == b"??":
            untracked.append(path)
        elif b"D" in code:
            deleted.append(path)
        if (b"R" in code or b"C" in code) and index < len(fields) and fields[index]:
            original = _decode(fields[index])
            index += 1
            paths.append(original)
            if b"R" in code:
                deleted.append(original)
    return paths, untracked, deleted


def changed_paths(bases) -> ChangeSet:
    """Every path the change set touches: each base against HEAD, then the working tree."""
    paths: list[str] = []
    deleted: list[str] = []
    for base in bases:
        paths += gitio.changed_names(base, "HEAD")
        deleted += gitio.changed_names(base, "HEAD", "--diff-filter=D")
    working = gitio.run_git("status", "--porcelain=v1", "-z", "--untracked-files=all").stdout
    status_paths, untracked, removed = _status_paths(working)
    return ChangeSet(
        paths=tuple(dict.fromkeys(path for path in paths + status_paths if path)),
        untracked=frozenset(untracked),
        deleted=frozenset(deleted + removed),
    )


def resolve_bases(env) -> list[str]:
    """The commits the change set is read against: the default chain, then any env base.

    The default chain is always read and an extra base is added to it, so a
    review base narrower than the default cannot hide a change.
    """
    refs = [gitio.effective_base("")]
    requested = (env.get("EVIDGATE_REVIEW_BASE") or "").strip()
    if requested and not gitio.ZERO_REF.match(requested):
        if not gitio.resolves(requested):
            raise gitio.ScanError(
                f"EVIDGATE_REVIEW_BASE {requested!r} does not resolve to a commit: fetch it "
                "(`git fetch origin`), or check the clone has full history (fetch-depth: 0)"
            )
        refs.append(requested)
    commits: list[str] = []
    for ref in refs:
        commit = gitio.resolve(ref, "base")
        if commit not in commits:
            commits.append(commit)
    return commits


def evaluate(env=None) -> Scope:
    """The scope of the current repository's change set; never raises."""
    env = dict(os.environ if env is None else env)
    try:
        return _evaluate(env)
    except gitio.ScanError as error:
        # A base that does not resolve, or a git call that fails, is a change set
        # nobody can read: full, and say which ref or command to look at.
        return Scope("full", str(error), 0, "unknown")
    except Exception as error:  # fail closed: an unreadable change set is a full gate
        return Scope("full", f"the classifier could not run ({error})", 0, "unknown")


def _evaluate(env) -> Scope:
    commits = resolve_bases(env)
    change = changed_paths(commits)
    judged = tuple(path for path in change.paths if not is_local_only(path, change.untracked))
    tier, reason = tier_of_paths(change.paths, untracked=change.untracked, deleted=change.deleted)
    tier, note = apply_request(tier, env.get("EVIDGATE_GATE_SCOPE"))
    notes = [note] if note else []
    if len(commits) > 1:
        notes.append(f"EVIDGATE_REVIEW_BASE was read against {gitio.short(commits[1])} as well")
    return Scope(
        tier=tier,
        reason=reason,
        changed=len(judged),
        base=gitio.short(commits[0]),
        paths=judged,
        notes=tuple(notes),
    )


def render(scope: Scope) -> str:
    """The report `scripts/verify.sh` parses: one `key: value` line per fact."""
    stages = scope.stages()
    lines = [
        f"tier: {scope.tier}",
        f"reason: {scope.reason}",
        f"changed: {scope.changed}",
        f"base: {scope.base}",
        f"stages: {' '.join(stages)}",
    ]
    skipped = [name for name in CANONICAL_STAGES if name not in stages]
    if skipped:
        lines.append(f"skipped: {' '.join(skipped)}")
    lines.extend(f"note: {note}" for note in scope.notes)
    return "\n".join(lines)


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if any(arg in ("-h", "--help") for arg in argv):
        print(__doc__.strip())
        return 0
    try:
        gitio.enter_repository()
    except gitio.ScanError as error:
        print(render(Scope("full", f"not inside a git work tree ({error})", 0, "unknown")))
        return 0
    print(render(evaluate()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
