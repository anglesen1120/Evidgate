#!/usr/bin/env bash
# check-links.sh — dead-link check for every tracked + untracked-not-ignored
# *.md file (plan 01.9.1-T3, AC3; the "lychee or equivalent" of AC3 — in-repo
# implementation on python3, so no package and no ADR-0002 matrix row).
#
# For every inline markdown link [text](target) and image ![alt](target):
#   - http(s)://, mailto: and any other scheme://, and protocol-relative // are
#     skipped (external);
#   - a relative path must resolve (inside the repo, relative to the .md file)
#     to an existing file or directory;
#   - a #fragment on a .md target (including a bare #fragment = same file) must
#     match a GitHub-style heading slug in that file;
#   - links inside fenced code blocks (``` / ~~~) and inline `code` spans are
#     ignored.
# Prints "E <file>:<line>: dead link <target>" per failure; exit 1 if any.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

python3 - "$ROOT" <<'PYEOF'
import os
import re
import subprocess
import sys
import unicodedata
from urllib.parse import unquote

root = os.path.abspath(sys.argv[1])

files = subprocess.run(
    ["git", "-C", root, "ls-files", "-c", "-o", "--exclude-standard", "--", "*.md"],
    check=True, capture_output=True, text=True,
).stdout.splitlines()

errors = 0
nlinks = 0

FENCE_RE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")
LINK_RE = re.compile(r"!?\[[^\]]*\]\(([^)\n]*)\)")
SCHEME_RE = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")
HEADING_RE = re.compile(r"^#{1,6}[ \t]+(.*?)[ \t]*#*[ \t]*$")
ANAME_RE = re.compile(r'<a\s+(?:name|id)=["\']([^"\']+)["\']', re.IGNORECASE)


def strip_markup(text):
    # Remove inline code spans and emphasis/link markup so slugs match GitHub.
    text = re.sub(r"`+([^`]*)`+", r"\1", text)
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"<[^>]+>", "", text)
    return re.sub(r"[*_~]", "", text)


def github_slug(text):
    text = strip_markup(text).strip().lower()
    out = []
    for ch in text:
        cat = unicodedata.category(ch)
        if ch == " ":
            out.append("-")
        elif cat[0] in ("L", "N") or ch in ("-", "_"):
            out.append(ch)
    return "".join(out)


def anchors_of(path):
    """GitHub-style slugs for the headings of one .md file (dup heading -> -1,-2)."""
    slugs = set()
    seen = {}
    in_fence = None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            m = FENCE_RE.match(line)
            if in_fence:
                if m and m.group(1)[0] == in_fence and len(m.group(1)) >= 3:
                    in_fence = None
                continue
            if m:
                in_fence = m.group(1)[0]
                continue
            h = HEADING_RE.match(line)
            if h:
                slug = github_slug(h.group(1))
                n = seen.get(slug, 0)
                seen[slug] = n + 1
                slugs.add(slug if n == 0 else f"{slug}-{n}")
            for a in ANAME_RE.findall(line):
                slugs.add(a)
    return slugs


def strip_inline_code(line):
    """Blank out `code` spans so links inside them are ignored."""
    out = list(line)
    i = 0
    n = len(line)
    while i < n:
        if line[i] == "`":
            j = i
            while j < n and line[j] == "`":
                j += 1
            run = line[i:j]
            k = line.find(run, j)
            if k != -1:
                for p in range(i, k + len(run)):
                    out[p] = " "
                i = k + len(run)
                continue
        i += 1
    return "".join(out)


anchor_cache = {}

for rel in files:
    path = os.path.join(root, rel)
    if not os.path.isfile(path):
        continue
    with open(path, encoding="utf-8") as fh:
        lines = fh.readlines()
    in_fence = None
    for lineno, raw in enumerate(lines, 1):
        m = FENCE_RE.match(raw)
        if in_fence:
            if m and m.group(1)[0] == in_fence:
                in_fence = None
            continue
        if m:
            in_fence = m.group(1)[0]
            continue
        line = strip_inline_code(raw)
        for lm in LINK_RE.finditer(line):
            inner = lm.group(1).strip()
            if not inner:
                continue
            if inner.startswith("<") and ">" in inner:
                inner = inner[1:inner.index(">")]
            else:
                # optional "title" or 'title' after whitespace
                inner = re.split(r"\s", inner, 1)[0]
            target = inner
            if not target:
                continue
            if SCHEME_RE.match(target) or target.startswith("//"):
                continue  # external (http(s), mailto, …)
            nlinks += 1
            ppath, _, frag = target.partition("#")
            ppath = unquote(ppath)
            frag = unquote(frag)
            if ppath == "":
                resolved = path
            else:
                resolved = os.path.normpath(os.path.join(os.path.dirname(path), ppath))
                if not (resolved == root or resolved.startswith(root + os.sep)):
                    print(f"E {rel}:{lineno}: dead link {target} (outside repo)")
                    errors += 1
                    continue
                if not os.path.exists(resolved):
                    print(f"E {rel}:{lineno}: dead link {target}")
                    errors += 1
                    continue
            if frag and resolved.lower().endswith(".md"):
                if resolved not in anchor_cache:
                    anchor_cache[resolved] = anchors_of(resolved)
                if frag not in anchor_cache[resolved]:
                    print(f"E {rel}:{lineno}: dead link {target} (anchor)")
                    errors += 1

if errors:
    sys.exit(1)
print(f"check-links: {len(files)} md files, {nlinks} internal links ok")
sys.exit(0)
PYEOF
