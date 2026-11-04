// The Settings area's state pins: the Secrets tab's token page and CI-binding section, whose rows
// must cover every verification state and the expiry window. The expected instants come from the
// fixed date the corpus was authored around, never from the clock.

import {
  STOREFRONT_PROJECT_ID,
  corpusDocument,
  oneTimeSecretMembers,
} from './fixture-contract.corpus';

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

  it('pins the Secrets tab’s CI-binding section states (09.7.1-F15)', () => {
    // 1. The page the section opens with: five bindings on the `Storefront web` project, one per
    //    state the section renders — the three `status` values plus the layout extremes (a single
    //    ref pattern, and a repository whose name and allowlists overflow a cell).
    const page = corpusDocument('ci-bindings-list-settings');
    expect([page.operation_id, page.variant, page.status]).toEqual([
      'listCiBindings',
      'settings',
      200,
    ]);
    const body = page.body as {
      items: {
        id: string;
        project_id: string;
        provider: string;
        repository_id: number;
        repository_full_name: string;
        allowed_ref_patterns: string[];
        allowed_workflow_refs: string[];
        status: string;
        activated_at?: string | null;
        created_at: string;
        version: number;
      }[];
      next_cursor: string | null;
    };
    expect(body.next_cursor).toBeNull();
    expect(
      body.items.length,
      'the section must show every verification state in one read',
    ).toBeGreaterThanOrEqual(5);

    const projectIds = new Set(body.items.map((item) => item.project_id));
    expect([...projectIds], 'every row belongs to the Storefront web project').toEqual([
      STOREFRONT_PROJECT_ID,
    ]);
    expect(new Set(body.items.map((item) => item.status))).toEqual(
      new Set(['active', 'pending_verification', 'disabled']),
    );

    // A pending binding is exactly one the OIDC handshake has not finished: it has no activation
    // time. An activated one always has it, so the two are never the same row.
    for (const item of body.items) {
      if (item.status === 'pending_verification') {
        expect(
          item.activated_at,
          `${item.id} is pending yet carries an activation time`,
        ).toBeNull();
      }
    }
    expect(
      body.items.some((item) => item.status !== 'pending_verification' && item.activated_at),
      'no activated binding carries its activation time',
    ).toBe(true);

    // Layout extremes: one binding allows a single ref and workflow, one overflows.
    expect(
      body.items.some(
        (item) => item.allowed_ref_patterns.length === 1 && item.allowed_workflow_refs.length === 1,
      ),
      'no binding has the single-pattern shape the compact row renders',
    ).toBe(true);
    expect(
      body.items.some(
        (item) =>
          item.repository_full_name.length > 40 &&
          item.allowed_ref_patterns.length >= 6 &&
          item.allowed_workflow_refs.length >= 3,
      ),
      'no binding has a long repository name with long allowlists, so the overflow cell has nothing to truncate',
    ).toBe(true);

    // 2. Every allowlist entry is in the form the server accepts — not merely a string the
    //    contract's patternless `array of string` lets through (`Ci/RefGlob.cs:69,79`): a ref
    //    pattern is `refs/heads/…` or `refs/tags/…`, and a workflow ref is
    //    `owner/repo/.github/workflows/<file>@refs/…` for THAT row's repository.
    for (const item of body.items) {
      expect(item.provider).toBe('github_actions');
      expect(Number.isInteger(item.repository_id) && item.repository_id > 0).toBe(true);
      expect(item.version).toBeGreaterThan(0);
      for (const pattern of item.allowed_ref_patterns) {
        expect(pattern, `${item.id} allows a ref pattern the server refuses`).toMatch(
          /^refs\/(heads|tags)\/[A-Za-z0-9._/*+@-]{1,200}$/,
        );
      }
      for (const workflowRef of item.allowed_workflow_refs) {
        expect(workflowRef, `${item.id} allows a workflow ref the server refuses`).toMatch(
          /^[A-Za-z0-9._*-]{1,100}\/[A-Za-z0-9._*-]{1,100}\/\.github\/workflows\/[A-Za-z0-9._*-]{1,100}@refs\/(heads|tags)\/[A-Za-z0-9._/*+@-]{1,200}$/,
        );
        expect(
          workflowRef.startsWith(`${item.repository_full_name}/.github/workflows/`),
          `${item.id} allows a workflow of a repository it is not bound to`,
        ).toBe(true);
      }
    }

    // 3. Metadata only: a listed binding never carries the challenge or its hash — the same pin the
    //    corpus-wide one-time-secret test applies, restated on the page the section renders.
    expect(oneTimeSecretMembers(page.body)).toEqual([]);
    for (const item of body.items) {
      expect(Object.keys(item), `${item.id} carries a challenge member`).not.toContain(
        'binding_challenge',
      );
    }

    // 4. The two other states: the unbound project's empty page, and the 403 the section renders
    //    as its own `forbidden` phase instead of an error message.
    const empty = corpusDocument('ci-bindings-list-settings-empty');
    expect([empty.operation_id, empty.variant, empty.status]).toEqual([
      'listCiBindings',
      'settings-empty',
      200,
    ]);
    expect(empty.body).toEqual({ items: [], next_cursor: null });

    const forbidden = corpusDocument('ci-bindings-list-settings-forbidden');
    expect([forbidden.operation_id, forbidden.variant, forbidden.status]).toEqual([
      'listCiBindings',
      'settings-forbidden',
      403,
    ]);
    const envelope = forbidden.body as { error: { code: string; retryable: boolean } };
    expect(envelope.error.code).toBe('forbidden');
    expect(envelope.error.retryable).toBe(false);
    expect(oneTimeSecretMembers(forbidden.body)).toEqual([]);
  });
});
