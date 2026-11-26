// Selection pins keep id-keyed and cursor-keyed fixture reads addressable from their corpus pages.

import type { RunAttemptPage } from '@evidgate/sdk';

import { FIXTURE_CATALOG } from './fixture-catalog';

import {
  CATALOG,
  CORPUS,
  FIXTURE_ROUTE_ROWS,
  type RunRowShape,
  corpusDocument,
  keysetTicks,
  readDocument,
} from './fixture-contract.corpus';
import { isRecord } from './fixture-contract.support';

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

  it('pins cursor-keyed routes and run-list walks to their corpus pages', () => {
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
    ).toEqual(['listProjectAiSettingsVersions', 'listSecurityEvents', 'listCases', 'listRuns', 'listRunAttempts']);
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
      'run-attempts-page-2',
      'run-list-page-2',
      'security-events-list-page-2',
      'catalog-cases-page-2',
      'ai-settings-versions-page-2',
    ]);
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
});
