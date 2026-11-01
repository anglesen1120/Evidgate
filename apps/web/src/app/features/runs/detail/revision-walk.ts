// The evidence-revision keyset walk, kept out of the store file so the store stays under the
// 500-line limit: the page limit, the typed refusals both walks share, and the pure helpers the
// revision computeds reuse. Nothing here reads state or calls the API.

import type { EvidenceRevision } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';

/** Rows per revision request: the contract's maximum, so one page covers a normal run. */
export const REVISIONS_PAGE_LIMIT = 200;

/** The revision-list read is its own region state: a failure never becomes a run-page failure. */
export type RevisionsStatus = 'idle' | 'loading' | 'ready' | 'error';

/**
 * The two ways a keyset walk reads itself into a loop: the server hands back the cursor it was
 * given, or a follow-up page adds no revision that is not already loaded. Both end the walk with a
 * typed failure and drop the cursor, so Load older cannot re-request it.
 */
export const REVISIONS_CURSOR_REPEATED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'revisions_cursor_repeated',
  message: 'The evidence revision list returned the same page cursor again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
export const REVISIONS_PAGE_WITHOUT_NEW_ROWS: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'revisions_page_without_new_rows',
  message: 'The evidence revision list returned a follow-up page with no new revisions.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
export const REVISIONS_WALK_REFUSALS: ReadonlySet<string> = new Set([
  REVISIONS_CURSOR_REPEATED.code,
  REVISIONS_PAGE_WITHOUT_NEW_ROWS.code,
]);

/** The highest `revision_no` on the loaded pages; the server order is newest first. */
export function newestRevision(loaded: readonly EvidenceRevision[]): EvidenceRevision | null {
  return loaded.reduce<EvidenceRevision | null>(
    (newest, revision) =>
      newest === null || revision.revision_no > newest.revision_no ? revision : newest,
    null,
  );
}

/**
 * The incoming page without the revisions already loaded, in page order, with the first copy
 * kept. Every `revision_no` seen — loaded before this read or new here — is added to `seen`.
 */
export function takeFreshRevisions(
  seen: Set<number>,
  incoming: readonly EvidenceRevision[],
): EvidenceRevision[] {
  const fresh: EvidenceRevision[] = [];
  for (const revision of incoming) {
    if (seen.has(revision.revision_no)) continue;
    seen.add(revision.revision_no);
    fresh.push(revision);
  }
  return fresh;
}
