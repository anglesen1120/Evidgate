// 09.1.1-T3.2 (00 §9.1/§9.2; ADR-0008 decision (a) and rule 1): the contract smoke.
//
// Three things can silently drift apart: `contracts/openapi.yaml`, the generated SDK and the
// recorded fixture under `testing/`. This spec derives the six operations the app calls from the
// spec on every run and fails when the fixture differs (with the re-record command in the
// message), then drives each recorded operation through the real stack — SDK function ->
// `ApiGateway` -> the Angular adapter -> `HttpClient` under the test backend — and asserts the
// method and same-origin `/v1` URL the wire actually carries, so an SDK regeneration that moves
// an operation fails here instead of in a feature page. Finally it feeds the recorded 00 §9.2
// envelope back through the stack as a 403 and requires a typed `ApiRequestError`.
//
// The URL is absolute because the SDK builds `new Request(baseUrl + path)` and Node's `Request`
// refuses a relative URL; it is the same origin the app calls in production, where `/v1` is
// resolved against the page.

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  cancelRun,
  completeArtifact,
  createManifest,
  createRun,
  getRun,
  isErrorEnvelope,
  listRuns,
  type Client,
  type ErrorEnvelope,
  type ManifestV1,
} from '@evidgate/sdk';
import { parse as parseYaml } from 'yaml';

import { EXACT_MATCH_PROFILE, takeRequestWith } from '../../shared/testing/http-data.testing';
import { isApiRequestError } from './api-error';
import { provideApi } from './api-client';
import { ApiGateway, type SdkResult } from './api-gateway';
import { API_BASE_PATH } from './api-origin';
import { createIntent, intentHeaders } from './intent';

const takeRequest = takeRequestWith(EXACT_MATCH_PROFILE);

const RECORDED_OPERATION_IDS: readonly string[] = [
  'listRuns',
  'getRun',
  'createRun',
  'cancelRun',
  'createManifest',
  'completeArtifact',
];

const RE_RECORD =
  'the contract fixture and contracts/openapi.yaml disagree — re-record with ' +
  '`node apps/web/scripts/record-openapi-fixture.mjs` from the repository root';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
// `ng test` runs with `apps/web` as the working directory.
const FIXTURE_PATH = resolve(
  process.cwd(),
  'src/app/core/http/testing/openapi-operations.fixture.json',
);
const CONTRACT_PATH = resolve(process.cwd(), '../../contracts/openapi.yaml');

const HTTP_METHODS: Readonly<Record<string, true>> = {
  get: true,
  put: true,
  post: true,
  delete: true,
  options: true,
  head: true,
  patch: true,
  trace: true,
};

/** One recorded `contracts/openapi.yaml` operation, in the shape the fixture stores. */
interface RecordedOperation {
  readonly operationId: string;
  readonly method: string;
  readonly path: string;
  readonly idempotencyKeyRequired: boolean;
  readonly responseStatuses: readonly number[];
}

/** A minimal manifest that satisfies `ManifestV1`; the smoke never asserts on its content. */
const MINIMAL_MANIFEST: ManifestV1 = {
  schema_version: 1,
  suite_revision_id: 'f0d1b7e2-3c4a-4f5b-8e6d-7a9c0b1d2e3f',
  planned_executions: [
    {
      case_revision_id: 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
      variant: { params: {} },
      target_env: 'linux-x64',
    },
  ],
  matrix: {
    case_revision_ids: ['a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d'],
    variants: [{ params: {} }],
    target_envs: ['linux-x64'],
  },
  shard_plan: { shards: [{ shard_id: 'shard-1', from_index: 0, to_index: 1 }] },
  env_needs: { target_envs: [{ target_env: 'linux-x64' }] },
};

/** How the smoke calls one operation, and the path parameters its URL needs. */
interface OperationDriver {
  readonly params: Readonly<Record<string, string>>;
  readonly call: (client: Client) => Promise<SdkResult>;
}

const OPERATION_DRIVERS: Readonly<Record<string, OperationDriver>> = {
  listRuns: {
    params: { p: 'p-1' },
    call: (client) => listRuns({ client, path: { p: 'p-1' } }),
  },
  getRun: {
    params: { r: 'r-1' },
    call: (client) => getRun({ client, path: { r: 'r-1' } }),
  },
  createRun: {
    params: { p: 'p-1' },
    call: (client) =>
      createRun({
        client,
        path: { p: 'p-1' },
        body: { manifest_id: 'mf-1' },
        headers: intentHeaders(createIntent()),
      }),
  },
  cancelRun: {
    params: { r: 'r-1' },
    call: (client) =>
      cancelRun({ client, path: { r: 'r-1' }, headers: intentHeaders(createIntent()) }),
  },
  createManifest: {
    params: { p: 'p-1' },
    call: (client) =>
      createManifest({
        client,
        path: { p: 'p-1' },
        body: MINIMAL_MANIFEST,
        headers: intentHeaders(createIntent()),
      }),
  },
  completeArtifact: {
    params: { a: 'a-1' },
    call: (client) =>
      completeArtifact({ client, path: { a: 'a-1' }, headers: intentHeaders(createIntent()) }),
  },
};

/**
 * The one shape guard this spec needs: it parses two documents it does not own — the YAML
 * contract and the recorded JSON fixture — and every field below is checked with `typeof`,
 * `Array.isArray` or `Object.entries` before use.
 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** Resolve a local `#/...` JSON pointer; the recorded parameters live in the same file. */
function resolveLocalRef(spec: unknown, value: unknown): unknown {
  if (!isRecord(value)) {
    return value;
  }
  const reference = value['$ref'];
  if (typeof reference !== 'string') {
    return value;
  }
  if (!reference.startsWith('#/')) {
    throw new Error(`contracts/openapi.yaml uses a non-local $ref (${reference}); ${RE_RECORD}`);
  }
  return reference
    .slice(2)
    .split('/')
    .reduce<unknown>((node, token) => (isRecord(node) ? node[token] : undefined), spec);
}

/** Path-level and operation-level parameters, every `$ref` dereferenced. */
function declaredParameters(
  spec: unknown,
  pathItem: Record<string, unknown>,
  operation: Record<string, unknown>,
): readonly unknown[] {
  const collected: unknown[] = [];
  for (const owner of [pathItem, operation]) {
    const parameters = owner['parameters'];
    if (!Array.isArray(parameters)) {
      continue;
    }
    for (const parameter of parameters) {
      collected.push(resolveLocalRef(spec, parameter));
    }
  }
  return collected;
}

function responseStatusesOf(operation: Record<string, unknown>): readonly number[] {
  const responses = operation['responses'];
  if (!isRecord(responses)) {
    return [];
  }
  return Object.keys(responses)
    .map(Number)
    .filter((status) => Number.isInteger(status))
    .sort((left, right) => left - right);
}

function declaresIdempotencyKey(
  spec: unknown,
  pathItem: Record<string, unknown>,
  operation: Record<string, unknown>,
): boolean {
  return declaredParameters(spec, pathItem, operation).some(
    (parameter) =>
      isRecord(parameter) &&
      parameter['in'] === 'header' &&
      typeof parameter['name'] === 'string' &&
      parameter['name'].toLowerCase() === 'idempotency-key' &&
      parameter['required'] === true,
  );
}

/**
 * The recorded rows, re-derived from `contracts/openapi.yaml`. This is the same derivation the
 * recorder script performs, kept here so the test never trusts the file it is checking.
 */
function deriveOperations(): readonly RecordedOperation[] {
  const spec: unknown = parseYaml(readFileSync(CONTRACT_PATH, 'utf8'));
  if (!isRecord(spec) || !isRecord(spec['paths'])) {
    throw new Error(`contracts/openapi.yaml did not parse into an OpenAPI document; ${RE_RECORD}`);
  }
  const found = new Map<string, RecordedOperation>();
  for (const [path, pathItem] of Object.entries(spec['paths'])) {
    if (!isRecord(pathItem)) {
      continue;
    }
    for (const [method, operation] of Object.entries(pathItem)) {
      if (HTTP_METHODS[method] !== true || !isRecord(operation)) {
        continue;
      }
      const operationId = operation['operationId'];
      if (typeof operationId !== 'string' || !RECORDED_OPERATION_IDS.includes(operationId)) {
        continue;
      }
      found.set(operationId, {
        operationId,
        method: method.toUpperCase(),
        path,
        idempotencyKeyRequired: declaresIdempotencyKey(spec, pathItem, operation),
        responseStatuses: responseStatusesOf(operation),
      });
    }
  }
  return RECORDED_OPERATION_IDS.map((operationId) => {
    const row = found.get(operationId);
    if (row === undefined) {
      throw new Error(`contracts/openapi.yaml no longer declares ${operationId}; ${RE_RECORD}`);
    }
    return row;
  });
}

/** The fixture as written by `apps/web/scripts/record-openapi-fixture.mjs`, untyped on purpose. */
function readFixture(): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(readFileSync(FIXTURE_PATH, 'utf8'));
    if (isRecord(parsed)) {
      return parsed;
    }
  } catch {
    // Reported once, below, with the command that repairs it.
  }
  throw new Error(`the recorded contract fixture is missing or unreadable; ${RE_RECORD}`);
}

/** The fixture's `00 §9.2` example, validated as an envelope before it is fed to the stack. */
function readEnvelopeExample(): ErrorEnvelope {
  const example: unknown = readFixture()['errorEnvelopeExample'];
  if (!isErrorEnvelope(example)) {
    throw new Error(`the fixture's errorEnvelopeExample is not a 00 §9.2 envelope; ${RE_RECORD}`);
  }
  return example;
}

/** The same-origin URL the wire must carry for one recorded row. */
function expectedUrl(row: RecordedOperation, driver: OperationDriver): string {
  const substituted = row.path.replace(/\{(\w+)\}/g, (_match: string, name: string) => {
    const value = driver.params[name];
    if (value === undefined) {
      throw new Error(
        `the smoke has no value for {${name}} of ${row.operationId}; add it to OPERATION_DRIVERS`,
      );
    }
    return value;
  });
  return `${API_BASE_URL}${substituted}`;
}

describe('OpenAPI contract smoke (09.1.1-T3.2)', () => {
  let httpMock: HttpTestingController;
  let gateway: ApiGateway;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(API_BASE_URL),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    gateway = TestBed.inject(ApiGateway);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('matches the recorded fixture, so fixture drift fails the suite', () => {
    const fixture = readFixture();
    expect(fixture['operations'], RE_RECORD).toEqual(deriveOperations());
  });

  it('records a 00 §9.2 envelope example for the 403 csrf_failed refusal', () => {
    const example = readEnvelopeExample();
    expect(example.error.code).toBe('csrf_failed');
    expect(example.error.request_id.length).toBeGreaterThan(0);
    expect(example.error.retryable).toBe(false);
  });

  for (const row of deriveOperations()) {
    it(`sends ${row.operationId} as ${row.method} ${row.path} to ${API_BASE_PATH}`, async () => {
      const driver = OPERATION_DRIVERS[row.operationId];
      if (driver === undefined) {
        throw new Error(`no smoke driver for ${row.operationId}; add one to OPERATION_DRIVERS`);
      }
      const successStatus = row.responseStatuses.find((status) => status >= 200 && status < 300);
      if (successStatus === undefined) {
        throw new Error(`${row.operationId} records no success status; ${RE_RECORD}`);
      }
      const url = expectedUrl(row, driver);

      const pending = gateway.call(driver.call);
      const request = await takeRequest(
        httpMock,
        (candidate) => candidate.method === row.method && candidate.url === url,
      );
      expect(request.request.method).toBe(row.method);
      expect(request.request.url).toBe(url);

      const idempotencyKey = request.request.headers.get('Idempotency-Key');
      if (row.idempotencyKeyRequired) {
        expect(idempotencyKey).toBeTruthy();
      } else {
        expect(idempotencyKey).toBeNull();
      }

      request.flush('{}', {
        status: successStatus,
        statusText: '',
        headers: { 'Content-Type': 'application/json' },
      });
      await pending;
    });
  }

  it('throws a typed ApiRequestError with the envelope code and request id', async () => {
    const example = readEnvelopeExample();
    const pending = gateway.call((client) =>
      createRun({
        client,
        path: { p: 'p-1' },
        body: { manifest_id: 'mf-1' },
        headers: intentHeaders(createIntent()),
      }),
    );
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/runs`)).flush(
      JSON.stringify(example),
      { status: 403, statusText: '', headers: { 'Content-Type': 'application/json' } },
    );

    const cause: unknown = await pending.then(
      () => undefined,
      (error: unknown) => error,
    );
    expect(isApiRequestError(cause)).toBe(true);
    if (!isApiRequestError(cause)) {
      return;
    }
    expect(cause.error.kind).toBe('csrf_failed');
    expect(cause.error.status).toBe(403);
    expect(cause.error.code).toBe(example.error.code);
    expect(cause.error.requestId).toBe(example.error.request_id);
  });
});
