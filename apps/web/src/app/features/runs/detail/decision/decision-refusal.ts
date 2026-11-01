import type { ApiError } from '../../../../core/http/api-error';

/**
 * Refusals that answer "you may not see this" rather than "not right now". A decision body's
 * reasons and hashes never survive one: keeping them on screen would disclose protected content to
 * a caller the server just refused, including across an identity, membership or tenant change.
 * The store and the region share this one list so the two can never disagree about which failures
 * merely stale a body and which erase it.
 */
export function refusesAccess(error: ApiError | null): boolean {
  return (
    error !== null &&
    (error.kind === 'unauthenticated' ||
      error.kind === 'csrf_failed' ||
      error.kind === 'forbidden' ||
      error.kind === 'not_found')
  );
}
