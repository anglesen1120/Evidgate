// 04.3.2-T1.3: `variant_hash` — the reporter's port of the server's catalog normaliser and
// digest. The server is the source of truth:
//   src/Modules/Evidgate.Modules.Catalog/Domain/VariantNormalizer.cs (Normalize/Scalar/StringValue)
//   src/Modules/Evidgate.Modules.Catalog/Domain/TextRules.cs (IsClean)
//   src/BuildingBlocks/SharedKernel/Security/SecretShape.cs (KeyLooksSecret/ValueLooksSecret/Entropy)
//   src/BuildingBlocks/Canonicalization/CanonicalHash.cs + JsonCanonicalizer.cs (RFC 8785 digest)
//
// The reporter must not import @evidgate/cli at runtime (the packages/adapter-playwright/src/uuidv7.ts
// precedent), so the pinned `canonicalize` 5.1.0 package is used directly — exactly the library
// packages/cli/src/canonical.ts wraps — and the `sha256:<lowercase hex>` wrapper is taken over its
// output. `fixtures/variant-hash/vectors.json` is the shared corpus the .NET suite
// (tests/Tenancy.IntegrationTests/VariantGoldenVectorTests.cs) asserts with the same expectations.
//
// The normalisation rules and the digest are ported exactly. Two things cannot be, because the
// reporter is handed a JavaScript value where the server reads the author's JSON text:
//
//   * `too_large` is measured the way the server measures it — the UTF-8 byte count of
//     `JsonSerializer.Serialize(result)` under the default `JavaScriptEncoder`, not of the
//     canonical form — so `serverSerializedLength` reproduces that serialisation (escaping and
//     number notation included). The escaping rule is exact; the number notation is exact for
//     every value the reporter accepts (see `dotnetDoubleLength`).
//   * A number's spelling is already lost: Playwright hands over a `number`, so `1e20` and
//     `100000000000000000000` are the same value. The server branches on the raw text
//     (`VariantNormalizer.Scalar`) — an exponent or a fraction means a `double`, plain digits mean
//     a `long` that must fit `MaxExactInteger` — while the reporter can only branch on
//     `String(value)`. Three spellings then disagree, each verified against the server:
//       - a value in [2^53, 1e21) written with an exponent or a fraction (`9.1e15`, `1e16`, `1e20`):
//         the server stores a `double` and accepts, the reporter sees plain digits past
//         `MaxExactInteger` and refuses;
//       - a value of 1e21 or more written in full digits (`1000000000000000000000`): the server's
//         `long` overflows and it refuses, the reporter sees `1e+21` and accepts;
//       - `-0.0` against `-0`: the server writes `-0` for the `double` and `0` for the `long`, the
//         reporter cannot tell them apart and writes `0`; the hash is identical, the length differs
//         by one byte.
//     The corpus records only what both sides agree on.
//
// Input is untrusted: only own enumerable properties are read (never a prototype lookup) and the
// normalised object is built with a null prototype, so a `__proto__`/`constructor` key can neither
// pollute Object.prototype nor be mistaken for an inherited member.
import { createHash } from 'node:crypto';

import canonicalize from 'canonicalize';

/** One refusal of a parameter set: where and why. */
export interface VariantProblem {
  /** JSON-pointer-like path inside the parameters (`/params/api_token`). */
  path: string;
  /** A short token (`secret_key`, `secret_value`, `not_scalar`, `invalid_key`, …). */
  reason: string;
}

/**
 * The normalised parameters and their content-addressed identity, or every refusal.
 * `hash` is `sha256:<lowercase hex>` over the RFC 8785 canonical form of the normalised
 * parameters; `paramsJson` is that canonical text.
 */
export type VariantResult =
  { ok: true; hash: string; paramsJson: string } | { ok: false; problems: VariantProblem[] };

/** Keys allowed per variant (VariantNormalizer.MaxKeys). */
const MAX_KEYS = 32;

/** The largest integer a double (and so RFC 8785) represents exactly: 2^53 − 1. */
const MAX_EXACT_INTEGER = 9_007_199_254_740_991;

/** The database refuses larger parameter sets (`variant_params_check`); measured as the server does. */
const MAX_PARAMS_BYTES = 8000;

const MAX_STRING_CHARS = 256;
const MAX_ARRAY_ITEMS = 32;

/** The path prefix used in problems, matching the server's `/params` base path. */
const BASE_PATH = '/params';

/** Keys that describe a variant for humans and say nothing about what it runs: dropped before hashing. */
const NON_IDENTIFYING: Record<string, true> = {
  description: true,
  label: true,
  labels: true,
  note: true,
  notes: true,
  comment: true,
  comments: true,
  title: true,
  tags: true,
  display_name: true,
  displayname: true,
};

/** Keys whose string values are case-insensitive enum-like names: lower-cased and trimmed before hashing. */
const ENUM_LIKE: Record<string, true> = {
  browser: true,
  engine: true,
  platform: true,
  os: true,
  mode: true,
  env: true,
  environment: true,
  channel: true,
  device: true,
  locale: true,
  viewport: true,
};

/** VariantNormalizer.KeyShape: `^[A-Za-z_][A-Za-z0-9_.-]{0,63}$`, culture-invariant. */
const KEY_SHAPE = /^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/;

/** TextRules.IsClean: control (Cc) and format (Cf) characters are refused, as is a lone surrogate. */
const UNCLEAN = /[\p{Cc}\p{Cf}]/u;

const LETTER_OR_DIGIT = /[A-Za-z0-9]/;
const UPPER = /[A-Z]/;
const LOWER = /[a-z]/;

/** SecretShape.SecretWords (compared case-insensitively). */
const SECRET_WORDS: Record<string, true> = {
  token: true,
  tokens: true,
  password: true,
  passwd: true,
  pwd: true,
  secret: true,
  secrets: true,
  key: true,
  keys: true,
  credential: true,
  credentials: true,
  bearer: true,
  apikey: true,
  authorization: true,
  passphrase: true,
  pass: true,
  jwt: true,
  dsn: true,
  signature: true,
  otp: true,
  pat: true,
  cert: true,
};

/** SecretShape.KeyLooksSecret: compound spellings without a separator. */
const SECRET_FLAT_MARKERS = [
  'apikey',
  'privatekey',
  'secretkey',
  'accesskey',
  'sshkey',
  'authkey',
  'licensekey',
  'passwd',
  'password',
  'secret',
  'token',
  'credential',
];

/** SecretShape.TokenPrefixes (matched case-sensitively, ordinal). */
const TOKEN_PREFIXES = [
  'ghp_',
  'gho_',
  'ghu_',
  'ghs_',
  'ghr_',
  'github_pat_',
  'glpat-',
  'xoxb-',
  'xoxp-',
  'xoxa-',
  'xoxs-',
  'sk-',
  'sk_live_',
  'sk_test_',
  'pk_live_',
  'rk_live_',
  'AKIA',
  'ASIA',
  'AIza',
  'egt_',
  'evg_',
  'npm_',
  'pypi-',
  'dop_v1_',
  'shpat_',
];

/** SecretShape.EmbeddedMarkers (compared case-insensitively). */
const EMBEDDED_MARKERS = [
  'password=',
  'passwd=',
  'pwd=',
  'secret=',
  'token=',
  'apikey=',
  'api_key=',
  'api-key=',
  'access_token',
  'client_secret',
  'x-amz-signature',
  'x-amz-credential',
  'sig=',
  'token ',
  'bearer ',
];

const JWT = /^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]*)?$/;
const URL_CREDENTIALS = /^[a-z][a-z0-9+.-]*:\/\/[^/\s:@]+:[^/\s@]+@/i;
const HEX_RUN = /^[0-9a-fA-F]+$/;
const RANDOM_RUN = /^[A-Za-z0-9+/=_-]+$/;
const UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const HAS_DIGIT = /[0-9]/;
/** `ContainsAnyInRange('A', 'z')`: the code-unit range A(65)..z(122), not a letter class. */
const IN_A_TO_Z_RANGE = /[A-z]/;

/** True when `text` is well-formed UTF-16 (no unpaired surrogate), as `Rune.TryGetRuneAt` requires. */
function isWellFormed(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = text.charCodeAt(i + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
      i++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}

/** TextRules.IsClean: non-null, well-formed and free of control and format characters. */
function isClean(text: string): boolean {
  return isWellFormed(text) && !UNCLEAN.test(text);
}

/**
 * VariantNormalizer.KeyShape().IsMatch. The trailing `$` of a JavaScript regex also matches
 * before a final line terminator, so the match must span the whole key (a key ending in
 * U+2028/U+2029 is not a control or format character and would otherwise slip through).
 */
function keyShapeOk(key: string): boolean {
  const match = KEY_SHAPE.exec(key);
  return match !== null && match[0].length === key.length;
}

/** SecretShape.Words: split on non-alphanumerics and camelCase boundaries. */
function words(key: string): string[] {
  const out: string[] = [];
  let word = '';
  for (let i = 0; i < key.length; i++) {
    const c = key[i]!;
    if (!LETTER_OR_DIGIT.test(c)) {
      if (word.length > 0) {
        out.push(word);
        word = '';
      }
      continue;
    }
    if (word.length > 0 && UPPER.test(c) && LOWER.test(key[i - 1]!)) {
      out.push(word);
      word = '';
    }
    word += c;
  }
  if (word.length > 0) out.push(word);
  return out;
}

/** SecretShape.KeyLooksSecret. */
function keyLooksSecret(key: string): boolean {
  for (const word of words(key)) {
    if (SECRET_WORDS[word.toLowerCase()] === true) return true;
  }
  const flat = key.toLowerCase();
  return SECRET_FLAT_MARKERS.some((marker) => flat.includes(marker));
}

/** SecretShape.Entropy: Shannon entropy in bits per UTF-16 code unit. */
function entropy(text: string): number {
  const counts = new Map<number, number>();
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    counts.set(code, (counts.get(code) ?? 0) + 1);
  }
  let bits = 0;
  for (const count of counts.values()) {
    const p = count / text.length;
    bits -= p * Math.log2(p);
  }
  return bits;
}

/** SecretShape.ValueLooksSecret. */
function valueLooksSecret(value: string): boolean {
  const trimmed = value.trim();
  if (trimmed.length < 8) return false;

  const lower = trimmed.toLowerCase();
  if (
    lower.startsWith('bearer ') ||
    lower.startsWith('basic ') ||
    trimmed.includes('-----BEGIN ')
  ) {
    return true;
  }

  for (const prefix of TOKEN_PREFIXES) {
    if (trimmed.startsWith(prefix) && trimmed.length >= prefix.length + 8) return true;
  }

  if (JWT.test(trimmed) || URL_CREDENTIALS.test(trimmed)) return true;

  for (const marker of EMBEDDED_MARKERS) {
    if (lower.includes(marker)) return true;
  }

  // A bare long hex run is a key however its entropy turns out; a UUID is an id.
  if (trimmed.length >= 32 && HEX_RUN.test(trimmed)) return true;

  return (
    trimmed.length >= 24 &&
    HAS_DIGIT.test(trimmed) &&
    IN_A_TO_Z_RANGE.test(trimmed) &&
    RANDOM_RUN.test(trimmed) &&
    !UUID.test(trimmed) &&
    entropy(trimmed) >= 3.7
  );
}

/**
 * VariantNormalizer.Scalar's number branch. Canonical JSON hashes numbers as IEEE doubles, so an
 * integer a double cannot hold exactly is refused. The reporter receives a JavaScript number, not
 * its source spelling, so `String(value)` (the same shortest round-trip form `JSON.stringify`
 * emits) stands in for the raw text: a value whose shortest form has no `.`/`e`/`E` must be a safe
 * integer, and anything else must be finite.
 */
function normalizeNumber(
  value: number,
  path: string,
  problems: VariantProblem[],
): number | undefined {
  if (!Number.isFinite(value)) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  if (!/[.eE]/.test(String(value)) && Math.abs(value) > MAX_EXACT_INTEGER) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  return value;
}

/** VariantNormalizer.StringValue. */
function normalizeString(
  text: string,
  key: string,
  path: string,
  problems: VariantProblem[],
): string | undefined {
  if (text.length > MAX_STRING_CHARS || !isClean(text)) {
    problems.push({ path, reason: 'invalid_value' });
    return undefined;
  }
  if (valueLooksSecret(text)) {
    problems.push({ path, reason: 'secret_value' });
    return undefined;
  }
  return ENUM_LIKE[key.toLowerCase()] === true ? text.trim().toLowerCase() : text;
}

/** VariantNormalizer.Scalar's array branch: at most 32 scalars, nulls dropped, containers refused. */
function normalizeArray(
  value: readonly unknown[],
  key: string,
  path: string,
  problems: VariantProblem[],
): unknown[] {
  if (value.length > MAX_ARRAY_ITEMS) {
    problems.push({ path, reason: 'too_many_items' });
    return [];
  }
  const array: unknown[] = [];
  let index = 0;
  for (const item of value) {
    if (item !== null && typeof item === 'object') {
      problems.push({ path: `${path}/${index}`, reason: 'not_scalar' });
    } else if (item !== null) {
      array.push(normalizeScalar(item, key, `${path}/${index}`, problems));
    }
    index++;
  }
  return array;
}

/** VariantNormalizer.Scalar: a scalar value, or undefined when it is dropped or refused. */
function normalizeScalar(
  value: unknown,
  key: string,
  path: string,
  problems: VariantProblem[],
): unknown {
  if (value === null) return undefined; // null is the default: dropped.
  switch (typeof value) {
    case 'boolean':
      return value;
    case 'number':
      return normalizeNumber(value, path, problems);
    case 'string':
      return normalizeString(value, key, path, problems);
    case 'object':
      if (Array.isArray(value)) return normalizeArray(value, key, path, problems);
      problems.push({ path, reason: 'not_scalar' });
      return undefined;
    default:
      // undefined, function, symbol, bigint: no JSON scalar the server could have seen.
      problems.push({ path, reason: 'not_scalar' });
      return undefined;
  }
}

/**
 * The printable ASCII characters the default `JavaScriptEncoder` writes as `\uXXXX` (six bytes)
 * instead of themselves; every other printable ASCII character is written as itself. `\` is the
 * one exception: it is written as `\\` (two bytes). Measured against `JsonSerializer.Serialize`
 * on .NET 10 (`System.Text.Encodings.Web`).
 */
const ENCODER_ESCAPED_ASCII: Record<string, true> = {
  '"': true,
  '&': true,
  "'": true,
  '+': true,
  '<': true,
  '>': true,
  '`': true,
};

/**
 * The length `JsonSerializer.Serialize` gives a string under the default encoder: the two quotes
 * plus, per code point, six bytes for an escaped character (`\uXXXX`), twelve for an astral
 * character (a surrogate pair, so two escapes), two for `\`, and one otherwise. The serialised
 * text is pure ASCII, so its length is its UTF-8 byte count.
 */
function encodedStringLength(text: string): number {
  let length = 2;
  for (const character of text) {
    const codePoint = character.codePointAt(0) as number;
    if (codePoint < 0x20) {
      // Control characters never survive `isClean`; kept so the rule is complete.
      length +=
        character === '\b' ||
        character === '\f' ||
        character === '\n' ||
        character === '\r' ||
        character === '\t'
          ? 2
          : 6;
    } else if (codePoint < 0x7f) {
      length += character === '\\' ? 2 : ENCODER_ESCAPED_ASCII[character] === true ? 6 : 1;
    } else {
      length += codePoint <= 0xffff ? 6 : 12;
    }
  }
  return length;
}

/**
 * The length of `JsonSerializer.Serialize(JsonValue.Create(double))`: the shortest round-trip
 * digits `String(value)` also produces, in .NET's `G17` notation — scientific when the decimal
 * exponent is below −4 or at least 17, uppercase `E`, an explicit sign and at least two exponent
 * digits. Only the notation differs from ECMAScript, which switches to scientific below −6 or at
 * least 21 and writes a lowercase `e` with no leading zero, so `1e-7` is four characters where
 * .NET writes `1E-07`, five.
 */
function dotnetDoubleLength(value: number): number {
  const [mantissa, exponentText] = Math.abs(value).toExponential().split('e');
  const exponent = Number(exponentText);
  const digits = (mantissa as string).replace('.', '');
  const sign = value < 0 ? 1 : 0;
  if (exponent < -4 || exponent >= 17) {
    const head = digits.length === 1 ? digits : `${digits[0]}.${digits.slice(1)}`;
    return sign + head.length + 2 + String(Math.abs(exponent)).padStart(2, '0').length;
  }
  if (exponent < 0) return sign + 2 + -exponent - 1 + digits.length;
  return sign + (digits.length > exponent + 1 ? digits.length + 1 : exponent + 1);
}

/**
 * The length `JsonSerializer.Serialize` gives the number the server stored for `value`
 * (`VariantNormalizer.Scalar`): a `long` written as its decimal digits, or a `double` written by
 * `dotnetDoubleLength`. The reporter has no raw text, so it branches on `String(value)` — the
 * shortest round-trip form `JSON.stringify` emits — exactly as the server branches on the raw
 * text; the spellings where that stands in for the raw text incorrectly are listed in the file
 * header.
 */
function serverNumberLength(value: number): number {
  const text = String(value);
  if (!/[.eE]/.test(text)) return text.length;
  return dotnetDoubleLength(value);
}

/**
 * The byte count the server measures for `too_large`: `Encoding.UTF8.GetByteCount` of
 * `JsonSerializer.Serialize(result)`, where `result` is the `SortedDictionary<string, JsonNode?>`
 * of normalised parameters. Keys are ASCII-only (`KeyShape`) and sorted ordinal, which the default
 * `Array.prototype.sort` order matches; the serialised text is pure ASCII, so its length is its
 * UTF-8 byte count.
 */
function serverSerializedLength(value: unknown): number {
  if (typeof value === 'string') return encodedStringLength(value);
  if (typeof value === 'number') return serverNumberLength(value);
  if (typeof value === 'boolean') return value ? 4 : 5;
  if (Array.isArray(value)) {
    let length = 2;
    for (const [index, item] of value.entries()) {
      if (index > 0) length += 1;
      length += serverSerializedLength(item);
    }
    return length;
  }
  const record = value as Record<string, unknown>;
  let length = 2;
  for (const [index, key] of Object.keys(record).sort().entries()) {
    if (index > 0) length += 1;
    length += encodedStringLength(key) + 1 + serverSerializedLength(record[key]);
  }
  return length;
}

/**
 * Normalises variant parameters into a content-addressed identity, mirroring
 * `VariantNormalizer.Normalize` exactly: identical parameters after normalisation — key order,
 * null defaults, the case of enum-like values, non-identifying keys — map to the same variant, and
 * a parameter set that carries a secret-shaped key or value never becomes one. Parameters are flat:
 * scalars and short arrays of scalars (nested objects are refused so a secret cannot hide one level
 * down). Pure and I/O-free.
 *
 * @param params The Playwright project `metadata.evidgate.params` object, or null/undefined for none.
 */
export function normalizeVariant(params: unknown): VariantResult {
  const problems: VariantProblem[] = [];
  const normalised: Record<string, unknown> = Object.create(null) as Record<string, unknown>;

  if (params !== null && params !== undefined) {
    if (typeof params !== 'object' || Array.isArray(params)) {
      problems.push({ path: BASE_PATH, reason: 'not_object' });
      return { ok: false, problems };
    }

    let count = 0;
    for (const [name, value] of Object.entries(params)) {
      count++;
      if (count > MAX_KEYS) {
        problems.push({ path: BASE_PATH, reason: 'too_many_keys' });
        break;
      }

      // A rejected key is named by its position, never echoed: it may itself be the secret.
      const positionPath = `${BASE_PATH}/#${count}`;
      if (!keyShapeOk(name) || !isClean(name)) {
        problems.push({ path: positionPath, reason: 'invalid_key' });
        continue;
      }
      if (keyLooksSecret(name) || valueLooksSecret(name)) {
        problems.push({ path: positionPath, reason: 'secret_key' });
        continue;
      }

      const path = `${BASE_PATH}/${name}`;
      if (name.startsWith('_') || NON_IDENTIFYING[name.toLowerCase()] === true) {
        continue;
      }

      const scalar = normalizeScalar(value, name, path, problems);
      if (scalar !== undefined) {
        normalised[name] = scalar;
      }
    }
  }

  if (problems.length > 0) {
    return { ok: false, problems };
  }

  // The server measures the serialised parameters, not the canonical form, and checks the size
  // before it canonicalises; both are kept so a refusal matches.
  if (serverSerializedLength(normalised) > MAX_PARAMS_BYTES) {
    problems.push({ path: BASE_PATH, reason: 'too_large' });
    return { ok: false, problems };
  }

  const paramsJson = canonicalize(normalised);
  if (paramsJson === undefined) {
    problems.push({ path: BASE_PATH, reason: 'invalid_value' });
    return { ok: false, problems };
  }

  const hash = `sha256:${createHash('sha256').update(paramsJson, 'utf8').digest('hex')}`;
  return { ok: true, hash, paramsJson };
}
