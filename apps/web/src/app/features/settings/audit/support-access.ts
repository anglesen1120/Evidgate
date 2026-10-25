// 09.7.1-T6 (plan 09.7.1 §2 AC8, §4 audit row, §6, §8 T6.1; COORDINATOR-DECISION S76): which audit
// rows are platform-operator support access, and with which grant, from the row alone.
//
// The wire has no `support_grant_id` field (audit entry, contracts/openapi.yaml:2616-2657). A row
// is a support-access row when either
//
//   - `metadata.support_grant_id` is a non-empty string — the server recorded the grant the action
//     was taken under, whoever the actor is; or
//   - `actor_type === 'platform_operator'` — an operator acted, even if the row names no grant.
//
// The grant id is `metadata.support_grant_id` when present and nothing else: no id is ever derived
// from `actor_id`, `request_id` or a row order, and grant expiry is not on the wire, so this module
// answers no expiry at all (the UI shows none; recorded gap for AC8's tooltip).
//
// `null` is the ordinary row — the absence of a marker is what the unflagged chip renders. The
// distinction between "operator without a recorded grant" (`{ grantId: null }`) and "ordinary row"
// (`null`) is deliberate: AC8 flags the first and leaves the second alone.

import type { AuditEntryDto } from '@evidgate/sdk';

/**
 * The support-access marker of one audit row, or `null` for an ordinary member/system row.
 *
 * @returns `{ grantId }` when the row carries a grant id, `{ grantId: null }` for a
 *   platform-operator row with no marker, `null` for every other row.
 */
export function supportAccessOf(row: AuditEntryDto): { readonly grantId: string | null } | null {
  const marker = row.metadata?.['support_grant_id'];
  // The contract's marker is a non-empty string. An empty string is the shape a serializer
  // produces for "no value", so it counts as absent rather than as a blank grant chip.
  if (typeof marker === 'string' && marker !== '') {
    return { grantId: marker };
  }
  return row.actor_type === 'platform_operator' ? { grantId: null } : null;
}
