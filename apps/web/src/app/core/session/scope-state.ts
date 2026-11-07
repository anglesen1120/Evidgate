/**
 * The scope half of the session state: what is current, what `has()` answers, and the guarantees
 * that make a stale answer harmless. Every writer here publishes a whole scope at once, so a reader
 * never sees the previous org's grants or projects attributed to the new one.
 */

import { patchState } from '@ngrx/signals';

import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import type { SessionContext } from './session-context';
import {
  NO_ORGANIZATIONS,
  NO_PERMISSIONS,
  NO_PROJECTS,
  type SessionStoreHandle,
} from './session.types';
import type { TenantStore } from './tenant.store';

export type ScopeStateDeps = {
  readonly store: SessionStoreHandle;
  readonly context: SessionContext;
  readonly tenant: TenantStore;
};

export type ScopeState = {
  /** Forgets the resolved pair and every cached project list. */
  readonly clearScope: () => void;
  /** Forgets the organizations and the grants, then the scope. */
  readonly clearSession: () => void;
  /** Publishes a new current org and empties the scope it replaces. */
  readonly dropScope: (org: OrganizationSummaryDto) => void;
  /** Appends a just-created project to the current org's cached list. */
  readonly adoptCreatedProject: (project: ProjectDto) => void;
  /** Answers from the published grant set only. */
  readonly has: (code: string) => boolean;
};

/**
 * Forgets the resolved pair, its project list, the grants that were read for it, and every cached
 * project list. A grant set without a scope has no org to answer `has()` for, so it goes with the
 * scope instead of staying published under no attribution.
 */
function clearScope(deps: ScopeStateDeps): void {
  const { store, context, tenant } = deps;
  context.scopeRevision += 1;
  context.permissionsOrgId = null;
  tenant.clear();
  patchState(store, {
    currentOrg: null,
    currentProject: null,
    projects: NO_PROJECTS,
    permissions: NO_PERMISSIONS,
  });
  context.projectsByOrg.clear();
}

/** Forgets everything a `reload()` must not keep. */
function clearSession(deps: ScopeStateDeps): void {
  patchState(deps.store, { permissions: NO_PERMISSIONS, organizations: NO_ORGANIZATIONS });
  clearScope(deps);
}

/**
 * Publishes the org the URL now names and empties the scope it replaces: the previous org's projects
 * and navigation links belong to an org the route no longer names, so neither may stay on screen
 * while the new org's grants and project list are read. Its grants go the same way, except for a drop
 * of the org whose grants are already published — the retry branch of `resolveScope` — which keeps
 * them.
 */
function dropScope(deps: ScopeStateDeps, org: OrganizationSummaryDto): void {
  const { store, context } = deps;
  context.projectsByOrg.clear();
  const keepsGrants = context.permissionsOrgId === org.id;
  if (!keepsGrants) {
    context.permissionsOrgId = null;
  }
  patchState(store, {
    currentOrg: org,
    currentProject: null,
    projects: NO_PROJECTS,
    permissions: keepsGrants ? store.permissions() : NO_PERMISSIONS,
  });
}

/**
 * Adopts a project the caller just created, so the scope the wizard redirects to is already known
 * without a second read. The server list stays the evidence: the project is appended only when its
 * org is the current one AND that org's list was actually read, and an id the list already holds is
 * not appended twice. In every other case the cache entry is dropped, so the next `resolveScope`
 * reads the server instead of answering `not-found` from a list that could not hold the project.
 */
function adoptCreatedProject(deps: ScopeStateDeps, project: ProjectDto): void {
  const { store, context } = deps;
  const orgId = project.org_id;
  const cached = store.currentOrg()?.id === orgId ? context.projectsByOrg.get(orgId) : undefined;
  if (cached === undefined) {
    // Not the current org, or its list was never read: nothing may be published from here.
    context.projectsByOrg.delete(orgId);
    return;
  }
  if (cached.some((candidate) => candidate.id === project.id)) {
    // Already listed: a second copy would make the id ambiguous for the guards and the switcher.
    return;
  }
  const next: readonly ProjectDto[] = [...cached, project];
  context.projectsByOrg.set(orgId, next);
  patchState(store, { projects: next });
}

/** `true` only for a code the source put in the set — an empty set grants nothing. */
function hasCode(deps: ScopeStateDeps, code: string): boolean {
  return deps.store.permissions().has(code);
}

export function createScopeState(
  store: SessionStoreHandle,
  context: SessionContext,
  tenant: TenantStore,
): ScopeState {
  const deps: ScopeStateDeps = { store, context, tenant };
  return {
    clearScope: () => clearScope(deps),
    clearSession: () => clearSession(deps),
    dropScope: (org) => dropScope(deps, org),
    adoptCreatedProject: (project) => adoptCreatedProject(deps, project),
    has: (code) => hasCode(deps, code),
  };
}
