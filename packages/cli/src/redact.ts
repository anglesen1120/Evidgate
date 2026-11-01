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
 * Percent-encoded spellings, one pattern per secret. A literal form cannot cover the hex-case
 * combinations a caller may produce, and a pattern matches all of them in one pass.
 */
let secretPatterns: readonly RegExp[] = [];

/**
 * Length a derived form needs before it is registered on length alone. The base64 of a
 * one-character secret is two characters of ordinary text, and stripping it corrupts output
 * that never held a secret, so a shorter form is registered only when its punctuation makes
 * it unmistakably encoded.
 */
const minDerivedFormLength = 8;

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

/**
 * Character codes rather than characters: the authority scan compares one code per position
 * and never slices a string or runs a regex, so a megabyte of `%2f%2f` costs one pass.
 */
const slashCode = '/'.charCodeAt(0);
const colonCode = ':'.charCodeAt(0);
const atCode = '@'.charCodeAt(0);
const spaceCode = ' '.charCodeAt(0);
const percentCode = '%'.charCodeAt(0);
const backslashCode = '\\'.charCodeAt(0);
const lineFeedCode = '\n'.charCodeAt(0);
const carriageReturnCode = '\r'.charCodeAt(0);
const tabCode = '\t'.charCodeAt(0);
/** Codes that end an authority before its userinfo: the path, the query, the wrapping text. */
const authorityStopCodes: ReadonlySet<number> = new Set(
  ['/', '?', '#', '"', "'", '<', '>', '`', '\\', '\n', '\r', '\t'].map((char) =>
    char.charCodeAt(0),
  ),
);
/**
 * Codes that end a userinfo that already has its colon. A quote, `?` or `#` is a password
 * character (`pa'ss`, `pa#ss`, `pa?ss` are real passwords), so an authority may not end there.
 */
const passwordStopCodes: ReadonlySet<number> = new Set([
  slashCode,
  backslashCode,
  lineFeedCode,
  carriageReturnCode,
  tabCode,
]);
/**
 * A userinfo longer than this is prose: no token, JWT or password reaches this size, and the
 * bound keeps the scan linear on a hostile line. It is generous because a 600-character JWT
 * used as a password is still a credential that must not print.
 */
const maxAuthorityLength = 4096;
/** A host name, the only thing a port can follow. */
const hostNameShape = /^[A-Za-z0-9._-]+$/;
/**
 * A port whose query follows is prose (`a.test:8080?x@y`), not `user:password`. A `/` cannot
 * appear here: the scan ends the run at a path, so a port closed by the `@` is a numeric
 * password and is not matched.
 */
const portQueryShape = /^\d{1,5}[?#]/;
/** A port and the space after it: `8080 and …`, `3000, foo …`. */
const portDigitsShape = /^\d+[^\w@]*\s+/;

/**
 * Registers a secret value and the forms it can take in an error message: the value itself,
 * its trimmed, JSON-escaped and control-character-escaped spellings, its percent-encoded
 * spelling (every hex escape in either case), and its base64 spellings. The escaped form
 * matters because the message is escaped before the redacting writer sees it. Empty forms are
 * skipped; the value, trimmed and JSON forms are registered at any length, because the value
 * is a known secret even when it is one character long.
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
  const pattern = percentEscapePattern(value);
  if (pattern !== undefined && !secretPatterns.some((known) => known.source === pattern.source)) {
    secretPatterns = [...secretPatterns, pattern];
    added = true;
  }
  if (added) {
    secretsByLength = [...registeredSecrets].sort((a, b) => b.length - a.length);
  }
}

/** Every spelling of one secret that a message can carry. */
function secretForms(value: string): string[] {
  const trimmed = value.trim();
  const forms = [
    value,
    trimmed,
    JSON.stringify(value).slice(1, -1),
    escapeControlCharacters(value),
  ];
  forms.push(...base64Forms(value));
  if (trimmed !== value) {
    forms.push(...base64Forms(trimmed));
  }
  return forms;
}

/** The value of one hex digit, or `-1` where the code is not one. */
function hexDigit(code: number): number {
  if (code >= 0x30 && code <= 0x39) {
    return code - 0x30;
  }
  if (code >= 0x41 && code <= 0x46) {
    return code - 0x41 + 10;
  }
  if (code >= 0x61 && code <= 0x66) {
    return code - 0x61 + 10;
  }
  return -1;
}

/**
 * The percent-encoded spelling of a secret with every escape's hex digits accepted in either
 * case, so `qa%2Fa%3ab` and `qa%2fa%3Ab` both match `qa/a:b`. A literal form cannot cover
 * that: one secret with three escapes has eight case spellings, and a caller may spell any of
 * them. Returns `undefined` when the encoded spelling holds no escape, where the raw form
 * already matches.
 */
function percentEscapePattern(value: string): RegExp | undefined {
  const encoded = encodeURIComponent(value);
  let source = '';
  let escapes = 0;
  let index = 0;
  while (index < encoded.length) {
    const escape = encoded.slice(index, index + 3);
    const high = hexDigit(escape.charCodeAt(1));
    const low = hexDigit(escape.charCodeAt(2));
    if (escape.startsWith('%') && high !== -1 && low !== -1) {
      source += `%${hexCaseClass(escape.charAt(1))}${hexCaseClass(escape.charAt(2))}`;
      escapes += 1;
      index += 3;
    } else {
      source += escapeRegExp(encoded.charAt(index));
      index += 1;
    }
  }
  return escapes === 0 ? undefined : new RegExp(source, 'g');
}

/** One hex digit of a percent escape as a class that accepts either case. */
function hexCaseClass(digit: string): string {
  return /^\d$/.test(digit) ? digit : `[${digit.toLowerCase()}${digit.toUpperCase()}]`;
}

/** A string usable inside a regular expression. */
function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * The base64 spellings of a secret: the standard and the url-safe alphabet, padded and
 * unpadded. Only the whole value can be encoded exactly — matching a secret at an unknown
 * offset inside a larger blob needs bytes the CLI does not have, so it is not attempted.
 * A form shorter than `minDerivedFormLength` is kept only when it carries a character outside
 * `[A-Za-z0-9]`: the unpadded encoding of a one-character secret is two letters of ordinary
 * text (`eA` is the whole encoding of `x`), and stripping that corrupts unrelated output.
 */
function base64Forms(value: string): string[] {
  const standard = Buffer.from(value, 'utf8').toString('base64');
  const urlSafe = standard.replace(/\+/g, '-').replace(/\//g, '_');
  const forms = [standard, standard.replace(/=+$/, '')];
  if (urlSafe !== standard) {
    forms.push(urlSafe, urlSafe.replace(/=+$/, ''));
  }
  return forms.filter((form) => form.length >= minDerivedFormLength || /[^A-Za-z0-9]/.test(form));
}

/** Clears the registry — `main` calls it at entry, specs between cases. */
export function resetSecrets(): void {
  registeredSecrets.clear();
  secretPatterns = [];
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
  for (const pattern of secretPatterns) {
    output = output.replace(pattern, redacted);
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
    // A match inside a run that was already stripped cannot open a second userinfo.
    if (start < copied) {
      continue;
    }
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
 * Where the userinfo of one authority ends, or `-1` when the run holds none.
 *
 * The last `@` before the first `/` closes it, because a password may hold one. Once the run
 * has its colon, only a slash, a backslash or a line break ends it — a quote, `?` or `#` is a
 * password character (`pa'ss`, `pa?ss` and `pa#ss` are real passwords). A space is a password
 * character too, but prose uses that shape, so a run with a space is accepted only for a bare
 * username whose colon is not a port followed by words (`host:8080 and user@mail` is prose).
 */
function userInfoEnd(text: string, start: number): number {
  const limit = Math.min(text.length, start + maxAuthorityLength);
  let index = start;
  let colon = -1;
  let at = -1;
  let end = -1;
  let spaces = 0;
  while (index < limit) {
    const code = text.charCodeAt(index);
    let decoded = code;
    let width = 1;
    if (code === percentCode) {
      const high = hexDigit(text.charCodeAt(index + 1));
      const low = hexDigit(text.charCodeAt(index + 2));
      if (high !== -1 && low !== -1) {
        decoded = high * 16 + low;
        width = 3;
      }
    }
    if (decoded === atCode) {
      at = index;
      end = index + width;
    } else if (authorityStopCodes.has(decoded)) {
      if (colon === -1 || end !== -1 || passwordStopCodes.has(decoded)) {
        break;
      }
    } else if (decoded === colonCode) {
      colon = colon === -1 ? index + width : colon;
    } else if (decoded === spaceCode) {
      // A username never holds a space, and a space after the `@` belongs to the prose.
      if (colon === -1 || end !== -1) {
        break;
      }
      spaces += 1;
    }
    index += width;
  }
  if (end === -1) {
    return -1;
  }
  const accepted =
    spaces === 0 ? !isHostPort(text, start, colon, at) : isSpacedPassword(text, start, colon, at);
  return accepted ? end : -1;
}

/**
 * `a.test:8080?x` reads as a host and a port, not as `user:password`. Digits that the `@`
 * closes are a numeric password (`svc:123@db`), not a port, and a name with no host shape
 * (`u:pw`) is a credential whatever follows it.
 */
function isHostPort(text: string, start: number, colon: number, at: number): boolean {
  if (colon === -1 || !hostNameShape.test(text.slice(start, colon - 1))) {
    return false;
  }
  return portQueryShape.test(text.slice(colon, at));
}

/**
 * Whether a run that holds a space is a password rather than prose. A dotted name or
 * `localhost` before the colon is a host (`docs.test: contact@example.com`), a space right
 * after the colon starts prose, and a port followed by words is a host and a port
 * (`host:8080 and user@mail`). Residue: prose of the shape `host:1 2 admin@corp.test` is
 * stripped, because it is indistinguishable from a numeric password with a space.
 */
function isSpacedPassword(text: string, start: number, colon: number, at: number): boolean {
  if (colon === -1) {
    return false;
  }
  const name = text.slice(start, colon - 1);
  if (name.includes('.') || name.toLowerCase() === 'localhost') {
    return false;
  }
  const rest = text.slice(colon, at);
  if (rest.startsWith(' ')) {
    return false;
  }
  const port = portDigitsShape.exec(rest);
  return port === null || !/[A-Za-z]/.test(rest.slice(port[0].length));
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
