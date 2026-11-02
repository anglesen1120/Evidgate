// `X-Evidgate-Org` scopes a request to one tenant: `/v1/permissions` and every org route needs it,
// and no other host or pre-tenant route may receive it. Each case names one boundary that keeps a
// tenant id from leaking or from going missing.
import { BrowserPlatformLocation, DOCUMENT, PlatformLocation } from '@angular/common';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { TenantStore } from '../session/tenant.store';
import { ORG_HEADER, tenantOrgInterceptor } from './tenant-org.interceptor';

interface Harness {
  readonly http: HttpClient;
  readonly controller: HttpTestingController;
  readonly origin: string;
  readonly tenant: TenantStore;
}

function createHarness(): Harness {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([tenantOrgInterceptor])),
      provideHttpClientTesting(),
      { provide: PlatformLocation, useClass: BrowserPlatformLocation },
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    origin: TestBed.inject(DOCUMENT).location.origin,
    tenant: TestBed.inject(TenantStore),
  };
}

describe('tenantOrgInterceptor', () => {
  let harness: Harness;

  beforeEach(() => {
    harness = createHarness();
  });

  afterEach(() => {
    harness.controller.verify();
  });

  it('adds the store tenant to a tenant-scoped same-origin API read', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/v1/permissions`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get(ORG_HEADER)).toBe('o-1');
    request.flush({ items: [] });
  });

  it('adds no tenant to a foreign-origin absolute URL', () => {
    harness.tenant.setOrg('o-1');
    const url = 'https://other.example/v1/permissions';

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({ items: [] });
  });

  it('adds no tenant to a same-origin read outside /v1', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/internal/operator/replay`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({});
  });

  it('adds no tenant to the caller own organization list', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/v1/orgs`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({ items: [] });
  });

  it('adds no tenant to CI token exchange, auth routes or webhooks', () => {
    harness.tenant.setOrg('o-1');
    const urls = [
      `${harness.origin}/v1/ci/token`,
      `${harness.origin}/v1/auth/sessions`,
      `${harness.origin}/v1/auth/logout`,
      `${harness.origin}/v1/webhooks/github`,
    ];

    for (const url of urls) {
      harness.http.get(url).subscribe();
      const request = harness.controller.expectOne(url);

      expect(request.request.headers.has(ORG_HEADER)).toBe(false);
      request.flush({});
    }
  });

  it('prefers the org the request path names over the store tenant', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/v1/orgs/o-2/projects`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get(ORG_HEADER)).toBe('o-2');
    request.flush({ items: [] });
  });

  it('scopes an org detail read by its own id, not by the store tenant', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/v1/orgs/o-2`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get(ORG_HEADER)).toBe('o-2');
    request.flush({});
  });

  it('keeps a header the caller already set, without duplicating it', () => {
    harness.tenant.setOrg('o-1');
    const url = `${harness.origin}/v1/permissions`;

    harness.http.get(url, { headers: { [ORG_HEADER]: 'o-9' } }).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get(ORG_HEADER)).toBe('o-9');
    expect(request.request.headers.getAll(ORG_HEADER)).toEqual(['o-9']);
    request.flush({ items: [] });
  });

  it('adds nothing while no tenant is known', () => {
    const url = `${harness.origin}/v1/permissions`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({ items: [] });
  });

  it('adds nothing after the tenant was cleared', () => {
    harness.tenant.setOrg('o-1');
    harness.tenant.clear();
    const url = `${harness.origin}/v1/permissions`;

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({ items: [] });
  });

  it('leaves a cross-origin org route bare even when its path names an org', () => {
    harness.tenant.setOrg('o-1');
    const url = 'https://other.example/v1/orgs/o-2/projects';

    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has(ORG_HEADER)).toBe(false);
    request.flush({ items: [] });
  });
});
