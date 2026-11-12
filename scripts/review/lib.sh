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
# Set only for teardown: `down` ignores --profile, so an all-profiles switch is
# the way to reach a service whose profile is not enabled.
RV_COMPOSE_PROFILES=""
# Extra `NAME=value` pairs for one compose call, e.g. a serialized build. Empty
# for every other call, so their argv is unchanged.
RV_COMPOSE_ENVS=""
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

RV_SSH_KEY=""
RV_SSH_OPTS=()
# Filled by the deploy's own steps: the seed's inputs, the previous release and
# the quick tunnel's URL, so the summary does not re-read them.
# shellcheck disable=SC2034 # written and read by deploy.sh
RV_SEED_TOKEN=""
# shellcheck disable=SC2034 # same
RV_SEED_SUITE=""
# shellcheck disable=SC2034 # same
RV_SEED_CASE_REV=""
# shellcheck disable=SC2034 # same
RV_SEED_ENV_REV=""
# shellcheck disable=SC2034 # same
RV_PREVIOUS=""
# shellcheck disable=SC2034 # same
RV_PUBLIC_URL=""

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

# The ssh identity. An explicit key (the -i flag, or EVIDGATE_REVIEW_SSH_KEY)
# wins over the user's ssh config and is the only identity offered, so an agent
# cannot answer first with a key the host refuses; without one no -i is added at
# all, which leaves the choice to that config. `~` is expanded here because the
# value often comes from a shell export.
rv_resolve_ssh_key() { # FLAG_VALUE (may be empty)
  local key="${1:-${EVIDGATE_REVIEW_SSH_KEY:-}}" mode socket_dir
  RV_SSH_OPTS=(-o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new)
  # One connection per host instead of one per command: a deploy runs dozens of
  # short remote commands, and a reused master also survives a slow link.
  # ssh refuses a ControlPath over 100 characters and expands %C to 40, so the
  # directory comes from a fixed short root: macOS TMPDIR alone is 49.
  if [ "$RV_MODE" != local ]; then
    socket_dir="$(mktemp -d /tmp/eg-ssh.XXXXXX)" \
      || rv_die "cannot create a directory for the ssh control socket"
    RV_SCRATCH+=("$socket_dir")
    RV_SSH_OPTS+=(-o ControlMaster=auto -o "ControlPath=$socket_dir/%C" -o ControlPersist=60
      -o ServerAliveInterval=30 -o ServerAliveCountMax=4)
  fi
  if [ -z "$key" ]; then
    RV_SSH_KEY=""
    return 0
  fi
  # shellcheck disable=SC2088 # the literal ~ is what this code expands
  case "$key" in
    '~') key="$HOME" ;;
    '~/'*) key="$HOME/${key#\~/}" ;;
    /* | '') ;;
    *) key="$PWD/$key" ;;
  esac
  [ -f "$key" ] || rv_die "ssh key '$key' is not a file"
  [ -r "$key" ] || rv_die "ssh key '$key' is not readable"
  # A key ssh itself would refuse is worth naming here: the remote side cannot
  # explain it once every call runs with BatchMode.
  #
  # The two stat dialects are probed in the order that keeps them apart: GNU
  # `stat -f` reports the FILESYSTEM and still writes some of it before rejecting
  # `%Lp`, so running it first would glue that output in front of the real mode
  # and hide a 0644 key behind a warning. `-c '%a'` is GNU's spelling and fails
  # outright on BSD; `-f '%Lp'` is BSD's. Anything that is not a plain octal mode
  # afterwards means the bits could not be read, which is a refusal, not a note.
  mode="$(stat -c '%a' "$key" 2>/dev/null || stat -f '%Lp' "$key" 2>/dev/null || true)"
  case "$mode" in
    '' | *[!0-7]*)
      rv_die "cannot read the permission bits of ssh key '$key' (stat printed '$mode'); chmod 600 it and re-run"
      ;;
  esac
  [ $((0$mode & 077)) -eq 0 ] \
    || rv_die "ssh key '$key' is group/world accessible (mode $mode): chmod 600 it"
  RV_SSH_KEY="$key"
  RV_SSH_OPTS+=(-i "$RV_SSH_KEY" -o IdentitiesOnly=yes)
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
# shellcheck disable=SC2029 # callers quote the remote command with rv_quote
rv_run() { # read-only command; always executes, even under --dry-run
  if [ "$RV_MODE" = local ]; then
    bash -c "$1"
  else
    # shellcheck disable=SC2029 # callers quote the remote command with rv_quote
    ssh ${RV_SSH_OPTS[@]+"${RV_SSH_OPTS[@]}"} "$RV_SSH_HOST" "$1"
  fi
}

# Refuses under --dry-run instead of trusting every caller to remember. stdin
# passes through, which is how every secret reaches the host.
rv_mutate() { # command that changes the host
  if [ "$RV_DRY_RUN" = 1 ]; then
    rv_die "internal: a mutating command ran under --dry-run: $1"
  fi
  rv_run "$1"
}

# The command text, without running it: --dry-run prints these, and the real
# path executes exactly the same string. EVIDGATE_REVIEW_SHA and _TAG are set per
# call so the image tag always names the release being served (with --build-local
# it is the short sha the shipped images carry), and EVIDGATE_REVIEW_TUNNEL_MODE
# is passed per call so switching between quick and token never needs a rewrite
# of the env file: the shell value wins over the one recorded at creation.
rv_compose_argv() {
  local arg out="" extra="" profiles="" envs=""
  for arg in "$@"; do
    out="$out $(rv_quote "$arg")"
  done
  # Quick mode is a second guard, not just a different profile: the override
  # makes Basic Auth `required` explicit, so the image refuses to serve the
  # review build unmasked even if the mode variable were misread.
  if [ "$RV_TUNNEL_MODE" = quick ]; then
    extra=" -f $(rv_quote "$RV_RELEASE_DIR/infra/review/compose.quick.yml")"
  fi
  # Quoted even for '*': an unquoted star would be globbed by the remote shell.
  if [ -n "$RV_COMPOSE_PROFILES" ]; then
    profiles="COMPOSE_PROFILES=$(rv_quote "$RV_COMPOSE_PROFILES") "
  fi
  # A build sets this so compose runs one build at a time; an unset hook keeps
  # every other call byte-identical to what it printed before. The pairs are
  # constants from this script, so they are emitted unquoted: a quoted word
  # would be a command name, not an assignment.
  if [ -n "$RV_COMPOSE_ENVS" ]; then
    envs="$RV_COMPOSE_ENVS $envs"
  fi
  # Only non-empty values are exported. An empty EVIDGATE_REVIEW_TAG would beat
  # the one in the env file and compose refuses a service that requires it, so a
  # teardown (which needs no image) must not send one.
  [ -z "$RV_IMAGE_TAG" ] || envs="$envs EVIDGATE_REVIEW_TAG=$(rv_quote "$RV_IMAGE_TAG")"
  [ -z "$RV_IMAGE_TAG" ] || envs="$envs EVIDGATE_REVIEW_SHA=$(rv_quote "$RV_IMAGE_TAG")"
  [ -z "$RV_TUNNEL_MODE" ] || envs="$envs EVIDGATE_REVIEW_TUNNEL_MODE=$(rv_quote "$RV_TUNNEL_MODE")"
  printf 'cd %s && %s%s docker compose --project-name %s --env-file %s -f %s%s%s' \
    "$(rv_quote "$RV_RELEASE_DIR")" \
    "$profiles" \
    "$envs" \
    "$(rv_quote "$RV_PROJECT")" \
    "$(rv_quote "$RV_ENV_FILE")" \
    "$(rv_quote "$RV_COMPOSE_REL")" \
    "$extra" \
    "$out"
}

# A command substitution that fails leaves an empty string, and an empty remote
# command exits 0 over ssh: that would skip a compose step in silence, so the
# argv is checked before it is run.
rv_compose() {
  local argv
  argv="$(rv_compose_argv "$@")" || rv_die "cannot build the compose command for '$*'"
  [ -n "$argv" ] || rv_die "internal: empty compose command for '$*'"
  rv_mutate "$argv"
}

rv_compose_read() {
  local argv
  argv="$(rv_compose_argv "$@")" || rv_die "cannot build the compose command for '$*'"
  [ -n "$argv" ] || rv_die "internal: empty compose command for '$*'"
  rv_run "$argv"
}

# Compose by project name alone: teardown and status must work when the release
# tree a stack was started from is already gone, and the labels carry the rest.
rv_compose_project_argv() {
  local arg out=""
  for arg in "$@"; do
    out="$out $(rv_quote "$arg")"
  done
  printf 'docker compose --project-name %s%s' "$(rv_quote "$RV_PROJECT")" "$out"
}

rv_project_compose() {
  local argv
  argv="$(rv_compose_project_argv "$@")" || rv_die "cannot build the compose command for '$*'"
  [ -n "$argv" ] || rv_die "internal: empty compose command for '$*'"
  rv_mutate "$argv"
}

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
    # No image has to exist for a down, and an unset tag makes compose refuse the
    # whole file: take the one the file records, and let the argv skip it if the
    # file has none.
    [ -n "$RV_IMAGE_TAG" ] || RV_IMAGE_TAG="$(rv_env_get EVIDGATE_REVIEW_TAG || true)"
    # down ignores --profile, and a service whose profile is not enabled is not
    # part of the model: '*' puts the tunnel and the debug doors in scope too.
    RV_COMPOSE_PROFILES="*"
    rv_compose "$@"
    RV_COMPOSE_PROFILES=""
    rv_mutate "ids=\$(docker ps -aq --filter $(rv_quote "label=com.docker.compose.project=$RV_PROJECT")); [ -z \"\$ids\" ] || docker rm -f \$ids"
  else
    rv_project_compose "$@"
  fi
}

# Resolves the host layout and proves docker answers; status and down need no
# more than this, and deploy builds on it.
rv_probe_host() { # CALLER (deploy, or anything else for a read-only command)
  local home
  # shellcheck disable=SC2016 # ${HOME} must expand on the host, not here
  home="$(rv_run 'printf %s "${HOME:-}"')" \
    || rv_die "cannot reach $(rv_target) (check the ssh host and key)"
  [ -n "$home" ] || rv_die "$(rv_target) reported an empty HOME"
  RV_HOST_HOME="${EVIDGATE_REVIEW_HOST_HOME:-$home}"
  if [ "$RV_MODE" = local ] && [ -z "${EVIDGATE_REVIEW_HOST_ROOT:-}" ]; then
    # Only a deploy may mint a throwaway root; status and down must name an
    # existing one, because an invented empty root would report a stack that
    # does not exist and then tear nothing down.
    if [ "${1:-read}" != deploy ]; then
      rv_die "--local needs EVIDGATE_REVIEW_HOST_ROOT: status and down read an existing smoke root, never a new one (export the root the deploy printed)"
    elif [ "$RV_DRY_RUN" = 1 ]; then
      # A dry run creates nothing, so the plan shows a path of the same shape.
      RV_HOST_ROOT="${TMPDIR:-/tmp}/evidgate-review-local.XXXXXXXX"
    else
      RV_HOST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/evidgate-review-local.XXXXXX")"
    fi
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
  # A deploy that runs the release's static gate on this machine needs the
  # generator's toolchain here; a load-only host builds nothing and needs none.
  if [ "$RV_BUILD_LOCAL" = 1 ] || [ "$RV_MODE" = local ]; then
    rv_require_cmd node
    rv_require_cmd python3
  fi

  local arch free_kb compose_version
  rv_probe_host deploy

  compose_version="$(rv_run 'docker compose version --short 2>/dev/null || true')"
  local compose_major compose_minor version="${compose_version#v}"
  compose_major="${version%%.*}"
  version="${version#*.}"
  compose_minor="${version%%.*}"
  case "$compose_major:$compose_minor" in
    *[!0-9:]* | :* | *:) rv_die "cannot read the docker compose version on $(rv_target): '${compose_version:-nothing}'" ;;
  esac
  # Inline configs require Compose 2.23; a newer major keeps that capability.
  { [ "$compose_major" -gt 2 ] || { [ "$compose_major" -eq 2 ] && [ "$compose_minor" -ge 23 ]; }; } \
    || rv_die "docker compose >= 2.23 is required on $(rv_target) for configs content (found $compose_version)"
  rv_run "command -v curl >/dev/null 2>&1" || rv_die "curl is required on $(rv_target)"
  # The release's infra gate runs the generator's node:test suite. A host that
  # only loads images never runs it (--build-local runs the same gate on this
  # machine, over the same extracted sha), so only a building host needs node
  # and python3.
  if [ "$RV_BUILD_LOCAL" != 1 ] && [ "$RV_MODE" != local ]; then
    local tool
    for tool in node python3; do
      rv_run "command -v $tool >/dev/null 2>&1" \
        || rv_die "$tool is required on $(rv_target): infra/review/check.sh runs there before the build (node >= 20)"
    done
  fi
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
# port would be a second door the runbook does not describe. The local smoke is
# the one exception the environment contract names: web-local publishes the
# bundle on loopback and nothing else may be reachable from this machine.
rv_assert_no_host_ports() {
  local published allowed unexpected
  published="$(rv_run "docker ps --filter label=com.docker.compose.project=$(rv_quote "$RV_PROJECT") --format '{{.Names}} {{.Ports}}' | grep -F -- '->' || true")"
  [ -n "$published" ] || return 0
  if [ "$RV_MODE" = local ]; then
    allowed="evidgate-review-web-local 127.0.0.1:$RV_WEB_PORT->8080/tcp"
    unexpected="$(printf '%s\n' "$published" | rv_published_mappings | grep -vxF -- "$allowed" || true)"
    [ -z "$unexpected" ] \
      || rv_die "the local smoke may publish only ${allowed#* }, and only from ${allowed%% *}, but: $unexpected"
    return 0
  fi
  rv_die "the tunnel profile must publish nothing on the host, but: $published"
}

# One `container mapping` line per published port. Docker prints every mapping
# of a container on one line, so a line-level test would accept a second door
# that shares a line with an allowed one.
rv_published_mappings() {
  awk '{
    name = $1
    for (i = 2; i <= NF; i++) {
      p = $i
      gsub(/,$/, "", p)
      if (index(p, "->")) print name, p
    }
  }'
}

rv_open_forward() {
  if [ "$RV_MODE" = local ]; then
    return 0
  fi
  rv_info "forwarding 127.0.0.1:$RV_API_PORT to $(rv_target)"
  # A mux handoff exits this child and leaves the master's forward behind.
  # ssh uses the first option value: ControlPath=none is what rules reuse out
  # (ControlMaster=no alone still reuses a live master), so it goes first.
  ssh -N -o ControlMaster=no -o ControlPath=none -o ExitOnForwardFailure=yes ${RV_SSH_OPTS[@]+"${RV_SSH_OPTS[@]}"} \
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
