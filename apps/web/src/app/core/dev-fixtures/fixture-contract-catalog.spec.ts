// Catalog and route integrity: the explicit catalog list names exactly the corpus directories it
// serves, and every routed operation matches the contract's method and path. Readers come from the
// corpus helper and the contract helper, so this family only states what must line up.

import { FIXTURE_CATALOG } from './fixture-catalog';
import { CORPUS, FIXTURE_ROUTE_ROWS, readDocument } from './fixture-contract.corpus';
import { VALIDATOR, isRecord } from './fixture-contract.support';

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('keeps FIXTURE_CATALOG in step with the corpus directory', () => {
    const corpusByName = new Map(CORPUS.map((fixture) => [fixture.name, fixture] as const));
    const catalogNames = FIXTURE_CATALOG.map((entry) => entry.name);

    expect(
      new Set(catalogNames).size,
      `FIXTURE_CATALOG lists a fixture twice: ${catalogNames.join(', ')}`,
    ).toBe(catalogNames.length);
    // A derived row has no corpus directory by construction, so only the imported rows are
    // compared; the derived ones are pinned below, which is what keeps this check from hiding one.
    const imported = FIXTURE_CATALOG.filter((entry) => entry.derived !== true);
    expect(
      [...imported.map((entry) => entry.name)].sort(),
      'FIXTURE_CATALOG and fixtures/web-api hold different fixtures — the explicit input.json imports drifted from the corpus',
    ).toEqual([...corpusByName.keys()].sort());

    for (const entry of imported) {
      const fixture = corpusByName.get(entry.name);
      if (fixture === undefined) {
        throw new Error(
          `FIXTURE_CATALOG names '${entry.name}', which fixtures/web-api does not hold`,
        );
      }
      expect(
        entry.document,
        `FIXTURE_CATALOG['${entry.name}'] no longer equals fixtures/web-api/${entry.name}/input.json — re-import it`,
      ).toEqual(readDocument(fixture));
    }

    // Derived documents have no corpus directory, so every added row is pinned explicitly.
    const derived = FIXTURE_CATALOG.filter((entry) => entry.derived === true);
    expect(
      derived.map((entry) => entry.name),
      'a new derived row needs its own pin here',
    ).toEqual([
      'run-detail-canceled',
      'run-detail-many-producers',
      'run-detail-no-producers',
      'run-detail-release',
      'run-detail-releases',
      'run-detail-release-refused',
      'run-detail-with-history',
      'release-detail',
      'release-detail-second',
      'release-detail-regular-run',
      'release-detail-forbidden',
      'release-detail-not-found',
    ]);
    const readyFixture = corpusByName.get('run-detail');
    if (readyFixture === undefined) {
      throw new Error('fixtures/web-api holds no run-detail document to derive from');
    }
    const ready = readDocument(readyFixture);
    expect(derived[0]?.document).toEqual({
      ...ready,
      variant: 'detail-canceled',
      body: { ...(ready.body as object), state: 'canceled', releases: [] },
    });

    // Every member the producer rows do not replace, so the pin still compares the whole body.
    const untouched = (body: object): Record<string, unknown> => {
      const members = { ...(body as Record<string, unknown>) };
      delete members['declared_producers'];
      delete members['shard_count'];
      return members;
    };
    const many = derived[1]?.document;
    const manyBody: unknown = many?.body;
    if (!isRecord(manyBody)) {
      throw new Error('the fifty-slot row carries no object body to pin');
    }
    const declared: unknown = manyBody['declared_producers'];
    if (!Array.isArray(declared)) {
      throw new Error('the fifty-slot row declares no producer array');
    }
    const manyIds: unknown[] = declared;
    expect(many?.variant).toBe('detail-many-producers');
    expect(manyIds).toHaveLength(50);
    expect(manyIds[0]).toBe('shard-1');
    expect(manyIds[49]).toBe('shard-50');
    expect(new Set(manyIds).size, 'the fifty slots are not distinct ids').toBe(50);
    expect(manyBody['shard_count']).toBe(50);
    expect(untouched(manyBody)).toEqual(untouched(ready.body as object));
    expect(derived[2]?.document).toEqual({
      ...ready,
      variant: 'detail-no-producers',
      body: { ...(ready.body as object), declared_producers: [], shard_count: 0 },
    });
    for (const entry of derived.slice(3)) {
      const document = entry.document;
      expect(
        VALIDATOR.validateResponse(document.operation_id, document.status, document.body).errors,
        entry.name,
      ).toEqual([]);
    }
  });

  it('pins the method and path template FIXTURE_ROUTES declares for every fixture operation', () => {
    expect(
      FIXTURE_ROUTE_ROWS.length,
      'FIXTURE_ROUTES is empty, so the fixture backend answers no request',
    ).toBeGreaterThan(0);

    const routed = new Set<string>();
    for (const route of FIXTURE_ROUTE_ROWS) {
      const operation = VALIDATOR.operation(route.operationId);
      if (operation === undefined) {
        throw new Error(
          `contracts/openapi.yaml declares no operation with operationId '${route.operationId}'`,
        );
      }
      expect(
        operation.method.toUpperCase(),
        `FIXTURE_ROUTES sends '${route.operationId}' as ${route.method}; contracts/openapi.yaml declares ${operation.method}`,
      ).toBe(route.method.toUpperCase());
      expect(
        operation.path,
        `FIXTURE_ROUTES routes '${route.operationId}' at ${route.path}; contracts/openapi.yaml declares ${operation.path} (relative to the /v1 server url)`,
      ).toBe(route.path);
      expect(
        routed.has(route.operationId),
        `FIXTURE_ROUTES lists '${route.operationId}' twice`,
      ).toBe(false);
      routed.add(route.operationId);
    }

    for (const fixture of CORPUS) {
      const document = readDocument(fixture);
      expect(
        routed.has(document.operation_id),
        `fixtures/web-api/${fixture.name} answers '${document.operation_id}', which FIXTURE_ROUTES does not route`,
      ).toBe(true);
    }
  });
});
