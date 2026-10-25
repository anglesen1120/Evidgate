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
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { ApiRequestError, type ApiErrorKind } from '../http/api-error';
import { IdentityDataService } from '../data/identity-data.service';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SessionStore } from './session.store';

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

  load(): Promise<ReadonlySet<string>> {
    this.calls += 1;
    return this.failure === null ? Promise.resolve(this.granted) : Promise.reject(this.failure);
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
  let store: SessionStore;

  beforeEach(() => {
    identity = new FakeIdentityDataService();
    source = new FakePermissionsSource();
    TestBed.configureTestingModule({
      providers: [
        { provide: IdentityDataService, useValue: identity },
        { provide: EffectivePermissionsSource, useValue: source },
      ],
    });
    store = TestBed.inject(SessionStore);
  });

  describe('bootstrap', () => {
    it('reads the organization list and the permission set, then becomes authenticated', async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      source.granted = new Set(['run:read', 'authoring:read']);

      await store.bootstrap();

      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE, ORG_TWO]);
      expect(store.permissions()).toBe(source.granted);
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

    it('asks the permission source without waiting for the organization list', async () => {
      identity.manualOrganizations = true;
      identity.organizations = [ORG_ONE];

      const booting = store.bootstrap();
      await settle();

      // Independent reads: the catalogue (up to twenty cursor pages) must not sit behind orgs.
      expect(source.calls).toBe(1);

      const first = identity.pendingOrgs[0];
      if (first === undefined) {
        throw new Error('the organization read must have reached the fake');
      }
      first([ORG_ONE]);
      await booting;

      expect(store.sessionState()).toBe('authenticated');
      expect(store.organizations()).toEqual([ORG_ONE]);
      expect(store.permissions()).toBe(source.granted);
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
        providers: [{ provide: IdentityDataService, useValue: identity }],
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
});
