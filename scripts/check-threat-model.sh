#!/usr/bin/env bash
# check-threat-model.sh — the threat-model completeness lint (11.1.1-T1.3, AC1/AC2/AC3/AC7).
#
# The model (docs/security/threat-model-v1.md) is machine-parsed; this script is
# the gate that keeps it complete. It checks, per the shared format contract of
# story 11.1.1 (local://tm-contract.md):
#   1. every required module/boundary id is in the catalogue (named on failure);
#   2. catalogue ids and `### <id> — <name>` section headings agree both ways;
#   3. every section has at least one threat row;
#   4. threat ids are globally unique and carry their section's prefix;
#   5. every STRIDE cell is a comma-separated subset of S T R I D E;
#   6. every Disposition starts `control:` or `accepted:` (`open:` fails) and
#      carries exactly one such marker;
#   7. every task id in a Disposition resolves against the area plans
#      (plans/NN-*.md first-column cells; a sub-task id resolves via its parent),
#      and a `control:` carries at least one task id;
#   8. a `control:` carries exactly one `[implemented: …]` / `[planned]` tag and
#      every `[implemented: <path>]` path is a repo-relative regular file inside
#      the repo (no absolute path, backslash, `..` segment, directory or
#      symlink that resolves outside the root);
#   9. an `accepted: RR-n` names the RR id as the first token and points to an
#      `accepted` register entry;
#  10. register field rules (owner, ISO review date, next review, receiving task)
#      and unique register ids;
#  11. every RR is cited by a row and every cited RR exists;
#  12. the ritual slot table has M1/M2/M3 with valid dates;
#  13. the milestone log table has at least one dated row;
#  14. the §7 summary table equals the computed counts;
#  15. every date is a real calendar date (strict YYYY-MM-DD), and no code fence
#      is left unclosed (an unclosed fence would swallow the rest of the model).
#
# Only the tables whose header row is exactly one named in the contract are
# parsed; fenced code blocks and any other table are ignored.
#
# Usage:
#   scripts/check-threat-model.sh [--model <file>] [--plans-dir <dir>]
#   scripts/check-threat-model.sh --summary
#   scripts/check-threat-model.sh --self-test
# Prints "E <file>: <problem>" per failure and exits 1; exit 0 prints
# "threat model: N sections, M threats, K register entries OK".
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

python3 - "$ROOT" "$0" "$@" <<'PYEOF'
import datetime, os, re, subprocess, sys

root, script, argv = sys.argv[1], sys.argv[2], sys.argv[3:]

model = "docs/security/threat-model-v1.md"
plans_dir = "plans"
mode = "check"
i = 0
while i < len(argv):
    a = argv[i]
    if a == "--model":
        i += 1
        if i >= len(argv):
            sys.exit("check-threat-model: --model needs a path")
        model = argv[i]
    elif a == "--plans-dir":
        i += 1
        if i >= len(argv):
            sys.exit("check-threat-model: --plans-dir needs a path")
        plans_dir = argv[i]
    elif a == "--self-test":
        mode = "self-test"
    elif a == "--summary":
        mode = "summary"
    elif a in ("-h", "--help"):
        print(
            "usage: scripts/check-threat-model.sh [--model <file>] [--plans-dir <dir>]\n"
            "       scripts/check-threat-model.sh --summary\n"
            "       scripts/check-threat-model.sh --self-test\n"
            "  checks the threat model (default docs/security/threat-model-v1.md) against\n"
            "  the shared format contract; --summary prints the §7 counts table; --self-test\n"
            "  runs the real model and every fixture under fixtures/threat-model/."
        )
        sys.exit(0)
    else:
        sys.exit(f"check-threat-model: unknown argument {a!r} (see the header for usage)")
    i += 1

REQUIRED_MODULES = [
    "M-identity", "M-tenancy", "M-catalog", "M-orchestrator", "M-ingestion",
    "M-evidence", "M-policy", "M-healing", "M-usage", "M-audit", "M-notifications",
]
REQUIRED_BOUNDARIES = [
    "B-web", "B-cli-adapters", "B-agent", "B-healing-sandbox", "B-llm-gateway",
    "B-object-storage", "B-database", "B-ci", "B-artifact-parsers", "B-webhook-out",
    "B-operator",
]

CAT_HEADER = ["ID", "Kind", "Name", "Source"]
THREAT_HEADER = ["ID", "STRIDE", "Threat", "Attacker", "Impact", "Disposition"]
REG_HEADER = ["ID", "Title", "State", "Owner", "Reviewed", "Next review", "Receiving task", "Rationale"]
SLOT_HEADER = ["Milestone", "Re-review slot", "Last review", "Result"]
LOG_HEADER = ["Date", "Milestone", "Reviewer", "Change"]
SUM_HEADER = ["State", "Count"]

TASK_RE = re.compile(r"\b\d{2}\.\d+\.\d+-T\d+(?:\.\d+)?\b")
TASK_FULL_RE = re.compile(r"^\d{2}\.\d+\.\d+-T\d+(?:\.\d+)?$")
RR_RE = re.compile(r"\bRR-\d+\b")
ISO_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
SECTION_RE = re.compile(r"^(M-[a-z0-9-]+|B-[a-z0-9-]+)\s*[—–-]\s*(.+)$")
STRIDE_LETTERS = set("STRIDE")


def read(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def body_lines(text):
    """Non-fenced lines (fence markers and their content are skipped)."""
    fenced = False
    for line in text.splitlines():
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
            continue
        if not fenced:
            yield line


def fence_unclosed(text):
    """True when a code fence is opened and never closed (it swallows the rest)."""
    fenced = False
    for line in text.splitlines():
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
    return fenced


def is_iso_date(s):
    """True for a strict YYYY-MM-DD calendar date, not merely that shape."""
    if not ISO_RE.match(s):
        return False
    try:
        datetime.date.fromisoformat(s)
    except ValueError:
        return False
    return True


def impl_path_error(p):
    """Error string for a bad `[implemented: <p>]` path, else None.

    A cited path must be a repo-relative regular file: no absolute path, no
    backslash, no `..` segment, no directory, and nothing that resolves
    (through symlinks) outside the repo root.
    """
    if p.startswith("/") or p.startswith("\\") or re.match(r"^[A-Za-z]:[\\/]", p):
        return f"implemented path is absolute: {p}"
    if "\\" in p:
        return f"implemented path contains a backslash: {p}"
    if any(seg == ".." for seg in p.split("/")):
        return f"implemented path contains a .. segment: {p}"
    full = os.path.join(root, p)
    real_root = os.path.realpath(root)
    real = os.path.realpath(full)
    if real != real_root and not real.startswith(real_root + os.sep):
        return f"implemented path resolves outside the repo: {p}"
    if not os.path.exists(full):
        return f"implemented path does not exist: {p}"
    if not os.path.isfile(full):
        return f"implemented path is not a regular file: {p}"
    return None


def split_row(line):
    """Cells of a markdown table row, or None when the line is not one."""
    s = line.strip()
    if not s.startswith("|"):
        return None
    s = s.replace("\\|", "\x00")
    s = s[1:]
    if s.endswith("|"):
        s = s[:-1]
    return [c.strip().replace("\x00", "|") for c in s.split("|")]


def is_sep(cells):
    return bool(cells) and all(re.fullmatch(r":?-+:?", c) for c in cells)


def find_table(lines, header):
    """Data rows of the first table whose header cells equal `header` (else None)."""
    n = len(lines)
    for i in range(n):
        if split_row(lines[i]) != header:
            continue
        rows = []
        j = i + 1
        if j < n:
            sc = split_row(lines[j])
            if sc is not None and is_sep(sc):
                j += 1
        while j < n:
            rc = split_row(lines[j])
            if rc is None:
                break
            if is_sep(rc):
                j += 1
                continue
            rows.append(rc)
            j += 1
        return rows
    return None


def sections(lines):
    """[(id, name, block_lines)] for each `### <id> — <name>` heading."""
    out, cur = [], None
    for line in lines:
        m = re.match(r"^###\s+(.*)$", line)
        if m:
            if cur is not None:
                out.append(cur)
            sm = SECTION_RE.match(m.group(1).strip())
            cur = [sm.group(1), sm.group(2).strip(), []] if sm else [None, None, []]
        elif cur is not None:
            cur[2].append(line)
    if cur is not None:
        out.append(cur)
    return out


def load_tasks(plans_root):
    """Task ids from the first-column cells of the area-plan tables."""
    tasks = set()
    if not os.path.isdir(plans_root):
        return tasks
    for name in sorted(os.listdir(plans_root)):
        if not re.match(r"^\d{2}-.*\.md$", name):
            continue
        path = os.path.join(plans_root, name)
        if not os.path.isfile(path):
            continue
        for line in body_lines(read(path)):
            cells = split_row(line)
            if not cells:
                continue
            first = cells[0].strip().strip("`").strip()
            if TASK_FULL_RE.match(first):
                tasks.add(first)
    return tasks


def resolves(tid, tasks):
    if tid in tasks:
        return True
    parent = re.sub(r"\.\d+$", "", tid)
    return parent != tid and parent in tasks


def check(model_path, plans_root):
    """Return (errors, stats) for one model file."""
    errors = []
    stats = {"sections": 0, "threats": 0, "register": 0}

    def err(msg):
        errors.append(f"E {model_path}: {msg}")

    full = model_path if os.path.isabs(model_path) else os.path.join(root, model_path)
    if not os.path.isfile(full):
        err("file not found")
        return errors, stats

    raw = read(full)
    if fence_unclosed(raw):
        err("unclosed code fence")
    lines = list(body_lines(raw))
    tasks = load_tasks(plans_root if os.path.isabs(plans_root) else os.path.join(root, plans_root))

    # --- catalogue -----------------------------------------------------------
    cat = find_table(lines, CAT_HEADER)
    cat_ids = []
    if cat is None:
        err("no catalogue table (header '| ID | Kind | Name | Source |')")
    else:
        for row in cat:
            if len(row) < 4:
                err(f"catalogue row has {len(row)} cells, expected 4: {' | '.join(row)}")
                continue
            cid, kind = row[0], row[1]
            if not re.match(r"^(M-[a-z0-9-]+|B-[a-z0-9-]+)$", cid):
                err(f"catalogue id {cid!r} is not M-<module> or B-<slug>")
                continue
            if kind not in ("module", "boundary"):
                err(f"catalogue id {cid}: kind {kind!r} is not module or boundary")
            elif (cid.startswith("M-") and kind != "module") or (
                cid.startswith("B-") and kind != "boundary"
            ):
                err(f"catalogue id {cid}: kind {kind!r} does not match its {cid[0]}- prefix")
            cat_ids.append(cid)
    cat_set = set(cat_ids)
    for mid in REQUIRED_MODULES:
        if mid not in cat_set:
            err(f"missing required module {mid}")
    for bid in REQUIRED_BOUNDARIES:
        if bid not in cat_set:
            err(f"missing required boundary {bid}")

    # --- sections ------------------------------------------------------------
    secs = sections(lines)
    sec_ids = []
    seen_threat_ids = {}
    row_counts = {"impl": 0, "planned": 0, "accepted": 0}
    accepted_cites = []  # (threat id, RR-n) from `accepted:` dispositions
    see_cites = []       # (threat id, RR-n) from `(see RR-n)` in a control name
    for sid, name, block in secs:
        if sid is None:
            # a `### ` heading that is not `### <id> — <name>` (e.g. a level-3
            # subsection of §1) is not a module/boundary section; ignore it.
            continue
        sec_ids.append(sid)
        stats["sections"] += 1
        if sid not in cat_set:
            err(f"section {sid} has no catalogue row")
        suffix = re.sub(r"^[MB]-", "", sid)
        table = find_table(block, THREAT_HEADER)
        if not table:
            err(f"section {sid} has no threat rows")
            continue
        for row in table:
            if len(row) < 6:
                err(f"section {sid}: threat row has {len(row)} cells, expected 6: {' | '.join(row)}")
                continue
            tid, stride, disp = row[0], row[1], row[5]
            stats["threats"] += 1
            if not re.match(rf"^TH-{re.escape(suffix)}-\d+$", tid):
                err(f"section {sid}: threat id {tid!r} does not match TH-{suffix}-<n>")
            if tid in seen_threat_ids:
                err(f"duplicate threat id {tid} (also in section {seen_threat_ids[tid]})")
            else:
                seen_threat_ids[tid] = sid
            # STRIDE
            letters = [t.strip() for t in stride.split(",")]
            for letter in letters:
                if letter not in STRIDE_LETTERS:
                    err(f"row {tid}: invalid STRIDE letter {letter!r}")
            # Disposition
            if disp.startswith("open:"):
                err(f"row {tid}: disposition uses open: (not allowed in the committed model)")
                continue
            if not (disp.startswith("control:") or disp.startswith("accepted:")):
                err(f"row {tid}: disposition is neither control: nor accepted: ({disp!r})")
                continue
            if len(re.findall(r"\b(?:control|accepted):", disp)) > 1:
                err(f"row {tid}: disposition has more than one control:/accepted: marker")
            for task in TASK_RE.findall(disp):
                if not resolves(task, tasks):
                    err(f"row {tid}: unknown task id {task}")
            for rr in re.findall(r"\(see (RR-\d+)\)", disp):
                see_cites.append((tid, rr))
            if disp.startswith("control:"):
                if not TASK_RE.search(disp):
                    err(f"row {tid}: control has no task id")
                n_impl = disp.count("[implemented:")
                n_planned = disp.count("[planned]")
                if n_impl + n_planned == 0:
                    err(f"row {tid}: control has no [implemented: …] or [planned] tag")
                elif n_impl + n_planned > 1:
                    err(f"row {tid}: control has more than one [implemented: …]/[planned] tag")
                if n_impl:
                    row_counts["impl"] += 1
                    for m in re.finditer(r"\[implemented:\s*([^\]]*)\]", disp):
                        for p in m.group(1).split(","):
                            p = p.strip()
                            if not p:
                                continue
                            problem = impl_path_error(p)
                            if problem:
                                err(f"row {tid}: {problem}")
                else:
                    row_counts["planned"] += 1
            else:  # accepted:
                row_counts["accepted"] += 1
                if not re.match(r"accepted:\s*RR-\d+\b", disp):
                    err(f"row {tid}: accepted disposition does not start with RR-<n>")
                rrs = RR_RE.findall(disp)
                if not rrs:
                    err(f"row {tid}: accepted disposition does not cite an RR-<n>")
                for rr in rrs:
                    accepted_cites.append((tid, rr))

    for cid in cat_ids:
        if cid not in sec_ids:
            err(f"catalogue id {cid} has no section heading")

    # --- register ------------------------------------------------------------
    reg = find_table(lines, REG_HEADER)
    reg_state = {}
    if reg is None:
        err("no residual-risk register table (header '| ID | Title | State | Owner | Reviewed | Next review | Receiving task | Rationale |')")
    else:
        for row in reg:
            if len(row) < 8:
                err(f"register row has {len(row)} cells, expected 8: {' | '.join(row)}")
                continue
            rid, _title, state, owner, reviewed, nextrev, receiving, rationale = row[:8]
            stats["register"] += 1
            if not re.match(r"^RR-\d+$", rid):
                err(f"register id {rid!r} is not RR-<n>")
                continue
            if rid in reg_state:
                err(f"duplicate register id {rid}")
            reg_state[rid] = state
            if state not in ("accepted", "open-decision"):
                err(f"{rid}: state {state!r} is not accepted or open-decision")
            if not owner:
                err(f"{rid}: {state or 'register'} entry has no owner")
            if not is_iso_date(reviewed):
                err(f"{rid}: reviewed is not an ISO date ({reviewed!r})")
            if nextrev not in ("M2", "M3") and not is_iso_date(nextrev):
                err(f"{rid}: next review {nextrev!r} is not M2, M3 or an ISO date")
            if state == "open-decision":
                if not TASK_FULL_RE.match(receiving) or not resolves(receiving, tasks):
                    err(f"{rid}: open-decision receiving task {receiving!r} is not a known task id")
                if "[OPEN]" not in rationale:
                    err(f"{rid}: open-decision rationale does not contain [OPEN]")

    for tid, rr in accepted_cites:
        if rr not in reg_state:
            err(f"row {tid}: accepted cites unknown {rr}")
        elif reg_state[rr] != "accepted":
            err(f"row {tid}: accepted cites {rr} which is {reg_state[rr]}, not accepted")
    for tid, rr in see_cites:
        if rr not in reg_state:
            err(f"row {tid}: control cites unknown {rr}")
    all_cited = {rr for _, rr in accepted_cites} | {rr for _, rr in see_cites}
    for rr in sorted(reg_state):
        if rr not in all_cited:
            err(f"{rr}: not cited by any threat row")

    # --- ritual --------------------------------------------------------------
    slot = find_table(lines, SLOT_HEADER)
    if slot is None:
        err("no milestone slot table (header '| Milestone | Re-review slot | Last review | Result |')")
    else:
        seen_ms = {}
        for row in slot:
            if len(row) < 4:
                err(f"slot row has {len(row)} cells, expected 4: {' | '.join(row)}")
                continue
            ms, _slot, last = row[0], row[1], row[2]
            seen_ms[ms] = last
            if last != "pending" and not is_iso_date(last):
                err(f"ritual slot {ms}: last review {last!r} is not an ISO date or pending")
        for ms in ("M1", "M2", "M3"):
            if ms not in seen_ms:
                err(f"ritual slot table is missing {ms}")

    log = find_table(lines, LOG_HEADER)
    if not log:
        err("milestone log table has no dated rows")
    else:
        for row in log:
            if len(row) < 4:
                err(f"log row has {len(row)} cells, expected 4: {' | '.join(row)}")
                continue
            if not is_iso_date(row[0]):
                err(f"log row date {row[0]!r} is not an ISO date")

    # --- summary -------------------------------------------------------------
    computed = {
        "control (implemented)": row_counts["impl"],
        "control (planned)": row_counts["planned"],
        "accepted": row_counts["accepted"],
        "open": 0,
    }
    stats["counts"] = computed
    sumt = find_table(lines, SUM_HEADER)
    if sumt is None:
        err("no disposition summary table (header '| State | Count |')")
    else:
        got = {}
        for row in sumt:
            if len(row) < 2:
                err(f"summary row has {len(row)} cells, expected 2: {' | '.join(row)}")
                continue
            got[row[0]] = row[1]
        for state, want in computed.items():
            if state not in got:
                err(f"summary table is missing the {state!r} row")
            elif got[state] != str(want):
                err(f"summary table: {state} is {got[state]}, computed {want}")

    return errors, stats


def run_one(model_path, plans_root):
    """Run the checker as a subprocess (used by --self-test)."""
    cmd = ["bash", script, "--model", model_path, "--plans-dir", plans_root]
    r = subprocess.run(cmd, capture_output=True, text=True)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


# --- modes -------------------------------------------------------------------
if mode == "self-test":
    fixtures = os.path.join(root, "fixtures", "threat-model")
    real = model if os.path.isabs(model) else os.path.join(root, model)
    abs_plans = plans_dir if os.path.isabs(plans_dir) else os.path.join(root, plans_dir)
    if not os.path.isfile(real):
        print(f"E {model}: self-test: the real model does not exist")
        sys.exit(1)
    rc, out = run_one(real, abs_plans)
    if rc != 0:
        print(f"E {model}: self-test: the real model does not pass")
        sys.stdout.write(out)
        sys.exit(1)

    n_neg = n_ok = 0
    failed = 0
    neg_dir = os.path.join(fixtures, "negative")
    ok_dir = os.path.join(fixtures, "ok")
    for name in sorted(os.listdir(neg_dir)) if os.path.isdir(neg_dir) else []:
        if not name.endswith(".md"):
            continue
        path = os.path.join(neg_dir, name)
        expect_path = path[:-3] + ".expect"
        if not os.path.isfile(expect_path):
            print(f"E {path}: self-test: no sibling .expect file")
            failed += 1
            continue
        expect = read(expect_path).strip()
        rc, out = run_one(path, abs_plans)
        n_neg += 1
        if rc == 0:
            print(f"E {path}: self-test: expected exit 1, got 0")
            failed += 1
        elif expect and expect not in out:
            print(f"E {path}: self-test: expected message {expect!r} not found")
            sys.stdout.write(out)
            failed += 1
    for name in sorted(os.listdir(ok_dir)) if os.path.isdir(ok_dir) else []:
        if not name.endswith(".md"):
            continue
        path = os.path.join(ok_dir, name)
        rc, out = run_one(path, abs_plans)
        n_ok += 1
        if rc != 0:
            print(f"E {path}: self-test: expected exit 0, got {rc}")
            sys.stdout.write(out)
            failed += 1
    if failed:
        sys.exit(1)
    print(f"threat model self-test: {n_neg} negative, {n_ok} ok fixtures OK")
    sys.exit(0)

errors, stats = check(model, plans_dir)

if mode == "summary":
    # A pure printer: the orchestrator runs it to get the §7 counts even while
    # the model still carries the 0 placeholders (which the default check flags).
    computed = stats.get("counts", {})
    print("| State | Count |")
    print("| --- | --- |")
    for state in ("control (implemented)", "control (planned)", "accepted", "open"):
        print(f"| {state} | {computed.get(state, 0)} |")
    sys.exit(0)

for e in errors:
    print(e)
if errors:
    sys.exit(1)
print(
    f"threat model: {stats['sections']} sections, {stats['threats']} threats, "
    f"{stats['register']} register entries OK"
)
sys.exit(0)
PYEOF
