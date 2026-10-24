// 09.1.1-T3 review F15 (00 §9.1; ADR-0008 rule 2): the only UUID source for `X-Request-Id` and
// `Idempotency-Key`. `crypto.randomUUID()` exists only in a secure context — served over plain
// http on a non-localhost host the member is absent and calling it throws a `TypeError` — so
// `newUuid()` falls back to the same 122 random bits assembled from `crypto.getRandomValues`.
// It never degrades to `Math.random`: a guessable idempotency key would collide across clients
// and could replay another caller's mutation.

const UUID_BYTE_LENGTH = 16;
const VERSION_BYTE_INDEX = 6;
const VARIANT_BYTE_INDEX = 8;
const VERSION_4_NIBBLE = 0x40;
const RFC_4122_VARIANT_BITS = 0x80;

function hexByte(byte: number): string {
  return byte.toString(16).padStart(2, '0');
}

/** One byte as two lower-case hex digits, with the v4 version nibble and variant bits applied. */
function uuidV4HexByte(byte: number, index: number): string {
  if (index === VERSION_BYTE_INDEX) {
    return hexByte((byte & 0x0f) | VERSION_4_NIBBLE);
  }
  if (index === VARIANT_BYTE_INDEX) {
    return hexByte((byte & 0x3f) | RFC_4122_VARIANT_BITS);
  }
  return hexByte(byte);
}

/** RFC 4122 version 4 wire form: lower-case hex, `8-4-4-4-12`. */
function toUuidV4(bytes: Uint8Array): string {
  const hex = Array.from(bytes, uuidV4HexByte).join('');
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join('-');
}

/**
 * A fresh v4 UUID: `crypto.randomUUID()` in a secure context, otherwise `crypto.getRandomValues`
 * with the version and variant bits set by hand. Throws when the context exposes neither member
 * — a silent weak fallback would break the uniqueness the server's idempotency store relies on.
 */
export function newUuid(): string {
  // Deliberately widened: the type says `crypto` always exists, but a pre-Web-Crypto engine has
  // no object at all and a non-secure context has no `randomUUID`.
  const webCrypto: Crypto | undefined = globalThis.crypto;
  if (webCrypto !== undefined && typeof webCrypto.randomUUID === 'function') {
    return webCrypto.randomUUID();
  }
  if (webCrypto !== undefined && typeof webCrypto.getRandomValues === 'function') {
    return toUuidV4(webCrypto.getRandomValues(new Uint8Array(UUID_BYTE_LENGTH)));
  }
  throw new Error(
    'newUuid: this context exposes neither crypto.randomUUID nor crypto.getRandomValues, so a ' +
      'cryptographically random id cannot be minted. The web app requires Web Crypto.',
  );
}
