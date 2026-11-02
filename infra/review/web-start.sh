#!/bin/sh
# Container start for the web image: materialise the basic-auth files, then hand
# over to nginx.
#
# nginx needs its realm and password file fixed when the configuration is parsed,
# and this image runs with a read-only rootfs, so the files are written here
# under /tmp (the tmpfs compose mounts) and the configuration always includes
# them: the file says `auth_basic off;` when the deployment has no credentials.
#
# The second file carries the proxy header that must exist only while basic auth
# is on; nginx cannot make a directive conditional, so the choice is written here.
#
# The plaintext password never reaches this container. Compose passes only the
# user and an apr1 hash; the password stays in .env.review for the owner.
set -eu

# nginx.conf includes these two files by their fixed path, so the container uses
# /tmp; the override lets a test render them outside a container.
auth_dir="${EVIDGATE_WEB_START_AUTH_DIR:-/tmp/evidgate}"
auth_conf="$auth_dir/auth.conf"
proxy_conf="$auth_dir/proxy-auth.conf"
htpasswd="$auth_dir/htpasswd"

die() {
  echo "evidgate-web: $*" >&2
  exit 1
}

mode=${EVIDGATE_REVIEW_TUNNEL_MODE:-token}
auth=${EVIDGATE_REVIEW_BASIC_AUTH:-optional}
user=${EVIDGATE_REVIEW_BASIC_AUTH_USER:-}
hash=${EVIDGATE_REVIEW_BASIC_AUTH_HASH:-}

# A quick tunnel is a public, crawlable URL, so it is never served without
# credentials whatever the environment says. compose.quick.yml sets `required`
# as well; this is the second, independent guard.
if [ "$mode" = quick ]; then
  auth=required
fi

mkdir -p "$auth_dir"

if [ -n "$user" ] && [ -n "$hash" ]; then
  case "$user" in
    *:*) die "EVIDGATE_REVIEW_BASIC_AUTH_USER must not contain ':'" ;;
  esac
  if printf '%s%s' "$user" "$hash" | grep -q '[[:space:]]'; then
    die 'EVIDGATE_REVIEW_BASIC_AUTH_USER/_HASH must not contain whitespace'
  fi
  # nginx implements $apr1$ itself; bcrypt ($2y$) needs a libc crypt() that this
  # alpine image does not have, so a bcrypt hash would fail every login silently.
  # shellcheck disable=SC2016 # "$apr1$salt$digest" is literal text, not expansion
  printf '%s' "$hash" | grep -Eq '^\$apr1\$[^$]+\$[^$]+$' ||
    die 'EVIDGATE_REVIEW_BASIC_AUTH_HASH must be an apr1 hash ($apr1$salt$digest), e.g. openssl passwd -apr1'
  printf '%s:%s\n' "$user" "$hash" >"$htpasswd"
  chmod 600 "$htpasswd"
  printf 'auth_basic "Evidgate review";\nauth_basic_user_file %s;\n' "$htpasswd" >"$auth_conf"
  # The Basic credential is for this hop only. Forwarded unchanged, the Api would
  # read it as a bearer token, so the header is emptied for /v1/ requests; the
  # session cookie is what authenticates them. A Bearer client (CLI/CI) is refused
  # by this realm before the strip matters, so a Basic-protected edge serves
  # browsers only.
  printf 'proxy_set_header Authorization "";\n' >"$proxy_conf"
elif [ -n "$user" ] || [ -n "$hash" ]; then
  die 'EVIDGATE_REVIEW_BASIC_AUTH_USER and EVIDGATE_REVIEW_BASIC_AUTH_HASH must be set together'
elif [ "$auth" = required ]; then
  die "basic auth is required (EVIDGATE_REVIEW_TUNNEL_MODE=$mode) but EVIDGATE_REVIEW_BASIC_AUTH_USER/_HASH are empty; refusing to serve"
elif [ "$auth" = optional ]; then
  printf 'auth_basic off;\n' >"$auth_conf"
  # No credential belongs to this hop, so the client's Authorization header is
  # the Api's bearer token and has to reach it; the include stays inert.
  printf '# basic auth is off: Authorization passes through to the Api.\n' >"$proxy_conf"
else
  die "EVIDGATE_REVIEW_BASIC_AUTH must be required or optional, not '$auth'"
fi

if [ "${EVIDGATE_WEB_START_CHECK:-}" = 1 ]; then
  # What the image gate runs instead of starting nginx: prove the files are
  # written and which mode won, without binding a port.
  printf 'evidgate-web: %s\n' "$(grep -h '^auth_basic' "$auth_conf")"
  printf 'evidgate-web: %s\n' "$(grep -h '^proxy_set_header' "$proxy_conf" || printf 'Authorization passes through')"
  exit 0
fi

exec nginx -g 'daemon off;'
