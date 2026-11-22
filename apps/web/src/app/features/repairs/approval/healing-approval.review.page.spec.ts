import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { HealingApprovalReviewPage } from './healing-approval.review.page';

async function open(query = '') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: HealingApprovalReviewPage },
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

describe('HealingApprovalReviewPage', () => {
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
    ['featureoff', 'Approval is not enabled'],
    ['disabled', 'Lotus Commerce has disabled healing'],
    ['shadowonly', 'Project is shadow-only'],
    ['conflict', 'A different reviewer must approve'],
    ['submitting', 'Preparing the hash-bound decision'],
    ['prepared', 'Request preview prepared'],
    ['success', 'Approved'],
    ['denied', 'Not authorized'],
    ['422', 'A reason is required'],
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
    expect(element.querySelector('app-approval-decision dl')?.textContent).toContain(
      'sha256:ch-8af2c41e73b90d11c6e8f207b5343e20',
    );
  });

  it.each(['412', 'reloaded'])(
    'never carries validation across the %s revision change',
    async (state) => {
      const { element } = await open('?state=' + state);
      expect(element.textContent).toContain('ch-91d4…63fc');
      expect(element.textContent).not.toContain('sha256:ch-8af2c41e73b90d11c6e8f207b5343e20');
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
  it('does not turn assignment into a decision', async () => {
    const { element } = await open();
    expect(element.textContent).toContain('Minh Trần');
    expect(element.textContent).toContain('09:59 UTC');
    expect(element.textContent).not.toMatch(/10:00 UTC|Approved/);
  });

  it('associates the missing reason with the rejection field in the 422 preview', async () => {
    const { element, harness } = await open('?state=422');
    element.querySelector<HTMLButtonElement>('[data-action="reject"]')?.click();
    await harness.fixture.whenStable();
    const dialog = document.querySelector('[role="dialog"]:has(#proposal-review-reason)');
    const reason = dialog?.querySelector('textarea');
    expect(reason?.getAttribute('aria-invalid')).toBe('true');
    expect(reason?.getAttribute('aria-describedby')).toContain('proposal-reason-error');
    expect(dialog?.querySelector('#proposal-reason-error')?.textContent).toContain(
      'A reason is required',
    );
    if (!reason) throw new Error('Missing review reason');
    reason.value = 'Reviewed the original failure';
    reason.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();
    expect(reason.hasAttribute('aria-invalid')).toBe(false);
    expect(reason.getAttribute('aria-describedby')).toBe('preview-impact');
    expect(dialog?.querySelector('#proposal-reason-error')).toBeNull();
  });

  it.each(['approved', 'rejected'])(
    'binds the %s alternative to reviewer, time and exact hash',
    async (state) => {
      const { element } = await open('?state=' + state);
      const decision = element.querySelector('[data-decision-preview]');
      for (const fact of ['Minh Trần', '10:00 UTC', 'sha256:ch-8af2c41e73b90d11c6e8f207b5343e20'])
        expect(decision?.textContent).toContain(fact);
      expect(element.textContent).not.toContain('Awaiting review');
    },
  );

  it.each(['approve', 'reject'])(
    'requires a reason to prepare a %s preview without changing the stored decision',
    async (action) => {
      const { element, harness } = await open();
      element.querySelector<HTMLButtonElement>('[data-action="' + action + '"]')?.click();
      await harness.fixture.whenStable();
      const dialog = document.querySelector('[role="dialog"]');
      expect(dialog?.textContent).toContain('sha256:ch-8af2c41e73b90d11c6e8f207b5343e20');
      const prepare = dialog?.querySelector<HTMLButtonElement>('[data-prepare]');
      const reason = dialog?.querySelector('textarea');
      expect(prepare?.disabled).toBe(true);
      if (!reason || !prepare) throw new Error('Missing decision controls');
      expect(reason.maxLength).toBe(2000);
      reason.value = 'Reviewed the original timeout';
      reason.dispatchEvent(new Event('input'));
      await harness.fixture.whenStable();
      expect(prepare.disabled).toBe(false);
      prepare.click();
      await harness.fixture.whenStable();
      expect(element.textContent).toContain('Request preview prepared');
      expect(element.textContent).toContain('Awaiting review');
      expect(element.textContent).not.toContain('10:00 UTC');
    },
  );
});
