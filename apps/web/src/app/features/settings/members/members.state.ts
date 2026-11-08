// The frozen values the Members store publishes when there is nothing to show, and the pure
// helpers that map a settled read or a rejection onto them: one declaration per value, so an empty
// list keeps one identity across a reload and every caller names the same failure.

import type { MembershipView, RoleView } from '@evidgate/sdk';

import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { AppliedRoleChange, MutationState } from './members.types';
import type { RolePreview } from './role-preview';

/** The frozen empty values, so an empty store and a degraded read share one array identity. */
export const NO_MEMBERS: readonly MembershipView[] = [];
export const NO_ROLES: readonly RoleView[] = [];
export const NO_APPLIED: readonly AppliedRoleChange[] = [];

export const IDLE: MutationState = { phase: 'idle', error: null };
export const SUBMITTING: MutationState = { phase: 'submitting', error: null };
export const SUBMITTED: MutationState = { phase: 'done', error: null };

/** What the preview of an id the store does not know answers, without building a new object. */
export const NO_ROLE_PREVIEW: RolePreview = { kind: 'unavailable', reason: 'unknown-role' };

/**
 * The failure a mutation reports when nothing answered — a cancellation or a bug: `unexpected` has
 * no UI state, `status: 0` says no response exists, and no text is taken from the thrown value (it
 * is not server copy, and its message may carry request data the store must not surface).
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: 'The request did not complete, so nothing was confirmed. Try again.',
  requestId: null,
  // Retrying the same intent is safe: the dialog replays the original `Idempotency-Key`.
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

/** The server's own refusal carried by a rejection, or `null` when it is not the server's answer. */
export function refusalOf(error: unknown): ApiError | null {
  return isApiRequestError(error) ? error.error : null;
}

/** What a mutation reports for a rejection that nothing answered: the generic failure above. */
export function mutationFailure(error: unknown): ApiError {
  return refusalOf(error) ?? UNRESOLVED_FAILURE;
}

/** One independent read of `load`, settled so one failure cannot cancel the other two. */
export type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/** Never rejects: a failure is a value, so one read cannot cancel its neighbours. */
export async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}
