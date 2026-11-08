// The corpus and document checks: every `input.json` follows the file contract, every response
// validates against `contracts/openapi.yaml` and every answered header is declared there. The
// readers are shared from `./fixture-contract.corpus`, contract resolution from `.support`.

import { FIXTURE_ROUTES } from './fixture-routes';

import {
  CORPUS,
  corpusDocument,
  readDocument,
  undeclaredResponseHeaders,
} from './fixture-contract.corpus';
import {
  VALIDATOR,
  WEB_API_DOMAIN,
  WEB_API_FIXTURES,
  declaredResponseHeaderNames,
  describeErrors,
  isRecord,
} from './fixture-contract.support';

/** The `kind` every `fixtures/web-api/<name>/meta.json` must declare. */
const API_RESPONSE_KIND = 'api-response';

/**
 * Documents that answer a body the contract refuses, on purpose: the endpoint serves live rows only,
 * and the receiver must still refuse an off-contract shadow body. Each names the one member the
 * validator must reject, so the exemption cannot hide any other drift.
 */
const OFF_CONTRACT_BY_DESIGN: ReadonlyMap<string, string> = new Map([
  ['run-decision-shadow-refused', '/mode'],
]);

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('holds a handcrafted web-api/api-response corpus', () => {
    expect(CORPUS.length, `no fixture directories under ${WEB_API_FIXTURES}`).toBeGreaterThan(0);
    for (const fixture of CORPUS) {
      expect(fixture.meta.domain, `fixtures/web-api/${fixture.name}/meta.json domain`).toBe(
        WEB_API_DOMAIN,
      );
      expect(fixture.meta.kind, `fixtures/web-api/${fixture.name}/meta.json kind`).toBe(
        API_RESPONSE_KIND,
      );
    }
  });

  it('gives every fixture the input.json document shape the backend answers from', () => {
    expect(CORPUS.length).toBeGreaterThan(0);
    for (const fixture of CORPUS) {
      readDocument(fixture);
    }
  });

  it('pins run-detail to the current getRun response without invented manifest data', () => {
    const names = [
      'run-detail',
      'run-detail-not-found',
      'run-detail-forbidden',
      'run-detail-ref-format',
      'run-detail-null-evidence',
    ];
    expect(FIXTURE_ROUTES.find((route) => route.operationId === 'getRun')).toEqual({
      operationId: 'getRun',
      method: 'GET',
      path: '/runs/{r}',
    });
    for (const name of names) {
      const document = corpusDocument(name);
      expect(document.operation_id).toBe('getRun');
      expect(VALIDATOR.validateResponse('getRun', document.status, document.body).errors).toEqual(
        [],
      );
      if (document.status === 200) {
        expect(Object.keys(document.body as object).sort()).toEqual([
          'after_cancel_events',
          'current_evidence_revision_id',
          'declared_producers',
          'environment',
          'manifest_hash',
          'policy_revision_id',
          'producers',
          'purpose',
          'ref',
          'releases',
          'run_id',
          'shard_count',
          'state',
          'state_history',
          'suite_revision_id',
          'unverified_ref',
          'version',
        ]);
      }
    }
    expect(corpusDocument('run-detail-not-found').status).toBe(404);
    expect(corpusDocument('run-detail-forbidden').status).toBe(403);
    const absent = corpusDocument('run-detail-null-evidence').body;
    expect(isRecord(absent) && absent['current_evidence_revision_id'] === null).toBe(true);
    const formatted = corpusDocument('run-detail-ref-format').body;
    const ref = isRecord(formatted) ? formatted['ref'] : undefined;
    expect(isRecord(ref) && typeof ref['name'] === 'string' && ref['name'].includes('\u202e')).toBe(
      true,
    );
  });
  it('pins the suite revision read to the states the composition screen renders', () => {
    // The screen reads one pinned revision and nothing else, so every state it can show is one of
    // these documents: a draft matrix, the immutable published revision, a revision with no member,
    // and the three refusals. The wire carries no warnings member and no case names, so a 2xx body
    // that grew either would make the screen claim data the read never sends.
    const names = [
      'suite-revision',
      'suite-revision-published',
      'suite-revision-empty',
      'suite-revision-large',
      'suite-revision-not-found',
      'suite-revision-forbidden',
      'suite-revision-unavailable',
    ];
    expect(FIXTURE_ROUTES.find((route) => route.operationId === 'getSuiteRevision')).toEqual({
      operationId: 'getSuiteRevision',
      method: 'GET',
      path: '/suite-revisions/{r}',
    });
    for (const name of names) {
      const document = corpusDocument(name);
      expect(document.operation_id).toBe('getSuiteRevision');
      expect(
        VALIDATOR.validateResponse('getSuiteRevision', document.status, document.body).errors,
      ).toEqual([]);
      if (document.status !== 200) {
        continue;
      }
      expect(Object.keys(document.body as object).sort()).toEqual([
        'created_at',
        'edges',
        'environment_requirements',
        'id',
        'level',
        'members',
        'platform',
        'purpose',
        'retry_policy',
        'revision_hash',
        'revision_no',
        'shard_strategy',
        'status',
        'suite_id',
      ]);
    }
    expect(corpusDocument('suite-revision-not-found').status).toBe(404);
    expect(corpusDocument('suite-revision-forbidden').status).toBe(403);
    expect(corpusDocument('suite-revision-unavailable').headers['Retry-After']).toBeDefined();
    const published = corpusDocument('suite-revision-published').body;
    expect(isRecord(published) && published['status'] === 'published').toBe(true);
    const draft = corpusDocument('suite-revision').body;
    expect(isRecord(draft) && draft['status'] === 'draft').toBe(true);
    const empty = corpusDocument('suite-revision-empty').body;
    expect(isRecord(empty) && (empty['members'] as readonly unknown[]).length).toBe(0);
    // The oversized row exists for one reason: its case revisions × variants product is past the
    // 2500 cells a matrix may draw, so the browser case proves the flat table took over. A corpus
    // trimmed below that would leave the case asserting a layout the fixture no longer forces.
    const large = corpusDocument('suite-revision-large').body;
    const cells = (body: unknown): number => {
      if (!isRecord(body)) return 0;
      const members: unknown = body['members'];
      // A body whose `members` is not an array has no cell at all, so it reads as zero instead of
      // being counted through a cast that would throw on the first `.map`.
      if (!Array.isArray(members)) return 0;
      const placed = members.filter(isRecord);
      return (
        new Set(placed.map((member) => member['case_revision_id'])).size *
        new Set(placed.map((member) => member['variant_hash'])).size
      );
    };
    expect(cells(large)).toBeGreaterThan(2500);
  });
  // The envelope schema accepts any string `path`, so the generic loop below cannot tell `/version`
  // from the bare `version` the dialog already read — while `e2e/shell/conflict-dialog.spec.ts`
  // fails if this document drifts back to the bare name, or to a reason the policy store refuses as
  // malformed (`PolicyStore.loadBody` reads the identifier a detail names). Both are pinned here.
  it('serves the 412 concurrency token in the pointer spelling the dialog reads', () => {
    const document = corpusDocument('policy-revision-create-version-pointer');
    expect(document.operation_id).toBe('createPolicyRevision');
    expect(document.variant).toBe('version-pointer');
    expect(document.status).toBe(412);
    const body = document.body;
    if (!isRecord(body) || !isRecord(body['error'])) {
      throw new Error('the pointer-form document carries no error envelope');
    }
    const error = body['error'];
    expect(error['code']).toBe('precondition_failed');
    const details: unknown = error['details'];
    if (!Array.isArray(details)) {
      throw new Error('the 412 envelope carries no details[] array');
    }
    const detailList: unknown[] = details;
    expect(
      detailList.map((detail) => (isRecord(detail) ? detail['path'] : undefined)),
      'the details are the authoring stale pair: the If-Match snapshot plus the pointer /version',
    ).toEqual(['/If-Match', '/version']);
    const pointer = detailList[1];
    const reason = isRecord(pointer) ? pointer['reason'] : undefined;
    expect(
      reason,
      'the pointer detail must carry a revision id the conflict dialog can reload',
    ).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  });

  for (const fixture of CORPUS) {
    it(`validates fixtures/web-api/${fixture.name} against contracts/openapi.yaml`, () => {
      const document = readDocument(fixture);
      const result = VALIDATOR.validateResponse(
        document.operation_id,
        document.status,
        document.body,
      );
      const refusedMember = OFF_CONTRACT_BY_DESIGN.get(fixture.name);
      if (refusedMember !== undefined) {
        expect(
          result.errors.map((error) => error.path),
          `fixtures/web-api/${fixture.name} must be refused for exactly ${refusedMember}`,
        ).toEqual([refusedMember]);
        return;
      }
      expect(
        result.errors,
        `fixtures/web-api/${fixture.name} (${document.operation_id} ${document.status}, variant '${document.variant}') no longer matches contracts/openapi.yaml: ${describeErrors(result.errors)}`,
      ).toEqual([]);
    });

    it(`answers fixtures/web-api/${fixture.name} with headers contracts/openapi.yaml declares`, () => {
      const document = readDocument(fixture);
      const declared = declaredResponseHeaderNames(document.operation_id, document.status);
      // 00 §9.1: every response carries `X-Request-Id`, so a fixture answering none would make
      // the header check below pass without checking a single name.
      expect(
        Object.keys(document.headers).length,
        `fixtures/web-api/${fixture.name} (${document.operation_id} ${document.status}) answers with no header at all; every response of §9.1 carries X-Request-Id`,
      ).toBeGreaterThan(0);
      expect(
        declared.length,
        `contracts/openapi.yaml declares no header for the ${document.status} response of '${document.operation_id}', so fixtures/web-api/${fixture.name} cannot answer one`,
      ).toBeGreaterThan(0);

      const undeclared = undeclaredResponseHeaders(document);
      expect(
        undeclared,
        `fixtures/web-api/${fixture.name} answers with header(s) ${undeclared.join(', ')} that contracts/openapi.yaml does not declare for the ${document.status} response of '${document.operation_id}' (declared: ${declared.join(', ')})`,
      ).toEqual([]);
    });
  }
});
