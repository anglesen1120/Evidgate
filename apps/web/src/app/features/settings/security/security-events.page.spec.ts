import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
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
  readonly images = this.locatorForAll('img');
  readonly details = this.locatorFor('summary');
  readonly project = this.locatorFor('#security-project');
  readonly apply = this.locatorFor('[data-security-apply]');
  readonly clear = this.locatorFor('[data-security-clear]');
  readonly emptyReset = this.locatorFor('app-empty-pattern button');
  readonly results = this.locatorFor('#security-results');
  readonly from = this.locatorFor('#security-from');
  readonly to = this.locatorFor('#security-to');
  readonly correctionSummary = this.locatorForOptional('#security-filter-errors');
  readonly correctionLinks = this.locatorForAll('#security-filter-errors a');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly loading = this.locatorForOptional('app-loading-pattern');
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
    const harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(SecurityPageHarness);
    return { fixture, harness };
  }
  it('renders support access with its grant and displays untrusted detail text without HTML', async () => {
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(1);
    expect(await harness.content()).toContain('Support access');
    await (await harness.details()).click();
    expect(await harness.content()).toContain('grant1');
    expect(await harness.content()).toContain('<img src=x onerror=alert(1)>');
    expect(await harness.images()).toHaveLength(0);
  });
  it('hides injection_attempt under V1 while preserving unknown future types verbatim', async () => {
    const future: SecurityEventRow = { ...event, id: 'future', type: 'future_alert' };
    const injection: SecurityEventRow = { ...event, id: 'injection', type: 'injection_attempt' };
    list.mockResolvedValue({ items: [event, future, injection], next_cursor: null });
    const { harness } = await render();
    expect(await harness.rows()).toHaveLength(2);
    expect(await harness.content()).toContain('future_alert');
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
    await (await harness.project()).setInputValue('018f0000-0000-7000-8000-000000000011');
    await (await harness.apply()).click();
    expect(await harness.content()).toContain('No results match these filters');
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 50, project_id: '018f0000-0000-7000-8000-000000000011' },
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
      const harness =
        await TestbedHarnessEnvironment.loader(fixture).getHarness(SecurityPageHarness);
      expect(await harness.loading()).not.toBeNull();
      finish({ items: [event], next_cursor: null });
    });
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(SecurityPageHarness);
    expect(await harness.rows()).toHaveLength(1);
  });
  it('hands focus from the no-match reset to stable results before loading removes the action', async () => {
    const { fixture, harness } = await render();
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    await (await harness.project()).setInputValue('missing-project');
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
      await (await harness.from()).setInputValue(from);
      await (await harness.to()).setInputValue(to);
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
      await (await harness.project()).setInputValue('another-project');
      expect(await harness.correctionSummary()).not.toBeNull();
      expect(await (await harness.from()).getAttribute('aria-invalid')).toBe('true');
      await (await harness.to()).setInputValue('2026-11-18T11:00');
      expect(await harness.correctionSummary()).toBeNull();
      expect(await (await harness.from()).getAttribute('aria-invalid')).toBeNull();
      expect(list).toHaveBeenCalledTimes(1);
      await (await harness.apply()).click();
      expect(list).toHaveBeenCalledTimes(2);
    },
  );
});
