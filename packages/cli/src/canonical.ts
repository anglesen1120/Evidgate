// The CLI/web half of canonical hashing. `canonicalize` is the pinned RFC 8785 (JCS)
// implementation and Node's crypto gives the `sha256:<hex>` wrapper; the digests must match
// the server-side Evidgate.BuildingBlocks.Canonicalization byte for byte, or the same document
// hashes differently on the two sides of the wire.
import { createHash } from 'node:crypto';

import canonicalize from 'canonicalize';

/** 00 §7: every canonical digest is `sha256:<lowercase hex>`. */
export const canonicalHashPrefix = 'sha256:';

/**
 * `JsonCanonicalizer.MaxDepth` on the server: the deepest container nesting either
 * runtime canonicalizes. The server's streaming reader rejects a document nested one
 * level deeper, so the CLI applies the same rule instead of handing out a digest for a
 * payload the ingest edge answers 400 for.
 */
export const canonicalMaxDepth = 64;

/**
 * RFC 8785 canonical JSON text of a parsed JSON value.
 *
 * The value must already be parsed, which makes one asymmetry with the server
 * structural rather than accidental: `JSON.parse` has already collapsed duplicate member
 * names (last one wins) before this function sees them, so a duplicate cannot be detected
 * here at all. `JsonCanonicalizer` reads the document itself and rejects duplicates, so
 * the .NET path is the arbiter of what may be hashed; a duplicate-name document never
 * reaches a server digest, and the CLI's job is the hashes of documents the server
 * accepts. A CLI-side JCS source-text guard is deliberately not implemented (it would be
 * a second canonicalizer); the asymmetry is recorded in the feature plan §8.
 *
 * @throws {TypeError} when the value is not valid JSON data (undefined,
 * functions, symbols, `NaN`, `±Infinity`, lone surrogates).
 * @throws {RangeError} when a container is nested deeper than {@link canonicalMaxDepth}.
 * @throws {Error} when this runtime does not produce a self-consistent canonical form —
 * see {@link assertCanonicalRoundTrip}; the digest is withheld rather than reported wrong.
 *
 * Costs one extra `JSON.parse` + `canonicalize` pass per call for that guard.
 */
export function canonicalizeJson(value: unknown): string {
  assertWithinCanonicalDepth(value, 1, new Set<object>(), new Map<object, number>());

  const canonical = canonicalize(value);
  if (canonical === undefined) {
    throw new TypeError('value cannot be canonicalized: it is not valid JSON data');
  }

  assertCanonicalRoundTrip(canonical);
  return canonical;
}

/** 00 §7 digest of a parsed JSON value: `sha256:<lowercase hex>` over its canonical form. */
export function canonicalHash(value: unknown): string {
  return canonicalHashOfCanonical(canonicalizeJson(value));
}

/** Digest of JSON text that is already canonical (no normalization is applied). */
export function canonicalHashOfCanonical(canonicalJson: string): string {
  const digest = createHash('sha256').update(canonicalJson, 'utf8').digest('hex');
  return `${canonicalHashPrefix}${digest}`;
}

/**
 * Self-consistency guard: a canonical text is a fixed point of `JSON.parse` + `canonicalize`,
 * so re-parsing it and canonicalizing it again must reproduce it byte for byte. Node does not
 * always hold up its side: in a long-running process, `JSON.parse` can mis-decode a member
 * name written with an escape (`"\r"` came back as a lone backslash) — the first parse of a
 * text differs from later ones for a small fraction of documents, so
 * `canonicalize(JSON.parse(text))` can depend on process history and produce a digest the
 * server never computes for the same document. That is a V8/Node defect, not fixable here;
 * this guard turns the silent wrong digest into a thrown error instead.
 *
 * It detects the divergence, not its cause: the check compares two parses made in the same
 * process, so it catches the case where the re-parse of the canonical text disagrees with the
 * value that produced it.
 *
 * @throws {Error} when the canonical text does not survive the round trip.
 */
export function assertCanonicalRoundTrip(canonicalJson: string): void {
  const recanonicalized = canonicalize(JSON.parse(canonicalJson) as unknown);
  if (recanonicalized !== canonicalJson) {
    throw new Error(
      'canonical form is not self-consistent in this runtime: re-parsing and canonicalizing it ' +
        'changed the text (known Node 26.10.0 warm-process JSON.parse defect, see canonical.ts)',
    );
  }
}

/** The one rejection message for the depth cap, thrown from both the descending and memoised paths. */
const depthCapMessage = `value cannot be canonicalized: a container is nested deeper than the ${canonicalMaxDepth} level cap`;

/**
 * Depth cap, counted the way the server's reader counts it: `level` is the
 * number of containers from the root value down to `value`, inclusive, and a container at
 * level {@link canonicalMaxDepth} + 1 is rejected. A repeated container on the current
 * path is a circular value, not depth, so the walk stops there and `canonicalize` reports
 * it as such.
 *
 * Returns the depth of the subtree below `value` in container levels (`value` itself is
 * 1, a primitive is 0). The depth of each container is memoised in `subtreeDepths`, so a
 * reference shared by several parents — a DAG, which `canonicalize` rejects — is measured
 * once instead of once per path; enumerating paths would make the walk O(k^depth) and
 * hang before `canonicalize` ever saw the value. `ancestors` still stops at a cycle, and a
 * memoised subtree reached again at a deeper level is re-checked against the cap, which is
 * the one place the absolute level can still push a shared subtree past it.
 *
 * Recursion is bounded by the cap itself (this function throws one frame above it) and
 * runs before `canonicalize`, which keeps an over-deep document from being serialized at
 * all.
 */
function assertWithinCanonicalDepth(
  value: unknown,
  level: number,
  ancestors: Set<object>,
  subtreeDepths: Map<object, number>,
): number {
  if (value === null || typeof value !== 'object') {
    return 0;
  }

  if (level > canonicalMaxDepth) {
    throw new RangeError(depthCapMessage);
  }

  if (ancestors.has(value)) {
    return 0;
  }

  const measured = subtreeDepths.get(value);
  if (measured !== undefined) {
    if (level + measured - 1 > canonicalMaxDepth) {
      throw new RangeError(depthCapMessage);
    }
    return measured;
  }

  ancestors.add(value);
  let depth = 1;
  const children: readonly unknown[] = Array.isArray(value) ? value : Object.values(value);
  for (const child of children) {
    const childDepth = assertWithinCanonicalDepth(child, level + 1, ancestors, subtreeDepths);
    if (childDepth + 1 > depth) {
      depth = childDepth + 1;
    }
  }
  ancestors.delete(value);
  subtreeDepths.set(value, depth);
  return depth;
}
