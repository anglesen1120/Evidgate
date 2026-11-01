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
// 09.7.1-T3 extends that pin to `invite_token` (`InviteIssuedView`, answered by `createMembership`
// and `resendMembershipInvite`) and adds the Settings area's state pins: the invite lifecycle's
// documents — the create and its `Idempotency-Key` replay, the rotated resend and its replay, the
// bodyless 204 cancel and both 409 `invite_not_pending` refusals — and the Secrets tab's token page,
// whose six rows must cover the reminder window (far, soon, already past, no expiry) and the
// rotation columns. The reminder window is measured against the fixed date the corpus's literal
// timestamps were authored around, so the pin describes the corpus instead of the calendar.
//
// The four closing tests are negative controls of test 3, of the header check above and of the
// external-reference inlining below: they take the `runs-list` page, break it the way a contract
// change would (a removed `required` run member, a non-uuid uuid), answer a header the contract does
// not declare and mistype a member only a standalone `contracts/schemas/` document declares, each of
// which must be reported. Without them "every fixture validates" could pass while the check
// validated nothing — AC10's done-when is precisely "fails on a fixture with a removed required
// field".
//
// 09.7.1-T4: a response schema may be a standalone document under `contracts/schemas/` —
// `PolicyRevisionDto.body` is `./schemas/policy-revision.v1.json`. `createOpenApiResponseValidator`
// resolves local `#/components/...` references only, so such a response was reported as
// `schema_unavailable` and every policy revision page went unchecked. The parsed contract handed to
// the validator is therefore the document with those external references inlined from the
// repository (`inlineExternalSchemaRefs`): the same schema, compiled instead of skipped. Nothing is
// loosened — a reference that cannot be inlined faithfully stays in place and still reports
// `schema_unavailable`, and the last negative control proves an inlined body is enforced.
//
// Read-only and offline: the contract, `contracts/schemas` and the corpus are read from the
// repository with `node:fs`, exactly like `../http/contract-smoke.spec.ts`. No `fetch`, no
// `HttpClient`, no network — the validator is the `packages/cli` one, never the app's HTTP stack.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';

import { createOpenApiResponseValidator, type ContractValidationError } from '@evidgate/cli';
import type { RunAttemptPage } from '@evidgate/sdk';
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

/** 09.7.1-T4: the policy revision document whose standalone body schema the last control breaks. */
const POLICY_REVISION_DOCUMENT_FIXTURE = 'policy-revision-head';

/** A member only `contracts/schemas/policy-revision.v1.json` declares, typed `integer`. */
const POLICY_BODY_TYPED_MEMBER = 'flaky_budget';

/** A value that schema's `integer` type refuses. */
const NOT_A_NUMBER = 'not-a-number';

/**
 * 09.2.1-T2: the two body members a step-2 response can carry a one-time secret in —
 * `CiBindingCreatedResponse.binding_challenge` and `IngestTokenCreatedResponse.secret`
 * (contracts/openapi.yaml:6607-6615, 6708-6719). Only the three creates the wizard's step 2 calls
 * answer with them; no list or detail read may carry either, which is what AC2's "metadata only on
 * resume" reduces to on the wire. (The contract's other secret-bearing body is the CI-side
 * `CiTokenResponse.token`, answered by `POST /v1/ci/token` — a pre-tenant route the browser never
 * calls, `contracts/openapi.yaml:71-81`, kept out of the web by decision (2) of the task's §10
 * entry.)
 *
 * 09.7.1-T3 adds `InviteIssuedView.invite_token` (contracts/openapi.yaml:7299-7304) to the same
 * pin: `createMembership` and `resendMembershipInvite` answer it once and no other document of the
 * corpus may carry it, which is the Members tab's "the link is shown once, then only metadata"
 * held on the wire. An `Idempotency-Key` replay is exactly a body with the member `null`, so the
 * helper below counts a member only when its value is not `null`.
 */
const ONE_TIME_SECRET_MEMBERS: readonly string[] = ['binding_challenge', 'secret', 'invite_token'];

/** The operations that mint one, and the only ones allowed to answer with a secret member. */
const SECRET_MINTING_OPERATIONS: readonly string[] = [
  'createCiBinding',
  'createIngestToken',
  'rotateIngestToken',
  'createMembership',
  'resendMembershipInvite',
];

/** The seed page that derives step 3 from a token: one active token, its metadata and no secret. */
const ONBOARDING_TOKEN_FIXTURE = 'ingest-tokens-list-onboarding-token';

/**
 * 09.7.1-F15: the `Storefront web` project the Settings area's pages are authored on — the first
 * row of `projects-list`, `PROJECT_STOREFRONT` in `apps/web/e2e/shell/support.ts`.
 */
const STOREFRONT_PROJECT_ID = '018f0000-0000-7000-8000-000000000011';

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
  /** 09.7.1-F15 note 3: the `{name}` parameter the answer is keyed by, where a route declares one. */
  readonly idKeyParameter?: string;
  /** 09.7.1-F15 note 4: the query parameter whose value selects the page, where a route declares one. */
  readonly cursorQueryParameter?: string;
}

/**
 * The `CiBindingView` members the two paged CI-binding fixtures are asserted through
 * (09.7.1-F15 note 4): the id the store de-duplicates by, the project the page belongs to, the
 * verification state, and the creation instant the keyset cursor is derived from.
 *
 * The `evidgate/no-hand-written-wire-dto` exemption is `FixtureDocumentShape`'s above: these are
 * `input.json` members, read to prove the two corpus pages continue one walk, not a body the app
 * reads. The row type belongs to `@evidgate/sdk`; nothing here is imported by production code.
 */
/* eslint-disable evidgate/no-hand-written-wire-dto -- the corpus members of a fixture page (see above). */
interface CiBindingRowShape {
  readonly id: string;
  readonly project_id: string;
  readonly status: string;
  readonly activated_at?: string | null;
  readonly created_at: string;
}

/**
 * The `RunSummary` members the two paged run-list fixtures are asserted through (09.3.1-T1): the id
 * the store de-duplicates by and the creation instant `KeysetCursor` derives the cursor from. The
 * exemption is the one above: these are `input.json` members read to prove the two corpus pages
 * continue one walk, not a body the app reads.
 */
interface RunRowShape {
  readonly run_id: string;
  readonly created_at: string;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

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

/** The `contracts/` tree external `$ref`s are resolved inside; anything outside it is refused. */
const CONTRACTS_DIRECTORY = join(REPOSITORY_ROOT, 'contracts');

/** How many external files one reference chain may cross before it counts as a cycle. */
const MAX_EXTERNAL_SCHEMA_HOPS = 8;

/**
 * The parsed contract with its external schema references inlined; the validator resolves its own
 * local `#/components/...` refs. See `inlineExternalSchemaRefs` for why the inlining happens here
 * and for the references it deliberately leaves alone.
 */
const CONTRACT_DOCUMENT: unknown = inlineExternalSchemaRefs(
  parseYaml(readFileSync(CONTRACT_PATH, 'utf8')),
);
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
 * 09.7.1-T4: the contract with every external schema reference inlined, so the validator compiles
 * the schemas it declares instead of reporting `schema_unavailable` for them.
 *
 * `createOpenApiResponseValidator` re-addresses local `#/components/schemas/<name>` references into
 * one Ajv document and reads nothing else — its own documentation says an external
 * `./schemas/*.json` reference "is reported as a row, never thrown". `PolicyRevisionDto.body` is
 * exactly such a reference, so every policy revision response was unchecked. Inlining keeps the
 * check honest: the document handed to the validator carries the schema the contract actually
 * names, read from this repository.
 *
 * A reference is left in place — and therefore still reported as `schema_unavailable`, never as
 * valid — when this spec cannot inline it faithfully:
 * - the file does not exist, or resolves outside `contracts/`;
 * - the reference chain revisits a file (a cycle);
 * - the inlined document makes a root-relative `#/...` reference, which would resolve against
 *   `contracts/openapi.yaml` instead of the file it was written for.
 *
 * `$id` and `$schema` are dropped from an inlined document: a nested `$id` re-bases the references
 * inside the subtree it is spliced into, and `$schema` is only meaningful at a schema resource root.
 */
function inlineExternalSchemaRefs(node: unknown, inlined: readonly string[] = []): unknown {
  if (Array.isArray(node)) {
    return (node as readonly unknown[]).map((entry) => inlineExternalSchemaRefs(entry, inlined));
  }
  if (!isRecord(node)) {
    return node;
  }

  const reference = node['$ref'];
  if (typeof reference === 'string' && !reference.startsWith('#')) {
    const target = readExternalSchema(reference, inlined);
    if (target !== undefined) {
      const siblings = Object.entries(node).filter(([key]) => key !== '$ref');
      if (siblings.length === 0) {
        return target;
      }
      // 2020-12 lets `$ref` carry siblings and applies both, so they stay side by side.
      const rest: Record<string, unknown> = {};
      for (const [key, value] of siblings) {
        rest[key] = inlineExternalSchemaRefs(value, inlined);
      }
      return { allOf: [target, rest] };
    }
  }

  const copy: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(node)) {
    copy[key] = key === '$ref' ? value : inlineExternalSchemaRefs(value, inlined);
  }
  return copy;
}

/**
 * One external reference (`'./schemas/policy-revision.v1.json'`, optionally with a
 * `'#/$defs/<name>'` tail) as an inlined schema node, or undefined when it cannot be inlined.
 */
function readExternalSchema(reference: string, inlined: readonly string[]): unknown {
  const hash = reference.indexOf('#');
  const file = hash < 0 ? reference : reference.slice(0, hash);
  const pointer = hash < 0 ? '' : reference.slice(hash);
  const path = resolve(dirname(CONTRACT_PATH), file);

  if (!path.startsWith(`${CONTRACTS_DIRECTORY}${sep}`) || !existsSync(path)) {
    return undefined;
  }
  if (inlined.includes(path) || inlined.length >= MAX_EXTERNAL_SCHEMA_HOPS) {
    return undefined;
  }

  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'));
  const target = resolveContractPointerIn(parsed, pointer);
  if (target === undefined || hasRootRelativeRef(target)) {
    return undefined;
  }

  const withoutIdentity: unknown = isRecord(target)
    ? Object.fromEntries(
        Object.entries(target).filter(([key]) => key !== '$id' && key !== '$schema'),
      )
    : target;
  return inlineExternalSchemaRefs(withoutIdentity, [...inlined, path]);
}

/** Resolve one `#/json/pointer` (or a bare `#`) inside a standalone schema document. */
function resolveContractPointerIn(document: unknown, pointer: string): unknown {
  if (!pointer.startsWith('#/')) {
    return document;
  }

  let current: unknown = document;
  for (const raw of pointer.slice(2).split('/')) {
    if (!isRecord(current)) {
      return undefined;
    }
    current = current[raw.replace(/~1/g, '/').replace(/~0/g, '~')];
  }
  return current;
}

/** Whether a schema node (at any depth) makes a `#/...` reference the contract document owns. */
function hasRootRelativeRef(node: unknown): boolean {
  if (Array.isArray(node)) {
    return node.some((entry) => hasRootRelativeRef(entry));
  }
  if (!isRecord(node)) {
    return false;
  }
  const reference = node['$ref'];
  if (typeof reference === 'string' && reference.startsWith('#')) {
    return true;
  }
  return Object.entries(node).some(([key, value]) => key !== '$ref' && hasRootRelativeRef(value));
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
 * The .NET `DateTime` ticks of an RFC 3339 UTC instant, written the way the server's `KeysetCursor`
 * writes a cursor (16 lower-case hex digits, zero-padded), so the corpus page can be proven to name
 * a cursor the server could actually have issued rather than a random opaque string.
 */
function keysetTicks(instant: string): string {
  const unixMs = Date.parse(instant);
  if (Number.isNaN(unixMs)) {
    throw new Error(`'${instant}' is not an RFC 3339 instant a cursor can be derived from`);
  }
  const ticks = (BigInt(unixMs) / 1_000n) * 10_000_000n + 621_355_968_000_000_000n;
  return ticks.toString(16).padStart(16, '0');
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

/**
 * The fixed date the Secrets tab's token expiries were authored around (09.7.1-T3): the corpus holds
 * literal timestamps, so the fourteen-day reminder window is measured against this date rather than
 * against whenever the suite runs.
 */
const FIXTURE_REVIEW_NOW = '2026-10-25T00:00:00Z';

/** Fourteen days in milliseconds — the window inside which a token's expiry is `expires-soon`. */
const FOURTEEN_DAYS_MS = 14 * 24 * 60 * 60 * 1000;

/**
 * Assert a corpus `invite_token` is an obviously fake placeholder: the `evgi_` prefix the contract
 * documents, the corpus's own `FIXTURE_ONLY` marker, one dot-free segment so it cannot read as a
 * JWT, and no `://` — the corpus carries a raw token, never a link a reviewer could paste.
 */
function expectInvitePlaceholder(value: string | null): void {
  expect(value, 'the invite answer carries no token at all').not.toBeNull();
  expect(value?.startsWith('evgi_')).toBe(true);
  expect(value).toContain('FIXTURE_ONLY');
  expect(value?.split('.')).toHaveLength(1);
  expect(value).not.toContain('://');
}

/**
 * A body without its `invite_token`, so a replay document can be compared with the answer it
 * stored: the two must differ in the token alone.
 */
function withoutInviteToken(body: unknown): Record<string, unknown> {
  const copy: Record<string, unknown> = { ...(body as Record<string, unknown>) };
  delete copy['invite_token'];
  return copy;
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
          'current_evidence_revision_id',
          'declared_producers',
          'environment',
          'manifest_hash',
          'policy_revision_id',
          'purpose',
          'ref',
          'run_id',
          'shard_count',
          'state',
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
        'members',
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

  it('pins the id-keyed revision read to distinct corpus ids (09.7.1-F15)', () => {
    // 09.7.1-F15 note (3): `getPolicyRevision` answers the document whose own `body.id` equals the
    // request's `{id}`. That is only sound when (a) the row names a parameter its template actually
    // carries, (b) every 2xx document of the operation carries a string `id`, (c) no two documents
    // share one, so the selection is unambiguous, and (d) the revision ids a corpus page offers are
    // exactly the documents a read can address — otherwise a page load would ask for an id no
    // document answers and silently get the variant.
    const keyed = FIXTURE_ROUTE_ROWS.filter((route) => route.idKeyParameter !== undefined);
    expect(
      keyed.map((route) => route.operationId),
      'only getPolicyRevision is id-keyed; a second id-keyed row needs its own pin here',
    ).toEqual(['getPolicyRevision']);

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
    // 09.7.1-F15 note (4): `listCiBindings` answers the catalog row whose `cursor` equals the
    // request's cursor, so the Secrets tab's Load more reaches its terminal page in a browser
    // instead of every fixture ending at `next_cursor: null`. 09.3.1-T1 adds the same seam for the
    // Runs list (`listRuns`, `run-list-page-2`). It is only sound when (a) every cursor-keyed row
    // names the query parameter the contract's list read takes, (b) each cursor-declaring catalog
    // row continues a page that names it — same project, disjoint ids, a cursor the server could
    // have issued — and (c) it ends the walk, because a second cursor with no document behind it
    // would leave the control on screen. The lists here are exhaustive on purpose: a third
    // cursor-keyed row or catalog cursor needs its own pin in this test.
    const keyed = FIXTURE_ROUTE_ROWS.filter((route) => route.cursorQueryParameter !== undefined);
    expect(
      keyed.map((route) => route.operationId),
      'listCiBindings, listRuns and listRunAttempts are cursor-keyed; a fourth row needs its own pin here',
    ).toEqual(['listCiBindings', 'listRuns', 'listRunAttempts']);
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
    ).toEqual(['ci-bindings-list-settings-page-2', 'run-attempts-page-2', 'run-list-page-2']);
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
    // The served set, not the corpus directory: the decision and evidence-revision documents are
    // typed modules (`./decision-fixtures`, `./evidence-revision-fixtures`), and a routed operation
    // must resolve its variants here whether or not `fixtures/web-api` holds a directory for it.
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

  it('answers the invite token once and only in the invite documents (09.7.1-T3)', () => {
    // The corpus page's `pending` row is the membership both lifecycle calls address, so the tab has
    // a row to resend or cancel without a second list document.
    const membersPage = corpusDocument('memberships-list').body as {
      items: { id: string; status: string }[];
    };
    const pendingRow = membersPage.items.find((item) => item.status === 'pending');
    expect(
      pendingRow,
      'memberships-list carries no pending membership, so the invite lifecycle documents address a row no page shows',
    ).toBeDefined();

    // 1. `createMembership`'s 201 carries the token, and its `replay` twin is that body without it.
    const created = corpusDocument('membership-create-ok');
    expect([created.operation_id, created.status, created.variant]).toEqual([
      'createMembership',
      201,
      'ok',
    ]);
    const createdBody = created.body as { status: string; invite_token: string | null };
    expect(createdBody.status).toBe('pending');
    expectInvitePlaceholder(createdBody.invite_token);

    const createdReplay = corpusDocument('membership-create-replay');
    expect([createdReplay.operation_id, createdReplay.status, createdReplay.variant]).toEqual([
      'createMembership',
      201,
      'replay',
    ]);
    const createdReplayBody = createdReplay.body as { invite_token: string | null };
    // A replay is the stored answer: the same membership, the token withheld — never a second one.
    expect(createdReplayBody.invite_token).toBeNull();
    expect(withoutInviteToken(createdReplayBody)).toEqual(withoutInviteToken(createdBody));

    // 2. `resendMembershipInvite`'s 202 rotates the pending row's token: same row, new `updated_at`
    //    and version, so the tab can prove it kept the membership and only reissued the link.
    const resent = corpusDocument('membership-resend-ok');
    expect([resent.operation_id, resent.status, resent.variant]).toEqual([
      'resendMembershipInvite',
      202,
      'ok',
    ]);
    const resentBody = resent.body as {
      id: string;
      status: string;
      version: number;
      invite_token: string | null;
    };
    expect(resentBody.id).toBe(pendingRow?.id);
    expect(resentBody.status).toBe('pending');
    expect(resentBody.version).toBe(2);
    expectInvitePlaceholder(resentBody.invite_token);
    // The rotation mints a different token from the create: one value cannot stand for both calls.
    expect(resentBody.invite_token).not.toBe(createdBody.invite_token);

    const resentReplay = corpusDocument('membership-resend-replay');
    expect([resentReplay.operation_id, resentReplay.status, resentReplay.variant]).toEqual([
      'resendMembershipInvite',
      202,
      'replay',
    ]);
    const resentReplayBody = resentReplay.body as { invite_token: string | null };
    expect(resentReplayBody.invite_token).toBeNull();
    expect(withoutInviteToken(resentReplayBody)).toEqual(withoutInviteToken(resentBody));

    // 3. `cancelMembershipInvite`'s answer is bodyless, like `removeMember`'s.
    const cancelled = corpusDocument('membership-cancel-ok');
    expect([cancelled.operation_id, cancelled.status, cancelled.variant]).toEqual([
      'cancelMembershipInvite',
      204,
      'ok',
    ]);
    expect(cancelled.body, 'a 204 answer carries no body').toBeUndefined();

    // 4. Both lifecycle calls refuse a row that is not pending with the same 409, carrying the
    //    server's own code — the state the tab must report instead of a rotated or cancelled invite.
    for (const name of ['membership-resend-not-pending', 'membership-cancel-not-pending']) {
      const refusal = corpusDocument(name);
      expect([refusal.status, refusal.variant], `fixtures/web-api/${name}`).toEqual([
        409,
        'not-pending',
      ]);
      const envelope = refusal.body as { error: { code: string; retryable: boolean } };
      expect(envelope.error.code, `fixtures/web-api/${name}`).toBe('invite_not_pending');
      expect(envelope.error.retryable, `fixtures/web-api/${name}`).toBe(false);
    }
  });

  it('pins the rotation burst refusal and its bounded retry delay (09.7.1-T3 AC3)', () => {
    const refusal = corpusDocument('ingest-token-rotate-rate-limited');
    expect([refusal.operation_id, refusal.status, refusal.variant]).toEqual([
      'rotateIngestToken',
      429,
      'rate-limited',
    ]);
    expect(refusal.headers['Retry-After']).toBe('30');
    const body = refusal.body as { error: { code: string; retryable: boolean } };
    expect(body.error.code).toBe('rate_limited');
    expect(body.error.retryable).toBe(true);
    expect(oneTimeSecretMembers(refusal.body)).toEqual([]);
  });

  it('pins the Secrets tab’s token page states (09.7.1-T3)', () => {
    const page = corpusDocument('ingest-tokens-list-settings');
    expect([page.operation_id, page.variant, page.status]).toEqual([
      'listIngestTokens',
      'settings',
      200,
    ]);
    const body = page.body as {
      items: {
        id: string;
        status: string;
        prefix: string;
        expires_at: string | null;
        overlap_ends_at: string | null;
        rotated_to_id: string | null;
      }[];
      next_cursor: string | null;
    };
    expect(body.next_cursor).toBeNull();
    expect(
      body.items.length,
      'the Secrets tab page must cover the reminder and rotation states in one read',
    ).toBeGreaterThanOrEqual(6);

    // The four `active` states the page's reminder chip distinguishes. The corpus dates are
    // literals (a fixture cannot move with the calendar), so the window is measured against the
    // fixed date these rows were authored around.
    const reviewNow = Date.parse(FIXTURE_REVIEW_NOW);
    const expiries = body.items
      .filter((item) => item.status === 'active')
      .map((item) => (item.expires_at === null ? null : Date.parse(item.expires_at)));
    expect(expiries, 'an active token with no expiry at all').toContain(null);
    expect(
      expiries.some((expiry) => expiry !== null && expiry - reviewNow > FOURTEEN_DAYS_MS),
      'an active token whose expiry is far enough away that no reminder chip shows',
    ).toBe(true);
    expect(
      expiries.some(
        (expiry) => expiry !== null && expiry > reviewNow && expiry - reviewNow <= FOURTEEN_DAYS_MS,
      ),
      'an active token inside the fourteen-day reminder window',
    ).toBe(true);
    expect(
      expiries.some((expiry) => expiry !== null && expiry <= reviewNow),
      'an active token whose expiry has already passed, the state the `expired` reminder reports',
    ).toBe(true);

    // The rotation states: a retired row pointing at its successor, and a revoked one.
    const rotated = body.items.find((item) => item.status === 'rotated');
    expect(rotated, 'no rotated token, so the page cannot show a retirement window').toBeDefined();
    expect(rotated?.overlap_ends_at).not.toBeNull();
    expect(
      body.items.map((item) => item.id),
      'the rotated token names a successor the same page does not hold',
    ).toContain(rotated?.rotated_to_id);
    expect(body.items.some((item) => item.status === 'revoked')).toBe(true);

    // A prefix is a public identifier, never a credential: short, and far below the length of any
    // token the contract's `egt_<prefix>_<secret>` form would mint.
    for (const item of body.items) {
      expect(item.prefix.length, `${item.id} carries a token-shaped prefix`).toBeLessThan(12);
      expect(item.prefix.startsWith('egt_')).toBe(true);
      expect(item.prefix.split('.')).toHaveLength(1);
    }
  });

  it('pins the Secrets tab’s CI-binding section states (09.7.1-F15)', () => {
    // 1. The page the section opens with: five bindings on the `Storefront web` project, one per
    //    state the section renders — the three `status` values plus the layout extremes (a single
    //    ref pattern, and a repository whose name and allowlists overflow a cell).
    const page = corpusDocument('ci-bindings-list-settings');
    expect([page.operation_id, page.variant, page.status]).toEqual([
      'listCiBindings',
      'settings',
      200,
    ]);
    const body = page.body as {
      items: {
        id: string;
        project_id: string;
        provider: string;
        repository_id: number;
        repository_full_name: string;
        allowed_ref_patterns: string[];
        allowed_workflow_refs: string[];
        status: string;
        activated_at?: string | null;
        created_at: string;
        version: number;
      }[];
      next_cursor: string | null;
    };
    expect(body.next_cursor).toBeNull();
    expect(
      body.items.length,
      'the section must show every verification state in one read',
    ).toBeGreaterThanOrEqual(5);

    const projectIds = new Set(body.items.map((item) => item.project_id));
    expect([...projectIds], 'every row belongs to the Storefront web project').toEqual([
      STOREFRONT_PROJECT_ID,
    ]);
    expect(new Set(body.items.map((item) => item.status))).toEqual(
      new Set(['active', 'pending_verification', 'disabled']),
    );

    // A pending binding is exactly one the OIDC handshake has not finished: it has no activation
    // time. An activated one always has it, so the two are never the same row.
    for (const item of body.items) {
      if (item.status === 'pending_verification') {
        expect(
          item.activated_at,
          `${item.id} is pending yet carries an activation time`,
        ).toBeNull();
      }
    }
    expect(
      body.items.some((item) => item.status !== 'pending_verification' && item.activated_at),
      'no activated binding carries its activation time',
    ).toBe(true);

    // Layout extremes: one binding allows a single ref and workflow, one overflows.
    expect(
      body.items.some(
        (item) => item.allowed_ref_patterns.length === 1 && item.allowed_workflow_refs.length === 1,
      ),
      'no binding has the single-pattern shape the compact row renders',
    ).toBe(true);
    expect(
      body.items.some(
        (item) =>
          item.repository_full_name.length > 40 &&
          item.allowed_ref_patterns.length >= 6 &&
          item.allowed_workflow_refs.length >= 3,
      ),
      'no binding has a long repository name with long allowlists, so the overflow cell has nothing to truncate',
    ).toBe(true);

    // 2. Every allowlist entry is in the form the server accepts — not merely a string the
    //    contract's patternless `array of string` lets through (`Ci/RefGlob.cs:69,79`): a ref
    //    pattern is `refs/heads/…` or `refs/tags/…`, and a workflow ref is
    //    `owner/repo/.github/workflows/<file>@refs/…` for THAT row's repository.
    for (const item of body.items) {
      expect(item.provider).toBe('github_actions');
      expect(Number.isInteger(item.repository_id) && item.repository_id > 0).toBe(true);
      expect(item.version).toBeGreaterThan(0);
      for (const pattern of item.allowed_ref_patterns) {
        expect(pattern, `${item.id} allows a ref pattern the server refuses`).toMatch(
          /^refs\/(heads|tags)\/[A-Za-z0-9._/*+@-]{1,200}$/,
        );
      }
      for (const workflowRef of item.allowed_workflow_refs) {
        expect(workflowRef, `${item.id} allows a workflow ref the server refuses`).toMatch(
          /^[A-Za-z0-9._*-]{1,100}\/[A-Za-z0-9._*-]{1,100}\/\.github\/workflows\/[A-Za-z0-9._*-]{1,100}@refs\/(heads|tags)\/[A-Za-z0-9._/*+@-]{1,200}$/,
        );
        expect(
          workflowRef.startsWith(`${item.repository_full_name}/.github/workflows/`),
          `${item.id} allows a workflow of a repository it is not bound to`,
        ).toBe(true);
      }
    }

    // 3. Metadata only: a listed binding never carries the challenge or its hash — the same pin the
    //    corpus-wide one-time-secret test applies, restated on the page the section renders.
    expect(oneTimeSecretMembers(page.body)).toEqual([]);
    for (const item of body.items) {
      expect(Object.keys(item), `${item.id} carries a challenge member`).not.toContain(
        'binding_challenge',
      );
    }

    // 4. The two other states: the unbound project's empty page, and the 403 the section renders
    //    as its own `forbidden` phase instead of an error message.
    const empty = corpusDocument('ci-bindings-list-settings-empty');
    expect([empty.operation_id, empty.variant, empty.status]).toEqual([
      'listCiBindings',
      'settings-empty',
      200,
    ]);
    expect(empty.body).toEqual({ items: [], next_cursor: null });

    const forbidden = corpusDocument('ci-bindings-list-settings-forbidden');
    expect([forbidden.operation_id, forbidden.variant, forbidden.status]).toEqual([
      'listCiBindings',
      'settings-forbidden',
      403,
    ]);
    const envelope = forbidden.body as { error: { code: string; retryable: boolean } };
    expect(envelope.error.code).toBe('forbidden');
    expect(envelope.error.retryable).toBe(false);
    expect(oneTimeSecretMembers(forbidden.body)).toEqual([]);
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

  // 09.7.1-T4: the control of the external-reference inlining. `PolicyRevisionDto.body` is the
  // standalone `contracts/schemas/policy-revision.v1.json`, and the validator reports
  // `schema_unavailable` for a response whose schema it cannot resolve — an unchecked policy page
  // behind a green suite. This breaks a member only that document declares and requires the row.
  it('enforces the standalone policy body schema contracts/openapi.yaml references', () => {
    const document = corpusDocument(POLICY_REVISION_DOCUMENT_FIXTURE);
    const baseline = VALIDATOR.validateResponse(
      document.operation_id,
      document.status,
      document.body,
    );
    expect(
      baseline.errors,
      `the unmutated policy revision document must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    const revisionBody = isRecord(document.body) ? document.body['body'] : undefined;
    expect(
      isRecord(revisionBody),
      'the policy revision document carries no object `body`, so the control below mutates nothing',
    ).toBe(true);

    const mutated = {
      ...(document.body as Record<string, unknown>),
      // `flaky_budget` is declared by contracts/schemas/policy-revision.v1.json alone, so a body
      // the validator accepted with it mistyped had its `body` schema skipped, not checked.
      body: {
        ...(revisionBody as Record<string, unknown>),
        [POLICY_BODY_TYPED_MEMBER]: NOT_A_NUMBER,
      },
    };

    const result = VALIDATOR.validateResponse(document.operation_id, document.status, mutated);
    expect(
      result.errors.length,
      `a policy revision whose \`body.${POLICY_BODY_TYPED_MEMBER}\` is '${NOT_A_NUMBER}' still validated — the contract's standalone policy body schema is not enforced`,
    ).toBeGreaterThan(0);
    expect(
      result.errors.some(
        (error) => error.path.includes(POLICY_BODY_TYPED_MEMBER) || error.reason === 'type',
      ),
      `the contract check reported ${describeErrors(result.errors)}, none of which names the mistyped '${POLICY_BODY_TYPED_MEMBER}'`,
    ).toBe(true);
  });
});
