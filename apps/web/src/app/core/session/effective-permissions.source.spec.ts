// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1; D-S68-Q1): the catalogue-backed permission source. The spec
// pins the one rule the seam owns — the set is exactly the server's catalogue codes that a member
// principal may hold, with no role mapping and no client-side derivation — and that a failed read
// rejects instead of answering an empty set (an empty set would silently hide the whole app).

import { TestBed } from '@angular/core/testing';
import type { PermissionView } from '@evidgate/sdk';

import { IdentityDataService } from '../data/identity-data.service';
import {
  CataloguePermissionsSource,
  EffectivePermissionsSource,
} from './effective-permissions.source';

function permission(code: string, tenantAssignable: boolean): PermissionView {
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

/** The catalogue the fake service answers, and the calls it observed. */
class FakeIdentityDataService {
  catalogue: readonly PermissionView[] = [];
  failure: Error | null = null;
  calls = 0;

  listPermissions(): Promise<readonly PermissionView[]> {
    this.calls += 1;
    return this.failure === null ? Promise.resolve(this.catalogue) : Promise.reject(this.failure);
  }
}

describe('CataloguePermissionsSource (09.1.1-T4.1)', () => {
  let identity: FakeIdentityDataService;
  let source: CataloguePermissionsSource;

  beforeEach(() => {
    identity = new FakeIdentityDataService();
    TestBed.configureTestingModule({
      providers: [{ provide: IdentityDataService, useValue: identity }],
    });
    source = TestBed.inject(CataloguePermissionsSource);
  });

  it('keeps only the codes a member principal may hold', async () => {
    identity.catalogue = [
      permission('audit:read', true),
      permission('billing:read', false),
      permission('ingest_token:write', true),
      permission('job:claim', false),
    ];

    const granted = await source.load();

    expect([...granted]).toEqual(['audit:read', 'ingest_token:write']);
  });

  it('answers an empty set for an empty catalogue, not a role default', async () => {
    identity.catalogue = [];

    const granted = await source.load();

    expect(granted.size).toBe(0);
  });

  it('reads the catalogue once per load and asks no other operation', async () => {
    identity.catalogue = [permission('run:read', true)];

    await source.load();
    await source.load();

    expect(identity.calls).toBe(2);
  });

  it('rejects when the catalogue cannot be read, never answering an empty set', async () => {
    const failure = new Error('catalogue unavailable');
    identity.failure = failure;

    await expect(source.load()).rejects.toBe(failure);
  });

  it('is a valid value for the EffectivePermissionsSource token, so a story can replace it', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: IdentityDataService, useValue: identity },
        { provide: EffectivePermissionsSource, useExisting: CataloguePermissionsSource },
      ],
    });

    expect(TestBed.inject(EffectivePermissionsSource)).toBeInstanceOf(CataloguePermissionsSource);
  });
});
