// 09.1.1-T12.2 (plan §9 "Fixture backend is dev-only and contract-valid"; AC10): the fixture
// corpus is handcrafted JSON, so nothing but these specs stops it drifting from
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
import { parse as parseYaml } from 'yaml';

/** The corpus domain segment of `fixtures/<domain>/<name>/`. */
export const WEB_API_DOMAIN = 'web-api';

/**
 * How many `$ref` hops a response or header object may take before the check calls it
 * unresolved — the same bound `packages/cli`'s response validator uses.
 */
const MAX_REFERENCE_HOPS = 16;

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
export const WEB_API_FIXTURES = join(REPOSITORY_ROOT, 'fixtures', WEB_API_DOMAIN);

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
export const VALIDATOR = createOpenApiResponseValidator(CONTRACT_DOCUMENT);

/** The one shape guard this spec needs for two documents it does not own. */
export function isRecord(value: unknown): value is Record<string, unknown> {
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
 * - the inlined document makes a root-relative `#/...` reference that survives the document's own
 *   local references being expanded in place (a cycle, or a document larger than the node budget),
 *   which would resolve against `contracts/openapi.yaml` instead of the file it was written for.
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
  if (target === undefined) {
    return undefined;
  }
  // A standalone document may use local `#/$defs/...` references of its own. Inlined as they
  // stand they would resolve against `contracts/openapi.yaml` instead of the file that wrote
  // them, so they are expanded in place first; anything a cycle or the node budget leaves
  // unresolved stays a root-relative reference and is refused below, exactly as before.
  const localised = expandLocalReferences(target, parsed, [pointer], { left: MAX_LOCAL_REF_NODES });
  if (hasRootRelativeRef(localised)) {
    return undefined;
  }

  const withoutIdentity: unknown = isRecord(localised)
    ? Object.fromEntries(
        Object.entries(localised).filter(([key]) => key !== '$id' && key !== '$schema'),
      )
    : localised;
  return inlineExternalSchemaRefs(withoutIdentity, [...inlined, path]);
}

/** How many nodes one standalone document may expand to; a runaway product stays refused. */
const MAX_LOCAL_REF_NODES = 20_000;

/**
 * One node of a standalone document with its own local `#/...` references expanded in place.
 * `stack` holds the pointers already being expanded, so a recursive schema leaves its reference
 * in place rather than looping; `budget` bounds the total work for a document that would
 * otherwise multiply out.
 */
function expandLocalReferences(
  node: unknown,
  document: unknown,
  stack: readonly string[],
  budget: { left: number },
): unknown {
  if (budget.left <= 0) {
    return node;
  }
  budget.left -= 1;
  if (Array.isArray(node)) {
    return node.map((entry) => expandLocalReferences(entry, document, stack, budget));
  }
  if (!isRecord(node)) {
    return node;
  }

  const reference = node['$ref'];
  if (typeof reference === 'string' && reference.startsWith('#/') && !stack.includes(reference)) {
    const target = resolveContractPointerIn(document, reference);
    if (target !== undefined) {
      const inlined = expandLocalReferences(target, document, [...stack, reference], budget);
      const siblings = Object.entries(node).filter(([key]) => key !== '$ref');
      if (siblings.length === 0) {
        return inlined;
      }
      // 2020-12 applies `$ref` siblings next to the reference, so they stay side by side.
      const rest: Record<string, unknown> = {};
      for (const [key, value] of siblings) {
        rest[key] = expandLocalReferences(value, document, stack, budget);
      }
      return { allOf: [inlined, rest] };
    }
  }

  const copy: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(node)) {
    copy[key] = expandLocalReferences(value, document, stack, budget);
  }
  return copy;
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

/** Resolve a local `#/...` JSON pointer inside `contracts/openapi.yaml`. */
export function resolveContractPointer(reference: string): unknown {
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
export function declaredResponseHeaderNames(
  operationId: string,
  status: number,
): readonly string[] {
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

/** The `ContractValidationResult` rows as one readable line for a failure message. */
export function describeErrors(errors: readonly ContractValidationError[]): string {
  if (errors.length === 0) {
    return 'no errors';
  }
  return errors
    .map((error) => `${error.path === '' ? '<root>' : error.path}: ${error.reason}`)
    .join('; ');
}
