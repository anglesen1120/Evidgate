// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; D-S68-Q2; converted to `@ngrx/signals` by 09.2.1-T1):
// the shell's session and scope state as a `signalStore`. The public members are the state
// signals — read-only, one per field (00 §8.9) — plus the methods; `patchState` is called only
// inside this file's own `withMethods` factory, so no writable state source escapes the store.
// The injected class is the value `SessionStore`; the type `SessionStore` is its instance, which
// is what a guard or a spec annotates a parameter with.
//
// Two facts drive every method:
//
// 1. The URL is a request, never evidence. `resolveScope` answers only from lists the server
//    returned: an org must be one of `GET /v1/orgs`, a project must come from
//    `GET /v1/orgs/{o}/projects` *and* carry the same `org_id`. A foreign id is `not-found`, which
//    the scope guards turn into a non-match so the 404 pattern renders — never a 403, which would
//    confirm the id exists in another tenant (00 §9.2, D-S68-Q4).
// 2. The permission set is a convenience (D-S68-Q1): it holds only server-provided codes and an
//    empty set grants nothing. The API's own `403` stays the only real gate.
//
// 09.1.1-T5.2 adds the two session BOUNDARIES, because they are decisions about the same state:
//
// - `logout()` is client-side only (D-S98-Q1). There is no logout endpoint until 02.3.2-T2, so the
//   store ends every piece of per-session work, sets the in-memory `signedOut` latch and leaves
//   through `SessionEnder` (a no-op today). The `HttpOnly` cookie stays valid: a reload
//   re-authenticates, which `SessionEnder` documents and the login page's copy respects.
// - `handleUnauthenticated()` answers a `401` that arrives AFTER bootstrap — the session expired
//   mid-navigation. It flips to `anonymous` exactly once and sends the caller to
//   `/login?reason=expired&returnUrl=<the route they were reading>`, so the sign-in returns there.
//   Every later `401` while already anonymous (or latched) is a no-op: one expiry produces one
//   redirect, never a loop.
//
// Both boundaries bump the two revisions, so an answer that belongs to the ended session can never
// write state, and run every registered `onSessionEnd` teardown (the onboarding step's health timer),
// so no session-scoped work outlives the session.

import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { validateReturnUrl } from '../routing/return-url';
import { IdentityDataService } from '../data/identity-data.service';
import { isApiRequestError } from '../http/api-error';
import { UnauthenticatedRelay } from '../http/unauthenticated-relay';
import {
  CataloguePermissionsSource,
  EffectivePermissionsSource,
} from './effective-permissions.source';
import { SESSION_ENDER } from './session-ender';
import { loadPermissionsForTenant } from './tenant-permissions';
import { TenantStore } from './tenant.store';

/** What the shell knows about the caller's session (00 §8.9). */
export type SessionState =
  /** Nothing has been read yet; `bootstrap()` settles it. */
  | 'unknown'
  /** The API answered with the caller's organizations and the permission source resolved. */
  | 'authenticated'
  /** The API refused with `401`: there is no usable session (00 §9.2). */
  | 'anonymous'
  /** The session could not be read: a transport failure, a 5xx, a rate limit, an unknown error. */
  | 'unavailable';

/** The outcome of resolving an org/project pair from the URL. */
export type ScopeResolution =
  /** The pair is one the server listed; `currentOrg`/`currentProject` now describe it. */
  | 'ok'
  /** Unknown, foreign, or not listed for this caller — deliberately one answer (00 §9.2). */
  | 'not-found'
  /**
   * A newer `resolveScope` (or a `reload()`) took the scope while this one awaited. The answer
   * describes a request nobody is waiting for any more, so it is neither a match nor a claim that
   * the id is unknown: the guard that receives it cancels instead of rendering the 404 pattern.
   */
  | 'superseded'
  /** The check itself could not be completed (transport, `5xx`, rate limit, programming error). */
  | 'unavailable';

/** The frozen empty set: `has()` on it answers `false` for every code, which is the safe default. */
const NO_PERMISSIONS: ReadonlySet<string> = new Set<string>();
const NO_ORGANIZATIONS: readonly OrganizationSummaryDto[] = [];
const NO_PROJECTS: readonly ProjectDto[] = [];

/**
 * The store's state. Every field is published as one read-only signal of the same name, which is
 * exactly the surface the guards, the sidebar and the pages already read: the conversion from the
 * hand-rolled signals class changed no name.
 */
type SessionStoreState = {
  /** `unknown` until the first `bootstrap()` settles it. */
  readonly sessionState: SessionState;
  /**
   * The in-memory sign-out latch (09.1.1-T5.2): `true` from `logout()` (or an expiry redirect) until
   * `reauth()`. While it is set, `bootstrap()` and `reload()` answer `anonymous` WITHOUT a request,
   * so a late probe can never resurrect the session the caller just ended. It is deliberately not
   * persisted: a reload re-authenticates from the still-valid `HttpOnly` cookie (`session-ender.ts`).
   */
  readonly signedOut: boolean;
  /** The codes the shell treats as granted; empty until the session is authenticated. */
  readonly permissions: ReadonlySet<string>;
  /** Every organization the server listed for this caller, in server order. */
  readonly organizations: readonly OrganizationSummaryDto[];
  /** The projects of {@link currentOrg}; empty when no org is resolved. */
  readonly projects: readonly ProjectDto[];
  /** The org the route resolved to, or `null`. */
  readonly currentOrg: OrganizationSummaryDto | null;
  /** The project the route resolved to, or `null` for an org-scope route. */
  readonly currentProject: ProjectDto | null;
};

const INITIAL_STATE: SessionStoreState = {
  sessionState: 'unknown',
  signedOut: false,
  permissions: NO_PERMISSIONS,
  organizations: NO_ORGANIZATIONS,
  projects: NO_PROJECTS,
  currentOrg: null,
  currentProject: null,
};

/** Where the session boundaries send the browser: the app's sign-in page. */
const LOGIN_PATH = '/login';

/** One side of the concurrent session read: its value, or the failure it answered with. */
type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/**
 * Settles one independent session read. `readSession` runs the organization list and the
 * permission source side by side, and each has to fail on its own so the other one's `401` can
 * still be seen: `Promise.all` would surface only the first rejection.
 */
async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}

/**
 * A failed scope lookup. A refusal the server expressed about the resource (`404`, and a `403` for
 * a caller who may not read this org's projects) is indistinguishable from an id that does not
 * exist — 00 §9.2 wants exactly that — so both answer `not-found` and the wildcard route renders
 * the same 404 pattern. Everything else means the check could not be completed: `unavailable`.
 */
function scopeFailure(error: unknown): ScopeResolution {
  if (
    isApiRequestError(error) &&
    (error.error.kind === 'not_found' || error.error.kind === 'forbidden')
  ) {
    return 'not-found';
  }
  return 'unavailable';
}

/** The route a scope read is serving, named for the expiry redirect when the session is gone. */
function scopePath(orgId: string, projectId: string | null): string {
  const org = `/orgs/${encodeURIComponent(orgId)}`;
  return projectId === null ? org : `${org}/projects/${encodeURIComponent(projectId)}`;
}

/**
 * The store definition. The non-signal bookkeeping a `signalStore` cannot hold as state — the
 * single in-flight bootstrap, the two revision counters, the project-list cache — is per-instance
 * closure state of the `withMethods` factory, which runs once per store instance.
 */
const SessionStoreDefinition = signalStore(
  { providedIn: 'root' },
  withState<SessionStoreState>(INITIAL_STATE),
  withMethods((store) => {
    const identity = inject(IdentityDataService);

    /**
     * The effective-grant seam (D-S68-Q1). The token is optional so the app boots with no binding:
     * until 02.3.1 provides the caller's real grants, the catalogue default answers. A replacement
     * registered anywhere in the injector wins, and this store stays unchanged.
     */
    const permissionsSource =
      inject(EffectivePermissionsSource, { optional: true }) ?? inject(CataloguePermissionsSource);

    /** The tenant the requests are scoped by; this store is its only writer. */
    const tenant = inject(TenantStore);

    /** The sign-in page the two session boundaries leave through (a redirect, never a reload). */
    const router = inject(Router);
    /** The one seam that would end the server session; a no-op until 02.3.2-T2 (`session-ender.ts`). */
    const sessionEnder = inject(SESSION_ENDER);
    /** The hook `ApiGateway` reports a later `401` through (T5.2), registered below. */
    const unauthenticatedRelay = inject(UnauthenticatedRelay);

    /**
     * The project list already read for the org it belongs to, published only for `org_id`s the
     * server attributed to that org. `resolveScope` answers from here when the route asks for the
     * org that is already current, so a navigation inside an org reads nothing and blanks nothing.
     * A real org change drops it before reading the new org, and `clearScope` drops it on every
     * failure, every `not-found` and `reload()`, so it never outlives the session it was read
     * under.
     */
    const projectsByOrg = new Map<string, readonly ProjectDto[]>();

    /** The single in-flight bootstrap, so a burst of guards awaits one read instead of N. */
    let inFlight: Promise<void> | null = null;
    /** Bumped by `reload()`; a bootstrap of an older generation never writes state. */
    let sessionRevision = 0;
    /** Bumped by every `resolveScope` call and by `reload()`; guards the async part of a scope. */
    let scopeRevision = 0;
    /**
     * The org whose grants `permissions` holds. The tenant can be published before its read
     * returns, so the tenant alone is not proof: a switch whose load was superseded must read again
     * instead of keeping the previous org's codes.
     */
    let permissionsOrgId: string | null = null;

    /**
     * The route an in-flight scope read is resolving, so a `401` from that read can name it as the
     * `returnUrl` of the expiry redirect. Cleared as soon as the read settles.
     */
    let pendingAttemptedPath: string | null = null;

    /** What every session-scoped timer registers here; `logout()` and an expiry run all of them. */
    const endCallbacks = new Set<() => void>();

    /** The login redirect's query: `reason` and, for an expiry, the validated `returnUrl`. */
    function navigateToLogin(queryParams: Record<string, string>): void {
      // A cancelled navigation (a guard redirecting again) is not an error worth reporting.
      void router.navigate([LOGIN_PATH], { queryParams }).catch(() => undefined);
    }

    /**
     * Ends every piece of per-session work and publishes the anonymous, latched state.
     *
     * Both revisions move, so a read or a scope resolution that was already awaiting settles against
     * a generation nobody waits for and writes nothing; the cached project lists go with it, so no
     * list read under the old session can be published after the end. The end callbacks run LAST,
     * after the state is gone: a callback that reads the store sees the ended session, not a half
     * one. One callback throwing cannot keep the next one's timer alive.
     */
    function endSession(): void {
      sessionRevision += 1;
      scopeRevision += 1;
      inFlight = null;
      pendingAttemptedPath = null;
      projectsByOrg.clear();
      permissionsOrgId = null;
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
      for (const callback of [...endCallbacks]) {
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
     * answers `401` names `/orgs/<id>`, not the `/orgs/<id>/projects/<id>/<entry>` the caller asked
     * for. The navigation the router is processing carries the whole attempted tree, so this is the
     * one place the full target is available while a guard is running. It is `null` when no
     * navigation is in progress (a read from a page that is already on screen), and then the
     * caller's own path — or the current URL — describes the attempt.
     */
    function attemptedNavigationPath(): string | null {
      const navigation = router.getCurrentNavigation();
      if (navigation === null) {
        return null;
      }
      const serialized = router.serializeUrl(navigation.extractedUrl);
      return serialized === '' ? null : serialized;
    }

    /**
     * A `401` that arrived after the session was established (T5.2): the session expired while the
     * caller was reading something.
     *
     * Exactly one redirect per expiry: a session that is not `authenticated` any more — already
     * flipped, latched signed out, or still being bootstrapped (that read answers the state itself)
     * is a no-op, so a burst of parallel 401s (a page reading three lists) produces one navigation.
     *
     * `attemptedUrl` is the route the failed read was serving: the store's own scope read names it,
     * and any other data service's `401` falls back to the current router URL.
     */
    function handleUnauthenticated(attemptedUrl: string | null): void {
      if (store.signedOut() || store.sessionState() !== 'authenticated') {
        return;
      }
      const target = validateReturnUrl(
        attemptedNavigationPath() ?? attemptedUrl ?? pendingAttemptedPath ?? router.url,
      );
      endSession();
      navigateToLogin({ reason: 'expired', returnUrl: target });
    }

    // The gateway reports every `401` it classifies as `unauthenticated`; one store, one handler.
    unauthenticatedRelay.register((attemptedUrl) => handleUnauthenticated(attemptedUrl));

    /** Forgets everything a `reload()` must not keep. */
    function clearSession(): void {
      patchState(store, { permissions: NO_PERMISSIONS, organizations: NO_ORGANIZATIONS });
      clearScope();
    }

    /** Forgets the resolved pair, its project list, and every cached project list. */
    function clearScope(): void {
      scopeRevision += 1;
      permissionsOrgId = null;
      tenant.clear();
      patchState(store, { currentOrg: null, currentProject: null, projects: NO_PROJECTS });
      projectsByOrg.clear();
    }

    /**
     * One round trip of organizations and permissions; a stale generation writes nothing.
     *
     * The organization list is read FIRST: the permission read is tenant-scoped, so the header the
     * interceptor writes for it must name this session's tenant. An empty (or failed) list leaves no
     * tenant to scope by — the interceptor adds nothing and the read answers the pre-tenant route.
     *
     * A `401` from EITHER read is the one refusal with a UI state of its own (`anonymous`) and wins
     * over the other read's failure — an expired session is an answer, not an outage. Everything
     * else — a `5xx`, a dropped connection, a broken cursor loop, a missing provider — means
     * "could not read", which the shell renders as unavailable rather than as an empty, silently
     * degraded workspace. Any failure also drops the cached project lists, so a later resolve
     * cannot publish a list read under a session that is no longer confirmed.
     */
    async function readSession(revision: number): Promise<void> {
      const organizations = await settledRead(() => identity.listMyOrganizations());
      // A newer generation (a `reload()`, a logout, an expiry) owns the state by now; a latched
      // sign-out must never be undone by a read that was already on the wire.
      if (revision !== sessionRevision || store.signedOut()) {
        return;
      }
      // A 200 whose body is no org list (an HTML page from a proxy) has no `items`; it counts as
      // "no org" for the permission read instead of crashing the bootstrap.
      const listed: readonly OrganizationSummaryDto[] | undefined = organizations.ok
        ? organizations.value
        : undefined;
      const firstOrgId = listed?.[0]?.id ?? null;
      const permissions = await settledRead(() =>
        loadPermissionsForTenant(tenant, permissionsSource, firstOrgId),
      );
      if (revision !== sessionRevision || store.signedOut()) {
        return;
      }
      if (!organizations.ok || !permissions.ok) {
        const failures = [organizations, permissions].flatMap((read) =>
          read.ok ? [] : [read.error],
        );
        // The tenant that scoped the refused read dies with the session: a later `/login` or
        // sign-in must not inherit it as the header of its first request.
        clearScope();
        const sessionGone = failures.some(
          (error) => isApiRequestError(error) && error.error.kind === 'unauthenticated',
        );
        patchState(store, { sessionState: sessionGone ? 'anonymous' : 'unavailable' });
        return;
      }
      patchState(store, {
        organizations: organizations.value,
        permissions: permissions.value,
        sessionState: 'authenticated',
      });
      permissionsOrgId = firstOrgId;
    }

    /**
     * Reads the session once (00 §8.9). Idempotent: a settled state is returned as-is and
     * concurrent callers share one in-flight read, so every guard may await it unconditionally.
     *
     * The organization list is read first, then permissions are read under the first org's
     * tenant header. A `401` from either read means the caller has no session; `readSession`
     * maps that refusal to an anonymous outcome.
     */
    function bootstrap(): Promise<void> {
      // The caller ended this session on this tab: no request may re-open it (T5.2). `reauth()`
      // clears the latch when a new sign-in begins.
      if (store.signedOut()) {
        return Promise.resolve();
      }
      if (inFlight !== null) {
        return inFlight;
      }
      if (store.sessionState() !== 'unknown') {
        return Promise.resolve();
      }
      const run: Promise<void> = readSession(sessionRevision).finally(() => {
        if (inFlight === run) {
          inFlight = null;
        }
      });
      inFlight = run;
      return run;
    }

    /**
     * Forgets the session and reads it again, for the service-unavailable page's retry and for a
     * later sign-in. In-flight reads of the previous generation are ignored when they settle.
     *
     * Unlike `bootstrap()`, this rejects when the retry ends unreadable: a guard must never throw,
     * but the retry button has to know whether it worked (`ServiceUnavailablePage` shows its
     * failure copy from this rejection; without it a failed retry would navigate and bounce
     * straight back). An `anonymous` outcome resolves — "no session" is an answer, not a failed
     * read.
     */
    async function reload(): Promise<void> {
      if (store.signedOut()) {
        // Latched (T5.2): staying anonymous without a request is the answer, exactly as `bootstrap`.
        patchState(store, { sessionState: 'anonymous' });
        clearSession();
        return;
      }
      sessionRevision += 1;
      inFlight = null;
      patchState(store, { sessionState: 'unknown' });
      clearSession();
      await bootstrap();
      if (store.sessionState() === 'unavailable') {
        throw new Error('the workspace is still unavailable');
      }
    }

    /**
     * The answer to a failed scope read: a `401` ends the session and cancels the guard chain, any
     * other failure clears the scope and is `unavailable`, and an answer a newer navigation
     * superseded changes nothing.
     */
    function failScopeRead(error: unknown, revision: number): ScopeResolution {
      const unauthenticated = isApiRequestError(error) && error.error.kind === 'unauthenticated';
      if (revision !== scopeRevision) {
        // A newer navigation owns the pending route: erasing it here would send its expiry
        // redirect to the wrong page (or none).
        return unauthenticated ? 'superseded' : scopeFailure(error);
      }
      const attempted = pendingAttemptedPath;
      pendingAttemptedPath = null;
      if (unauthenticated) {
        handleUnauthenticated(attempted);
        return 'superseded';
      }
      clearScope();
      return scopeFailure(error);
    }

    /**
     * A failed grant reload. It answers nothing about the org's existence, so a refusal is
     * `unavailable` (never the `not-found` a project lookup may share); the session's own `401` is
     * the expiry path, and a failure a newer navigation overtook is `superseded` without touching
     * the state that navigation now owns.
     */
    function failPermissionReload(error: unknown, revision: number): ScopeResolution {
      if (revision !== scopeRevision) {
        return 'superseded';
      }
      const attempted = pendingAttemptedPath;
      pendingAttemptedPath = null;
      if (isApiRequestError(error) && error.error.kind === 'unauthenticated') {
        handleUnauthenticated(attempted);
        return 'superseded';
      }
      clearScope();
      return 'unavailable';
    }

    /**
     * Resolves the URL's org/project pair against what the server listed and makes it the current
     * scope. Never trusts an id: a foreign or unknown org, a project of another org, or a project
     * the server did not list is `not-found`, and the scope is cleared so nothing stale stays
     * rendered.
     *
     * A route chain asks more than once per navigation (`orgScopeGuard`, then `projectScopeGuard`,
     * then the landing guard), and every sidebar click is another navigation inside the org
     * already resolved. So the project list read for an org is cached: when the target org is the
     * current one and its list is already loaded, neither the signals nor the network are touched,
     * and the project is answered from the cache. That keeps `currentOrg()`/`projects()` populated
     * for the whole navigation — the sidebar and the project switcher never blink empty — and
     * keeps one project-list read per navigation instead of one per guard. A real org change, a
     * failure and `reload()` all drop the cache and read again.
     *
     * A resolution that a newer one (or a `reload()`) overtook while it awaited answers
     * `superseded`, not `not-found`: the id was never shown to be unknown, and the guard that
     * receives it must cancel rather than render the 404 pattern. A resolve that changes the org
     * also republishes the request tenant and reloads the permission set it scopes; a failure
     * there is `unavailable`, and a `401` is the expiry path below.
     *
     * A `401` from the project-list read is the session expiring mid-navigation (T5.2) and is the
     * one failure with an answer of its own: `handleUnauthenticated` — which the gateway's relay may
     * already have run for this same refusal — flips the session to `anonymous` once and starts the
     * `/login?reason=expired&returnUrl=…` redirect, so this call answers `superseded` and the guard
     * chain cancels: the store owns that navigation now.
     */
    async function resolveScope(orgId: string, projectId: string | null): Promise<ScopeResolution> {
      await bootstrap();
      if (store.sessionState() === 'unavailable') {
        return 'unavailable';
      }
      const revision = ++scopeRevision;
      if (store.sessionState() !== 'authenticated') {
        // No session, no scope; `not-found` never confirms that the id exists anywhere.
        clearScope();
        return 'not-found';
      }

      const org = store.organizations().find((candidate) => candidate.id === orgId);
      if (org === undefined) {
        clearScope();
        return 'not-found';
      }

      // The tenant follows the org the route resolved to, and it does so before anything
      // tenant-scoped is read: the permission set is scoped by it too, so a switch reloads it. The
      // tenant alone does not say the grants arrived — a load that a newer resolve overtook never
      // patched them — so the org the published grants belong to is the second half of the test.
      if (tenant.orgId() !== orgId || permissionsOrgId !== orgId) {
        pendingAttemptedPath = scopePath(orgId, projectId);
        try {
          const granted = await loadPermissionsForTenant(tenant, permissionsSource, orgId);
          if (revision !== scopeRevision) {
            return 'superseded';
          }
          patchState(store, { permissions: granted });
          permissionsOrgId = orgId;
        } catch (error) {
          return failPermissionReload(error, revision);
        }
        pendingAttemptedPath = null;
      }

      // The org is already current and its list is loaded: answer from the cache. `undefined`
      // means this org's list was never read, or a failure dropped it.
      const cached = store.currentOrg()?.id === orgId ? projectsByOrg.get(orgId) : undefined;
      if (cached !== undefined) {
        const project =
          projectId === null
            ? null
            : cached.find((candidate) => candidate.id === projectId && candidate.org_id === orgId);
        if (projectId !== null && project === undefined) {
          clearScope();
          return 'not-found';
        }
        patchState(store, { currentProject: project ?? null });
        return 'ok';
      }

      // A real org change: the previous org's list is not this one's, and refilling it is one
      // read.
      if (store.currentOrg()?.id !== orgId) {
        projectsByOrg.clear();
      }

      // The first resolve of this org, a real org change, or a retry after a failure. The previous
      // scope is dropped before the await: a slow project list must not keep the old org's
      // projects (and therefore the old org's navigation links) on screen.
      patchState(store, { currentOrg: org, currentProject: null, projects: NO_PROJECTS });

      let projects: readonly ProjectDto[];
      // The route this read is serving, named for the expiry redirect if the session is gone (T5.2).
      pendingAttemptedPath = scopePath(orgId, projectId);
      try {
        projects = await identity.listProjects(orgId);
      } catch (error) {
        return failScopeRead(error, revision);
      }
      if (revision !== scopeRevision) {
        // A newer navigation owns the scope (and the pending route) by now; this answer is about a
        // request nobody waits for.
        return 'superseded';
      }
      pendingAttemptedPath = null;

      // Only the rows the server attributed to THIS org. The endpoint is asked per org, but the
      // published list is what the switcher offers and what a landing address is built from, so a
      // row carrying another `org_id` must never reach it — the explicit-id branch above refuses
      // the same row, and one answer must not contradict the other.
      const owned = projects.filter((candidate) => candidate.org_id === orgId);
      projectsByOrg.set(orgId, owned);
      patchState(store, { projects: owned });

      if (projectId === null) {
        return 'ok';
      }
      const project = owned.find((candidate) => candidate.id === projectId);
      if (project === undefined) {
        clearScope();
        return 'not-found';
      }
      patchState(store, { currentProject: project });
      return 'ok';
    }

    /** `true` only for a code the source put in the set — an empty set grants nothing. */
    function has(code: string): boolean {
      return store.permissions().has(code);
    }

    /**
     * Registers session-scoped work to stop when the session ends (T5.2): the onboarding step's
     * health timer, and anything later that holds a timer, an interval or a subscription that must
     * not outlive the caller's session. The returned function unregisters it (a destroyed component).
     */
    function onSessionEnd(callback: () => void): () => void {
      endCallbacks.add(callback);
      return () => {
        endCallbacks.delete(callback);
      };
    }

    /**
     * Signs out on this tab (D-S98-Q1): every signal back to anonymous, every in-flight read ignored
     * by its generation, every registered teardown run, then `/login?reason=signed-out`.
     *
     * The server is asked through `SessionEnder` and its answer never decides the local outcome: until
     * 02.3.2-T2 the seam is a no-op, and a failing call must not keep the caller signed in. No request
     * is made by the store itself, so a spec can assert that a sign-out issues none.
     *
     * Calling it twice is harmless: the second call leaves through the same redirect.
     */
    async function logout(): Promise<void> {
      if (!store.signedOut()) {
        endSession();
      }
      try {
        await sessionEnder.end();
      } catch {
        // Best effort: the tab is signed out either way (`session-ender.ts`).
      }
      navigateToLogin({ reason: 'signed-out' });
    }

    /**
     * Clears the sign-out latch when a new sign-in begins (the login page's `AuthFlow.start`). The
     * state stays `anonymous` on purpose: the browser is about to leave for the provider, and a
     * probe here would either re-open the old session or race the callback's own `reload()`.
     */
    function reauth(): void {
      if (store.signedOut()) {
        patchState(store, { signedOut: false });
      }
    }

    /**
     * Adopts a project the caller just created, so the scope the wizard redirects to is already
     * known without a second read (09.2.1 T4 delta N2).
     *
     * The server list stays the evidence. The project is appended only when its org is the current
     * one AND that org's list was actually read; an id the list already holds is not appended
     * twice, so the switcher never shows one project twice. In every other case the org's cache
     * entry is dropped, and the next `resolveScope` reads the server instead of answering
     * `not-found` from a list that could not have contained this project.
     */
    function adoptCreatedProject(project: ProjectDto): void {
      const orgId = project.org_id;
      const cached = store.currentOrg()?.id === orgId ? projectsByOrg.get(orgId) : undefined;
      if (cached === undefined) {
        // Not the current org, or its list was never read: nothing may be published from here.
        projectsByOrg.delete(orgId);
        return;
      }
      if (cached.some((candidate) => candidate.id === project.id)) {
        // Already listed: a second copy would make the id ambiguous for the guards and the
        // switcher.
        return;
      }
      const next: readonly ProjectDto[] = [...cached, project];
      projectsByOrg.set(orgId, next);
      patchState(store, { projects: next });
    }

    return {
      bootstrap,
      reload,
      resolveScope,
      has,
      adoptCreatedProject,
      logout,
      reauth,
      handleUnauthenticated,
      onSessionEnd,
    };
  }),
);

/**
 * The session store token and class. `@ngrx/signals` builds it, so injecting the value is the only
 * way to obtain the singleton (`providedIn: 'root'`, the shell's one session read per session).
 */
export const SessionStore = SessionStoreDefinition;

/**
 * The store instance: the exported `SessionStore` value is the injectable class, so a guard or a
 * spec that annotates a parameter or a field writes this type. Its members are exactly the state
 * signals plus the methods above.
 */
export type SessionStore = InstanceType<typeof SessionStoreDefinition>;
