/**
 * The evidence pin is the server revision number written as a canonical decimal: one to nine
 * digits, no sign, no leading zero, no exponent or hexadecimal form.
 *
 * `Number()` alone accepts `1e3`, `0x10` and `' 5 '`, so a URL value is never handed to it
 * unvalidated: those forms would silently pin a revision the visitor never asked for.
 */
const CANONICAL_REVISION = /^[1-9]\d{0,8}$/;

/** The largest revision number the canonical form can carry (`999999999`). */
export const MAX_PINNED_REVISION = 999_999_999;

/** A `?evidence_revision=` value as a pin, or `null` when it is not a canonical decimal. */
export function parsePinnedRevision(value: string | null): number | null {
  if (value === null || !CANONICAL_REVISION.test(value)) return null;
  return Number(value);
}

/**
 * True for a number a canonical pin can carry. Callers that receive a number (the store's pin
 * methods) apply the same range, so `1e21` or `0x10` cannot enter through a typed call site.
 */
export function isCanonicalRevisionNo(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 1 &&
    value <= MAX_PINNED_REVISION
  );
}
