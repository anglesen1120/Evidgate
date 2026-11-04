// 09.7.1-T4 (plan 09.7.1 §2 AC4, §5 "activation mode" row; S83 P2): the mode chip's whole
// decision, as one pure function so the chip component renders and this file decides.
//
// The mode is `policy_activation.mode` VERBATIM: the wire type narrows it to `advisory` and
// `enforcing`, but a server that later serves a third mode must not be rendered as one of the two
// (a chip that says "Gating" for a mode nobody declared is a security claim the UI invented).
// So anything else is `unknown` with the raw value kept for the chip to show as text, and a mode
// that is absent — no activation, or one the caller may not read — is `unavailable`.
//
// Pure and Angular-free: the store, the chip and their specs share this one table, and nothing
// here reads a revision's `state` (a revision being `active` is a different fact from the
// project's activation mode; inferring one from the other is exactly the guess P2 forbids).

/** One rendering of a project's activation mode. `raw` is always the server's own string. */
export type PolicyModeView =
  | { readonly kind: 'gating'; readonly raw: 'enforcing' }
  | { readonly kind: 'advisory'; readonly raw: 'advisory' }
  | { readonly kind: 'unknown'; readonly raw: string }
  | { readonly kind: 'unavailable' };

/**
 * The view of one `mode` value: `enforcing` is the gating chip, `advisory` the advisory one, any
 * other non-empty string is `unknown` (raw kept), and `null`/`undefined`/the empty string is
 * `unavailable` — the chip says the mode is not readable rather than picking a default.
 */
export function policyModeView(mode: string | null | undefined): PolicyModeView {
  if (mode === null || mode === undefined || mode.length === 0) {
    return { kind: 'unavailable' };
  }
  switch (mode) {
    case 'enforcing':
      return { kind: 'gating', raw: 'enforcing' };
    case 'advisory':
      return { kind: 'advisory', raw: 'advisory' };
    default:
      return { kind: 'unknown', raw: mode };
  }
}
