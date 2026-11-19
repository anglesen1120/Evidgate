// Selection pins for the keyed reads: the id-keyed revision read, the cursor-keyed CI-binding,
// run-list and attempts walks, every answered cursor, the one-ok-variant rule and the
// project-keyed activation read. Page readers and row shapes come from the corpus helper.

import type { RunAttemptPage } from '@evidgate/sdk';

import { FIXTURE_CATALOG } from './fixture-catalog';

import {
  CATALOG,
  CORPUS,
  FIXTURE_ROUTE_ROWS,
  STOREFRONT_PROJECT_ID,
  type CiBindingRowShape,
  type RunRowShape,
  corpusDocument,
  keysetTicks,
  oneTimeSecretMembers,
  readDocument,
} from './fixture-contract.corpus';
import { VALIDATOR, isRecord } from './fixture-contract.support';

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('pins the id-keyed revision read to distinct corpus ids (09.7.1-F15)', () => {
    // 09.7.1-F15 note (3): `getPolicyRevision` answers the document whose own `body.id` equals the
    // request's `{id}`. That is only sound when (a) the row names a parameter its template actually
    // carries, (b) every 2xx document of the operation carries a string `id`, (c) no two documents
    // share one, so the selection is unambiguous, and (d) the revision ids a corpus page offers are
    // exactly the documents a read can address — otherwise a page load would ask for an id no
    // document answers and silently get the variant.
    const idKeyed = FIXTURE_ROUTE_ROWS.filter((route) => route.idKeyParameter !== undefined);
    expect(
      idKeyed.map((route) => route.operationId),
      'each id-keyed read needs its own corpus pin',
    ).toEqual(['getProjectAiSettingsVersion', 'getPolicyRevision', 'getRelease']);
    const keyed = idKeyed.filter((route) => route.operationId === 'getPolicyRevision');

    const page = corpusDocument('policy-revisions-list-after-publish');
    const pageItems = (page.body as { items?: readonly { id?: unknown }[] } | undefined)?.items;
    if (pageItems === undefined) {
      throw new Error('the post-publish revision page carries no items array');
    }
    const pageIds = pageItems.map((item) => item.id);

    for (const route of keyed) {
      const parameter = route.idKeyParameter;
      if (parameter === undefined) {
        throw new Error(`'${route.operationId}' holds an empty idKeyParameter`);
      }
      expect(
        route.path,
        `'${route.operationId}' declares idKeyParameter '${parameter}' but its template carries no {${parameter}}`,
      ).toContain(`{${parameter}}`);

      const ids = CORPUS.map(readDocument)
        .filter((document) => document.operation_id === route.operationId && document.status < 400)
        .map((document) => {
          const body = document.body;
          const id = isRecord(body) ? body['id'] : undefined;
          expect(
            typeof id,
            `a '${route.operationId}' 2xx document carries no string body.id, so a request for its id cannot find it`,
          ).toBe('string');
          return id as string;
        });

      expect(
        ids.length,
        `'${route.operationId}' has ${ids.length} addressable document(s); two are needed for a revision comparison`,
      ).toBeGreaterThan(1);
      expect(
        new Set(ids).size,
        `'${route.operationId}' answers ${ids.length} documents with only ${new Set(ids).size} distinct body.id values: the id-keyed read would be ambiguous`,
      ).toBe(ids.length);
      expect(
        [...ids].sort(),
        `the ids '${route.operationId}' can answer differ from the revision ids the corpus page offers, so a page load would fall back to the variant`,
      ).toEqual(pageIds.map(String).sort());
    }
  });

  it('pins the cursor-keyed CI-binding and run-list walks to their corpus pages (09.7.1-F15, 09.3.1-T1)', () => {
    // A cursor-keyed route answers the catalog row whose `cursor` equals the request's cursor, so
    // Load more reaches a terminal page in a browser. It is only sound when (a) every keyed row
    // names the contract's cursor parameter, (b) each cursor-declaring row continues a page that
    // names it (same project, disjoint ids, a cursor the server could issue) and (c) it ends the
    // walk. The lists are exhaustive: a new cursor needs its own pin; one line each holds the cap.
    const keyed = FIXTURE_ROUTE_ROWS.filter((route) => route.cursorQueryParameter !== undefined);
    // prettier-ignore
    expect(
      keyed.map((route) => route.operationId),
      'each cursor-keyed route needs its own page-continuation pin here',
    ).toEqual(['listProjectAiSettingsVersions', 'listSecurityEvents', 'listCases', 'listCiBindings', 'listRuns', 'listRunAttempts']);
    for (const route of keyed) {
      expect(
        route.cursorQueryParameter,
        `'${route.operationId}' declares an empty cursorQueryParameter`,
      ).toBe('cursor');
    }

    const paged = FIXTURE_CATALOG.filter((entry) => entry.cursor !== undefined);
    expect(
      paged.map((entry) => entry.name),
      'each cursor-keyed walk has exactly one catalog row declaring the request cursor it answers',
    ).toEqual([
      'ci-bindings-list-settings-page-2',
      'run-attempts-page-2',
      'run-list-page-2',
      'security-events-list-page-2',
      'catalog-cases-page-2',
      'ai-settings-versions-page-2',
    ]);
    const secondPageRow = paged.find((entry) => entry.name === 'ci-bindings-list-settings-page-2');
    if (secondPageRow === undefined || secondPageRow.cursor === undefined) {
      throw new Error(
        'the second CI-binding page declares no cursor, so Load more cannot answer it',
      );
    }
    const cursor = secondPageRow.cursor;

    const first = corpusDocument('ci-bindings-list-settings-page-1');
    const second = corpusDocument('ci-bindings-list-settings-page-2');
    expect([first.operation_id, first.variant, first.status]).toEqual([
      'listCiBindings',
      'settings-page-1',
      200,
    ]);
    expect([second.operation_id, second.variant, second.status]).toEqual([
      'listCiBindings',
      'settings-page-2',
      200,
    ]);

    const firstBody = first.body as {
      items: CiBindingRowShape[];
      next_cursor: string | null;
    };
    const secondBody = second.body as {
      items: CiBindingRowShape[];
      next_cursor: string | null;
    };
    expect(firstBody.items.length, 'the first page holds no binding to click past').toBeGreaterThan(
      0,
    );
    expect(secondBody.items.length, 'the second page holds no binding to append').toBeGreaterThan(
      0,
    );
    expect(
      firstBody.next_cursor,
      'the first page must name exactly the cursor the backend answers, or Load more 501s',
    ).toBe(cursor);
    expect(secondBody.next_cursor, 'the last page must end the walk').toBeNull();

    // A real keyset cursor over `(created_at, id)` of the row it follows — `<utc ticks>-<uuid>` —
    // not an opaque string a test made up.
    const lastOfFirstPage = firstBody.items[firstBody.items.length - 1];
    if (lastOfFirstPage === undefined) {
      throw new Error('the first CI-binding page is empty');
    }
    expect(cursor, 'the cursor must be the keyset position of the row it follows').toBe(
      `${keysetTicks(lastOfFirstPage.created_at)}-${lastOfFirstPage.id}`,
    );

    const rows = [...firstBody.items, ...secondBody.items];
    const ids = rows.map((item) => item.id);
    expect(
      new Set(ids).size,
      'a binding id appears on both pages, so the store would drop the second copy silently',
    ).toBe(ids.length);
    expect(
      [...new Set(rows.map((item) => item.project_id))],
      'both pages must belong to the Storefront web project the tab read',
    ).toEqual([STOREFRONT_PROJECT_ID]);
    expect(
      new Set(firstBody.items.map((item) => item.status)),
      'the first page must still cover every verification state',
    ).toEqual(new Set(['active', 'pending_verification', 'disabled']));
    // An activation can never precede the binding's creation: the OIDC handshake that activates a
    // pending binding happens after it exists. Same-second RFC 3339 UTC instants compare as strings.
    for (const row of rows) {
      if (row.activated_at === undefined || row.activated_at === null) {
        continue;
      }
      expect(
        row.activated_at >= row.created_at,
        `${row.id} is activated at ${row.activated_at}, before it was created at ${row.created_at}`,
      ).toBe(true);
    }
    expect(oneTimeSecretMembers(second.body)).toEqual([]);

    // 09.3.1-T1: the second cursor-keyed walk. `listRuns` answers `run-list-page-2` for the
    // `next_cursor` `run-list-page-1` names, so the Runs list's Load more appends a second page
    // instead of being answered with page 1 again (which the store refuses as a repeated cursor).
    const runSecondPageRow = paged.find((entry) => entry.name === 'run-list-page-2');
    if (runSecondPageRow === undefined || runSecondPageRow.cursor === undefined) {
      throw new Error('the second run-list page declares no cursor, so Load more cannot answer it');
    }
    const runCursor = runSecondPageRow.cursor;

    const runFirst = corpusDocument('run-list-page-1');
    const runSecond = corpusDocument('run-list-page-2');
    expect([runFirst.operation_id, runFirst.variant, runFirst.status]).toEqual([
      'listRuns',
      'page-1',
      200,
    ]);
    expect([runSecond.operation_id, runSecond.variant, runSecond.status]).toEqual([
      'listRuns',
      'page-2',
      200,
    ]);

    const runFirstBody = runFirst.body as {
      items: RunRowShape[];
      next_cursor: string | null;
    };
    const runSecondBody = runSecond.body as {
      items: RunRowShape[];
      next_cursor: string | null;
    };
    expect(runFirstBody.items.length, 'the first page holds no run to click past').toBeGreaterThan(
      0,
    );
    expect(runSecondBody.items.length, 'the second page holds no run to append').toBeGreaterThan(0);
    expect(
      runFirstBody.next_cursor,
      'the first run page must name exactly the cursor the backend answers, or Load more 501s',
    ).toBe(runCursor);
    expect(runSecondBody.next_cursor, 'the last run page must end the walk').toBeNull();

    const lastRunOfFirstPage = runFirstBody.items[runFirstBody.items.length - 1];
    if (lastRunOfFirstPage === undefined) {
      throw new Error('the first run-list page is empty');
    }
    expect(runCursor, 'the run cursor must be the keyset position of the row it follows').toBe(
      `${keysetTicks(lastRunOfFirstPage.created_at)}-${lastRunOfFirstPage.run_id}`,
    );

    const runIds = [...runFirstBody.items, ...runSecondBody.items].map((item) => item.run_id);
    expect(
      new Set(runIds).size,
      'a run appears on both pages, so the store would drop the second copy silently',
    ).toBe(runIds.length);

    // 09.3.1-T4: the third cursor-keyed walk, the run detail's attempts keyset.
    const attemptsSecondRow = paged.find((entry) => entry.name === 'run-attempts-page-2');
    const attemptsFirst = corpusDocument('run-attempts-page-1').body as RunAttemptPage;
    const attemptsSecond = corpusDocument('run-attempts-page-2').body as RunAttemptPage;
    expect(attemptsFirst.next_cursor, 'page 1 must name the cursor page 2 answers').toBe(
      attemptsSecondRow?.cursor,
    );
    expect(attemptsSecond.next_cursor, 'the last attempts page must end the walk').toBeNull();
    const attemptKeys = [...attemptsFirst.items, ...attemptsSecond.items].map(
      (item) => `${item.execution_key}/${item.attempt_no}`,
    );
    expect(new Set(attemptKeys).size, 'an attempt appears on both pages').toBe(attemptKeys.length);
    // The keyset order is (execution_key, attempt_no) ascending across both pages.
    expect(attemptKeys, 'attempts are not in keyset order').toEqual([...attemptKeys].sort());
  });

  it('gives every cursor a catalog row that answers it (09.3.1-T1)', () => {
    const keyedOperations = new Set(
      FIXTURE_ROUTE_ROWS.filter((route) => route.cursorQueryParameter !== undefined).map(
        (route) => route.operationId,
      ),
    );
    const declared = new Set<string>();
    for (const entry of FIXTURE_CATALOG) {
      if (entry.cursor !== undefined) {
        declared.add(entry.cursor);
      }
    }

    // A page that names a cursor no row declares asks the backend for a document it does not have;
    // the backend answers the variant's page again, and the store refuses that second answer as a
    // repeated cursor. A plain visit (no `?fixture.variant.`) has to survive it: the `ok` page of a
    // cursor-keyed operation therefore either names a cursor a row answers, or ends the walk.
    const unanswered: string[] = [];
    for (const fixture of CORPUS) {
      const document = readDocument(fixture);
      if (!keyedOperations.has(document.operation_id)) {
        continue;
      }
      const body = document.body as { next_cursor?: string | null } | undefined;
      const next = body?.next_cursor ?? null;
      if (next !== null && !declared.has(next)) {
        unanswered.push(`fixtures/web-api/${fixture.name} names ${next}`);
      }
    }

    expect(
      unanswered,
      'a page names a cursor no catalog row declares, so following it re-answers the same page',
    ).toEqual([]);
  });

  it('pins at most one ok variant per operation and the 403 and 412 error fixtures (AC10)', () => {
    // The served set: a routed operation must resolve its variants here.
    const documents = CATALOG.map((entry) => ({ name: entry.name, document: entry.document }));

    const variantsByOperation = new Map<string, string[]>();
    for (const { name, document } of documents) {
      const variants = variantsByOperation.get(document.operation_id) ?? [];
      expect(
        variants,
        `fixtures/web-api/${name} repeats variant '${document.variant}' of '${document.operation_id}'`,
      ).not.toContain(document.variant);
      variants.push(document.variant);
      variantsByOperation.set(document.operation_id, variants);
    }

    for (const [operationId, variants] of variantsByOperation) {
      const defaults = variants.filter((variant) => variant === 'ok');
      expect(
        defaults.length,
        `operation '${operationId}' has ${defaults.length} 'ok' fixtures (variants: ${variants.join(', ')}); the answer the backend serves when no toggle is set must be pinned at most once`,
      ).toBeLessThanOrEqual(1);
    }

    // Every routed operation has at least one fixture, and an operation with no 'ok' fixture may
    // only pin error states: the T12.1 corpus carries `updateProject` as its 412 alone (the
    // precondition-failed state 09.2.1 needs), and the backend answers the un-pinned request with
    // `501 fixture_not_found` rather than a fabricated success body. An operation that lost its
    // `ok` while still pinning a 2xx is drift and fails here.
    for (const route of FIXTURE_ROUTE_ROWS) {
      const variants = variantsByOperation.get(route.operationId);
      expect(
        variants,
        `operation '${route.operationId}' is routed but has no fixture`,
      ).toBeDefined();
      if (variants === undefined || variants.includes('ok')) {
        continue;
      }
      const statuses = documents
        .filter(({ document }) => document.operation_id === route.operationId)
        .map(({ document }) => document.status);
      expect(
        statuses.every((status) => status >= 400),
        `operation '${route.operationId}' has no 'ok' fixture but pins the success status(es) ${statuses.join(', ')}`,
      ).toBe(true);
    }

    const forbidden = documents.filter(({ document }) => document.status === 403);
    expect(
      forbidden.length,
      'no fixture pins a 403, so the shell has no refused state to render (AC10)',
    ).toBeGreaterThan(0);
    expect(
      forbidden.map(({ document }) => document.variant),
      "the 403 fixture must be the selectable 'forbidden' variant",
    ).toContain('forbidden');

    const preconditionFailed = documents.filter(({ document }) => document.status === 412);
    expect(
      preconditionFailed.length,
      'no fixture pins a 412, so the shell has no precondition-failed state to render (AC10)',
    ).toBeGreaterThan(0);
    expect(
      preconditionFailed.map(({ document }) => document.variant),
      "the 412 fixture must be the selectable 'precondition_failed' variant",
    ).toContain('precondition_failed');
  });

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
