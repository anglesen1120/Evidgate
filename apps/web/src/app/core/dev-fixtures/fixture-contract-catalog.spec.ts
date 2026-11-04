// Catalog and route integrity: the explicit catalog list names exactly the corpus directories it
// serves, and every routed operation matches the contract's method and path. Readers come from the
// corpus helper and the contract helper, so this family only states what must line up.

import { CATALOG, CORPUS, FIXTURE_ROUTE_ROWS, readDocument } from './fixture-contract.corpus';
import { VALIDATOR } from './fixture-contract.support';

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('keeps FIXTURE_CATALOG in step with the corpus directory', () => {
    const corpusByName = new Map(CORPUS.map((fixture) => [fixture.name, fixture] as const));
    const catalogNames = CATALOG.map((entry) => entry.name);

    expect(
      new Set(catalogNames).size,
      `FIXTURE_CATALOG lists a fixture twice: ${catalogNames.join(', ')}`,
    ).toBe(catalogNames.length);
    // The decision and evidence-revision documents are typed modules
    // (`./decision-fixtures`, `./evidence-revision-fixtures`) rather than corpus directories until
    // the reconciled `Decision` schema lands — a `pending` body needs `reasons: null`, which today's
    // non-nullable `Decision.reasons` refuses. They are exempt from the directory equality and from
    // the body check above, and the reconciliation deletes this list together with the modules.
    const TYPED_MODULE_DOCUMENTS: readonly string[] = [
      'run-decision',
      'run-decision-evaluated-fail-missing-shard',
      'run-decision-evaluated-pass',
      'run-decision-shadow-refused',
      'run-decision-unavailable',
      'run-evidence-revisions',
      'run-evidence-revisions-empty',
      'run-evidence-revisions-unavailable',
    ];
    const fromModules = new Set(TYPED_MODULE_DOCUMENTS);
    expect(
      [...catalogNames].filter((name) => !fromModules.has(name)).sort(),
      'FIXTURE_CATALOG and fixtures/web-api hold different fixtures — the explicit input.json imports drifted from the corpus',
    ).toEqual([...corpusByName.keys()].sort());
    expect(
      catalogNames.filter((name) => fromModules.has(name)).sort(),
      'FIXTURE_CATALOG no longer serves one of the typed-module documents',
    ).toEqual([...fromModules].sort());

    for (const entry of CATALOG) {
      if (fromModules.has(entry.name)) {
        continue;
      }
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
