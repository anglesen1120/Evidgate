// Catalog and route integrity: the explicit catalog list names exactly the corpus directories it
// serves, and every routed operation matches the contract's method and path. Readers come from the
// corpus helper and the contract helper, so this family only states what must line up.

import { FIXTURE_CATALOG } from './fixture-catalog';
import { CORPUS, FIXTURE_ROUTE_ROWS, readDocument } from './fixture-contract.corpus';
import { VALIDATOR } from './fixture-contract.support';

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

    // The derived rows: each one is a corpus document with the members it shows replaced, never a
    // stand-alone body. Today that is the canceled run, which `GET /v1/runs/{r}` keeps in `state`.
    const derived = FIXTURE_CATALOG.filter((entry) => entry.derived === true);
    expect(
      derived.map((entry) => entry.name),
      'a second derived row needs its own pin here',
    ).toEqual(['run-detail-canceled']);
    const readyFixture = corpusByName.get('run-detail');
    if (readyFixture === undefined) {
      throw new Error('fixtures/web-api holds no run-detail document to derive from');
    }
    const ready = readDocument(readyFixture);
    expect(derived[0]?.document).toEqual({
      ...ready,
      variant: 'detail-canceled',
      body: { ...(ready.body as object), state: 'canceled' },
    });
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
