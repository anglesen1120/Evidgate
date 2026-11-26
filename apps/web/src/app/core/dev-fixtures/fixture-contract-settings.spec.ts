// Token expiry fixtures use a fixed review date, never the current clock.

import { corpusDocument } from './fixture-contract.corpus';

/**
 * The fixed date the Secrets tab's token expiries were authored around: the corpus holds literal
 * timestamps, so the fourteen-day reminder window is measured against this date rather than
 * against whenever the suite runs.
 */
const FIXTURE_REVIEW_NOW = '2026-10-25T00:00:00Z';

/** Fourteen days in milliseconds — the window inside which a token's expiry is `expires-soon`. */
const FOURTEEN_DAYS_MS = 14 * 24 * 60 * 60 * 1000;

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('pins the Secrets tab’s token page states (09.7.1-T3)', () => {
    const page = corpusDocument('ingest-tokens-list-settings');
    expect([page.operation_id, page.variant, page.status]).toEqual([
      'listIngestTokens',
      'settings',
      200,
    ]);
    const body = page.body as {
      items: {
        id: string;
        status: string;
        prefix: string;
        expires_at: string | null;
        overlap_ends_at: string | null;
        rotated_to_id: string | null;
      }[];
      next_cursor: string | null;
    };
    expect(body.next_cursor).toBeNull();
    expect(
      body.items.length,
      'the Secrets tab page must cover the reminder and rotation states in one read',
    ).toBeGreaterThanOrEqual(6);

    // The four `active` states the page's reminder chip distinguishes. The corpus dates are
    // literals (a fixture cannot move with the calendar), so the window is measured against the
    // fixed date these rows were authored around.
    const reviewNow = Date.parse(FIXTURE_REVIEW_NOW);
    const expiries = body.items
      .filter((item) => item.status === 'active')
      .map((item) => (item.expires_at === null ? null : Date.parse(item.expires_at)));
    expect(expiries, 'an active token with no expiry at all').toContain(null);
    expect(
      expiries.some((expiry) => expiry !== null && expiry - reviewNow > FOURTEEN_DAYS_MS),
      'an active token whose expiry is far enough away that no reminder chip shows',
    ).toBe(true);
    expect(
      expiries.some(
        (expiry) => expiry !== null && expiry > reviewNow && expiry - reviewNow <= FOURTEEN_DAYS_MS,
      ),
      'an active token inside the fourteen-day reminder window',
    ).toBe(true);
    expect(
      expiries.some((expiry) => expiry !== null && expiry <= reviewNow),
      'an active token whose expiry has already passed, the state the `expired` reminder reports',
    ).toBe(true);

    // The rotation states: a retired row pointing at its successor, and a revoked one.
    const rotated = body.items.find((item) => item.status === 'rotated');
    expect(rotated, 'no rotated token, so the page cannot show a retirement window').toBeDefined();
    expect(rotated?.overlap_ends_at).not.toBeNull();
    expect(
      body.items.map((item) => item.id),
      'the rotated token names a successor the same page does not hold',
    ).toContain(rotated?.rotated_to_id);
    expect(body.items.some((item) => item.status === 'revoked')).toBe(true);

    // A prefix is a public identifier, never a credential: short, and far below the length of any
    // token the contract's `egt_<prefix>_<secret>` form would mint.
    for (const item of body.items) {
      expect(item.prefix.length, `${item.id} carries a token-shaped prefix`).toBeLessThan(12);
      expect(item.prefix.startsWith('egt_')).toBe(true);
      expect(item.prefix.split('.')).toHaveLength(1);
    }
  });
});
