#!/usr/bin/env bash
# Executes the web image's start script (web-start.sh) against throwaway
# directories and asserts what it renders: the realm, and the proxy header that
# must exist only while basic auth is on.
#
# check-web-image.sh proves the same inside the built image. This runs without
# docker, so --static-only covers the rendering rules, including the on-to-off
# transition where a stale proxy-auth.conf would keep stripping the header.
set -uo pipefail

# shellcheck source=infra/review/lib-check.sh
. "$(cd "$(dirname "$0")" && pwd)/lib-check.sh"

SCRIPT="$REVIEW_DIR/web-start.sh"
[ -r "$SCRIPT" ] || {
  echo "web-start.test.sh: cannot read $SCRIPT" >&2
  exit 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fail=0
note() {
  echo "web-start.test.sh: $*" >&2
  fail=1
}

# A fixed apr1 hash: the script accepts nothing else, and minting one here would
# make the test depend on openssl.
# shellcheck disable=SC2016 # "$apr1$salt$digest" is credential text, not expansion
hash='$apr1$c2FsdA$ZHVtbXk'
strip='^proxy_set_header Authorization "";$'

# env -i so a caller's EVIDGATE_* cannot decide the outcome.
render() { # AUTH_DIR NAME=VALUE...
  local dir="$1"
  shift
  env -i PATH="$PATH" EVIDGATE_WEB_START_CHECK=1 EVIDGATE_WEB_START_AUTH_DIR="$dir" \
    "$@" /bin/sh "$SCRIPT"
}

printf '== web-start.sh renderings\n'

# --- basic auth on ----------------------------------------------------------
if render "$work/on" \
  EVIDGATE_REVIEW_TUNNEL_MODE=quick \
  EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer \
  EVIDGATE_REVIEW_BASIC_AUTH_HASH="$hash" >"$work/on.out" 2>&1; then
  grep -qx 'auth_basic "Evidgate review";' "$work/on/auth.conf" ||
    note 'the credential branch did not write the realm'
  grep -q '^auth_basic_user_file /' "$work/on/auth.conf" ||
    note 'the credential branch wrote no user file'
  [ -f "$work/on/proxy-auth.conf" ] ||
    note 'the credential branch wrote no proxy-auth.conf: nginx fails to start on the missing include'
  grep -qx "$strip" "$work/on/proxy-auth.conf" ||
    note 'basic auth is on but the start script does not empty Authorization for /v1/'
else
  note "the credential branch failed: $(head -n 3 "$work/on.out" | tr '\n' ' ')"
fi

# --- basic auth off: the header is the client's bearer token ----------------
if render "$work/off" \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH=optional \
  >"$work/off.out" 2>&1; then
  grep -qx 'auth_basic off;' "$work/off/auth.conf" ||
    note 'token mode without credentials did not turn the realm off'
  # nginx includes the file unconditionally, so every successful start must write
  # it, even when it carries nothing but the explanation.
  [ -f "$work/off/proxy-auth.conf" ] ||
    note 'the auth-off branch wrote no proxy-auth.conf: nginx fails to start on the missing include'
  if grep -q 'proxy_set_header' "$work/off/proxy-auth.conf" 2>/dev/null; then
    note 'basic auth is off but the start script still empties Authorization; every Api bearer token would be lost'
  fi
else
  note "the auth-off branch failed: $(head -n 3 "$work/off.out" | tr '\n' ' ')"
fi

# --- on, then off, in the same directory ------------------------------------
render "$work/same" \
  EVIDGATE_REVIEW_TUNNEL_MODE=quick \
  EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer \
  EVIDGATE_REVIEW_BASIC_AUTH_HASH="$hash" >/dev/null 2>&1 ||
  note 'the first leg of the on-to-off transition failed'
grep -qx "$strip" "$work/same/proxy-auth.conf" ||
  note 'the first leg of the on-to-off transition wrote no strip'
render "$work/same" \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH=optional \
  >/dev/null 2>&1 ||
  note 'the second leg of the on-to-off transition failed'
if grep -q 'proxy_set_header' "$work/same/proxy-auth.conf" 2>/dev/null; then
  note 'a stale strip survived the on-to-off transition: the next container start would still empty Authorization'
fi

# --- credentials with optional auth: the browser may still send Basic -------
render "$work/optional" \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH=optional \
  EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer \
  EVIDGATE_REVIEW_BASIC_AUTH_HASH="$hash" >"$work/optional.out" 2>&1 ||
  note "optional auth with credentials failed: $(head -n 3 "$work/optional.out" | tr '\n' ' ')"
grep -qx "$strip" "$work/optional/proxy-auth.conf" ||
  note 'a configured realm does not empty Authorization, so the Basic credential reaches the Api'

# --- refusals: each one must exit non-zero and name the problem ------------
expect_refusal() { # LABEL MESSAGE NAME=VALUE...
  local label="$1" message="$2" out status
  shift 2
  out="$(render "$work/refusal" "$@" 2>&1)"
  status=$?
  if [ "$status" -eq 0 ]; then
    note "$label started successfully; it must refuse to serve"
    return 0
  fi
  case "$out" in
    *"$message"*) ;;
    *) note "$label refused for an unexpected reason: $(printf '%s' "$out" | head -n 3 | tr '\n' ' ')" ;;
  esac
}

expect_refusal 'quick mode without credentials' 'refusing to serve' \
  EVIDGATE_REVIEW_TUNNEL_MODE=quick
expect_refusal 'required auth without credentials' 'refusing to serve' \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH=required
expect_refusal 'a user without a hash' 'must be set together' \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer
# shellcheck disable=SC2016 # a bcrypt-shaped literal, which the script must reject
expect_refusal 'a hash that is not apr1' 'apr1 hash' \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer \
  EVIDGATE_REVIEW_BASIC_AUTH_HASH='$2y$a$b'
expect_refusal "a user containing ':'" "must not contain ':'" \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH_USER='a:b' \
  EVIDGATE_REVIEW_BASIC_AUTH_HASH="$hash"
expect_refusal 'an unknown EVIDGATE_REVIEW_BASIC_AUTH value' 'must be required or optional' \
  EVIDGATE_REVIEW_TUNNEL_MODE=token EVIDGATE_REVIEW_BASIC_AUTH=sometimes

if [ "$fail" -eq 0 ]; then
  printf '== ok: the start script renders the realm and empties Authorization only while basic auth is on\n'
fi
exit "$fail"
