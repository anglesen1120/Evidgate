import { FIXTURE_CATALOG_AUTHORING_PENDING_ROWS } from './fixture-catalog-authoring-pending-rows';
import { declaredResponseHeaderNames, VALIDATOR } from './fixture-contract.support';

describe('authoring pending fixture contract pins', () => {
  for (const row of FIXTURE_CATALOG_AUTHORING_PENDING_ROWS) {
    it(`validates ${row.name} against its declared operation and status`, () => {
      const document = row.document;
      expect(
        VALIDATOR.validateResponse(document.operation_id, document.status, document.body).errors,
      ).toEqual([]);
      const declared = declaredResponseHeaderNames(document.operation_id, document.status).map(
        (name) => name.toLowerCase(),
      );
      expect(
        Object.keys(document.headers).filter((name) => !declared.includes(name.toLowerCase())),
      ).toEqual([]);
    });
  }

  it('pins the batch ETag on the selection read and the row version on the queue answer', () => {
    const etagOf = (name: string) =>
      FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find((row) => row.name === name)?.document.headers[
        'ETag'
      ];
    expect(etagOf('authoring-pending-selection')).toBe('"batch-7"');
    expect(etagOf('authoring-pending-queue')).toBe('"5"');
    for (const row of FIXTURE_CATALOG_AUTHORING_PENDING_ROWS) {
      if (row.name === 'authoring-pending-selection' || row.name === 'authoring-pending-queue')
        continue;
      expect(row.document.headers['ETag']).toBeUndefined();
    }
  });

  it('carries a valid selection staleness flag and a comment row without one', () => {
    const selection = FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find(
      (row) => row.name === 'authoring-pending-selection',
    );
    const body = selection?.document.body as
      { items?: readonly Record<string, unknown>[] } | undefined;
    expect(body?.items?.some((item) => item['stale'] === true)).toBe(true);
    expect(body?.items?.some((item) => item['stale'] === false)).toBe(true);

    const list = FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find(
      (row) => row.name === 'authoring-pending-list',
    );
    const listBody = list?.document.body as
      { items?: readonly Record<string, unknown>[] } | undefined;
    expect(listBody?.items?.every((item) => item['stale'] === undefined)).toBe(true);
  });

  it('keeps a comment-only receipt with an empty versions array', () => {
    const receipt = FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find(
      (row) => row.name === 'authoring-pending-apply-comment-only',
    );
    const body = receipt?.document.body as { versions?: readonly unknown[] } | undefined;
    expect(body?.versions).toEqual([]);
  });

  it('names the supplied 412 pointers and keeps the error retryable flag required', () => {
    const conflict = FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find(
      (row) => row.name === 'authoring-pending-apply-conflict',
    );
    const body = conflict?.document.body as
      { error?: { retryable?: boolean; details?: readonly { reason?: string }[] } } | undefined;
    expect(body?.error?.details?.map((detail) => detail.reason)).toEqual([
      'target_changed',
      'batch_stale',
    ]);
    expect(
      VALIDATOR.validateResponse('applyAuthoringChanges', 412, {
        error: { ...body?.error, retryable: undefined },
      }).errors.length,
    ).toBeGreaterThan(0);
  });

  it('rejects a pending row whose ids are not UUIDs and a stale flag of the wrong type', () => {
    const list = FIXTURE_CATALOG_AUTHORING_PENDING_ROWS.find(
      (row) => row.name === 'authoring-pending-list',
    );
    const body = list?.document.body as
      { items: readonly Record<string, unknown>[]; next_cursor: string | null } | undefined;
    const [first] = body?.items ?? [];
    expect(
      VALIDATOR.validateResponse('listAuthoringChanges', 200, {
        items: [{ ...first, change_id: 'not-a-uuid' }],
        next_cursor: null,
      }).errors.length,
    ).toBeGreaterThan(0);
    expect(
      VALIDATOR.validateResponse('listAuthoringChanges', 200, {
        items: [{ ...first, stale: 'yes' }],
        next_cursor: null,
      }).errors.length,
    ).toBeGreaterThan(0);
  });
});
