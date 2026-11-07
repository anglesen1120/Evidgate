import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { UsageSummaryDto, UsageSummaryMetricDto } from '@evidgate/sdk';

import { QuotaPanelComponent } from './quota-panel.component';
import { QuotaStore, type QuotaLoadState } from './quota.store';

class QuotaPanelHarness extends ComponentHarness {
  static hostSelector = 'app-quota-panel';
  readonly metrics = this.locatorForAll('[data-quota-metric]');
  readonly retry = this.locatorFor('button');
}

function metric(changes: Partial<UsageSummaryMetricDto> = {}): UsageSummaryMetricDto {
  return {
    metric: 'egress_gb',
    scope: 'tenant',
    used: null,
    limit: null,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    unavailable_reason: 'The egress producer is unavailable.',
    projects: [],
    ...changes,
  };
}

describe('QuotaPanelComponent', () => {
  const usageSummary = signal<UsageSummaryDto | null>(null);
  const loadState = signal<QuotaLoadState>('ready');
  const load = vi.fn();
  const reload = vi.fn();

  beforeEach(() => {
    usageSummary.set({ period: '2026-11', project: null, metrics: [metric()] });
    loadState.set('ready');
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      imports: [QuotaPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: QuotaStore,
          useValue: { usageSummary, loadState, load, reload },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(QuotaPanelComponent);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, QuotaPanelHarness);
    return { fixture, harness, root: fixture.nativeElement as HTMLElement };
  }

  it('renders a null metric as unavailable, never zero, with the server reason', async () => {
    const { harness, root } = await render();
    const row = (await harness.metrics())[0];
    expect(row).toBeDefined();
    expect(await row?.text()).toContain('Unavailable');
    expect(await row?.text()).not.toContain('0');
    expect(root.querySelector('[data-quota-reason]')?.textContent).toContain('egress producer');
    expect(root.querySelector('[data-quota-storage]')?.textContent).not.toContain('0');
  });

  it('renders a measured zero as zero rather than unavailable', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric({ used: 0, limit: 100, unavailable_reason: null })],
    });
    const { root } = await render();
    expect(root.querySelector('[data-quota-used]')?.textContent?.trim()).toBe('0');
    expect(root.querySelector('[data-quota-limit]')?.textContent?.trim()).toBe('100');
    expect(load).toHaveBeenCalledOnce();
  });

  it.each(['warn', 'blocked'] as const)('names the served %s state', async (severity) => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [
        metric({
          used: 80,
          limit: 100,
          unavailable_reason: null,
          warned_at: '2026-11-01T00:00:00Z',
          blocked_at: severity === 'blocked' ? '2026-11-02T00:00:00Z' : null,
        }),
      ],
    });
    const { root } = await render();
    expect(root.textContent).toContain(
      severity === 'blocked' ? 'Limit reached' : 'Approaching limit',
    );
  });

  it('announces loading without presenting stale counters', async () => {
    loadState.set('loading');
    const { root } = await render();
    expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading');
    expect(root.querySelector('[data-quota-metric]')).toBeNull();
  });

  it('offers one explicit retry for an unavailable summary', async () => {
    loadState.set('unavailable');
    usageSummary.set(null);
    const { harness, root } = await render();
    expect(root.querySelector('[data-quota-unavailable]')).not.toBeNull();
    await (await harness.retry()).click();
    expect(reload).toHaveBeenCalledOnce();
  });

  it('distinguishes an empty metric list from an unavailable summary', async () => {
    usageSummary.set({ period: '2026-11', project: null, metrics: [] });
    const { root } = await render();
    expect(root.querySelector('[data-quota-empty]')).not.toBeNull();
    expect(root.querySelector('[data-quota-unavailable]')).toBeNull();
  });
});
