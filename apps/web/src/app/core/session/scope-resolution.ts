/**
 * Resolving the URL's org/project pair into the current scope. The pair is answered only from what
 * the server listed, the project list is cached per org so an in-org navigation reads nothing, and a
 * resolution a newer one overtook answers `superseded` instead of claiming the id is unknown.
 *
 * Every await in `resolveScope` is one the store has always had — the session bootstrap, the grant
 * reload on an org switch and the project-list read. Nothing else may add one: a continuation that
 * runs after a queued session end could republish state the end has already cleared.
 */

import { patchState } from '@ngrx/signals';

import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import type { IdentityDataService } from '../data/identity-data.service';
import { isApiRequestError } from '../http/api-error';

import type { EffectivePermissionsSource } from './effective-permissions.source';
import type { SessionContext } from './session-context';
import { scopeFailure, scopePath } from './session-read.helpers';
import type { ScopeResolution, SessionStoreHandle } from './session.types';
import { loadPermissionsForTenant } from './tenant-permissions';
import type { TenantStore } from './tenant.store';

export type ScopeResolverDeps = {
  readonly store: SessionStoreHandle;
  readonly context: SessionContext;
  readonly tenant: TenantStore;
  readonly identity: IdentityDataService;
  readonly permissionsSource: EffectivePermissionsSource;
  readonly bootstrap: () => Promise<void>;
  readonly clearScope: () => void;
  readonly dropScope: (org: OrganizationSummaryDto) => void;
  readonly handleUnauthenticated: (attemptedUrl: string | null) => void;
};

export type ScopeResolver = {
  /** Makes the URL's pair the current scope, or answers why it is not one. */
  readonly resolveScope: (orgId: string, projectId: string | null) => Promise<ScopeResolution>;
};

/**
 * Resolves the URL's org/project pair against what the server listed and makes it the current scope.
 * Never trusts an id: a foreign or unknown org, a project of another org, or a project the server did
 * not list is `not-found`, and the scope is cleared so nothing stale stays rendered.
 *
 * A route chain asks more than once per navigation and every sidebar click is another navigation
 * inside the org already resolved, so a list already read for the current org is answered from the
 * cache: neither the signals nor the network are touched and the sidebar never blinks empty. A real
 * org change, a failure and `reload()` drop the cache and read again.
 *
 * A `401` from the project-list read is the session expiring mid-navigation: it answers
 * `superseded`, because the store's expiry path owns that navigation now.
 */
async function resolveScope(
  deps: ScopeResolverDeps,
  orgId: string,
  projectId: string | null,
): Promise<ScopeResolution> {
  const { store, context, tenant, permissionsSource, clearScope, dropScope } = deps;
  await deps.bootstrap();
  if (store.sessionState() === 'unavailable') {
    return 'unavailable';
  }
  const revision = ++context.scopeRevision;
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

  // The tenant follows the org the route resolved to, before anything tenant-scoped is read, so a
  // switch reloads the grants. The tenant alone does not say the grants arrived — a load a newer
  // resolve overtook never patched them — so the org the published grants belong to is the second
  // half of the test. The replaced scope is dropped before the read: the previous org's projects,
  // navigation links and switcher value must not survive the switch.
  if (tenant.orgId() !== orgId || context.permissionsOrgId !== orgId) {
    dropScope(org);
    context.pendingAttemptedPath = scopePath(orgId, projectId);
    try {
      const granted = await loadPermissionsForTenant(tenant, permissionsSource, orgId);
      if (revision !== context.scopeRevision) {
        return 'superseded';
      }
      patchState(store, { permissions: granted });
      context.permissionsOrgId = orgId;
    } catch (error) {
      return failPermissionReload(deps, error, revision);
    }
    context.pendingAttemptedPath = null;
  }

  const cachedAnswer = answerFromCache(deps, orgId, projectId);
  if (cachedAnswer !== null) {
    return cachedAnswer;
  }

  // Deliberately not awaited: the whole publication after the read belongs to one continuation.
  return readAndPublishProjects(deps, org, orgId, projectId, revision);
}

/**
 * The answer a cached list can give, synchronously. `null` means this org's list was never read, or a
 * failure dropped it.
 */
function answerFromCache(
  deps: ScopeResolverDeps,
  orgId: string,
  projectId: string | null,
): ScopeResolution | null {
  const { store, context, clearScope } = deps;
  const cached = store.currentOrg()?.id === orgId ? context.projectsByOrg.get(orgId) : undefined;
  if (cached === undefined) {
    return null;
  }
  if (projectId === null) {
    patchState(store, { currentProject: null });
    return 'ok';
  }
  const project = cached.find(
    (candidate) => candidate.id === projectId && candidate.org_id === orgId,
  );
  if (project === undefined) {
    clearScope();
    return 'not-found';
  }
  patchState(store, { currentProject: project });
  return 'ok';
}

/**
 * Reads the org's project list and publishes it, the requested project, or the `not-found` answer.
 * The first resolve of the org, or a retry after a failure, drops the previous scope before the read.
 *
 * The read is the only await: the revision check, the cache write and both publications after it run
 * in one synchronous continuation, so a session end queued while the read was on the wire can never
 * be overwritten by an answer that belongs to the ended session.
 */
async function readAndPublishProjects(
  deps: ScopeResolverDeps,
  org: OrganizationSummaryDto,
  orgId: string,
  projectId: string | null,
  revision: number,
): Promise<ScopeResolution> {
  const { store, context, identity, dropScope, clearScope } = deps;
  if (store.currentOrg()?.id !== orgId) {
    dropScope(org);
  }
  let projects: readonly ProjectDto[];
  // The route this read is serving, named for the expiry redirect if the session is gone.
  context.pendingAttemptedPath = scopePath(orgId, projectId);
  try {
    projects = await identity.listProjects(orgId);
  } catch (error) {
    return failScopeRead(deps, error, revision);
  }
  if (revision !== context.scopeRevision) {
    // A newer navigation owns the scope (and the pending route) by now; this answer is about a
    // request nobody waits for.
    return 'superseded';
  }
  context.pendingAttemptedPath = null;

  // Only the rows the server attributed to THIS org: the published list is what the switcher offers
  // and what a landing address is built from, so a row carrying another `org_id` must never reach it.
  const owned = projects.filter((candidate) => candidate.org_id === orgId);
  context.projectsByOrg.set(orgId, owned);
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

/**
 * The answer to a failed scope read: a `401` ends the session and cancels the guard chain, any other
 * failure clears the scope and is `unavailable`, and an answer a newer navigation superseded changes
 * nothing.
 */
function failScopeRead(deps: ScopeResolverDeps, error: unknown, revision: number): ScopeResolution {
  const { context, clearScope, handleUnauthenticated } = deps;
  const unauthenticated = isApiRequestError(error) && error.error.kind === 'unauthenticated';
  if (revision !== context.scopeRevision) {
    // A newer navigation owns the pending route: erasing it here would send its expiry redirect to
    // the wrong page (or none).
    return unauthenticated ? 'superseded' : scopeFailure(error);
  }
  const attempted = context.pendingAttemptedPath;
  context.pendingAttemptedPath = null;
  if (unauthenticated) {
    handleUnauthenticated(attempted);
    return 'superseded';
  }
  clearScope();
  return scopeFailure(error);
}

/**
 * A failed grant reload. It answers nothing about the org's existence, so a refusal is `unavailable`
 * (never the `not-found` a project lookup may share); the session's own `401` is the expiry path, and
 * a failure a newer navigation overtook is `superseded` without touching the state that navigation
 * now owns.
 */
function failPermissionReload(
  deps: ScopeResolverDeps,
  error: unknown,
  revision: number,
): ScopeResolution {
  const { context, clearScope, handleUnauthenticated } = deps;
  if (revision !== context.scopeRevision) {
    return 'superseded';
  }
  const attempted = context.pendingAttemptedPath;
  context.pendingAttemptedPath = null;
  if (isApiRequestError(error) && error.error.kind === 'unauthenticated') {
    handleUnauthenticated(attempted);
    return 'superseded';
  }
  clearScope();
  return 'unavailable';
}

export function createScopeResolver(deps: ScopeResolverDeps): ScopeResolver {
  return { resolveScope: (orgId, projectId) => resolveScope(deps, orgId, projectId) };
}
