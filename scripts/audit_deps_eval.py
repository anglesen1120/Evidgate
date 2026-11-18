#!/usr/bin/env python3
"""Evaluate dependency-vulnerability rows against scripts/exceptions.json.

Rows are '<eco>\\t<pkg>\\t<ver>\\t<severity>\\t<advisory>[\\t<audit node paths>]'.
A finding fails unless its advisory has a live exception naming its ADR; an npm
entry may narrow the exemption with a `scope` of (workspace, direct dependency)
pairs, checked over the package-lock.json graph so a shared intermediate cannot
hide a second consumer the way npm's deduplicated display tree does. Exit 0
clean, 1 unexcepted findings, 2 when the inputs cannot prove the chain.
"""
import fnmatch
import glob
import json
import os
import re
import sys
from datetime import date

REGISTRY = "https://registry.npmjs.org/"
LOCK_VERSION = 3
# Not a package name (npm names cannot hold spaces), so a workspace called
# "root" cannot collide with the root package's own dependencies.
ROOT_ORIGIN = "the root package"
# npm expands workspace patterns with glob. Matching below is per path segment,
# so any richer syntax is refused instead of silently matching nothing.
UNSUPPORTED_GLOB = (("{", "braces"), ("}", "braces"), ("(", "extglob"),
                    (")", "extglob"), ("**", "a recursive wildcard"), ("\\", "escape"))


class ScopeError(Exception):
    """The exception itself is unusable: report the finding, never exempt it."""


class ScannerFailure(Exception):
    """The inputs cannot prove the chain: exit 2, never an exemption."""


def lock_records(scope_dir):
    """The lockfile v3 `packages` map; any other shape is a scanner failure.

    Only v3 records carry the install layout this walk needs, so a different
    version is refused rather than read with v2 semantics.
    """
    try:
        with open(os.path.join(scope_dir, "package-lock.json")) as fh:
            lock = json.load(fh)
    except Exception as e:
        raise ScannerFailure("package-lock.json is unusable (%s)" % e)
    if not isinstance(lock, dict):
        raise ScannerFailure("package-lock.json is unusable (not an object)")
    if lock.get("lockfileVersion") != LOCK_VERSION:
        raise ScannerFailure("package-lock.json is lockfileVersion %r, not %d"
                             % (lock.get("lockfileVersion"), LOCK_VERSION))
    recs = lock.get("packages")
    if not isinstance(recs, dict):
        raise ScannerFailure("package-lock.json is unusable ('packages' is not an object)")
    for key, rec in recs.items():
        if not isinstance(rec, dict):
            raise ScannerFailure("package-lock.json record %r is not an object" % key)
    return recs


def real_name(key, rec):
    """The installed package's real name: an alias record names its target."""
    return rec.get("name") or key.rsplit("node_modules/", 1)[-1]


def lookup_dirs(key):
    """`node_modules` search directories for a dependent installed at `key`.

    Node skips any ancestor whose last segment is `node_modules` and tries every
    other one, so `packages/server` sees `packages/node_modules` before the root
    and a nested dependency never looks in `node_modules/node_modules`.
    """
    out, cur = [], key
    while True:
        if cur.rsplit("/", 1)[-1] != "node_modules":
            out.append(cur)
        if not cur:
            return out
        cur = cur.rsplit("/", 1)[0] if "/" in cur else ""


def resolve(recs, key, dep):
    """(raw record key, followed record key) for `dep`, or (None, None).

    The raw key is returned because the identity of a direct dependency must be
    judged on the record npm installed there, before a `link: true` record is
    followed to the directory it points at.
    """
    for base in lookup_dirs(key):
        cand = (base + "/" if base else "") + "node_modules/" + dep
        if cand in recs:
            rec = recs[cand]
            target = rec.get("resolved") if rec.get("link") else cand
            return cand, target if target in recs else None
    return None, None


def edges(recs, key):
    """(dependency, raw key, followed key) for every production edge of `key`.

    Only `dependencies` is required: an absent optional or peer edge is what npm
    installs, and `devDependencies` are never traversed because `--omit=dev`
    does not install them.
    """
    rec = recs.get(key) or {}
    out = []
    for field in ("dependencies", "optionalDependencies", "peerDependencies"):
        for dep in rec.get(field) or {}:
            raw, child = resolve(recs, key, dep)
            if child is None:
                if field == "dependencies":
                    raise ScannerFailure(
                        "package-lock.json cannot resolve %s from %s" % (dep, key))
                continue
            out.append((dep, raw, child))
    return out


def walk(recs, start, targets):
    """(name path to one target, every target record reached) from `start`.

    The subtree is walked to the end even after a target turns up, so the lock
    validation never stops at the vulnerable record: a target's own unresolved
    required dependency is a scanner failure, not a silent exemption.
    """
    todo, seen = [(start, [real_name(start, recs[start])])], {start}
    reached, path = set(), None
    while todo:
        key, names = todo.pop()
        if key in targets:
            reached.add(key)
            path = path or names
        for _dep, _raw, child in edges(recs, key):
            if child in seen:
                continue
            seen.add(child)
            todo.append((child, names + [real_name(child, recs[child])]))
    return path, reached


def workspace_patterns(recs):
    """The root record's `workspaces` patterns, normalised as npm normalises.

    npm strips a leading `./` and a trailing `/` and then expands with glob;
    this gate matches per path segment and refuses the rest of that dialect, so
    a pattern it could not honour fails the run instead of matching nothing.
    """
    root = recs.get("") or {}
    pats = root.get("workspaces") or []
    if isinstance(pats, dict):
        pats = pats.get("packages") or []
    if not isinstance(pats, list):
        raise ScannerFailure("package-lock.json records 'workspaces' as %r" % (pats,))
    out = []
    for pat in pats:
        if not isinstance(pat, str) or not pat.strip():
            raise ScannerFailure("package-lock.json records workspace pattern %r" % (pat,))
        clean = pat.strip()
        while clean.startswith("./"):
            clean = clean[2:]
        clean = clean.strip("/")
        if not clean or clean.startswith("!"):
            raise ScannerFailure("package-lock.json workspace pattern %r uses negation, "
                                 "which this gate does not implement" % pat)
        for token, what in UNSUPPORTED_GLOB:
            if token in clean:
                raise ScannerFailure("package-lock.json workspace pattern %r uses %s, "
                                     "which this gate does not implement" % (pat, what))
        out.append(clean.split("/"))
    return out


def declared_workspace(path, patterns):
    """Whether a lock path is a workspace npm declares, segment by segment.

    `*` never crosses `/` here: npm's `packages/*` matches `packages/cli` and
    not `packages/nested/web`, so a nested directory cannot pose as a workspace.
    """
    parts = path.split("/")
    for pat in patterns:
        if len(pat) == len(parts) and all(
                fnmatch.fnmatchcase(seg, want) for seg, want in zip(parts, pat)):
            return True
    return False


def origins(recs, patterns):
    """(record key, label, is a declared workspace) for every origin.

    npm writes ordinary `file:` link targets outside `node_modules` too, so the
    walk starts from every such record; only one the root manifest also declares
    as a workspace may satisfy a scope pair, everything else fails closed.
    """
    out = [("", ROOT_ORIGIN, False)]
    for key in sorted(recs):
        if key and "node_modules/" not in key:
            out.append((key, real_name(key, recs[key]), declared_workspace(key, patterns)))
    return out


def via_problem(raw, rec, dep):
    """Why the un-followed direct dependency is not the registry package.

    A `link: true` record is a directory package whose source record carries no
    `resolved`, so identity is judged before the link is followed.
    """
    if not raw or "node_modules/" not in raw:
        return "resolved outside node_modules"
    if rec.get("link"):
        return "is a link to %s" % rec.get("resolved")
    if real_name(raw, rec) != dep:
        return "is installed as %r" % real_name(raw, rec)
    resolved = str(rec.get("resolved") or "")
    if not resolved.startswith(REGISTRY + dep + "/-/"):
        return "has no registry tarball for %r (%s)" % (dep, resolved or "no resolved")
    if not rec.get("integrity"):
        return "has no integrity hash"
    return None


def pair_problem(key, label, declared, recs, raw, dep, pairs):
    """Why this (origin, direct dependency) chain may not be exempted."""
    if not key:
        return "the root package cannot be scoped"
    if not declared:
        return "origin %r is not a workspace declared by package-lock.json" % key
    if not recs[key].get("name"):
        return "workspace %r records no 'name', so no scope pair can name it" % key
    if (label, dep) not in pairs:
        return "pair not in the exception scope"
    why = via_problem(raw, recs.get(raw) or {}, dep)
    if why:
        return ("direct dependency '%s' is not the registry package of that name — %s"
                % (dep, why))
    return None


def check_audit_nodes(recs, pkg, nodes, reached):
    """npm audit's install locations must be lock records the walk reaches.

    Audit works from arborist's real graph, so it is the independent oracle: an
    empty list, or a location the walk never reaches, means the two disagree
    about where the package is installed, and no exemption may be granted.
    """
    keys = [n.strip() for n in nodes if n.strip()]
    if not keys:
        raise ScannerFailure("npm audit lists no install location for %s" % pkg)
    for key in keys:
        if key not in recs or real_name(key, recs[key]) != pkg:
            raise ScannerFailure("npm audit reports %s at %s, which package-lock.json "
                                 "does not record as %s" % (pkg, key, pkg))
    for key in keys:
        if key not in reached:
            raise ScannerFailure("npm audit reports %s at %s, which no production path "
                                 "in package-lock.json reaches" % (pkg, key))


def scope_pairs(entry, eco, pkg):
    """Validate the scope and return its (workspace, via) pairs."""
    if eco != "npm":
        raise ScopeError("exception scope is npm-only; %s findings cannot be scoped" % eco)
    if entry.get("package") != pkg:
        raise ScopeError("exception scope names package '%s', not '%s'"
                         % (entry.get("package"), pkg))
    exp = entry.get("expires")
    if not exp:
        raise ScopeError("exception scope requires an 'expires' date")
    try:
        date.fromisoformat(str(exp))
    except ValueError:
        raise ScopeError("exception 'expires' %r is not a YYYY-MM-DD date" % (exp,))
    scope = entry.get("scope")
    if not isinstance(scope, dict) or set(scope) != {"npm"}:
        raise ScopeError("exception scope must be {\"npm\": [{\"workspace\": ..., \"via\": ...}]}")
    items = scope["npm"]
    if not isinstance(items, list):
        raise ScopeError("exception scope 'npm' must be a non-empty list of "
                         "{workspace, via} objects")
    if not items:
        raise ScopeError("exception scope lists no chains")
    pairs = set()
    for item in items:
        if not isinstance(item, dict) or set(item) != {"workspace", "via"}:
            raise ScopeError("exception scope entries hold exactly 'workspace' and 'via'")
        if not all(isinstance(item[k], str) and item[k] for k in ("workspace", "via")):
            raise ScopeError("exception scope entries hold non-empty 'workspace' and 'via'")
        pairs.add((item["workspace"], item["via"]))
    if len(pairs) != len(items):
        raise ScopeError("exception scope repeats a (workspace, via) pair")
    return pairs


def scope_violation(entry, eco, pkg, nodes, scope_dir):
    """None when every production chain to pkg is inside the declared scope."""
    pairs = scope_pairs(entry, eco, pkg)
    recs = lock_records(scope_dir)
    patterns = workspace_patterns(recs)
    targets = {k for k in recs if real_name(k, recs[k]) == pkg}
    if not targets:
        raise ScannerFailure("package-lock.json records no %s" % pkg)
    bad, reached, chains = [], set(), 0
    for key, label, declared in origins(recs, patterns):
        for dep, raw, child in edges(recs, key):
            path, hit = walk(recs, child, targets)
            reached |= hit
            if not path:
                continue
            chains += 1
            full = " > ".join([label, dep] + path[1:])
            why = pair_problem(key, label, declared, recs, raw, dep, pairs)
            if why:
                bad.append("%s (%s)" % (full, why))
    if not chains:
        raise ScannerFailure("package-lock.json records no production path to %s" % pkg)
    check_audit_nodes(recs, pkg, nodes, reached)
    if not bad:
        return None
    return "exception scope does not cover production path %s" % " ; ".join(bad)


def expiry_reason(entry, today):
    """Why a dated exception no longer holds; an unparseable date never exempts."""
    raw = entry.get("expires")
    if raw is None or raw == "":
        return None
    try:
        day = date.fromisoformat(str(raw))
    except ValueError:
        return "exception 'expires' %r is not a YYYY-MM-DD date" % (raw,)
    if day < today:
        return "exception expired %s" % raw
    return None


def adr_problem(entry, adr):
    """The ADR id must be a real id and resolve to a docs/adr file."""
    m = re.fullmatch(r"ADR-(\d{4})", adr)
    if not m:
        return "exception adr '%s' is not an ADR id (expected ADR-NNNN)" % adr
    if not glob.glob(os.path.join("docs", "adr", m.group(1) + "-*.md")):
        return "exception adr %s has no docs/adr/%s-*.md file" % (adr, m.group(1))
    return None


def evaluate(rows_file, exceptions_path, scope_dir):
    with open(exceptions_path) as fh:
        exceptions = json.load(fh).get("exceptions") or {}
    today = date.today()
    fails, exempted, total = [], 0, 0
    with open(rows_file) as fh:
        for line in fh:
            parts = line.rstrip("\n").split("\t")
            if not parts[0]:
                continue
            eco, pkg, ver, sev, adv = parts[:5]
            nodes = [n for n in (parts[5].split(",") if len(parts) > 5 and parts[5] else []) if n]
            total += 1
            entry = exceptions.get(adv)
            if not entry:
                fails.append((eco, pkg, ver, sev, adv, "not allowlisted"))
                continue
            adr = entry.get("adr") or ""
            problem = adr_problem(entry, adr) or expiry_reason(entry, today)
            if problem:
                fails.append((eco, pkg, ver, sev, adv, problem))
                continue
            scope = ""
            if "scope" in entry:
                try:
                    why = scope_violation(entry, eco, pkg, nodes, scope_dir)
                except ScopeError as e:
                    why = str(e)
                except ScannerFailure as e:
                    print("audit-deps: %s" % e, file=sys.stderr)
                    return 2
                if why:
                    fails.append((eco, pkg, ver, sev, adv, why))
                    continue
                scope = " (scope: %s)" % ", ".join(
                    "%s > %s" % (i["workspace"], i["via"]) for i in entry["scope"]["npm"])
            exempted += 1
            print("  warn %s:%s@%s %s %s — exempted by %s%s"
                  % (eco, pkg, ver, sev, adv, adr, scope))
    for eco, pkg, ver, sev, adv, why in fails:
        print("  fail %s:%s@%s %s %s — %s" % (eco, pkg, ver, sev, adv, why))
    print("audit-deps: %d findings — %d exempted by exceptions.json, %d failing"
          % (total, exempted, len(fails)))
    if fails:
        print("audit-deps: upgrade the dependency or add an exceptions.json entry "
              "keyed by the advisory id with its ADR", file=sys.stderr)
    return 1 if fails else 0


if __name__ == "__main__":
    try:
        sys.exit(evaluate(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "."))
    except ScannerFailure as e:
        print("audit-deps: %s" % e, file=sys.stderr)
        sys.exit(2)
    except Exception as e:
        # A crash must read as a scanner failure, not as an unexcepted finding,
        # and must never print a traceback a log reader has to interpret.
        print("audit-deps: internal error: %s: %s" % (type(e).__name__, e), file=sys.stderr)
        sys.exit(2)
