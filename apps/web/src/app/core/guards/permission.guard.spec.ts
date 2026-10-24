// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §2 AC4; 00 §9.2; D-S68-Q1/Q3): the permission guard. AC4 is
// the shape of this spec: a route guarded for `run:read` is not matched when the server-declared
// set lacks the code, and the refusal is the 403 pattern — with no echo of the attempted URL, since
// that URL may name a resource of another tenant.

import { TestBed } from '@angular/core/testing';
import { Router, UrlSegment, UrlTree, provideRouter } from '@angular/router';

import { SessionStore, type SessionState } from '../session/session.store';
import { permissionGuard } from './permission.guard';
import { EMPTY_MATCH_SNAPSHOT } from './spec-helpers';

/** The guard's whole dependency: a state, an idempotent bootstrap and the loaded set. */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  granted: ReadonlySet<string> = new Set<string>();
  bootstrapCalls = 0;
  asked: string[] = [];

  bootstrap(): Promise<void> {
    this.bootstrapCalls += 1;
    return Promise.resolve();
  }

  sessionState(): SessionState {
    return this.state;
  }

  has(code: string): boolean {
    this.asked.push(code);
    return this.granted.has(code);
  }
}

const RUN_ROUTE = new UrlSegment('runs', {});

describe('permissionGuard (09.1.1-T4.1)', () => {
  let session: FakeSessionStore;
  let router: Router;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    router = TestBed.inject(Router);
  });

  /** Runs the guard for one code against one URL, as the router would during a match attempt. */
  async function run(
    code: string,
    segments: readonly UrlSegment[] = [RUN_ROUTE],
  ): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      permissionGuard(code)({}, [...segments], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it('matches when the loaded set holds the code', async () => {
    session.granted = new Set(['run:read']);

    const result = await run('run:read');

    expect(result).toBe(true);
    expect(session.asked).toEqual(['run:read']);
    expect(session.bootstrapCalls).toBe(1);
  });

  it('does not match another code of the same resource (AC4)', async () => {
    session.granted = new Set(['run:create']);

    const result = await run('run:read');

    expect(result).not.toBe(true);
    expect(result).toBeInstanceOf(UrlTree);
  });

  it('sends a caller without the code to the 403 pattern (AC4)', async () => {
    session.granted = new Set(['artifact:read', 'project:read']);

    const result = await run('run:read');

    if (!(result instanceof UrlTree)) {
      throw new Error('a missing permission must not match');
    }
    expect(router.serializeUrl(result)).toBe('/forbidden');
  });

  it('echoes nothing of the attempted URL in the refusal (D-S68-Q3)', async () => {
    session.granted = new Set<string>();

    const result = await run('run:read', [
      new UrlSegment('orgs', {}),
      new UrlSegment('o-foreign', {}),
      new UrlSegment('projects', {}),
      new UrlSegment('p-foreign', {}),
      RUN_ROUTE,
    ]);

    if (!(result instanceof UrlTree)) {
      throw new Error('a missing permission must not match');
    }
    expect(router.serializeUrl(result)).toBe('/forbidden');
    expect(Object.keys(result.queryParams)).toEqual([]);
  });

  it('matches nothing while the set is empty, even for a code that exists server-side', async () => {
    for (const code of ['run:read', 'authoring:read', 'organization:read']) {
      expect(await run(code)).not.toBe(true);
    }
  });

  it('sends an unreadable session to /unavailable', async () => {
    session.state = 'unavailable';

    const result = await run('run:read');

    if (!(result instanceof UrlTree)) {
      throw new Error('an unreadable session must not match');
    }
    expect(router.serializeUrl(result)).toBe('/unavailable');
    expect(session.asked).toEqual([]);
  });

  it('sends an anonymous caller to login instead of the 403 pattern', async () => {
    session.state = 'anonymous';

    const result = await run('run:read');

    if (!(result instanceof UrlTree)) {
      throw new Error('an anonymous session must not match');
    }
    // No `returnUrl` here: this guard only sees the segments left to consume, so a path built from
    // them would be partial. The parent route's `authGuard` owns the return URL.
    expect(router.serializeUrl(result)).toBe('/login');
    expect(result.queryParams['returnUrl']).toBeUndefined();
  });
});
