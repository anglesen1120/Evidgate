/**
 * The pure helpers the session reads and the scope resolution share. The settled-read pair already
 * has one implementation in `core/http`; it is re-exported here instead of copied, so the two
 * cannot drift.
 */

import { isApiRequestError } from '../http/api-error';

import type { ScopeResolution } from './session.types';

export { settledRead } from '../http/settled-read';
export type { SettledRead } from '../http/settled-read';

/**
 * A failed scope lookup. A refusal the server expressed about the resource (`404`, and a `403` for
 * a caller who may not read this org's projects) is indistinguishable from an id that does not
 * exist, so both answer `not-found` and the wildcard route renders the same 404 pattern.
 * Everything else means the check could not be completed: `unavailable`.
 */
export function scopeFailure(error: unknown): ScopeResolution {
  if (
    isApiRequestError(error) &&
    (error.error.kind === 'not_found' || error.error.kind === 'forbidden')
  ) {
    return 'not-found';
  }
  return 'unavailable';
}

/** The route a scope read is serving, named for the expiry redirect when the session is gone. */
export function scopePath(orgId: string, projectId: string | null): string {
  const org = `/orgs/${encodeURIComponent(orgId)}`;
  return projectId === null ? org : `${org}/projects/${encodeURIComponent(projectId)}`;
}
