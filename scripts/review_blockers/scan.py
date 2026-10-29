"""One change set: the added-line rules, the gate-file warnings and D1.

The diff scan reads only the lines base...head adds, so debt already on the base
never fails a change set; D1 reads the whole head file of every migration the
change set touches, because a policy's missing `TO` clause is a property of the
statement, not of a line.
"""
from __future__ import annotations

from review_blockers import gitio, ratchet, rules, sql


def scan_change_set(base: str, head: str) -> tuple[list[rules.Finding], int]:
    """Every finding of base...head, plus the number of added lines examined.

    `gitio.changed_names` keeps rename detection off so `git mv` reports the old
    path as deleted and the new one as added: a gate file that moves is still a
    gate file change, and a deleted migration is not read as if it were still in
    the head tree.
    """
    names = gitio.changed_names(base, head)
    in_head = {path for path in gitio.git("ls-tree", "-r", "-z", "--name-only", head).split("\0") if path}
    found = rules.gate_findings(names)
    for path in names:
        if path.startswith("migrations/") and path.endswith(".sql") and path in in_head:
            for line_no, statement in sql.policies_without_roles(head, path):
                found.append(rules.Finding("BLOCK", "D1", path, line_no, statement))
    lines = list(gitio.added_lines(base, head))
    return found + rules.added_line_findings(lines, ratchet.U1_PATHS, ratchet.U1_IDS), len(lines)


def scan(base: str, head: str) -> list[rules.Finding]:
    """Every finding of base...head: the added-line rules plus the migration policy rule."""
    return scan_change_set(base, head)[0]
