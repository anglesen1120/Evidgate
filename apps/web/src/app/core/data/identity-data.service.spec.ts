// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1; 00 §9.1 cursor pages, §9.2 refusals): the identity reads
// against the real stack. Each case pins the operation and URL the service calls, that a cursor
// page is followed to the last page, and that a broken cursor cannot hang the shell: the loop is
// bounded, a repeated cursor is refused, and a server refusal surfaces as the typed
// `ApiRequestError` instead of a partial list.
//
// The base URL is the absolute form of the same-origin API path because Node's `Request` (the one
// `ng test` runs on) refuses the relative `/v1` the browser resolves for us.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import type { HttpRequest } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { OrganizationSummaryDto, PermissionView, ProjectDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { IdentityDataService, MAX_CURSOR_PAGES } from './identity-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const PERMISSIONS_URL = `${API_BASE_URL}/permissions`;
const ORGANIZATIONS_URL = `${API_BASE_URL}/orgs`;
const PROJECTS_URL = `${API_BASE_URL}/orgs/o-1/projects`;

/** A JSON body as the adapter delivers it (the SDK parses a `Response`, and the adapter reads a blob). */
function jsonBlob(value: unknown): Blob {
  return new Blob([JSON.stringify(value)], { type: 'application/json' });
}

/** The served media type, which `flush` does not derive from the blob (`parseAs: 'auto'`). */
const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

function permission(code: string, tenantAssignable = true): PermissionView {
  const [resource = code, action = 'read'] = code.split(':');
  return {
    code,
    resource,
    action,
    description: `${code} (test)`,
    sensitive: false,
    reserved: false,
    tenant_assignable: tenantAssignable,
  };
}

const ORGANIZATION: OrganizationSummaryDto = {
  id: 'o-1',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};

const PROJECT: ProjectDto = {
  id: 'p-1',
  org_id: 'o-1',
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-02T09:00:00Z',
  updated_at: '2026-10-18T11:20:00Z',
  version: 3,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'STOR',
};

/**
 * The SDK -> adapter -> `HttpClient` pipeline is asynchronous, so a request is registered after
 * the call starts — and after a *page* is flushed the next one waits on the blob parse, which is a
 * macrotask in jsdom, so yielding only microtasks stalls the second page. Each attempt yields a
 * macrotask before matching; `expectOne` would throw on the first empty look.
 */
async function takeRequest(
  httpMock: HttpTestingController,
  match: string | ((request: HttpRequest<unknown>) => boolean),
): Promise<TestRequest> {
  // A string match is a URL prefix, not an exact URL: a cursor page's `url` still equals the bare
  // path while its `urlWithParams` carries `?cursor=…`, and an exact-match argument would silently
  // never see page two.
  const matches = (candidate: HttpRequest<unknown>): boolean =>
    typeof match === 'string' ? candidate.url.startsWith(match) : match(candidate);
  for (let attempt = 0; attempt < 50; attempt += 1) {
    const [request] = httpMock.match(matches);
    if (request !== undefined) {
      return request;
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  throw new Error('no HTTP request reached HttpTestingController');
}

/** Answers one page with a body, so the SDK's parser sees the served media type. */
function flushPage(request: TestRequest, body: unknown, status = 200): void {
  request.flush(jsonBlob(body), { status, statusText: '', headers: JSON_HEADERS });
}

describe('IdentityDataService (09.1.1-T4.1)', () => {
  let httpMock: HttpTestingController;
  let service: IdentityDataService;

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
    service = TestBed.inject(IdentityDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listPermissions() follows next_cursor to the last page and keeps server order', async () => {
    const catalogue = service.listPermissions();

    const first = await takeRequest(httpMock, PERMISSIONS_URL);
    expect(first.request.method).toBe('GET');
    expect(first.request.urlWithParams).not.toContain('cursor=');
    flushPage(first, {
      items: [permission('audit:read'), permission('billing:read', false)],
      next_cursor: 'billing:read',
    });

    const second = await takeRequest(httpMock, PERMISSIONS_URL);
    expect(second.request.urlWithParams).toContain('cursor=billing%3Aread');
    flushPage(second, {
      items: [permission('project:read')],
      next_cursor: null,
    });

    await expect(catalogue).resolves.toEqual([
      permission('audit:read'),
      permission('billing:read', false),
      permission('project:read'),
    ]);
  });

  it('listPermissions() refuses a repeated cursor instead of paging forever', async () => {
    const catalogue = service.listPermissions();
    const read = catalogue.then(
      () => undefined,
      (error: unknown) => error,
    );

    // The second page names itself as the next cursor: a server bug the loop must not follow.
    flushPage(await takeRequest(httpMock, PERMISSIONS_URL), {
      items: [permission('audit:read')],
      next_cursor: 'c-1',
    });
    flushPage(await takeRequest(httpMock, PERMISSIONS_URL), {
      items: [permission('case:read')],
      next_cursor: 'c-1',
    });

    const cause = await read;
    expect(cause).toBeInstanceOf(Error);
    expect((cause as Error).message).toContain('repeated cursor');
  });

  it('listPermissions() stops at the page cap when the cursor never ends', async () => {
    const catalogue = service.listPermissions();
    const read = catalogue.then(
      () => undefined,
      (error: unknown) => error,
    );

    for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
      const next = `c-${page + 1}`;
      flushPage(await takeRequest(httpMock, PERMISSIONS_URL), {
        items: [permission('audit:read')],
        next_cursor: next,
      });
    }

    const cause = await read;
    expect(cause).toBeInstanceOf(Error);
    expect((cause as Error).message).toContain(`within ${MAX_CURSOR_PAGES} pages`);
  });

  it('listPermissions() surfaces a 403 as a typed forbidden error, never a partial list', async () => {
    const catalogue = service.listPermissions();
    const read = catalogue.then(
      () => undefined,
      (error: unknown) => error,
    );

    flushPage(
      await takeRequest(httpMock, PERMISSIONS_URL),
      {
        error: {
          code: 'forbidden',
          message: 'Missing role:read.',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: false,
        },
      },
      403,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
    }
  });

  it('listMyOrganizations() reads the unbounded org list in one call', async () => {
    const organizations = service.listMyOrganizations();
    const request = await takeRequest(httpMock, ORGANIZATIONS_URL);
    expect(request.request.method).toBe('GET');
    flushPage(request, { items: [ORGANIZATION] });
    await expect(organizations).resolves.toEqual([ORGANIZATION]);
  });

  it('listMyOrganizations() surfaces a 401 as unauthenticated', async () => {
    const organizations = service.listMyOrganizations();
    const read = organizations.then(
      () => undefined,
      (error: unknown) => error,
    );
    flushPage(
      await takeRequest(httpMock, ORGANIZATIONS_URL),
      {
        error: {
          code: 'unauthenticated',
          message: 'No session.',
          request_id: '8a1f0f0d-9d63-4cf2-9a1e-1f4d6f1f58a2',
          retryable: false,
        },
      },
      401,
    );
    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('unauthenticated');
    }
  });

  it('listProjects() reads the org projects URL and follows the cursor', async () => {
    const projects = service.listProjects('o-1');

    const first = await takeRequest(httpMock, PROJECTS_URL);
    expect(first.request.method).toBe('GET');
    flushPage(first, { items: [PROJECT], next_cursor: 'p-1' });

    const second = await takeRequest(httpMock, PROJECTS_URL);
    expect(second.request.urlWithParams).toContain('cursor=p-1');
    flushPage(second, {
      items: [{ ...PROJECT, id: 'p-2', slug: 'checkout-api' }],
      next_cursor: null,
    });

    await expect(projects).resolves.toEqual([
      PROJECT,
      { ...PROJECT, id: 'p-2', slug: 'checkout-api' },
    ]);
  });

  it('listProjects() surfaces a network failure as the typed network error', async () => {
    const projects = service.listProjects('o-1');
    const read = projects.then(
      () => undefined,
      (error: unknown) => error,
    );
    (await takeRequest(httpMock, PROJECTS_URL)).error(new ProgressEvent('network error'));
    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('network');
      expect(cause.error.status).toBe(0);
    }
  });
});
