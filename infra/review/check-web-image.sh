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
# The configuration always includes /tmp/evidgate/auth.conf, which the container
# start script writes; `run` bypasses that script, so the gate writes both shapes
# of the file itself. A missing include would otherwise fail the parse here.
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; mkdir -p /tmp/evidgate; printf "auth_basic off;\n" > /tmp/evidgate/auth.conf; nginx -t'

review_step "nginx -t inside $tag (realm enabled)"
# shellcheck disable=SC2016 # the $apr1$ text is a credential literal, not expansion
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; mkdir -p /tmp/evidgate; printf "auth_basic \"Evidgate review\";\nauth_basic_user_file /tmp/evidgate/htpasswd;\n" > /tmp/evidgate/auth.conf; printf "reviewer:\$apr1\$c2FsdA\$ZHVtbXk\n" > /tmp/evidgate/htpasswd; nginx -t'

review_step 'both locale bundles exist'
# shellcheck disable=SC2016 # $locale must reach the container unexpanded
compose run --rm --no-deps --entrypoint /bin/sh web -c \
  'set -e; for locale in en vi; do test -f "/usr/share/nginx/html/$locale/index.html"; done; echo "en/ and vi/ index.html present"'

review_step 'basic auth: quick mode without credentials refuses to start'
if compose run --rm --no-deps -e EVIDGATE_WEB_START_CHECK=1 \
  -e EVIDGATE_REVIEW_TUNNEL_MODE=quick \
  --entrypoint /bin/sh web -c 'exec /bin/sh /usr/local/bin/evidgate-web-start' \
  >"$work/quick.out" 2>&1; then
  review_die 'the web image started in quick mode with no credentials: a public quick tunnel would serve without auth'
fi
grep -q 'refusing to serve' "$work/quick.out" ||
  review_die "quick mode failed for an unexpected reason: $(head -n 3 "$work/quick.out" | tr '\n' ' ')"

# A real apr1 hash, because the start script rejects every other format on
# purpose: nginx in this image has no bcrypt crypt().
hash="$(openssl passwd -apr1 sentinel 2>/dev/null || true)"
if [ -z "$hash" ]; then
  printf 'skip: openssl is unavailable, so the apr1 enforcement case cannot be minted here\n'
else
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

  review_step 'basic auth: token mode without credentials turns the realm off'
  compose run --rm --no-deps -e EVIDGATE_WEB_START_CHECK=1 \
    -e EVIDGATE_REVIEW_TUNNEL_MODE=token \
    --entrypoint /bin/sh web -c 'exec /bin/sh /usr/local/bin/evidgate-web-start' \
    >"$work/off.out" 2>&1 ||
    review_die "the web image failed without credentials in token mode: $(head -n 3 "$work/off.out" | tr '\n' ' ')"
  grep -q 'auth_basic off' "$work/off.out" ||
    review_die "auth was not disabled in token mode: $(head -n 3 "$work/off.out" | tr '\n' ' ')"
fi

review_step 'ok: the web image parses its configuration and serves both locales'
