// 09.1.1-T12.1: the OpenAPI response validator the web fixture backend
// (09.1.1-T12) serves fixtures through and its contract check asserts. The
// positive cases run against the real contracts/openapi.yaml, so a change there
// that silently renames a status key, a component schema or a required member
// fails here instead of in the browser; the synthetic document covers the
// shapes this document does not use today (a `default` response, a chain of
// response `$ref`s, a recursive component schema).
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { parse } from 'yaml';

import { createOpenApiResponseValidator } from './openapiResponse.js';
import type { OpenApiResponseValidator } from './openapiResponse.js';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');

/** The real contract document, parsed once. */
function realValidator(): OpenApiResponseValidator {
  const document: unknown = parse(readFileSync(join(repoRoot, 'contracts/openapi.yaml'), 'utf8'));
  return createOpenApiResponseValidator(document);
}

const validator = realValidator();

/** The stored identity block of a run (`RunRef`): every member present. */
const runRef = {
  kind: 'branch',
  name: 'main',
  pr_number: null,
  head_sha: 'a'.repeat(40),
  base_sha: 'b'.repeat(40),
  trigger_event: 'push',
  is_fork: false,
};

/** One `RunSummary` row, as `listRuns` serves it. */
const runSummary = {
  run_id: '0192f0e2-3f4a-7c8d-9e0f-1a2b3c4d5e6f',
  state: 'queued',
  created_at: '2026-10-24T00:00:00Z',
  ref: runRef,
  unverified_ref: false,
  completeness: null,
  parent_run_id: null,
  rerun_scope: null,
};

/** A minimal valid `RunPage` body for `GET /v1/projects/{p}/runs` (200). */
const runPage = { items: [runSummary], next_cursor: null };

/** The same row with `run_id` omitted — a required-member failure one level down. */
const runSummaryWithoutId = {
  state: 'queued',
  created_at: '2026-10-24T00:00:00Z',
  ref: runRef,
  unverified_ref: false,
  completeness: null,
  parent_run_id: null,
  rerun_scope: null,
};

/** The 00 §9.2 envelope body a 403 carries. */
const forbiddenEnvelope = {
  error: {
    code: 'forbidden',
    message: 'missing run:read',
    request_id: '0192f0e2-3f4a-7c8d-9e0f-1a2b3c4d5e6f',
    retryable: false,
  },
};

/** The envelope's `error` object with `retryable` omitted. */
const incompleteEnvelopeError = {
  code: 'forbidden',
  message: 'missing run:read',
  request_id: '0192f0e2-3f4a-7c8d-9e0f-1a2b3c4d5e6f',
};

/** A document whose component schemas recurse and whose responses reference each other. */
const syntheticDocument = {
  openapi: '3.1.0',
  paths: {
    '/widgets': {
      get: {
        operationId: 'listWidgets',
        responses: {
          '200': { $ref: '#/components/responses/Widgets' },
          '201': { $ref: '#/components/responses/WidgetsAlias' },
          '202': { $ref: '#/components/responses/External' },
          default: { $ref: '#/components/responses/Fault' },
        },
      },
    },
  },
  components: {
    responses: {
      WidgetsAlias: { $ref: '#/components/responses/Widgets' },
      Widgets: {
        description: 'page of widgets',
        content: {
          'application/json': { schema: { $ref: '#/components/schemas/WidgetPage' } },
        },
      },
      Fault: {
        description: 'unexpected failure',
        content: {
          'application/json': { schema: { $ref: '#/components/schemas/Fault' } },
        },
      },
      // A schema this one document cannot compile: a file-relative reference, the
      // shape `contracts/openapi.yaml` itself uses for `PolicyRevisionDto.body`.
      External: {
        description: 'body described only by an external file',
        content: {
          'application/json': { schema: { $ref: './external.json' } },
        },
      },
    },
    schemas: {
      WidgetPage: {
        type: 'object',
        required: ['items'],
        properties: {
          items: { type: 'array', items: { $ref: '#/components/schemas/Widget' } },
        },
      },
      Widget: {
        type: 'object',
        required: ['id', 'children'],
        properties: {
          id: { type: 'string' },
          children: { type: 'array', items: { $ref: '#/components/schemas/Widget' } },
        },
      },
      Fault: {
        type: 'object',
        required: ['fault'],
        properties: { fault: { type: 'string' } },
      },
    },
  },
};

describe('openapi response validator: routes', () => {
  it('maps an operationId to its method and path template', () => {
    expect(validator.operation('listRuns')).toEqual({ method: 'GET', path: '/projects/{p}/runs' });
    expect(validator.operation('updateProject')).toEqual({
      method: 'PATCH',
      path: '/projects/{p}',
    });
    expect(validator.operation('removeMember')).toEqual({
      method: 'DELETE',
      path: '/orgs/{o}/memberships/{m}',
    });
  });

  it('answers undefined for an operationId the document does not declare', () => {
    expect(validator.operation('fixtureNotAnOperation')).toBeUndefined();
  });
});

describe('openapi response validator: declared responses', () => {
  it('accepts a minimal valid listRuns page', () => {
    expect(validator.validateResponse('listRuns', 200, runPage).errors).toEqual([]);
  });

  it('reports a missing required member with its path', () => {
    expect(validator.validateResponse('listRuns', 200, { items: [runSummary] }).errors).toEqual([
      { index: null, path: '/next_cursor', reason: 'required' },
    ]);
  });

  it('reports a missing required member of a page item with its indexed path', () => {
    expect(
      validator.validateResponse('listRuns', 200, {
        items: [runSummaryWithoutId],
        next_cursor: null,
      }).errors,
    ).toEqual([{ index: null, path: '/items/0/run_id', reason: 'required' }]);
  });

  it('reports a member of the wrong type', () => {
    expect(
      validator.validateResponse('listRuns', 200, { items: [], next_cursor: 5 }).errors,
    ).toEqual([{ index: null, path: '/next_cursor', reason: 'type' }]);
  });

  it('reports an unknown operationId', () => {
    expect(validator.validateResponse('fixtureNotAnOperation', 200, {}).errors).toEqual([
      { index: null, path: '', reason: 'unknown_operation' },
    ]);
  });

  it('reports a status the operation does not declare', () => {
    expect(validator.validateResponse('listRuns', 418, runPage).errors).toEqual([
      { index: null, path: '', reason: 'undeclared_status' },
    ]);
  });

  it('accepts the 00 §9.2 envelope against the shared Forbidden response schema', () => {
    expect(validator.validateResponse('listRuns', 403, forbiddenEnvelope).errors).toEqual([]);
  });

  it('reports a malformed envelope member', () => {
    expect(
      validator.validateResponse('listRuns', 403, { error: incompleteEnvelopeError }).errors,
    ).toEqual([{ index: null, path: '/error/retryable', reason: 'required' }]);
  });

  it('accepts an absent body on a declared no-content response', () => {
    expect(validator.validateResponse('removeMember', 204, null).errors).toEqual([]);
    expect(validator.validateResponse('cancelRun', 202, undefined).errors).toEqual([]);
  });

  it('reports a body on a declared no-content response', () => {
    expect(validator.validateResponse('removeMember', 204, { removed: true }).errors).toEqual([
      { index: null, path: '', reason: 'unexpected_body' },
    ]);
    expect(validator.validateResponse('cancelRun', 202, {}).errors).toEqual([
      { index: null, path: '', reason: 'unexpected_body' },
    ]);
  });
});

describe('openapi response validator: synthetic document', () => {
  const synthetic = createOpenApiResponseValidator(syntheticDocument);

  it('follows a chain of response $refs and resolves recursive component schemas', () => {
    const page = { items: [{ id: 'root', children: [{ id: 'leaf', children: [] }] }] };

    expect(synthetic.validateResponse('listWidgets', 200, page).errors).toEqual([]);
    expect(synthetic.validateResponse('listWidgets', 201, page).errors).toEqual([]);
  });

  it('locates a failure inside a recursive component schema', () => {
    expect(
      synthetic.validateResponse('listWidgets', 200, {
        items: [{ id: 'root', children: [{ children: [] }] }],
      }).errors,
    ).toEqual([{ index: null, path: '/items/0/children/0/id', reason: 'required' }]);
  });

  it('falls back to the default response for an undeclared status', () => {
    expect(synthetic.validateResponse('listWidgets', 500, { fault: 'boom' }).errors).toEqual([]);
    expect(synthetic.validateResponse('listWidgets', 500, {}).errors).toEqual([
      { index: null, path: '/fault', reason: 'required' },
    ]);
  });

  it('reports a schema this one document cannot compile', () => {
    expect(synthetic.validateResponse('listWidgets', 202, { anything: true }).errors).toEqual([
      { index: null, path: '', reason: 'schema_unavailable' },
    ]);
  });

  it('treats a document without paths as declaring no operations', () => {
    const empty = createOpenApiResponseValidator({ openapi: '3.1.0' });

    expect(empty.operation('listRuns')).toBeUndefined();
    expect(empty.validateResponse('listRuns', 200, runPage).errors).toEqual([
      { index: null, path: '', reason: 'unknown_operation' },
    ]);
  });

  it('refuses a document that is not a parsed OpenAPI object', () => {
    expect(() => createOpenApiResponseValidator(null)).toThrow(TypeError);
    expect(() => createOpenApiResponseValidator('openapi.yaml')).toThrow(TypeError);
  });
});
