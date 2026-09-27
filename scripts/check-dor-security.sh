#!/usr/bin/env bash
# check-dor-security.sh — the Definition-of-Ready security-review check (11.1.1-T2, AC4).
#
# Three requirements, all from AC4 / 00 §12 / plans/features/README.md §6:
#   1. the DoR checklist (plans/features/README.md §6) contains the four review questions:
#      authorization, retention/deletion, telemetry, failure path;
#   2. .github/PULL_REQUEST_TEMPLATE.md references that DoR and carries the four questions;
#   3. every feature plan added or modified in the change set under review answers all four
#      in its §8 "Security, privacy and performance" section, each on a labelled line.
#
# Scoping decision (the AC leaves it open): the DoR is "checked at build start" (00 §12,
# README §6), so a plan is in scope when it is new or changed in *this* change set. The
# pre-existing plans predate the rule and are not retro-failed; they must answer the four
# questions the first time they are touched. Nothing here looks at git history beyond the
# change set, and there is no waiver switch.
#
# A labelled answer is a line whose first cell/words are the label, followed by ":" or "|"
# or an em/en dash and then non-empty content, e.g.
#   - **Telemetry:** metric evidgate_runs_total; no customer identifiers
#   | Failure path | 502 `upstream_error`; operator runbook 10.7.1-T1 |
# Fenced code blocks are ignored.
#
# Usage:
#   scripts/check-dor-security.sh                 # base auto-detected (origin/develop, develop, origin/main, main)
#   scripts/check-dor-security.sh --base <ref>    # diff <ref>...HEAD, plus staged, unstaged and untracked files
#   scripts/check-dor-security.sh --plan <file>   # check one plan explicitly (repeatable)
#   scripts/check-dor-security.sh --list          # print the in-scope plans and exit 0
# Prints "E <file>: <problem>" per failure; exit 1 if any.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

python3 - "$ROOT" "$@" <<'PYEOF'
import os, re, subprocess, sys

root, argv = sys.argv[1], sys.argv[2:]

base = None
explicit = []
show_list = False
i = 0
while i < len(argv):
    a = argv[i]
    if a == "--base":
        i += 1
        if i >= len(argv):
            sys.exit("check-dor-security: --base needs a git ref")
        base = argv[i]
    elif a == "--plan":
        i += 1
        if i >= len(argv):
            sys.exit("check-dor-security: --plan needs a path")
        explicit.append(argv[i])
    elif a in ("--list", "-l"):
        show_list = True
    elif a in ("-h", "--help"):
        print(
            "usage: scripts/check-dor-security.sh [--base <git-ref>] [--plan <file>]... [--list]\n"
            "  checks the DoR four review questions in plans/features/README.md §6 and\n"
            "  .github/PULL_REQUEST_TEMPLATE.md, and in every feature plan added or changed\n"
            "  in the change set (base defaults to origin/develop, then develop/main)"
        )
        sys.exit(0)
    else:
        sys.exit(f"check-dor-security: unknown argument {a!r} (see the header for usage)")
    i += 1

PLAN_RE = re.compile(r"^plans/features/\d{2}-[^/]+/\d{2}\.\d+\.\d+-[^/]+\.md$")
DOR_FILE = "plans/features/README.md"
PR_TEMPLATE = ".github/PULL_REQUEST_TEMPLATE.md"

# label -> regex for the label itself (case-insensitive)
QUESTIONS = [
    ("authorization", r"authoriz(?:ation|sation)"),
    ("retention/deletion", r"retention(?:\s*/\s*deletion)?"),
    ("telemetry", r"telemetry"),
    ("failure path", r"failure\s+paths?"),
]
# label, optional filler (no "|"), then a separator; the decoration prefix covers bullets,
# ordered items, checkboxes, headings, block quotes, table pipes and emphasis markers.
LINE_RE = {
    label: re.compile(
        r"^(?:[-*+>]\s+(?:\[[ xX]\]\s+)?|#{1,6}\s+|\d+\.\s+)?[\s|*_`]*"
        r"(?:" + pat + r")\b[^|\n]{0,48}?(?:[:|]|\s[—–])",
        re.I,
    )
    for label, pat in QUESTIONS
}
CONTENT_CLEAN = re.compile(r"^[\s|*_`]*(?P<rest>.*)$")

errors = []


def read(path):
    with open(os.path.join(root, path), encoding="utf-8") as fh:
        return fh.read()


def body_lines(text):
    """Yield non-fenced lines (fence markers and their content are skipped)."""
    fenced = False
    for line in text.splitlines():
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
            continue
        if not fenced:
            yield line


def answered_lines(text):
    """Map each question label to the answer text found for it (or None)."""
    found = {label: None for label, _ in QUESTIONS}
    for line in body_lines(text):
        for label, _ in QUESTIONS:
            if found[label] is not None:
                continue
            m = LINE_RE[label].match(line)
            if not m:
                continue
            rest = CONTENT_CLEAN.match(line[m.end():]).group("rest").strip()
            if rest:
                found[label] = rest
    return found


def section(text, number):
    """Text of '## <number>. …' up to the next top-level '## ' heading ('' when absent).

    Headings inside fenced code blocks do not end the section (the README template
    listing contains '## 1. Goal and non-goals' inside a fence).
    """
    lines = text.splitlines()
    start = next(
        (i for i, l in enumerate(lines) if re.match(rf"^## {number}\. ", l)), None
    )
    if start is None:
        return ""
    out, fenced = [], False
    for line in lines[start + 1:]:
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
        elif not fenced and line.startswith("## "):
            break
        out.append(line)
    return "\n".join(out)


def report(path, missing, where):
    for label in missing:
        errors.append(
            f"E {path}: {where} does not answer the DoR question {label!r} — "
            f"add a labelled line such as '- **{label.capitalize()}:** …'"
        )


# --- 1. the DoR checklist itself (the grep AC4 names) ------------------------------
dor = read(DOR_FILE)
dor_sec = section(dor, 6)
if not dor_sec:
    errors.append(f"E {DOR_FILE}: no '## 6. Feature plan template and Definition of Ready' section")
else:
    ok = answered_lines(dor_sec)
    report(DOR_FILE, [l for l, v in ok.items() if not v], "the DoR checklist (§6)")

# --- 2. the PR template references the DoR and asks the four questions -------------
pr = read(PR_TEMPLATE)
if "Definition of Ready" not in pr and "plans/features/README.md" not in pr:
    errors.append(f"E {PR_TEMPLATE}: does not reference the Definition of Ready checklist")
ok = answered_lines(pr)
report(PR_TEMPLATE, [l for l, v in ok.items() if not v], "the reviewer checklist")

# --- 3. feature plans added or changed in this change set ---------------------------
def git(*args):
    return subprocess.run(
        ["git", "-C", root, *args], capture_output=True, text=True, check=False
    )


def detect_base():
    if base is not None:
        return base
    for ref in ("origin/develop", "develop", "origin/main", "main"):
        if git("rev-parse", "--verify", "--quiet", ref + "^{commit}").returncode == 0:
            return ref
    return None


def in_scope():
    if explicit:
        return sorted(set(explicit))
    ref = detect_base()
    cmds = [
        ["diff", "--name-only", "--diff-filter=ACMR", "--cached"],
        ["diff", "--name-only", "--diff-filter=ACMR"],
        ["ls-files", "--others", "--exclude-standard"],
    ]
    if not ref and os.environ.get("CI"):
        errors.append(
            "E no base ref (origin/develop, develop, origin/main, main) resolves: "
            "check out full history (fetch-depth: 0) or pass --base"
        )
    if ref:
        cmds.insert(0, ["diff", "--name-only", "--diff-filter=ACMR", f"{ref}...HEAD"])
    seen = set()
    for cmd in cmds:
        r = git(*cmd)
        if r.returncode != 0:
            errors.append(f"E git {' '.join(cmd)}: {r.stderr.strip() or 'failed'}")
            continue
        seen.update(ln.strip() for ln in r.stdout.splitlines() if ln.strip())
    return sorted(f for f in seen if PLAN_RE.match(f))


plans = in_scope()
if show_list:
    for p in plans:
        print(p)
    sys.exit(0)

for p in plans:
    path = p if os.path.isabs(p) else os.path.join(root, p)
    if not os.path.exists(path):
        errors.append(f"E {p}: file not found")
        continue
    with open(path, encoding="utf-8") as fh:
        text = fh.read()
    sec = section(text, 8)
    if not sec:
        errors.append(f"E {p}: no '## 8. Security, privacy and performance' section")
        continue
    ok = answered_lines(sec)
    report(p, [l for l, v in ok.items() if not v], "§8")

for e in errors:
    print(e)
print(
    f"check-dor-security: {len(plans)} feature plan(s) in scope; "
    f"{len(errors)} error(s)"
)
sys.exit(1 if errors else 0)
PYEOF
