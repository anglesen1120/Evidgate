// The secret-shape rules `VariantNormalizer` refuses on — the port of
// `SharedKernel/Security/SecretShape.cs`. A key and a value are checked separately because they
// fail for different reasons: a secret-shaped *name* means the parameter is a credential, and a
// secret-shaped *value* means a credential was pasted into a value, so both refuse the variant.
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

/** SecretShape.ValueLooksSecret. */
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
