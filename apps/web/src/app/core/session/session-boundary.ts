/**
 * The two session BOUNDARIES are decisions about the same state, so they live together: ending a
 * session (sign-out, expiry) and the latch a new sign-in clears. Both bump the two revisions, so an
 * answer that belongs to the ended session can never write state, and both run every registered
 * teardown, so no session-scoped work outlives the session.
 */

import type { Router } from '@angular/router';
import { patchState } from '@ngrx/signals';

import type { UnauthenticatedRelay } from '../http/unauthenticated-relay';
import { validateReturnUrl } from '../routing/return-url';

import type { SessionContext } from './session-context';
import type { SessionEnder } from './session-ender';
import {
  NO_ORGANIZATIONS,
  NO_PERMISSIONS,
  NO_PROJECTS,
  type SessionStoreHandle,
} from './session.types';
import type { TenantStore } from './tenant.store';

/** Where the session boundaries send the browser: the app's sign-in page. */
const LOGIN_PATH = '/login';

export type SessionBoundaryDeps = {
  readonly store: SessionStoreHandle;
  readonly context: SessionContext;
  readonly tenant: TenantStore;
  readonly router: Router;
  readonly sessionEnder: SessionEnder;
  readonly relay: UnauthenticatedRelay;
};

export type SessionBoundary = {
  /** Answers a `401` that arrived after the session was established. */
  readonly handleUnauthenticated: (attemptedUrl: string | null) => void;
  /** Signs out on this tab and leaves for the sign-in page. */
  readonly logout: () => Promise<void>;
  /** Clears the sign-out latch when a new sign-in begins. */
  readonly reauth: () => void;
  /** Registers session-scoped work to stop when the session ends. */
  readonly onSessionEnd: (callback: () => void) => () => void;
};

/** The login redirect's query: `reason` and, for an expiry, the validated `returnUrl`. */
function navigateToLogin(deps: SessionBoundaryDeps, queryParams: Record<string, string>): void {
  // A cancelled navigation (a guard redirecting again) is not an error worth reporting.
  void deps.router.navigate([LOGIN_PATH], { queryParams }).catch(() => undefined);
}

/**
 * Ends every piece of per-session work and publishes the anonymous, latched state.
 *
 * Both revisions move, so a read or a scope resolution that was already awaiting settles against a
 * generation nobody waits for and writes nothing; the cached project lists go with it, so no list
 * read under the old session can be published after the end. The end callbacks run LAST, after the
 * state is gone: a callback that reads the store sees the ended session, not a half one. One
 * callback throwing cannot keep the next one's timer alive.
 */
function endSession(deps: SessionBoundaryDeps): void {
  const { store, context, tenant } = deps;
  context.sessionRevision += 1;
  context.scopeRevision += 1;
  context.inFlight = null;
  context.pendingAttemptedPath = null;
  context.projectsByOrg.clear();
  context.permissionsOrgId = null;
  tenant.clear();
  patchState(store, {
    sessionState: 'anonymous',
    signedOut: true,
    permissions: NO_PERMISSIONS,
    organizations: NO_ORGANIZATIONS,
    projects: NO_PROJECTS,
    currentOrg: null,
    currentProject: null,
  });
  for (const callback of [...context.endCallbacks]) {
    try {
      callback();
    } catch {
      // A teardown is best effort: it must never stop another one from running.
    }
  }
}

/**
 * The URL the caller is trying to reach RIGHT NOW, if a navigation is in flight.
 *
 * A guard that resolves a scope only knows the segments matched so far — an org-level read that
 * answers `401` names `/orgs/<id>`, not the `/orgs/<id>/projects/<id>/<entry>` the caller asked for.
 * The navigation the router is processing carries the whole attempted tree, so this is the one place
 * the full target is available while a guard is running. It is `null` when no navigation is in
 * progress, and then the caller's own path — or the current URL — describes the attempt.
 */
function attemptedNavigationPath(deps: SessionBoundaryDeps): string | null {
  const navigation = deps.router.getCurrentNavigation();
  if (navigation === null) {
    return null;
  }
  const serialized = deps.router.serializeUrl(navigation.extractedUrl);
  return serialized === '' ? null : serialized;
}

/**
 * A `401` that arrived after the session was established: the session expired while the caller was
 * reading something.
 *
 * Exactly one redirect per expiry: a session that is not `authenticated` any more — already flipped,
 * latched signed out, or still being bootstrapped (that read answers the state itself) — is a no-op,
 * so a burst of parallel 401s (a page reading three lists) produces one navigation.
 *
 * `attemptedUrl` is the route the failed read was serving: the store's own scope read names it, and
 * any other data service's `401` falls back to the current router URL.
 */
function handleUnauthenticated(deps: SessionBoundaryDeps, attemptedUrl: string | null): void {
  const { store, context, router } = deps;
  if (store.signedOut() || store.sessionState() !== 'authenticated') {
    return;
  }
  const target = validateReturnUrl(
    attemptedNavigationPath(deps) ?? attemptedUrl ?? context.pendingAttemptedPath ?? router.url,
  );
  endSession(deps);
  navigateToLogin(deps, { reason: 'expired', returnUrl: target });
}

/**
 * Registers session-scoped work to stop when the session ends: the onboarding step's health timer,
 * and anything later that holds a timer, an interval or a subscription that must not outlive the
 * caller's session. The returned function unregisters it (a destroyed component).
 */
function registerSessionEnd(deps: SessionBoundaryDeps, callback: () => void): () => void {
  deps.context.endCallbacks.add(callback);
  return () => {
    deps.context.endCallbacks.delete(callback);
  };
}

/**
 * Signs out on this tab: every signal back to anonymous, every in-flight read ignored by its
 * generation, every registered teardown run, then `/login?reason=signed-out`.
 *
 * The server is asked through `SessionEnder` and its answer never decides the local outcome: the seam
 * is a no-op today, and a failing call must not keep the caller signed in. No request is made by the
 * store itself, so a spec can assert that a sign-out issues none.
 *
 * Calling it twice is harmless: the second call leaves through the same redirect.
 */
async function logout(deps: SessionBoundaryDeps): Promise<void> {
  if (!deps.store.signedOut()) {
    endSession(deps);
  }
  try {
    await deps.sessionEnder.end();
  } catch {
    // Best effort: the tab is signed out either way (`session-ender.ts`).
  }
  navigateToLogin(deps, { reason: 'signed-out' });
}

/**
 * Clears the sign-out latch when a new sign-in begins (the login page's `AuthFlow.start`). The state
 * stays `anonymous` on purpose: the browser is about to leave for the provider, and a probe here
 * would either re-open the old session or race the callback's own `reload()`.
 */
function reauth(deps: SessionBoundaryDeps): void {
  if (deps.store.signedOut()) {
    patchState(deps.store, { signedOut: false });
  }
}

export function createSessionBoundary(deps: SessionBoundaryDeps): SessionBoundary {
  // The gateway reports every `401` it classifies as `unauthenticated`; one store, one handler.
  deps.relay.register((attemptedUrl) => handleUnauthenticated(deps, attemptedUrl));
  return {
    handleUnauthenticated: (attemptedUrl) => handleUnauthenticated(deps, attemptedUrl),
    logout: () => logout(deps),
    reauth: () => reauth(deps),
    onSessionEnd: (callback) => registerSessionEnd(deps, callback),
  };
}
