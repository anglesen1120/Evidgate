// The secret-shape and serialised-size rules `VariantNormalizer` applies, mirrored so a CLI
// mirror of the reporter's normaliser refuses the same parameter sets. `VariantHash` next door
// holds the key and scalar rules; both must stay in step with
// `packages/adapter-playwright/src/variantSecrets.ts` and `variantSerializedLength.ts`.
const LETTER_OR_DIGIT = /[A-Za-z0-9]/;
const UPPER = /[A-Z]/;
const LOWER = /[a-z]/;

/** SecretShape.SecretWords, compared case-insensitively. */
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

/** SecretShape.TokenPrefixes, matched case-sensitively. */
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

/** SecretShape.EmbeddedMarkers, compared case-insensitively. */
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
/** `ContainsAnyInRange('A', 'z')` is a code-unit range, not a letter class. */
const IN_A_TO_Z_RANGE = /[A-z]/;

/** SecretShape.Words: split on non-alphanumerics and camelCase boundaries. */
function words(key: string): string[] {
  const out: string[] = [];
  let word = '';
  for (let i = 0; i < key.length; i++) {
    const character = key[i] as string;
    if (!LETTER_OR_DIGIT.test(character)) {
      if (word.length > 0) {
        out.push(word);
        word = '';
      }
      continue;
    }
    if (word.length > 0 && UPPER.test(character) && LOWER.test(key[i - 1] as string)) {
      out.push(word);
      word = '';
    }
    word += character;
  }
  if (word.length > 0) out.push(word);
  return out;
}

/** SecretShape.KeyLooksSecret: a secret-shaped name means the parameter is a credential. */
export function keyLooksSecret(key: string): boolean {
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

/** SecretShape.ValueLooksSecret: a secret-shaped value means a credential was pasted in. */
export function valueLooksSecret(value: string): boolean {
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

/** Printable ASCII the default `JavaScriptEncoder` writes as `\uXXXX` (six bytes). */
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
 * The length `JsonSerializer.Serialize` gives a string under the default encoder: quotes, six
 * bytes per escaped character, twelve per astral character, two per backslash. The serialised
 * text is ASCII, so its length is its UTF-8 byte count.
 */
function encodedStringLength(text: string): number {
  let length = 2;
  for (const character of text) {
    const codePoint = character.codePointAt(0) as number;
    if (codePoint < 0x20) {
      length += ['\b', '\f', '\n', '\r', '\t'].includes(character) ? 2 : 6;
    } else if (codePoint < 0x7f) {
      length += character === '\\' ? 2 : ENCODER_ESCAPED_ASCII[character] === true ? 6 : 1;
    } else {
      length += codePoint <= 0xffff ? 6 : 12;
    }
  }
  return length;
}

/**
 * The length .NET's `G17` notation gives a double: scientific below −4 or at least 17, an
 * uppercase `E`, an explicit sign and at least two exponent digits — not ECMAScript's spelling.
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

/** The length of the number the server stored for `value`: `long` digits or a `double`. */
function serverNumberLength(value: number): number {
  const text = String(value);
  if (!/[.eE]/.test(text)) return text.length;
  return dotnetDoubleLength(value);
}

/**
 * The byte count the server measures for `too_large`: `Encoding.UTF8.GetByteCount` of
 * `JsonSerializer.Serialize` over the sorted normalised parameters, not the canonical form.
 */
export function serverSerializedLength(value: unknown): number {
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
  const keys = Object.keys(record).sort();
  for (const [index, key] of keys.entries()) {
    if (index > 0) length += 1;
    length += encodedStringLength(key) + 1 + serverSerializedLength(record[key]);
  }
  return length;
}
