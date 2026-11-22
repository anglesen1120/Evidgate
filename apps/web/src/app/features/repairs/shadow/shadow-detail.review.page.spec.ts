import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { ShadowDetailReviewPage } from './shadow-detail.review.page';

async function open(query = '') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: ShadowDetailReviewPage },
      ]),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: () => true, allowsMutation: () => true },
      },
    ],
  });
  const harness = await RouterTestingHarness.create('/orgs/o-review/projects/p-review' + query);
  const element = harness.routeNativeElement;
  if (!element) throw new Error('Review page did not mount');
  return { element, harness };
}

describe('ShadowDetailReviewPage', () => {
  it.each([
    ['default', 'Awaiting review'],
    ['loading', 'Loading candidate'],
    ['empty', 'No candidate supplied'],
    ['filtered', 'No matching validation records'],
    ['error', 'Unable to refresh'],
    ['reloaded', 'ch-91d4…63fc'],
    ['proposed', 'Not yet validated'],
    ['running', 'Shadow running'],
    ['missing', 'Required validator missing'],
    ['negative', 'Negative control passed unexpectedly'],
    ['stale', 'Validation is stale'],
    ['expired', 'Proposal expired'],
    ['approved', 'Approved'],
    ['applied', 'Applied'],
    ['rejected', 'Rejected'],
    ['revoked', 'Revoked'],
    ['unavailable', 'Knowledge store unavailable'],
    ['403', 'Not authorized'],
    ['404', 'Not found'],
    ['401', 'Session expired'],
    ['429', 'Too many requests'],
    ['412', 'Revision changed'],
    ['nolineage', 'Lineage unavailable'],
    ['restricted', 'Sensitive artifact'],
    ['nodom', 'Structural diff unavailable'],
    ['observing', 'Observation window open'],
    ['merged', 'Merged'],
    ['closed', 'Closed'],
    ['ready', 'Both captures are ready'],
    ['off', 'Screenshots are off'],
    ['scan', 'Scan pending'],
    ['conflict', 'Revision changed'],
  ])('renders the %s snapshot', async (state, expected) => {
    const { element } = await open('?state=' + state);
    expect(element.textContent).toContain(expected);
  });

  it('keeps the original failure ahead of any repair and preserves its stored decision', async () => {
    const { element } = await open();
    const headings = [...element.querySelectorAll('h2')].map((h) => h.textContent?.trim());
    expect(headings[0]).toBe('Original failure');
    expect(headings.indexOf('Locator comparison')).toBeGreaterThan(0);
    const failure = element.querySelector('app-repair-evidence section');
    for (const fact of ['Incomplete', 'has_failures', 'FAIL', 'inc-92'])
      expect(failure?.textContent).toContain(fact);
    expect(failure?.textContent).not.toMatch(/\bPASS\b|HTTP 500|att-1842-01/);
    const pending = element.querySelector('[data-tone="pending"]');
    expect(pending?.querySelector('hlm-icon')).toBeTruthy();
    expect(pending?.textContent).toContain('Awaiting review');
    expect(element.textContent).toContain('24/24');
  });

  it.each(['412', 'reloaded'])(
    'never carries validation across the %s revision change',
    async (state) => {
      const { element } = await open('?state=' + state);
      expect(element.textContent).toContain('ch-91d4…63fc');
      expect(element.textContent).not.toMatch(/24\/24|Awaiting review/);
    },
  );

  it.each(['?id=inc-91', '?id=cand-73&id=cand-77', '?state=403'])(
    'does not substitute a candidate for %s',
    async (query) => {
      const { element } = await open(query);
      expect(element.querySelector('app-repair-evidence')).toBeNull();
      expect(element.textContent).not.toContain('ch-8af2');
    },
  );
  it('links the same candidate and project into approval', async () => {
    const { element } = await open();
    expect(element.querySelector('a[href*="healing-approval"]')?.getAttribute('href')).toBe(
      '/dev/healing-approval/orgs/o-review/projects/p-review?id=cand-73',
    );
  });
});
