#!/usr/bin/env bash
# Deploys the Pilot-0 review stack: the same images as the dev stack, on a host
# reached over ssh, behind a Cloudflare Tunnel so named reviewers can sign in.
# The release is shipped from one commit — `git archive` of exactly that sha into
# $HOME/evidgate-review/releases/<sha> — and `current` is a symlink pointing at
# the release that serves. Secrets are generated once into
# $HOME/evidgate-review/.env.review (mode 600) and never written again.
#
# usage: scripts/review/deploy.sh <ssh-host> [--sha <sha>] [--tunnel quick|token|none]
#                                 [--public-origin <https-url>] [--build-local]
#                                 [-i <ssh-keyfile>]
#                                 [--setup-swap] [--no-dogfood] [--dry-run]
#        scripts/review/deploy.sh --local [--sha <sha>] [--no-dogfood] [--dry-run]
#
#   <ssh-host>            the review host, reachable with the current ssh key.
#   --sha <sha>           ship this commit instead of HEAD (a rollback is the
#                         previous sha printed by the last deploy).
#   --tunnel quick        cloudflared's throwaway trycloudflare.com host: no
#                         account and no token, but a random URL that changes
#                         whenever cloudflared restarts.
#   --tunnel token        a named tunnel the owner created in the dashboard:
#                         CLOUDFLARE_TUNNEL_TOKEN must be exported in this shell.
#   --tunnel none         no ingress at all, which is the default without an origin.
#   --public-origin URL   https://host the token tunnel serves. First deploy only:
#                         the value lives in .env.review, which is never rewritten.
#   --build-local         build the images on this machine for the host's platform
#                         and ship them with docker save, for a host too small to
#                         build three images itself.
#   --setup-swap          add a 2 GiB swap file to the host when it has under 1 GiB.
#   --no-dogfood          skip the Pilot-0 dogfood run against the deployed API.
#   --dry-run             print the plan and run only the read-only preflight.
#   --local               run the same steps on this machine (no ssh), for the
#                         smoke test: loopback only, under a throwaway root.
#
# Every mode serves the web behind HTTP Basic Auth with the user and password
# minted into .env.review on the first deploy and never printed.
#
# The Cloudflare tunnel token is read from CLOUDFLARE_TUNNEL_TOKEN in this
# shell, never from a flag: argv is visible in ps. It is written into
# .env.review on the first deploy and never printed.
#
# dogfood needs a freshly minted dev ingest token, which the seed prints only on
# its first run. A redeploy against the same volumes has no token to print, so
# pass --no-dogfood there, or export the token from the first deploy.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/review/lib.sh
. "$SCRIPT_DIR/lib.sh"

DOGFOOD=1
ORIGIN_ARG=""
HOST_ARG=""
KEY_ARG=""
TUNNEL_ARG=""
TUNNEL_TOKEN="${CLOUDFLARE_TUNNEL_TOKEN:-}"

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
}

# The origin reaches the web CSP as an allowed connect-src, so it must be a bare
# https origin: a path or a query would silently not match a request.
normalize_origin() { # ORIGIN
  local origin="$1"
  [ -n "$origin" ] || return 0
  origin="${origin%/}"
  printf '%s' "$origin" | grep -Eq '^https://[A-Za-z0-9.-]+(:[0-9]+)?$' \
    || rv_die "--public-origin must be https://host[:port] with no path or query (got '$1')"
  printf '%s' "$origin"
}

parse_args() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --sha)
        [ $# -ge 2 ] || rv_die "--sha needs a commit sha"
        RV_RELEASE_SHA="$2"
        shift 2
        ;;
      --sha=*)
        RV_RELEASE_SHA="${1#*=}"
        shift
        ;;
      --public-origin)
        [ $# -ge 2 ] || rv_die "--public-origin needs an https URL"
        ORIGIN_ARG="$2"
        shift 2
        ;;
      --public-origin=*)
        ORIGIN_ARG="${1#*=}"
        shift
        ;;
      --tunnel)
        [ $# -ge 2 ] || rv_die "--tunnel needs quick, token or none"
        TUNNEL_ARG="$2"
        shift 2
        ;;
      --tunnel=*)
        TUNNEL_ARG="${1#*=}"
        shift
        ;;
      --build-local)
        RV_BUILD_LOCAL=1
        shift
        ;;
      -i)
        [ $# -ge 2 ] || rv_die "-i needs an ssh key file"
        KEY_ARG="$2"
        shift 2
        ;;
      -i?*)
        KEY_ARG="${1#-i}"
        shift
        ;;
      --setup-swap)
        # shellcheck disable=SC2034 # read by rv_report_memory in lib-host.sh
        RV_SETUP_SWAP=1
        shift
        ;;
      --no-dogfood)
        DOGFOOD=0
        shift
        ;;
      --dry-run)
        RV_DRY_RUN=1
        shift
        ;;
      --local)
        RV_MODE=local
        shift
        ;;
      -h | --help)
        usage
        exit 0
        ;;
      -*)
        rv_die "unknown option '$1' (try --help)"
        ;;
      *)
        [ -z "$HOST_ARG" ] || rv_die "expected one host, got '$HOST_ARG' and '$1'"
        HOST_ARG="$1"
        shift
        ;;
    esac
  done

  if [ "$RV_MODE" = local ]; then
    [ -z "$HOST_ARG" ] || rv_die "--local runs on this machine and takes no host (got '$HOST_ARG')"
  else
    [ -n "$HOST_ARG" ] || rv_die "the ssh host is required (or --local; try --help)"
    case "$HOST_ARG" in
      -* | *" "*) rv_die "invalid ssh host '$HOST_ARG'" ;;
    esac
    RV_SSH_HOST="$HOST_ARG"
  fi
  ORIGIN_ARG="$(normalize_origin "$ORIGIN_ARG")"

  # An origin implies a named tunnel, which keeps every earlier invocation
  # working; quick gets a random host from cloudflared and none publishes
  # nothing, so only one of the three flags can apply.
  if [ -n "$TUNNEL_ARG" ]; then
    RV_TUNNEL_MODE="$TUNNEL_ARG"
  elif [ -n "$ORIGIN_ARG" ]; then
    RV_TUNNEL_MODE="token"
  else
    RV_TUNNEL_MODE="none"
  fi
  rv_tunnel_mode_valid "$RV_TUNNEL_MODE" \
    || rv_die "--tunnel must be quick, token or none (got '$RV_TUNNEL_MODE')"
  if [ "$RV_TUNNEL_MODE" = quick ] && [ -n "$ORIGIN_ARG" ]; then
    rv_die "--tunnel quick gets its own random trycloudflare.com host, so it takes no --public-origin"
  fi
  if [ "$RV_MODE" = local ] && [ "$RV_TUNNEL_MODE" != none ]; then
    rv_die "--local runs the smoke on this machine and cannot publish a tunnel (--tunnel $RV_TUNNEL_MODE)"
  fi
  if [ "$RV_MODE" = local ] && [ "$RV_BUILD_LOCAL" = 1 ]; then
    rv_die "--build-local ships images to the review host; with --local the images are already built here"
  fi
}

# Prints the same command strings the real path executes; no secret can appear
# here because every secret travels over stdin into a file.
print_plan() {
  rv_info "dry run: nothing is created, built, started or written"
  rv_step "plan"
  printf '  1  preflight  %s: docker, compose v2, curl, arch, >=15 GiB free, %s writable\n' \
    "$(rv_target)" "$RV_HOST_HOME"
  printf '  2  resolve    release %s (tree already verified clean)\n' "$RV_RELEASE_SHA"
  printf '  3  secrets    %s: create from %s if absent, mode 600; kept if present\n' \
    "$RV_ENV_FILE" "$RV_TEMPLATE_REL"
  printf '  4  ship       git -C %s archive --format=tar %s | %s: tar -xf - -C %s\n' \
    "$RV_REPO_ROOT" "$RV_RELEASE_SHA" "$(rv_target)" "$RV_RELEASE_DIR"
  printf '  5  images     cd %s && bash infra/review/check.sh --static-only\n' "$RV_RELEASE_DIR"
  if [ "$RV_BUILD_LOCAL" = 1 ]; then
    printf '                 docker buildx build --platform linux/%s --load -t evidgate-review-<svc>:%s (from a git archive of the sha)\n' \
      "$RV_HOST_ARCH" "$RV_IMAGE_TAG"
    printf '                 docker save %s | gzip | %s: gunzip | docker load\n' \
      "$(for svc in migrator api worker web seed; do printf 'evidgate-review-%s:%s ' "$svc" "$RV_IMAGE_TAG"; done)" "$(rv_target)"
  else
    # Two calls, in this order: the seed image is the migrator image plus
    # contracts/schemas, so it cannot be built before its base exists.
    printf '                 %s\n' "$(rv_compose_argv build migrator api worker web)"
    printf '                 %s\n' "$(rv_compose_argv build seed)"
  fi
  printf '                 cd %s && bash infra/review/check-web-image.sh --env-file %s\n' \
    "$RV_RELEASE_DIR" "$RV_ENV_FILE"
  printf '  6  state      %s\n' "$(rv_compose_argv up -d --wait postgres seaweedfs)"
  printf '  7  schema     %s\n' "$(rv_compose_argv run --rm -T db-init bootstrap)"
  printf '     %s\n' "$(rv_compose_argv run --rm -T migrator)"
  printf '     %s\n' "$(rv_compose_argv run --rm -T db-init grants)"
  printf '  8  storage    %s\n' "$(rv_compose_argv run --rm -T bucket-init)"
  printf '  9  seed       %s\n' "$(rv_compose_argv run --rm -T seed seed-dev --with-token)"
  printf '                 (the EVIDGATE_DEV_TOKEN line is kept in a shell variable only)\n'
  if [ "$RV_MODE" = local ]; then
    printf ' 10  serve      %s\n' "$(rv_compose_argv up -d --wait api worker web-local)"
  else
    printf ' 10  serve      %s\n' "$(rv_compose_argv up -d --wait api worker web)"
  fi
  printf '                 %s\n' "$(rv_compose_argv run --rm -T --no-deps probe)"
  printf '                 %s\n' "$(rv_compose_argv run --rm -T --no-deps probe -fsS --retry 60 --retry-delay 2 --retry-all-errors http://worker:8081/internal/health/ready)"
  if [ "$DOGFOOD" = 1 ]; then
    printf ' 11  dogfood    %s\n' "$(rv_compose_argv --profile debug up -d api-debug)"
    if [ "$RV_MODE" != local ]; then
      printf '                 then ssh -L 127.0.0.1:%s:%s and scripts/dev/pilot0-dogfood.sh\n' \
        "$RV_API_PORT" "$RV_API_PORT"
    else
      printf '                 then scripts/dev/pilot0-dogfood.sh against http://127.0.0.1:%s\n' "$RV_API_PORT"
    fi
    printf '                 %s\n' "$(rv_compose_argv --profile debug rm -sf api-debug)"
  else
    printf ' 11  dogfood    skipped (--no-dogfood)\n'
  fi
  case "$RV_TUNNEL_MODE" in
    token)
      printf ' 12  tunnel     %s\n' "$(rv_compose_argv --profile tunnel up -d --wait cloudflared)"
      printf '                 then no container may publish a host port'
      [ -z "$RV_PUBLIC_ORIGIN" ] || printf ', and %s/ must answer' "$RV_PUBLIC_ORIGIN"
      printf '\n'
      ;;
    quick)
      printf ' 12  tunnel     %s\n' "$(rv_compose_argv --profile tunnel-quick up -d cloudflared-quick)"
      printf '                 then no container may publish a host port, and the trycloudflare.com URL is read from its log\n'
      ;;
    none)
      printf ' 12  tunnel     none (nothing is published; --tunnel quick gives a temporary public URL)\n'
      ;;
  esac
  printf ' 13  current    %s -> releases/%s\n' "$RV_HOST_ROOT/current" "$RV_RELEASE_SHA"
}

# The seed prints its inputs only while it creates them; a redeploy against the
# same volumes prints neither the token nor the ids.
seed_field() { # OUTPUT NAME
  printf '%s\n' "$1" | awk -v key="$2=" '
    { for (i = 1; i <= NF; i++) if (index($i, key) == 1) { print substr($i, length(key) + 1); exit } }'
}

run_dogfood() { # TOKEN SUITE CASE_REV ENV_REV
  local token="$1" suite="$2" case_rev="$3" env_rev="$4" case_id api_url
  [ "$DOGFOOD" = 1 ] || { rv_info "dogfood skipped (--no-dogfood)"; return 0; }

  if [ -z "$token" ]; then
    if [ -n "${EVIDGATE_DEV_TOKEN:-}" ]; then
      token="$EVIDGATE_DEV_TOKEN"
      rv_info "using EVIDGATE_DEV_TOKEN from this shell (the seed minted none)"
    else
      rv_die "dogfood needs a dev ingest token and the seed printed none: a live token exists and its secret cannot be shown again. Export the token from the first deploy, or pass --no-dogfood to deploy without the dogfood run."
    fi
  fi
  if [ -z "$suite" ] || [ -z "$case_rev" ] || [ -z "$env_rev" ]; then
    rv_die "dogfood needs PILOT0_SUITE_REVISION_ID, PILOT0_CASE_REVISION_ID and PILOT0_ENVIRONMENT_REVISION_ID, and the seed printed none (it seeds only once). Read them from the first deploy's output, or pass --no-dogfood."
  fi

  # The dev token cannot read the catalog and the dev user has no login, so the
  # one seeded case comes straight out of the throwaway review database.
  case_id="$(rv_compose_read exec -T postgres psql -U postgres -d evidgate -At -c 'select id from test_case limit 1')"
  [ -n "$case_id" ] || rv_die "the review database has no test_case row; the dev seed did not run"

  # api-debug is the Api with a loopback port: the tunnel profile publishes
  # nothing, so the dogfood gets its own door and the door is removed after.
  rv_compose --profile debug up -d api-debug
  rv_open_forward
  api_url="http://127.0.0.1:$RV_API_PORT"
  rv_wait_http "$api_url/internal/health/ready" 240 200 "the api-debug readiness endpoint"

  rv_info "dogfood: scripts/dev/pilot0-dogfood.sh against $api_url"
  if ! (
    cd "$RV_REPO_ROOT"
    EVIDGATE_API_URL="$api_url" \
      EVIDGATE_DEV_TOKEN="$token" \
      PILOT0_SUITE_REVISION_ID="$suite" \
      PILOT0_CASE_REVISION_ID="$case_rev" \
      PILOT0_ENVIRONMENT_REVISION_ID="$env_rev" \
      PILOT0_CASE_ID="$case_id" \
      bash scripts/dev/pilot0-dogfood.sh
  ); then
    rv_die "the dogfood run failed; the stack stays up at $RV_RELEASE_SHA, and api-debug stays published on 127.0.0.1:$RV_API_PORT for debugging"
  fi
  rv_compose --profile debug rm -sf api-debug
  rv_close_forward
}

main() {
  local env_state seed_out dev_token suite case_rev env_rev previous public_url=""

  parse_args "$@"
  trap rv_cleanup EXIT
  rv_resolve_ssh_key "$KEY_ARG"

  rv_step "1/13 preflight"
  rv_preflight
  rv_step "2/13 resolve the release"
  rv_resolve_sha
  # Shipped images carry the short sha: it is what the loaded tags read and what
  # the host's compose resolves, so the per-call tag has to be the short one.
  if [ "$RV_BUILD_LOCAL" = 1 ]; then
    RV_IMAGE_TAG="${RV_RELEASE_SHA:0:12}"
  fi
  rv_init_paths
  rv_info "release $RV_RELEASE_SHA from $RV_REPO_ROOT"
  if [ "$RV_MODE" = local ]; then
    rv_info "local smoke root: $RV_HOST_ROOT (delete it when the smoke is done)"
  fi

  if [ "$RV_DRY_RUN" = 1 ]; then
    # The plan must name the tunnel step exactly when the real run would take
    # it, so the read-only env read happens here too.
    [ -z "$ORIGIN_ARG" ] || RV_PUBLIC_ORIGIN="$ORIGIN_ARG"
    rv_refresh_ports
    print_plan
    exit 0
  fi

  rv_step "3/13 secrets: $RV_ENV_FILE"
  # Refuse before the file exists: a first deploy that wrote .env.review without
  # the token would leave a file that is never rewritten and can never tunnel.
  if [ "$RV_TUNNEL_MODE" = token ] && [ -z "$TUNNEL_TOKEN" ] \
    && ! rv_run "test -e $(rv_quote "$RV_ENV_FILE")"; then
    rv_die "--tunnel token on a first deploy needs CLOUDFLARE_TUNNEL_TOKEN in this shell: the token is written into .env.review (never argv), and that file is created once"
  fi
  env_state=0
  rv_env_ensure "$RV_RELEASE_SHA" "$ORIGIN_ARG" "$TUNNEL_TOKEN" || env_state=$?
  rv_refresh_ports
  if [ "$RV_TUNNEL_MODE" = token ]; then
    [ "$(rv_env_get CLOUDFLARE_TUNNEL_TOKEN)" != "" ] \
      || rv_die "$RV_ENV_FILE has no CLOUDFLARE_TUNNEL_TOKEN; export CLOUDFLARE_TUNNEL_TOKEN in this shell and delete that file on the host (first deploy only), or deploy with --tunnel quick"
    if [ "$env_state" = 1 ] && [ -n "$ORIGIN_ARG" ]; then
      rv_warn "$RV_ENV_FILE already existed: --public-origin only takes effect on the first deploy"
    fi
  elif [ "$RV_TUNNEL_MODE" = quick ]; then
    # A public URL with no door at all is the one state this deploy must never
    # reach, so the hash is checked before anything is built.
    rv_require_auth_hash quick
    if [ "$env_state" = 1 ]; then
      rv_warn "$RV_ENV_FILE already existed: its Basic Auth keys are the ones quick mode will use"
    fi
  elif [ "$RV_MODE" != local ]; then
    rv_warn "no tunnel: the stack runs but reviewers cannot reach it (--tunnel quick gives a temporary public URL)"
  fi

  rv_step "4/13 ship release"
  rv_ship_release "$RV_RELEASE_SHA"

  # Static first: a drifting pin or lifecycle rule stops the deploy before a
  # single layer is built. The image check needs the image, so it follows build.
  rv_step "5/13 infra checks and images"
  if [ "$RV_BUILD_LOCAL" = 1 ]; then
    # The host only loads what was built here: it runs no gate, so it needs
    # neither node nor python3. rv_build_local_images runs the same static gate on
    # the extracted sha before the first build.
    :
  else
    rv_mutate "cd $(rv_quote "$RV_RELEASE_DIR") && bash infra/review/check.sh --static-only"
  fi
  if [ "$RV_BUILD_LOCAL" = 1 ]; then
    # The host only loads what was built here, so it never needs the build
    # headroom that a 4 GiB VM does not have.
    rv_build_local_images
    rv_ship_images
  else
    # Two calls, in this order: the seed image derives from the migrator image,
    # and compose does not order one service's build after another's.
    rv_compose build migrator api worker web
    rv_compose build seed
  fi
  # The gate resolves the web image from compose, and the shell value wins over
  # the one stored at first deploy: without this it would inspect the previous
  # release's image, or nothing at all under --build-local's short tag.
  rv_mutate "cd $(rv_quote "$RV_RELEASE_DIR") && EVIDGATE_REVIEW_TAG=$(rv_quote "$RV_IMAGE_TAG") bash infra/review/check-web-image.sh --env-file $(rv_quote "$RV_ENV_FILE")"

  rv_step "6/13 database and object store"
  rv_compose up -d --wait postgres seaweedfs

  rv_step "7/13 schema: roles, migrations, grants"
  rv_compose run --rm -T db-init bootstrap
  rv_compose run --rm -T migrator
  rv_compose run --rm -T db-init grants

  rv_step "8/13 bucket and lifecycle"
  rv_compose run --rm -T bucket-init

  rv_step "9/13 seed"
  seed_out="$(rv_compose run --rm -T seed seed-dev --with-token)"
  # The one secret line is dropped before anything reaches the terminal.
  printf '%s\n' "$seed_out" | sed -e '/^EVIDGATE_DEV_TOKEN=/d' -e '/^[[:space:]]*$/d'
  dev_token="$(seed_field "$seed_out" EVIDGATE_DEV_TOKEN)"
  suite="$(seed_field "$seed_out" suite_revision_id)"
  case_rev="$(seed_field "$seed_out" case_revision_id)"
  env_rev="$(seed_field "$seed_out" environment_revision_id)"

  rv_step "10/13 api, worker, web"
  if [ "$RV_MODE" = local ]; then
    # web-local is web plus the loopback publish; naming services explicitly
    # keeps the profile-less web out, so there is no duplicate nginx.
    rv_compose up -d --wait api worker web-local
  else
    rv_compose up -d --wait api worker web
  fi
  # The api and worker images are chiselled: no shell, no HTTP client, so they
  # cannot carry a healthcheck and --wait only proves they are running.
  rv_compose run --rm -T --no-deps probe
  rv_compose run --rm -T --no-deps probe -fsS --retry 60 --retry-delay 2 \
    --retry-all-errors http://worker:8081/internal/health/ready

  rv_step "11/13 dogfood"
  run_dogfood "$dev_token" "$suite" "$case_rev" "$env_rev"

  rv_step "12/13 tunnel"
  case "$RV_TUNNEL_MODE" in
    token)
      rv_compose --profile tunnel up -d --wait cloudflared
      rv_assert_no_host_ports
      if [ -n "$RV_PUBLIC_ORIGIN" ]; then
        # 401 is a pass: Basic Auth answers before anything else does.
        rv_wait_http "$RV_PUBLIC_ORIGIN/" 180 "200 301 302 303 307 308 401" "the public origin"
      else
        rv_info "no --public-origin recorded, so the tunnel's hostname is whatever the Cloudflare dashboard serves"
      fi
      ;;
    quick)
      rv_compose --profile tunnel-quick up -d cloudflared-quick
      rv_assert_no_host_ports
      public_url="$(rv_quick_tunnel_url 180)"
      rv_wait_http "$public_url/" 120 "200 301 302 303 307 308 401" "the quick tunnel"
      ;;
    none)
      rv_info "no tunnel: nothing is published beyond the loopback binds of this profile"
      ;;
  esac

  rv_step "13/13 current"
  previous="$(rv_point_current "$RV_RELEASE_SHA")"

  printf '\n'
  rv_info "deployed $RV_RELEASE_SHA"
  case "$RV_TUNNEL_MODE" in
    token)
      if [ -n "$RV_PUBLIC_ORIGIN" ]; then
        rv_info "public URL: $RV_PUBLIC_ORIGIN/"
      fi
      ;;
    quick)
      rv_info "public URL: $public_url/"
      rv_warn "a quick tunnel host changes on every restart of cloudflared-quick; --tunnel token with your own hostname is the stable one"
      ;;
  esac
  if [ "$RV_MODE" = local ]; then
    rv_info "local URL: http://127.0.0.1:$RV_WEB_PORT/"
  elif [ "$RV_TUNNEL_MODE" = none ]; then
    rv_info "no public URL: deploy again with --tunnel quick for a temporary one, or --tunnel token with a Cloudflare token"
  fi
  rv_info "Basic Auth user '$RV_AUTH_USER', and user/password are in $RV_ENV_FILE (mode 600, never printed)"
  if [ -n "$previous" ]; then
    rv_info "previous release: $previous — roll back with: $0 ${HOST_ARG:-<ssh-host>} --sha ${previous#releases/}"
  fi
}

main "$@"
