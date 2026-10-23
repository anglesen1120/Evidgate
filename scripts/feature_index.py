#!/usr/bin/env python3
"""Emit story metadata (slug, target path, milestone, wave, deps, blocks) for plans/features.

  --json            machine-readable list (used to brief writers and by check_features.py)
  --order           markdown for README §5.2 (waves per milestone)
  --index           markdown for README §7 (one row per story)
  --critical        task-level critical chain per milestone (README §5.2)
"""
import glob, json, os, re, subprocess, sys
from collections import defaultdict
g = json.loads(subprocess.check_output([sys.executable, "scripts/plan_graph.py", "--json"]))
S, T = g["stories"], g["tasks"]
SIZE = {"XS": .25, "S": .5, "M": 1, "L": 2, "XL": 3}
def key(s): return [int(x) for x in s.split(".")]
def _path(area, sid, title):
    """The existing feature file when there is exactly one, else the slug the writer should use."""
    found = glob.glob(f"plans/features/{area}/{sid}-*.md")
    return found[0] if len(found) == 1 else f"plans/features/{area}/{sid}-{slug(title)}.md"
def slug(title):
    t = re.sub(r"`", "", title.lower()); t = re.sub(r"[^a-z0-9]+", "-", t).strip("-")
    return "-".join(t.split("-")[:6])
blocks = defaultdict(set)
for s, d in S.items():
    for x in d["deps"]: blocks[x].add(s)
# Condense story-level strongly connected components ("bundles": stories whose tasks interleave).
sys.setrecursionlimit(100000)
_idx, _low, _st, _on, comp, _c = {}, {}, [], set(), {}, [0]
def _scc(v):
    _idx[v] = _low[v] = _c[0]; _c[0] += 1; _st.append(v); _on.add(v)
    for w in S[v]["deps"]:
        if w not in _idx: _scc(w); _low[v] = min(_low[v], _low[w])
        elif w in _on: _low[v] = min(_low[v], _idx[w])
    if _low[v] == _idx[v]:
        members = []
        while True:
            w = _st.pop(); _on.discard(w); members.append(w)
            if w == v: break
        cid = "+".join(sorted(members, key=key))
        for m in members: comp[m] = cid
for v in S:
    if v not in _idx: _scc(v)
cdeps = defaultdict(set); csize = defaultdict(float)
for v in S:
    csize[comp[v]] += S[v]["size"]
    for w in S[v]["deps"]:
        if comp[w] != comp[v]: cdeps[comp[v]].add(comp[w])
cwave, cpath = {}, {}
def _w(c):
    if c not in cwave:
        cwave[c] = 1 + max([_w(d) for d in cdeps[c]] or [0])
        best = max([cpath_of(d) for d in cdeps[c]] or [(0, [])])
        cpath[c] = (best[0] + csize[c], best[1] + [c])
    return cwave[c]
def cpath_of(c): _w(c); return cpath[c]
for c in set(comp.values()): _w(c)
rows = []
for s in sorted(S, key=key):
    d = S[s]; area = d["file"].split("/")[1][:-3]
    vers = sorted({T[t]["ver"] for t in d["tasks"]})
    ms = sorted({T[t]["ms"] for t in d["tasks"]})
    rows.append(dict(id=s, title=d["title"], area=area, path=_path(area, s, d["title"]),
                     ms=d["ms"], milestones=ms, versions=vers, wave=cwave[comp[s]], bundle=comp[s] if "+" in comp[s] else "", size=d["size"],
                     deps=sorted(d["deps"], key=key), blocks=sorted(blocks[s], key=key),
                     tasks=[dict(id=t, size=T[t]["size"], ver=T[t]["ver"], ms=T[t]["ms"], deps=T[t]["deps"]) for t in d["tasks"]]))
if "--json" in sys.argv: json.dump(rows, sys.stdout, indent=1); sys.exit()
if "--order" in sys.argv:
    by = defaultdict(list)
    for r in rows: by[(r["ms"], r["wave"])].append(r)
    print("| Milestone | Wave | Stories (placed at their earliest task milestone; bundles in brackets) | Story raw days (all milestones) |\n|---|---|---|---|")
    for (m, w) in sorted(by, key=lambda k: (k[0], k[1])):
        rs = by[(m, w)]; groups, items = defaultdict(list), []
        for r in rs:
            if r["bundle"]:
                if r["bundle"] not in groups: items.append(("b", r["bundle"]))
                groups[r["bundle"]].append(r["id"])
            else: items.append(("s", r["id"]))
        items = [x if k == "s" else "[" + " + ".join(groups[x]) + (f" — bundle {x.replace('+', ' + ')}" if len(groups[x]) < len(x.split("+")) else "") + "]" for k, x in items]
        print(f"| {m} | {w} | " + ", ".join(items) + f" | {sum(r['size'] for r in rs):g} |")
    sys.exit()
if "--bundles" in sys.argv:
    # Story-level SCCs with a task-level topological build order over their member tasks.
    print("| Bundle (stories built as one unit) | Raw days | Task-level build order (topological; parallel tasks may start together) |\n|---|---|---|")
    for c in sorted({v for v in comp.values() if "+" in v}, key=lambda c: key(c.split("+")[0])):
        members = set(c.split("+")); ts = [t for t in T if T[t]["story"] in members]
        indeg = {t: sum(1 for d in T[t]["deps"] if d in ts) for t in ts}; order = []
        ready = sorted([t for t in ts if indeg[t] == 0], key=lambda t: (key(t.split("-")[0]), int(t.split("-T")[1])))
        while ready:
            layer = ready; order.append(", ".join(layer)); ready = []
            for t in layer:
                for u in ts:
                    if t in T[u]["deps"]:
                        indeg[u] -= 1
                        if indeg[u] == 0: ready.append(u)
            ready.sort(key=lambda t: (key(t.split("-")[0]), int(t.split("-T")[1])))
        print(f"| {c.replace('+', ' + ')} | {csize[c]:g} | " + " → ".join(order) + " |")
    sys.exit()
if "--critical" in sys.argv:
    # Task-level longest dependency chain (size-weighted) from project start to the end of each milestone.
    print("| Up to | Work in this milestone (raw d) | Longest dependency chain from start (d) | Chain |\n|---|---|---|---|")
    for M in range(1, 5):
        ok = {t for t, d in T.items() if int(d["ms"][1]) <= M}
        memo = {}
        def lp(t):
            if t not in memo:
                b = max([lp(x) for x in T[t]["deps"] if x in ok] or [(0, [])])
                memo[t] = (b[0] + SIZE[T[t]["size"]], b[1] + [t])
            return memo[t]
        top = max(ok, key=lambda t: lp(t)[0]); d, path = lp(top)
        work = sum(SIZE[T[t]["size"]] for t in ok if int(T[t]["ms"][1]) == M)
        print(f"| G{M} | {work:g} | {d:g} | " + " → ".join(path) + " |")
    sys.exit()
if "--index" in sys.argv:
    print("| Story | Title | Milestone | Wave | Bundle | Raw days | Depends on | Blocks |\n|---|---|---|---|---|---|---|---|")
    for r in rows:
        rel = r["path"].replace("plans/features/", "")
        # Stories whose feature plan is not written yet (DoR pending) are listed without a link.
        sid = f"[{r['id']}]({rel})" if os.path.exists(r["path"]) else f"{r['id']} (feature plan pending)"
        print(f"| {sid} | {r['title']} | {r['ms']} | {r['wave']} | {'yes' if r['bundle'] else '—'} | {r['size']:g} | {', '.join(r['deps']) or '—'} | {', '.join(r['blocks']) or '—'} |")
