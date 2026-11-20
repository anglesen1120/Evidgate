import { FIXTURE_ROUTE_ROWS, corpusDocument } from './fixture-contract.corpus';
import { VALIDATOR, isRecord } from './fixture-contract.support';

describe('Fixture project-keyed policy reads', () => {
  it("pins step 5's activation read to the project-keyed GET (09.2.1-T5.2)", () => {
    // The wizard derives step 5 from this read alone (07.1.1 F18), so the corpus must answer the
    // operation the route names, with the read's own document shape: `PolicyActivationStateDto`
    // holds the six stored/joined members and none of the POST answer's two extra ones (migration
    // `0033` stores no `previous_policy_revision_id` and no `changed`, and this route does not
    // serve them). A fixture that drifted either way would make the wizard's `done[5]` a claim
    // about a body the server cannot produce.
    expect(FIXTURE_ROUTE_ROWS.find((route) => route.operationId === 'getPolicyActivation')).toEqual(
      {
        operationId: 'getPolicyActivation',
        method: 'GET',
        path: '/projects/{p}/policy-activation',
      },
    );

    const names = [
      'policy-activation-read-ok',
      'policy-activation-read-enforcing',
      'policy-activation-read-not-found',
      'policy-activation-read-forbidden',
      'policy-activation-read-rate-limited',
    ];
    for (const name of names) {
      const document = corpusDocument(name);
      expect(document.operation_id).toBe('getPolicyActivation');
      expect(
        VALIDATOR.validateResponse('getPolicyActivation', document.status, document.body).errors,
        `fixtures/web-api/${name} no longer matches the getPolicyActivation responses of contracts/openapi.yaml`,
      ).toEqual([]);
    }

    const activated = corpusDocument('policy-activation-read-ok').body;
    expect(isRecord(activated)).toBe(true);
    if (isRecord(activated)) {
      expect(Object.keys(activated).sort()).toEqual([
        'activated_at',
        'activated_by',
        'hash',
        'mode',
        'policy_revision_id',
        'project_id',
      ]);
      expect(activated['mode']).toBe('advisory');
    }
    // The same pointer in `enforcing` mode: a mode the wizard's own confirm never sends (07.7.1),
    // so a review can still show the read answering it.
    const enforcing = corpusDocument('policy-activation-read-enforcing').body;
    expect(isRecord(enforcing) && enforcing['mode']).toBe('enforcing');
    expect(corpusDocument('policy-activation-read-enforcing').status).toBe(200);

    // The contract's one undifferentiated 404 and the 403 of a principal without `policy:read`:
    // the two states the client separates from "no activation" and from a failed load.
    const notFound = corpusDocument('policy-activation-read-not-found');
    expect(notFound.status).toBe(404);
    const notFoundBody = notFound.body;
    expect(
      isRecord(notFoundBody) && isRecord(notFoundBody['error']) && notFoundBody['error']['code'],
    ).toBe('not_found');

    const forbidden = corpusDocument('policy-activation-read-forbidden');
    expect(forbidden.status).toBe(403);
    const forbiddenBody = forbidden.body;
    expect(
      isRecord(forbiddenBody) && isRecord(forbiddenBody['error']) && forbiddenBody['error']['code'],
    ).toBe('forbidden');

    // AC-F63-a: the throttled read. The wait the page renders is built from this document alone —
    // the contract's `429 RateLimited` response, which always carries `Retry-After` (00 §9.2) — so
    // the interval and the envelope's own `retryable` are pinned rather than assumed.
    const rateLimited = corpusDocument('policy-activation-read-rate-limited');
    expect([rateLimited.operation_id, rateLimited.status, rateLimited.variant]).toEqual([
      'getPolicyActivation',
      429,
      'rate-limited',
    ]);
    expect(rateLimited.headers['Retry-After']).toBe('30');
    const rateLimitedBody = rateLimited.body;
    expect(
      isRecord(rateLimitedBody) && isRecord(rateLimitedBody['error'])
        ? rateLimitedBody['error']
        : null,
    ).toMatchObject({
      code: 'rate_limited',
      retryable: true,
      request_id: rateLimited.headers['X-Request-Id'],
    });
  });

  it('pins the Policy tab’s head read to the project-keyed GET and its quoted ETag', () => {
    // The tab shows the activation's mode beside the head revision, so the head read answers that
    // same revision without the body, and issues the `ETag` a publish sends back as `If-Match`.
    expect(FIXTURE_ROUTE_ROWS.find((route) => route.operationId === 'getPolicyHead')).toEqual({
      operationId: 'getPolicyHead',
      method: 'GET',
      path: '/projects/{p}/policies/head',
    });

    for (const name of ['policy-head-read-ok', 'policy-head-read-not-found']) {
      const document = corpusDocument(name);
      expect(document.operation_id).toBe('getPolicyHead');
      expect(
        VALIDATOR.validateResponse('getPolicyHead', document.status, document.body).errors,
        `fixtures/web-api/${name} no longer matches the getPolicyHead responses of contracts/openapi.yaml`,
      ).toEqual([]);
    }

    // Same revision as the revision read answers: the head read is that document without its body,
    // and a summary member the server cannot produce would make the tab's head a client claim.
    const summary = corpusDocument('policy-head-read-ok');
    const head = corpusDocument('policy-revision-head').body;
    if (!isRecord(summary.body) || !isRecord(head)) {
      throw new Error('the head read and the revision read must both answer a summary object');
    }
    expect(summary.body).toEqual({
      id: head['id'],
      revision_no: head['revision_no'],
      hash: head['hash'],
      state: head['state'],
      created_by: head['created_by'],
      created_at: head['created_at'],
      version: head['version'],
    });
    // Quoted exactly as `GET /policies/{id}` issues it, so the tab's `If-Match` source is that id.
    expect(summary.headers['ETag']).toBe(`"${String(head['id'])}"`);

    // One undifferentiated 404: no revision, unknown, soft-deleted and other-tenant read alike.
    const notFound = corpusDocument('policy-head-read-not-found');
    expect([notFound.status, notFound.variant]).toEqual([404, 'not-found']);
    expect(
      isRecord(notFound.body) && isRecord(notFound.body['error']) ? notFound.body['error'] : null,
    ).toMatchObject({
      code: 'not_found',
      retryable: false,
      request_id: notFound.headers['X-Request-Id'],
    });
  });
});
