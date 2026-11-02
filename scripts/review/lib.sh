#!/usr/bin/env bash
# Shared helpers for the review-deployment scripts (deploy.sh, status.sh,
# down.sh). The review stack serves the Pilot-0 build to named reviewers from a
# host reached over ssh: that host holds the only copy of the stack secrets and
# is the one stack reachable from the internet. Every helper keeps a secret out
# of argv, out of logs and out of the repository, and every compose call names
# the project and the env file explicitly — the default project name would come
# from the release directory, so it would change with the sha and orphan the
# volumes.
#
# Sourced, never executed.

RV_PROJECT="evidgate-review"
RV_ENV_BASENAME=".env.review"
RV_COMPOSE_REL="infra/review/compose.review.yml"
RV_TEMPLATE_REL="infra/review/env.review.template"
RV_DEFAULT_API_PORT=18080
RV_DEFAULT_WEB_PORT=8088
# Three image builds plus a database, an object store and one release tree per
# sha: a host below this has died with ENOSPC halfway through a build.
RV_MIN_FREE_KB=$((15 * 1024 * 1024))

RV_MODE="remote"
RV_SSH_HOST=""
RV_DRY_RUN=0
RV_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
RV_HOST_HOME=""
RV_HOST_ROOT=""
RV_RELEASE_SHA=""
RV_RELEASE_DIR=""
RV_ENV_FILE=""
RV_PUBLIC_ORIGIN="${EVIDGATE_REVIEW_PUBLIC_ORIGIN:-}"
RV_API_PORT="$RV_DEFAULT_API_PORT"
RV_WEB_PORT="$RV_DEFAULT_WEB_PORT"
RV_FORWARD_PID=""
RV_SCRATCH=()
# One setting decides how the stack is reachable: none (loopback only), quick
# (cloudflared's throwaway trycloudflare.com host, no token) or token (a named
# tunnel the owner created, which needs CLOUDFLARE_TUNNEL_TOKEN).
RV_TUNNEL_MODE="none"
# Images built on the deploying machine and shipped by `docker save`: the image
# tag is then the short sha, because that is what the loaded images carry.
# shellcheck disable=SC2034 # read by lib-host.sh, which this file sources
RV_BUILD_LOCAL=0
# shellcheck disable=SC2034 # same, read by lib-host.sh
RV_SETUP_SWAP=0
RV_IMAGE_TAG=""
# shellcheck disable=SC2034 # read by lib-env.sh and lib-secrets.sh
RV_AUTH_USER="reviewer"
# shellcheck disable=SC2034 # same
RV_AUTH_PASSWORD=""
# shellcheck disable=SC2034 # written and read by lib-env.sh
RV_AUTH_HASH=""
RV_HOST_ARCH=""
# shellcheck disable=SC2034 # read by lib-tunnel.sh and status.sh
RV_QUICK_TUNNEL_CONTAINER="evidgate-review-cloudflared-quick"

rv_info() { printf '%s: %s\n' "${0##*/}" "$*"; }

rv_warn() { printf '%s: %s\n' "${0##*/}" "$*" >&2; }

rv_die() {
  printf '%s: %s\n' "${0##*/}" "$*" >&2
  exit 1
}

rv_step() { printf '\n== %s\n' "$*"; }

# Where the next command runs, for messages only.
rv_target() {
  if [ "$RV_MODE" = local ]; then
    printf 'this machine'
  else
    printf 'ssh:%s' "$RV_SSH_HOST"
  fi
}

# One argument, quoted for a POSIX shell. The remote side re-parses every
# command as a string, so a path with a space or a sha must not split there.
rv_quote() {
  printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
}

rv_require_cmd() {
  command -v "$1" >/dev/null 2>&1 || rv_die "$1 is required on this machine (not on $(rv_target))"
}

# Never printed: a generated value only ever reaches the env file.
rv_random_hex() { # BYTES
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$1"
  else
    od -An -tx1 -N "$1" /dev/urandom | tr -d ' \n'
  fi
}

# The pepper and the signing key are parsed as base64 by the API; hex would be
# rejected there, so this one path needs openssl.
rv_random_base64() { # BYTES
  command -v openssl >/dev/null 2>&1 || rv_die "openssl is required to mint the base64 review secrets"
  openssl rand -base64 "$1" | tr -d '\n'
}

# Remote commands run through the login shell of the host. `accept-new` records
# a host key on first contact and still fails on a changed one: a deploy cannot
# answer an interactive prompt, and silently trusting a new key would defeat it.
rv_run() { # read-only command; always executes, even under --dry-run
  if [ "$RV_MODE" = local ]; then
    bash -c "$1"
  else
    ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new \
      "$RV_SSH_HOST" "$1"
  fi
}

rv_run_stdin() {
  if [ "$RV_MODE" = local ]; then
    bash -c "$1"
  else
    ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new \
      "$RV_SSH_HOST" "$1"
  fi
}

# Refuses under --dry-run instead of trusting every caller to remember.
rv_mutate() { # command that changes the host
  if [ "$RV_DRY_RUN" = 1 ]; then
    rv_die "internal: a mutating command ran under --dry-run: $1"
  fi
  rv_run "$1"
}

rv_mutate_stdin() {
  if [ "$RV_DRY_RUN" = 1 ]; then
    rv_die "internal: a mutating command ran under --dry-run: $1"
  fi
  rv_run_stdin "$1"
}

# The command text, without running it: --dry-run prints these, and the real
# path executes exactly the same string. EVIDGATE_REVIEW_SHA and _TAG are set per
# call so the image tag always names the release being served (with --build-local
# it is the short sha the shipped images carry), and EVIDGATE_REVIEW_TUNNEL_MODE
# is passed per call so switching between quick and token never needs a rewrite
# of the env file: the shell value wins over the one recorded at creation.
rv_compose_argv() {
  local arg out="" extra=""
  for arg in "$@"; do
    out="$out $(rv_quote "$arg")"
  done
  # Quick mode is a second guard, not just a different profile: the override
  # makes Basic Auth `required` explicit, so the image refuses to serve the
  # review build unmasked even if the mode variable were misread.
  if [ "$RV_TUNNEL_MODE" = quick ]; then
    extra=" -f $(rv_quote "$RV_RELEASE_DIR/infra/review/compose.quick.yml")"
  fi
  printf 'cd %s && EVIDGATE_REVIEW_SHA=%s EVIDGATE_REVIEW_TAG=%s EVIDGATE_REVIEW_TUNNEL_MODE=%s docker compose --project-name %s --env-file %s -f %s%s%s' \
    "$(rv_quote "$RV_RELEASE_DIR")" \
    "$(rv_quote "$RV_IMAGE_TAG")" \
    "$(rv_quote "$RV_IMAGE_TAG")" \
    "$(rv_quote "$RV_TUNNEL_MODE")" \
    "$(rv_quote "$RV_PROJECT")" \
    "$(rv_quote "$RV_ENV_FILE")" \
    "$(rv_quote "$RV_COMPOSE_REL")" \
    "$extra" \
    "$out"
}

rv_compose() { rv_mutate "$(rv_compose_argv "$@")"; }

rv_compose_read() { rv_run "$(rv_compose_argv "$@")"; }

# Compose by project name alone: teardown and status must work when the release
# tree a stack was started from is already gone, and the labels carry the rest.
rv_compose_project_argv() {
  local arg out=""
  for arg in "$@"; do
    out="$out $(rv_quote "$arg")"
  done
  printf 'docker compose --project-name %s%s' "$(rv_quote "$RV_PROJECT")" "$out"
}

rv_project_compose() { rv_mutate "$(rv_compose_project_argv "$@")"; }

# Teardown must still work when the tree behind `current` is gone; the container
# labels identify the project, so compose can run without a file then.
rv_teardown() {
  if rv_run "test -f $(rv_quote "$RV_ENV_FILE")" \
    && rv_run "test -f $(rv_quote "$RV_HOST_ROOT/current/$RV_COMPOSE_REL")"; then
    if [ -z "$RV_RELEASE_SHA" ]; then
      RV_RELEASE_SHA="$(rv_run "readlink $(rv_quote "$RV_HOST_ROOT/current")")"
      RV_RELEASE_SHA="${RV_RELEASE_SHA#releases/}"
    fi
    RV_RELEASE_DIR="$RV_HOST_ROOT/current"
    rv_compose "$@"
  else
    rv_project_compose "$@"
  fi
}





# Resolves the host layout and proves docker answers; status and down need no
# more than this, and deploy builds on it.
rv_probe_host() {
  local home
  # shellcheck disable=SC2016 # ${HOME} must expand on the host, not here
  home="$(rv_run 'printf %s "${HOME:-}"')" \
    || rv_die "cannot reach $(rv_target) (check the ssh host and key)"
  [ -n "$home" ] || rv_die "$(rv_target) reported an empty HOME"
  RV_HOST_HOME="${EVIDGATE_REVIEW_HOST_HOME:-$home}"
  if [ "$RV_MODE" = local ] && [ -z "${EVIDGATE_REVIEW_HOST_ROOT:-}" ]; then
    # The smoke must not inherit or leave the real review layout on this
    # machine, so it gets a throwaway root and prints where it went.
    RV_HOST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/evidgate-review-local.XXXXXX")"
  else
    RV_HOST_ROOT="${EVIDGATE_REVIEW_HOST_ROOT:-$RV_HOST_HOME/evidgate-review}"
  fi
  case "$RV_HOST_ROOT" in
    /*) ;;
    *) rv_die "the host root must be an absolute path (got '$RV_HOST_ROOT')" ;;
  esac
  RV_ENV_FILE="$RV_HOST_ROOT/$RV_ENV_BASENAME"
  rv_run "command -v docker >/dev/null 2>&1" || rv_die "docker is not on PATH on $(rv_target)"
  rv_run "docker info >/dev/null 2>&1" \
    || rv_die "cannot reach the docker daemon on $(rv_target) (permissions or daemon down)"
}

# Fails instead of deploying onto a host that cannot build or store the stack.
# shellcheck disable=SC2034 # RV_HOST_ARCH is read by lib-host.sh
rv_preflight() {
  rv_require_cmd git
  rv_require_cmd curl
  [ "$RV_MODE" = local ] || rv_require_cmd ssh

  local arch free_kb compose_version
  rv_probe_host

  compose_version="$(rv_run 'docker compose version --short 2>/dev/null || true')"
  [ -n "$compose_version" ] || rv_die "docker compose v2 is required on $(rv_target)"
  rv_run "command -v curl >/dev/null 2>&1" || rv_die "curl is required on $(rv_target)"
  # The release's own infra gate runs on the host before any image is built, and
  # it needs node (>= 20) and python3 there, not in a container.
  for tool in node python3; do
    rv_run "command -v $tool >/dev/null 2>&1" \
      || rv_die "$tool is required on $(rv_target): infra/review/check.sh runs there before the build (node >= 20)"
  done
  rv_run "test -w $(rv_quote "$RV_HOST_HOME")" \
    || rv_die "$RV_HOST_HOME is not writable on $(rv_target)"

  arch="$(rv_run 'uname -m')"
  case "$arch" in
    x86_64 | amd64) RV_HOST_ARCH="amd64" ;;
    aarch64 | arm64) RV_HOST_ARCH="arm64" ;;
    *) rv_die "unsupported architecture '$arch' on $(rv_target): the images are built for x86_64 and aarch64" ;;
  esac

  free_kb="$(rv_run "df -Pk $(rv_quote "$RV_HOST_HOME") | awk 'NR==2 {print \$4}'")"
  case "$free_kb" in
    '' | *[!0-9]*) rv_die "cannot read free space under $RV_HOST_HOME on $(rv_target)" ;;
  esac
  [ "$free_kb" -ge "$RV_MIN_FREE_KB" ] \
    || rv_die "only $((free_kb / 1024 / 1024)) GiB free under $RV_HOST_HOME on $(rv_target); $((RV_MIN_FREE_KB / 1024 / 1024)) GiB is required"

  rv_report_memory

  rv_info "preflight ok: $(rv_target), docker compose $compose_version, $arch, $((free_kb / 1024 / 1024)) GiB free, root $RV_HOST_ROOT"
}

# The release is shipped from a commit, so uncommitted edits would silently not
# be in it; and a commit the operator's checkout does not hold cannot be
# verified against what was reviewed.
rv_resolve_sha() {
  local sha="$RV_RELEASE_SHA" dirty
  # sed, not `| head -n 1`: head closing the pipe makes git fail with SIGPIPE,
  # and pipefail would turn a clean tree into a failure.
  dirty="$(git -C "$RV_REPO_ROOT" status --porcelain 2>/dev/null | sed -n '1p' || true)"
  [ -z "$dirty" ] || rv_die "the working tree $RV_REPO_ROOT is dirty (first entry: $dirty) — commit or stash first, or check out the sha to ship"

  [ -n "$sha" ] || sha="$(git -C "$RV_REPO_ROOT" rev-parse HEAD)"
  case "$sha" in
    *[!0-9a-f]*) rv_die "--sha must be a full lowercase commit sha (got '$sha')" ;;
  esac
  [ "${#sha}" -eq 40 ] || rv_die "--sha must be the full 40-character commit sha (got '$sha')"
  git -C "$RV_REPO_ROOT" cat-file -e "$sha^{commit}" 2>/dev/null \
    || rv_die "commit $sha is not in $RV_REPO_ROOT; fetch it first"
  git -C "$RV_REPO_ROOT" merge-base --is-ancestor "$sha" HEAD \
    || rv_die "commit $sha is not an ancestor of HEAD — check out the commit or fetch the branch that holds it"
  git -C "$RV_REPO_ROOT" cat-file -e "$sha:$RV_COMPOSE_REL" 2>/dev/null \
    || rv_die "commit $sha has no $RV_COMPOSE_REL"
  git -C "$RV_REPO_ROOT" cat-file -e "$sha:$RV_TEMPLATE_REL" 2>/dev/null \
    || rv_die "commit $sha has no $RV_TEMPLATE_REL"
  RV_RELEASE_SHA="$sha"
}

rv_init_paths() {
  RV_ENV_FILE="$RV_HOST_ROOT/$RV_ENV_BASENAME"
  RV_RELEASE_DIR="$RV_HOST_ROOT/releases/$RV_RELEASE_SHA"
  # Without --build-local the host builds and tags with the full sha; the caller
  # sets the short tag before this runs when images are shipped instead.
  [ -n "$RV_IMAGE_TAG" ] || RV_IMAGE_TAG="$RV_RELEASE_SHA"
}

# The env file on the host is the source of truth for the published ports once
# it exists; the defaults apply only before the first deploy.
rv_refresh_ports() {
  local value
  if rv_run "test -f $(rv_quote "$RV_ENV_FILE")"; then
    value="$(rv_env_get EVIDGATE_REVIEW_API_PORT)"
    if [ -n "$value" ]; then RV_API_PORT="$value"; fi
    value="$(rv_env_get EVIDGATE_REVIEW_LOCAL_PORT)"
    if [ -n "$value" ]; then RV_WEB_PORT="$value"; fi
    value="$(rv_env_get EVIDGATE_REVIEW_PUBLIC_ORIGIN)"
    # shellcheck disable=SC2034 # read by deploy.sh and by rv_compose_argv's callers
    if [ -n "$value" ]; then RV_PUBLIC_ORIGIN="$value"; fi
  fi
  local port
  for port in "$RV_API_PORT" "$RV_WEB_PORT"; do
    case "$port" in
      '' | *[!0-9]*) rv_die "a review port is not a number (got '$port') — check $RV_ENV_FILE" ;;
    esac
  done
}





# In the tunnel profile cloudflared is the only ingress, so a published host
# port would be a second door the runbook does not describe.
rv_assert_no_host_ports() {
  local published
  published="$(rv_run "docker ps --filter label=com.docker.compose.project=$(rv_quote "$RV_PROJECT") --format '{{.Names}} {{.Ports}}' | grep -F -- '->' || true")"
  [ -z "$published" ] \
    || rv_die "the tunnel profile must publish nothing on the host, but: $published"
}

# Waits on a condition the caller can observe, never on a fixed sleep.
rv_wait_http() { # URL TIMEOUT_SECONDS "ACCEPTED CODES" LABEL
  local url="$1" deadline=$(($2 + SECONDS)) accepted="$3" label="$4" code="000"
  while [ "$SECONDS" -lt "$deadline" ]; do
    if [ -n "$RV_FORWARD_PID" ] && ! kill -0 "$RV_FORWARD_PID" 2>/dev/null; then
      rv_die "the ssh port forward for $label died; check that 127.0.0.1:$RV_API_PORT is free on this machine"
    fi
    code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    case " $accepted " in
      *" ${code} "*) return 0 ;;
    esac
    sleep 2
  done
  rv_die "$label did not answer with one of [$accepted] within $2 s (last HTTP ${code:-none}): $url"
}

rv_open_forward() {
  if [ "$RV_MODE" = local ]; then
    return 0
  fi
  rv_info "forwarding 127.0.0.1:$RV_API_PORT to $(rv_target)"
  ssh -N -o ExitOnForwardFailure=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new \
    -L "127.0.0.1:$RV_API_PORT:127.0.0.1:$RV_API_PORT" "$RV_SSH_HOST" &
  RV_FORWARD_PID=$!
}

rv_close_forward() {
  [ -n "$RV_FORWARD_PID" ] || return 0
  kill "$RV_FORWARD_PID" 2>/dev/null || true
  wait "$RV_FORWARD_PID" 2>/dev/null || true
  RV_FORWARD_PID=""
}

rv_cleanup() {
  local file
  rv_close_forward
  for file in ${RV_SCRATCH[@]+"${RV_SCRATCH[@]}"}; do
    # -r: the build-local path registers an extracted tree, not just a file.
    rm -rf "$file"
  done
}

# The rest is split by concern and sourced here: the parts only ever call the
# helpers above at run time, so the order between files does not matter.
for _rv_part in env secrets tunnel host; do
  # shellcheck source=/dev/null # the sibling path is resolved at run time
  . "$(dirname "${BASH_SOURCE[0]}")/lib-$_rv_part.sh"
done
unset _rv_part
