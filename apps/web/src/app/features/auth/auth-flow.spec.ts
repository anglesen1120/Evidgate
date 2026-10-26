// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; D-S98-Q2/Q3; contracts/openapi.yaml `/v1/auth/oidc/…`): the
// sign-in flow the login page drives.
//
// What these cases pin, in the order the reviewer needs them:
//   - the browser navigation is a full page load to the API's start route, with the callback and the
//     validated target encoded inside `returnUrl` — no form, no fetch, no password;
//   - the provider is the closed union the page passes, never a value read from the URL;
//   - a tampered `returnUrl` is `/` here and again at the callback (the two independent checks);
//   - an unknown `?reason=` is `none`, and the store's signed-out latch is `signed-out`;
//   - a session that already exists at `/login` is sent to the validated target;
//   - the flow leaves no auth material in `localStorage` or `sessionStorage` (AC5's second half: the
//     cookie is the credential, and it is `HttpOnly`).

import { Component, inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  type ActivatedRouteSnapshot,
  Router,
  type RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';

import { SessionStore, type SessionState } from '../../core/session/session.store';
import {
  AuthFlow,
  LOCATION_NAVIGATE,
  LOGIN_PROVIDERS,
  OIDC_START_PATH,
  signedInRedirectGuard,
} from './auth-flow';

/** The query-parameter surface `AuthFlow` and the guard read, without a router transition. */
function queryParamMap(values: Readonly<Record<string, string>>): {
  get(name: string): string | null;
} {
  return {
    get: (name: string): string | null => values[name] ?? null,
  };
}

/** The store surface the flow touches: the latch, `reauth()` and the bootstrap the guard awaits. */
class FakeSessionStore {
  readonly signedOut = signal(false);
  state: SessionState = 'anonymous';
  reauthCalls = 0;
  bootstrapCalls = 0;

  bootstrap(): Promise<void> {
    this.bootstrapCalls += 1;
    return Promise.resolve();
  }

  sessionState(): SessionState {
    return this.state;
  }

  reauth(): void {
    this.reauthCalls += 1;
    this.signedOut.set(false);
  }
}

/** The mounted flow plus everything a case asserts on. */
interface Harness {
  readonly flow: AuthFlow;
  readonly session: FakeSessionStore;
  readonly navigations: string[];
}

function setUp(query: Readonly<Record<string, string>> = {}): Harness {
  // A case may build several harnesses in one `it` (the reason table); each starts from a clean
  // module so an earlier injection cannot fail the next configuration.
  TestBed.resetTestingModule();
  const session = new FakeSessionStore();
  const navigations: string[] = [];
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      // The login route provides the flow per activation; a spec mirrors that.
      AuthFlow,
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: queryParamMap(query) } } },
      { provide: SessionStore, useValue: session },
      { provide: LOCATION_NAVIGATE, useValue: (url: string): void => void navigations.push(url) },
    ],
  });
  return { flow: TestBed.inject(AuthFlow), session, navigations };
}

/** The `returnUrl` a start navigation carries, already decoded once (the API reads it that way). */
function encodedReturnUrl(navigation: string): string {
  const query = navigation.slice(navigation.indexOf('?') + 1);
  return new URLSearchParams(query).get('returnUrl') ?? '';
}

/** The target the callback URL carries, decoded as the callback page reads its own query. */
function callbackTarget(navigation: string): string {
  const callback = encodedReturnUrl(navigation);
  return new URLSearchParams(callback.slice(callback.indexOf('?') + 1)).get('returnUrl') ?? '';
}

describe('AuthFlow (09.1.1-T5.1)', () => {
  it('offers exactly the two providers the API serves', () => {
    expect(LOGIN_PROVIDERS).toEqual(['google', 'github']);
  });

  it('navigates the browser to the API start route with the callback and the target encoded', () => {
    const { flow, navigations } = setUp({ returnUrl: '/orgs/o-1/projects/p-1/overview' });

    flow.start('google');

    // One navigation, a full page load: the API redirects to the provider and sets the cookie.
    expect(navigations).toHaveLength(1);
    expect(navigations[0]?.startsWith(`${OIDC_START_PATH}/google/start?returnUrl=`)).toBe(true);
    // The outer value is this app's callback route (`<base href>auth/callback`, no base in a spec);
    // the inner one is the router path the callback will navigate to.
    expect(encodedReturnUrl(navigations[0] ?? '')).toBe(
      `/auth/callback?returnUrl=${encodeURIComponent('/orgs/o-1/projects/p-1/overview')}`,
    );
    expect(callbackTarget(navigations[0] ?? '')).toBe('/orgs/o-1/projects/p-1/overview');
  });

  it('navigates for either provider and never reads one from the URL', () => {
    const { flow, navigations } = setUp({ returnUrl: '/runs', provider: 'github' });

    flow.start('google');

    expect(navigations[0]).toContain(`${OIDC_START_PATH}/google/start`);
    expect(navigations[0]).not.toContain('github');
  });

  it('answers a tampered returnUrl with the workspace root', () => {
    const { flow, navigations } = setUp({ returnUrl: '//evil.example/steal' });

    flow.start('github');

    expect(callbackTarget(navigations[0] ?? '')).toBe('/');
    expect(navigations[0]).not.toContain('evil.example');
  });

  it('starts once: a second call while the browser is leaving is ignored', () => {
    const { flow, navigations } = setUp({ returnUrl: '/runs' });

    expect(flow.redirecting()).toBe(false);
    flow.start('google');
    expect(flow.redirecting()).toBe(true);
    flow.start('github');

    expect(navigations).toHaveLength(1);
  });

  it('clears the signed-out latch before leaving for the provider', () => {
    const { flow, session } = setUp({ returnUrl: '/runs' });
    session.signedOut.set(true);

    flow.start('google');

    expect(session.reauthCalls).toBe(1);
    expect(session.signedOut()).toBe(false);
  });

  it('reads reason from the URL and turns an unknown value into none', () => {
    expect(setUp({ reason: 'expired' }).flow.reason()).toBe('expired');
    expect(setUp({ reason: 'signed-out' }).flow.reason()).toBe('signed-out');
    expect(setUp({ reason: 'unavailable' }).flow.reason()).toBe('unavailable');
    expect(setUp({ reason: 'failed' }).flow.reason()).toBe('failed');
    expect(setUp({ reason: 'something-else' }).flow.reason()).toBe('none');
    expect(setUp({}).flow.reason()).toBe('none');
  });

  it('falls back to the store latch for a signed-out tab with no reason parameter', () => {
    const { flow, session } = setUp({});
    session.signedOut.set(true);

    expect(flow.reason()).toBe('signed-out');
  });

  it('gives every page activation its own query, and its own notices', () => {
    // The flow is a provider of the page, so the proof is component-shaped: a host with the same
    // `providers` entry, mounted twice with two different activation queries.
    @Component({
      selector: 'app-auth-flow-host',
      standalone: true,
      providers: [AuthFlow],
      template: '',
    })
    class FlowHost {
      readonly flow = inject(AuthFlow);
    }

    const mount = (query: Readonly<Record<string, string>>): AuthFlow => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: SessionStore, useValue: new FakeSessionStore() },
          // What the router supplies per activation: the matched route's own snapshot.
          {
            provide: ActivatedRoute,
            useValue: { snapshot: { queryParamMap: queryParamMap(query) } },
          },
        ],
      });
      return TestBed.createComponent(FlowHost).componentInstance.flow;
    };

    const afterExpiry = mount({ reason: 'expired', returnUrl: '/orgs/o-1/projects/p-1/overview' });
    const afterSignOut = mount({ reason: 'signed-out' });

    // The second activation does not inherit the first one's reason or target.
    expect(afterExpiry.reason()).toBe('expired');
    expect(afterSignOut.reason()).toBe('signed-out');
    expect(afterExpiry.returnUrl()).toBe('/orgs/o-1/projects/p-1/overview');
    expect(afterSignOut.returnUrl()).toBe('/');
  });

  it('removes its pageshow listener when the page is destroyed', () => {
    const { flow } = setUp({ returnUrl: '/runs' });
    const view = document.defaultView;
    const remove = vi.spyOn(view as Window, 'removeEventListener');

    // `TestBed.resetTestingModule()` in the next `setUp` destroys the module's instances; the
    // DestroyRef hook is what must unpair the listener the constructor added.
    TestBed.resetTestingModule();

    expect(remove.mock.calls.some(([type]) => type === 'pageshow')).toBe(true);
    expect(flow.redirecting()).toBe(false);
  });

  it('stops redirecting when the browser restores the page from the back-forward cache', () => {
    const { flow } = setUp({ returnUrl: '/runs' });
    flow.start('google');
    expect(flow.redirecting()).toBe(true);

    // A plain `pageshow` (the ordinary load) says nothing about the attempt: the flow stays busy.
    document.defaultView?.dispatchEvent(new Event('pageshow'));
    expect(flow.redirecting()).toBe(true);

    // `persisted` means the browser put this document back from its cache: the navigation that
    // disabled the buttons never completed, so the controls must work again.
    const restored = new Event('pageshow');
    Object.defineProperty(restored, 'persisted', { value: true });
    document.defaultView?.dispatchEvent(restored);

    expect(flow.redirecting()).toBe(false);
  });

  it('leaves no auth material in web storage (AC5)', () => {
    const { flow } = setUp({ returnUrl: '/orgs/o-1' });

    flow.start('google');

    // The credential is the `HttpOnly` session cookie: nothing this app could store.
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});

describe('signedInRedirectGuard (09.1.1-T5.1)', () => {
  function run(
    query: Readonly<Record<string, string>>,
    state: SessionState,
  ): Promise<boolean | UrlTree> {
    const session = new FakeSessionStore();
    session.state = state;
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
    return TestBed.runInInjectionContext(
      () =>
        signedInRedirectGuard(
          { queryParamMap: queryParamMap(query) } as unknown as ActivatedRouteSnapshot,
          {} as RouterStateSnapshot,
        ) as Promise<boolean | UrlTree>,
    );
  }

  it('sends an authenticated visitor to the validated returnUrl', async () => {
    const result = await run({ returnUrl: '/orgs/o-1/projects/p-1/overview' }, 'authenticated');
    const router = TestBed.inject(Router);

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/orgs/o-1/projects/p-1/overview');
  });

  it('sends an authenticated visitor with a tampered target to the workspace root', async () => {
    const result = await run({ returnUrl: 'https://evil.example' }, 'authenticated');
    const router = TestBed.inject(Router);

    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });

  it('lets an anonymous visitor see the page', async () => {
    expect(await run({ returnUrl: '/runs' }, 'anonymous')).toBe(true);
  });

  it('lets an unreadable session see the page, because sign-in must stay reachable', async () => {
    expect(await run({}, 'unavailable')).toBe(true);
  });
});
