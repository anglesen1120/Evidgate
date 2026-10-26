// 09.3.1-T1 (plan 09.3.1 §10 T1.1/T1.2, Notes at lines 226-227; hand-over from 05.2.2-T1, #99):
// how a run's `ref_name` reaches the screen.
//
// The server accepts Unicode format characters (category Cf — the U+202E RIGHT-TO-LEFT OVERRIDE
// and its relatives) in a git ref name, because git does, and rejects control characters. A Cf
// character is invisible: a name carrying U+202E reorders everything rendered after it, so a cell
// that interpolates the raw value can make one run read as another (a trojan-source-style
// spoof in a review queue). Two defences are applied to every rendered ref name and neither is
// optional:
//
// 1. **This helper** replaces every Cf code point with a visible token, `⟨U+202E⟩`, so the operator
//    sees that the name carries a format character and which one.
// 2. **`<bdi>` isolation** in the table's cells (`run-list-table.component.html`: the ref cell and
//    the two scope cells): the value is rendered inside `<bdi>` so even a name without a Cf
//    character cannot reorder the columns around it.
//
// Both defences cover the row's `rerun_label` / `reprocess_label` too (see {@link escapeFormatLabel}):
// those are caller-written strings rendered beside the ref name, so an unescaped one spoofs a row
// exactly as an unescaped ref name would.
//
// Escaping happens on the value only. The markup is plain interpolation, never a sanitised HTML
// string, and the token itself contains no `&`/`<`/`>` that a renderer could reinterpret.

/**
 * The Unicode general category this module escapes: "Other, format" (`Cf`) — invisible characters
 * that affect rendering (bidi overrides and isolates, zero-width joiners, tag characters). Written
 * as one regex because `\p{Cf}` needs the `u` flag and a per-code-point test is the only correct
 * way to walk a string with astral members.
 */
const FORMAT_CHARACTER = /\p{Cf}/u;

/**
 * Render a run's `ref_name` (or any server-supplied label) as text that cannot hide a format
 * character.
 *
 * Every Cf code point becomes `⟨U+XXXX⟩`, with the code point in upper-case hex and at least four
 * digits (`U+202E`, `U+E0001`). Every other code point — letters, digits, punctuation, emoji, a
 * lone surrogate — is copied through unchanged, so the escaped value still compares equal to the
 * original once the tokens are read back by a human and never changes the string's length contract
 * for a caller that only displays it.
 *
 * @param value the stored `ref.name` / `ref_name`, or `''` when the run has no ref at all.
 * @returns the same text with each format character replaced by its visible token.
 */
export function escapeFormatChars(value: string): string {
  let escaped = '';
  // `for … of` walks code points, so a supplementary-plane Cf character (a tag character, for
  // example U+E0001) is one iteration and one token — `String.fromCharCode`/index arithmetic would
  // split its surrogate pair and emit two tokens for one character.
  for (const character of value) {
    escaped += FORMAT_CHARACTER.test(character)
      ? `⟨U+${(character.codePointAt(0) ?? 0).toString(16).toUpperCase().padStart(4, '0')}⟩`
      : character;
  }
  return escaped;
}

/**
 * Neutral alias of {@link escapeFormatChars} for a server-written value that is not a `ref_name`.
 *
 * A run row's `rerun_label` / `reprocess_label` comes from the same CI caller, carries the same
 * invisible-character surface (a U+202E in a label reorders the rest of the row) and is rendered in
 * the same row as the ref name, so it takes the identical escaping. The alias exists only to name
 * the intent at the call site instead of claiming the value is a ref.
 */
export const escapeFormatLabel = escapeFormatChars;
