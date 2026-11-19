import { HttpHeaders, HttpRequest } from '@angular/common/http';
import { AuthoringPendingFixtureScenario } from './fixture-authoring-pending';
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

describe('AuthoringPendingFixtureScenario', () => {
  const project = '018f0000-0000-7000-8000-000000000001';
  const path = `/projects/${project}/authoring/changes`;
  const ids = ['018f0000-0000-7000-8000-00000000cd01', '018f0000-0000-7000-8000-00000000cd02'];
  const variant = () => 'ok';
  it('reads the exact selected rows with an ETag and applies one delete version only once', () => {
    const scenario = new AuthoringPendingFixtureScenario();
    const selection = scenario.entry(
      new HttpRequest('GET', path + '?ids=' + ids.join(',')),
      path,
      variant,
    );
    expect(selection?.document.headers['ETag']).toBe('"batch-7"');
    const page = selection?.document.body as { items: readonly { change_id: string }[] };
    expect(page.items.map((row) => row.change_id)).toEqual(ids);
    const request = new HttpRequest(
      'POST',
      path + ':apply',
      { change_ids: ids },
      {
        headers: new HttpHeaders({ 'If-Match': '"batch-7"', 'Idempotency-Key': 'one-intent' }),
      },
    );
    const first = scenario.entry(request, path + ':apply', variant);
    expect(
      VALIDATOR.validateResponse('applyAuthoringChanges', 200, first?.document.body).errors,
    ).toEqual([]);
    const receipt = first?.document.body as {
      versions: readonly unknown[];
      applied_change_ids: readonly string[];
    };
    expect(receipt.versions).toHaveLength(1);
    expect(receipt.applied_change_ids).toEqual(ids);
    expect(scenario.entry(request, path + ':apply', variant)?.document.body).toEqual(
      first?.document.body,
    );
    const remaining = scenario.entry(new HttpRequest('GET', path), path, variant)?.document
      .body as { items: readonly { change_id: string }[] };
    expect(remaining.items.map((row) => row.change_id)).not.toEqual(expect.arrayContaining(ids));
  });
  it('returns a 412 without changing any queued row', () => {
    const scenario = new AuthoringPendingFixtureScenario();
    const before = scenario.entry(new HttpRequest('GET', path), path, variant)?.document.body;
    const refusal = scenario.entry(
      new HttpRequest('POST', path + ':apply', { change_ids: ids }),
      path + ':apply',
      (operation) => (operation === 'applyAuthoringChanges' ? 'conflict' : 'ok'),
    );
    expect(refusal?.document.status).toBe(412);
    expect(scenario.entry(new HttpRequest('GET', path), path, variant)?.document.body).toEqual(
      before,
    );
  });
});
