// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; 09.1.1-T3 hand-over, S61, #137; T38; D-S98-Q3): the sign-in
// flow of the web app, which is a full-page BROWSER NAVIGATION and never a form.
//
// The wire (contracts/openapi.yaml: `/v1/auth/oidc/{provider}/start`): the browser leaves the app for
// `/v1/auth/oidc/<provider>/start?returnUrl=…`, the API redirects to the provider, the provider
// answers at the API's `/v1/auth/oidc/<provider>/callback`, the API sets the `HttpOnly`
// `__Host-evidgate_session` cookie plus the readable `XSRF-TOKEN` cookie, and 302s to the
// allowlisted `returnUrl`. There is no password field anywhere and no token this app could store.
//
// The two paths this app owns:
//   - `/login` (this class): choose a provider, remember where the caller was going;
//   - `/auth/callback` (`callback/callback.page.ts`): the post-login landing. It is a ROUTE inside
//     the bundle, so it is spelled with the app's base href (`/<locale>/auth/callback`) while the
//     inner `returnUrl` is a router path (`/orgs/…`, no locale — the router adds the base href).
//
// `returnUrl` is validated by the shared `return-url.ts` on the way in (the `/login` query) and
// carried encoded inside the callback's own `returnUrl`, so the API's allowlist sees a path of this
// app and the callback re-validates what it receives: a tampered value is `/`, never an open
// redirect.
//
// The navigation itself is an injected seam (`LOCATION_NAVIGATE`), so the flow is testable without a
// real page unload: a spec replaces the token and asserts the exact URL, and the class never touches
// `window` directly.

import { DOCUMENT, Location } from '@angular/common';
import { computed, inject, Injectable, InjectionToken, signal, type Signal } from '@angular/core';
import { ActivatedRoute, Router, type CanActivateFn } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { validateReturnUrl } from '../../core/routing/return-url';

/** The sign-in providers the API offers (`contracts/openapi.yaml`, `/v1/auth/oidc/{provider}`). */
export type LoginProvider = 'google' | 'github';

/** The closed set of providers, in the order the login page renders them. */
export const LOGIN_PROVIDERS: readonly LoginProvider[] = ['google', 'github'];

/**
 * Why the login page is on screen. `none` is the ordinary first visit; every other value comes from
 * the `?reason=` parameter the store or the callback page set, and an unknown value is `none` rather
 * than a free-form string a translation or a template would have to trust.
 */
export type LoginReason = 'none' | 'expired' | 'signed-out' | 'unavailable' | 'failed';

/** The app route the API's callback redirects to, as the router spells it (no base href). */
export const AUTH_CALLBACK_ROUTE = '/auth/callback';

/** The API's OIDC start route; `/v1/auth/oidc/<provider>/start`. */
export const OIDC_START_PATH = '/v1/auth/oidc';

/** Everything the login page may show, as one table: the URL value maps to `none` if unknown. */
const LOGIN_REASONS: readonly LoginReason[] = [
  'none',
  'expired',
  'signed-out',
  'unavailable',
  'failed',
];

/** Where the browser goes. One function, so a spec replaces the page unload with a recorder. */
export type LocationNavigate = (url: string) => void;

/**
 * The navigation seam. The default implementation is the only place in this feature that calls
 * `location.assign`: the flow builds the URL, and where it is handed over is an injection decision.
 */
export const LOCATION_NAVIGATE = new InjectionToken<LocationNavigate>(
  'evidgate.auth.location-navigate',
  {
    providedIn: 'root',
    factory: (): LocationNavigate => {
      const document = inject(DOCUMENT);
      return (url: string): void => {
        document.defaultView?.location.assign(url);
      };
    },
  },
);

/** One value of `?reason=`, or `none` for a missing or unknown one. */
function parseReason(raw: string | null): LoginReason {
  return LOGIN_REASONS.find((reason) => reason === raw) ?? 'none';
}

/**
 * The sign-in flow the login page drives (the page injects this and nothing else). `returnUrl` and
 * `reason` are read ONCE from the URL the page was activated with: the login route is entered by a
 * guard redirect, so the query is that redirect's, and a later internal navigation that keeps the
 * page mounted must not silently change what `start()` will return to.
 */
@Injectable({ providedIn: 'root' })
export class AuthFlow {
  private readonly route = inject(ActivatedRoute);
  private readonly location = inject(Location);
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  private readonly navigate = inject(LOCATION_NAVIGATE);
  private readonly query = this.route.snapshot.queryParamMap;
  private readonly redirectingState = signal(false);

  /** The validated target of a completed sign-in; `/` when the parameter is missing or refused. */
  readonly returnUrl: Signal<string> = signal(validateReturnUrl(this.query.get('returnUrl')));

  /**
   * The notice the page shows. The URL wins (the store and the callback page put the reason there),
   * and the store's own latch is the fallback for a visit to `/login` that carries no parameter —
   * a signed-out tab stays `signed-out` until `start()` begins a new sign-in.
   */
  readonly reason: Signal<LoginReason> = computed(() => {
    const fromUrl = parseReason(this.query.get('reason'));
    return fromUrl === 'none' && this.session.signedOut() ? 'signed-out' : fromUrl;
  });

  /** `true` from `start()` until the browser leaves this page; the buttons stay disabled. */
  readonly redirecting: Signal<boolean> = this.redirectingState.asReadonly();

  /** The callback URL the API must redirect to, base href included (`/<locale>/auth/callback`). */
  get callbackUrl(): string {
    return this.location.prepareExternalUrl(AUTH_CALLBACK_ROUTE);
  }

  /**
   * Starts the sign-in for `provider`: the page navigates to the API's start route with the
   * callback and the validated target encoded inside it. A second call while the browser is already
   * leaving is ignored (a double click must not start two flows).
   *
   * `session.reauth()` clears the signed-out latch first, so a sign-in started from the signed-out
   * notice is a new session rather than a request issued before the sign-out.
   */
  start(provider: LoginProvider): void {
    if (this.redirectingState()) {
      return;
    }
    this.redirectingState.set(true);
    this.session.reauth();
    const callback = `${this.callbackUrl}?returnUrl=${encodeURIComponent(this.returnUrl())}`;
    this.navigate(`${OIDC_START_PATH}/${provider}/start?returnUrl=${encodeURIComponent(callback)}`);
  }
}

/**
 * `/login` for a caller who already has a session: the attempt is answered with the validated
 * `returnUrl` (or the workspace root, whose landing guard picks the server's first org and project),
 * so a signed-in visitor never sees the sign-in buttons.
 *
 * An anonymous or unreadable session is allowed through: the login page is the one screen both of
 * those states need, and a `503` on the session probe must not make sign-in unreachable.
 */
export const signedInRedirectGuard: CanActivateFn = async (route) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.bootstrap();
  if (session.sessionState() !== 'authenticated') {
    return true;
  }
  return router.parseUrl(validateReturnUrl(route.queryParamMap.get('returnUrl')));
};
