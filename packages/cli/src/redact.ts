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

const authorizationHeader = /(authorization\s*:\s*)[^\r\n]*/gi;

/**
 * An absolute http(s) URL. The character class stops at whitespace, quotes, angle
 * brackets and the usual closing punctuation, so wrapping text is preserved.
 */
const absoluteUrl = /\bhttps?:\/\/[^\s"'<>`)\]}]+/gi;
/** `scheme://userinfo@host` — userinfo is a credential pair, never printed. */
const urlUserInfo = /^(https?:\/\/)[^/?#@]+@/i;

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
 * credentials: userinfo is removed, and an http(s) URL with a query loses the whole query
 * because a presigned URL IS a bearer credential whose signature travels in it. Naming the
 * parameters to strip would leak the ones a provider names differently, so the rule is
 * positional.
 */
export function redact(text: string): string {
  let output = text;
  for (const secret of secretsByLength) {
    output = output.split(secret).join(redacted);
  }
  output = output.replace(authorizationHeader, `$1${redacted}`);
  return output.replace(absoluteUrl, (raw) => {
    const withoutUserInfo = raw.replace(urlUserInfo, '$1');
    const query = withoutUserInfo.indexOf('?');
    return query === -1 ? withoutUserInfo : `${withoutUserInfo.slice(0, query)}?${redacted}`;
  });
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
