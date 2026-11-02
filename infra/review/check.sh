#!/usr/bin/env bash
# Everything about the review stack that can be verified without a build.
#
#   infra/review/check.sh                 # needs node + docker
#   infra/review/check.sh --static-only   # needs node only
#
# Reviewers and the deploy host run this; scripts/review/deploy.sh calls it with
# --static-only before it builds, so a pin or a lifecycle drift stops a deploy
# before a single layer is built.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
static_only=false

usage() {
  cat <<'USAGE'
usage: infra/review/check.sh [--static-only]

  --static-only   skip the two checks that render the compose file with docker
                  (check-no-public-ports.sh, check-config-redacted.sh)
  -h, --help      this text
USAGE
}

for arg in "$@"; do
  case "$arg" in
    --static-only) static_only=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "check.sh: unexpected argument '$arg' (try --help)" >&2
      exit 2
      ;;
  esac
done

node_bin="$(command -v node || true)"
if [ -z "$node_bin" ]; then
  cat >&2 <<'MSG'
check.sh: node is required on THIS host (not in a container): the nginx CSP header
snippet is generated from apps/web/scripts/csp.mjs, and the generator's node:test
suite is the only guard that it still matches that policy. Install node 20 or newer
on the deploy host and re-run; a host without node cannot run `--static-only`.
MSG
  exit 1
fi

# `node --test` needs a runner new enough to take a file argument: an older node
# fails with an unrelated syntax error, which reads like a broken test.
node_major="$("$node_bin" -p 'process.versions.node.split(".")[0]')"
if [ "$node_major" -lt 20 ]; then
  echo "check.sh: node $("$node_bin" -v) is too old; node 20 or newer is required for the node:test suite" >&2
  exit 1
fi

printf '== generate-nginx-headers.mjs tests\n'
"$node_bin" --test "$SCRIPT_DIR/generate-nginx-headers.test.mjs"

"$SCRIPT_DIR/check-pins.sh"
"$SCRIPT_DIR/check-inlined-files.sh"

if [ "$static_only" = false ]; then
  "$SCRIPT_DIR/check-no-public-ports.sh"
  "$SCRIPT_DIR/check-config-redacted.sh"
fi

printf '== review infra: all checks passed\n'
