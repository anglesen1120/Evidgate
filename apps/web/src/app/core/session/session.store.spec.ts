// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; AC4/AC5; D-S68-Q1/Q2/Q4): the session and scope store.
// 09.2.1-T1 converted the store to `@ngrx/signals`; every case below kept its assertions unchanged,
// because the conversion kept the public surface identical — signal names, method signatures and
// the resolve outcomes. The `adoptCreatedProject` block covers the one method the conversion added.
// Every case is a decision a guard depends on:
//
// - one bootstrap, one read (a burst of guards must not fire N org lists);
// - `401` is `anonymous`, everything else that fails is `unavailable` — never a silently empty
//   workspace;
// - the URL is never evidence: an org must be one the server listed, a project must come from that
//   org's own list *and* carry the same `org_id`, otherwise `not-found` and no scope at all;
// - `has()` reads only the loaded set, so an empty set grants nothing.

import { effect } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { ApiRequestError, type ApiErrorKind } from '../http/api-error';
import { UnauthenticatedRelay } from '../http/unauthenticated-relay';
import { IdentityDataService } from '../data/identity-data.service';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SESSION_ENDER, type SessionEnder } from './session-ender';
import { SessionStore } from './session.store';
import { TenantStore } from './tenant.store';

function apiError(kind: ApiErrorKind, status: number): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `${kind} (test)`,
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

const ORG_ONE: OrganizationSummaryDto = {
  id: 'o-1',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};
const ORG_TWO: OrganizationSummaryDto = {
  id: 'o-2',
  slug: 'lumen-labs',
  name: 'Lumen Labs',
  status: 'active',
};

function project(id: string, orgId: string): ProjectDto {
  return {
    id,
    org_id: orgId,
    name: `Project ${id}`,
    slug: `project-${id}`,
    status: 'active',
    default_environment_id: null,
    created_at: '2026-10-02T09:00:00Z',
    updated_at: '2026-10-18T11:20:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: null,
    case_prefix: 'PROJ',
  };
}

const PROJECT_P1 = project('p-1', 'o-1');
const PROJECT_P2 = project('p-2', 'o-1');
const PROJECT_Q1 = project('q-1', 'o-2');

/** One project-list read the store is waiting on, for the out-of-order scope case. */
interface PendingProjects {
  readonly orgId: string;
  readonly resolve: (projects: readonly ProjectDto[]) => void;
}

/**
 * The identity reads the store performs. `listPermissions` throws on purpose: the store never reads
 * the catalogue itself — that is the permission source's job (D-S68-Q1).
 */
class FakeIdentityDataService {
  organizations: readonly OrganizationSummaryDto[] = [];
  organizationFailure: Error | null = null;
  projectsByOrg = new Map<string, readonly ProjectDto[]>();
  projectsFailure: Error | null = null;
  /** When true, `listProjects` answers only when the test settles {@link pending}. */
  manualProjects = false;
  /** When true, `listMyOrganizations` answers only when the test settles {@link pendingOrgs}. */
  manualOrganizations = false;
  organizationCalls = 0;
  projectOrgIds: string[] = [];
  readonly pending: PendingProjects[] = [];
  readonly pendingOrgs: Array<(organizations: readonly OrganizationSummaryDto[]) => void> = [];

  listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    this.organizationCalls += 1;
    if (this.manualOrganizations) {
      // `Promise.withResolvers` is not in this project's lib target (ES2022).
      return new Promise<readonly OrganizationSummaryDto[]>((resolve) => {
        this.pendingOrgs.push(resolve);
      });
    }
    return this.organizationFailure === null
      ? Promise.resolve(this.organizations)
      : Promise.reject(this.organizationFailure);
  }

  listProjects(orgId: string): Promise<readonly ProjectDto[]> {
    this.projectOrgIds.push(orgId);
    if (this.manualProjects) {
      return new Promise<readonly ProjectDto[]>((resolve) => {
        this.pending.push({ orgId, resolve });
      });
    }
    return this.projectsFailure === null
      ? Promise.resolve(this.projectsByOrg.get(orgId) ?? [])
      : Promise.reject(this.projectsFailure);
  }

  listPermissions(): Promise<never> {
    throw new Error('the session store must not read the catalogue directly');
  }
}

/** The granted set the store consumes, plus the calls it observed. */
class FakePermissionsSource {
  granted: ReadonlySet<string> = new Set<string>();
  failure: Error | null = null;
  calls = 0;
  /**
   * The tenant each read ran under, read from the live store at call time: a permission read is
   * tenant-scoped, so what matters is the org published BEFORE the source was asked.
   */
  tenantAtLoad: (() => string | null) | null = null;
  readonly tenants: Array<string | null> = [];
  /** When true, `load` answers only when the test settles {@link pendingLoads}. */
  manual = false;
  readonly pendingLoads: Array<{
    readonly resolve: (granted: ReadonlySet<string>) => void;
    readonly reject: (error: unknown) => void;
  }> = [];

  load(): Promise<ReadonlySet<string>> {
    this.calls += 1;
    this.tenants.push(this.tenantAtLoad?.() ?? null);
    if (this.manual) {
      // This project's lib is ES2022, so `Promise.withResolvers` is unavailable.
      return new Promise<ReadonlySet<string>>((resolve, reject) => {
        this.pendingLoads.push({ resolve, reject });
      });
    }
    return this.failure === null ? Promise.resolve(this.granted) : Promise.reject(this.failure);
  }
}

/**
 * The one server-side seam of a sign-out (T5.2). It resolves by default — that is what the real
 * default does until 02.3.2-T2 — and `reject` proves a failing end never keeps the tab signed in.
 */
class FakeSessionEnder implements SessionEnder {
  calls = 0;
  rejection: Error | null = null;

  end(): Promise<void> {
    this.calls += 1;
    return this.rejection === null ? Promise.resolve() : Promise.reject(this.rejection);
  }
}

/** Lets every pending microtask (the store's awaits) run before the next assertion. */
async function settle(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

describe('SessionStore (09.1.1-T4.1/T4.2)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let ender: FakeSessionEnder;
  let router: Router;
  let navigations: Array<{ commands: readonly unknown[]; extras: unknown }>;
  let store: SessionStore;
  let tenant: TenantStore;

  beforeEach(() => {
    identity = new FakeIdentityDataService();
    source = new FakePermissionsSource();
    ender = new FakeSessionEnder();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: IdentityDataService, useValue: identity },
        { provide: EffectivePermissionsSource, useValue: source },
        { provide: SESSION_ENDER, useValue: ender },
      ],
    });
    store = TestBed.inject(SessionStore);
    tenant = TestBed.inject(TenantStore);
    source.tenantAtLoad = () => tenant.orgId();
    router = TestBed.inject(Router);
    // The two session boundaries leave through `/login`; a spec records the decision instead of
    // running a navigation through an empty route table. Recording (rather than asserting on the
    // spy object) keeps the expected call shape readable as data.
    navigations = [];
    vi.spyOn(router, 'navigate').mockImplementation((commands, extras) => {
      navigations.push({ commands: commands as readonly unknown[], extras });
      return Promise.resolve(true);
    });
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
      // The catalogue is read in the same round trip (P2), and its answer is discarded: an
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

    it('does not reload the permissions for a navigation inside the org it already scoped', async () => {
      await store.resolveScope('o-1', 'p-1');

      expect(await store.resolveScope('o-1', 'p-2')).toBe('ok');

      expect(source.calls).toBe(1);
      expect(tenant.orgId()).toBe(ORG_ONE.id);
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

    it('resolves an org and its project from the server lists', async () => {
      const resolution = await store.resolveScope('o-1', 'p-2');

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_ONE);
      expect(store.currentProject()).toEqual(PROJECT_P2);
      expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
    });

    it('resolves an org with no project for an org-scope route', async () => {
      const resolution = await store.resolveScope('o-1', null);

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_ONE);
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
    });

    it('refuses an org the server did not list and reads no project list', async () => {
      const resolution = await store.resolveScope('o-foreign', 'p-1');

      expect(resolution).toBe('not-found');
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
      expect(identity.projectOrgIds).toEqual([]);
    });

    it('refuses a project id the org does not have', async () => {
      const resolution = await store.resolveScope('o-1', 'p-unknown');

      expect(resolution).toBe('not-found');
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
    });

    it('refuses a project the server returned with another org_id', async () => {
      identity.projectsByOrg.set('o-1', [{ ...PROJECT_Q1, id: 'p-9' }]);

      const resolution = await store.resolveScope('o-1', 'p-9');

      expect(resolution).toBe('not-found');
      expect(store.currentProject()).toBeNull();
    });

    it('drops a previous scope before resolving the next one', async () => {
      await store.resolveScope('o-1', 'p-1');
      expect(store.currentProject()).toEqual(PROJECT_P1);

      const resolution = await store.resolveScope('o-2', 'q-1');

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.currentProject()).toEqual(PROJECT_Q1);
      expect(store.projects()).toEqual([PROJECT_Q1]);
    });

    it('refuses a scope while the session is anonymous, without reading projects', async () => {
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

      const resolution = await anonymousStore.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('not-found');
      expect(anonymousIdentity.projectOrgIds).toEqual([]);
      expect(anonymousStore.sessionState()).toBe('anonymous');
    });

    it('answers unavailable when the scope is unreadable', async () => {
      identity.projectsFailure = apiError('unavailable', 503);

      const resolution = await store.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('unavailable');
      expect(store.currentOrg()).toBeNull();
    });

    it('answers not-found for a project list refusal, which 00 §9.2 makes indistinguishable', async () => {
      identity.projectsFailure = apiError('not_found', 404);

      const resolution = await store.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('not-found');
    });

    it('answers not-found for a forbidden project list, never confirming the org exists', async () => {
      identity.projectsFailure = apiError('forbidden', 403);

      const resolution = await store.resolveScope('o-1', null);

      expect(resolution).toBe('not-found');
    });

    it('lets the newest navigation win when two resolutions overlap', async () => {
      identity.manualProjects = true;

      const first = store.resolveScope('o-1', 'p-1');
      await settle();
      const second = store.resolveScope('o-2', 'q-1');
      await settle();
      expect(identity.pending.map((entry) => entry.orgId)).toEqual(['o-1', 'o-2']);

      const newest = identity.pending[1];
      const older = identity.pending[0];
      if (newest === undefined || older === undefined) {
        throw new Error('both scope reads must have reached the fake');
      }
      newest.resolve([PROJECT_Q1]);
      expect(await second).toBe('ok');
      // The stale answer arrives last and must change nothing — and it must not claim that the
      // valid org it was asked about is unknown.
      older.resolve([PROJECT_P1, PROJECT_P2]);
      expect(await first).toBe('superseded');

      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.currentProject()).toEqual(PROJECT_Q1);
      expect(store.projects()).toEqual([PROJECT_Q1]);
    });

    it('publishes only the rows the server attributed to the resolved org', async () => {
      // The fixture corpus answers any org's project request with org 1's rows, and a server that
      // leaks another org's row must not make the switcher or a landing address use it.
      identity.projectsByOrg.set('o-2', [PROJECT_P1, PROJECT_Q1]);

      const resolution = await store.resolveScope('o-2', null);

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.projects()).toEqual([PROJECT_Q1]);
      // The explicit-id branch refuses the same foreign row, so one answer never contradicts the
      // other: the row is not a project of this org either way.
      expect(await store.resolveScope('o-2', PROJECT_P1.id)).toBe('not-found');
    });

    describe('inside the org already resolved', () => {
      /**
       * Records every value the two scope signals settle on: a same-org navigation must not pass
       * through a state where the sidebar has no org (or no projects) to render.
       */
      function recordScope(): Array<{
        readonly org: string | null;
        readonly projects: number;
        readonly project: string | null;
      }> {
        const seen: Array<{ org: string | null; projects: number; project: string | null }> = [];
        TestBed.runInInjectionContext(() => {
          effect(() => {
            seen.push({
              org: store.currentOrg()?.id ?? null,
              projects: store.projects().length,
              project: store.currentProject()?.id ?? null,
            });
          });
        });
        return seen;
      }

      it('reads the project list once for the whole navigation and never blanks the scope', async () => {
        const seen = recordScope();
        // The first run records the state before any resolve; later runs record each settle.
        TestBed.tick();

        // `/orgs/o-1/projects/p-1/<segment>`: the org guard, then the project guard.
        await store.resolveScope('o-1', null);
        TestBed.tick();
        await store.resolveScope('o-1', 'p-1');
        TestBed.tick();

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(seen).toEqual([
          { org: null, projects: 0, project: null },
          { org: 'o-1', projects: 2, project: null },
          { org: 'o-1', projects: 2, project: 'p-1' },
        ]);
      });

      it('answers a second navigation inside the org from the loaded list, without a read', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-1', 'p-2')).toBe('ok');

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(store.currentProject()).toEqual(PROJECT_P2);
        expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
      });

      it('refuses a project that is not in the loaded list without reading it again', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-1', 'p-unknown')).toBe('not-found');

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(store.currentOrg()).toBeNull();
      });

      it('re-reads the project list when the org really changes', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-2', 'q-1')).toBe('ok');

        expect(identity.projectOrgIds).toEqual(['o-1', 'o-2']);
        expect(store.currentOrg()).toEqual(ORG_TWO);
        expect(store.projects()).toEqual([PROJECT_Q1]);
      });

      it('drops the cache so reload() reads the list again', async () => {
        await store.resolveScope('o-1', null);

        await store.reload();
        await store.resolveScope('o-1', null);

        expect(identity.projectOrgIds).toEqual(['o-1', 'o-1']);
      });

      it('re-reads the list after a reload that recovered from a failed session read', async () => {
        await store.resolveScope('o-1', null);
        identity.organizationFailure = apiError('unavailable', 503);

        await expect(store.reload()).rejects.toThrow('still unavailable');
        expect(store.sessionState()).toBe('unavailable');

        identity.organizationFailure = null;
        await store.reload();

        expect(await store.resolveScope('o-1', null)).toBe('ok');
        // The failed generation and the recovered one both read the list: nothing from the old
        // session may survive into the new one.
        expect(identity.projectOrgIds).toEqual(['o-1', 'o-1']);
      });
    });
  });

  describe('adoptCreatedProject (09.2.1 T4 delta N2)', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      identity.projectsByOrg.set('o-1', [PROJECT_P1]);
      identity.projectsByOrg.set('o-2', [PROJECT_Q1]);
      await store.bootstrap();
      await store.resolveScope('o-1', null);
    });

    it('appends the created project to the loaded list of the current org, with no read', async () => {
      const created = project('p-new', 'o-1');

      store.adoptCreatedProject(created);

      expect(store.projects()).toEqual([PROJECT_P1, created]);
      // Adopting is not a navigation: the scope the wizard was on stays where it was.
      expect(store.currentProject()).toBeNull();
      expect(identity.projectOrgIds).toEqual(['o-1']);

      // The wizard redirects to the new project id, and the guards ask about it: the answer comes
      // from the list that now holds it, so the redirect does not land on the 404 pattern.
      expect(await store.resolveScope('o-1', created.id)).toBe('ok');
      expect(store.currentProject()).toEqual(created);
      // The cache, not just the published signal, was updated: a later same-org resolve reads
      // nothing either.
      expect(await store.resolveScope('o-1', null)).toBe('ok');
      expect(identity.projectOrgIds).toEqual(['o-1']);
      expect(store.projects()).toEqual([PROJECT_P1, created]);
    });

    it('appends a created project once, however often it is adopted', async () => {
      const created = project('p-new', 'o-1');

      store.adoptCreatedProject(created);
      store.adoptCreatedProject(created);

      expect(store.projects()).toEqual([PROJECT_P1, created]);
      expect(await store.resolveScope('o-1', created.id)).toBe('ok');
      expect(store.currentProject()).toEqual(created);
    });

    it('does not append an id the loaded list already holds', () => {
      store.adoptCreatedProject(PROJECT_P1);

      expect(store.projects()).toEqual([PROJECT_P1]);
    });

    it('publishes nothing for a project of another org and reads that org before answering', async () => {
      store.adoptCreatedProject(project('q-new', 'o-2'));

      // The current scope is org one, so its list is what the switcher offers.
      expect(store.projects()).toEqual([PROJECT_P1]);
      expect(store.currentOrg()).toEqual(ORG_ONE);

      // The current org's own list is untouched: adopting a foreign project is not a reason to
      // make the next same-org navigation read the list again.
      expect(await store.resolveScope('o-1', 'p-1')).toBe('ok');
      expect(identity.projectOrgIds).toEqual(['o-1']);

      // The adopted row must never answer for org two out of the store: the first resolve of that
      // org reads the server, so an id the server did not list is `not-found`, not a match.
      expect(await store.resolveScope('o-2', 'q-new')).toBe('not-found');
      expect(identity.projectOrgIds).toEqual(['o-1', 'o-2']);
    });

    it('publishes nothing when the org list was never read, and fabricates no cache entry', async () => {
      // A session that only knows the org — step one of the wizard — has no project list at all.
      TestBed.resetTestingModule();
      const freshIdentity = new FakeIdentityDataService();
      freshIdentity.organizations = [ORG_ONE];
      freshIdentity.projectsByOrg.set('o-1', [PROJECT_P1]);
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: IdentityDataService, useValue: freshIdentity },
          { provide: EffectivePermissionsSource, useValue: new FakePermissionsSource() },
        ],
      });
      const freshStore = TestBed.inject(SessionStore);
      await freshStore.bootstrap();

      freshStore.adoptCreatedProject(project('p-unread', 'o-1'));

      expect(freshStore.projects()).toEqual([]);
      // The next resolve reads the server list: the store never answers `ok` for a project it has
      // not seen the server list for this org in.
      expect(await freshStore.resolveScope('o-1', 'p-unread')).toBe('not-found');
      expect(freshIdentity.projectOrgIds).toEqual(['o-1']);
    });
  });

  describe('has', () => {
    it('grants nothing while the session is unknown', () => {
      expect(store.has('run:read')).toBe(false);
    });

    it('grants nothing from an empty set, even for a code the catalogue has', async () => {
      source.granted = new Set<string>();
      await store.bootstrap();

      expect(store.has('run:read')).toBe(false);
      expect(store.has('')).toBe(false);
    });

    it('grants exactly the codes the source loaded', async () => {
      source.granted = new Set(['run:read']);
      await store.bootstrap();

      expect(store.has('run:read')).toBe(true);
      expect(store.has('run:create')).toBe(false);
    });

    it('matches the whole code, never a prefix', async () => {
      source.granted = new Set(['run:read']);
      await store.bootstrap();

      expect(store.has('run')).toBe(false);
      expect(store.has('run:read:x')).toBe(false);
    });
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
      // (session-ender.ts: the HttpOnly cookie is still valid until 02.3.2-T2).
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
