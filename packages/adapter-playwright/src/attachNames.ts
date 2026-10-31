// The name and code hygiene of an attachment record: a base name only, control characters
// escaped rather than removed, a length cap on a rune boundary, and a CLI error `code` reduced to a
// bare token. A record, a warning or a message never carries a directory, a URL or an error message.
import type { PlaywrightAttachment } from './attachMap.js';

/** The most UTF-16 units of a base name a record, warning or message carries. */
const NAME_MAX = 120;

/** Control and format characters, escaped rather than removed (`status.ts` `printable`). */
const UNSAFE_CHARS = /[\p{Cc}\p{Cf}]/gu;

/** The escapes a reader can undo; every other unsafe character becomes `\u{<hex>}`. */
const NAMED_ESCAPES: Readonly<Record<string, string>> = {
  '\n': '\\n',
  '\r': '\\r',
  '\t': '\\t',
};

/** A UUID in any case; the artifact id from a completed upload must be one of these. */
export const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

/** A CLI error code is a bare token; anything else is reported as `unknown`. */
const CODE_PATTERN = /^[A-Za-z0-9_.-]{1,64}$/u;

/**
 * The CLI's error `code` as a bare token, or `unknown`. The MESSAGE is deliberately not
 * forwarded: it can embed a presigned URL, a token echo or a filesystem path.
 */
export function errorCode(error: unknown): string {
  if (typeof error !== 'object' || error === null || !('code' in error)) return 'unknown';
  const code = error.code;
  return typeof code === 'string' && CODE_PATTERN.test(code) ? code : 'unknown';
}

/** The name an attachment is known by: base name only, escaped, capped. */
export function shownName(attachment: PlaywrightAttachment): string {
  const path = typeof attachment.path === 'string' ? attachment.path : '';
  const raw = path.length > 0 ? baseNameOf(path) : String(attachment.name ?? '');
  return cut(escapeControls(raw), NAME_MAX);
}

/** The last segment of a path that may use either separator; `''` when there is none. */
function baseNameOf(path: string): string {
  const normalised = path.replaceAll('\\', '/');
  const index = normalised.lastIndexOf('/');
  return index >= 0 ? normalised.slice(index + 1) : normalised;
}

/**
 * Every control or format character as visible text (the same rule as
 * `attachMap.ts`/`attachPaths.ts`: an escape keeps tampering visible instead of hiding it).
 */
function escapeControls(value: string): string {
  return value.replace(UNSAFE_CHARS, (character) => {
    const named = NAMED_ESCAPES[character];
    if (named !== undefined) return named;
    const codePoint = character.codePointAt(0) ?? 0;
    return codePoint <= 0xffff
      ? `\\u${codePoint.toString(16).padStart(4, '0')}`
      : `\\u{${codePoint.toString(16)}}`;
  });
}

/** At most `max` UTF-16 units, never ending on a lone high surrogate. */
function cut(value: string, max: number): string {
  if (value.length <= max) return value;
  let end = max;
  const last = value.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) end -= 1;
  return value.slice(0, end);
}
