import { apiError } from '../../shared/testing/http-data.testing';
import type { SessionStore } from './session.store';
import type { TenantStore } from './tenant.store';
import {
  ORG_ONE,
  ORG_TWO,
  PROJECT_P1,
  PROJECT_P2,
  PROJECT_Q1,
  settle,
  type FakeIdentityDataService,
  type FakePermissionsSource,
  setupSessionStore,
} from './session.store.test-support';

describe('SessionStore (scope-switch)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let navigations: Array<{ commands: readonly unknown[]; extras: unknown }>;
  let store: SessionStore;
  let tenant: TenantStore;

  beforeEach(() => {
    ({ identity, source, navigations, store, tenant } = setupSessionStore());
  });

  describe('resolveScope', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      identity.projectsByOrg.set('o-1', [PROJECT_P1, PROJECT_P2]);
      identity.projectsByOrg.set('o-2', [PROJECT_Q1]);
      source.granted = new Set(['run:read']);
      await store.bootstrap();
    });

    it('switches the request tenant and reloads the permissions on a real org change', async () => {
      await store.resolveScope('o-1', null);
      expect(tenant.orgId()).toBe(ORG_ONE.id);
      expect(source.calls).toBe(1);
      source.granted = new Set(['run:read', 'audit:read']);

      expect(await store.resolveScope('o-2', 'q-1')).toBe('ok');

      expect(tenant.orgId()).toBe(ORG_TWO.id);
      expect(source.calls).toBe(2);
      expect(source.tenants).toEqual([ORG_ONE.id, ORG_TWO.id]);
      // The grant set follows the tenant: the previous org's codes are not reused.
      expect(store.has('audit:read')).toBe(true);
    });

    it('empties the previous org grants while the new org read is pending', async () => {
      await store.resolveScope('o-1', 'p-1');
      expect(store.has('run:read')).toBe(true);
      expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
      source.granted = new Set(['audit:read']);
      source.manual = true;

      const switching = store.resolveScope('o-2', 'q-1');
      await settle();

      // The switch is in flight: org A's codes and project list must not answer for org B.
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.has('run:read')).toBe(false);
      expect(store.projects()).toEqual([]);

      source.pendingLoads[0]?.resolve(source.granted);
      expect(await switching).toBe('ok');
      expect(store.has('audit:read')).toBe(true);
      expect(store.has('run:read')).toBe(false);
      expect(store.projects()).toEqual([PROJECT_Q1]);
    });

    it('does not reload the permissions for a navigation inside the org it already scoped', async () => {
      await store.resolveScope('o-1', 'p-1');

      expect(await store.resolveScope('o-1', 'p-2')).toBe('ok');

      expect(source.calls).toBe(1);
      expect(tenant.orgId()).toBe(ORG_ONE.id);
      // A second resolve of the same org keeps the grants that are already that org's.
      expect(store.has('run:read')).toBe(true);
    });

    it('does not keep the previous org grants when the switch was superseded', async () => {
      await store.resolveScope('o-1', null);
      expect(store.has('run:read')).toBe(true);
      source.granted = new Set(['run:read', 'audit:read']);
      source.manual = true;

      // The first resolve publishes the tenant and parks its load; the second one is a newer
      // navigation to the SAME org, so the tenant alone must not make it skip the read.
      const first = store.resolveScope('o-2', null);
      await settle();
      const second = store.resolveScope('o-2', 'q-1');
      await settle();
      expect(source.tenants).toEqual([ORG_ONE.id, ORG_TWO.id, ORG_TWO.id]);

      const newer = source.pendingLoads[1];
      if (newer === undefined) {
        throw new Error('the newer resolution must have asked for the grants');
      }
      newer.resolve(source.granted);
      expect(await second).toBe('ok');
      expect(store.has('audit:read')).toBe(true);

      // The abandoned load settles last: a newer navigation owns the scope, so its failure is
      // `superseded` — it must neither republish its answer nor blank the newer scope.
      source.pendingLoads[0]?.reject(apiError('unavailable', 503));
      expect(await first).toBe('superseded');
      expect(store.has('audit:read')).toBe(true);
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(tenant.orgId()).toBe(ORG_TWO.id);
    });

    it('keeps the newer route for the expiry redirect when the abandoned load settles first', async () => {
      await store.resolveScope('o-1', null);
      source.manual = true;

      // Both resolves publish the tenant and park a grant read; the older one settles first.
      const older = store.resolveScope('o-2', 'q-1');
      await settle();
      const newer = store.resolveScope('o-2', 'p-1');
      await settle();
      expect(source.pendingLoads).toHaveLength(2);

      source.pendingLoads[0]?.resolve(source.granted);
      expect(await older).toBe('superseded');

      // The newer read is the one that expires the session, so its redirect must name ITS route:
      // the abandoned answer above may not erase the route it was waiting on.
      source.pendingLoads[1]?.reject(apiError('unauthenticated', 401));
      expect(await newer).toBe('superseded');

      expect(store.sessionState()).toBe('anonymous');
      expect(navigations).toEqual([
        {
          commands: ['/login'],
          extras: { queryParams: { reason: 'expired', returnUrl: '/orgs/o-2/projects/p-1' } },
        },
      ]);
    });

    it('clears the request tenant when an unknown org resolves not-found', async () => {
      expect(await store.resolveScope('o-foreign', null)).toBe('not-found');

      expect(tenant.orgId()).toBeNull();
      expect(store.currentOrg()).toBeNull();
    });

    it('publishes no grants once the scope is cleared', async () => {
      await store.resolveScope('o-1', 'p-1');
      expect(store.has('run:read')).toBe(true);

      expect(await store.resolveScope('o-foreign', null)).toBe('not-found');

      // No scope, no org the published codes were read for: `has()` must not keep answering.
      expect(store.currentOrg()).toBeNull();
      expect(store.permissions().size).toBe(0);
      expect(store.has('run:read')).toBe(false);
    });

    for (const [kind, status] of [
      ['unavailable', 503],
      ['forbidden', 403],
      ['not_found', 404],
    ] as const) {
      it(`answers unavailable, not not-found, when the permission reload fails ${kind}`, async () => {
        await store.resolveScope('o-1', null);
        source.failure = apiError(kind, status);

        // A refused grant read says nothing about whether the org exists, so it is never the
        // 404 pattern the project lookup shares.
        expect(await store.resolveScope('o-2', 'q-1')).toBe('unavailable');

        expect(tenant.orgId()).toBeNull();
        expect(store.currentOrg()).toBeNull();
        // No project list was read for an org whose grants could not be loaded.
        expect(identity.projectOrgIds).toEqual(['o-1']);
      });
    }

    it('ends the session when the permission reload answers 401', async () => {
      await store.resolveScope('o-1', null);
      source.failure = apiError('unauthenticated', 401);

      expect(await store.resolveScope('o-2', 'q-1')).toBe('superseded');

      expect(store.sessionState()).toBe('anonymous');
      expect(tenant.orgId()).toBeNull();
      expect(navigations).toEqual([
        {
          commands: ['/login'],
          extras: { queryParams: { reason: 'expired', returnUrl: '/orgs/o-2/projects/q-1' } },
        },
      ]);
    });
  });
});
