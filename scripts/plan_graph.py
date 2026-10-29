#!/usr/bin/env python3
"""Parse task rows of plans/01-13 and report sums, dangling ids, cycles and story waves."""
import re, glob, sys, json
from collections import defaultdict
SIZE = {"XS": .25, "S": .5, "M": 1, "L": 2, "XL": 3}
ROW = re.compile(r"^\|\s*(\d{2}\.\d+\.\d+-T\d+)\s*\|(.*)$")
STORY = re.compile(r"^#### Story (\d{2}\.\d+\.\d+)\s+(.*)$")
tasks, stories = {}, {}
bad_tokens = []
for f in sorted(glob.glob("plans/[01][0-9]-*.md")):
    if f.endswith("00-overview.md"): continue
    cur = None
    for ln in open(f, encoding="utf-8"):
        m = STORY.match(ln)
        if m:
            cur = m.group(1); stories[cur] = {"file": f, "title": m.group(2).split(" — ")[0].strip()}
            continue
        m = ROW.match(ln)
        if not m: continue
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", m.group(2).rstrip().rstrip("|"))]
        if len(cells) < 5: continue
        tid = m.group(1); size, ver, ms, dep = cells[1], cells[2], cells[3], cells[4]
        if size not in SIZE: continue
        deps = re.findall(r"\d{2}\.\d+\.\d+-T\d+", dep)
        rest = re.sub(r"\d{2}\.\d+\.\d+(-T\d+)?", "", dep)
        if rest.replace(",", "").replace("—", "").replace("-", "").strip():
            bad_tokens.append(f"{tid}: unrecognised dependency text {dep!r}")
        story_refs = re.findall(r"(?<![\w.-])(\d{2}\.\d+\.\d+)(?!-T|\.\d|\d)", dep)
        tasks[tid] = dict(story=tid.split("-")[0], size=size, ver=ver, ms=ms, deps=deps, story_refs=story_refs, file=f, sub=cur)
errs = list(bad_tokens)
# A bare story id in "Depends on" means every task of that story.
for t, d in tasks.items():
    for sref in d.pop("story_refs"):
        members = [x for x in tasks if tasks[x]["story"] == sref]
        if not members: errs.append(f"{t} -> unknown story {sref}")
        d["deps"] += [x for x in members if x not in d["deps"] and x != t]
for t, d in tasks.items():
    if d["story"] != d["sub"]: errs.append(f"task {t} sits under story {d['sub']}")
    for x in d["deps"]:
        if x not in tasks: errs.append(f"{t} -> unknown {x}")
        elif int(tasks[x]["ms"][1]) > int(d["ms"][1]): errs.append(f"{t} ({d['ms']}) depends on later {x} ({tasks[x]['ms']})")
# cycles (task level)
color = {}
def dfs(u, stack):
    color[u] = 1
    for v in tasks[u]["deps"]:
        if v not in tasks: continue
        if color.get(v) == 1: errs.append("cycle: " + " -> ".join(stack + [u, v]))
        elif not color.get(v): dfs(v, stack + [u])
    color[u] = 2
sys.setrecursionlimit(10000)
for t in tasks:
    if not color.get(t): dfs(t, [])
# sums
per = defaultdict(lambda: defaultdict(float)); cnt = defaultdict(int); ms_sum = defaultdict(float)
for t, d in tasks.items():
    p = d["file"].split("/")[1][:2]; per[p][d["ver"]] += SIZE[d["size"]]; cnt[p] += 1; ms_sum[d["ms"]] += SIZE[d["size"]]
# story waves: story deps = union of task deps outside the story
sdeps = defaultdict(set)
for t, d in tasks.items():
    for x in d["deps"]:
        if x in tasks and tasks[x]["story"] != d["story"]: sdeps[d["story"]].add(tasks[x]["story"])
sms = {}
for t, d in tasks.items():
    s = d["story"]; sms[s] = min(sms.get(s, 9), int(d["ms"][1]))
# Stories whose tasks interleave depend on each other at story level, so the story
# graph is not a DAG. Condense its strongly connected components (the "[a + b]" cells
# of plans/features/README.md §5.1) and take the longest path over the condensation,
# as scripts/feature_index.py does: a wave has one value per story and must not move
# with set iteration order (memoising the raw graph stored cycle-truncated values).
si, sl, ss, son, scomp, sc = {}, {}, [], set(), {}, [0]
def scc(v):
    si[v] = sl[v] = sc[0]; sc[0] += 1; ss.append(v); son.add(v)
    for x in sorted(sdeps[v]):
        if x not in si: scc(x); sl[v] = min(sl[v], sl[x])
        elif x in son: sl[v] = min(sl[v], si[x])
    if sl[v] == si[v]:
        members = []
        while True:
            x = ss.pop(); son.discard(x); members.append(x)
            if x == v: break
        cid = "+".join(sorted(members))
        for m in members: scomp[m] = cid
for s in sorted(sms):
    if s not in si: scc(s)
cdeps = defaultdict(set)
for s in sms:
    for x in sdeps[s]:
        if scomp[x] != scomp[s]: cdeps[scomp[s]].add(scomp[x])
cwave = {}
def cw(c):
    if c not in cwave:
        cwave[c] = 1 + max([cw(x) for x in sorted(cdeps[c])] or [0])
    return cwave[c]
wave = {s: cw(scomp[s]) for s in sms}
if "--json" in sys.argv:
    json.dump(dict(stories={s: dict(stories.get(s, {}), ms=f"G{sms[s]}", wave=wave[s], deps=sorted(sdeps[s]),
        size=sum(SIZE[d['size']] for d in tasks.values() if d['story'] == s),
        tasks=sorted([t for t in tasks if tasks[t]['story'] == s], key=lambda x: int(x.split('-T')[1])))
        for s in sms}, tasks=tasks), sys.stdout, indent=1); sys.exit(0)
tot = defaultdict(float)
print("plan tasks V1 V2 V3 raw buffered")
for p in sorted(per):
    r = sum(per[p].values()); [tot.__setitem__(k, tot[k] + v) for k, v in per[p].items()]
    print(p, cnt[p], per[p]["V1"], per[p]["V2"], per[p]["V3"], r, round(r * 1.2, 1))
R = sum(tot.values())
print("TOTAL", sum(cnt.values()), tot["V1"], tot["V2"], tot["V3"], R, round(R * 1.2, 1))
print("by milestone:", {k: ms_sum[k] for k in sorted(ms_sum)})
print("stories:", len(sms), "max wave:", max(wave.values()))
print("errors:", len(errs)); [print("  ", e) for e in errs[:40]]
