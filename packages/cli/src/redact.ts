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
 * The `//` that opens a URL authority, plain or percent-encoded (`%2f%2f`). Credentials are
 * stripped relative to this pair, so every scheme (`postgres:`, a `%3a` spelling, or none at
 * all) is covered by one rule instead of one rule per scheme.
 */
const authorityStart = /(?:\/\/|%2f%2f)/gi;
/** Where an authority ends: the path, the query, the fragment or the wrapping text. */
const authorityStops: Record<string, true> = {
  '/': true,
  '?': true,
  '#': true,
  '"': true,
  "'": true,
  '<': true,
  '>': true,
  '`': true,
  '\\': true,
  '\n': true,
  '\r': true,
  '\t': true,
};
/** A host and its port, which is never userinfo: an `@` after one belongs to the prose. */
const hostPortShape = /^[A-Za-z0-9._-]+:\d+$/;
/** A percent escape inside an authority, decoded so `%3a` and `:` are one case. */
const percentEscapeShape = /^[0-9a-f]{2}$/i;
/** A real userinfo is short; the cap keeps the scan linear on a hostile line. */
const maxAuthorityLength = 512;

/**
 * Registers a secret value and the forms it can take in an error message: the value itself,
 * its trimmed, percent-encoded (either hex case), JSON-escaped, control-character-escaped and
 * base64 spellings. The escaped form matters because the message is escaped before the
 * redacting writer sees it. Empty forms are skipped; a short value is still registered.
 */
export function registerSecret(value: string | undefined): void {
  if (value === undefined || value.length === 0) {
    return;
  }
  let added = false;
  for (const form of secretForms(value)) {
    if (form.length > 0 && !registeredSecrets.has(form)) {
      registeredSecrets.add(form);
      added = true;
    }
  }
  if (added) {
    secretsByLength = [...registeredSecrets].sort((a, b) => b.length - a.length);
  }
}

/** Every spelling of one secret that a message can carry. */
function secretForms(value: string): string[] {
  const trimmed = value.trim();
  const encoded = encodeURIComponent(value);
  const forms = [
    value,
    trimmed,
    encoded,
    encoded.replace(/%[0-9a-f]{2}/gi, (escape) => escape.toLowerCase()),
    JSON.stringify(value).slice(1, -1),
    escapeControlCharacters(value),
    ...base64Forms(value),
  ];
  if (trimmed !== value) {
    forms.push(...base64Forms(trimmed));
  }
  return forms;
}

/**
 * The base64 spellings of a secret: the standard and the url-safe alphabet, padded and
 * unpadded. Only the whole value can be encoded exactly — matching a secret at an unknown
 * offset inside a larger blob needs bytes the CLI does not have, so it is not attempted.
 */
function base64Forms(value: string): string[] {
  const standard = Buffer.from(value, 'utf8').toString('base64');
  const urlSafe = standard.replace(/\+/g, '-').replace(/\//g, '_');
  const forms = [standard, standard.replace(/=+$/, '')];
  if (urlSafe !== standard) {
    forms.push(urlSafe, urlSafe.replace(/=+$/, ''));
  }
  return forms;
}

/** Clears the registry — `main` calls it at entry, specs between cases. */
export function resetSecrets(): void {
  registeredSecrets.clear();
  secretsByLength = [];
}

/**
 * Replaces every registered secret and any `Authorization` header value, then strips URL
 * credentials: userinfo goes from every `scheme://`, `//` and percent-encoded spelling, and an
 * http(s) URL loses its query and fragment because a presigned URL is a bearer credential.
 * A second pass over already-redacted text changes nothing.
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
  return stripUrlCredentials(output).replace(absoluteUrl, redactUrl);
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
 * Redacts one matched URL: everything from the first `?` or `#` on goes, because a presigned
 * URL is a bearer credential whose signature travels in it (an OAuth fragment flow carries a
 * token the same way). Punctuation that belongs to the surrounding text — a closing bracket
 * from wrapping text, or the backslash of a JSON escape — stays outside the placeholder.
 */
function redactUrl(raw: string): string {
  const start = credentialStart(raw);
  if (start === -1) {
    return raw;
  }
  const tail = raw.slice(start + 1);
  if (tail.length === 0) {
    return raw;
  }
  const end = credentialEnd(tail);
  if (tail.slice(0, end) === redacted) {
    return raw;
  }
  return `${raw.slice(0, start + 1)}${redacted}${tail.slice(end)}`;
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

/** One character of a URL authority, decoded when it is written as a percent escape. */
interface AuthorityChar {
  readonly char: string;
  readonly width: number;
}

/**
 * Strips the userinfo of every authority in text: `user:pass@` and its percent-encoded
 * spelling. The scan keys on the `//` alone, so a scheme, a space inside the password and an
 * `@` inside it need no pattern of their own.
 */
function stripUrlCredentials(text: string): string {
  let output = '';
  let copied = 0;
  for (const match of text.matchAll(authorityStart)) {
    const start = match.index + match[0].length;
    const end = userInfoEnd(text, start);
    if (end === -1) {
      continue;
    }
    output += text.slice(copied, start);
    copied = end;
  }
  return copied === 0 ? text : output + text.slice(copied);
}

/**
 * Where the userinfo of one authority ends, or `-1` when the run holds none. `@` is taken
 * greedily because a password may hold one; a space is allowed only after the credential
 * colon and only once, so prose that follows a URL is not swallowed with it.
 */
function userInfoEnd(text: string, start: number): number {
  const limit = Math.min(text.length, start + maxAuthorityLength);
  let index = start;
  let colon = false;
  let spaced = false;
  let terminator = -1;
  while (index < limit) {
    const read = readAuthorityChar(text, index);
    if (read === undefined) {
      break;
    }
    const { char, width } = read;
    if (char === '@') {
      terminator = index + width;
    } else if (char === ':') {
      colon = true;
    } else if (char === ' ') {
      if (terminator !== -1 || spaced || !colon || hostPortShape.test(text.slice(start, index))) {
        break;
      }
      spaced = true;
    }
    index += width;
  }
  return terminator;
}

/** Reads one character of an authority, or `undefined` where the authority ends. */
function readAuthorityChar(text: string, index: number): AuthorityChar | undefined {
  if (text[index] === '%') {
    const hex = text.slice(index + 1, index + 3);
    if (percentEscapeShape.test(hex)) {
      return { char: String.fromCharCode(Number.parseInt(hex, 16)), width: 3 };
    }
  }
  const char = text[index] as string;
  return authorityStops[char] === true ? undefined : { char, width: 1 };
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
