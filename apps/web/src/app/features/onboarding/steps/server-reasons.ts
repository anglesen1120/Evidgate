import type { ApiError } from '../../../core/http/api-error';

/**
 * The fields whose server reason a visitor has already answered by editing them.
 *
 * A refusal's `details` describe the values that were submitted, so the field a visitor is editing
 * is the one that says a value is no longer the one they meant. The set is tied to the refusal it
 * came from: a new answer carries its own reasons, and dismissing one field never speaks for
 * another.
 */
export interface DismissedReasons {
  readonly refusal: ApiError | null;
  readonly fields: ReadonlySet<string>;
}

/** No reason has been dismissed yet. */
export const NO_DISMISSED_REASONS: DismissedReasons = { refusal: null, fields: new Set<string>() };

/** Whether `field`'s server reason is still shown for `refusal`. */
export function showsFieldReason(
  state: DismissedReasons,
  refusal: ApiError | null,
  field: string,
): boolean {
  if (refusal === null) return false;
  return !(state.refusal === refusal && state.fields.has(field));
}

/** The state after the visitor edited `field`, answering the refusal they are showing. */
export function dismissFieldReason(
  state: DismissedReasons,
  refusal: ApiError | null,
  field: string,
): DismissedReasons {
  if (refusal === null) return state;
  if (state.refusal !== refusal) return { refusal, fields: new Set([field]) };
  if (state.fields.has(field)) return state;
  return { refusal, fields: new Set([...state.fields, field]) };
}
