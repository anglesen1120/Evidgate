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
 * i.e. the bytes the request would carry, with no indentation — or `null` when there is no
 * measurable wire body: `text` is not JSON at all, or the engine refuses to WRITE the compact form
 * of what it parsed (a pathological body, e.g. nesting deep enough to exhaust the stack and raise
 * `RangeError`; delta finding D2). Both are refusals that send nothing, so one answer covers both:
 * a body whose wire size cannot be measured cannot be checked against the cap, and a size invented
 * for it is worse than no size.
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
  // `JSON.parse` never yields `undefined`, so `JSON.stringify` always answers a string here — but
  // it can THROW, so it belongs inside the guard (D2): a `RangeError` escaping this pure helper
  // would surface as a bug in the publish and byte-counter paths instead of the refusal they know.
  let compact: string | undefined;
  try {
    compact = JSON.stringify(parsed);
  } catch {
    return null;
  }
  if (compact === undefined) {
    return null;
  }
  return new TextEncoder().encode(compact).byteLength;
}
