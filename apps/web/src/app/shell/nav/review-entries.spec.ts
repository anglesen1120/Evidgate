// The review build opens exactly one later-phase entry. Production opens none, and the review
// replacement must name a registry row that is a project-scope run entry gated on `run:read`,
// so the product route and sidebar link keep their permission gate.

import { isEntryActive } from './product-phase';
import { NAV_ENTRIES } from './nav-registry';
import { REVIEW_OPEN_ENTRIES as PRODUCTION } from './review-entries';
import { REVIEW_OPEN_ENTRIES as REVIEW } from './review-entries.review';

describe('review entries seam', () => {
  it('production opens no entry and keeps every later phase closed', () => {
    expect(PRODUCTION).toEqual([]);
    const hidden = NAV_ENTRIES.find((entry) => entry.id === 'runs');
    if (hidden === undefined) throw new Error('no runs entry');
    expect(isEntryActive(hidden)).toBe(false);
    const later = NAV_ENTRIES.filter((entry) => entry.phase !== 'G1');
    expect(later.length).toBeGreaterThan(0);
    for (const entry of later) {
      expect(isEntryActive(entry), entry.id).toBe(false);
    }
  });

  it('the review replacement opens only the project-scope runs entry gated on run:read', () => {
    expect(REVIEW.map((open) => open.id)).toEqual(['runs']);
    const runs = NAV_ENTRIES.find((entry) => entry.id === 'runs');
    expect(runs?.scope).toBe('project');
    expect(runs?.permission).toBe('run:read');
    expect(runs?.phase).not.toBe('G1');
  });
});
