import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ManualCaseDetailDto } from '@evidgate/sdk';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { ApiRequestError, isApiRequestError } from '../http/api-error';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import {
  AuthoringCaseConflict,
  AuthoringCaseValidation,
  AuthoringEditorDataService,
} from './authoring-editor-data.service';

const baseUrl = location.origin + API_BASE_PATH;
const caseUrl = baseUrl + '/projects/p1/authoring/manual-cases/c1';
const versionsUrl = baseUrl + '/projects/p1/authoring/versions';
const versionUrl = versionsUrl + '/v1';
const restoreUrl = versionUrl + '/restore';

const detail: ManualCaseDetailDto = {
  case_id: 'c1',
  code: 'AUTH-001',
  checklist_item_id: 'i1',
  node_id: 'n1',
  title: 'Sign in',
  description: null,
  precondition: null,
  steps: [{ no: 1, action: 'Open the sign-in page', expected: 'The form renders' }],
  priority: 'high',
  state: 'draft',
  version: 2,
  created_at: '2026-11-01T12:00:00Z',
  updated_at: '2026-11-02T12:00:00Z',
};

function envelope(code: string, details: readonly Record<string, unknown>[]) {
  return JSON.stringify({
    error: {
      code,
      message: 'Refused',
      request_id: '018f0000-0000-7000-8000-00000000b441',
      retryable: false,
      details,
    },
  });
}

/** Exact `urlWithParams` match: the SDK appends the query only where the operation accepts one. */
function at(urlPart: string) {
  return (request: { url: string }) =>
    request.url === urlPart || request.url.startsWith(urlPart + '?');
}

describe('AuthoringEditorDataService', () => {
  let http: HttpTestingController;
  let data: AuthoringEditorDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(baseUrl),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    data = TestBed.inject(AuthoringEditorDataService);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  it('reads a case with the quoted ETag the next save sends back', async () => {
    const read = data.readCase('p1', 'c1');
    (await takeRequest(http, at(caseUrl))).flush(JSON.stringify(detail), {
      headers: { ...JSON_HEADERS, ETag: '"2"' },
    });
    const loaded = await read;
    expect(loaded.view.case_id).toBe('c1');
    expect(loaded.etag).toBe('"2"');
  });

  it('saves under If-Match with the intent key, then returns the reloaded case', async () => {
    const pending = data.updateCase('p1', 'c1', { title: 'Sign in twice' }, '"2"', 'intent-one');
    const patch = await takeRequest(http, at(caseUrl));
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.headers.get('If-Match')).toBe('"2"');
    expect(patch.request.headers.get('Idempotency-Key')).toBe('intent-one');
    // The adapter forwards the SDK's serialized body as text (ADR-0008 rule 3).
    expect(JSON.parse(patch.request.body as string)).toEqual({ title: 'Sign in twice' });
    patch.flush(JSON.stringify({ version: 3, changed: true }), { headers: JSON_HEADERS });
    const reload = await takeRequest(http, at(caseUrl));
    expect(reload.request.method).toBe('GET');
    reload.flush(JSON.stringify({ ...detail, title: 'Sign in twice', version: 3 }), {
      headers: { ...JSON_HEADERS, ETag: '"3"' },
    });
    const saved = await pending;
    expect(saved.view.version).toBe(3);
    expect(saved.view.title).toBe('Sign in twice');
    expect(saved.etag).toBe('"3"');
  });

  it('answers a stale ETag with the freshly read server head', async () => {
    const pending = data
      .updateCase('p1', 'c1', { title: 'Mine' }, '"1"', 'intent-two')
      .catch((error: unknown) => error);
    (await takeRequest(http, at(caseUrl))).flush(
      envelope('precondition_failed', [{ path: '/version', reason: '2' }]),
      {
        status: 412,
        statusText: 'Precondition Failed',
        headers: JSON_HEADERS,
      },
    );
    (await takeRequest(http, at(caseUrl))).flush(
      JSON.stringify({ ...detail, title: 'Theirs', version: 2 }),
      { headers: { ...JSON_HEADERS, ETag: '"2"' } },
    );
    const conflict = await pending;
    expect(conflict).toBeInstanceOf(AuthoringCaseConflict);
    if (conflict instanceof AuthoringCaseConflict) {
      expect(conflict.error.kind).toBe('precondition_failed');
      expect(conflict.current.view.title).toBe('Theirs');
      expect(conflict.current.etag).toBe('"2"');
      expect(isApiRequestError(conflict)).toBe(true);
    }
  });

  it('keeps the mapped refusal when the conflict snapshot itself cannot be read', async () => {
    const pending = data
      .updateCase('p1', 'c1', { title: 'Mine' }, '"1"', 'intent-three')
      .catch((error: unknown) => error);
    (await takeRequest(http, at(caseUrl))).flush(
      envelope('precondition_failed', [{ path: '/version', reason: '2' }]),
      {
        status: 412,
        statusText: 'Precondition Failed',
        headers: JSON_HEADERS,
      },
    );
    (await takeRequest(http, at(caseUrl))).flush(envelope('forbidden', []), {
      status: 403,
      statusText: 'Forbidden',
      headers: JSON_HEADERS,
    });
    const failure = await pending;
    expect(failure).toBeInstanceOf(ApiRequestError);
    expect(failure).not.toBeInstanceOf(AuthoringCaseConflict);
    expect((failure as ApiRequestError).error.kind).toBe('precondition_failed');
  });

  it('groups a 422 body by the pointer of each refused field', async () => {
    const pending = data
      .updateCase('p1', 'c1', { title: '', steps: [] }, '"2"', 'intent-four')
      .catch((error: unknown) => error);
    (await takeRequest(http, at(caseUrl))).flush(
      envelope('request_invalid', [
        { path: '/title', reason: 'required' },
        { path: '/steps/0/action', reason: 'required' },
        { path: '/title', reason: 'max_length', limit: 200, actual: 201 },
      ]),
      { status: 422, statusText: 'Unprocessable Content', headers: JSON_HEADERS },
    );
    const failure = await pending;
    expect(failure).toBeInstanceOf(AuthoringCaseValidation);
    if (failure instanceof AuthoringCaseValidation) {
      expect(failure.error.kind).toBe('validation');
      expect(failure.fields['/title']).toEqual(['required', 'max_length']);
      expect(failure.fields['/steps/0/action']).toEqual(['required']);
    }
  });

  it('keeps a 422 that names no field on the plain mapped refusal', async () => {
    const pending = data
      .updateCase('p1', 'c1', { title: 'Mine' }, '"2"', 'intent-six')
      .catch((error: unknown) => error);
    (await takeRequest(http, at(caseUrl))).flush(
      envelope('request_invalid', [{ reason: 'authoring_parent_invalid' }]),
      { status: 422, statusText: 'Unprocessable Content', headers: JSON_HEADERS },
    );
    const failure = await pending;
    expect(failure).toBeInstanceOf(ApiRequestError);
    expect(failure).not.toBeInstanceOf(AuthoringCaseValidation);
    expect((failure as ApiRequestError).error.code).toBe('request_invalid');
  });

  it('asks the timeline for one manual case and forwards its cursor', async () => {
    const pending = data.versions('p1', 'c1', { limit: 2, cursor: 'page-2' });
    const request = await takeRequest(http, at(versionsUrl));
    const url = new URL(request.request.urlWithParams);
    expect(url.searchParams.get('entity_kind')).toBe('manual_test_case');
    expect(url.searchParams.get('entity_id')).toBe('c1');
    expect(url.searchParams.get('limit')).toBe('2');
    expect(url.searchParams.get('cursor')).toBe('page-2');
    request.flush(
      JSON.stringify({
        items: [
          {
            version_id: 'v1',
            entity_kind: 'manual_test_case',
            entity_id: 'c1',
            version_no: 1,
            batch_id: 'b1',
            change_kind: 'create',
            source: 'manual',
            content_hash: 'sha256:aa',
            author: null,
            reason: null,
            ai_run_id: null,
            created_at: '2026-11-01T12:00:00Z',
          },
        ],
        next_cursor: null,
      }),
      { headers: JSON_HEADERS },
    );
    const page = await pending;
    expect(page.items[0]?.version_no).toBe(1);
    expect(page.next_cursor).toBeNull();
  });

  it('reads one version with the content hash as its ETag', async () => {
    const pending = data.version('p1', 'v1');
    (await takeRequest(http, at(versionUrl))).flush(
      JSON.stringify({
        version_id: 'v1',
        entity_kind: 'manual_test_case',
        entity_id: 'c1',
        version_no: 1,
        batch_id: 'b1',
        change_kind: 'create',
        source: 'manual',
        content_hash: 'sha256:aa',
        author: null,
        reason: null,
        ai_run_id: null,
        created_at: '2026-11-01T12:00:00Z',
        content: { title: 'Sign in' },
      }),
      { headers: { ...JSON_HEADERS, ETag: '"sha256:aa"' } },
    );
    const loaded = await pending;
    expect(loaded.view.content).toEqual({ title: 'Sign in' });
    expect(loaded.etag).toBe('"sha256:aa"');
  });

  it('restores with only the intent key the contract declares', async () => {
    const pending = data.restoreVersion('p1', 'v1', 'intent-five');
    const request = await takeRequest(http, at(restoreUrl));
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('intent-five');
    expect(request.request.headers.has('If-Match')).toBe(false);
    expect(request.request.body).toBeNull();
    request.flush(JSON.stringify({ version: 3 }), { headers: JSON_HEADERS });
    expect((await pending).version).toBe(3);
  });

  for (const operation of ['readCase', 'versions', 'version'] as const) {
    it('forwards abort for ' + operation, async () => {
      const controller = new AbortController();
      const pending = (
        operation === 'readCase'
          ? data.readCase('p1', 'c1', controller.signal)
          : operation === 'versions'
            ? data.versions('p1', 'c1', {}, controller.signal)
            : data.version('p1', 'v1', controller.signal)
      ).catch((error: unknown) => error);
      const request = await takeRequest(http, () => true);
      controller.abort();
      expect(request.cancelled).toBe(true);
      expect(await pending).toBeInstanceOf(DOMException);
    });
  }
});
