// 09.7.1-T4 (plan 09.7.1 §2 AC4, S83 P4; review finding F1): the size of the body a publish
// actually sends.
//
// The cap of `policy-revision.v1` is a cap on the WIRE body — the compact JSON the request carries —
// not on the text the textarea happens to show. The editor is seeded pretty-printed (two-space
// indentation, so the user can read and edit it), and indentation can add far more bytes than the
// cap allows while the document itself is well under it. Measuring the textarea's text would
// therefore refuse a body the server accepts. Measuring `JSON.stringify(JSON.parse(text))` measures
// exactly the bytes `POST /v1/projects/{p}/policies` puts on the wire.
//
// Pure and framework-free, so both the store and its spec can use it without Angular.

/**
 * The body cap of `policy-revision.v1` (contracts/schemas, AC4): 65 536 UTF-8 bytes are accepted,
 * one more byte is refused before any request. The server enforces the same number, so this is a
 * courtesy that saves a round trip, not a second source of truth.
 */
export const POLICY_BODY_MAX_BYTES = 65_536;

/**
 * The UTF-8 byte length of the compact body `text` describes — `JSON.stringify(JSON.parse(text))`,
 * i.e. the bytes the request would carry, with no indentation — or `null` when `text` is not JSON at
 * all, in which case there is no body to measure.
 *
 * A multi-byte character counts as the bytes it occupies, because the cap is declared in bytes.
 */
export function policyBodyBytes(text: string): number | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  // `JSON.parse` never yields `undefined`, so `JSON.stringify` always answers a string here.
  return new TextEncoder().encode(JSON.stringify(parsed)).byteLength;
}
