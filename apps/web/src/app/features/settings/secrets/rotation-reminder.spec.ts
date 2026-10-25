// 09.7.1-T3.2 (plan 09.7.1 §2 AC3, §6 "rotation state and expiry" row, §10 T3.2; COORDINATOR
// DECISIONS S82 P3/P4): the rotation reminder chip.
//
// The rule is a boundary, not a vibe, so every case here is a boundary: exactly 14 days ahead, one
// millisecond beyond it, the expiry instant itself, one millisecond before it. The two inputs that
// make a reminder meaningless — a token that is no longer `active` (its `status` IS the rotation
// state on the wire, S82 P3) and a date the server never sent or that cannot be parsed — answer
// `none` rather than a guess (T60, no fabricated state).

import type { IngestTokenListResponse, IngestTokenView } from '@evidgate/sdk';

import { EXPIRES_SOON_DAYS, rotationReminder } from './rotation-reminder';

import liveListFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import unusableListFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-unusable/input.json';

const NOW = new Date('2026-10-25T12:00:00.000Z');
const DAY_MS = 24 * 60 * 60 * 1000;

/** The served `active` page: one token with no expiry. */
const LIVE_PAGE = liveListFixture.body as unknown as IngestTokenListResponse;
const LIVE_ROWS = LIVE_PAGE.items;
/** The served unusable page: a revoked token and an `expired` one. */
const UNUSABLE_PAGE = unusableListFixture.body as unknown as IngestTokenListResponse;
const UNUSABLE_ROWS = UNUSABLE_PAGE.items;

/** An `active` token whose deadline is `offsets[0]` milliseconds from {@link NOW}. */
function activeToken(offsetMs: number | null, status: IngestTokenView['status'] = 'active') {
  return {
    status,
    expires_at: offsetMs === null ? null : new Date(NOW.getTime() + offsetMs).toISOString(),
  };
}

describe('rotationReminder (09.7.1-T3.2, S82 P4)', () => {
  it('pins the reminder window at 14 days', () => {
    expect(EXPIRES_SOON_DAYS).toBe(14);
  });

  describe('expires-soon', () => {
    it('is expires-soon exactly 14 days ahead (the window is inclusive)', () => {
      expect(rotationReminder(activeToken(14 * DAY_MS), NOW)).toBe('expires-soon');
    });

    it('is none one millisecond past 14 days', () => {
      expect(rotationReminder(activeToken(14 * DAY_MS + 1), NOW)).toBe('none');
    });

    it('is expires-soon inside the window, e.g. one hour ahead', () => {
      expect(rotationReminder(activeToken(60 * 60 * 1000), NOW)).toBe('expires-soon');
    });

    it('is expires-soon one millisecond before the expiry instant', () => {
      expect(rotationReminder(activeToken(1), NOW)).toBe('expires-soon');
    });

    it('is none when the deadline is further out than the window', () => {
      expect(rotationReminder(activeToken(15 * DAY_MS), NOW)).toBe('none');
    });
  });

  describe('expired', () => {
    it('is expired at exactly the expiry instant', () => {
      expect(rotationReminder(activeToken(0), NOW)).toBe('expired');
    });

    it('is expired one millisecond after the expiry instant', () => {
      expect(rotationReminder(activeToken(-1), NOW)).toBe('expired');
    });
  });

  it('gives no chip to any row whose served status is not active, whatever its dates say', () => {
    // The real corpus of unusable rows: a revoked token with no deadline, and an `expired` token
    // whose deadline is in the past. Their status already says why they are out of use, so the chip
    // stays away — the reminder is for a token that is still verifying.
    expect(UNUSABLE_ROWS.map((row) => rotationReminder(row, NOW))).toEqual(
      UNUSABLE_ROWS.map(() => 'none'),
    );
    // ...while the served `active` token with no expiry earns none either (nothing to warn about).
    expect(LIVE_ROWS.map((row) => rotationReminder(row, NOW))).toEqual(LIVE_ROWS.map(() => 'none'));
  });

  describe('none', () => {
    it('is none when the server sent no expiry', () => {
      expect(rotationReminder(activeToken(null), NOW)).toBe('none');
    });

    it('is none when the field is absent from the row at all', () => {
      expect(rotationReminder({ status: 'active' }, NOW)).toBe('none');
    });

    it('is none when the expiry cannot be parsed', () => {
      expect(rotationReminder({ status: 'active', expires_at: 'not-a-date' }, NOW)).toBe('none');
    });

    it.each(['rotated', 'expired', 'revoked'] as const)(
      'is none for a %s token even when its deadline is inside the window',
      (status) => {
        expect(rotationReminder(activeToken(1, status), NOW)).toBe('none');
      },
    );

    it('is none for a non-active token whose deadline has already passed', () => {
      expect(rotationReminder(activeToken(-DAY_MS, 'revoked'), NOW)).toBe('none');
    });
  });
});
