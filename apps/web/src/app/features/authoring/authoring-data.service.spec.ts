import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { API_CLIENT, provideApi } from '../../core/http/api-client';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { AuthoringDataService } from './authoring-data.service';

const READS = [
  { method: 'listTree', route: 'tree' },
  { method: 'listChecklistItems', route: 'checklist-items' },
  { method: 'listManualCases', route: 'manual-cases' },
] as const;
const EMPTY_PAGE = { items: [], next_cursor: null };
const BASE = `${location.origin}/v1/projects/project-1/authoring`;

function envelope(code: string) {
  return {
    error: {
      code,
      message: 'Request refused',
      request_id: 'request-authoring',
      retryable: code === 'unavailable' || code === 'rate_limited',
      details: [],
    },
  };
}

describe('AuthoringDataService', () => {
  let service: AuthoringDataService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideApi(`${location.origin}/v1`),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    service = TestBed.inject(AuthoringDataService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  for (const { method, route } of READS) {
    describe(method, () => {
      it('forwards the exact abort signal through the generated SDK', async () => {
        const get = vi.spyOn(TestBed.inject(API_CLIENT), 'get');
        const controller = new AbortController();
        const pending = service[method]('project-1', { limit: 25 }, controller.signal);
        const request = await takeRequest(http, `${BASE}/${route}`);
        expect(get).toHaveBeenCalledWith(expect.objectContaining({ signal: controller.signal }));
        expect(request.request.method).toBe('GET');
        request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
        await expect(pending).resolves.toEqual(EMPTY_PAGE);
      });

      it.each(['', '   '])('refuses project id %j without issuing a request', async (projectId) => {
        await expect(
          service[method](projectId, {}, new AbortController().signal),
        ).rejects.toMatchObject({
          error: { kind: 'unexpected', code: 'project_required' },
        });
        http.expectNone((request) => request.url.includes('/authoring/'));
      });

      it.each([
        [401, 'unauthenticated', 'unauthenticated'],
        [403, 'forbidden', 'forbidden'],
        [404, 'not_found', 'not_found'],
        [422, 'request_invalid', 'validation'],
        [429, 'rate_limited', 'rate_limited'],
        [500, 'internal_error', 'unexpected'],
        [503, 'unavailable', 'unavailable'],
      ] as const)('maps HTTP %s to the shared typed %s failure', async (status, code, kind) => {
        const pending = service[method]('project-1', {}, new AbortController().signal);
        const rejected = expect(pending).rejects.toMatchObject({
          error: {
            status,
            code,
            kind,
            retryAfterSeconds: status === 429 || status === 503 ? 7 : null,
          },
        });
        const request = await takeRequest(http, `${BASE}/${route}`);
        request.flush(JSON.stringify(envelope(code)), {
          status,
          statusText: 'Refused',
          headers: { ...JSON_HEADERS, 'Retry-After': '7' },
        });
        await rejected;
      });

      it('maps a transport failure to the shared typed network error', async () => {
        const pending = service[method]('project-1', {}, new AbortController().signal);
        const rejected = expect(pending).rejects.toMatchObject({
          error: { kind: 'network', status: 0 },
        });
        const request = await takeRequest(http, `${BASE}/${route}`);
        request.error(new ProgressEvent('error'));
        await rejected;
      });

      it('keeps caller cancellation distinct from a network failure', async () => {
        const controller = new AbortController();
        const pending = service[method]('project-1', {}, controller.signal);
        const rejected = expect(pending).rejects.toMatchObject({ name: 'AbortError' });
        const request = await takeRequest(http, `${BASE}/${route}`);
        controller.abort();
        expect(request.cancelled).toBe(true);
        await rejected;
      });

      it('refuses a repeated cursor as a typed unexpected contract failure', async () => {
        const pending = service[method](
          'project-1',
          { cursor: 'opaque+/=' },
          new AbortController().signal,
        );
        const rejected = expect(pending).rejects.toMatchObject({
          error: { kind: 'unexpected', code: 'cursor_repeated' },
        });
        const request = await takeRequest(http, `${BASE}/${route}`);
        request.flush(JSON.stringify({ items: [], next_cursor: 'opaque+/=' }), {
          headers: JSON_HEADERS,
        });
        await rejected;
      });

      it('accepts an exhausted cursor page instead of refusing null as a repeated cursor', async () => {
        const pending = service[method](
          'project-1',
          { cursor: 'last-page' },
          new AbortController().signal,
        );
        const request = await takeRequest(http, `${BASE}/${route}`);
        request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
        await expect(pending).resolves.toEqual(EMPTY_PAGE);
      });
    });
  }

  it('maps a tree read to the wire names and always asks for the path on a node read', async () => {
    const pending = service.listTree(
      'project-1',
      { parentId: 'parent', nodeId: 'node', cursor: 'opaque+/=', limit: 25 },
      new AbortController().signal,
    );
    const request = await takeRequest(http, `${BASE}/tree`);
    expect(Object.fromEntries(new URL(request.request.urlWithParams).searchParams)).toEqual({
      parent_id: 'parent',
      node_id: 'node',
      include: 'path,counts',
      cursor: 'opaque+/=',
      limit: '25',
    });
    request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(EMPTY_PAGE);
  });

  it('asks for no path on a parent read, because only an addressed node has ancestors', async () => {
    const pending = service.listTree(
      'project-1',
      { parentId: 'parent' },
      new AbortController().signal,
    );
    const request = await takeRequest(http, `${BASE}/tree`);
    expect(Object.fromEntries(new URL(request.request.urlWithParams).searchParams)).toEqual({
      parent_id: 'parent',
      limit: '50',
    });
    request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(EMPTY_PAGE);
  });

  it('passes checklist node, lifecycle and priority filters to the real SDK', async () => {
    const query = { node_id: 'node', state: 'active', priority: 'high', limit: 50 } as const;
    const pending = service.listChecklistItems('project-1', query, new AbortController().signal);
    const request = await takeRequest(http, `${BASE}/checklist-items`);
    expect(Object.fromEntries(new URL(request.request.urlWithParams).searchParams)).toEqual({
      ...query,
      limit: '50',
    });
    request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(EMPTY_PAGE);
  });

  it('passes manual-case keyset, coverage false and all supported filters without inventing parameters', async () => {
    const query = {
      node_id: 'node',
      item_id: 'item',
      state: 'approved',
      priority: 'critical',
      covered: false,
      q: 'title %_\\',
      cursor: 'opaque+/=',
      limit: 100,
    } as const;
    const pending = service.listManualCases('project-1', query, new AbortController().signal);
    const request = await takeRequest(http, `${BASE}/manual-cases`);
    expect(Object.fromEntries(new URL(request.request.urlWithParams).searchParams)).toEqual({
      ...query,
      covered: 'false',
      limit: '100',
    });
    request.flush(JSON.stringify(EMPTY_PAGE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(EMPTY_PAGE);
  });
});
