// 09.7.1-T2 (plan 09.7.1 §5 members row, §6 Membership/Invite rows, §10 T2.1/T2.2; 02.1.2 §4/§5):
// the membership, role and catalogue reads plus the mutations — invite, remove and the per-role
// grant/revoke pair the edit-roles dialog applies against the real stack. Each case pins three
// things at once — the SDK operation and URL the service calls, the `Idempotency-Key` the caller's
// intent contributes, and that a refusal surfaces as the typed `ApiRequestError` instead of a value
// (a dialog cannot get a 409 `last_admin` into its success path).
//
// The role list follows `next_cursor` to the last page, so its two contract violations are pinned
// here too: a repeated cursor and a cursor that never ends are both a plain `Error`, never a
// partial column set handed to the matrix.
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
import type { MembershipView, PermissionView, RoleView } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { createIntent } from '../http/intent';
import { MAX_CURSOR_PAGES } from './identity-data.service';
import { MembersDataService } from './members-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const MEMBERSHIPS_URL = `${API_BASE_URL}/orgs/o-1/memberships`;
const MEMBER_URL = `${MEMBERSHIPS_URL}/m-1`;
const MEMBER_ROLES_URL = `${MEMBER_URL}/roles`;
const ROLES_URL = `${API_BASE_URL}/roles`;
const PERMISSIONS_URL = `${API_BASE_URL}/permissions`;

/** The served media type: the adapter reads bodies as text and `flush` does not derive it. */
const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

function role(id: string, code: string): RoleView {
  return {
    id,
    org_id: null,
    code,
    name: `${code} (test)`,
    is_builtin: true,
    created_at: '2026-10-02T09:00:00Z',
    version: 1,
  };
}

const VIEWER = role('role-viewer', 'viewer');
const TENANT_ADMIN = role('role-tenant-admin', 'tenant_admin');

function membership(id: string, invitedEmail: string, roles: RoleView[]): MembershipView {
  return {
    id,
    org_id: 'o-1',
    user_id: null,
    invited_email: invitedEmail,
    status: 'active',
    roles,
    created_at: '2026-10-02T09:00:00Z',
    updated_at: '2026-10-18T11:20:00Z',
    version: 2,
  };
}

const OWNER = membership('m-1', 'owner@acme.test', [TENANT_ADMIN]);

function permission(code: string): PermissionView {
  const [resource = code, action = 'read'] = code.split(':');
  return {
    code,
    resource,
    action,
    description: `${code} (test)`,
    sensitive: false,
    reserved: false,
    tenant_assignable: true,
  };
}

/**
 * The SDK -> adapter -> `HttpClient` pipeline is asynchronous, so a request is registered after the
 * call starts — and after a *page* is flushed the next one waits on the blob parse, which is a
 * macrotask in jsdom, so yielding only microtasks stalls the second page.
 */
async function takeRequest(
  httpMock: HttpTestingController,
  match: string | ((request: HttpRequest<unknown>) => boolean),
): Promise<TestRequest> {
  // A string match is a URL prefix: a cursor page's `url` stays the bare path while its
  // `urlWithParams` carries `?cursor=…`.
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
  request.flush(JSON.stringify(body), { status, statusText: '', headers: JSON_HEADERS });
}

/** The rejection one promise settles with, so a refusal can be inspected instead of thrown. */
async function rejected(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

const INVITE_EXISTS_ENVELOPE = {
  error: {
    code: 'invite_exists',
    message: 'An invitation to owner@acme.test is already pending.',
    request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
    retryable: false,
  },
};

const LAST_ADMIN_ENVELOPE = {
  error: {
    code: 'last_admin',
    message: 'The organization must keep at least one active tenant_admin.',
    request_id: '8a1f0f0d-9d63-4cf2-9a1e-1f4d6f1f58a2',
    retryable: false,
  },
};

/** A role the tenant cannot see, or a staff/service role: the same 409 on grant and on invite. */
const UNKNOWN_ROLE_ENVELOPE = {
  error: {
    code: 'unknown_role',
    message: 'the role is not visible to this tenant or is not a member role',
    request_id: '1b6f2c9e-5f4a-4c2d-8f3e-7a0d9b6e2c41',
    retryable: false,
  },
};

/**
 * The 403 of the self-escalation rule (02.1.2 §3b): granting the caller a permission the caller
 * does not hold, on the caller's own membership.
 */
const SELF_ESCALATION_ENVELOPE = {
  error: {
    code: 'forbidden',
    message:
      "the request would grant the caller a permission the caller does not hold (00 §2: a role never widens the caller's own access)",
    request_id: 'c2f0a2b8-0c55-4a4c-9d5f-2f4f6c1f7d31',
    retryable: false,
  },
};

/**
 * The 404 of both routes: an id of another org (indistinguishable by design, 00 §9.2) and a role
 * pair that was never granted.
 */
const NOT_FOUND_ENVELOPE = {
  error: {
    code: 'not_found',
    message: 'the requested resource does not exist',
    request_id: '7d4e8a10-2b93-4f6c-9a07-3c5e1f8d2b64',
    retryable: false,
  },
};

describe('MembersDataService (09.7.1-T2)', () => {
  let httpMock: HttpTestingController;
  let service: MembersDataService;

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
    service = TestBed.inject(MembersDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listMemberships() reads one page of the org URL and passes the cursor it is given', async () => {
    const page = service.listMemberships('o-1');
    const first = await takeRequest(httpMock, MEMBERSHIPS_URL);
    expect(first.request.method).toBe('GET');
    expect(first.request.urlWithParams).not.toContain('cursor=');
    const body = { items: [OWNER], next_cursor: 'm-1' };
    flushPage(first, body);
    await expect(page).resolves.toEqual(body);

    const second = service.listMemberships('o-1', 'm-1');
    const request = await takeRequest(httpMock, MEMBERSHIPS_URL);
    expect(request.request.urlWithParams).toContain('cursor=m-1');
    flushPage(request, { items: [], next_cursor: null });
    await expect(second).resolves.toEqual({ items: [], next_cursor: null });
  });

  it('listMemberships() surfaces a 403 as the typed forbidden error, never a page', async () => {
    const page = service.listMemberships('other-tenant');
    const read = rejected(page);
    flushPage(
      await takeRequest(httpMock, `${API_BASE_URL}/orgs/other-tenant/memberships`),
      {
        error: {
          code: 'forbidden',
          message: 'Missing membership:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      },
      403,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.code).toBe('forbidden');
    }
  });

  it('listRoles() follows next_cursor to the last page and keeps server order', async () => {
    const roles = service.listRoles();

    flushPage(await takeRequest(httpMock, ROLES_URL), {
      items: [VIEWER],
      next_cursor: 'role-viewer',
    });
    const second = await takeRequest(httpMock, ROLES_URL);
    expect(second.request.urlWithParams).toContain('cursor=role-viewer');
    flushPage(second, { items: [TENANT_ADMIN], next_cursor: null });

    await expect(roles).resolves.toEqual([VIEWER, TENANT_ADMIN]);
  });

  it('listRoles() refuses a repeated cursor instead of paging forever', async () => {
    const roles = rejected(service.listRoles());

    flushPage(await takeRequest(httpMock, ROLES_URL), { items: [VIEWER], next_cursor: 'c-1' });
    flushPage(await takeRequest(httpMock, ROLES_URL), { items: [VIEWER], next_cursor: 'c-1' });

    const cause = await roles;
    expect(cause).toBeInstanceOf(Error);
    expect((cause as Error).message).toContain('repeated cursor');
  });

  it('listRoles() stops at the page cap when the cursor never ends', async () => {
    const roles = rejected(service.listRoles());

    for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
      flushPage(await takeRequest(httpMock, ROLES_URL), {
        items: [VIEWER],
        next_cursor: `c-${page + 1}`,
      });
    }

    const cause = await roles;
    expect(cause).toBeInstanceOf(Error);
    expect((cause as Error).message).toContain(`within ${MAX_CURSOR_PAGES} pages`);
  });

  it('listPermissions() answers the catalogue through IdentityDataService', async () => {
    const catalogue = service.listPermissions();

    flushPage(await takeRequest(httpMock, PERMISSIONS_URL), {
      items: [permission('membership:read')],
      next_cursor: null,
    });

    await expect(catalogue).resolves.toEqual([permission('membership:read')]);
  });

  it('invite() posts { email, roles } with the intent’s Idempotency-Key', async () => {
    const intent = createIntent();
    const invited = service.invite('o-1', { email: 'new@acme.test', roles: [VIEWER.id] }, intent);

    const request = await takeRequest(httpMock, MEMBERSHIPS_URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    // The adapter hands the interceptors the request text, so the body is the serialized JSON.
    expect(request.request.body).toBe(
      JSON.stringify({ email: 'new@acme.test', roles: [VIEWER.id] }),
    );
    const created = { ...membership('m-2', 'new@acme.test', []), status: 'pending' as const };
    flushPage(request, created, 201);
    await expect(invited).resolves.toEqual(created);
  });

  it('invite() surfaces 409 invite_exists as the typed conflict with the server’s message', async () => {
    const invited = rejected(service.invite('o-1', { email: 'owner@acme.test' }, createIntent()));

    flushPage(await takeRequest(httpMock, MEMBERSHIPS_URL), INVITE_EXISTS_ENVELOPE, 409);

    const cause = await invited;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.code).toBe('invite_exists');
      expect(cause.error.message).toBe(INVITE_EXISTS_ENVELOPE.error.message);
      expect(cause.error.retryable).toBe(false);
    }
  });

  it('remove() sends DELETE with the intent key and resolves on 204', async () => {
    const intent = createIntent();
    const removed = service.remove('o-1', 'm-1', intent);

    const request = await takeRequest(httpMock, MEMBERSHIPS_URL);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.url).toBe(`${MEMBERSHIPS_URL}/m-1`);
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expect(removed).resolves.toBeUndefined();
  });

  it('remove() surfaces 409 last_admin as the typed failure with message and code verbatim', async () => {
    const removed = rejected(service.remove('o-1', 'm-1', createIntent()));

    flushPage(await takeRequest(httpMock, MEMBERSHIPS_URL), LAST_ADMIN_ENVELOPE, 409);

    const cause = await removed;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.status).toBe(409);
      expect(cause.error.code).toBe('last_admin');
      expect(cause.error.message).toBe(LAST_ADMIN_ENVELOPE.error.message);
    }
  });

  it('getMembership() reads the one member URL and resolves the membership it serves', async () => {
    const read = service.getMembership('o-1', 'm-1');

    const request = await takeRequest(httpMock, MEMBER_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.url).toBe(MEMBER_URL);
    expect(request.request.urlWithParams).toBe(MEMBER_URL);
    flushPage(request, OWNER);

    await expect(read).resolves.toEqual(OWNER);
  });

  it('getMembership() surfaces 404 as the typed not_found, never a row', async () => {
    const read = rejected(service.getMembership('o-1', 'm-other'));

    flushPage(await takeRequest(httpMock, `${MEMBERSHIPS_URL}/m-other`), NOT_FOUND_ENVELOPE, 404);

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
      expect(cause.error.status).toBe(404);
      expect(cause.error.code).toBe(NOT_FOUND_ENVELOPE.error.code);
    }
  });

  it('grantRole() posts { role_id } to the roles URL with the intent’s key', async () => {
    const intent = createIntent();
    const granted = service.grantRole('o-1', 'm-1', VIEWER.id, intent);

    const request = await takeRequest(httpMock, MEMBER_ROLES_URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.url).toBe(MEMBER_ROLES_URL);
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    // The adapter hands the interceptors the request text, so the body is the serialized JSON.
    expect(request.request.body).toBe(JSON.stringify({ role_id: VIEWER.id }));
    const withViewer = membership('m-1', 'owner@acme.test', [TENANT_ADMIN, VIEWER]);
    flushPage(request, withViewer);

    await expect(granted).resolves.toEqual(withViewer);
  });

  it('grantRole() surfaces 409 unknown_role verbatim, so the dialog can name the role', async () => {
    const granted = rejected(service.grantRole('o-1', 'm-1', 'role-unseen', createIntent()));

    flushPage(await takeRequest(httpMock, MEMBER_ROLES_URL), UNKNOWN_ROLE_ENVELOPE, 409);

    const cause = await granted;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.status).toBe(409);
      expect(cause.error.code).toBe('unknown_role');
      expect(cause.error.message).toBe(UNKNOWN_ROLE_ENVELOPE.error.message);
      expect(cause.error.retryable).toBe(false);
    }
  });

  it('grantRole() surfaces the self-escalation 403 forbidden verbatim', async () => {
    const granted = rejected(service.grantRole('o-1', 'm-1', TENANT_ADMIN.id, createIntent()));

    flushPage(await takeRequest(httpMock, MEMBER_ROLES_URL), SELF_ESCALATION_ENVELOPE, 403);

    const cause = await granted;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
      expect(cause.error.code).toBe('forbidden');
      expect(cause.error.message).toBe(SELF_ESCALATION_ENVELOPE.error.message);
    }
  });

  it('revokeRole() deletes the one pair URL with the intent key and resolves on 204', async () => {
    const intent = createIntent();
    const revoked = service.revokeRole('o-1', 'm-1', VIEWER.id, intent);

    const request = await takeRequest(httpMock, MEMBER_ROLES_URL);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.url).toBe(`${MEMBER_ROLES_URL}/${VIEWER.id}`);
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expect(revoked).resolves.toBeUndefined();
  });

  it('revokeRole() surfaces 409 last_admin as the typed failure with message and code verbatim', async () => {
    const revoked = rejected(service.revokeRole('o-1', 'm-1', TENANT_ADMIN.id, createIntent()));

    flushPage(await takeRequest(httpMock, MEMBER_ROLES_URL), LAST_ADMIN_ENVELOPE, 409);

    const cause = await revoked;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.status).toBe(409);
      expect(cause.error.code).toBe('last_admin');
      expect(cause.error.message).toBe(LAST_ADMIN_ENVELOPE.error.message);
    }
  });

  it('revokeRole() surfaces 404 for a pair that was never granted', async () => {
    const revoked = rejected(service.revokeRole('o-1', 'm-1', VIEWER.id, createIntent()));

    flushPage(await takeRequest(httpMock, MEMBER_ROLES_URL), NOT_FOUND_ENVELOPE, 404);

    const cause = await revoked;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
      expect(cause.error.code).toBe(NOT_FOUND_ENVELOPE.error.code);
      expect(cause.error.message).toBe(NOT_FOUND_ENVELOPE.error.message);
    }
  });
});
