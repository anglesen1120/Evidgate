#!/usr/bin/env bash
# The review web image must be built from the `review` configuration, not from
# `production`: the review stack has no human login yet, so its bundle carries
# the Development-only dev sign-in form, while the production bundle stays
# form-free. These are static assertions — no docker build, no node — because the
# ways this regresses (the image reverting to the production command, or the
# review script losing the sign-in exemption flag) are visible in the source.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../../.." && pwd)"
dockerfile="$repo/infra/review/Dockerfile.web"
web_package="$repo/apps/web/package.json"

ok() { printf '  ok: %s\n' "$*"; }
fail() {
  printf '  FAIL: %s\n' "$*" >&2
  exit 1
}

[ -f "$dockerfile" ] || fail "missing $dockerfile"
[ -f "$web_package" ] || fail "missing $web_package"

grep -qF -- 'npm run build:review -w apps/web' "$dockerfile" ||
  fail "Dockerfile.web does not build the web workspace with build:review"
if grep -qF -- 'npm run build -w apps/web' "$dockerfile"; then
  fail "Dockerfile.web still builds the production bundle"
fi
ok "the review web image is built from the review configuration"

grep -qF -- '"build:review": "ng build --configuration review' "$web_package" ||
  fail "apps/web has no build:review script starting with the review configuration"
grep -qF -- 'check-no-fixture-code.mjs --allow-dev-sign-in' "$web_package" ||
  fail "build:review does not pass --allow-dev-sign-in to the fixture-leak gate"
grep -qF -- '"build": "ng build &&' "$web_package" ||
  fail "the production build script no longer starts with a plain ng build"
ok "build:review runs the review configuration behind both gates"
