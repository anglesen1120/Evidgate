#!/usr/bin/env bash
# The dogfood step opens an ssh forward from this machine to the review host.
# RV_SSH_OPTS carries a ControlMaster/ControlPath mux (one connection per host
# for the dozens of short remote commands), and with a live master a plain
# `ssh -L` hands the forward to the master and exits at once: the pid the script
# stores is already dead, rv_wait_http then reports "the port forward died",
# while the master keeps listening on 127.0.0.1:18080 for the rest of the deploy.
# The forward must therefore disable the mux before the shared options (ssh uses
# the first value it is given), and stay a real child rv_close_forward can kill.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup forward-no-mux

# A fake ssh that models the hand-off instead of hiding it: the first
# ControlMaster/ControlPath values win, as in ssh itself, and a forward with a
# mux path exits 0 immediately. Anything else stays in the foreground so the
# caller owns a live child, exactly like a real forward.
fwd_bin="$SANDBOX/fwd-bin"
mkdir -p "$fwd_bin"
cat >"$fwd_bin/ssh" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >"$FWD_ARGV"
cp=""
forward=0
while [ $# -gt 0 ]; do
  case "$1" in
    -o)
      case "$2" in
        # First value wins, as in ssh. The hand-off is decided by the control
        # path alone: ControlMaster=no still reuses an existing master, and only
        # ControlPath=none rules the reuse out for certain.
        ControlPath=*) if [ -z "$cp" ]; then cp="${2#ControlPath=}"; fi ;;
      esac
      shift 2
      ;;
    -N) forward=1; shift ;;
    -L|-R) forward=1; shift 2 ;;
    -i|-p|-l|-F|-E|-J) shift 2 ;;
    -*) shift ;;
    *) break ;;
  esac
done
if [ "$forward" = 1 ] && [ "${cp:-none}" != none ]; then
  exit 0
fi
exec sleep 30
STUB
chmod +x "$fwd_bin/ssh"
export FWD_ARGV="$SANDBOX/forward-argv"
: >"$FWD_ARGV"
export PATH="$fwd_bin:$PATH"

# shellcheck source=/dev/null # the lib path is resolved at run time
. "$FX_REPO/scripts/review/lib.sh"
trap 'rv_close_forward; rv_cleanup; fx_teardown' EXIT

# shellcheck disable=SC2034 # read by rv_open_forward/rv_resolve_ssh_key in the sourced lib
RV_MODE=remote
# shellcheck disable=SC2034 # same
RV_SSH_HOST=review-host
# shellcheck disable=SC2034 # same
RV_API_PORT=18080
# The real helper builds the mux options; that argv is what this test guards.
# An exported key would be resolved against the fixture's HOME and die, and this
# test needs no identity at all.
unset EVIDGATE_REVIEW_SSH_KEY
rv_resolve_ssh_key ""
case " ${RV_SSH_OPTS[*]} " in
  *" ControlMaster=auto "*" ControlPath="*) : ;;
  *) fx_fail "the shared ssh options carry no mux: ${RV_SSH_OPTS[*]}" ;;
esac
fx_ok "the shared ssh options mux to one connection"

# --- the fake really models the bug ---------------------------------------
# The child must be dead at once in both mux shapes, which is why the stored pid
# was useless and the master's listener leaked. A foreground fake would hang the
# suite, so each probe is backgrounded and must be gone after a second.
handoff_check() { # LABEL ARGS...
  local label="$1" probe
  shift
  "$fwd_bin/ssh" "$@" &
  probe=$!
  sleep 1
  if kill -0 "$probe" 2>/dev/null; then
    kill -9 "$probe" 2>/dev/null || true
    wait "$probe" 2>/dev/null || true
    fx_fail "$label: the fake stayed in the foreground, so it does not model the hand-off"
  fi
  wait "$probe" 2>/dev/null || true
  fx_ok "$label"
}

# Old argv shape: mux first, no override.
handoff_check "an auto master hands the forward off and exits at once (the reported bug)" \
  -N -o ControlMaster=auto -o ControlPath=/tmp/eg-ssh.XXXXXX/%C \
  -o ExitOnForwardFailure=yes -L 127.0.0.1:18080:127.0.0.1:18080 review-host
# ControlMaster=no does not rule out reuse: an existing master socket is still
# used, so a non-none control path hands off there too.
handoff_check "ControlMaster=no still reuses a master over a control path" \
  -N -o ControlMaster=no -o ControlPath=/tmp/eg-ssh.XXXXXX/%C \
  -o ExitOnForwardFailure=yes -L 127.0.0.1:18080:127.0.0.1:18080 review-host
: >"$FWD_ARGV"

# --- the forward the scripts actually open ---------------------------------
rv_open_forward
pid="$RV_FORWARD_PID"
[ -n "$pid" ] || fx_fail "rv_open_forward stored no pid"
sleep 1
if ! kill -0 "$pid" 2>/dev/null; then
  fx_fail "the forward pid is dead after 1 s, so the deploy would fail its readiness wait: $(cat "$FWD_ARGV")"
fi
fx_ok "the forward is a live child after 1 s (pid $pid)"

argv="$(cat "$FWD_ARGV")"
fx_assert_contains "$argv" "-L 127.0.0.1:18080:127.0.0.1:18080" "the forward asks for the api port"
# ssh uses the first value of an option, so the override has to come before the
# shared options; a later ControlMaster=no would be ignored.
before_mux="${argv%%ControlMaster=auto*}"
[ "$before_mux" != "$argv" ] || fx_fail "the shared mux option is missing from the forward: $argv"
fx_assert_contains "$before_mux" "-o ControlMaster=no" "the forward opts out of the mux before the shared options"
fx_assert_contains "$before_mux" "-o ControlPath=none" "the forward uses no control path before the shared options"
fx_assert_contains "$argv" "-o ExitOnForwardFailure=yes" "the forward still fails fast when the port is taken"

# --- teardown really stops it ---------------------------------------------
rv_close_forward
[ -z "$RV_FORWARD_PID" ] || fx_fail "rv_close_forward left RV_FORWARD_PID set"
if kill -0 "$pid" 2>/dev/null; then
  fx_fail "the forward survived rv_close_forward (pid $pid)"
fi
fx_ok "rv_close_forward stops the forward"
