// Build seam: entries the build opens although their phase is not reached. Production stand-in,
// opens none; the `review` configuration swaps in `review-entries.review.ts`. A build decision,
// never a runtime flag; sidebar and `phaseGuard` both read it through `isEntryActive`.

import type { ReviewOpenEntry } from './nav.models';

/** Production: no entry is opened early. */
export const REVIEW_OPEN_ENTRIES: readonly ReviewOpenEntry[] = [];
