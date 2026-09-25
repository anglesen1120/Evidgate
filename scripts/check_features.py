#!/usr/bin/env python3
"""Structural checks for plans/features/<NN>-<area>/<story-id>-<slug>.md against the area plans.

Checks: one file per story; H1; header table; the 11 sections of 00 §12 in order; one
`### <task-id>` block per area task in §10; sub-task ids/sizes (each ≤ S, sum == task size);
balanced code fences; every referenced story/task id exists. Exit 1 on any error.
"""
import glob, json, os, re, subprocess, sys

SIZE = {"XS": .25, "S": .5, "M": 1, "L": 2, "XL": 3}
SECTIONS = ["Goal and non-goals", "User stories and acceptance criteria", "Contracts touched",
            "Data model and migrations", "API shapes", "States and transitions", "Jobs and integrations",
            "Security, privacy and performance", "Test plan", "Tasks, sub-tasks and re-estimate", "Rollout"]

graph = json.loads(subprocess.check_output([sys.executable, "scripts/plan_graph.py", "--json"]))
stories, tasks = graph["stories"], graph["tasks"]
errors, warn = [], []
only = sys.argv[1] if len(sys.argv) > 1 else None  # optional plan prefix filter, e.g. "04"

files = {}
for f in glob.glob("plans/features/[01][0-9]-*/*.md"):
    m = re.match(r"(\d{2}\.\d+\.\d+)-", os.path.basename(f))
    if m: files.setdefault(m.group(1), []).append(f)

for sid, st in sorted(stories.items(), key=lambda kv: [int(x) for x in kv[0].split(".")]):
    if only and not sid.startswith(only + "."): continue
    fl = files.get(sid, [])
    if len(fl) != 1:
        errors.append(f"{sid}: expected 1 feature file, found {len(fl)} {fl}"); continue
    f = fl[0]; txt = open(f, encoding="utf-8").read(); lines = txt.splitlines()
    area = os.path.basename(st["file"])[:-3]
    if os.path.basename(os.path.dirname(f)) != area: errors.append(f"{f}: folder must be plans/features/{area}/")
    if not lines or not re.match(rf"# {re.escape(sid)} — \S", lines[0]): errors.append(f"{f}: H1 must be '# {sid} — <title>'")
    if "| Area plan | Milestone | Version |" not in txt: errors.append(f"{f}: header table missing")
    else:
        hi = next(k for k, l in enumerate(lines) if l.startswith("| Area plan | Milestone |"))
        hc = [c.strip() for c in re.split(r"(?<!\\)\|", lines[hi + 2].strip())[1:-1]] if hi + 2 < len(lines) else []
        if len(hc) == 7:
            want_deps = ", ".join(st["deps"]) or "—"
            if hc[5] != want_deps: errors.append(f"{f}: header 'Depends on' {hc[5]!r} != graph {want_deps!r} (run scripts/sync_feature_headers.py)")
            m = re.match(r"([\d.]+)\s*d", hc[4])
            if not m or abs(float(m.group(1)) - st["size"]) > 1e-9: errors.append(f"{f}: header Re-estimate {hc[4]!r} != story size {st['size']:g} d")
        else: errors.append(f"{f}: header row must have 7 cells")
    heads = [re.sub(r"\s*\(.*$", "", h) for h in re.findall(r"^## \d+\. (.+)$", txt, re.M)]
    want = [s if s != "API shapes" else "API shapes" for s in SECTIONS]
    got = [h if not h.startswith("API shapes") else "API shapes" for h in heads]
    if got != want: errors.append(f"{f}: sections {got} != 00 §12 template")
    if txt.count("```") % 2: errors.append(f"{f}: unbalanced code fences")
    sec10 = txt.split("## 10.", 1)[1].split("\n## 11.", 1)[0] if "## 10." in txt else ""
    for tid in st["tasks"]:
        blk = re.search(rf"^### {re.escape(tid)}\b.*?(?=^### |\Z)", sec10, re.M | re.S)
        if not blk: errors.append(f"{f}: §10 lacks '### {tid}' block"); continue
        subs = []
        for ln in blk.group(0).splitlines():
            cells = [c.strip() for c in re.split(r"(?<!\\)\|", ln.strip().strip("|"))] if ln.startswith("|") else []
            m = re.match(rf"{re.escape(tid)}\.(\d+)$", cells[0]) if cells else None
            if m and len(cells) >= 3:
                size = next((c for c in cells[2:] if c in SIZE), None)
                if size is None: errors.append(f"{f}: {tid}.{m.group(1)} has no size cell"); continue
                subs.append((m.group(1), size))
        if not subs: errors.append(f"{f}: {tid} has no sub-task rows"); continue
        nums = [int(n) for n, _ in subs]
        if nums != list(range(1, len(nums) + 1)): errors.append(f"{f}: {tid} sub-task ids not 1..n: {nums}")
        big = [f"{tid}.{n}={s}" for n, s in subs if SIZE[s] > .5]
        if big: errors.append(f"{f}: sub-tasks > S: {big}")
        tot = sum(SIZE[s] for _, s in subs); want_sz = SIZE[tasks[tid]["size"]]
        if abs(tot - want_sz) > 1e-9: errors.append(f"{f}: {tid} sub-tasks sum {tot} != task size {want_sz} ({tasks[tid]['size']})")
    extra = set(re.findall(r"^### (\d{2}\.\d+\.\d+-T\d+)\b", sec10, re.M)) - set(st["tasks"])
    if extra: errors.append(f"{f}: §10 has blocks for tasks not in the area plan: {sorted(extra)}")
    for ref in set(re.findall(r"\b(\d{2}\.\d+\.\d+-T\d+)(?:\.\d+)?\b", txt)):
        if ref not in tasks: errors.append(f"{f}: unknown task id {ref}")
    if len(lines) < 60: warn.append(f"{f}: thin ({len(lines)} lines)")
    for ref in set(re.findall(r"(?<![\d.])(\d{2})\.(\d+)\.(\d+)(?![\d-]|\.\d)", txt)):
        sref = ".".join(ref)
        if 1 <= int(ref[0]) <= 12 and ref[1] != "0" and sref not in stories:
            warn.append(f"{f}: story id {sref} not found")

orphans = [f for sid, fl in files.items() if sid not in stories for f in fl]
if orphans: errors.append(f"feature files without a story: {orphans}")
print(f"stories checked: {sum(1 for s in stories if not only or s.startswith(only + '.'))}, files: {sum(len(v) for v in files.values())}")
print(f"errors: {len(errors)}"); [print("  E", e) for e in errors[:200]]
print(f"warnings: {len(warn)}"); [print("  W", w) for w in warn[:60]]
sys.exit(1 if errors else 0)
