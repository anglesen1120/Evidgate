// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; AC4/AC5; D-S68-Q1/Q2/Q4): the session and scope store.
// Every case is a decision a guard depends on:
//
// - one bootstrap, one read (a burst of guards must not fire N org lists);
// - `401` is `anonymous`, everything else that fails is `unavailable` — never a silently empty
//   workspace;
// - the URL is never evidence: an org must be one the server listed, a project must come from that
//   org's own list *and* carry the same `org_id`, otherwise `not-found` and no scope at all;
// - `has()` reads only the loaded set, so an empty set grants nothing.

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
  organizationCalls = 0;
  projectOrgIds: string[] = [];
  readonly pending: PendingProjects[] = [];

  listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    this.organizationCalls += 1;
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
    it('reads organizations then the permission set and becomes authenticated', async () => {
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

      await store.bootstrap();

      expect(store.sessionState()).toBe('anonymous');
      expect(store.permissions().size).toBe(0);
      expect(store.has('run:read')).toBe(false);
      expect(source.calls).toBe(0);
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
      // The stale answer arrives last and must change nothing.
      older.resolve([PROJECT_P1, PROJECT_P2]);
      expect(await first).toBe('not-found');

      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.currentProject()).toEqual(PROJECT_Q1);
      expect(store.projects()).toEqual([PROJECT_Q1]);
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
