/**
 * Reading the session: one round trip of organizations and permissions, the single-flight
 * `bootstrap()` every guard may await, and the `reload()` the unavailable page retries. Nothing here
 * resolves a scope; a read only publishes the session and its first tenant's grants.
 */

import { patchState } from '@ngrx/signals';

import type { OrganizationSummaryDto } from '@evidgate/sdk';

import type { IdentityDataService } from '../data/identity-data.service';
import { isApiRequestError } from '../http/api-error';

import type { EffectivePermissionsSource } from './effective-permissions.source';
import type { SessionContext } from './session-context';
import { settledRead } from './session-read.helpers';
import type { SessionStoreHandle } from './session.types';
import { loadPermissionsForTenant } from './tenant-permissions';
import type { TenantStore } from './tenant.store';

export type SessionReadDeps = {
  readonly store: SessionStoreHandle;
  readonly context: SessionContext;
  readonly identity: IdentityDataService;
  readonly tenant: TenantStore;
  readonly permissionsSource: EffectivePermissionsSource;
  readonly clearScope: () => void;
  readonly clearSession: () => void;
};

export type SessionRead = {
  /** Reads the session once; concurrent callers share one in-flight read. */
  readonly bootstrap: () => Promise<void>;
  /** Forgets the session and reads it again; rejects when the retry ends unreadable. */
  readonly reload: () => Promise<void>;
};

/**
 * One round trip of organizations and permissions; a stale generation writes nothing.
 *
 * The organization list is read FIRST: the permission read is tenant-scoped, so the header the
 * interceptor writes for it must name this session's tenant. An empty (or failed) list leaves no
 * tenant to scope by — the interceptor adds nothing and the read answers the pre-tenant route.
 *
 * A `401` from EITHER read is the one refusal with a UI state of its own (`anonymous`) and wins over
 * the other read's failure — an expired session is an answer, not an outage. Everything else — a
 * `5xx`, a dropped connection, a broken cursor loop, a missing provider — means "could not read",
 * which the shell renders as unavailable rather than as an empty, silently degraded workspace. Any
 * failure also drops the cached project lists, so a later resolve cannot publish a list read under a
 * session that is no longer confirmed.
 */
async function readSession(deps: SessionReadDeps, revision: number): Promise<void> {
  const { store, context, identity, tenant, permissionsSource, clearScope } = deps;
  const organizations = await settledRead(() => identity.listMyOrganizations());
  // A newer generation (a `reload()`, a logout, an expiry) owns the state by now; a latched sign-out
  // must never be undone by a read that was already on the wire.
  if (revision !== context.sessionRevision || store.signedOut()) {
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
  if (revision !== context.sessionRevision || store.signedOut()) {
    return;
  }
  if (!organizations.ok || !permissions.ok) {
    const failures = [organizations, permissions].flatMap((read) => {
      if (read.ok) {
        return [];
      }
      return [read.error];
    });
    // The tenant that scoped the refused read dies with the session: a later `/login` or sign-in
    // must not inherit it as the header of its first request.
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
  context.permissionsOrgId = firstOrgId;
}

/**
 * Reads the session once. Idempotent: a settled state is returned as-is and concurrent callers share
 * one in-flight read, so every guard may await it unconditionally.
 */
function bootstrap(deps: SessionReadDeps): Promise<void> {
  const { store, context } = deps;
  // The caller ended this session on this tab: no request may re-open it. `reauth()` clears the
  // latch when a new sign-in begins.
  if (store.signedOut()) {
    return Promise.resolve();
  }
  if (context.inFlight !== null) {
    return context.inFlight;
  }
  if (store.sessionState() !== 'unknown') {
    return Promise.resolve();
  }
  const run: Promise<void> = readSession(deps, context.sessionRevision).finally(() => {
    if (context.inFlight === run) {
      context.inFlight = null;
    }
  });
  context.inFlight = run;
  return run;
}

/**
 * Forgets the session and reads it again, for the service-unavailable page's retry and for a later
 * sign-in. In-flight reads of the previous generation are ignored when they settle.
 *
 * Unlike `bootstrap()`, this rejects when the retry ends unreadable: a guard must never throw, but
 * the retry button has to know whether it worked (`ServiceUnavailablePage` shows its failure copy
 * from this rejection; without it a failed retry would navigate and bounce straight back). An
 * `anonymous` outcome resolves — "no session" is an answer, not a failed read.
 */
async function reload(deps: SessionReadDeps): Promise<void> {
  const { store, context, clearSession } = deps;
  if (store.signedOut()) {
    // Latched: staying anonymous without a request is the answer, exactly as `bootstrap`.
    patchState(store, { sessionState: 'anonymous' });
    clearSession();
    return;
  }
  context.sessionRevision += 1;
  context.inFlight = null;
  patchState(store, { sessionState: 'unknown' });
  clearSession();
  await bootstrap(deps);
  if (store.sessionState() === 'unavailable') {
    throw new Error('the workspace is still unavailable');
  }
}

export function createSessionRead(deps: SessionReadDeps): SessionRead {
  return { bootstrap: () => bootstrap(deps), reload: () => reload(deps) };
}
