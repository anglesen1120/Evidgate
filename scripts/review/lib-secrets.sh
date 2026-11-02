#!/usr/bin/env bash
# Secrets the deployed stack reads, minted once into .env.review. Sourced by
# lib.sh; never executed.
#
# The web answers HTTP Basic Auth on every route, so what the container needs is
# the apr1 hash (the format htpasswd writes), never the plaintext: the plaintext
# exists only so the owner can tell a reviewer what to type.

# -stdin keeps the password out of argv, and therefore out of ps and the shell
# history of the host.
# shellcheck disable=SC2016 # the apr1 prefix is a literal case pattern
rv_basic_auth_hash() { # PASSWORD
  local hash
  hash="$(printf '%s\n' "$1" | openssl passwd -apr1 -stdin 2>/dev/null)" \
    || rv_die "cannot mint the Basic Auth hash: 'openssl passwd -apr1' is required on this machine"
  case "$hash" in
    '$apr1$'*) printf '%s' "$hash" ;;
    *) rv_die "openssl printed no apr1 hash for the Basic Auth password (got ${#hash} characters)" ;;
  esac
}

# A stack without a hash would serve the review build with no door at all, so
# quick mode refuses to start one. The value is compared, never printed.
rv_require_auth_hash() { # MODE
  [ "$1" = quick ] || return 0
  [ -n "$(rv_env_get EVIDGATE_REVIEW_BASIC_AUTH_HASH)" ] && return 0
  rv_die "$RV_ENV_FILE has no EVIDGATE_REVIEW_BASIC_AUTH_HASH, and quick mode serves the build on a public URL. The file is never rewritten, so add the three keys by hand with: printf '%s\\n' \"EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer\" \"EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD=<pick one>\" \"EVIDGATE_REVIEW_BASIC_AUTH_HASH='\$(printf '%s\\n' '<same password>' | openssl passwd -apr1 -stdin)'\" >> $RV_ENV_FILE"
}
