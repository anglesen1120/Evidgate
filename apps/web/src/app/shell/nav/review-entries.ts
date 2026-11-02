// The seam the review build opens one later-phase navigation entry through. This file is the
// PRODUCTION stand-in: it opens nothing, so the phase gate stays exactly `ACTIVE_PHASE`.
//
// `angular.json`'s `review` configuration replaces it (`fileReplacements`) with
// `review-entries.review.ts`, which lets the review stack show the real run list and run detail
// although `runs` is a later phase. Which entries are visible is a build decision, never a runtime
// flag a shipped page could flip; `isEntryActive` is the one predicate the sidebar and the route
// table both read, so a visible entry always has a matching route.

import type { ReviewOpenEntry } from './nav.models';

/** Production: no entry is opened early. */
export const REVIEW_OPEN_ENTRIES: readonly ReviewOpenEntry[] = [];
