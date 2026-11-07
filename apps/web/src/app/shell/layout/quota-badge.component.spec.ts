import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { UsageSummaryDto, UsageSummaryMetricDto, UsageSummaryProjectDto } from '@evidgate/sdk';

import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';
import { TenantStore } from '../../core/session/tenant.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { QuotaBadgeComponent } from './quota-badge.component';

class QuotaBadgeHarness extends ComponentHarness {
  static hostSelector = 'app-quota-badge';
  readonly link = this.locatorForOptional('a');
}

function summary(changes: Partial<UsageSummaryMetricDto> = {}): UsageSummaryDto {
  return {
    period: '2026-11',
    project: null,
    metrics: [
      {
        metric: 'results',
        scope: 'tenant',
        used: 80,
        limit: 100,
        warned_at: '2026-11-01T00:00:00Z',
        blocked_at: null,
        retry_after_seconds: null,
        unavailable_reason: null,
        projects: [],
        ...changes,
      },
    ],
  };
}

/** One project's counter under the metric; below both lines unless the test says otherwise. */
function project(changes: Partial<UsageSummaryProjectDto> = {}): UsageSummaryProjectDto {
  return {
    project_id: 'project-one',
    scope: 'project',
    used: 1,
    limit: 100,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    ...changes,
  };
}

describe('QuotaBadgeComponent', () => {
  const granted = signal(true);
  const read = vi.fn<(...args: unknown[]) => Promise<UsageSummaryDto | null>>();

  beforeEach(() => {
    granted.set(true);
    read.mockReset().mockResolvedValue(summary());
    TestBed.configureTestingModule({
      imports: [QuotaBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: QuotaDataService, useValue: { summary: read } },
        { provide: SessionStore, useValue: { has: () => granted() } },
      ],
    });
    TestBed.inject(TenantStore).setOrg('org-one');
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(QuotaBadgeComponent);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, QuotaBadgeHarness);
    return { fixture, harness };
  }

  it('links a warned metric to the current organization quota tab', async () => {
    read.mockResolvedValue(summary({ used: 1 }));
    const { harness } = await render();
    const link = await harness.link();
    expect(await link?.getAttribute('data-quota-severity')).toBe('warn');
    expect(await link?.text()).toContain('Approaching quota limit:');
    expect(await link?.text()).toContain('Results');
    expect(await link?.getAttribute('href')).toBe('/orgs/org-one/settings/quota-retention');
    const classes = (await link?.getAttribute('class')) ?? '';
    // The link is a control: its touch minimum must win over the domain badge's chip minimum.
    expect(classes).toContain('min-h-(--eg-control-touch)');
    expect(classes).toContain('min-w-(--eg-control-touch)');
    expect(classes).not.toContain('min-h-[var(--eg-space-6)]');
    expect(classes).not.toContain('min-w-0');
    await link?.focus();
    expect(await link?.isFocused()).toBe(true);
  });

  it('uses the blocked style and names the metric even without a warning timestamp', async () => {
    read.mockResolvedValue(
      summary({ metric: 'ai_tokens', warned_at: null, blocked_at: '2026-11-02T00:00:00Z' }),
    );
    const { harness } = await render();
    const link = await harness.link();
    expect(await link?.getAttribute('data-quota-severity')).toBe('blocked');
    expect(await link?.text()).toContain('Quota limit reached:');
    expect(await link?.text()).toContain('AI tokens');
  });

  it('prioritizes a blocked metric over an earlier warned metric', async () => {
    const payload = summary();
    const blocked = summary({ metric: 'ai_tokens', blocked_at: '2026-11-02T00:00:00Z' }).metrics[0];
    if (!blocked) throw new Error('Expected the blocked metric');
    read.mockResolvedValue({ ...payload, metrics: [...payload.metrics, blocked] });
    const { harness } = await render();
    expect(await (await harness.link())?.text()).toContain('AI tokens');
  });

  it('names a project counter the tenant counter does not warn about', async () => {
    read.mockResolvedValue(
      summary({
        used: 10,
        limit: 100,
        warned_at: null,
        projects: [project({ warned_at: '2026-11-01T09:00:00Z' })],
      }),
    );
    const { harness } = await render();
    const link = await harness.link();
    expect(await link?.getAttribute('data-quota-severity')).toBe('warn');
    expect(await link?.getAttribute('data-quota-scope')).toBe('project');
    expect(await link?.text()).toContain('Approaching quota limit:');
    expect(await link?.text()).toContain('Results');
    expect(await link?.text()).toContain('(project)');
  });

  it('names a blocked project counter without a tenant warning', async () => {
    read.mockResolvedValue(
      summary({
        used: 10,
        limit: 100,
        warned_at: null,
        projects: [project({ used: 100, blocked_at: '2026-11-02T00:00:00Z' })],
      }),
    );
    const { harness } = await render();
    const link = await harness.link();
    expect(await link?.getAttribute('data-quota-severity')).toBe('blocked');
    expect(await link?.getAttribute('data-quota-scope')).toBe('project');
    expect(await link?.text()).toContain('Quota limit reached:');
    expect(await link?.text()).toContain('(project)');
  });

  it('hides and does not read without usage permission; restores with permission', async () => {
    granted.set(false);
    const { fixture, harness } = await render();
    expect(await harness.link()).toBeNull();
    expect(read).not.toHaveBeenCalled();
    granted.set(true);
    await fixture.whenStable();
    expect(await harness.link()).not.toBeNull();
    expect(read).toHaveBeenCalledOnce();
  });

  it.each(['null', 'error'] as const)('hides an unavailable %s summary', async (state) => {
    if (state === 'null') read.mockResolvedValue(null);
    else read.mockRejectedValue(new Error('Usage summary unavailable'));
    const { harness } = await render();
    expect(await harness.link()).toBeNull();
  });

  it('never infers a warning from a count or from an unavailable metric', async () => {
    read.mockResolvedValue(summary({ used: 100, warned_at: null }));
    const { fixture, harness } = await render();
    expect(await harness.link()).toBeNull();
    read.mockResolvedValue(summary({ used: null, unavailable_reason: 'Producer unavailable' }));
    TestBed.inject(QuotaStore).reload();
    await fixture.whenStable();
    expect(await harness.link()).toBeNull();
  });

  it('reloads on organization switch and replaces the metric and link', async () => {
    const { fixture, harness } = await render();
    expect(read).toHaveBeenCalledOnce();
    read.mockResolvedValue(summary({ metric: 'ai_tokens', blocked_at: '2026-11-02T00:00:00Z' }));
    TestBed.inject(TenantStore).setOrg('org-two');
    await fixture.whenStable();
    expect(read).toHaveBeenCalledTimes(2);
    expect(read.mock.calls[1]?.[0]).toBe('org-two');
    expect(await (await harness.link())?.getAttribute('href')).toBe(
      '/orgs/org-two/settings/quota-retention',
    );
    expect(await (await harness.link())?.text()).toContain('AI tokens');
  });
});
