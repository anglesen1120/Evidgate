import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject, of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { HealingQueueReviewPage } from './healing-queue.review.page';

class QueueHarness extends ComponentHarness {
  static hostSelector = 'app-healing-queue-review';
  readonly text = async () => (await this.host()).text();
  readonly record = (id: string) => this.locatorFor('[data-row-id="' + id + '"]')();
  readonly disabledReview = (id: string) =>
    this.locatorFor('[data-row-id="' + id + '"] button[disabled]')();
  readonly summaryLink = (id: string) =>
    this.locatorFor('[data-row-id="' + id + '"] a[aria-label^="View summary"]')();
  readonly summaryIcon = (id: string) =>
    this.locatorFor('[data-row-id="' + id + '"] a[data-summary] hlm-icon')();
  readonly pendingIcon = this.locatorFor('[data-row-id="cand-73"] .pending hlm-icon');
  readonly pending = this.locatorFor('[data-row-id="cand-73"] .pending');
  readonly headers = this.locatorForAll('app-data-table [role="columnheader"]');
  readonly detail = this.locatorFor('app-queue-detail');
  readonly batchValues = this.locatorForAll('app-queue-detail figure tbody td');
  readonly metricValues = this.locatorForAll('.kpis .metric > strong');
  readonly bytes = this.locatorForAll('app-data-table [data-row-id]');
  readonly bulk = this.locatorFor('button[data-review-selected]');
  readonly cartChoice = this.locatorFor('[data-row-id="cand-77"] [role="checkbox"]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly filters = this.locatorForAll('button[data-filter]');
  readonly shadowLink = this.locatorFor('[data-row-id="cand-73"] a[aria-label^="View "]');
  readonly shadowIcon = this.locatorFor('[data-row-id="cand-73"] a[data-shadow] hlm-icon');
  readonly destinations = this.locatorForAll(
    'a[href*="/dev/shadow-detail/"], a[href*="/dev/healing-approval/"]',
  );
  readonly detailDestinations = this.locatorForAll('app-queue-detail footer a[href]');
}
class DialogHarness extends ComponentHarness {
  static hostSelector = 'hlm-dialog-content';
  readonly identities = this.locatorForAll('li');
  readonly accept = this.locatorFor('button[data-confirm]');
}

describe('HealingQueueReviewPage', () => {
  provideLayoutTestStubs();
  async function mount(state = 'default', id = 'cand-73') {
    const params = new BehaviorSubject(convertToParamMap({ state, id }));
    TestBed.configureTestingModule({
      imports: [HealingQueueReviewPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: params,
            paramMap: of(convertToParamMap({ orgId: 'org-a', projectId: 'proj-b' })),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(HealingQueueReviewPage);
    fixture.detectChanges();
    const queue = await TestbedHarnessEnvironment.harnessForFixture(fixture, QueueHarness);
    return { queue, dialogs: TestbedHarnessEnvironment.documentRootLoader(fixture) };
  }

  it.each([
    ['loading', 'Loading candidates'],
    ['empty', 'No proposals'],
    ['filtered', 'No filter matches'],
    ['disabled', 'Disabled by operator'],
    ['unavailable', 'Dependency unavailable'],
    ['error', 'Something went wrong'],
    ['403', 'Not authorized'],
    ['404', 'Not found'],
    ['412', 'Linked review conflict'],
    ['reloaded', 'ch-91d4…63fc'],
    ['success', 'No decision was recorded'],
    ['over-sla', 'Over SLA'],
  ])('renders the %s review state', async (state, expected) => {
    const { queue } = await mount(state);
    expect(await queue.text()).toContain(expected);
    if (['403', '404', 'empty', 'error', 'loading'].includes(state))
      expect(await queue.text()).not.toContain('cand-73');
    if (state === 'reloaded') expect(await queue.text()).not.toContain('24/24');
  });

  it('keeps original evidence before the sandbox result and never turns it into a release decision', async () => {
    const { queue } = await mount();
    expect(await queue.bytes()).toHaveLength(4);
    expect(await Promise.all((await queue.metricValues()).map((metric) => metric.text()))).toEqual([
      '4',
      '2',
      '100%',
      '0',
    ]);
    expect(await Promise.all((await queue.batchValues()).map((cell) => cell.text()))).toEqual([
      '2',
      '4',
      '3',
      '6',
      '4',
      '5',
    ]);
    const detail = await (await queue.detail()).text();
    expect(detail).toContain('inc-92');
    expect(detail.indexOf('Required delivery address field not found')).toBeLessThan(
      detail.indexOf('24/24'),
    );
    expect(detail).toMatch(/Incomplete[\s\S]*has_failures[\s\S]*FAIL/);
    expect(detail).not.toMatch(/\bPASS\b/);
    const record = await (await queue.record('cand-73')).text();
    expect(record).toContain('9 min');
    expect(record).not.toContain('Over SLA');
    expect(await (await queue.pending()).text()).toContain('Awaiting review');
    expect(await (await queue.pending()).hasClass('bg-(--eg-neutral-bg)')).toBe(true);
    expect(await (await queue.pendingIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await queue.shadowLink()).getAttribute('href')).toBe(
      '/dev/shadow-detail/orgs/org-a/projects/proj-b?id=cand-73',
    );
    expect(await (await queue.shadowLink()).text({ exclude: '[popover]' })).toBe('');
    expect(await (await queue.shadowLink()).getAttribute('aria-label')).toContain(
      'Required delivery address',
    );
    expect(await (await queue.shadowIcon()).getAttribute('aria-hidden')).toBe('true');
    const shadow = await queue.shadowLink();
    await shadow.focus();
    expect(await shadow.isFocused()).toBe(true);
    expect(await Promise.all((await queue.headers()).map((header) => header.text()))).toEqual([
      'Select for review',
      'Candidate',
      'Original failure',
      'Review status',
      'Shadow evidence',
      'View',
    ]);
  });

  it('opens only the loaded summary for an unlinkable candidate and retains the queue query', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'dev/healing-queue/orgs/:orgId/projects/:projectId',
            component: HealingQueueReviewPage,
          },
        ]),
      ],
    });
    const path = '/dev/healing-queue/orgs/org-a/projects/proj-b';
    const router = await RouterTestingHarness.create();
    await router.navigateByUrl(`${path}?state=default&id=cand-73`, HealingQueueReviewPage);
    const queue = await TestbedHarnessEnvironment.loader(router.fixture).getHarness(QueueHarness);
    const summary = await queue.summaryLink('cand-77');
    const url = new URL((await summary.getAttribute('href')) ?? '', 'https://example.test');
    expect(url.pathname).toBe(path);
    expect(url.searchParams.get('id')).toBe('cand-77');
    expect(url.searchParams.get('state')).toBe('default');
    await summary.click();
    expect(await (await queue.detail()).text()).toContain('Cart total after refresh');
    expect(await queue.detailDestinations()).toHaveLength(0);
  });

  it.each(['cand-73', 'cand-77', 'cand-74', 'cand-75'])(
    'links only to supported records in the queue and the %s detail footer',
    async (id) => {
      const { queue } = await mount('default', id);
      const links = await queue.destinations();
      const supported = id === 'cand-73';
      expect(links).toHaveLength(supported ? 4 : 2);
      expect(await queue.detailDestinations()).toHaveLength(supported ? 2 : 0);
      for (const link of links) {
        expect(await link.getAttribute('href')).toMatch(
          /^\/dev\/(?:shadow-detail|healing-approval)\/orgs\/org-a\/projects\/proj-b\?id=cand-73$/,
        );
      }
      if (!supported) {
        const note = 'Detail not available in this review specimen.';
        const summary = await queue.summaryLink(id);
        expect(await summary.text({ exclude: '[popover]' })).toBe('');
        expect(await summary.getAttribute('aria-label')).toContain('View summary');
        const label = (await summary.getAttribute('aria-label')) ?? '';
        expect(label).toMatch(/^View summary for \S.+/);
        expect(await (await queue.record(id)).text()).toContain(
          label.replace(/^View summary for /, ''),
        );
        expect(await (await queue.summaryIcon(id)).getAttribute('aria-hidden')).toBe('true');
        await summary.focus();
        expect(await summary.isFocused()).toBe(true);
        expect(await summary.getAttribute('href')).toContain(`id=${id}`);
        expect(await (await queue.record(id)).text()).toContain(note);
        expect(await (await queue.detail()).text()).toContain(note);
      }
    },
  );

  it.each(['cand-74', 'cand-75'])(
    'does not offer approval or a rate for unsampled %s',
    async (id) => {
      const { queue } = await mount('default', id);
      const record = await (await queue.record(id)).text();
      expect(record).toContain('Rate unavailable');
      expect(record).toContain('Approval not enabled');
      expect(await (await queue.disabledReview(id)).getProperty('disabled')).toBe(true);
      expect(await (await queue.detail()).text()).not.toMatch(/24\/24|8\/8|\bPASS\b/);
      expect(await queue.batchValues()).toHaveLength(0);
    },
  );

  it('filters records by category and text without substituting another record', async () => {
    const { queue } = await mount();
    const wait = (await queue.filters()).at(-1);
    await wait?.click();
    expect(await queue.bytes()).toHaveLength(1);
    expect(await queue.text()).toContain('Showing 1 of 4');
    expect(await (await queue.record('cand-77')).text()).toContain('8/8');
    const field = await queue.search();
    await field.setInputValue('cand-73');
    await field.dispatchEvent('input');
    expect(await queue.bytes()).toHaveLength(0);
    expect(await queue.text()).toContain('Showing 0 of 4');
    expect(await queue.text()).toContain('No filter matches');
  });

  it('confirms only checked IDs and hashes without granting approval', async () => {
    const { queue, dialogs } = await mount();
    await (await queue.cartChoice()).click();
    await (await queue.bulk()).click();
    const dialog = await dialogs.getHarness(DialogHarness);
    expect(await Promise.all((await dialog.identities()).map((row) => row.text()))).toEqual([
      'cand-73 · ch-8af2…3e20',
    ]);
    await (await dialog.accept()).click();
    expect(await queue.text()).toContain('No decision was recorded');
    expect(await (await queue.pending()).text()).toContain('Awaiting review');
  });

  it('opens the direct confirmation specimen without unrelated or unsampled identities', async () => {
    const { dialogs } = await mount('confirmation');
    const dialog = await dialogs.getHarness(DialogHarness);
    expect(await Promise.all((await dialog.identities()).map((row) => row.text()))).toEqual([
      'cand-73 · ch-8af2…3e20',
      'cand-77 · ch-27ca…512e',
    ]);
  });

  it('marks a proposal overdue only in the separately dated next-day specimen', async () => {
    const { queue } = await mount('over-sla');
    expect(await queue.text()).toContain('Later SLA specimen');
    expect(await (await queue.record('cand-73')).text()).toMatch(/24 h\s+9 min/);
    expect(await (await queue.record('cand-73')).text()).toContain('Over SLA');
  });

  it('returns not-found rather than replacing an unknown requested candidate', async () => {
    const { queue } = await mount('default', 'cand-999');
    expect(await queue.text()).toContain('Not found');
    expect(await queue.bytes()).toHaveLength(0);
  });
});
