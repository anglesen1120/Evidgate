// 09.7.1-T3.2 (plan 09.7.1 §2 AC3, §6 "rotation state and expiry" row, §10 T3.2; COORDINATOR
// DECISIONS S82 P4): the rotation reminder chip is a pure function of the token's metadata and the
// clock, so the answer is testable at every boundary and the list component holds no date arithmetic.
//
// P4, verbatim: `expires-soon` when `0 < expires_at - now <= 14 days` (exactly 14 days INCLUDED),
// `expired` when `expires_at <= now`, no reminder when `expires_at` is null or `status` is not
// `active`. An unparsable `expires_at` is also no reminder: a chip is a claim about a deadline, and
// a deadline that cannot be read is not one this function may invent (T60, no fabricated state).
//
// `status` IS the rotation state on the wire: `IngestTokenView` has no `rotation_state` or
// `last_rotated_at` (S82 P3), so a `rotated`/`expired`/`revoked` row never earns an expiry chip —
// its own status already says why it is no longer in use.

import type { IngestTokenView } from '@evidgate/sdk';

/** The window in which a still-active token's expiry is called out: 14 days, inclusive. */
export const EXPIRES_SOON_DAYS = 14;

/** The chip a row shows for its token's deadline: none, approaching, or already past. */
export type RotationReminder = 'none' | 'expires-soon' | 'expired';

const MILLISECONDS_PER_DAY = 24 * 60 * 60 * 1000;

/**
 * The reminder one ingest token earns at `now`.
 *
 * Boundaries (S82 P4), each pinned by a spec: exactly `14 * 24h` ahead is `expires-soon`, one
 * millisecond beyond it is `none`; `expires_at` equal to `now` is `expired`, one millisecond
 * before it is `expires-soon`; a null `expires_at`, a non-`active` status and an unparsable date
 * are `none`.
 */
export function rotationReminder(
  token: Pick<IngestTokenView, 'status' | 'expires_at'>,
  now: Date,
): RotationReminder {
  if (token.status !== 'active') {
    return 'none';
  }
  const expiresAt = token.expires_at;
  if (expiresAt === null || expiresAt === undefined) {
    return 'none';
  }
  const expiresAtMs = Date.parse(expiresAt);
  const nowMs = now.getTime();
  if (Number.isNaN(expiresAtMs) || Number.isNaN(nowMs)) {
    return 'none';
  }
  if (expiresAtMs <= nowMs) {
    return 'expired';
  }
  if (expiresAtMs - nowMs <= EXPIRES_SOON_DAYS * MILLISECONDS_PER_DAY) {
    return 'expires-soon';
  }
  return 'none';
}
