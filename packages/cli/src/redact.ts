// Redaction of every secret the CLI reads. `main` and `resolveConfig` register env
// secrets, and every write goes through `createRedactingIo`, which replaces a registered
// value with `[REDACTED]` and never lets an `Authorization` header through.
import { escapeControlCharacters } from './errors.js';

/** Placeholder that replaces every registered secret. */
export const redacted = '[REDACTED]';

/** The two writers `main` is given; both are pure sinks so the CLI stays testable. */
export interface Io {
  stdout(text: string): void;
  stderr(text: string): void;
}

const registeredSecrets = new Set<string>();

/** Registered secrets, longest first, so a shorter prefix cannot mask a longer value. */
let secretsByLength: readonly string[] = [];

/**
 * The `Authorization:` header shape, name included, and the `Authorization=` assignment that
 * carries the same credential. `redact` keeps the name (`$1`) because a reader benefits from
 * knowing what was masked; a caller writing a single-line format (JSON) replaces the whole
 * match instead, so the value cannot run to the end of that line.
 */
export const authorizationHeaderShape = /(authorization\s*[:=]\s*)[^\r\n]*/gi;

/**
 * The same header as a JSON member, `"Authorization":"Bearer …"`. The quotes around the value
 * are matched so the replacement can keep the member parseable; an escape pair counts as a
 * value character, the value stops at an unescaped quote or a line end, and a member whose
 * quote was cut off (a truncated message) is masked to the line end rather than left readable.
 */
const authorizationJsonMemberShape =
  /(\\?"authorization\\?"\s*:\s*\\?")(?:[^"\\\r\n]|\\.)*(\\?")?/gi;

/**
 * An absolute http(s) URL. Only the characters that cannot appear in one end the match —
 * whitespace, `"`, `<`, `>`, a backtick — so a `)`, `]` or `}` inside a path can never cut the
 * match short of its query. Wrapping punctuation is separated again in `credentialEnd`.
 */
const absoluteUrl = /\bhttps?:\/\/[^\s"<>`]+/gi;
/** Characters that belong to the surrounding text or to a JSON escape, not to the credential. */
const urlTrailingPunctuation: Record<string, true> = {
  '.': true,
  ',': true,
  ';': true,
  ':': true,
  '!': true,
  '?': true,
  '\\': true,
  ')': true,
  ']': true,
  '}': true,
  '>': true,
  "'": true,
  '"': true,
};
/**
 * `scheme://userinfo@host` — userinfo is a credential pair, never printed. The run is greedy
 * up to the last `@` before the path, because the password itself may contain one.
 */
const urlUserInfo = /^(https?:\/\/)[^/?#]*@/i;

/**
 * Registers a secret value and the forms it can take in an error message: the value
 * itself, its trimmed, percent-encoded, JSON-escaped and control-character-escaped forms.
 * The escaped form matters because the message is escaped before the redacting writer sees
 * it, so a value containing a `Cc`/`Cf` character would otherwise no longer match. Empty
 * forms are skipped; a value shorter than four characters is still registered (fail-safe).
 */
export function registerSecret(value: string | undefined): void {
  if (value === undefined || value.length === 0) {
    return;
  }
  let added = false;
  const jsonEscaped = JSON.stringify(value).slice(1, -1);
  for (const form of [
    value,
    value.trim(),
    encodeURIComponent(value),
    jsonEscaped,
    escapeControlCharacters(value),
  ]) {
    if (form.length > 0 && !registeredSecrets.has(form)) {
      registeredSecrets.add(form);
      added = true;
    }
  }
  if (added) {
    secretsByLength = [...registeredSecrets].sort((a, b) => b.length - a.length);
  }
}

/** Clears the registry — `main` calls it at entry, specs between cases. */
export function resetSecrets(): void {
  registeredSecrets.clear();
  secretsByLength = [];
}

/**
 * Replaces every registered secret and any `Authorization` header value, then strips URL
 * credentials: userinfo is removed, and an http(s) URL loses its whole query and its whole
 * fragment, because a presigned URL IS a bearer credential whose signature travels in it (an
 * OAuth fragment flow carries the token in the same way). Naming the parameters to strip
 * would leak the ones a provider names differently, so the rule is positional. Only an
 * absolute http(s) URL is recognised; a scheme-relative or percent-encoded one is not text
 * this command produces, and widening the rule would start masking unrelated words.
 * Rewriting an already-redacted URL is a no-op, so the writer's second pass cannot grow it.
 */
export function redact(text: string): string {
  // The text is split on the placeholder first, so a secret registered as a short fragment of
  // `[REDACTED]` cannot rewrite one and grow it on every pass.
  let output = text.split(redacted).map(replaceSecrets).join(redacted);
  output = output.replace(
    authorizationJsonMemberShape,
    (_member: string, head: string, tail: string | undefined) => `${head}${redacted}${tail ?? ''}`,
  );
  output = output.replace(authorizationHeaderShape, `$1${redacted}`);
  return output.replace(absoluteUrl, redactUrl);
}

/** Replaces every registered secret in text that carries no placeholder yet. */
function replaceSecrets(text: string): string {
  let output = text;
  for (const secret of secretsByLength) {
    output = output.split(secret).join(redacted);
  }
  return output;
}

/**
 * Redacts one matched URL: the userinfo goes, and so does everything after the first `?` or
 * `#`. Punctuation that belongs to the surrounding text — a closing bracket from wrapping
 * text, or the backslash of a JSON escape — stays outside the placeholder, and an
 * already-redacted URL is returned unchanged, so a second pass cannot grow it.
 */
function redactUrl(raw: string): string {
  const withoutUserInfo = raw.replace(urlUserInfo, '$1');
  const start = credentialStart(withoutUserInfo);
  if (start === -1) {
    return withoutUserInfo;
  }
  const tail = withoutUserInfo.slice(start + 1);
  if (tail.length === 0) {
    return withoutUserInfo;
  }
  const end = credentialEnd(tail);
  if (tail.slice(0, end) === redacted) {
    return withoutUserInfo;
  }
  return `${withoutUserInfo.slice(0, start + 1)}${redacted}${tail.slice(end)}`;
}

/** Where the credential inside a URL tail ends: trailing punctuation and escapes stay out. */
function credentialEnd(tail: string): number {
  let end = tail.length;
  while (end > 0) {
    const last = tail[end - 1] as string;
    if (urlTrailingPunctuation[last] !== true || tail.slice(0, end).endsWith(redacted)) {
      return end;
    }
    end -= 1;
  }
  return end;
}

/**
 * Where the credential part of a URL begins: the first `?` or `#`, whichever comes first. A
 * fragment counts because an OAuth implicit flow carries its token there, and a `?` inside a
 * fragment must not hide the fragment in front of it.
 */
function credentialStart(url: string): number {
  const query = url.indexOf('?');
  const fragment = url.indexOf('#');
  if (query === -1 || fragment === -1) {
    return Math.max(query, fragment);
  }
  return Math.min(query, fragment);
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
