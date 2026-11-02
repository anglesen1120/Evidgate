#!/usr/bin/env bash
# The coordinator's host needs an explicit identity. -i (or
# EVIDGATE_REVIEW_SSH_KEY) must reach every ssh call, flag first, and nothing
# else may be offered when a key is given; with no key at all the user's ssh
# config decides, so no -i is added.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup sshkey

mkdir -p "$HOME/.ssh"
key="$HOME/.ssh/evidgate-review"
other="$HOME/.ssh/other-key"
printf 'private\n' >"$key"
printf 'private\n' >"$other"
chmod 600 "$key" "$other"

invocations() { grep -F 'argv: ' "$SSH_LOG" || true; }

# --- -i, with the shell's ~ expanded ----------------------------------------
# shellcheck disable=SC2088 # the deploy is what expands this ~
if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i '~/.ssh/evidgate-review' 2>&1)"; then
  fx_fail "the deploy with -i failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "-o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new -o ControlMaster=auto -o ControlPath=" \
  "every call carries the fixed options and one multiplexed connection"
fx_assert_log_has "$SSH_LOG" "-i $key -o IdentitiesOnly=yes review-host" \
  "every call carries the expanded key and IdentitiesOnly"
# ssh refuses a ControlPath over 100 characters and expands %C to 40 of them,
# so what matters is the expanded path, not the directory alone: a long TMPDIR
# must not be able to break every call.
path="$(sed -n 's/.*-o ControlPath=\([^ ]*\)\/%C.*/\1/p' "$SSH_LOG" | sed -n '1p')"
[ -n "$path" ] || fx_fail "no ControlPath directory in the ssh argv"
expanded=$(( ${#path} + 2 + 40 ))
[ "$expanded" -lt 100 ] || fx_fail "the expanded ControlPath is $expanded characters (> 99): $path/%C"
fx_ok "the expanded ControlPath is $expanded characters ($path/%C)"
[ ! -e "$path" ] || fx_fail "the socket directory $path outlived the deploy"
fx_ok "the socket directory is removed when the deploy exits"
fx_assert_not_contains "$out" "$key" "the key path is not echoed into the deploy output"

# --- the env var is the fallback, and the flag wins over it -----------------
: >"$SSH_LOG"
if ! out="$(EVIDGATE_REVIEW_SSH_KEY="$other" bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy with EVIDGATE_REVIEW_SSH_KEY failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "-i $other -o IdentitiesOnly=yes review-host" \
  "the env key is used when no flag is given"

: >"$SSH_LOG"
if ! out="$(EVIDGATE_REVIEW_SSH_KEY="$other" bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$key" 2>&1)"; then
  fx_fail "the deploy with both keys failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "-i $key -o IdentitiesOnly=yes review-host" "the flag wins over the env"
fx_assert_log_lacks "$SSH_LOG" "-i $other" "the env key is not offered as a second identity"

# --- with no key, ssh's own configuration decides ---------------------------
: >"$SSH_LOG"
if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy without a key failed: $out"
fi
fx_assert_log_lacks "$SSH_LOG" " -i " "no key means no -i, so the user's ssh config applies"
fx_assert_log_has "$SSH_LOG" "argv: -o BatchMode=yes" "the fixed options are still there"

# --- a key the deploy must refuse before it connects ------------------------
chmod 644 "$key"
if out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$key" 2>&1)"; then
  fx_fail "a world-readable key was accepted"
fi
fx_assert_contains "$out" "group/world accessible" "a world-readable key is refused"

chmod 600 "$key"
if out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$HOME/.ssh/absent" 2>&1)"; then
  fx_fail "a missing key was accepted"
fi
fx_assert_contains "$out" "is not a file" "a missing key is refused"

# --- the mode is read in whichever stat dialect the host speaks -------------
# GNU coreutils' `stat -f` reports the FILESYSTEM, and it writes part of that
# output before it rejects the BSD `%Lp` format. Reading it first used to glue
# that text in front of the real mode, and the non-octal result only warned, so a
# 0644 key reached ssh. The dialects are probed apart and anything unreadable is
# now a refusal.
gnu="$SANDBOX/gnu-bin"
mkdir -p "$gnu"
cat >"$gnu/stat" <<'STUB'
#!/bin/sh
# GNU stat, as far as this test needs it: -c takes the format, and -f is the
# filesystem report that writes partial output and then fails.
case "$1" in
  -c)
    [ -n "${FX_STAT_MODE:-}" ] || exit 1
    printf '%s\n' "$FX_STAT_MODE"
    ;;
  -f)
    echo '  File: "'"$3"'"'
    echo '    ID: 1ed1b1a0 Namelen: 255 Type: apfs'
    echo "stat: invalid file system specifier: 'Lp'" >&2
    exit 1
    ;;
  *) exit 1 ;;
esac
STUB
chmod +x "$gnu/stat"

chmod 644 "$key"
if out="$(PATH="$gnu:$PATH" FX_STAT_MODE=644 bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$key" 2>&1)"; then
  fx_fail "a 0644 key was accepted under GNU stat"
fi
fx_assert_contains "$out" "group/world accessible (mode 644)" \
  "GNU stat's mode is read cleanly instead of degrading to a warning"

: >"$SSH_LOG"
chmod 600 "$key"
if ! out="$(PATH="$gnu:$PATH" FX_STAT_MODE=600 bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$key" 2>&1)"; then
  fx_fail "a 0600 key was refused under GNU stat: $out"
fi
fx_assert_log_has "$SSH_LOG" "-i $key -o IdentitiesOnly=yes review-host" \
  "GNU stat's 600 key is used"

broken="$SANDBOX/broken-bin"
mkdir -p "$broken"
cat >"$broken/stat" <<'STUB'
#!/bin/sh
# A stat that can read no mode at all, and writes junk before failing.
echo '  File: "'"$3"'"'
exit 1
STUB
chmod +x "$broken/stat"
if out="$(PATH="$broken:$PATH" bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood -i "$key" 2>&1)"; then
  fx_fail "a key whose mode cannot be read was accepted"
fi
fx_assert_contains "$out" "cannot read the permission bits" \
  "an unreadable mode is a refusal, not a note"

# --- status and down take the same flag ------------------------------------
: >"$SSH_LOG"
if ! out="$(bash "$FX_REPO/scripts/review/status.sh" review-host -i "$key" 2>&1)"; then
  fx_fail "status.sh with -i failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "-i $key -o IdentitiesOnly=yes review-host" "status.sh uses the key"

: >"$SSH_LOG"
if ! out="$(EVIDGATE_REVIEW_SSH_KEY="$other" bash "$FX_REPO/scripts/review/down.sh" review-host --yes 2>&1)"; then
  fx_fail "down.sh with the env key failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "-i $other -o IdentitiesOnly=yes review-host" "down.sh uses the env key"

fx_ok "the ssh identity reaches every call"
