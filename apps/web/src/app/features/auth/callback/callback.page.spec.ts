// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; D-S98-Q2): the post-login landing.
//
// The two rules the plan makes structural are the two cases a reviewer must be able to read here:
//   - the page reads exactly ONE query parameter (`returnUrl`): `code` and `state` belong to the API's
//     own callback, and a client that touched them could log or render an authorization code;
//   - every outcome is a `replaceUrl` redirect, so the callback address never stays in the history:
//     the target when a session exists, `/login?reason=failed` when the probe says anonymous, and
//     `/login?reason=unavailable` when the probe itself could not be completed.

import type { ComponentFixture } from '@angular/core/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';

import { SessionStore, type SessionState } from '../../../core/session/session.store';
import { CallbackPage } from './callback.page';

/** A query-parameter map that records every parameter the page asks for. */
class RecordingParamMap {
  readonly asked: string[] = [];
  constructor(private readonly values: Readonly<Record<string, string>>) {}

  get(name: string): string | null {
    this.asked.push(name);
    return this.values[name] ?? null;
  }
}

/** The store surface the page touches: one `reload()` and the state it settled on. */
class FakeSessionStore {
  reloadCalls = 0;
  state: SessionState = 'authenticated';

  /**
   * When set, `reload()` rejects exactly as it does for `unavailable`.
   *
   * It is a constructor input because the page calls `reload()` from `ngOnInit`: setting it after
   * the harness mounted the page would be read too late, by the second (never-made) call.
   */
  readonly reloadFailure: Error | null;

  constructor(outcome: FakeSessionOutcome = {}) {
    this.state = outcome.state ?? 'authenticated';
    this.reloadFailure = outcome.reloadFailure ?? null;
  }

  reload(): Promise<void> {
    this.reloadCalls += 1;
    return this.reloadFailure === null ? Promise.resolve() : Promise.reject(this.reloadFailure);
  }

  sessionState(): SessionState {
    return this.state;
  }
}

/** The session answer a case mounts the page with. */
interface FakeSessionOutcome {
  readonly state?: SessionState;
  readonly reloadFailure?: Error;
}

/** Both router spellings the page uses, recorded in order. */
class FakeRouter {
  readonly navigations: Array<{ kind: 'url' | 'commands'; value: unknown; extras: unknown }> = [];

  navigateByUrl(url: string, extras?: unknown): Promise<boolean> {
    this.navigations.push({ kind: 'url', value: url, extras });
    return Promise.resolve(true);
  }

  navigate(commands: readonly unknown[], extras?: unknown): Promise<boolean> {
    this.navigations.push({ kind: 'commands', value: commands, extras });
    return Promise.resolve(true);
  }
}

/** Lets the page's `complete()` finish its awaits before the next assertion. */
async function settle(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

interface Harness {
  readonly fixture: ComponentFixture<CallbackPage>;
  readonly page: CallbackPage;
  readonly session: FakeSessionStore;
  readonly router: FakeRouter;
  readonly params: RecordingParamMap;
}

function setUp(query: Readonly<Record<string, string>>, outcome: FakeSessionOutcome = {}): Harness {
  const session = new FakeSessionStore(outcome);
  const router = new FakeRouter();
  const params = new RecordingParamMap(query);
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: params } } },
      { provide: SessionStore, useValue: session },
      { provide: Router, useValue: router },
    ],
  });
  const fixture = TestBed.createComponent(CallbackPage);
  fixture.detectChanges();
  return { fixture, page: fixture.componentInstance, session, router, params };
}

describe('CallbackPage (09.1.1-T5.1)', () => {
  it('keeps its one callback heading in the shared page role', () => {
    const { fixture } = setUp({});
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('#auth-callback-heading')?.getAttribute('data-title-size')).toBe(
      'page',
    );
  });

  it('reads only returnUrl: code and state are never touched', async () => {
    const { router, params } = setUp({
      returnUrl: '/orgs/o-1/projects/p-1/overview',
      code: 'auth-code-that-must-not-be-read',
      state: 'csrf-state-that-must-not-be-read',
      session_state: 'must-not-be-read-either',
    });
    await settle();

    expect(params.asked).toEqual(['returnUrl']);
    expect(params.asked).not.toContain('code');
    expect(params.asked).not.toContain('state');
    // Nothing the page navigates to carries the exchange parameters forward.
    expect(JSON.stringify(router.navigations)).not.toContain('auth-code-that-must-not-be-read');
  });

  it('lands on the target once the session probe is authenticated', async () => {
    const { router, session } = setUp({ returnUrl: '/orgs/o-1/projects/p-1/overview' });
    await settle();

    expect(session.reloadCalls).toBe(1);
    expect(router.navigations).toEqual([
      {
        kind: 'url',
        value: '/orgs/o-1/projects/p-1/overview',
        extras: { replaceUrl: true },
      },
    ]);
  });

  it('answers a tampered returnUrl with the workspace root', async () => {
    const { router } = setUp({ returnUrl: '//evil.example/steal' });
    await settle();

    expect(router.navigations).toEqual([{ kind: 'url', value: '/', extras: { replaceUrl: true } }]);
  });

  it('sends an anonymous probe to /login?reason=failed', async () => {
    const { router, session } = setUp({ returnUrl: '/runs' }, { state: 'anonymous' });
    await settle();

    // Exactly one probe, and its answer is what chose the reason.
    expect(session.reloadCalls).toBe(1);
    expect(router.navigations).toEqual([
      {
        kind: 'commands',
        value: ['/login'],
        extras: { queryParams: { reason: 'failed' }, replaceUrl: true },
      },
    ]);
  });

  it('sends an unreadable probe to /login?reason=unavailable', async () => {
    const { router, session } = setUp(
      { returnUrl: '/runs' },
      { reloadFailure: new Error('the workspace is still unavailable') },
    );
    await settle();

    expect(session.reloadCalls).toBe(1);
    expect(router.navigations).toEqual([
      {
        kind: 'commands',
        value: ['/login'],
        extras: { queryParams: { reason: 'unavailable' }, replaceUrl: true },
      },
    ]);
  });

  it('renders the loading pattern while the probe runs', () => {
    const { fixture, page } = setUp({ returnUrl: '/runs' });

    // `$localize` resolves the source message in the spec build.
    expect(page.heading).toBe('Finishing sign-in');
    expect(page.loadingLabel).toBe('Checking your session…');
    // Read from the fixture's own element: a TestBed host is not attached to `document`, so a
    // document-wide query would find nothing even when the pattern rendered.
    const host = fixture.nativeElement as HTMLElement;
    const pattern = host.querySelector('app-loading-pattern');
    expect(pattern).not.toBeNull();
    expect(pattern?.textContent).toContain('Checking your session…');
    expect(host.querySelector('main')?.getAttribute('aria-labelledby')).toBe(
      'auth-callback-heading',
    );
    expect(host.querySelectorAll('h1')).toHaveLength(1);
    expect(host.querySelector('button, a, input')).toBeNull();
    expect(pattern?.querySelector('[role="status"]')).not.toBeNull();
  });
});
