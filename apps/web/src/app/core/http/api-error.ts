// 09.1.1-T3.1 (00 §9.2, ADR-0008 decision (a) and rule 2): the typed failure every API call
// is reduced to before it reaches a page or a store. The kind set is closed — one member per
// outcome the UI has a distinct state for (00 §5.6 W5) — so no component ever branches on an
// HTTP status or on a server code string, and a new server code never adds a UI state.

import type { ErrorEnvelopeDetail } from '@evidgate/sdk';

/**
 * UI-level classification of a failed API call. Exactly one kind per failure, assigned by
 * `mapApiError` (see `error-mapper.ts` for the status/code table).
 */
export type ApiErrorKind =
  /** 401: no usable session; the shell sends the user back through login. */
  | 'unauthenticated'
  /** 403 `csrf_failed`: the antiforgery token did not match; re-read the cookie and retry. */
  | 'csrf_failed'
  /** 403 for any other reason: permission, project scope, membership, waiver, producer slot. */
  | 'forbidden'
  /** 404, including other-tenant and other-project ids (they are indistinguishable by design). */
  | 'not_found'
  /** 409 `idempotency_conflict`: the key was already used for a different request. */
  | 'idempotency_conflict'
  /** 409 for any other reason: invalid transition, state conflict, taken key, deleting, … */
  | 'conflict'
  /** 412: the row changed since it was read; `details` carries the current revision. */
  | 'precondition_failed'
  /** 413: the body or batch was refused before any write. */
  | 'payload_too_large'
  /** 422: the payload or query was refused; `code`/`details` name the offending field. */
  | 'validation'
  /** 429: burst or quota limit; `retryAfterSeconds` is the server's `Retry-After`. */
  | 'rate_limited'
  /** 503: a dependency or lock was busy; `retryAfterSeconds` is the server's `Retry-After`. */
  | 'unavailable'
  /**
   * No HTTP response at all (offline, DNS, TLS) — not a server decision; a cancellation is
   * re-thrown, never mapped.
   */
  | 'network'
  /**
   * No HTTP response within the request deadline (`DEFAULT_REQUEST_TIMEOUT_MS`, 09.1.1-T8). The
   * request may have reached the server, or it may never have left, so it is neither a server
   * decision nor proof that nothing happened: a retry is only safe with the same
   * `Idempotency-Key` as the original attempt.
   */
  | 'timeout'
  /** Anything the table does not classify: a status whose meaning needs the body, 2xx misuse. */
  | 'unexpected';

/**
 * The single error shape pages, stores and the shell consume. `message` and `details` are
 * server text (00 §9.2: details never contain secrets); they are rendered as text and are
 * never used to build markup, and no request body or header value is ever added here.
 */
export interface ApiError {
  readonly kind: ApiErrorKind;
  /** HTTP status of the response; `0` when no response exists (network/transport failure). */
  readonly status: number;
  /** The server `error.code` (00 §9.2), or a mapper-local code when no envelope exists. */
  readonly code: string;
  readonly message: string;
  /** `error.request_id` (echoed in `X-Request-Id`); `null` when no envelope was received. */
  readonly requestId: string | null;
  /** Whether the same intent may be retried (same `Idempotency-Key`, 00 §9.2). */
  readonly retryable: boolean;
  /** `error.details` verbatim (field paths and reasons); empty when absent. */
  readonly details: readonly ErrorEnvelopeDetail[];
  /** `Retry-After` in whole seconds for 429/503; `null` when absent or unparsable. */
  readonly retryAfterSeconds: number | null;
}

/**
 * Thrown (or rejected with) by the data layer so a caller can `catch` one type and read the
 * typed failure from `error`. Rejections that are not this type are programming errors.
 */
export class ApiRequestError extends Error {
  /**
   * @param error the typed failure the caller reads.
   * @param options `cause` carries the raw exception for the `network` kind (`fetch`'s
   *   `TypeError`), so a bug report keeps the transport detail the `ApiError` deliberately omits.
   */
  constructor(
    readonly error: ApiError,
    options?: ErrorOptions,
  ) {
    super(error.message, options);
    this.name = 'ApiRequestError';
  }
}

/** Narrows an unknown rejection to {@link ApiRequestError}. */
export function isApiRequestError(value: unknown): value is ApiRequestError {
  return value instanceof ApiRequestError;
}
