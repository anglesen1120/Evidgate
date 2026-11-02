#!/usr/bin/env bash
# The lifecycle rule the review bucket gets is inlined in compose.review.yml
# because a bind mount of a repository file is unreadable through
# colima/virtiofs. Inlining makes a second copy, so this check keeps it byte-equal
# to infra/seaweedfs/pending-lifecycle.xml: a rule that drifted would expire the
# wrong prefix in the review bucket and no test would notice.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=infra/review/lib-check.sh
. "$SCRIPT_DIR/lib-check.sh"

SOURCE="$REVIEW_DIR/../seaweedfs/pending-lifecycle.xml"
[ -f "$SOURCE" ] || review_die "missing $SOURCE"

review_step 'comparing the inlined pending/ lifecycle with its source file'
python3 - "$REVIEW_COMPOSE" "$SOURCE" <<'PY'
import re
import sys

compose_path, source_path = sys.argv[1], sys.argv[2]
lines = open(compose_path, encoding='utf-8').read().splitlines()

start = None
for index, line in enumerate(lines):
    if re.fullmatch(r'\s{2}pending-lifecycle:', line):
        start = index
        break
if start is None:
    sys.exit('compose.review.yml has no configs.pending-lifecycle entry')

content_at = None
for index in range(start + 1, len(lines)):
    if re.fullmatch(r'\s{4}content:\s*\|', lines[index]):
        content_at = index
        break
if content_at is None:
    sys.exit('configs.pending-lifecycle has no "content: |" block')

block = []
for line in lines[content_at + 1 :]:
    if line.strip() and not line.startswith(' ' * 6):
        break
    block.append(line[6:] if line.strip() else '')
inlined = '\n'.join(block).rstrip('\n') + '\n'

# A YAML block scalar always ends with one newline; the source file may not.
expected = open(source_path, encoding='utf-8').read().rstrip('\n') + '\n'
if inlined != expected:
    sys.exit(
        'the inlined lifecycle rule differs from '
        f'{source_path}:\n--- inlined ---\n{inlined}--- expected ---\n{expected}'
    )
PY

review_step 'ok: the inlined lifecycle matches infra/seaweedfs/pending-lifecycle.xml'
