#!/usr/bin/env bash
# The compose file must carry references, never secret values — and the render
# path anybody might paste into a ticket must be the redacted one.
#
# Two facts about `docker compose config` make this check shaped the way it is:
#   1. with interpolation ON it prints the resolved value of every variable, so a
#      render of a real .env.review is a secrets dump. deploy.sh therefore never
#      runs `docker compose config` at all, and this script runs only over the
#      sentinel environment built from env.review.template (lib-check.sh), never
#      over the host's .env.review;
#   2. with `--no-interpolate` it prints the file's own text, which is what a
#      reviewer should paste. That render must contain no sentinel value.
#
# The substantive assertion is that in the redacted render every secret-named
# setting is a ${...} reference and every reference names a key the template
# declares: the value lives in .env.review (mode 600, written once) and nowhere
# in the repository, and a renamed key fails here instead of at deploy time.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=infra/review/lib-check.sh
. "$SCRIPT_DIR/lib-check.sh"

review_require docker "$(basename "$0")"
review_require python3 "$(basename "$0")"
review_require_no_interpolate "$(basename "$0")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

review_write_sentinel_env "$work/env.sentinel"

review_step 'rendering with --no-interpolate (the only pasteable render)'
review_render_redacted "$work/env.sentinel" "$work/redacted.json" \
  --profile tunnel --profile local --profile debug

review_step 'checking references and secret-named settings'
python3 - "$work/redacted.json" "$work/env.sentinel" <<'PY'
import json
import re
import sys

SECRET_NAME = re.compile(r'(PASSWORD|PASSWD|SECRET|TOKEN|_KEY|KEY$|HASH|PEPPER|CONNECTIONSTRING)', re.I)
REFERENCE = re.compile(r'^\$\{([A-Za-z0-9_]+)(:([-?])([^}]*))?\}$')

data = json.load(open(sys.argv[1], encoding='utf-8'))
declared = {
    line.partition('=')[0].strip()
    for line in open(sys.argv[2], encoding='utf-8')
    if '=' in line and not line.lstrip().startswith('#')
}
services = data.get('services', {})
if not services:
    sys.exit('the render has no services')

problems = []


def check(where, name, value):
    if 'SENTINEL' in value:
        problems.append(f'{where}: {name} carries a sentinel value (interpolation is on)')
    if not SECRET_NAME.search(name):
        return
    match = REFERENCE.match(value)
    if match is None:
        problems.append(f'{where}: {name} carries a literal value')
        return
    if match.group(1) not in declared:
        problems.append(f'{where}: {name} references ${{{match.group(1)}}}, which the template does not declare')
    # `:?message` refuses to render without the value; `:-` must default to
    # empty, because a non-empty default is a committed secret.
    if match.group(3) == '-' and match.group(4):
        problems.append(f'{where}: {name} has a non-empty default value')


for service, definition in sorted(services.items()):
    for name, value in (definition.get('environment') or {}).items():
        if isinstance(value, str):
            check(f'services.{service}.environment', name, value)
    for arg, value in (definition.get('build', {}).get('args') or {}).items():
        if isinstance(value, str):
            check(f'services.{service}.build.args', arg, value)

raw = json.dumps(services)
if 'EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD' in raw:
    problems.append(
        'a container receives EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD: only the user and the '
        'apr1 hash may reach web, the plaintext stays in .env.review for the owner'
    )

if problems:
    sys.exit('compose references are not safe:\n  ' + '\n  '.join(problems))
PY

# The password key is owner-facing only: it must not appear in the compose file at
# all, so no future edit can route it into a container by accident.
if grep -q 'EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD' "$REVIEW_COMPOSE"; then
  review_die "compose.review.yml names EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD; the container gets the user and the hash, never the password"
fi

review_step "ok: redacted render carries no sentinel, secret settings are \${...} references"
printf 'note: with interpolation ON compose prints the resolved values by design, so\n'
printf '      deploy.sh never runs compose config; quote the redacted form instead.\n'
