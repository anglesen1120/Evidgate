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
 * The `Authorization:` header shape, name included. `redact` keeps the name (`$1`) because a
 * reader benefits from knowing what was masked; a caller writing a single-line format (JSON)
 * replaces the whole match instead, so the value cannot run to the end of that line.
 */
export const authorizationHeaderShape = /(authorization\s*:\s*)[^\r\n]*/gi;

/**
 * An absolute http(s) URL. A unit is either a bracketed group — a host like `[::1]`, or an
 * already-redacted query value — or a run of characters that stops at whitespace, quotes,
 * angle brackets, brackets and the usual closing punctuation. That keeps wrapping text
 * outside the match and never cuts a placeholder in half, so a redacted URL stays stable.
 */
const absoluteUrl = /\bhttps?:\/\/(?:\[[^\s"'<>\]]*\]|[^\s"'<>`)\]}\[])+/gi;
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
 * would leak the ones a provider names differently, so the rule is positional. Rewriting an
 * already-redacted URL is a no-op: the pattern matches the placeholder whole, so the value
 * compares equal and stays as it is.
 */
export function redact(text: string): string {
  let output = text;
  for (const secret of secretsByLength) {
    output = output.split(secret).join(redacted);
  }
  output = output.replace(authorizationHeaderShape, `$1${redacted}`);
  return output.replace(absoluteUrl, (raw) => {
    const withoutUserInfo = raw.replace(urlUserInfo, '$1');
    const query = withoutUserInfo.indexOf('?');
    if (query !== -1) {
      return withoutUserInfo.slice(query + 1) === redacted
        ? withoutUserInfo
        : `${withoutUserInfo.slice(0, query)}?${redacted}`;
    }
    const fragment = withoutUserInfo.indexOf('#');
    const tail = withoutUserInfo.slice(fragment + 1);
    if (fragment === -1 || tail.length === 0 || tail === redacted) {
      return withoutUserInfo;
    }
    return `${withoutUserInfo.slice(0, fragment)}#${redacted}`;
  });
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
