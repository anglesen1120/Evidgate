// 09.1.1-T12.2 (plan §9 "Fixture backend is dev-only and contract-valid"; AC10): the fixture
// corpus is handcrafted JSON, so nothing but this spec stops it drifting from
// `contracts/openapi.yaml` — a renamed member or a dropped `required` field would surface as a
// broken fixture-backed screen instead of a red suite. The dev-only fixture backend of
// `apps/web` answers requests from exactly two places, and both are pinned here:
//
//   1. `fixtures/web-api/<name>/input.json` — every response body validates against the
//      response schema the contract declares for its `operation_id` + `status`, through the
//      same Ajv 2020-12 + ajv-formats configuration the server-side contract tests use
//      (`createOpenApiResponseValidator` from `@evidgate/cli`, T12.2 slice A), and every
//      response header it answers with is declared in the `headers` object of that same
//      (operation, status) response — the corpus only ever answers `X-Request-Id`, which
//      00 §9.1 requires on every response;
//   2. `FIXTURE_CATALOG` (the explicit `import … input.json` list the backend serves) names
//      exactly the corpus directories and holds each parsed `input.json`, so the two cannot
//      drift; `FIXTURE_ROUTES` carries the contract's method and path template per operation.
//
// The status/variant pins the shell's loading and error states rely on are asserted too: no
// operation pins two `ok` fixtures, every routed operation has a fixture, and an operation
// without an `ok` fixture may only pin error states (`updateProject` is the corpus's 412 alone),
// plus the 403 and 412 fixtures AC10 names. The 01.6.1-T3 corpus shape (`web-api` /
// `api-response`) is checked as well.
//
// 09.2.1-T2 added step 2's write path and its pin: the two one-time secret members of the whole
// contract (`binding_challenge`, `secret`) may appear in the three documents whose operation mints
// one and in no other, which is "metadata only after the reveal" held on the wire rather than in a
// component.
//
// The last three tests are the negative controls of test 3 and of the header check above: they
// take the `runs-list` page, break it the way a contract change would (a removed `required` run
// member, a non-uuid uuid) and answer a header the contract does not declare, each of which must
// be reported. Without them "every fixture validates" could pass while the check validated
// nothing — AC10's done-when is precisely "fails on a fixture with a removed required field".
//
// Read-only and offline: the contract and the corpus are read from the repository with
// `node:fs`, exactly like `../http/contract-smoke.spec.ts`. No `fetch`, no `HttpClient`, no
// network — the validator is the `packages/cli` one, never the app's HTTP stack.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { createOpenApiResponseValidator, type ContractValidationError } from '@evidgate/cli';
import { loadAllFixtures, type Fixture } from '@evidgate/test-fixtures';
import { parse as parseYaml } from 'yaml';

import { FIXTURE_CATALOG } from './fixture-catalog';
import { FIXTURE_ROUTES } from './fixture-routes';

/** The corpus domain and kind of the dev-only web API fixtures (T12.1). */
const WEB_API_DOMAIN = 'web-api';
const API_RESPONSE_KIND = 'api-response';

/** The fixture and operation the negative control mutates. */
const RUNS_LIST_FIXTURE = 'runs-list';
const LIST_RUNS_OPERATION = 'listRuns';

/** A string the contract's `format: uuid` refuses, used by the second negative control. */
const NOT_A_UUID = 'not-a-uuid';

/** A header name no response of `contracts/openapi.yaml` declares, used by the third one. */
const UNDECLARED_HEADER = 'X-Fixture-Undeclared';

/**
 * 09.2.1-T2: the two body members a step-2 response can carry a one-time secret in —
 * `CiBindingCreatedResponse.binding_challenge` and `IngestTokenCreatedResponse.secret`
 * (contracts/openapi.yaml:6607-6615, 6708-6719). Only the three creates the wizard's step 2 calls
 * answer with them; no list or detail read may carry either, which is what AC2's "metadata only on
 * resume" reduces to on the wire. (The contract's other secret-bearing body is the CI-side
 * `CiTokenResponse.token`, answered by `POST /v1/ci/token` — a pre-tenant route the browser never
 * calls, `contracts/openapi.yaml:71-81`, kept out of the web by decision (2) of the task's §10
 * entry.)
 */
const ONE_TIME_SECRET_MEMBERS: readonly string[] = ['binding_challenge', 'secret'];

/** The operations that mint one, and the only ones allowed to answer with a secret member. */
const SECRET_MINTING_OPERATIONS: readonly string[] = [
  'createCiBinding',
  'createIngestToken',
  'rotateIngestToken',
];

/** The seed page that derives step 3 from a token: one active token, its metadata and no secret. */
const ONBOARDING_TOKEN_FIXTURE = 'ingest-tokens-list-onboarding-token';

/**
 * How many `$ref` hops a response or header object may take before the check calls it
 * unresolved — the same bound `packages/cli`'s response validator uses.
 */
const MAX_REFERENCE_HOPS = 16;

/** The exact member names of the T12.1 `input.json` document contract. */
const DOCUMENT_MEMBERS: readonly string[] = [
  'operation_id',
  'variant',
  'status',
  'headers',
  'latency_ms',
  'body',
];

/** Every member but `body`, which a bodyless response omits. */
const REQUIRED_DOCUMENT_MEMBERS: readonly string[] = [
  'operation_id',
  'variant',
  'status',
  'headers',
  'latency_ms',
];

/**
 * One `fixtures/web-api/<name>/input.json` document. Declared structurally (rather than
 * imported from `./fixture-catalog`) so this spec asserts the shape the backend must consume
 * and does not inherit whatever the catalog happens to declare.
 *
 * The `evidgate/no-hand-written-wire-dto` exemption is the same narrow one `fixture-catalog.ts`
 * carries: `operation_id` and `latency_ms` are the 01.6.1-T3 *fixture file format* shared with
 * the .NET loader, not a response body the app reads, and there is no SDK type for it.
 */
/* eslint-disable evidgate/no-hand-written-wire-dto -- the 01.6.1-T3 fixture file format (see above). */
interface FixtureDocumentShape {
  readonly operation_id: string;
  readonly variant: string;
  readonly status: number;
  readonly headers: Record<string, string>;
  readonly latency_ms: number;
  readonly body?: unknown;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * The `FIXTURE_ROUTES` row shape, declared structurally for the same reason: the sibling module
 * owns its exported type name, this spec owns what it must satisfy.
 */
interface FixtureRouteShape {
  readonly operationId: string;
  readonly method: string;
  readonly path: string;
}

/**
 * The repository root. `ng test` runs with `apps/web` as its working directory (the same fact
 * `../http/contract-smoke.spec.ts` relies on), but a native Vitest run may start anywhere, so
 * the root is the nearest ancestor that holds `contracts/openapi.yaml`.
 */
function findRepositoryRoot(): string {
  let directory = process.cwd();
  for (;;) {
    if (existsSync(join(directory, 'contracts', 'openapi.yaml'))) {
      return directory;
    }
    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error(
        `cannot locate the repository root (the directory holding contracts/openapi.yaml) from ${process.cwd()}`,
      );
    }
    directory = parent;
  }
}

const REPOSITORY_ROOT = findRepositoryRoot();
const CONTRACT_PATH = join(REPOSITORY_ROOT, 'contracts', 'openapi.yaml');
const WEB_API_FIXTURES = join(REPOSITORY_ROOT, 'fixtures', WEB_API_DOMAIN);

/** The parsed contract; the validator resolves its own local `#/components/...` refs. */
const CONTRACT_DOCUMENT: unknown = parseYaml(readFileSync(CONTRACT_PATH, 'utf8'));
const VALIDATOR = createOpenApiResponseValidator(CONTRACT_DOCUMENT);

/**
 * The corpus, loaded once with the shared 01.6.1-T3 loader — the same loader the .NET side
 * mirrors, so a malformed fixture directory refuses here instead of being skipped.
 */
const CORPUS: readonly Fixture[] = loadAllFixtures(WEB_API_FIXTURES);

/** `FIXTURE_CATALOG` seen through the document contract this spec asserts. */
const CATALOG: readonly { readonly name: string; readonly document: FixtureDocumentShape }[] =
  FIXTURE_CATALOG;

/** `FIXTURE_ROUTES` seen through the route contract this spec asserts. */
const FIXTURE_ROUTE_ROWS: readonly FixtureRouteShape[] = FIXTURE_ROUTES;

/** The one shape guard this spec needs for two documents it does not own. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/**
 * Every one-time secret member a response body carries, at any depth: a member named in
 * `ONE_TIME_SECRET_MEMBERS` whose value is not `null` (the contract types both as a plain
 * `string`, so a `null` is a body the schema refuses and the validation test reports it).
 */
function oneTimeSecretMembers(value: unknown): readonly string[] {
  if (Array.isArray(value)) {
    return value.flatMap((item) => oneTimeSecretMembers(item));
  }
  if (!isRecord(value)) {
    return [];
  }
  return Object.entries(value).flatMap(([member, memberValue]) => [
    ...(ONE_TIME_SECRET_MEMBERS.includes(member) && memberValue !== null ? [member] : []),
    ...oneTimeSecretMembers(memberValue),
  ]);
}

/** Resolve a local `#/...` JSON pointer inside `contracts/openapi.yaml`. */
function resolveContractPointer(reference: string): unknown {
  if (!reference.startsWith('#/')) {
    throw new Error(
      `contracts/openapi.yaml uses a non-local $ref (${reference}); the fixture contract check only follows local pointers`,
    );
  }
  return reference
    .slice(2)
    .split('/')
    .reduce<unknown>(
      (node, token) =>
        isRecord(node) ? node[token.replace(/~1/g, '/').replace(/~0/g, '~')] : undefined,
      CONTRACT_DOCUMENT,
    );
}

/**
 * Follow a local `$ref` chain (`'200': {$ref: '#/components/responses/…'}`, `X-Request-Id:
 * {$ref: '#/components/headers/…'}`) until a real node remains, the way the response validator
 * follows response refs. A `$ref` the document cannot resolve (a non-local file, a pointer to
 * nothing) or a chain longer than `MAX_REFERENCE_HOPS` throws, naming the member being read.
 */
function derefContractNode(node: unknown, member: string): unknown {
  let current = node;
  let reference = isRecord(current) ? current['$ref'] : undefined;
  for (let hops = 0; typeof reference === 'string'; hops += 1) {
    if (hops >= MAX_REFERENCE_HOPS) {
      throw new Error(
        `contracts/openapi.yaml: ${member} follows more than ${MAX_REFERENCE_HOPS} $ref hops; the header check cannot resolve it`,
      );
    }
    current = resolveContractPointer(reference);
    reference = isRecord(current) ? current['$ref'] : undefined;
  }
  return current;
}

/** The operation object of `contracts/openapi.yaml` carrying `operationId`. */
function operationNode(operationId: string): Record<string, unknown> {
  const paths = isRecord(CONTRACT_DOCUMENT) ? CONTRACT_DOCUMENT['paths'] : undefined;
  if (!isRecord(paths)) {
    throw new Error('contracts/openapi.yaml declares no paths object');
  }
  for (const pathItem of Object.values(paths)) {
    if (!isRecord(pathItem)) {
      continue;
    }
    for (const operation of Object.values(pathItem)) {
      if (isRecord(operation) && operation['operationId'] === operationId) {
        return operation;
      }
    }
  }
  throw new Error(
    `contracts/openapi.yaml declares no operation with operationId '${operationId}', so the header check has no response to read`,
  );
}

/**
 * The header names the contract declares for the response `operationId` answers with `status`.
 * The response object is followed through its `$ref` (the error responses live in
 * `components.responses`) and every header through its own `$ref`, so a broken reference fails
 * here instead of silently leaving the response with no declared headers.
 */
function declaredResponseHeaderNames(operationId: string, status: number): readonly string[] {
  const context = `the ${status} response of '${operationId}'`;
  const responses = operationNode(operationId)['responses'];
  if (!isRecord(responses)) {
    throw new Error(`contracts/openapi.yaml: '${operationId}' declares no responses object`);
  }
  // The response validator reads a numeric status the same way: the declared code, else `default`.
  const declaredResponse = responses[String(status)] ?? responses['default'];
  if (declaredResponse === undefined) {
    throw new Error(`contracts/openapi.yaml: ${context} is undeclared, so it declares no headers`);
  }

  const response = derefContractNode(declaredResponse, context);
  if (!isRecord(response)) {
    throw new Error(`contracts/openapi.yaml: ${context} is not a response object`);
  }
  const headers = response['headers'];
  if (headers === undefined) {
    return [];
  }
  if (!isRecord(headers)) {
    throw new Error(`contracts/openapi.yaml: the headers of ${context} are not an object`);
  }

  const names: string[] = [];
  for (const [name, header] of Object.entries(headers)) {
    if (derefContractNode(header, `the '${name}' header of ${context}`) === undefined) {
      throw new Error(
        `contracts/openapi.yaml: the '${name}' header of ${context} is an unresolvable $ref, so it is not a declared header`,
      );
    }
    names.push(name);
  }
  return names;
}

/**
 * The fixture's header names that the same (operation, status) response does not declare.
 * HTTP header names are case-insensitive, so the comparison is too: a fixture answering
 * `x-request-id` is as declared as one answering `X-Request-Id`. Empty means every header of
 * the fixture is part of the contract.
 */
function undeclaredResponseHeaders(document: FixtureDocumentShape): readonly string[] {
  const headerNames = declaredResponseHeaderNames(document.operation_id, document.status);
  const declared = new Set(headerNames.map((name) => name.toLowerCase()));
  return Object.keys(document.headers).filter((name) => !declared.has(name.toLowerCase()));
}

/** The `ContractValidationResult` rows as one readable line for a failure message. */
function describeErrors(errors: readonly ContractValidationError[]): string {
  if (errors.length === 0) {
    return 'no errors';
  }
  return errors
    .map((error) => `${error.path === '' ? '<root>' : error.path}: ${error.reason}`)
    .join('; ');
}

/**
 * The fixture's `input.json`, parsed and checked against the T12.1 document contract: exactly
 * the six members, `body` optional, and the five fixed ones typed. A throw names the fixture,
 * so a malformed document is attributable without a second lookup.
 */
function readDocument(fixture: Fixture): FixtureDocumentShape {
  const parsed: unknown = parseYaml(fixture.inputText);
  if (!isRecord(parsed)) {
    throw new Error(`fixtures/web-api/${fixture.name}/input.json is not a JSON object`);
  }

  for (const member of Object.keys(parsed)) {
    if (!DOCUMENT_MEMBERS.includes(member)) {
      throw new Error(
        `fixtures/web-api/${fixture.name}/input.json has the unknown member '${member}'; the document contract is exactly ${DOCUMENT_MEMBERS.join(', ')}`,
      );
    }
  }
  for (const member of REQUIRED_DOCUMENT_MEMBERS) {
    if (!(member in parsed)) {
      throw new Error(`fixtures/web-api/${fixture.name}/input.json is missing '${member}'`);
    }
  }

  const operationId = parsed['operation_id'];
  if (typeof operationId !== 'string' || operationId === '') {
    throw new Error(
      `fixtures/web-api/${fixture.name}/input.json: operation_id must be a non-empty string (an operationId of contracts/openapi.yaml)`,
    );
  }

  const variant = parsed['variant'];
  if (typeof variant !== 'string' || variant === '') {
    throw new Error(
      `fixtures/web-api/${fixture.name}/input.json: variant must be a non-empty string ('ok' for the default one)`,
    );
  }

  const status = parsed['status'];
  if (typeof status !== 'number' || !Number.isInteger(status) || status < 100 || status > 599) {
    throw new Error(
      `fixtures/web-api/${fixture.name}/input.json: status must be an HTTP status code, got ${String(status)}`,
    );
  }

  const headers = parsed['headers'];
  if (!isRecord(headers)) {
    throw new Error(
      `fixtures/web-api/${fixture.name}/input.json: headers must be an object of string values`,
    );
  }
  const headerRecord: Record<string, string> = {};
  for (const [name, value] of Object.entries(headers)) {
    if (typeof value !== 'string') {
      throw new Error(
        `fixtures/web-api/${fixture.name}/input.json: headers['${name}'] must be a string`,
      );
    }
    headerRecord[name] = value;
  }

  const latency = parsed['latency_ms'];
  if (typeof latency !== 'number' || !Number.isFinite(latency) || latency < 0) {
    throw new Error(
      `fixtures/web-api/${fixture.name}/input.json: latency_ms must be a non-negative number, got ${String(latency)}`,
    );
  }

  return {
    operation_id: operationId,
    variant,
    status,
    headers: headerRecord,
    latency_ms: latency,
    body: parsed['body'],
  };
}

/**
 * `#/components/schemas/RunSummary` — the row schema of the `listRuns` page the negative
 * control mutates. Reached through `RunPage.items` so the spec proves it reads the schema of
 * the rows it actually breaks, not a same-named schema somewhere else.
 */
function runSummarySchema(): Record<string, unknown> {
  const runPage = resolveContractPointer('#/components/schemas/RunPage');
  const pageProperties = isRecord(runPage) ? runPage['properties'] : undefined;
  const pageItems = isRecord(pageProperties) ? pageProperties['items'] : undefined;
  const itemSchema = isRecord(pageItems) ? pageItems['items'] : undefined;
  const reference = isRecord(itemSchema) ? itemSchema['$ref'] : undefined;
  if (typeof reference !== 'string' || reference !== '#/components/schemas/RunSummary') {
    throw new Error(
      `RunPage.items no longer references #/components/schemas/RunSummary (got ${String(reference)}); the negative control reads its required members from there`,
    );
  }

  const summary = resolveContractPointer(reference);
  if (!isRecord(summary)) {
    throw new Error('#/components/schemas/RunSummary is not an object in contracts/openapi.yaml');
  }
  return summary;
}

/** The `required[]` members a schema object declares. */
function requiredMembers(schema: Record<string, unknown>): readonly string[] {
  const declared: unknown = schema['required'];
  if (!Array.isArray(declared) || declared.length === 0) {
    throw new Error('the schema declares no non-empty required[] list in contracts/openapi.yaml');
  }
  const members: string[] = [];
  for (const member of declared as readonly unknown[]) {
    if (typeof member !== 'string') {
      throw new Error('the schema required[] holds a non-string member in contracts/openapi.yaml');
    }
    members.push(member);
  }
  return members;
}

/** A required `RunSummary` member the contract types with `format: uuid`. */
function requiredRunUuidField(): string {
  const schema = runSummarySchema();
  const properties = schema['properties'];
  const field = requiredMembers(schema).find((member) => {
    const property = isRecord(properties) ? properties[member] : undefined;
    return isRecord(property) && property['format'] === 'uuid';
  });
  if (field === undefined) {
    throw new Error(
      'RunSummary declares no required uuid member, so the non-uuid negative control has nothing to break',
    );
  }
  return field;
}

/**
 * One corpus document by directory name (09.2.1-T2). A missing directory fails with a message that
 * names it, so a renamed fixture cannot make the step-2 pin below vacuous.
 */
function corpusDocument(name: string): FixtureDocumentShape {
  const fixture = CORPUS.find((entry) => entry.name === name);
  if (fixture === undefined) {
    throw new Error(`fixtures/web-api/${name} is missing; the step-2 corpus needs it`);
  }
  return readDocument(fixture);
}

/**
 * The `runs-list` fixture document — the subject of every negative control. `readDocument`
 * re-parses `input.json` on every call, so a document handed out here is never shared.
 */
function runsListDocument(): FixtureDocumentShape {
  const fixture = CORPUS.find((entry) => entry.name === RUNS_LIST_FIXTURE);
  if (fixture === undefined) {
    throw new Error(
      `fixtures/web-api/${RUNS_LIST_FIXTURE} is missing; the AC10 negative control needs the listRuns page`,
    );
  }
  return readDocument(fixture);
}

/**
 * The default `listRuns` page and its first run row — the subject of both body negative
 * controls.
 * `readDocument` re-parses `input.json` on every call, so mutating the returned page never
 * leaks into another test.
 */
function runsListSubject(): {
  readonly page: Record<string, unknown>;
  readonly row: Record<string, unknown>;
} {
  const body = runsListDocument().body;
  if (!isRecord(body)) {
    throw new Error(
      `fixtures/web-api/${RUNS_LIST_FIXTURE}/input.json: the 200 body must be a RunPage object`,
    );
  }

  const items: unknown = body['items'];
  if (!Array.isArray(items) || items.length === 0) {
    throw new Error(
      `fixtures/web-api/${RUNS_LIST_FIXTURE}/input.json must carry at least one run row (items[0]); the AC10 negative control breaks one`,
    );
  }

  const first: unknown = items[0];
  if (!isRecord(first)) {
    throw new Error(
      `fixtures/web-api/${RUNS_LIST_FIXTURE}/input.json: items[0] must be a RunSummary object`,
    );
  }

  return { page: body, row: first };
}

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

  for (const fixture of CORPUS) {
    it(`validates fixtures/web-api/${fixture.name} against contracts/openapi.yaml`, () => {
      const document = readDocument(fixture);
      const result = VALIDATOR.validateResponse(
        document.operation_id,
        document.status,
        document.body,
      );
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

  it('keeps FIXTURE_CATALOG in step with the corpus directory', () => {
    const corpusByName = new Map(CORPUS.map((fixture) => [fixture.name, fixture] as const));
    const catalogNames = CATALOG.map((entry) => entry.name);

    expect(
      new Set(catalogNames).size,
      `FIXTURE_CATALOG lists a fixture twice: ${catalogNames.join(', ')}`,
    ).toBe(catalogNames.length);
    expect(
      [...catalogNames].sort(),
      'FIXTURE_CATALOG and fixtures/web-api hold different fixtures — the explicit input.json imports drifted from the corpus',
    ).toEqual([...corpusByName.keys()].sort());

    for (const entry of CATALOG) {
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

  it('pins at most one ok variant per operation and the 403 and 412 error fixtures (AC10)', () => {
    const documents = CORPUS.map((fixture) => ({
      name: fixture.name,
      document: readDocument(fixture),
    }));

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

  it('answers the one-time step-2 secrets in the create documents alone (09.2.1-T2)', () => {
    // 1. Every document of an operation that mints no secret is secret-free. This is AC2's
    //    "metadata only on resume" on the wire: the wizard's lists and detail reads can only show
    //    metadata when the corpus itself has no member that could carry a secret.
    for (const fixture of CORPUS) {
      const document = readDocument(fixture);
      if (SECRET_MINTING_OPERATIONS.includes(document.operation_id)) {
        continue;
      }
      expect(
        oneTimeSecretMembers(document.body),
        `fixtures/web-api/${fixture.name} (${document.operation_id}) answers with a one-time secret; only ${SECRET_MINTING_OPERATIONS.join(', ')} mint one`,
      ).toEqual([]);
    }

    // 2. The three creates do carry theirs, and their values are obviously fake: the corpus has no
    //    real issuer, and 00 §5.7 S3 forbids a credential-shaped value.
    const bindingCreate = corpusDocument('ci-binding-create');
    expect([bindingCreate.operation_id, bindingCreate.status]).toEqual(['createCiBinding', 201]);
    const bindingBody = bindingCreate.body as {
      binding: { status: string };
      binding_challenge: string;
    };
    expect(bindingBody.binding.status).toBe('pending_verification');
    expect(bindingBody.binding_challenge).toContain('fixture_only');
    // A JWT carries three dot-separated segments; a challenge that looked like one would read as
    // a bearer credential in a page that shows it.
    expect(bindingBody.binding_challenge.split('.')).toHaveLength(1);
    expect(bindingBody.binding_challenge).not.toContain('eyJ');

    const tokenCreate = corpusDocument('ingest-token-create');
    expect([tokenCreate.operation_id, tokenCreate.status]).toEqual(['createIngestToken', 201]);
    const tokenBody = tokenCreate.body as {
      token: { status: string };
      secret: string;
      overlap_ends_at: string | null;
    };
    expect(tokenBody.token.status).toBe('active');
    expect(tokenBody.secret.startsWith('egt_')).toBe(true);
    expect(tokenBody.secret).toContain('FIXTURE_ONLY');
    expect(tokenBody.secret.split('.')).toHaveLength(1);
    // A freshly issued token retires nothing, so there is no overlap window yet.
    expect(tokenBody.overlap_ends_at).toBeNull();

    const tokenRotate = corpusDocument('ingest-token-rotate');
    expect([tokenRotate.operation_id, tokenRotate.status]).toEqual(['rotateIngestToken', 200]);
    const rotatedBody = tokenRotate.body as {
      token: { overlap_ends_at: string | null };
      secret: string;
      overlap_ends_at: string | null;
    };
    expect(rotatedBody.secret.startsWith('egt_')).toBe(true);
    expect(rotatedBody.secret).toContain('FIXTURE_ONLY');
    // A rotation is the one answer that carries a window: the retired token keeps verifying until
    // `overlap_ends_at`, both on the successor (its own retirement date) and at the top level.
    expect(rotatedBody.overlap_ends_at).not.toBeNull();
    expect(rotatedBody.token.overlap_ends_at).toBe(rotatedBody.overlap_ends_at);

    // 3. The seed that derives step 3 from a token shows exactly one active token's metadata.
    const tokenPage = corpusDocument(ONBOARDING_TOKEN_FIXTURE);
    expect([tokenPage.operation_id, tokenPage.variant]).toEqual([
      'listIngestTokens',
      'onboarding-token',
    ]);
    const page = tokenPage.body as { items: { status: string }[]; next_cursor: string | null };
    expect(page.items).toHaveLength(1);
    expect(page.items[0]?.status).toBe('active');
    expect(page.next_cursor).toBeNull();
  });

  it('fails on the runs-list page when a required run member is removed (AC10)', () => {
    const field = requiredMembers(runSummarySchema())[0];
    if (field === undefined) {
      throw new Error(
        '#/components/schemas/RunSummary declares no required member, so the negative control cannot remove one',
      );
    }

    const { page, row } = runsListSubject();
    const baseline = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      baseline.errors,
      `the unmutated runs-list page must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    expect(
      Object.keys(row),
      `the runs-list row does not carry the required member '${field}', so removing it proves nothing`,
    ).toContain(field);

    const withoutMember: Record<string, unknown> = {};
    for (const [member, value] of Object.entries(row)) {
      if (member !== field) {
        withoutMember[member] = value;
      }
    }
    page['items'] = [withoutMember];

    const result = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      result.errors.length,
      `a listRuns page whose run row lost the required member '${field}' still validated against contracts/openapi.yaml — the contract check cannot catch a schema drift (AC10)`,
    ).toBeGreaterThan(0);
    // Ajv locates a `required` failure on the object and names the missing member in `params`;
    // the shared mapper appends it to the path (`/items/0/<field>`). Either spelling names the
    // removed field, so both are accepted — the rows are printed when neither does.
    expect(
      result.errors.some((error) => error.path.includes(field) || error.reason === 'required'),
      `the contract check reported ${describeErrors(result.errors)}, none of which names the removed '${field}'`,
    ).toBe(true);
  });

  it('fails on a run row whose uuid member is not a uuid', () => {
    const field = requiredRunUuidField();
    const { page, row } = runsListSubject();
    const baseline = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      baseline.errors,
      `the unmutated runs-list page must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    page['items'] = [{ ...row, [field]: NOT_A_UUID }];

    const result = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      result.errors.length,
      `a listRuns page whose run row carries '${field}: ${NOT_A_UUID}' still validated against contracts/openapi.yaml — the contract check ignores the uuid format (AC10)`,
    ).toBeGreaterThan(0);
    expect(
      result.errors.some((error) => error.path.includes(field) || error.reason === 'format'),
      `the contract check reported ${describeErrors(result.errors)}, none of which names '${field}'`,
    ).toBe(true);
  });

  it('reports a fixture header contracts/openapi.yaml does not declare', () => {
    const document = runsListDocument();
    const declared = declaredResponseHeaderNames(document.operation_id, document.status);

    // The response ref is followed before its `headers` object is read: a check that looked at
    // the operation's own `responses['200']` and stopped there would see no header at all.
    expect(
      declared.map((name) => name.toLowerCase()),
      `the ${document.status} response of '${document.operation_id}' declares no 'x-request-id', so the baseline below is empty for the wrong reason`,
    ).toContain('x-request-id');
    expect(
      undeclaredResponseHeaders(document),
      'the unmutated runs-list headers must be declared before the control adds one that is not',
    ).toEqual([]);

    // Header names are case-insensitive: a fixture answering the declared header in another
    // case is still declared, so only a genuinely absent name fails.
    const lowercasedHeaders: Record<string, string> = { 'x-request-id': 'fixture-request-id' };
    const lowercased = { ...document, headers: lowercasedHeaders };
    expect(
      undeclaredResponseHeaders(lowercased),
      "a lowercased 'x-request-id' must count as the declared 'X-Request-Id'",
    ).toEqual([]);

    const withUndeclared = { ...document, headers: { ...lowercasedHeaders } };
    withUndeclared.headers[UNDECLARED_HEADER] = 'fixture-only';
    const undeclared = undeclaredResponseHeaders(withUndeclared);
    expect(
      undeclared,
      `a runs-list fixture answering '${UNDECLARED_HEADER}' still passed the header check — it cannot catch a fixture that answers a header contracts/openapi.yaml does not declare`,
    ).toEqual([UNDECLARED_HEADER]);
  });
});
