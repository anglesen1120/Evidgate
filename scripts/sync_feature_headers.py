#!/usr/bin/env python3
"""Rewrite the Milestone, Version, Size, 'Depends on (stories)' and 'Blocks (stories)' cells of every feature-plan header
from the task graph (scripts/feature_index.py), so headers never drift from the area plans."""
import json, re, subprocess, sys
rows = json.loads(subprocess.check_output([sys.executable, "scripts/feature_index.py", "--json"]))
changed = 0
for r in rows:
    try: txt = open(r["path"], encoding="utf-8").read()
    except FileNotFoundError: continue
    lines = txt.split("\n")
    for i, l in enumerate(lines):
        if l.startswith("| Area plan | Milestone |"):
            j = i + 2
            cells = re.split(r"(?<!\\)\|", lines[j].strip())[1:-1]
            if len(cells) != 7: print("bad header", r["path"]); break
            ms = sorted({t["ms"] for t in r["tasks"]}); vs = sorted({t["ver"] for t in r["tasks"]})
            cells[1] = " " + "/".join(ms) + " "
            cells[2] = " " + "/".join(vs) + " "
            sizes = " + ".join(t["size"] for t in r["tasks"])
            cells[3] = f" {r['size']:g} d ({sizes}) "
            cells[5] = " " + (", ".join(r["deps"]) or "—") + " "
            cells[6] = " " + (", ".join(r["blocks"]) or "—") + " "
            new = "|" + "|".join(cells) + "|"
            if new != lines[j]: lines[j] = new; changed += 1
            break
    open(r["path"], "w", encoding="utf-8").write("\n".join(lines))
print("headers updated:", changed)
