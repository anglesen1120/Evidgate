// The shell's session and scope state as a `signalStore`. The public members are the state signals —
// read-only, one per field — plus the methods; `patchState` is called only inside the store's own
// feature modules, so no writable state source escapes the store. The injected class is the value
// `SessionStore`; the type `SessionStore` is its instance, which is what a guard or a spec annotates
// a parameter with.
//
// Two facts drive every method:
//
// 1. The URL is a request, never evidence. `resolveScope` answers only from lists the server
//    returned: an org must be one of `GET /v1/orgs`, a project must come from
//    `GET /v1/orgs/{o}/projects` *and* carry the same `org_id`. A foreign id is `not-found`, which
//    the scope guards turn into a non-match so the 404 pattern renders — never a 403, which would
//    confirm the id exists in another tenant.
// 2. The permission set is a convenience: it holds only server-provided codes and an empty set
//    grants nothing. The API's own `403` stays the only real gate.
//
// The two session boundaries are decisions about the same state. `logout()` is client-side only:
// there is no logout endpoint yet, so the store ends every piece of per-session work, sets the
// in-memory `signedOut` latch and leaves through `SessionEnder` (a no-op today). The `HttpOnly`
// cookie stays valid, so a reload re-authenticates — which `SessionEnder` documents and the login
// page's copy respects. `handleUnauthenticated()` answers a `401` that arrives AFTER bootstrap: it
// flips to `anonymous` exactly once and sends the caller to
// `/login?reason=expired&returnUrl=<the route they were reading>`. Every later `401` while already
// anonymous (or latched) is a no-op: one expiry produces one redirect, never a loop.
//
// Both boundaries bump the two revisions, so an answer that belongs to the ended session can never
// write state, and run every registered `onSessionEnd` teardown, so no session-scoped work outlives
// the session.
//
// The behaviour is split by responsibility into sibling modules — the context, the scope state, the
// boundaries, the session reads and the scope resolution — composed here in dependency order, so no
// module imports another that imports it back.

import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { signalStore, withMethods, withState } from '@ngrx/signals';

import type { ProjectDto } from '@evidgate/sdk';

import { IdentityDataService } from '../data/identity-data.service';
import { UnauthenticatedRelay } from '../http/unauthenticated-relay';
import {
  CataloguePermissionsSource,
  EffectivePermissionsSource,
} from './effective-permissions.source';
import { createScopeResolver } from './scope-resolution';
import { createScopeState } from './scope-state';
import { createSessionBoundary } from './session-boundary';
import { createSessionContext } from './session-context';
import { SESSION_ENDER, type SessionEnder } from './session-ender';
import { createSessionRead } from './session-read';
import {
  INITIAL_STATE,
  type ScopeResolution,
  type SessionStoreHandle,
  type SessionStoreState,
} from './session.types';
import { TenantStore } from './tenant.store';

export type { ScopeResolution, SessionState } from './session.types';

/** Everything the store's feature modules need from the injector. */
type SessionDependencies = {
  readonly identity: IdentityDataService;
  /**
   * The effective-grant seam. The token is optional so the app boots with no binding: until the API
   * offers the caller's own grants, the catalogue default answers. A replacement registered anywhere
   * in the injector wins, and this store stays unchanged.
   */
  readonly permissionsSource: EffectivePermissionsSource;
  /** The tenant the requests are scoped by; this store is its only writer. */
  readonly tenant: TenantStore;
  /** The sign-in page the two session boundaries leave through (a redirect, never a reload). */
  readonly router: Router;
  /** The one seam that would end the server session; a no-op today (`session-ender.ts`). */
  readonly sessionEnder: SessionEnder;
  /** The hook `ApiGateway` reports a later `401` through; the boundary registers it once. */
  readonly unauthenticatedRelay: UnauthenticatedRelay;
};

/** The store's public method map; the read-only state signals are added by the store itself. */
type SessionMethods = {
  readonly bootstrap: () => Promise<void>;
  readonly reload: () => Promise<void>;
  readonly resolveScope: (orgId: string, projectId: string | null) => Promise<ScopeResolution>;
  readonly has: (code: string) => boolean;
  readonly adoptCreatedProject: (project: ProjectDto) => void;
  readonly logout: () => Promise<void>;
  readonly reauth: () => void;
  readonly handleUnauthenticated: (attemptedUrl: string | null) => void;
  readonly onSessionEnd: (callback: () => void) => () => void;
};

/**
 * Resolves the store's collaborators in the store's own injection context, so the modules below take
 * them as plain parameters and stay constructible in a spec.
 */
function injectSessionDependencies(): SessionDependencies {
  return {
    identity: inject(IdentityDataService),
    permissionsSource:
      inject(EffectivePermissionsSource, { optional: true }) ?? inject(CataloguePermissionsSource),
    tenant: inject(TenantStore),
    router: inject(Router),
    sessionEnder: inject(SESSION_ENDER),
    unauthenticatedRelay: inject(UnauthenticatedRelay),
  };
}

/**
 * Composes the feature modules in dependency order and returns the store's public methods. The
 * non-signal bookkeeping a `signalStore` cannot hold as state — the single in-flight bootstrap, the
 * two revision counters, the project-list cache — is the per-instance context created here.
 */
function composeSessionStore(
  store: SessionStoreHandle,
  dependencies: SessionDependencies,
): SessionMethods {
  const { identity, tenant, permissionsSource, router, sessionEnder } = dependencies;
  const context = createSessionContext();
  const scopeState = createScopeState(store, context, tenant);
  const boundary = createSessionBoundary({
    store,
    context,
    tenant,
    router,
    sessionEnder,
    relay: dependencies.unauthenticatedRelay,
  });
  const sessionRead = createSessionRead({
    store,
    context,
    identity,
    tenant,
    permissionsSource,
    clearScope: scopeState.clearScope,
    clearSession: scopeState.clearSession,
  });
  const scopeResolver = createScopeResolver({
    store,
    context,
    tenant,
    identity,
    permissionsSource,
    bootstrap: sessionRead.bootstrap,
    clearScope: scopeState.clearScope,
    dropScope: scopeState.dropScope,
    handleUnauthenticated: boundary.handleUnauthenticated,
  });

  return {
    bootstrap: sessionRead.bootstrap,
    reload: sessionRead.reload,
    resolveScope: scopeResolver.resolveScope,
    has: scopeState.has,
    adoptCreatedProject: scopeState.adoptCreatedProject,
    logout: boundary.logout,
    reauth: boundary.reauth,
    handleUnauthenticated: boundary.handleUnauthenticated,
    onSessionEnd: boundary.onSessionEnd,
  };
}

const SessionStoreDefinition = signalStore(
  { providedIn: 'root' },
  withState<SessionStoreState>(INITIAL_STATE),
  withMethods((store) => composeSessionStore(store, injectSessionDependencies())),
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
