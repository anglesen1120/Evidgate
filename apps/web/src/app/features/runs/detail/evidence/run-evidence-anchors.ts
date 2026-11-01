/**
 * Stable anchors for run-detail targets, shared by the reason links, the target rows and the
 * failure-inbox/healing consumers: `#attempt-<execution_key>-<n>` and `#producer-<shard>`.
 *
 * Identity is the server value, not the rendered text: case-sensitive, locale-independent, never
 * slugified, lowercased, truncated or translated. A fragment is data, so resolution validates the
 * known prefix/suffix and then looks the id up exactly — never through a CSS selector, URL, path
 * or authorization check.
 */

/** A row a reason may point at, once the caller has established the relation from run data. */
export type EvidenceTarget =
  | { readonly kind: 'producer'; readonly shard: string }
  | { readonly kind: 'attempt'; readonly executionKey: string; readonly attemptNo: number };

/** `n` is the server attempt number: a positive integer, never an array index or page offset. */
const ATTEMPT_ID = /^attempt-(.+)-([1-9]\d*)$/;
const PRODUCER_ID = /^producer-(.+)$/;

/** DOM id of a producer slot row. Percent-encoding keeps a reserved character out of the id. */
export function producerAnchorId(shard: string): string {
  return `producer-${encodeURIComponent(shard)}`;
}

/** DOM id of one attempt row. Two attempts of a key differ only by the server attempt number. */
export function attemptAnchorId(executionKey: string, attemptNo: number): string {
  return `attempt-${encodeURIComponent(executionKey)}-${attemptNo}`;
}

export function evidenceAnchorId(target: EvidenceTarget): string {
  return target.kind === 'producer'
    ? producerAnchorId(target.shard)
    : attemptAnchorId(target.executionKey, target.attemptNo);
}

/**
 * Fragment for a reason link. The id already carries percent-encoded components, so encoding the
 * whole id is what keeps a literal `#`, space or `%` from changing navigation; decoding once
 * restores exactly the id the target row renders.
 */
export function evidenceHref(target: EvidenceTarget): string {
  return `#${encodeURIComponent(evidenceAnchorId(target))}`;
}

/** True for the two documented shapes only; anything else is not an evidence anchor. */
export function isEvidenceAnchorId(id: string): boolean {
  return ATTEMPT_ID.test(id) || PRODUCER_ID.test(id);
}

/** The exact DOM id a fragment names, or null when it is malformed or not an evidence anchor. */
export function decodeEvidenceAnchor(fragment: string): string | null {
  const raw = fragment.startsWith('#') ? fragment.slice(1) : fragment;
  if (raw === '') return null;
  let id: string;
  try {
    id = decodeURIComponent(raw);
  } catch {
    // A lone `%` or a broken escape is a malformed fragment, not a target.
    return null;
  }
  return isEvidenceAnchorId(id) ? id : null;
}

/**
 * The element a fragment names, or null. `getElementById` is the exact lookup: the fragment never
 * reaches a selector parser, so an identifier cannot widen the match.
 */
export function resolveEvidenceAnchor(fragment: string, root: Document): HTMLElement | null {
  const id = decodeEvidenceAnchor(fragment);
  return id === null ? null : root.getElementById(id);
}
