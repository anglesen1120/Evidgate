// The tenant slice is what the request header is built from, so a write must be visible at once and
// a clear must leave nothing behind for the next session.
import { TestBed } from '@angular/core/testing';

import { TenantStore } from './tenant.store';

describe('TenantStore', () => {
  let tenant: TenantStore;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    tenant = TestBed.inject(TenantStore);
  });

  it('starts with no tenant', () => {
    expect(tenant.orgId()).toBeNull();
  });

  it('publishes the org the session confirmed', () => {
    tenant.setOrg('o-1');

    expect(tenant.orgId()).toBe('o-1');
  });

  it('replaces the previous org on a switch', () => {
    tenant.setOrg('o-1');
    tenant.setOrg('o-2');

    expect(tenant.orgId()).toBe('o-2');
  });

  it('clears back to no tenant', () => {
    tenant.setOrg('o-1');
    tenant.clear();

    expect(tenant.orgId()).toBeNull();
  });
});
