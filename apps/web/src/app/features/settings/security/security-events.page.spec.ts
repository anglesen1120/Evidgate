import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { SecurityEventItem } from '@evidgate/sdk';
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
    const future = { ...event, id: 'future', type: 'future_alert' } as SecurityEventItem;
    const injection = { ...event, id: 'injection', type: 'injection_attempt' } as SecurityEventItem;
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
});
