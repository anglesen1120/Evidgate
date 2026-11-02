#!/usr/bin/env bash
# Post-build gate for the web image: the nginx configuration parses and the
# bundle carries both locales.
#
# Run it right after `docker compose build web` and before `up`: a syntax error in
# nginx.conf only shows up when the container starts, and a build that localized
# one locale would serve 404s for the other without failing the build.
#
#   infra/review/check-web-image.sh [--env-file PATH]
#
# PATH defaults to a throwaway sentinel environment, which resolves the image to a
# tag nothing ever builds; a deploy therefore passes the real .env.review so the
# gate inspects and runs the image it just built under the release sha.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=infra/review/lib-check.sh
. "$SCRIPT_DIR/lib-check.sh"

review_require docker "$(basename "$0")"
# The credential cases are minted with openssl, and a minted hash is what proves
# the realm path end to end; skipping them on a host without openssl would leave
# the only case that enables basic auth untested.
review_require openssl "$(basename "$0")"

env_file=""
while [ $# -gt 0 ]; do
  case "$1" in
    --env-file)
      [ $# -ge 2 ] || review_die '--env-file needs a path'
      env_file="$2"
      shift 2
      ;;
    -h | --help)
      printf 'usage: %s [--env-file PATH]\n' "$(basename "$0")"
      exit 0
      ;;
    *) review_die "unexpected argument '$1' (try --help)" ;;
  esac
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "$env_file" ]; then
  [ -f "$env_file" ] || review_die "no such environment file: $env_file"
  review_step "using the deploy environment $env_file"
else
  env_file="$work/env.sentinel"
  review_write_sentinel_env "$env_file"
fi

compose() {
  docker compose --project-name "$REVIEW_PROJECT" --env-file "$env_file" \
    -f "$REVIEW_COMPOSE" "$@"
}

# The image name is asked of compose itself, so the gate names the tag a deploy
# will run rather than a copy of the tag expression kept here. `config --images`
# prints image names only: no other value of the environment can reach this
# output, which is why it is the one `config` form allowed in a log.
review_step 'resolving the built web image'
tag="$(compose config --images | grep -E '^evidgate-review-web:' | head -n 1 || true)"
[ -n "$tag" ] || review_die 'compose does not name a web image; the service or its image line changed'
docker image inspect "$tag" >/dev/null 2>&1 ||
  review_die "no image $tag on this host: build the web image before running this gate"

review_step "nginx -t inside $tag (auth off)"
# The configuration always includes /tmp/evidgate/auth.conf and, inside /v1/,
# proxy-auth.conf; the container start script writes both, and `run` bypasses that
# script, so the gate writes both shapes of both files itself. A missing include
# would otherwise fail the parse here.
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; mkdir -p /tmp/evidgate; printf "auth_basic off;\n" > /tmp/evidgate/auth.conf; printf "# auth off\n" > /tmp/evidgate/proxy-auth.conf; nginx -t'

review_step "nginx -t inside $tag (realm enabled)"
# shellcheck disable=SC2016 # the $apr1$ text is a credential literal, not expansion
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; mkdir -p /tmp/evidgate; printf "auth_basic \"Evidgate review\";\nauth_basic_user_file /tmp/evidgate/htpasswd;\n" > /tmp/evidgate/auth.conf; printf "proxy_set_header Authorization \"\";\n" > /tmp/evidgate/proxy-auth.conf; printf "reviewer:\$apr1\$c2FsdA\$ZHVtbXk\n" > /tmp/evidgate/htpasswd; nginx -t'

review_step 'the health probe answers loopback only'
# The rewrite-phase guard is the only thing keeping /nginx-health off the tunnel,
# so it is exercised instead of trusted: nginx runs in a one-shot container, is
# asked from loopback (200 ok) and from the container's own bridge address, which
# is the source an off-host request presents (must be 403).
# shellcheck disable=SC2016 # the container shell expands these, not this one
health_probe_script='set -e
mkdir -p /tmp/evidgate
printf "auth_basic off;\n" > /tmp/evidgate/auth.conf
printf "# auth off\n" > /tmp/evidgate/proxy-auth.conf
nginx
i=0
while [ "$i" -lt 40 ]; do
  if wget -qO- http://127.0.0.1:8080/nginx-health > /tmp/evidgate/health 2>/dev/null; then break; fi
  i=$((i + 1)); sleep 0.25
done
grep -qx ok /tmp/evidgate/health || { echo "FAIL: the loopback probe did not answer ok"; exit 1; }
ip=$(hostname -i 2>/dev/null | tr " " "\n" | grep -E "^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$" | head -1)
[ -n "$ip" ] || { echo "FAIL: no IPv4 address to test the refusal with"; exit 1; }
out=$(wget -qO- -S "http://$ip:8080/nginx-health" 2>&1 || true)
case "$out" in
  *"HTTP/1.1 403"*) echo "loopback 200 ok, bridge address $ip refused (403)" ;;
  *) echo "FAIL: /nginx-health answered a non-loopback source $ip: $out"; exit 1 ;;
esac'
compose run --rm --no-deps --entrypoint /bin/sh web -c "$health_probe_script"

review_step 'both locale bundles exist'
# shellcheck disable=SC2016 # $locale must reach the container unexpanded
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; for locale in en vi; do test -f "/usr/share/nginx/html/$locale/index.html"; done; echo "en/ and vi/ index.html present"'

review_step 'basic auth: quick mode without credentials refuses to start'
if compose run --rm --no-deps -e EVIDGATE_WEB_START_CHECK=1 \
  -e EVIDGATE_REVIEW_TUNNEL_MODE=quick \
  -e EVIDGATE_REVIEW_BASIC_AUTH_USER= -e EVIDGATE_REVIEW_BASIC_AUTH_HASH= \
  --entrypoint /bin/sh web -c 'exec /bin/sh /usr/local/bin/evidgate-web-start' \
  >"$work/quick.out" 2>&1; then
  review_die 'the web image started in quick mode with no credentials: a public quick tunnel would serve without auth'
fi
grep -q 'refusing to serve' "$work/quick.out" ||
  review_die "quick mode failed for an unexpected reason: $(head -n 3 "$work/quick.out" | tr '\n' ' ')"

# A real apr1 hash, because the start script rejects every other format on
# purpose: nginx in this image has no bcrypt crypt().
if ! hash="$(openssl passwd -apr1 sentinel 2>/dev/null)"; then
  review_die 'openssl could not mint an apr1 hash, so the realm case cannot run; install a working openssl'
fi
[ -n "$hash" ] || review_die 'openssl produced an empty apr1 hash, so the realm case cannot run'

review_step 'basic auth: quick mode with credentials enables the realm'
compose run --rm --no-deps -e EVIDGATE_WEB_START_CHECK=1 \
  -e EVIDGATE_REVIEW_TUNNEL_MODE=quick \
  -e EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer \
  -e "EVIDGATE_REVIEW_BASIC_AUTH_HASH=$hash" \
  --entrypoint /bin/sh web -c 'exec /bin/sh /usr/local/bin/evidgate-web-start' \
  >"$work/on.out" 2>&1 ||
  review_die "the web image rejected valid apr1 credentials: $(head -n 3 "$work/on.out" | tr '\n' ' ')"
grep -q 'auth_basic "Evidgate review"' "$work/on.out" ||
  review_die "the image did not write an auth realm: $(head -n 3 "$work/on.out" | tr '\n' ' ')"
# The Basic credential authenticated the reviewer to nginx; the Api must not
# receive it as a bearer token.
grep -q 'proxy_set_header Authorization "";' "$work/on.out" ||
  review_die "the realm is on but the proxy still forwards the Basic credential: $(head -n 3 "$work/on.out" | tr '\n' ' ')"

review_step 'basic auth: token mode without credentials turns the realm off'
compose run --rm --no-deps -e EVIDGATE_WEB_START_CHECK=1 \
  -e EVIDGATE_REVIEW_TUNNEL_MODE=token -e EVIDGATE_REVIEW_BASIC_AUTH=optional \
  -e EVIDGATE_REVIEW_BASIC_AUTH_USER= -e EVIDGATE_REVIEW_BASIC_AUTH_HASH= \
  --entrypoint /bin/sh web -c 'exec /bin/sh /usr/local/bin/evidgate-web-start' \
  >"$work/off.out" 2>&1 ||
  review_die "the web image failed without credentials in token mode: $(head -n 3 "$work/off.out" | tr '\n' ' ')"
grep -q 'auth_basic off' "$work/off.out" ||
  review_die "auth was not disabled in token mode: $(head -n 3 "$work/off.out" | tr '\n' ' ')"
# With no Basic auth there is no hop credential to remove: the header is the
# client's own bearer token and stripping it would break every Api call.
grep -q 'Authorization passes through' "$work/off.out" ||
  review_die "auth is off but the proxy empties Authorization anyway: $(head -n 3 "$work/off.out" | tr '\n' ' ')"

review_step 'ok: the web image parses its configuration and serves both locales'
