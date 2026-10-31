// The one `<n>` / `<n>s` / `<n>m` / `<n>h` duration grammar the deadline settings and
// `gate --wait` share. The caller owns the ceiling and the wording, so an environment
// variable and a flag can each name themselves in their own error message.

/** Why a duration was refused; the caller turns it into its own error. */
export type DurationRejection = 'grammar' | 'not_positive' | 'above_ceiling';

export type ParsedDuration =
  | { readonly ok: true; readonly milliseconds: number }
  | { readonly ok: false; readonly reason: DurationRejection };

/**
 * A bare integer is seconds and an `s`/`m`/`h` suffix scales it. A sign, a fraction, an
 * unknown suffix or surrounding text is `grammar`; an amount whose product leaves the safe
 * integer range is reported as `above_ceiling`, because the caller's ceiling can never admit
 * it. The digits are bounded only to keep the parse cheap, not to bound the value.
 */
const durationPattern = /^([0-9]{1,20})([smh]?)$/;

/** Parses `raw` against `maxMs`; the value is never echoed, so a caller may name it freely. */
export function parseDuration(raw: string, maxMs: number): ParsedDuration {
  const match = durationPattern.exec(raw);
  if (match === null) {
    return { ok: false, reason: 'grammar' };
  }
  const unit = match[2] ?? 's';
  const scale = unit === 'h' ? 3_600_000 : unit === 'm' ? 60_000 : 1_000;
  const milliseconds = Number(match[1]) * scale;
  if (milliseconds <= 0) {
    return { ok: false, reason: 'not_positive' };
  }
  if (!Number.isSafeInteger(milliseconds) || milliseconds > maxMs) {
    return { ok: false, reason: 'above_ceiling' };
  }
  return { ok: true, milliseconds };
}
