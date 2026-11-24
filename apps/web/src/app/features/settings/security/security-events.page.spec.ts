import { ComponentHarness, manualChangeDetection, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { SecurityEventItem } from '@evidgate/sdk';
import type { SecurityEventRow } from '../../../core/data/security-events-data.service';
import { SecurityEventsDataService } from '../../../core/data/security-events-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { SecurityEventsPage } from './security-events.page';
import { securityEventVisible, securityTypeLabel } from './security-events.config';

provideLayoutTestStubs();
const event: SecurityEventItem = {
  id: 'event1',
  type: 'support_access',
  severity: 'critical',
  actor: { kind: 'user', id: 'actor1' },
  details: { support_grant_id: 'grant1', message: '<img src=x onerror=alert(1)>' },
  created_at: '2026-11-01T00:00:00Z',
};
class SecurityPageHarness extends ComponentHarness {
  static hostSelector = 'app-security-events-page';
  readonly content = async () => (await this.host()).text();
  readonly rows = this.locatorForAll('[data-row-id]');
  readonly summaryValues = this.locatorForAll('[data-security-summary] dd strong');
  readonly details = this.locatorFor('button[data-security-view]');
  readonly detailsIcon = this.locatorFor('button[data-security-view] hlm-icon');
  readonly project = this.locatorFor('#security-project');
  readonly apply = this.locatorFor('[data-security-apply]');
  readonly clear = this.locatorFor('[data-security-clear]');
  readonly emptyReset = this.locatorFor('app-empty-pattern button');
  readonly results = this.locatorFor('#security-results');
  readonly table = this.locatorFor('#security-results [role="table"]');
  readonly from = this.locatorFor('#security-from');
  readonly to = this.locatorFor('#security-to');
  readonly correctionSummary = this.locatorForOptional('#security-filter-errors');
  readonly correctionLinks = this.locatorForAll('#security-filter-errors a');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly pager = this.locatorFor('app-data-table-pager');
  readonly previous = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly next = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly retry = this.locatorFor('app-error-pattern button');
  readonly sizeTrigger = this.locatorFor('[id^="eg-table-pager-size-"]');
  readonly sizeOptions = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  async chooseSize(value: string): Promise<void> {
    await (await this.sizeTrigger()).click();
    for (const option of await this.sizeOptions()) {
      if ((await option.text()).trim() === value) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing page-size option ${value}`);
  }
  async fill(field: 'project' | 'from' | 'to', value: string): Promise<void> {
    const input = await this[field]();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
}
describe('SecurityEventsPage', () => {
  const list = vi.fn<SecurityEventsDataService['list']>();
  beforeEach(() => {
    list.mockReset().mockResolvedValue({ items: [event], next_cursor: null });
    TestBed.configureTestingModule({
      imports: [SecurityEventsPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: SecurityEventsDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: signal({ id: 'org1' }) } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function render() {
    const fixture = TestBed.createComponent(SecurityEventsPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SecurityPageHarness);
    return { fixture, harness };
  }
  it('renders support access with its grant and displays untrusted detail text without HTML', async () => {
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(1);
    expect(await harness.content()).toContain('Support access');
    const trigger = await harness.details();
    expect(await trigger.text({ exclude: '[popover]' })).toBe('');
    expect(await trigger.getProperty('type')).toBe('button');
    expect(await trigger.getAttribute('aria-label')).toBe('View · Flagged support access · event1');
    expect(await (await harness.detailsIcon()).getAttribute('aria-hidden')).toBe('true');
    await trigger.focus();
    await trigger.click();
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
    expect(dialog?.textContent).toContain('grant1');
    expect(dialog?.textContent).toContain('actor1');
    expect(dialog?.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(dialog?.querySelector('img')).toBeNull();
    dialog?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await vi.waitFor(() => expect(document.querySelector('[role="dialog"]')).toBeNull());
    expect(await trigger.isFocused()).toBe(true);
  });
  it('hides injection_attempt under V1 while preserving unknown future types verbatim', async () => {
    const future: SecurityEventRow = {
      ...event,
      id: 'future',
      type: 'future_alert',
      severity: 'warning',
    };
    const injection: SecurityEventRow = { ...event, id: 'injection', type: 'injection_attempt' };
    list.mockResolvedValue({ items: [event, future, injection], next_cursor: null });
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(2);
    expect(await harness.content()).toContain('future_alert');
    expect(await Promise.all((await harness.summaryValues()).map((cell) => cell.text()))).toEqual([
      '2',
      '1',
      '1',
      'V1',
    ]);
    expect(await harness.content()).not.toContain('injection_attempt');
    expect(securityEventVisible(injection.type, 'V2')).toBe(true);
    expect(securityTypeLabel('future_alert')).toBe('future_alert');
  });
  it('renders the unfiltered empty state', async () => {
    list.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await render();
    expect(await harness.content()).toContain('No security events recorded');
  });
  it('applies a project filter and offers a distinct no-match state with reset', async () => {
    const { harness } = await render();
    list.mockResolvedValue({ items: [], next_cursor: null });
    await harness.fill('project', '018f0000-0000-7000-8000-000000000011');
    await (await harness.apply()).click();
    expect(await harness.content()).toContain('No results match these filters');
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, project_id: '018f0000-0000-7000-8000-000000000011' },
      expect.any(AbortSignal),
    );
    await (await harness.clear()).click();
    expect(await harness.content()).toContain('No security events recorded');
  });
  it.each([
    ['forbidden', 403],
    ['unavailable', 503],
  ] as const)('renders %s as refusal, never empty success', async (kind, status) => {
    const error: ApiError = {
      kind,
      status,
      code: 'events_refused',
      message: 'Events refused <b>verbatim</b>',
      requestId: null,
      retryable: false,
      details: [],
      retryAfterSeconds: null,
    };
    list.mockRejectedValue(new ApiRequestError(error));
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(0);
    expect(await harness.summaryValues()).toHaveLength(0);
    expect(await harness.content()).not.toContain('No security events recorded');
    if (status === 403) expect(await harness.forbidden()).not.toBeNull();
    else expect(await harness.content()).toContain(error.message);
  });
  it('shows the loading state before the scoped response settles', async () => {
    let finish: (value: { items: SecurityEventItem[]; next_cursor: null }) => void = () =>
      undefined;
    list.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const fixture = TestBed.createComponent(SecurityEventsPage);
    fixture.detectChanges();
    TestBed.tick();
    await manualChangeDetection(async () => {
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        SecurityPageHarness,
      );
      expect(await harness.loading()).not.toBeNull();
      expect(await harness.summaryValues()).toHaveLength(0);
      finish({ items: [event], next_cursor: null });
    });
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SecurityPageHarness);
    expect(await harness.rows()).toHaveLength(1);
  });
  it('hands focus from the no-match reset to stable results before loading removes the action', async () => {
    const { fixture, harness } = await render();
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    await harness.fill('project', 'missing-project');
    await (await harness.apply()).click();
    let finish: (value: { items: SecurityEventItem[]; next_cursor: null }) => void = () =>
      undefined;
    list.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    await (await harness.emptyReset()).focus();
    await manualChangeDetection(async () => {
      await (await harness.emptyReset()).click();
      fixture.detectChanges();
      TestBed.tick();
      expect(await (await harness.results()).isFocused()).toBe(true);
      expect(await harness.loading()).not.toBeNull();
      finish({ items: [event], next_cursor: null });
    });
    await fixture.whenStable();
    expect(await (await harness.results()).isFocused()).toBe(true);
    expect(await harness.rows()).toHaveLength(1);
  });
  it.each([
    ['equal', '2026-11-18T09:00', '2026-11-18T09:00'],
    ['inverted', '2026-11-18T10:00', '2026-11-18T09:00'],
  ] as const)(
    'keeps the %s range correction linked and focused until the range is corrected',
    async (_case, from, to) => {
      const { harness } = await render();
      await harness.fill('from', from);
      await harness.fill('to', to);
      await (await harness.apply()).click();
      const summary = await harness.correctionSummary();
      expect(summary).not.toBeNull();
      expect(await summary?.isFocused()).toBe(true);
      expect(list).toHaveBeenCalledTimes(1);
      const links = await harness.correctionLinks();
      expect(links).toHaveLength(2);
      expect(await links[0]!.getAttribute('href')).toBe('#security-from');
      expect(await links[1]!.getAttribute('href')).toBe('#security-to');
      await links[0]!.click();
      expect(await (await harness.from()).isFocused()).toBe(true);
      expect(await (await harness.from()).getAttribute('aria-invalid')).toBe('true');
      expect(await (await harness.to()).getAttribute('aria-describedby')).toContain(
        'security-to-error',
      );
      await harness.fill('project', 'another-project');
      expect(await harness.correctionSummary()).not.toBeNull();
      expect(await (await harness.from()).getAttribute('aria-invalid')).toBe('true');
      await harness.fill('to', '2026-11-18T11:00');
      expect(await harness.correctionSummary()).toBeNull();
      expect(await (await harness.from()).getAttribute('aria-invalid')).toBeNull();
      expect(list).toHaveBeenCalledTimes(1);
      await (await harness.apply()).click();
      expect(list).toHaveBeenCalledTimes(2);
    },
  );
  it('renders the shared pager with only 20/50/100 choices and unknown-total page text', async () => {
    const { harness } = await render();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 20 }, expect.any(AbortSignal));
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(await (await harness.pager()).text()).not.toContain('of 1');
    expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
    await (await harness.sizeTrigger()).click();
    expect(await Promise.all((await harness.sizeOptions()).map((option) => option.text()))).toEqual(
      ['20', '50', '100'],
    );
  });
  it('keeps the shared pager available for an empty page and refuses unavailable navigation', async () => {
    list.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await render();
    expect(await (await harness.pager()).text()).toContain('Page 1');
    await (await harness.next()).click();
    await (await harness.previous()).click();
    expect(list).toHaveBeenCalledTimes(1);
    expect(await harness.content()).toContain('No security events recorded');
  });
  it('pages forward and backward while View preserves the current page and request state', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: 'third' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'third' }], next_cursor: null })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: 'third' });
    const { harness } = await render();
    await (await harness.next()).click();
    expect(await (await harness.pager()).text()).toContain('Page 2');
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.rows())[0]?.getAttribute('data-row-id')).toBe('second');
    expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('false');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('false');
    await (await harness.details()).click();
    expect(await harness.dialog()).not.toBeNull();
    expect(list).toHaveBeenCalledTimes(2);
    await (await harness.dialog())?.sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await harness.dialog()).toBeNull());
    expect(await (await harness.pager()).text()).toContain('Page 2');
    expect(list).toHaveBeenCalledTimes(2);
    await (await harness.next()).click();
    expect(await (await harness.pager()).text()).toContain('Page 3');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
    await (await harness.previous()).click();
    expect(await (await harness.pager()).text()).toContain('Page 2');
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, cursor: 'second' },
      expect.any(AbortSignal),
    );
  });
  it('resets paging for size and applied filters without discarding the project filter', async () => {
    list.mockResolvedValue({ items: [event], next_cursor: 'second' });
    const { harness } = await render();
    await harness.fill('project', 'project1');
    await (await harness.apply()).click();
    await (await harness.next()).click();
    expect(await (await harness.pager()).text()).toContain('Page 2');
    await harness.chooseSize('50');
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 50, project_id: 'project1' },
      expect.any(AbortSignal),
    );
    await (await harness.next()).click();
    await harness.fill('project', 'project2');
    await (await harness.apply()).click();
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 50, project_id: 'project2' },
      expect.any(AbortSignal),
    );
  });
  it('retains the prior page and exposes Retry when a later-page request fails', async () => {
    const error: ApiError = {
      kind: 'unavailable',
      status: 503,
      code: 'page_unavailable',
      message: 'The next page is unavailable.',
      requestId: null,
      retryable: true,
      details: [],
      retryAfterSeconds: null,
    };
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockRejectedValueOnce(new ApiRequestError(error))
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: null });
    const { harness } = await render();
    await (await harness.next()).click();
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.rows())[0]?.getAttribute('data-row-id')).toBe('event1');
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(await harness.content()).toContain(error.message);
    await (await harness.retry()).click();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, cursor: 'second' },
      expect.any(AbortSignal),
    );
    expect(await (await harness.pager()).text()).toContain('Page 2');
    expect(await (await harness.rows())[0]?.getAttribute('data-row-id')).toBe('second');
  });
  it('locks paging without claiming a pending request during a Retry-After cooldown', async () => {
    list.mockResolvedValueOnce({ items: [event], next_cursor: 'second' }).mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'unavailable',
        status: 503,
        code: 'page_unavailable',
        message: 'Try again later.',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: 60,
      }),
    );
    const { harness } = await render();
    await (await harness.next()).click();
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(document.querySelector('app-data-table-pager [role="progressbar"]')).toBeNull();
    expect(document.querySelector('app-data-table-pager nav')?.getAttribute('aria-busy')).toBe(
      'false',
    );
    await (await harness.next()).click();
    await (await harness.previous()).click();
    await harness.chooseSize('50');
    expect(list).toHaveBeenCalledTimes(2);
    expect(await (await harness.pager()).text()).toContain('Page 1');
  });

  it('keeps the loaded page visible and disables paging while the next request is pending', async () => {
    list.mockResolvedValueOnce({ items: [event], next_cursor: 'second' });
    const { fixture, harness } = await render();
    let finish: (value: { items: SecurityEventItem[]; next_cursor: null }) => void = () =>
      undefined;
    list.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    await manualChangeDetection(async () => {
      await (await harness.next()).click();
      fixture.detectChanges();
      TestBed.tick();
      expect(await harness.rows()).toHaveLength(1);
      expect(await (await harness.pager()).text()).toContain('Page 1');
      expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
      expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('true');
      expect(await (await harness.table()).getAttribute('aria-busy')).toBe('true');
      finish({ items: [{ ...event, id: 'second' }], next_cursor: null });
    });
    await fixture.whenStable();
    expect(await (await harness.pager()).text()).toContain('Page 2');
    expect(await (await harness.rows())[0]?.getAttribute('data-row-id')).toBe('second');
  });
  it('can advance a page containing only hidden V1 events without claiming an empty organization', async () => {
    list
      .mockResolvedValueOnce({
        items: [{ ...event, type: 'injection_attempt' }],
        next_cursor: 'visible',
      })
      .mockResolvedValueOnce({ items: [event], next_cursor: null });
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(0);
    expect(await harness.content()).not.toContain('No security events recorded');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('false');
    await (await harness.next()).click();
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.pager()).text()).toContain('Page 2');
  });
});
