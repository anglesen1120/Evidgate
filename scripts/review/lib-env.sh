#!/usr/bin/env bash
# The secrets file and the release tree: rendering .env.review once, shipping
# exactly one commit, and pointing `current` at it. Sourced by lib.sh; never
# executed.
#
# Everything here is about the host layout, and every write travels over stdin:
# a secret in argv would be visible in ps on the deploying machine and on the
# host for as long as the command runs.

# Reads one key from the env file over the host, so the script never copies the
# file to this machine.
rv_env_get() { # KEY
  case "$1" in
    *[!A-Z0-9_]*) rv_die "internal: env key '$1' is not a shell name" ;;
  esac
  rv_run "sed -n 's/^$1=//p' $(rv_quote "$RV_ENV_FILE") | tail -n 1"
}

# Replaces the key's line, or appends it: a template may or may not carry the
# key, and compose rejects a duplicated key in an env file.
rv_env_set() { # CONTENT KEY VALUE
  local content="$1" key="$2" value="$3" line found=0 out=""
  while IFS= read -r line; do
    case "$line" in
      "$key"=*) out="${out}${key}=${value}"$'\n' ; found=1 ;;
      *) out="${out}${line}"$'\n' ;;
    esac
  done <<<"$content"
  [ "$found" = 1 ] || out="${out}${key}=${value}"$'\n'
  printf '%s' "$out"
}

rv_local_sha256() { # FILE — host-side helper, mirrors rv_file_sha
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  else
    rv_die "neither sha256sum nor shasum is available on this machine"
  fi
}

rv_file_sha() { # FILE on the host
  rv_run "if command -v sha256sum >/dev/null 2>&1; then sha256sum $(rv_quote "$1") | cut -d' ' -f1; else shasum -a 256 $(rv_quote "$1") | cut -d' ' -f1; fi"
}

# Renders the env file content on this machine: every __PLACEHOLDER__ in the
# template gets a fresh random value, so a template that gains a secret needs no
# edit here. Only full __NAME__ tokens are replaced, never a bare key name: the
# key names themselves contain double underscores. The content is transported
# over stdin and never becomes an argv.
rv_render_env() { # SHA PUBLIC_ORIGIN TUNNEL_TOKEN
  local sha="$1" origin="$2" token="$3" content placeholder name value
  content="$(git -C "$RV_REPO_ROOT" show "$sha:$RV_TEMPLATE_REL")" \
    || rv_die "cannot read $RV_TEMPLATE_REL from $sha"
  # The image tag is the release sha, never a random value: a tag that does not
  # name what it runs is worse than no tag.
  content="${content//__EVIDGATE_REVIEW_SHA__/$sha}"

  # Only the value side of a line is substituted: key names carry double
  # underscores of their own (EVIDGATE__CI__PEPPER, EVIDGATE__TASKS__*), so a
  # whole-line match would replace '__CI__' inside a key and corrupt both sides.
  # Longest token first, so a shorter token cannot eat a longer one.
  while IFS= read -r placeholder; do
    [ -n "$placeholder" ] || continue
    name="$(printf '%s' "$placeholder" | sed -e 's/^__//' -e 's/__$//')"
    case "$name" in
      *SIGNINGKEY*) value="$(rv_random_base64 48)" ;;
      *PEPPER*) value="$(rv_random_base64 32)" ;;
      *DEVLOGIN*SECRET*) value="$(rv_random_base64 32)" ;;
      *BASIC_AUTH_USER*) value="$RV_AUTH_USER" ;;
      *BASIC_AUTH_PASSWORD*)
        # Plaintext exists only for the owner to hand to a reviewer; the
        # container verifies the apr1 hash below. 24 bytes because the quick
        # tunnel puts that password on a public URL with no rate limit.
        RV_AUTH_PASSWORD="$(rv_random_hex 24)"
        value="$RV_AUTH_PASSWORD"
        ;;
      *BASIC_AUTH_HASH*)
        [ -n "$RV_AUTH_PASSWORD" ] \
          || rv_die "internal: the Basic Auth hash placeholder was rendered before its password"
        RV_AUTH_HASH="$(rv_basic_auth_hash "$RV_AUTH_PASSWORD")"
        value="$RV_AUTH_HASH"
        ;;
      *TUNNEL_MODE*) value="$RV_TUNNEL_MODE" ;;
      *) value="$(rv_random_hex 24)" ;;
    esac
    content="${content//"$placeholder"/$value}"
  done <<<"$(printf '%s\n' "$content" | sed -n 's/^[^=]*=//p' \
    | grep -oE '__[A-Za-z0-9_]+__' | awk '{ print length($0) "\t" $0 }' \
    | sort -rn | cut -f2- || true)"

  content="$(rv_env_set "$content" EVIDGATE_REVIEW_PUBLIC_ORIGIN "$origin")"
  content="$(rv_env_set "$content" CLOUDFLARE_TUNNEL_TOKEN "$token")"
  content="$(rv_env_set "$content" EVIDGATE_REVIEW_TUNNEL_MODE "$RV_TUNNEL_MODE")"
  # One pair of quotes, whatever the template put around the placeholder: an
  # unquoted $apr1 value would be read as a variable reference by the dotenv
  # parser and the container would reject every login.
  [ -z "$RV_AUTH_HASH" ] \
    || content="$(rv_env_set "$content" EVIDGATE_REVIEW_BASIC_AUTH_HASH "'$RV_AUTH_HASH'")"
  # Minting a dev session needs this key; without it POST /v1/dev/session answers
  # 503. The template owns it, and this keeps a template that predates the key
  # from shipping a stack nobody can log into.
  if ! printf '%s\n' "$content" | grep -q '^EVIDGATE__SESSION__PEPPER='; then
    content="$content
EVIDGATE__SESSION__PEPPER=$(rv_random_base64 32)"
  fi

  if printf '%s\n' "$content" | sed -n 's/^[^=]*=//p' | grep -qE '__[A-Za-z0-9_]+__'; then
    rv_die "$RV_TEMPLATE_REL still has a placeholder this script cannot mint: $(printf '%s\n' "$content" | sed -n 's/^[^=]*=//p' | grep -oE '__[A-Za-z0-9_]+__' | sort -u | tr '\n' ' ')"
  fi
  printf '%s\n' "$content"
}

# Creates the secrets file on the first deploy and never touches its content
# again: it holds the only copy of the database and API credentials, and a
# rewrite would lock every running service out of its own data.
rv_env_ensure() { # SHA PUBLIC_ORIGIN TUNNEL_TOKEN; returns 0 created, 1 kept
  local sha="$1" origin="$2" token="$3" content rendered remote_tmp want_sha
  if rv_run "test -e $(rv_quote "$RV_ENV_FILE")"; then
    rv_mutate "chmod 600 $(rv_quote "$RV_ENV_FILE")"
    if rv_run "grep -Eq '=__[A-Za-z0-9_]+__\$' $(rv_quote "$RV_ENV_FILE")"; then
      rv_die "$RV_ENV_FILE still holds a __PLACEHOLDER__; it is never rewritten, so fill it in on the host or move it aside and deploy again"
    fi
    rv_info "$RV_ENV_FILE kept existing (never rewritten): $(rv_target):$RV_ENV_FILE"
    return 1
  fi

  content="$(rv_render_env "$sha" "$origin" "$token")"
  rendered="$(mktemp "${TMPDIR:-/tmp}/evidgate-review-env.XXXXXX")" \
    || rv_die "cannot create a temporary file for the secrets"
  RV_SCRATCH+=("$rendered")
  (umask 077; printf '%s\n' "$content" >"$rendered")
  chmod 600 "$rendered"
  want_sha="$(rv_local_sha256 "$rendered")"

  remote_tmp="$RV_ENV_FILE.new.$$"
  rv_mutate "set -eu; mkdir -p $(rv_quote "$RV_HOST_ROOT")"
  rv_mutate_stdin "set -eu; umask 077; cat > $(rv_quote "$remote_tmp") && chmod 600 $(rv_quote "$remote_tmp")" <"$rendered"
  # -n: a concurrent deploy that won the race keeps its file and ours is dropped.
  rv_mutate "mv -n $(rv_quote "$remote_tmp") $(rv_quote "$RV_ENV_FILE")"
  rv_mutate "rm -f $(rv_quote "$remote_tmp")"
  rv_run "test -e $(rv_quote "$RV_ENV_FILE")" || rv_die "$RV_ENV_FILE did not appear on $(rv_target)"

  if [ "$(rv_file_sha "$RV_ENV_FILE")" != "$want_sha" ]; then
    rv_warn "$RV_ENV_FILE was created concurrently and kept as it is; re-run to use it"
    return 1
  fi
  rv_info "$RV_ENV_FILE created from $RV_TEMPLATE_REL (mode 600, random credentials, $(rv_target):$RV_ENV_FILE)"
  return 0
}

# Ships exactly one commit: git archive of that sha, unpacked into an immutable
# directory, with the sha recorded beside the tree so status can prove the two
# agree. A release directory already there is reused only when it carries the
# same marker.
rv_ship_release() { # SHA
  local sha="$1" tmp marker
  [ "$sha" = "$RV_RELEASE_SHA" ] || rv_die "internal: shipping $sha for a release of $RV_RELEASE_SHA"
  if rv_run "test -e $(rv_quote "$RV_RELEASE_DIR")"; then
    marker="$(rv_run "cat $(rv_quote "$RV_RELEASE_DIR/.release-sha") 2>/dev/null || true")"
    [ "$marker" = "$sha" ] \
      || rv_die "$RV_RELEASE_DIR exists but its .release-sha reads '${marker:-missing}'; move it aside and deploy again"
    rv_info "release $sha already shipped — reusing $(rv_target):$RV_RELEASE_DIR"
    return 0
  fi

  tmp="$RV_HOST_ROOT/releases/.tmp-$sha-$$"
  rv_info "shipping $sha to $(rv_target):$RV_RELEASE_DIR"
  rv_mutate "set -eu; mkdir -p $(rv_quote "$RV_HOST_ROOT/releases") && rm -rf $(rv_quote "$tmp") && mkdir -p $(rv_quote "$tmp")"
  git -C "$RV_REPO_ROOT" archive --format=tar "$sha" \
    | rv_mutate_stdin "set -eu; tar -xf - -C $(rv_quote "$tmp")"
  printf '%s\n' "$sha" | rv_mutate_stdin "set -eu; cat > $(rv_quote "$tmp/.release-sha")"
  rv_mutate "set -eu; chmod 644 $(rv_quote "$tmp/.release-sha") && mv $(rv_quote "$tmp") $(rv_quote "$RV_RELEASE_DIR")"
  rv_run "test -f $(rv_quote "$RV_RELEASE_DIR/$RV_COMPOSE_REL")" \
    || rv_die "the shipped release has no $RV_COMPOSE_REL"
  [ "$(rv_run "cat $(rv_quote "$RV_RELEASE_DIR/.release-sha")")" = "$sha" ] \
    || rv_die "$RV_RELEASE_DIR does not read back as $sha"
}

# Points `current` at the release that is now serving; prints the previous
# target so the caller can offer the rollback command.
rv_point_current() { # SHA
  local sha="$1" previous
  previous="$(rv_run "readlink $(rv_quote "$RV_HOST_ROOT/current") 2>/dev/null || true")"
  rv_mutate "ln -sfn $(rv_quote "releases/$sha") $(rv_quote "$RV_HOST_ROOT/current")"
  [ "$(rv_run "readlink $(rv_quote "$RV_HOST_ROOT/current")")" = "releases/$sha" ] \
    || rv_die "$RV_HOST_ROOT/current does not point at releases/$sha"
  printf '%s' "$previous"
}
