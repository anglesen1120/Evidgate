// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §5; AC5; D-S68-Q4): the session guard. It is the one guard
// that must never match an anonymous caller, and its redirect must be a same-origin path this app
// built — never a value echoed back from the request.
//
// The store is faked to its public surface so each case states one guard decision; the real store's
// own read paths are pinned in `../session/session.store.spec.ts`.

import { TestBed } from '@angular/core/testing';
import { Router, UrlSegment, UrlTree, provideRouter } from '@angular/router';

import { SessionStore, type SessionState } from '../session/session.store';
import { attemptedPath, authGuard, signInTree } from './auth.guard';
import { EMPTY_MATCH_SNAPSHOT } from './spec-helpers';

/** The guard's whole dependency: a state and an idempotent bootstrap. */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  bootstrapCalls = 0;

  bootstrap(): Promise<void> {
    this.bootstrapCalls += 1;
    return Promise.resolve();
  }

  sessionState(): SessionState {
    return this.state;
  }
}

const ATTEMPTED = [
  new UrlSegment('orgs', {}),
  new UrlSegment('o-1', {}),
  new UrlSegment('projects', {}),
  new UrlSegment('p-1', {}),
  new UrlSegment('runs', {}),
];

/** The serialized URL of a guard result, refusing a boolean where a redirect was expected. */
function urlOf(router: Router, result: unknown): string {
  if (!(result instanceof UrlTree)) {
    throw new Error(`expected a UrlTree, received ${String(result)}`);
  }
  return router.serializeUrl(result);
}

describe('authGuard (09.1.1-T4.1)', () => {
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
      authGuard({}, [...segments], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it('matches an authenticated session and awaits one bootstrap', async () => {
    const result = await run(ATTEMPTED);

    expect(result).toBe(true);
    expect(session.bootstrapCalls).toBe(1);
  });

  it('sends an anonymous caller to /login with the attempted path as returnUrl', async () => {
    session.state = 'anonymous';

    const result = await run(ATTEMPTED);

    if (!(result instanceof UrlTree)) {
      throw new Error('anonymous must not match');
    }
    expect(router.serializeUrl(result).startsWith('/login?')).toBe(true);
    expect(result.queryParams['returnUrl']).toBe('/orgs/o-1/projects/p-1/runs');
  });

  it('never carries a query string or fragment into returnUrl', async () => {
    session.state = 'anonymous';

    const result = await run([new UrlSegment('orgs', {}), new UrlSegment('o-1', {})]);

    if (!(result instanceof UrlTree)) {
      throw new Error('anonymous must not match');
    }
    expect(result.queryParams['returnUrl']).toBe('/orgs/o-1');
  });

  it('sends an unreadable session to /unavailable rather than to login', async () => {
    session.state = 'unavailable';

    expect(urlOf(router, await run(ATTEMPTED))).toBe('/unavailable');
  });

  it('treats a still-unknown state after bootstrap as anonymous', async () => {
    session.state = 'unknown';

    const result = await run(ATTEMPTED);

    expect(router.serializeUrl(result as UrlTree).startsWith('/login')).toBe(true);
  });

  it('builds a same-origin path from the segments only', () => {
    expect(attemptedPath([])).toBe('/');
    expect(attemptedPath([new UrlSegment('orgs', {}), new UrlSegment('o 1', {})])).toBe(
      '/orgs/o%201',
    );
  });

  it('cannot turn a segment into a protocol-relative authority', () => {
    expect(attemptedPath([new UrlSegment('//evil.example', {})])).toBe('/%2F%2Fevil.example');
    expect(attemptedPath([new UrlSegment('https:', {})])).toBe('/https%3A');
  });

  it('signInTree reuses the attempted path for the same redirect', () => {
    const tree = signInTree(router, ATTEMPTED);

    expect(tree.queryParams['returnUrl']).toBe('/orgs/o-1/projects/p-1/runs');
    expect(router.serializeUrl(tree).startsWith('/login?')).toBe(true);
  });
});
