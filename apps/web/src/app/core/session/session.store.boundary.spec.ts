import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { UnauthenticatedRelay } from '../http/unauthenticated-relay';
import { IdentityDataService } from '../data/identity-data.service';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SessionStore } from './session.store';
import type { TenantStore } from './tenant.store';
import {
  apiError,
  ORG_ONE,
  PROJECT_P1,
  settle,
  FakeIdentityDataService,
  FakePermissionsSource,
  type FakeSessionEnder,
  setupSessionStore,
} from './session.store.test-support';

describe('SessionStore (boundary)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let ender: FakeSessionEnder;
  let navigations: Array<{ commands: readonly unknown[]; extras: unknown }>;
  let store: SessionStore;
  let tenant: TenantStore;

  beforeEach(() => {
    ({ identity, source, ender, navigations, store, tenant } = setupSessionStore());
  });

  describe('logout (09.1.1-T5.2)', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE];
      identity.projectsByOrg.set('o-1', [PROJECT_P1]);
      source.granted = new Set(['run:read']);
      await store.bootstrap();
      await store.resolveScope('o-1', 'p-1');
    });

    it('clears every signal, latches the sign-out and leaves through /login', async () => {
      await store.logout();

      expect(store.sessionState()).toBe('anonymous');
      expect(store.signedOut()).toBe(true);
      expect(store.organizations()).toEqual([]);
      expect(store.projects()).toEqual([]);
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.permissions().size).toBe(0);
      // No tenant survives the session: the next request cannot be scoped by the ended one.
      expect(tenant.orgId()).toBeNull();
      // `reason=signed-out` is what the login page reads; it never claims the server session ended
      // (session-ender.ts: the HttpOnly cookie is still valid).
      expect(navigations).toEqual([
        { commands: ['/login'], extras: { queryParams: { reason: 'signed-out' } } },
      ]);
      // The one server-side seam was asked, and its default is a no-op that resolves.
      expect(ender.calls).toBe(1);
    });

    it('makes no request after the sign-out, however the session is asked for again', async () => {
      await store.logout();
      const organizationCalls = identity.organizationCalls;
      const permissionCalls = source.calls;
      const projectCalls = identity.projectOrgIds.length;

      await store.bootstrap();
      await store.reload();
      const resolution = await store.resolveScope('o-1', 'p-1');

      // The latch answers `anonymous` itself: an expiry probe, a guard burst or a retry button must
      // not re-open the session the caller just ended.
      expect(store.sessionState()).toBe('anonymous');
      expect(identity.organizationCalls).toBe(organizationCalls);
      expect(source.calls).toBe(permissionCalls);
      expect(identity.projectOrgIds.length).toBe(projectCalls);
      expect(resolution).toBe('not-found');
    });

    it('leaves the tab signed out even when the server-side end fails', async () => {
      ender.rejection = new Error('logout is not implemented yet');

      await store.logout();

      expect(store.sessionState()).toBe('anonymous');
      expect(store.signedOut()).toBe(true);
      expect(navigations).toEqual([
        { commands: ['/login'], extras: { queryParams: { reason: 'signed-out' } } },
      ]);
    });

    it('stops every registered session-scoped teardown, once', async () => {
      const stopped = vi.fn();
      const unregister = store.onSessionEnd(stopped);

      await store.logout();
      expect(stopped).toHaveBeenCalledTimes(1);

      // Unregistered work is not called again by the next sign-out; registered work is.
      unregister();
      store.reauth();
      await store.logout();
      expect(stopped).toHaveBeenCalledTimes(1);
    });

    it('runs the other teardowns when one throws', async () => {
      const stopped = vi.fn();
      store.onSessionEnd(() => {
        throw new Error('a broken teardown');
      });
      store.onSessionEnd(stopped);

      await store.logout();

      expect(stopped).toHaveBeenCalledTimes(1);
    });

    it('ignores the answer of a read that was on the wire when the session ended', async () => {
      identity.manualOrganizations = true;
      // `reload()` resets the state to `unknown`, so this read is genuinely on the wire.
      const booting = store.reload();
      await settle();
      expect(identity.pendingOrgs).toHaveLength(1);

      await store.logout();
      // The read answers for the session that no longer exists.
      identity.pendingOrgs[0]?.([ORG_ONE]);
      await booting;

      expect(store.sessionState()).toBe('anonymous');
      expect(store.organizations()).toEqual([]);
      expect(store.signedOut()).toBe(true);
      // The late organization list must not republish a tenant for the ended session.
      expect(tenant.orgId()).toBeNull();
    });

    it('does not redirect again for a 401 reported after the sign-out', async () => {
      const relay = TestBed.inject(UnauthenticatedRelay);
      await store.logout();
      navigations.length = 0;

      relay.notify(null);

      // A request issued before the sign-out can answer afterwards; it must not resurrect the
      // session or bounce the caller off the login page.
      expect(navigations).toEqual([]);
      expect(store.sessionState()).toBe('anonymous');
      expect(store.signedOut()).toBe(true);
    });

    it('re-auths from the server once a new sign-in clears the latch', async () => {
      await store.logout();

      store.reauth();
      expect(store.signedOut()).toBe(false);
      await store.reload();

      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE]);
    });
  });

  describe('an expired session (09.1.1-T5.2)', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE];
      identity.projectsByOrg.set('o-1', [PROJECT_P1]);
      source.granted = new Set(['run:read']);
      await store.bootstrap();
    });

    it('flips to anonymous once on a scope-read 401 and returns to the attempted route', async () => {
      identity.projectsFailure = apiError('unauthenticated', 401);

      const resolution = await store.resolveScope('o-1', 'p-1');

      // `superseded` cancels the guard chain: the store's own redirect owns this navigation now.
      expect(resolution).toBe('superseded');
      expect(store.sessionState()).toBe('anonymous');
      expect(store.permissions().size).toBe(0);
      expect(navigations).toEqual([
        {
          commands: ['/login'],
          extras: { queryParams: { reason: 'expired', returnUrl: '/orgs/o-1/projects/p-1' } },
        },
      ]);
    });

    it('does not redirect a second time for the same expiry', async () => {
      identity.projectsFailure = apiError('unauthenticated', 401);
      await store.resolveScope('o-1', 'p-1');
      const relay = TestBed.inject(UnauthenticatedRelay);

      // The guard chain asks again, and a page's other reads answer 401 too: still one redirect.
      const again = await store.resolveScope('o-1', 'p-1');
      relay.notify(null);
      relay.notify(null);

      expect(again).toBe('not-found');
      expect(navigations).toHaveLength(1);
    });

    it('uses the current route for a 401 another data service reports', () => {
      const relay = TestBed.inject(UnauthenticatedRelay);

      // The relay's handler is synchronous: it decides and calls the router before it returns.
      relay.notify(null);

      expect(navigations).toEqual([
        {
          commands: ['/login'],
          extras: { queryParams: { reason: 'expired', returnUrl: '/' } },
        },
      ]);
      expect(store.sessionState()).toBe('anonymous');
    });

    it('leaves a 401 during the first bootstrap to the bootstrap itself', async () => {
      TestBed.resetTestingModule();
      const anonymousIdentity = new FakeIdentityDataService();
      anonymousIdentity.organizationFailure = apiError('unauthenticated', 401);
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: IdentityDataService, useValue: anonymousIdentity },
          { provide: EffectivePermissionsSource, useValue: new FakePermissionsSource() },
        ],
      });
      const anonymousStore = TestBed.inject(SessionStore);
      const anonymousRouter = TestBed.inject(Router);
      const anonymousNavigations: unknown[] = [];
      vi.spyOn(anonymousRouter, 'navigate').mockImplementation((commands, extras) => {
        anonymousNavigations.push({ commands, extras });
        return Promise.resolve(true);
      });
      const relay = TestBed.inject(UnauthenticatedRelay);

      // The gateway reports the refusal while the state is still `unknown`: there is nothing to
      // expire yet, and the bootstrap answers `anonymous` without a redirect of its own.
      anonymousIdentity.listMyOrganizations = () => {
        relay.notify(null);
        return Promise.reject(apiError('unauthenticated', 401));
      };
      await anonymousStore.bootstrap();

      expect(anonymousStore.sessionState()).toBe('anonymous');
      expect(anonymousStore.signedOut()).toBe(false);
      expect(anonymousNavigations).toEqual([]);
    });
  });
});
