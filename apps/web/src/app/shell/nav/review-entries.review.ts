// Review build only (`angular.json` configuration `review`): see `review-entries.ts`. `runs` is a
// phase-G2 entry; opening it here mounts the real product route and sidebar link — same guards,
// `run:read` included — so the review stack shows the run list, decision and evidence panel.
// The `mount` tag is what `check-no-fixture-code.mjs --require-run-views` looks for.

import type { ReviewOpenEntry } from './nav.models';

export const REVIEW_OPEN_ENTRIES: readonly ReviewOpenEntry[] = [
  { id: 'runs', mount: 'review-run-views' },
];
