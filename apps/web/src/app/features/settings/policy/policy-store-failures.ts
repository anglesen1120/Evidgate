// The typed failures of the Policy tab's reads and publishes, and the one body shape a publish
// accepts. Every copy here is the store's own: text taken from a thrown value may carry request
// data, so it is never surfaced.

import { isApiRequestError, type ApiError, type ApiErrorKind } from '../../../core/http/api-error';
import { conflictFromError } from '../../../shared/ui/conflict/conflict-details';

/**
 * The refusals indistinguishable from "there is no such revision": a `404` and a `403` for a
 * reason other than the antiforgery token. Both become `not_found`, so the tab cannot tell an id
 * it may not see from an id that does not exist.
 */
const REFUSAL_KINDS: readonly ApiErrorKind[] = ['not_found', 'forbidden'];

/** No fields to compare in a policy conflict: the dialog learns the head, not a field snapshot. */
const NO_CONFLICT_FIELDS: Readonly<Record<string, string>> = {};

/**
 * The typed failure when a publish rejection is not the server's answer — an adapter cancellation
 * or a bug. A request whose outcome is unknown is never retried automatically, because the head
 * may have moved.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'policy_publish_unresolved',
  message:
    'The request did not complete, so nothing was confirmed; reload the revisions before publishing again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/** No project is open, so a publish has nowhere to go and sends nothing. */
export const NO_SCOPE_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'policy_scope_unavailable',
  message: 'No policy revision is open, so nothing was sent.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * This session does not hold `policy:write`. The publish control is absent from the DOM already,
 * so this repeats a rule the UI hides rather than pre-empting a server answer.
 */
export const NO_WRITE_FAILURE: ApiError = {
  kind: 'forbidden',
  status: 0,
  code: 'policy_write_forbidden',
  message: 'This session may not publish policy revisions, so nothing was sent.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * A read was asked for an id that is not revision-shaped, so it makes no request at all. The id
 * may be server text from a `412` envelope, which never passed the page's own check.
 */
export const MALFORMED_REVISION_FAILURE: ApiError = {
  kind: 'not_found',
  status: 0,
  code: 'policy_revision_id_invalid',
  message: 'That is not a policy revision id, so no revision was read.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * A body read answered for another project than the one the read was asked for. It is refused as
 * a missing revision, so a body cannot prove that another project's id exists.
 */
export const FOREIGN_REVISION_FAILURE: ApiError = {
  kind: 'not_found',
  status: 0,
  code: 'policy_revision_not_found',
  message: 'The policy revision was not found in this project.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The wire body a publish sends: the text parsed, or `null` when it is not a JSON OBJECT. An array
 * and a scalar are refused alike, because the schema is a closed object.
 */
export function parsePolicyObject(bodyText: string): Record<string, unknown> | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(bodyText);
  } catch {
    return null;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return null;
  }
  return parsed as Record<string, unknown>;
}

/** What one failed read means: an existence-hiding refusal, or an error with (or without) copy. */
export interface ReadFailure {
  /** True for the refusals that must render exactly as a missing revision. */
  readonly notFound: boolean;
  /** The server's own error, or `null` when it said nothing a user can act on. */
  readonly error: ApiError | null;
}

/**
 * Classifies one failed read. Both reads classify identically on purpose: a `404` on either route
 * must not tell the user which of the two objects was missing, and a rejection that never reached
 * the server is an error without copy.
 */
export function classifyReadFailure(error: unknown): ReadFailure {
  if (!isApiRequestError(error)) {
    return { notFound: false, error: null };
  }
  if (REFUSAL_KINDS.includes(error.error.kind)) {
    return { notFound: true, error: null };
  }
  return { notFound: false, error: error.error };
}

/** The two publish rejections a caller acts on differently: a conflict, or a refusal. */
export type PublishFailure =
  | {
      readonly kind: 'conflict';
      readonly error: ApiError;
      readonly currentRevisionId: string | null;
    }
  | { readonly kind: 'refused'; readonly error: ApiError };

/**
 * Classifies one publish rejection. A `412` names the current head in `details`, which the shared
 * conflict dialog renders and offers a reload for; every other refusal is handed back verbatim.
 */
export function classifyPublishFailure(error: unknown): PublishFailure {
  if (!isApiRequestError(error)) {
    return { kind: 'refused', error: UNRESOLVED_FAILURE };
  }
  const failure = error.error;
  if (failure.kind === 'precondition_failed') {
    return {
      kind: 'conflict',
      error: failure,
      currentRevisionId: conflictFromError(failure, NO_CONFLICT_FIELDS, NO_CONFLICT_FIELDS)
        .currentRevision,
    };
  }
  return { kind: 'refused', error: failure };
}
