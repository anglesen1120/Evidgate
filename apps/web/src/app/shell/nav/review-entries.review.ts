// Review build only: opens the phase-G2 `runs` entry on its real product route and sidebar link
// (same guards, `run:read` included). `mount` is the tag `check-no-fixture-code.mjs
// --require-run-views` requires.

import type { ReviewOpenEntry } from './nav.models';

export const REVIEW_OPEN_ENTRIES: readonly ReviewOpenEntry[] = [
  { id: 'runs', mount: 'review-run-views' },
];
