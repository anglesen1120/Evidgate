// Invisible format characters can spoof server-supplied identifiers and labels.
// Display escaped text inside <bdi>; neither defence replaces the other.
// Refuse clipboard originals containing these characters rather than copying invisible changes.

/** Unicode format characters include bidi controls, zero-width joiners and astral tags. */
const FORMAT_CHARACTER = /\p{Cf}/u;

/** Makes every format code point visible; other code points remain unchanged. */
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

/** The same defence applies to server-written labels as to ref names. */
export const escapeFormatLabel = escapeFormatChars;

/** Use the same category for clipboard refusal and visible escaping. */
export function hasFormatChars(value: string): boolean {
  return FORMAT_CHARACTER.test(value);
}
