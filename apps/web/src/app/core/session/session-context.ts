/**
 * The mutable bookkeeping a `signalStore` cannot hold as state: it is per-instance, never published
 * as a signal and mutated by several feature modules. One object is created per store instance, so
 * every module below shares the same counters and caches without passing them one by one.
 */

import type { ProjectDto } from '@evidgate/sdk';

export type SessionContext = {
  /** The single in-flight bootstrap, so a burst of guards awaits one read instead of N. */
  inFlight: Promise<void> | null;
  /** Bumped by `reload()`; a bootstrap of an older generation never writes state. */
  sessionRevision: number;
  /** Bumped by every `resolveScope` call and by `reload()`; guards the async part of a scope. */
  scopeRevision: number;
  /**
   * The org whose grants `permissions` holds. The tenant can be published before its read returns,
   * so the tenant alone is not proof: a switch whose load was superseded must read again instead of
   * keeping the previous org's codes.
   */
  permissionsOrgId: string | null;
  /**
   * The route an in-flight scope read is resolving, so a `401` from that read can name it as the
   * `returnUrl` of the expiry redirect. Cleared as soon as the read settles.
   */
  pendingAttemptedPath: string | null;
  /**
   * The project list already read for the org it belongs to. A navigation inside the current org is
   * answered from here, and every failure, `not-found` and `reload()` drops it, so a list never
   * outlives the scope it was read under.
   */
  projectsByOrg: Map<string, readonly ProjectDto[]>;
  /** What every session-scoped timer registers here; `logout()` and an expiry run all of them. */
  endCallbacks: Set<() => void>;
};

export function createSessionContext(): SessionContext {
  return {
    inFlight: null,
    sessionRevision: 0,
    scopeRevision: 0,
    permissionsOrgId: null,
    pendingAttemptedPath: null,
    projectsByOrg: new Map<string, readonly ProjectDto[]>(),
    endCallbacks: new Set<() => void>(),
  };
}
