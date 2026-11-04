// The corpus as the specs assert it: the document file shape, one reader per fixture, the
// `runs-list` subject the negative controls mutate, and the typed views of the catalog and
// of `FIXTURE_ROUTES`. Split from the suite so every family shares one reader instead of copies.

import { loadAllFixtures, type Fixture } from '@evidgate/test-fixtures';
import { parse as parseYaml } from 'yaml';

import { FIXTURE_CATALOG } from './fixture-catalog';
import { FIXTURE_ROUTES } from './fixture-routes';
import {
  declaredResponseHeaderNames,
  isRecord,
  resolveContractPointer,
  WEB_API_FIXTURES,
} from './fixture-contract.support';

/** The fixture and operation the negative control mutates. */
const RUNS_LIST_FIXTURE = 'runs-list';

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
export const ONE_TIME_SECRET_MEMBERS: readonly string[] = [
  'binding_challenge',
  'secret',
  'invite_token',
];

/**
 * 09.7.1-F15: the `Storefront web` project the Settings area's pages are authored on — the first
 * row of `projects-list`, `PROJECT_STOREFRONT` in `apps/web/e2e/shell/support.ts`.
 */
export const STOREFRONT_PROJECT_ID = '018f0000-0000-7000-8000-000000000011';

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
export interface FixtureDocumentShape {
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
export interface FixtureRouteShape {
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
export interface CiBindingRowShape {
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
export interface RunRowShape {
  readonly run_id: string;
  readonly created_at: string;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * The corpus, loaded once with the shared 01.6.1-T3 loader — the same loader the .NET side
 * mirrors, so a malformed fixture directory refuses here instead of being skipped.
 */
export const CORPUS: readonly Fixture[] = loadAllFixtures(WEB_API_FIXTURES);

/** `FIXTURE_CATALOG` seen through the document contract this spec asserts. */
export const CATALOG: readonly {
  readonly name: string;
  readonly document: FixtureDocumentShape;
}[] = FIXTURE_CATALOG;

/** `FIXTURE_ROUTES` seen through the route contract this spec asserts. */
export const FIXTURE_ROUTE_ROWS: readonly FixtureRouteShape[] = FIXTURE_ROUTES;

/**
 * Every one-time secret member a response body carries, at any depth: a member named in
 * `ONE_TIME_SECRET_MEMBERS` whose value is not `null` (the contract types both as a plain
 * `string`, so a `null` is a body the schema refuses and the validation test reports it).
 */
export function oneTimeSecretMembers(value: unknown): readonly string[] {
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

/**
 * The fixture's header names that the same (operation, status) response does not declare.
 * HTTP header names are case-insensitive, so the comparison is too: a fixture answering
 * `x-request-id` is as declared as one answering `X-Request-Id`. Empty means every header of
 * the fixture is part of the contract.
 */
export function undeclaredResponseHeaders(document: FixtureDocumentShape): readonly string[] {
  const headerNames = declaredResponseHeaderNames(document.operation_id, document.status);
  const declared = new Set(headerNames.map((name) => name.toLowerCase()));
  return Object.keys(document.headers).filter((name) => !declared.has(name.toLowerCase()));
}

/**
 * The fixture's `input.json`, parsed and checked against the T12.1 document contract: exactly
 * the six members, `body` optional, and the five fixed ones typed. A throw names the fixture,
 * so a malformed document is attributable without a second lookup.
 */
export function readDocument(fixture: Fixture): FixtureDocumentShape {
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
export function runSummarySchema(): Record<string, unknown> {
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
export function requiredMembers(schema: Record<string, unknown>): readonly string[] {
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
export function requiredRunUuidField(): string {
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
export function corpusDocument(name: string): FixtureDocumentShape {
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
export function keysetTicks(instant: string): string {
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
export function runsListDocument(): FixtureDocumentShape {
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
export function runsListSubject(): {
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
