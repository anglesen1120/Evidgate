import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { IdentityDataService } from '../data/identity-data.service';
import { SessionStore } from './session.store';
import type { TenantStore } from './tenant.store';
import {
  apiError,
  ORG_ONE,
  ORG_TWO,
  PROJECT_P1,
  settle,
  type FakeIdentityDataService,
  type FakePermissionsSource,
  setupSessionStore,
} from './session.store.test-support';

describe('SessionStore (bootstrap)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let store: SessionStore;
  let tenant: TenantStore;

  beforeEach(() => {
    ({ identity, source, store, tenant } = setupSessionStore());
  });

  describe('bootstrap', () => {
    it('reads the organization list and the permission set, then becomes authenticated', async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      source.granted = new Set(['run:read', 'authoring:read']);

      await store.bootstrap();

      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE, ORG_TWO]);
      expect(store.permissions()).toBe(source.granted);
      // The first listed org is the tenant the tenant-scoped reads are sent for.
      expect(tenant.orgId()).toBe(ORG_ONE.id);
      expect(source.tenants).toEqual([ORG_ONE.id]);
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
    });

    it('treats a 401 as an anonymous session with no grants', async () => {
      identity.organizationFailure = apiError('unauthenticated', 401);
      source.granted = new Set(['run:read']);

      await store.bootstrap();

      expect(store.sessionState()).toBe('anonymous');
      expect(store.permissions().size).toBe(0);
      expect(store.has('run:read')).toBe(false);
      // The catalogue is read in the same round trip, and its answer is discarded: an
      // anonymous session publishes no grants even when the permission source succeeded.
      expect(source.calls).toBe(1);
      expect(source.granted.has('run:read')).toBe(true);
    });

    it('treats a 401 from the permission read as anonymous, whichever read refused', async () => {
      identity.organizations = [ORG_ONE];
      source.failure = apiError('unauthenticated', 401);

      await store.bootstrap();

      expect(store.sessionState()).toBe('anonymous');
      expect(store.organizations()).toEqual([]);
      expect(store.permissions().size).toBe(0);
    });

    it('drops the tenant of a refused bootstrap, so no stale org rides to sign-in', async () => {
      identity.organizations = [ORG_ONE];
      source.failure = apiError('unauthenticated', 401);

      await store.bootstrap();

      // The permission read ran under the first listed org, and that tenant is gone with the
      // session: `/login` and the next sign-in must not carry it as `X-Evidgate-Org`.
      expect(source.tenants).toEqual([ORG_ONE.id]);
      expect(store.sessionState()).toBe('anonymous');
      expect(tenant.orgId()).toBeNull();
    });

    it('survives an org list answer without items, reading permissions with no tenant', async () => {
      // The body a proxy's HTML page decodes to: `items` is missing at runtime.
      Object.assign(identity, { organizations: undefined });

      await store.bootstrap();

      expect(source.tenants).toEqual([null]);
      expect(tenant.orgId()).toBeNull();
    });

    it('prefers a 401 from either read over the other read failing', async () => {
      identity.organizationFailure = apiError('unavailable', 503);
      source.failure = apiError('unauthenticated', 401);

      await store.bootstrap();

      expect(store.sessionState()).toBe('anonymous');
    });

    it('treats both reads failing as unavailable, not as an empty workspace', async () => {
      identity.organizationFailure = apiError('unavailable', 503);
      source.failure = apiError('unavailable', 503);

      await store.bootstrap();

      expect(store.sessionState()).toBe('unavailable');
      expect(store.organizations()).toEqual([]);
      expect(store.permissions().size).toBe(0);
    });

    it('treats a 5xx as unavailable, not as an empty workspace', async () => {
      identity.organizationFailure = apiError('unavailable', 503);

      await store.bootstrap();

      expect(store.sessionState()).toBe('unavailable');
      expect(store.organizations()).toEqual([]);
    });

    it('treats an unknown failure as unavailable', async () => {
      identity.organizationFailure = new Error('boom');

      await store.bootstrap();

      expect(store.sessionState()).toBe('unavailable');
    });

    it('treats a failing permission read as unavailable', async () => {
      identity.organizations = [ORG_ONE];
      source.failure = apiError('forbidden', 403);

      await store.bootstrap();

      expect(store.sessionState()).toBe('unavailable');
      expect(store.permissions().size).toBe(0);
    });

    it('shares one in-flight read between concurrent callers', async () => {
      identity.organizations = [ORG_ONE];

      await Promise.all([store.bootstrap(), store.bootstrap(), store.bootstrap()]);

      expect(identity.organizationCalls).toBe(1);
      expect(source.calls).toBe(1);
      expect(store.sessionState()).toBe('authenticated');
    });

    it('publishes the first organization as the tenant before it reads the permissions', async () => {
      identity.manualOrganizations = true;
      identity.organizations = [ORG_ONE, ORG_TWO];

      const booting = store.bootstrap();
      await settle();

      // The permission read is tenant-scoped, so it waits for the org the header must name.
      expect(source.calls).toBe(0);
      expect(tenant.orgId()).toBeNull();

      const first = identity.pendingOrgs[0];
      if (first === undefined) {
        throw new Error('the organization read must have reached the fake');
      }
      first([ORG_ONE, ORG_TWO]);
      await booting;

      expect(tenant.orgId()).toBe(ORG_ONE.id);
      expect(source.tenants).toEqual([ORG_ONE.id]);
      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE, ORG_TWO]);
      expect(store.permissions()).toBe(source.granted);
    });

    it('reads the permissions with no tenant when the caller has no organization', async () => {
      identity.organizations = [];
      source.granted = new Set(['run:read']);

      await store.bootstrap();

      // No organization, no tenant: the interceptor adds nothing and the read answers as before.
      expect(tenant.orgId()).toBeNull();
      expect(source.calls).toBe(1);
      expect(source.tenants).toEqual([null]);
      expect(store.sessionState()).toBe('authenticated');
    });

    it('does not read again once the state is settled', async () => {
      await store.bootstrap();
      await store.bootstrap();

      expect(identity.organizationCalls).toBe(1);
    });

    it('reload() re-reads after a failure and can recover', async () => {
      identity.organizationFailure = apiError('unavailable', 503);
      await store.bootstrap();
      expect(store.sessionState()).toBe('unavailable');

      identity.organizationFailure = null;
      identity.organizations = [ORG_ONE];
      source.granted = new Set(['run:read']);
      await store.reload();

      expect(identity.organizationCalls).toBe(2);
      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE]);
      expect(store.has('run:read')).toBe(true);
    });

    it('reload() rejects when the retry is still unreadable, so the retry button can say so', async () => {
      identity.organizationFailure = apiError('unavailable', 503);
      await store.bootstrap();

      await expect(store.reload()).rejects.toThrow('still unavailable');
      expect(store.sessionState()).toBe('unavailable');
    });

    it('reload() resolves for an anonymous outcome, which is an answer and not a failed read', async () => {
      identity.organizationFailure = apiError('unauthenticated', 401);

      await expect(store.reload()).resolves.toBeUndefined();
      expect(store.sessionState()).toBe('anonymous');
    });

    it('reload() drops the resolved scope', async () => {
      identity.organizations = [ORG_ONE];
      identity.projectsByOrg.set('o-1', [PROJECT_P1]);
      await store.bootstrap();
      await store.resolveScope('o-1', 'p-1');
      expect(store.currentProject()).toEqual(PROJECT_P1);

      await store.reload();

      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
    });

    it('falls back to the catalogue source when no replacement is bound', async () => {
      // With no `EffectivePermissionsSource` provider the store must reach the catalogue default;
      // the fake's `listPermissions` throws, so `unavailable` is the proof that it did.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideRouter([]), { provide: IdentityDataService, useValue: identity }],
      });
      const catalogueStore = TestBed.inject(SessionStore);

      await catalogueStore.bootstrap();

      expect(catalogueStore.sessionState()).toBe('unavailable');
    });
  });
});
