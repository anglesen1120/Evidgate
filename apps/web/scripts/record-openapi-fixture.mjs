#!/usr/bin/env node
// 09.1.1-T3.2 (00 §9.1/§9.2, ADR-0008 rule 1): record the operations fixture the web contract
// smoke compares against `contracts/openapi.yaml`.
//
// The fixture is data, never a hand-written type: `http/contract-smoke.spec.ts` re-derives the
// same rows from the spec on every run and fails on any difference, so an SDK regeneration or a
// spec edit that moves an operation must re-record here. Only the six operations the app calls
// today are recorded — extending the list is a code change in both files, by design.
//
//   node apps/web/scripts/record-openapi-fixture.mjs           # rewrite the fixture
//   node apps/web/scripts/record-openapi-fixture.mjs --check   # diff summary, exit 1 on drift
//
// Run from the repository root. `yaml` is a root devDependency; Node resolves it from there.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';

/** The operations `apps/web` consumes through `@evidgate/sdk` (RunsDataService and the smoke). */
export const RECORDED_OPERATION_IDS = Object.freeze([
  'listRuns',
  'getRun',
  'createRun',
  'cancelRun',
  'createManifest',
  'completeArtifact',
]);

/**
 * A 00 §9.2 envelope for the one refusal every cookie-authenticated mutation can produce: the
 * spec describes this body in prose (`components.responses.Forbidden`) and ships no example, so
 * the fixture carries one. `contract-smoke.spec.ts` feeds it back through the stack as a 403.
 */
export const ERROR_ENVELOPE_EXAMPLE = Object.freeze({
  error: {
    code: 'csrf_failed',
    message:
      'The X-XSRF-TOKEN header is missing or does not match the XSRF-TOKEN cookie (00 §9.1, T38).',
    request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
    retryable: false,
  },
});

/** Where the fixture lives, resolved from this script so the cwd does not matter. */
export const FIXTURE_URL = new URL(
  '../src/app/core/http/testing/openapi-operations.fixture.json',
  import.meta.url,
);

/** The contract the fixture is recorded from. */
export const CONTRACT_URL = new URL('../../../contracts/openapi.yaml', import.meta.url);

const HTTP_METHODS = new Set(['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace']);

/** Sorted keys at every depth, so two recordings of the same spec are byte-identical. */
function sortKeysDeep(value) {
  if (Array.isArray(value)) {
    return value.map(sortKeysDeep);
  }
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, sortKeysDeep(value[key])]),
    );
  }
  return value;
}

/** Resolve a local `#/…` JSON pointer; the spec keeps every recorded parameter in one file. */
function resolvePointer(document, pointer) {
  if (!pointer.startsWith('#/')) {
    throw new Error(`record-openapi-fixture: only local $refs are resolved, got ${pointer}`);
  }
  return pointer
    .slice(2)
    .split('/')
    .map((token) => token.replaceAll('~1', '/').replaceAll('~0', '~'))
    .reduce((node, token) => {
      if (node === null || typeof node !== 'object' || !(token in node)) {
        throw new Error(`record-openapi-fixture: unresolvable $ref ${pointer} (missing ${token})`);
      }
      return node[token];
    }, document);
}

/** `parameters` of one operation, path-level entries included, every `$ref` dereferenced. */
function resolveParameters(document, pathItem, operation) {
  const declared = [...(pathItem.parameters ?? []), ...(operation.parameters ?? [])];
  return declared.map((parameter) =>
    parameter.$ref ? resolvePointer(document, parameter.$ref) : parameter,
  );
}

/** True when the operation declares a required `Idempotency-Key` header (00 §9.1). */
function declaresIdempotencyKey(parameters) {
  return parameters.some(
    (parameter) =>
      parameter.in === 'header' &&
      String(parameter.name).toLowerCase() === 'idempotency-key' &&
      parameter.required === true,
  );
}

/** Every recorded operation, keyed by id, in declaration order of the spec. */
function findOperations(document) {
  const found = new Map();
  for (const [path, pathItem] of Object.entries(document.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem ?? {})) {
      if (!HTTP_METHODS.has(method) || operation === null || typeof operation !== 'object') {
        continue;
      }
      const { operationId } = operation;
      if (typeof operationId !== 'string' || !RECORDED_OPERATION_IDS.includes(operationId)) {
        continue;
      }
      found.set(operationId, {
        method,
        operation,
        parameters: resolveParameters(document, pathItem, operation),
        path,
      });
    }
  }
  return found;
}

/**
 * The fixture value for a parsed `contracts/openapi.yaml`: one row per recorded operation plus
 * the §9.2 envelope example. Pure, so `--check` and the smoke compare the same thing.
 */
export function buildFixture(document) {
  const found = findOperations(document);
  const missing = RECORDED_OPERATION_IDS.filter((operationId) => !found.has(operationId));
  if (missing.length > 0) {
    throw new Error(
      `record-openapi-fixture: contracts/openapi.yaml no longer declares ${missing.join(', ')}`,
    );
  }

  const operations = RECORDED_OPERATION_IDS.map((operationId) => {
    const entry = found.get(operationId);
    const responseStatuses = Object.keys(entry.operation.responses ?? {})
      .map(Number)
      .filter((status) => Number.isInteger(status))
      .sort((left, right) => left - right);
    return {
      operationId,
      method: entry.method.toUpperCase(),
      path: entry.path,
      idempotencyKeyRequired: declaresIdempotencyKey(entry.parameters),
      responseStatuses,
    };
  });

  return sortKeysDeep({
    errorEnvelopeExample: ERROR_ENVELOPE_EXAMPLE,
    generatedBy: 'node apps/web/scripts/record-openapi-fixture.mjs',
    operations,
  });
}

/** The width `prettier --check` enforces (`.prettierrc`); the recorded bytes must match it. */
const PRINT_WIDTH = 100;

/** The exact bytes the fixture file must hold: sorted keys, 2-space indent, trailing newline. */
export function serializeFixture(fixture) {
  return `${serializeValue(sortKeysDeep(fixture), 0, 0)}\n`;
}

/**
 * `JSON.stringify(value, null, 2)` with one difference: an array of primitives is printed on one
 * line when it fits. That is how prettier prints JSON, and `prettier --check` gates this file, so
 * the recording has to be prettier's own shape rather than one item per line.
 */
function serializeValue(value, depth, column) {
  if (Array.isArray(value)) {
    return serializeArray(value, depth, column);
  }
  if (isObject(value)) {
    const entries = Object.entries(value);
    if (entries.length === 0) {
      return '{}';
    }
    const inner = `${'  '.repeat(depth + 1)}`;
    const body = entries.map(([key, entry]) => {
      const prefix = `${inner}${JSON.stringify(key)}: `;
      return `${prefix}${serializeValue(entry, depth + 1, prefix.length)}`;
    });
    return `{\n${body.join(',\n')}\n${'  '.repeat(depth)}}`;
  }
  return JSON.stringify(value);
}

function serializeArray(value, depth, column) {
  if (value.length === 0) {
    return '[]';
  }
  const primitivesOnly = value.every((item) => !isObject(item) && !Array.isArray(item));
  if (primitivesOnly) {
    const inline = `[${value.map((item) => JSON.stringify(item)).join(', ')}]`;
    // One column for the comma prettier also has to fit before the line breaks.
    if (column + inline.length + 1 <= PRINT_WIDTH) {
      return inline;
    }
  }
  const inner = '  '.repeat(depth + 1);
  const body = value.map((item) => `${inner}${serializeValue(item, depth + 1, inner.length)}`);
  return `[\n${body.join(',\n')}\n${'  '.repeat(depth)}]`;
}

/** Byte-level rendering of a value, for the `--check` diff summary. */
function render(value) {
  return JSON.stringify(value);
}

function isObject(value) {
  return value !== null && typeof value === 'object';
}

/** Changed leaf paths between two values, capped so a rewrite prints a bounded summary. */
function changedPaths(expected, actual, prefix, out) {
  if (out.length >= 20) {
    return out;
  }
  if (isObject(expected) && isObject(actual)) {
    const keys = new Set([...Object.keys(expected), ...Object.keys(actual)]);
    for (const key of [...keys].sort()) {
      changedPaths(expected[key], actual[key], prefix ? `${prefix}.${key}` : key, out);
    }
    return out;
  }
  if (render(expected) !== render(actual)) {
    out.push(`  ${prefix}: recorded ${render(actual)} -> spec ${render(expected)}`);
  }
  return out;
}

/** Human-readable drift between the recorded fixture and the spec-derived one. */
export function describeDrift(expected, recorded) {
  const operations = new Map(
    (Array.isArray(recorded?.operations) ? recorded.operations : []).map((row) => [
      row?.operationId,
      row,
    ]),
  );
  const lines = [];
  for (const row of expected.operations) {
    const recordedRow = operations.get(row.operationId);
    operations.delete(row.operationId);
    if (recordedRow === undefined) {
      lines.push(`  ${row.operationId}: missing from the fixture`);
      continue;
    }
    const changed = changedPaths(row, recordedRow, row.operationId, []);
    lines.push(...changed);
  }
  for (const operationId of operations.keys()) {
    lines.push(`  ${String(operationId)}: no longer in contracts/openapi.yaml`);
  }
  const envelopeLines = changedPaths(
    expected.errorEnvelopeExample,
    recorded?.errorEnvelopeExample,
    'errorEnvelopeExample',
    [],
  );
  lines.push(...envelopeLines);
  return lines.length > 0 ? lines.join('\n') : '  (formatting differs only)';
}

function readJson(url) {
  try {
    return JSON.parse(readFileSync(url, 'utf8'));
  } catch (cause) {
    if (cause?.code === 'ENOENT') {
      return null;
    }
    throw cause;
  }
}

function main(argv) {
  const checkOnly = argv.includes('--check');
  const document = parseYaml(readFileSync(CONTRACT_URL, 'utf8'));
  const expected = buildFixture(document);
  const serialized = serializeFixture(expected);

  if (!checkOnly) {
    writeFileSync(FIXTURE_URL, serialized);
    process.stdout.write(`record-openapi-fixture: wrote ${fileURLToPath(FIXTURE_URL)}\n`);
    return 0;
  }

  const recorded = readJson(FIXTURE_URL);
  if (recorded === null) {
    process.stderr.write(
      `record-openapi-fixture: ${fileURLToPath(FIXTURE_URL)} is missing — run without --check\n`,
    );
    return 1;
  }

  const recordedText = readFileSync(FIXTURE_URL, 'utf8');
  if (recordedText === serialized) {
    process.stdout.write('record-openapi-fixture: fixture matches contracts/openapi.yaml\n');
    return 0;
  }

  const driftReport = describeDrift(expected, recorded);
  process.stderr.write('record-openapi-fixture: the fixture drifted from contracts/openapi.yaml\n');
  process.stderr.write(`${driftReport}\n`);
  process.stderr.write('Re-record with: node apps/web/scripts/record-openapi-fixture.mjs\n');
  return 1;
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
  process.exitCode = main(process.argv.slice(2));
}
