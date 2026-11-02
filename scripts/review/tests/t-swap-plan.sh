#!/usr/bin/env bash
# A host that cannot build must be told so before a layer is built, and --dry-run
# must never take the mutation path: a plan that dies halfway through preflight
# because --setup-swap was passed is useless. The swap file itself is created by
# three guarded steps, so a second --setup-swap changes nothing.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup swap-plan

deploy="$FX_REPO/scripts/review/deploy.sh"

# --- the meminfo stub is what the check reads -----------------------------
if ! out="$(FX_SWAP_TOTAL_KB=1048576 bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy with 1 GiB swap failed: $out"
fi
fx_assert_contains "$out" "MiB RAM, 1024 MiB swap" "the reported memory comes from /proc/meminfo"

# --- dry-run with --setup-swap and no swap prints the plan -----------------
: >"$SSH_LOG"
: >"$SUDO_LOG"
mkdir -p "$SANDBOX/tmp"
if ! out="$(TMPDIR="$SANDBOX/tmp" FX_SWAP_TOTAL_KB=0 bash "$deploy" review-host --setup-swap --dry-run 2>&1)"; then
  fx_fail "a dry run with --setup-swap on a swap-less host died: $out"
fi
fx_assert_contains "$out" "would add a 2 GiB swap file" "the plan names the swap step"
fx_assert_contains "$out" "dry run: nothing is created" "the plan is printed"
fx_assert_log_lacks "$SUDO_LOG" "fallocate" "a dry run never touches the swap file"

# --- a small host without swap is refused, with both ways out -------------
if out="$(FX_MEM_TOTAL_KB=4030000 FX_SWAP_TOTAL_KB=0 bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "a 4 GiB host with no swap built the images"
fi
fx_assert_contains "$out" "--build-local" "the refusal names --build-local"
fx_assert_contains "$out" "--setup-swap" "the refusal names --setup-swap"

# --- a host with enough RAM only warns ------------------------------------
: >"$SSH_LOG"
if ! out="$(FX_MEM_TOTAL_KB=8388608 FX_SWAP_TOTAL_KB=0 bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "an 8 GiB host with no swap was refused: $out"
fi
fx_assert_contains "$out" "swap is under 1 GiB" "an 8 GiB host is warned, not refused"

# --- --setup-swap issues one guarded command ------------------------------
# The sudo stub records the argv and does nothing, so the command string is
# exercised without touching this machine's swap or /etc/fstab.
: >"$SUDO_LOG"
if ! out="$(FX_MEM_TOTAL_KB=4030000 FX_SWAP_TOTAL_KB=0 bash "$deploy" review-host --setup-swap --no-dogfood 2>&1)"; then
  fx_fail "the deploy with --setup-swap failed: $out"
fi
fx_assert_log_has "$SUDO_LOG" "fallocate -l 2G /swapfile" "the swap file is created once"
fx_assert_log_has "$SUDO_LOG" "mkswap /swapfile" "the swap file is formatted"
fx_assert_log_has "$SUDO_LOG" "swapon /swapfile" "the swap file is enabled"
fx_assert_log_has "$SUDO_LOG" "stdin /swapfile none swap sw 0 0" "fstab records the swap file"

# The three guards are what makes a second --setup-swap a no-op.
fx_assert_log_has "$SSH_LOG" "if [ ! -f /swapfile ]" "an existing swap file is reused"
fx_assert_log_has "$SSH_LOG" "swapon --show" "an active swap file is not enabled twice"
fx_assert_log_has "$SSH_LOG" "grep -q '^/swapfile '" "fstab keeps a single line"

# ... and re-running the same deploy is supported.
: >"$SUDO_LOG"
if ! out="$(FX_MEM_TOTAL_KB=4030000 FX_SWAP_TOTAL_KB=0 bash "$deploy" review-host --setup-swap --no-dogfood 2>&1)"; then
  fx_fail "a second --setup-swap deploy failed: $out"
fi
fx_assert_log_has "$SUDO_LOG" "fallocate -l 2G /swapfile" "a second run re-issues the same guarded command"
