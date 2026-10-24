// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4.2, §2 AC4; 00 §9.2; D-S68-Q4): the org and project scope
// guards. Their refusal is a boolean `false`, never a redirect: a non-match lets the wildcard route
// take the URL and render the SAME 404 page an unmatched route gets, so a foreign id cannot be told
// apart from a URL that never existed. `unavailable` is the only case that redirects, and only to
// the retry page.

import { TestBed } from '@angular/core/testing';
import { Router, UrlSegment, UrlTree, provideRouter } from '@angular/router';
import type { OrganizationSummaryDto } from '@evidgate/sdk';

import { SessionStore, type ScopeResolution, type SessionState } from '../session/session.store';
import { orgScopeGuard, projectScopeGuard } from './scope.guard';
import { EMPTY_MATCH_SNAPSHOT } from './spec-helpers';

const ORG: OrganizationSummaryDto = {
  id: 'o-1',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};

/** One `resolveScope` call the guard made. */
interface ScopeCall {
  readonly orgId: string;
  readonly projectId: string | null;
}

/** The guards' whole dependency: a state, the resolved org and the scope lookup. */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  resolution: ScopeResolution = 'ok';
  org: OrganizationSummaryDto | null = ORG;
  calls: ScopeCall[] = [];

  async bootstrap(): Promise<void> {
    // The guards await one bootstrap; the fake needs no work of its own.
  }

  sessionState(): SessionState {
    return this.state;
  }

  resolveScope(orgId: string, projectId: string | null): Promise<ScopeResolution> {
    this.calls.push({ orgId, projectId });
    return Promise.resolve(this.resolution);
  }

  currentOrg(): OrganizationSummaryDto | null {
    return this.org;
  }
}

describe('orgScopeGuard (09.1.1-T4.1)', () => {
  let session: FakeSessionStore;
  let router: Router;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    router = TestBed.inject(Router);
  });

  function run(segments: readonly UrlSegment[]): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      orgScopeGuard({}, [...segments], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it('matches an org the server listed and resolves it without a project', async () => {
    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-1', {})]);

    expect(result).toBe(true);
    expect(session.calls).toEqual([{ orgId: 'o-1', projectId: null }]);
  });

  it('does not match a foreign or unknown org, so the wildcard renders the 404 pattern', async () => {
    session.resolution = 'not-found';

    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-foreign', {})]);

    expect(result).toBe(false);
    expect(result).not.toBeInstanceOf(UrlTree);
  });

  it('redirects to the retry page when the scope cannot be read', async () => {
    session.resolution = 'unavailable';

    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('an unreadable scope must not match');
    }
    expect(router.serializeUrl(result)).toBe('/unavailable');
  });

  it('does not match a route shape without an id, and asks the store nothing', async () => {
    const result = await run([new UrlSegment('orgs', {})]);

    expect(result).toBe(false);
    expect(session.calls).toEqual([]);
  });

  it('sends an anonymous caller to login with the attempted org path', async () => {
    session.state = 'anonymous';

    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('an anonymous session must not match');
    }
    expect(result.queryParams['returnUrl']).toBe('/orgs/o-1');
  });

  it('redirects to the retry page when the session itself is unreadable', async () => {
    session.state = 'unavailable';

    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('an unreadable session must not match');
    }
    expect(router.serializeUrl(result)).toBe('/unavailable');
    expect(session.calls).toEqual([]);
  });
});

describe('projectScopeGuard (09.1.1-T4.1/T4.2)', () => {
  let session: FakeSessionStore;
  let router: Router;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    router = TestBed.inject(Router);
  });

  function run(segments: readonly UrlSegment[]): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      projectScopeGuard({}, [...segments], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it('matches a project of the resolved org', async () => {
    const result = await run([new UrlSegment('projects', {}), new UrlSegment('p-1', {})]);

    expect(result).toBe(true);
    expect(session.calls).toEqual([{ orgId: 'o-1', projectId: 'p-1' }]);
  });

  it('does not match a project the org does not have', async () => {
    session.resolution = 'not-found';

    const result = await run([new UrlSegment('projects', {}), new UrlSegment('p-foreign', {})]);

    expect(result).toBe(false);
  });

  it('does not match without a resolved org, and asks the store nothing', async () => {
    session.org = null;

    const result = await run([new UrlSegment('projects', {}), new UrlSegment('p-1', {})]);

    expect(result).toBe(false);
    expect(session.calls).toEqual([]);
  });

  it('redirects to the retry page when the project list cannot be read', async () => {
    session.resolution = 'unavailable';

    const result = await run([new UrlSegment('projects', {}), new UrlSegment('p-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('an unreadable scope must not match');
    }
    expect(router.serializeUrl(result)).toBe('/unavailable');
  });

  it('sends an anonymous caller to login with the attempted project path', async () => {
    session.state = 'anonymous';

    const result = await run([new UrlSegment('projects', {}), new UrlSegment('p-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('an anonymous session must not match');
    }
    expect(result.queryParams['returnUrl']).toBe('/projects/p-1');
  });
});
