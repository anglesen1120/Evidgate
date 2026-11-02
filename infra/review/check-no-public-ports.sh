#!/usr/bin/env bash
# The tunnel profile must publish nothing on a host port, and the two local
# profiles may publish only on 127.0.0.1.
#
# Why this is a check and not just a convention: publishing a port is the one
# mistake in this file that puts the review stack on the internet without
# Cloudflare Access in front of it, and the yaml is edited by agents.
#
# Renders the compose file with a sentinel environment (lib-check.sh), so no
# command here can touch the host's real .env.review.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=infra/review/lib-check.sh
. "$SCRIPT_DIR/lib-check.sh"

review_require docker "$(basename "$0")"
review_require python3 "$(basename "$0")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

review_write_sentinel_env "$work/env.sentinel"

review_step 'rendering the tunnel profile'
review_render "$work/env.sentinel" "$work/tunnel.json" --profile tunnel

python3 - "$work/tunnel.json" <<'PY'
import json
import sys

services = json.load(open(sys.argv[1], encoding='utf-8')).get('services', {})
if 'cloudflared' not in services:
    sys.exit('the tunnel profile must contain cloudflared')
offenders = []
for name, service in sorted(services.items()):
    for mapping in service.get('ports') or []:
        host = mapping.get('host_ip') or '0.0.0.0'
        offenders.append(f'{name}: {host}:{mapping.get("published")}->{mapping.get("target")}')
if offenders:
    sys.exit('the tunnel profile publishes host port(s):\n  ' + '\n  '.join(offenders))
PY

review_step 'rendering the local and debug profiles (plus the build-only images)'
# `images` is not a runtime profile: it is rendered here so the build-only base
# services are covered by this check too, and a port added to one of them fails.
review_render "$work/env.sentinel" "$work/local.json" --profile local --profile debug --profile images
python3 - "$work/local.json" <<'PY'
import json
import sys

services = json.load(open(sys.argv[1], encoding='utf-8')).get('services', {})
expected = {'web-local', 'api-debug'}
found = {}
for name, service in sorted(services.items()):
    for mapping in service.get('ports') or []:
        found[name] = found.get(name, 0) + 1
        host = mapping.get('host_ip')
        if host != '127.0.0.1':
            sys.exit(f"{name} publishes {mapping.get('published')} on '{host}', not on 127.0.0.1")
if set(found) != expected:
    sys.exit(f'published-port services are {sorted(found)}, expected {sorted(expected)}')
for name, count in found.items():
    if count != 1:
        sys.exit(f'{name} publishes {count} ports; one loopback port per profile is the contract')
PY

review_step 'rendering the quick tunnel (profile tunnel-quick + compose.quick.yml)'
review_render_quick "$work/env.sentinel" "$work/quick.json" --profile tunnel-quick

python3 - "$work/quick.json" <<'PY'
import json
import sys

services = json.load(open(sys.argv[1], encoding='utf-8')).get('services', {})
if 'cloudflared-quick' not in services:
    sys.exit('profile tunnel-quick must contain cloudflared-quick')
if 'cloudflared' in services:
    sys.exit('profile tunnel-quick must not also start the token cloudflared')
offenders = []
for name, service in sorted(services.items()):
    for mapping in service.get('ports') or []:
        offenders.append(f'{name}: {mapping.get("host_ip") or "0.0.0.0"}:{mapping.get("published")}')
if offenders:
    sys.exit('the quick tunnel profile publishes host port(s):\n  ' + '\n  '.join(offenders))
environment = services['web'].get('environment') or {}
if environment.get('EVIDGATE_REVIEW_BASIC_AUTH') != 'required':
    sys.exit('compose.quick.yml did not force EVIDGATE_REVIEW_BASIC_AUTH=required on web')
if environment.get('EVIDGATE_REVIEW_TUNNEL_MODE') != 'quick':
    sys.exit('compose.quick.yml did not set EVIDGATE_REVIEW_TUNNEL_MODE=quick on web')
command = services['cloudflared-quick'].get('command') or []
if not command or command[-1] != 'http://web:8080':
    sys.exit(f'cloudflared-quick must target http://web:8080, got {command}')
PY

review_step 'ok: tunnel and tunnel-quick publish nothing, local/debug publish 127.0.0.1 only'
