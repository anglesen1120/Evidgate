// The presigned URL and its signed header set, validated before a byte is sent: the PUT is
// self-verifying only when the server signed the length and the digest, and a response may
// never talk the CLI into sending a credential or a second framing.
import { badResponse } from './uploadErrors.js';

/** A lower-cased header name, but only when it is a plain HTTP token: never echoed raw. */
export function headerLabel(lowerName: string): string {
  return /^[a-z0-9!#$%&'*+.^_`|~-]{1,64}$/.test(lowerName) ? lowerName : 'same';
}

/** The presigned URL must be an absolute http(s) URL with no embedded credential. */
export function presignedUrl(raw: string): URL {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw badResponse('the upload-url response is not an absolute URL');
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    throw badResponse('the upload-url response is not an http(s) URL');
  }
  if (url.username !== '' || url.password !== '') {
    throw badResponse('the upload-url response embeds a credential');
  }
  return url;
}

/**
 * Headers a presigned PUT must never carry, whatever the server signs: a credential
 * (`authorization`, `cookie`, `set-cookie`, `proxy-authorization`), the target
 * (`host`), a second framing (`transfer-encoding`, `connection`) — plus any `proxy-*`.
 * The client keeps its own bearer off the object store, and a response is not
 * allowed to talk it into doing otherwise.
 *
 * The transport-owned set (`expect`, `te`, `trailer`, `upgrade`, `keep-alive`,
 * `content-encoding`) is refused for the same reason: `fetch` throws a `TypeError` (or
 * `NotSupported`) on most of them, and a thrown `TypeError` reads as a transport failure —
 * it would be retried until the deadline for a response that will never change. Refusing
 * them here makes them the deterministic `request_failed` they are. `content-encoding` is
 * on the list because the body is streamed raw: a signed encoding would either break the
 * length/checksum framing or make the store decode bytes the CLI never encoded.
 */
const forbiddenSignedHeaders: Readonly<Record<string, true>> = {
  authorization: true,
  cookie: true,
  'set-cookie': true,
  host: true,
  'proxy-authorization': true,
  'transfer-encoding': true,
  connection: true,
  expect: true,
  te: true,
  trailer: true,
  upgrade: true,
  'keep-alive': true,
  'content-encoding': true,
};

/** RFC 9110 `token`: the only shape a header name may have (and what `fetch` accepts). */
const headerNamePattern = /^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,128}$/;
/**
 * The characters a header value may carry, negated: `fetch` (undici's `headerCharRegex`)
 * refuses anything outside tab, printable ASCII and Latin-1, and a value it refuses throws a
 * `TypeError`. That throw would read as a transport failure and be retried until the
 * deadline, so the same class — CR/LF (a split field), NUL, ESC (which would repaint a
 * terminal), DEL and any higher code point — is refused here first, deterministically.
 */
const headerValuePattern = /[^\t\u0020-\u007e\u0080-\u00ff]/;

interface SignedHeader {
  readonly name: string;
  readonly value: string;
}

/**
 * Exactly the signed headers, validated against the local file: `Content-Length` must be the
 * declared size and the `x-amz-checksum-sha256` header must equal the base64 of the digest.
 * A response that signs neither is refused — the checksum exists to make the PUT
 * self-verifying, and sending an unsigned length would defeat that.
 */
export function signedHeaders(
  headers: Readonly<Record<string, string>>,
  size: number,
  sha256: string,
): Record<string, string> {
  const byLowerName = collectSignedHeaders(headers);
  assertSignedContentLength(byLowerName, size);
  assertSignedChecksum(byLowerName, sha256);
  const result: Record<string, string> = {};
  for (const { name, value } of byLowerName.values()) {
    result[name] = value;
  }
  return result;
}

/** Collects the signed set under lower-cased names, refusing what may never be sent. */
function collectSignedHeaders(
  headers: Readonly<Record<string, string>>,
): Map<string, SignedHeader> {
  const byLowerName = new Map<string, SignedHeader>();
  for (const [name, value] of Object.entries(headers)) {
    const lower = name.toLowerCase();
    // A name `fetch` would refuse (a space, a colon, a bare non-ASCII byte) is a malformed
    // response, not a transport failure: the name is never echoed when it is not a token.
    if (!headerNamePattern.test(name)) {
      throw badResponse('the upload URL signed a header whose name is not an HTTP token');
    }
    if (headerValuePattern.test(value)) {
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header with a value the request cannot carry`,
      );
    }
    if (forbiddenSignedHeaders[lower] === true || lower.startsWith('proxy-')) {
      // A 201 can name any header; these would let the response make the CLI send a
      // credential, rewrite the target host, or smuggle a second body framing. The value is
      // never echoed — only the name.
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header, which is never sent to the object store`,
      );
    }
    const existing = byLowerName.get(lower);
    // Two names that fold to the same header are one signed header. The same value twice
    // is a harmless duplicate; two different values make the signed set ambiguous (the
    // transport would send whichever it kept), so the response is refused. The value is
    // signed material and never enters the message — only the header name may.
    if (existing !== undefined && existing.value !== value) {
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header twice with different values`,
      );
    }
    if (existing === undefined) {
      byLowerName.set(lower, { name, value });
    }
  }
  return byLowerName;
}

function assertSignedContentLength(byLowerName: Map<string, SignedHeader>, size: number): void {
  const length = byLowerName.get('content-length');
  if (length === undefined) {
    throw badResponse('the upload URL did not sign Content-Length');
  }
  if (length.value !== String(size)) {
    throw badResponse('the signed Content-Length does not match the file size');
  }
}

function assertSignedChecksum(byLowerName: Map<string, SignedHeader>, sha256: string): void {
  const checksum = byLowerName.get('x-amz-checksum-sha256');
  if (checksum === undefined) {
    throw badResponse('the upload URL did not sign the checksum header');
  }
  const expected = Buffer.from(sha256.slice('sha256:'.length), 'hex').toString('base64');
  if (checksum.value !== expected) {
    throw badResponse('the signed checksum does not match the local file digest');
  }
}
