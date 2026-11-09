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

review_step 'redirects are relative: no scheme, host or listen port in Location'
# Behind the tunnel the browser sees https on 443; a Location that names http://
# or :8080 sends it to a port nothing serves over TLS. Raw nc requests, because
# a client would follow the redirect.
# shellcheck disable=SC2016 # the container shell expands these, not this one
redirect_probe_script='set -e
mkdir -p /tmp/evidgate
printf "auth_basic off;\n" > /tmp/evidgate/auth.conf
printf "# auth off\n" > /tmp/evidgate/proxy-auth.conf
nginx
i=0
while [ "$i" -lt 40 ]; do
  if wget -qO- http://127.0.0.1:8080/nginx-health >/dev/null 2>&1; then break; fi
  i=$((i + 1)); sleep 0.25
done
for pair in "/ /vi/" "/vi /vi/" "/en /en/" "/runs?x=1 /vi/runs?x=1"; do
  set -- $pair
  loc=$(printf "GET %s HTTP/1.0\r\nHost: review.example.trycloudflare.com\r\n\r\n" "$1" | timeout 5 nc 127.0.0.1 8080 | tr -d "\r" | sed -n "s/^[Ll]ocation: //p")
  [ "$loc" = "$2" ] || { echo "FAIL: GET $1 redirected to [$loc], expected [$2]"; exit 1; }
done
loc=$(printf "GET /v1 HTTP/1.0\r\nHost: review.example.trycloudflare.com\r\n\r\n" | timeout 5 nc 127.0.0.1 8080 | tr -d "\r" | sed -n "s/^[Ll]ocation: //p")
case "$loc" in
  http:* | https:* | *:8080*) echo "FAIL: GET /v1 redirected to an absolute [$loc]"; exit 1 ;;
esac
echo "/, /vi, /en and an unprefixed path redirect with a relative Location"'
compose run --rm --no-deps --entrypoint /bin/sh web -c "$redirect_probe_script"

review_step 'hashed assets: only a successful body is long-lived, every error is no-store'
# A shared cache keeps an error that carries a year-long immutable lifetime, so
# the 401 of a lazy chunk once blanked the login page for every visitor. Raw nc
# requests against nginx in a one-shot container, in both auth modes.
if ! asset_hash="$(openssl passwd -apr1 sentinel 2>/dev/null)" || [ -z "$asset_hash" ]; then
  review_die 'openssl could not mint an apr1 hash, so the asset cache-control case cannot run'
fi
asset_basic="$(printf 'reviewer:sentinel' | openssl base64 -A)"
# shellcheck disable=SC2016 # the container shell expands these, not this one
asset_probe_script='set -e
mkdir -p /tmp/evidgate
if [ "$PROBE_AUTH" = on ]; then
  printf "auth_basic \"Evidgate review\";\nauth_basic_user_file /tmp/evidgate/htpasswd;\n" > /tmp/evidgate/auth.conf
  printf "reviewer:%s\n" "$PROBE_HASH" > /tmp/evidgate/htpasswd
  printf "proxy_set_header Authorization \"\";\n" > /tmp/evidgate/proxy-auth.conf
else
  printf "auth_basic off;\n" > /tmp/evidgate/auth.conf
  printf "# auth off\n" > /tmp/evidgate/proxy-auth.conf
fi
asset=$(cd /usr/share/nginx/html && ls vi/*.js | head -n 1)
[ -n "$asset" ] || { echo "FAIL: the vi bundle has no hashed .js file"; exit 1; }
nginx
i=0
while [ "$i" -lt 40 ]; do
  if wget -qO- http://127.0.0.1:8080/nginx-health >/dev/null 2>&1; then break; fi
  i=$((i + 1)); sleep 0.25
done
# get PATH EXPECTED_STATUS EXPECTED_CACHE_CONTROL [Basic credential] [extra header line]
get() {
  hdrs="Host: review.example.test"
  [ -z "${4:-}" ] || hdrs="$hdrs\r\nAuthorization: Basic $4"
  [ -z "${5:-}" ] || hdrs="$hdrs\r\n$5"
  resp=$(printf "GET /%s HTTP/1.0\r\n$hdrs\r\n\r\n" "$1" | timeout 5 nc 127.0.0.1 8080 | tr -d "\r")
  status=$(printf "%s\n" "$resp" | sed -n "1s/^HTTP\/[0-9.]* \([0-9]*\).*/\1/p")
  cc=$(printf "%s\n" "$resp" | sed -n "s/^[Cc]ache-[Cc]ontrol: //p")
  [ "$status" = "$2" ] && [ "$cc" = "$3" ] || { echo "FAIL: GET /$1 [$PROBE_AUTH ${5:-}] gave $status with Cache-Control [$cc], expected $2 with [$3] (one header, no duplicate)"; exit 1; }
  echo "GET /$1 [$PROBE_AUTH${4:+, credentials}${5:+, $5}] -> $status Cache-Control: $cc"
}
imm="max-age=31536000, immutable"
if [ "$PROBE_AUTH" = on ]; then
  get "$asset" 401 "no-store"
  get "vi/missing-ABCDEFGH.js" 401 "no-store"
  get "$asset" 200 "private, $imm" "$PROBE_BASIC"
  get "vi/missing-ABCDEFGH.js" 404 "no-store" "$PROBE_BASIC"
  get "$asset" 304 "private, $imm" "$PROBE_BASIC" "If-None-Match: *"
  get "$asset" 200 "private, $imm" "$PROBE_BASIC" "Range: bytes=999999999-"
else
  get "$asset" 200 "public, $imm"
  get "vi/missing-ABCDEFGH.js" 404 "no-store"
  get "$asset" 304 "public, $imm" "" "If-None-Match: *"
  get "$asset" 200 "public, $imm" "" "Range: bytes=999999999-"
  get "$asset" 200 "public, $imm" "" "Range: bytes=0-9"
fi'
for mode in off on; do
  compose run --rm --no-deps -e "PROBE_AUTH=$mode" -e "PROBE_HASH=$asset_hash" \
    -e "PROBE_BASIC=$asset_basic" --entrypoint /bin/sh web -c "$asset_probe_script"
done

review_step 'gzip carries Vary, and /v1/ is no-store unless the Api sends a Cache-Control'
# A compressed and a plain body share a URL, so a shared cache needs Vary. The Api
# proxy sends the upstream Cache-Control unchanged (exactly one header) and
# no-store when there is none, nginx's own 502 included. nginx runs from a copy of
# the shipped configuration whose upstream is a one-shot nc listener in the same
# container, so the real location block is what answers.
# shellcheck disable=SC2016 # the container shell expands these, not this one
api_probe_script='set -e
mkdir -p /tmp/evidgate
printf "auth_basic off;\n" > /tmp/evidgate/auth.conf
printf "# auth off\n" > /tmp/evidgate/proxy-auth.conf
grep -q "set \$evidgate_api http://api:8080;" /etc/nginx/nginx.conf || { echo "FAIL: the upstream line moved, so the probe cannot repoint it"; exit 1; }
sed "s#http://api:8080#http://127.0.0.1:9000#" /etc/nginx/nginx.conf > /tmp/nginx-probe.conf
nginx -c /tmp/nginx-probe.conf
i=0
while [ "$i" -lt 40 ]; do
  if wget -qO- http://127.0.0.1:8080/nginx-health >/dev/null 2>&1; then break; fi
  i=$((i + 1)); sleep 0.25
done
css=$(cd /usr/share/nginx/html && ls -S vi/*.css 2>/dev/null | head -n 1 || true)
[ -n "$css" ] || { echo "FAIL: the vi bundle has no .css file to compress"; exit 1; }
size=$(wc -c < "/usr/share/nginx/html/$css")
[ "$size" -ge 1024 ] || { echo "FAIL: $css is $size bytes, below gzip_min_length, so it cannot show Vary"; exit 1; }
for enc in "Accept-Encoding: gzip" "X-Probe: plain"; do
  hdrs=$(printf "GET /%s HTTP/1.1\r\nHost: review.example.test\r\nConnection: close\r\n%s\r\n\r\n" "$css" "$enc" | timeout 5 nc 127.0.0.1 8080 | tr -d "\r" | sed "/^$/q")
  printf "%s\n" "$hdrs" | grep -qix "Vary: Accept-Encoding" || { echo "FAIL: GET /$css [$enc] has no Vary: Accept-Encoding: $hdrs"; exit 1; }
  case "$enc" in
    *gzip) printf "%s\n" "$hdrs" | grep -qix "Content-Encoding: gzip" || { echo "FAIL: GET /$css was not gzip-encoded: $hdrs"; exit 1; } ;;
  esac
  echo "GET /$css [$enc] -> Vary: Accept-Encoding"
done
api() {
  # api UPSTREAM_HEADER_LINE EXPECTED_STATUS EXPECTED_CACHE_CONTROL [REQUEST_HEADER_LINE]
  if [ "$1" != none ]; then
    ( printf "HTTP/1.1 200 OK\r\n%bContent-Length: 2\r\nConnection: close\r\n\r\nok" "$1" | nc -l -s 127.0.0.1 -p 9000 >/dev/null 2>&1 ) &
    # Bound to IPv4 so it shows in /proc/net/tcp. Port 9000 is 0x2328; state 0A is LISTEN. Reading /proc does not connect,
    # so the one-shot listener is not consumed by the readiness check.
    w=0
    until grep -q ":2328 .* 0A " /proc/net/tcp; do
      w=$((w + 1)); [ "$w" -lt 50 ] || { echo "FAIL: the probe upstream never listened"; exit 1; }
      sleep 0.1
    done
  fi
  # The pause keeps nc from exiting on stdin EOF before the proxied answer
  # arrives; a static file is served fast enough not to need it.
  resp=$( { printf "GET /v1/probe HTTP/1.0\r\nHost: review.example.test\r\n%b\r\n" "${4:-}"; sleep 1; } | timeout 5 nc 127.0.0.1 8080 | tr -d "\r")
  status=$(printf "%s\n" "$resp" | sed -n "1s/^HTTP\/[0-9.]* \([0-9]*\).*/\1/p")
  cc=$(printf "%s\n" "$resp" | sed "/^$/q" | sed -n "s/^[Cc]ache-[Cc]ontrol: //p")
  [ "$status" = "$2" ] && [ "$cc" = "$3" ] || { echo "FAIL: GET /v1/probe [upstream: $1${4:+; request: $4}] gave $status with Cache-Control [$cc], expected $2 with [$3] (one header, no duplicate)"; exit 1; }
  echo "GET /v1/probe [upstream: $1${4:+; request: $4}] -> $status Cache-Control: $cc"
}
api "Cache-Control: private, max-age=60\r\n" 200 "private, max-age=60"
api "X-Other: 1\r\n" 200 "no-store"
api "Cache-Control: no-store\r\n" 200 "no-store"
api "Cache-Control:\r\nCache-Control:\r\n" 200 "no-store"
# A credentialed response never keeps a shared-cache directive; an anonymous
# one and every other upstream value pass verbatim.
api "Cache-Control: public, max-age=60\r\n" 200 "public, max-age=60"
api "Cache-Control: public, max-age=60\r\n" 200 "private" "Authorization: Bearer probe\r\n"
api "Cache-Control: max-age=60, s-maxage=120\r\n" 200 "private" "Cookie: a=b; __Host-evidgate_session=probe\r\n"
api "Cache-Control: PUBLIC\r\n" 200 "private" "Authorization: Bearer probe\r\n"
api "Cache-Control: private, max-age=60\r\n" 200 "private, max-age=60" "Authorization: Bearer probe\r\n"
api "Cache-Control: public, max-age=60\r\n" 200 "public, max-age=60" "Cookie: other=1\r\n"
api none 502 "no-store"
# nginx answers a stranger itself, before the Api is asked, and that 401 must
# not be storable either.
printf "auth_basic \"Evidgate review\";\nauth_basic_user_file /tmp/evidgate/htpasswd;\n" > /tmp/evidgate/auth.conf
printf "reviewer:x\n" > /tmp/evidgate/htpasswd
nginx -c /tmp/nginx-probe.conf -s reload
sleep 1
api none 401 "no-store"'
compose run --rm --no-deps --entrypoint /bin/sh web -c "$api_probe_script"

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
