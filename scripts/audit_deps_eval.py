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
# Not a package name (npm names cannot hold spaces), so a workspace called
# "root" cannot collide with the root package's own dependencies.
ROOT_ORIGIN = "the root package"


class ScopeError(Exception):
    """The exception itself is unusable: report the finding, never exempt it."""


class ScannerFailure(Exception):
    """The inputs cannot prove the chain: exit 2, never an exemption."""


def lock_records(scope_dir):
    """The lockfile v3 `packages` map; an unreadable lock is a scanner failure."""
    try:
        with open(os.path.join(scope_dir, "package-lock.json")) as fh:
            return json.load(fh)["packages"]
    except Exception as e:
        raise ScannerFailure("package-lock.json is unusable (%s)" % e)


def real_name(key, rec):
    """The installed package's real name: an alias record names its target."""
    return rec.get("name") or key.rsplit("node_modules/", 1)[-1]


def resolve(recs, key, dep):
    """The lock record `dep` resolves to from `key`, the way node resolves it.

    `<dir>/node_modules/<dep>`, then each ancestor's node_modules, and a link
    record follows `resolved` to its source directory.
    """
    cur = key
    while True:
        cand = (cur + "/" if cur else "") + "node_modules/" + dep
        if cand in recs:
            rec = recs[cand]
            target = rec.get("resolved") if rec.get("link") else cand
            return target if target in recs else None
        if not cur:
            return None
        nxt = re.sub(r"(^|/)node_modules/(@[^/]+/)?[^/]+$", "", cur)
        cur = nxt if nxt != cur else ""


def edges(recs, key):
    """(dependency, resolved record, required) for every production edge.

    Only `dependencies` is required; an optional or peer edge may legitimately
    be absent. Dev edges are never traversed, which matches `--omit=dev`: the
    walk starts at the root and workspace manifests' production edges.
    """
    rec = recs.get(key) or {}
    out = []
    for field in ("dependencies", "optionalDependencies", "peerDependencies"):
        for dep in rec.get(field) or {}:
            out.append((dep, resolve(recs, key, dep), field == "dependencies"))
    return out


def workspace_paths(recs):
    """Lock paths the root manifest declares as workspaces.

    A named record outside node_modules is not enough: npm writes ordinary
    `file:` link targets there too, and those are root dependencies.
    """
    root = recs.get("") or {}
    patterns = root.get("workspaces") or []
    if isinstance(patterns, dict):
        patterns = patterns.get("packages") or []
    return [key for key in recs
            if key and "node_modules/" not in key
            and any(fnmatch.fnmatchcase(key, str(pat).rstrip("/")) for pat in patterns)]


def witness(recs, first, targets):
    """One production path, by name, from the record `first` to a target node."""
    todo, seen = [(first, [real_name(first, recs[first])])], {first}
    while todo:
        key, path = todo.pop()
        if key in targets:
            return path
        for dep, child, required in edges(recs, key):
            if child is None:
                if required:
                    raise ScannerFailure(
                        "package-lock.json cannot resolve %s from %s" % (dep, key))
                continue
            if child in seen:
                continue
            seen.add(child)
            todo.append((child, path + [real_name(child, recs[child])]))
    return None


def reaching_chains(recs, targets):
    """(origin, direct dependency, child record, path) for every origin.

    Origins are the root package and each declared workspace, and reachability
    is decided per (origin, direct dependency) pair, so one shared intermediate
    cannot mask a consumer npm's deduplicated tree would not print.
    """
    origins = [(ROOT_ORIGIN, "")] + [(recs[k].get("name") or k, k) for k in workspace_paths(recs)]
    found = []
    for name, key in origins:
        for dep, child, required in edges(recs, key):
            if child is None:
                if required:
                    raise ScannerFailure(
                        "package-lock.json cannot resolve %s from %s" % (dep, key))
                continue
            path = witness(recs, child, targets)
            if path:
                found.append((name, dep, child, [name, dep] + path[1:]))
    return found


def check_audit_nodes(recs, pkg, nodes):
    """npm audit's install locations must be lock records of the real package."""
    for node in nodes:
        key = node.strip()
        if not key:
            continue
        if key not in recs or real_name(key, recs[key]) != pkg:
            raise ScannerFailure("npm audit reports %s at %s, which package-lock.json "
                                 "does not record as %s" % (pkg, key, pkg))


def scope_pairs(entry, eco, pkg):
    """Validate the scope and return its (workspace, via) pairs."""
    if eco != "npm":
        raise ScopeError("exception scope is npm-only; %s findings cannot be scoped" % eco)
    if entry.get("package") != pkg:
        raise ScopeError("exception scope names package '%s', not '%s'"
                         % (entry.get("package"), pkg))
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(entry.get("expires") or "")):
        raise ScopeError("exception scope requires an 'expires' date")
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
    check_audit_nodes(recs, pkg, nodes)
    targets = {k for k in recs if real_name(k, recs[k]) == pkg}
    if not targets:
        raise ScannerFailure("package-lock.json records no %s" % pkg)
    chains = reaching_chains(recs, targets)
    if not chains:
        raise ScannerFailure("package-lock.json records no production path to %s" % pkg)
    bad = []
    for origin, dep, child, path in chains:
        label = " > ".join(path)
        if origin == ROOT_ORIGIN or (origin, dep) not in pairs:
            bad.append(label)
            continue
        rec = recs[child]
        resolved = str(rec.get("resolved") or "")
        # An alias always names its target and a file:/git/link dependency always
        # carries that `resolved`; so a self-named record with a registry tarball
        # or no resolved at all is the registry package, and nothing else is.
        if real_name(child, rec) != dep or (resolved and not resolved.startswith(REGISTRY)):
            bad.append("%s (direct dependency '%s' is not the registry package of that name)"
                       % (label, dep))
    if not bad:
        return None
    return "exception scope does not cover production path %s" % " ; ".join(bad)


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
    today = date.today().isoformat()
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
            problem = adr_problem(entry, adr)
            if problem:
                fails.append((eco, pkg, ver, sev, adv, problem))
                continue
            exp = entry.get("expires")
            if exp and exp < today:
                fails.append((eco, pkg, ver, sev, adv, "exception expired %s" % exp))
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
