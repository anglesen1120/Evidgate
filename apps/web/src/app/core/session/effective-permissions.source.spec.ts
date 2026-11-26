// The source binds the shell's grants to `GET /v1/me/permissions`: the set is exactly the server's
// permission strings for the tenant the session published, a missing tenant asks nothing, and a
// failed read rejects instead of answering an empty set (which would silently hide the whole app).

import { TestBed } from '@angular/core/testing';
import type { MyPermissionsResponse } from '@evidgate/sdk';

import { IdentityDataService } from '../data/identity-data.service';
import { ApiPermissionsSource, EffectivePermissionsSource } from './effective-permissions.source';
import { TenantStore } from './tenant.store';

class FakeIdentityDataService {
  answer: MyPermissionsResponse = { org_id: 'o-1', roles: [], permissions: [] };
  failure: Error | null = null;
  readonly orgs: string[] = [];

  getMyPermissions(orgId: string): Promise<MyPermissionsResponse> {
    this.orgs.push(orgId);
    return this.failure === null ? Promise.resolve(this.answer) : Promise.reject(this.failure);
  }
}

describe('ApiPermissionsSource', () => {
  let identity: FakeIdentityDataService;
  let tenant: InstanceType<typeof TenantStore>;
  let source: ApiPermissionsSource;

  beforeEach(() => {
    identity = new FakeIdentityDataService();
    TestBed.configureTestingModule({
      providers: [{ provide: IdentityDataService, useValue: identity }],
    });
    tenant = TestBed.inject(TenantStore);
    source = TestBed.inject(ApiPermissionsSource);
  });

  it('grants exactly the codes the server returned for the current tenant', async () => {
    tenant.setOrg('o-1');
    identity.answer = {
      org_id: 'o-1',
      roles: ['viewer'],
      permissions: ['audit:read', 'run:read'],
    };

    const granted = await source.load();

    expect([...granted]).toEqual(['audit:read', 'run:read']);
    expect(identity.orgs).toEqual(['o-1']);
  });

  it('reads the grants of the tenant current at call time, org by org', async () => {
    tenant.setOrg('o-1');
    await source.load();
    tenant.setOrg('o-2');
    await source.load();

    expect(identity.orgs).toEqual(['o-1', 'o-2']);
  });

  it('grants nothing and asks nothing when there is no tenant', async () => {
    const granted = await source.load();

    expect(granted.size).toBe(0);
    expect(identity.orgs).toEqual([]);
  });

  it('rejects when the grants cannot be read, never answering an empty set', async () => {
    tenant.setOrg('o-1');
    const failure = new Error('grants unavailable');
    identity.failure = failure;

    await expect(source.load()).rejects.toBe(failure);
  });

  it('is the root default of the EffectivePermissionsSource token', () => {
    expect(TestBed.inject(EffectivePermissionsSource)).toBeInstanceOf(ApiPermissionsSource);
  });
});
