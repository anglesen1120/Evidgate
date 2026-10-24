// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; D-S68-Q4): the landing guards of `/` and
// `/orgs/:orgId`. Their answer is computed from the server's own lists — the first organization the
// server returned and its first project — never from a remembered scope or a guessed id.

import { TestBed } from '@angular/core/testing';
import {
  Router,
  UrlTree,
  convertToParamMap,
  provideRouter,
  type ActivatedRouteSnapshot,
  type ParamMap,
  type RouterStateSnapshot,
} from '@angular/router';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { SessionStore, type ScopeResolution, type SessionState } from '../session/session.store';
import { landingRedirectGuard, orgLandingGuard } from './landing.guard';

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

/** The guards' whole dependency. */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  listed: readonly OrganizationSummaryDto[] = [ORG_ONE];
  listedProjects: readonly ProjectDto[] = [];
  resolution: ScopeResolution = 'ok';
  calls: Array<{ orgId: string; projectId: string | null }> = [];

  async bootstrap(): Promise<void> {
    // One idempotent bootstrap; nothing to do.
  }

  sessionState(): SessionState {
    return this.state;
  }

  organizations(): readonly OrganizationSummaryDto[] {
    return this.listed;
  }

  projects(): readonly ProjectDto[] {
    return this.listedProjects;
  }

  resolveScope(orgId: string, projectId: string | null): Promise<ScopeResolution> {
    this.calls.push({ orgId, projectId });
    return Promise.resolve(this.resolution);
  }
}

/** The two members `orgLandingGuard` reads off the router's snapshots. */
function routeWith(paramMap: ParamMap): ActivatedRouteSnapshot {
  return { paramMap } as unknown as ActivatedRouteSnapshot;
}

function stateOf(url: string): RouterStateSnapshot {
  return { url } as unknown as RouterStateSnapshot;
}

function urlOf(router: Router, result: unknown): string {
  if (!(result instanceof UrlTree)) {
    throw new Error(`expected a UrlTree, received ${String(result)}`);
  }
  return router.serializeUrl(result);
}

describe('landingRedirectGuard (09.1.1-T4.1)', () => {
  let session: FakeSessionStore;
  let router: Router;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    router = TestBed.inject(Router);
  });

  function run(): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      landingRedirectGuard(routeWith(convertToParamMap({})), stateOf('/')),
    );
  }

  it('sends an anonymous caller to login without a returnUrl', async () => {
    session.state = 'anonymous';

    const result = await run();

    expect(urlOf(router, result)).toBe('/login');
  });

  it('sends an unreadable session to the retry page', async () => {
    session.state = 'unavailable';

    expect(urlOf(router, await run())).toBe('/unavailable');
  });

  it('sends a caller with no organization to /welcome', async () => {
    session.listed = [];

    expect(urlOf(router, await run())).toBe('/welcome');
  });

  it('lands on the first org and its first project overview', async () => {
    session.listed = [ORG_ONE, ORG_TWO];
    session.listedProjects = [project('p-1', 'o-1'), project('p-2', 'o-1')];

    const result = await run();

    expect(urlOf(router, result)).toBe('/orgs/o-1/projects/p-1/overview');
    expect(session.calls).toEqual([{ orgId: 'o-1', projectId: null }]);
  });

  it('lands on onboarding for an org that has no project yet', async () => {
    session.listedProjects = [];

    expect(urlOf(router, await run())).toBe('/orgs/o-1/onboarding');
  });

  it('redirects to the retry page when the first org cannot be resolved', async () => {
    session.resolution = 'unavailable';

    expect(urlOf(router, await run())).toBe('/unavailable');
  });

  it('does not activate when the first org is no longer resolvable', async () => {
    session.resolution = 'not-found';

    expect(await run()).toBe(false);
  });
});

describe('orgLandingGuard (09.1.1-T4.2)', () => {
  let session: FakeSessionStore;
  let router: Router;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    router = TestBed.inject(Router);
  });

  function run(orgId: string, url = `/orgs/${orgId}`): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      orgLandingGuard(routeWith(convertToParamMap({ orgId })), stateOf(url)),
    );
  }

  it('lands on the org named by the URL, not on a remembered org', async () => {
    session.listed = [ORG_ONE, ORG_TWO];
    session.listedProjects = [project('q-1', 'o-2')];

    const result = await run('o-2');

    expect(urlOf(router, result)).toBe('/orgs/o-2/projects/q-1/overview');
    expect(session.calls).toEqual([{ orgId: 'o-2', projectId: null }]);
  });

  it('lands on the onboarding step of the org named by the URL', async () => {
    session.listedProjects = [];

    expect(urlOf(router, await run('o-2'))).toBe('/orgs/o-2/onboarding');
  });

  it('keeps the attempted deep link for an anonymous caller', async () => {
    session.state = 'anonymous';

    const result = await run('o-2');

    if (!(result instanceof UrlTree)) {
      throw new Error('an anonymous session must not activate');
    }
    expect(result.queryParams['returnUrl']).toBe('/orgs/o-2');
  });

  it('does not activate a route without an org id', async () => {
    const result = await TestBed.runInInjectionContext(async () =>
      orgLandingGuard(routeWith(convertToParamMap({})), stateOf('/orgs')),
    );

    expect(result).toBe(false);
  });

  it('sends an unreadable session to the retry page', async () => {
    session.state = 'unavailable';

    expect(urlOf(router, await run('o-1'))).toBe('/unavailable');
    expect(session.calls).toEqual([]);
  });
});
