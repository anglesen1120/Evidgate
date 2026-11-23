import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { DashboardWidgetComponent } from './dashboard-widget.component';
import { DashboardKpiComponent } from './dashboard-kpi.component';
import { DashboardMeterComponent } from './dashboard-meter.component';
import { DecisionTrendComponent } from './decision-trend.component';

class DashboardHarness extends ComponentHarness {
  static hostSelector =
    'app-dashboard-widget, app-dashboard-kpi, app-dashboard-meter, app-decision-trend';
  readonly text = async () => (await this.host()).text();
  readonly retry = this.locatorFor('button');
  readonly meter = this.locatorForOptional('meter');
  readonly bars = this.locatorForAll('.trend-stack [data-segment]');
  readonly stacks = this.locatorForAll('.trend-segments');
  readonly totals = this.locatorForAll('.trend-total');
  readonly kpiIcon = this.locatorForOptional('h3 hlm-icon');
  readonly scrollRegion = this.locatorFor('[role="region"]');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly emptyIcon = this.locatorFor('app-empty-pattern hlm-icon');
  readonly emptyIconPath = this.locatorFor('app-empty-pattern hlm-icon path');
}

describe('Dashboard primitives', () => {
  it('keeps loading, empty and failure states distinct and emits deliberate retry', async () => {
    const fixture = TestBed.createComponent(DashboardWidgetComponent);
    fixture.componentRef.setInput('title', 'Recent runs');
    fixture.componentRef.setInput('emptyTitle', 'No runs yet');
    fixture.componentRef.setInput('loading', true);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    expect(await harness.loading()).not.toBeNull();
    fixture.componentRef.setInput('loading', false);
    fixture.componentRef.setInput('empty', true);
    expect(await harness.empty()).not.toBeNull();
    fixture.componentRef.setInput('error', {
      kind: 'unavailable',
      status: 503,
      code: 'busy',
      message: 'Try later',
      requestId: null,
      retryable: true,
      retryAfterSeconds: null,
      details: [],
    });
    let retried = false;
    fixture.componentInstance.retry.subscribe(() => {
      retried = true;
    });
    await (await harness.retry()).click();
    expect(retried).toBe(true);
    expect(await harness.empty()).toBeNull();
  });

  it('defaults to an inbox and forwards an optional contextual empty icon', async () => {
    const fixture = TestBed.createComponent(DashboardWidgetComponent);
    fixture.componentRef.setInput('title', 'Recent runs');
    fixture.componentRef.setInput('empty', true);
    fixture.componentRef.setInput('emptyTitle', 'No runs yet');
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);

    expect(await (await harness.emptyIconPath()).getAttribute('d')).toBe(
      'M22 12h-6l-2 3h-4l-2-3H2',
    );
    fixture.componentRef.setInput('emptyIcon', 'navRun');
    expect(await (await harness.emptyIconPath()).getAttribute('d')).toBe(
      'M5 3h14v18H5z M10 8l5 4-5 4z',
    );
    expect(await (await harness.emptyIcon()).getAttribute('aria-hidden')).toBe('true');
  });

  it('shows an unavailable KPI without inventing a zero', async () => {
    const fixture = TestBed.createComponent(DashboardKpiComponent);
    fixture.componentRef.setInput('label', 'Recent runs');
    fixture.componentRef.setInput('value', null);
    fixture.componentRef.setInput('unavailableLabel', 'Unavailable');
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    expect(await harness.text()).toContain('Unavailable');
    fixture.componentRef.setInput('value', 0);
    expect(await harness.text()).toContain('0');
    expect(await harness.text()).not.toContain('Unavailable');
    fixture.componentRef.setInput('value', '50%');
    expect(await harness.text()).toContain('50%');
  });

  it('only renders a decorative KPI icon on request and keeps its default tone neutral', async () => {
    const fixture = TestBed.createComponent(DashboardKpiComponent);
    fixture.componentRef.setInput('label', 'Recent runs');
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    expect(await harness.kpiIcon()).toBeNull();
    fixture.componentRef.setInput('icon', 'navRun');
    const icon = await harness.kpiIcon();
    expect(await icon?.getAttribute('aria-hidden')).toBe('true');
    expect(await icon?.hasClass('text-[var(--eg-neutral-fg)]')).toBe(true);
    fixture.componentRef.setInput('tone', 'danger');
    const dangerIcon = await harness.kpiIcon();
    expect(await dangerIcon?.hasClass('text-[var(--eg-danger-fg)]')).toBe(true);
    expect(await dangerIcon?.hasClass('text-[var(--eg-neutral-fg)]')).toBe(false);
  });

  it('does not draw a quota ratio with an unknown or zero limit', async () => {
    const fixture = TestBed.createComponent(DashboardMeterComponent);
    fixture.componentRef.setInput('label', 'AI credit');
    fixture.componentRef.setInput('value', 12);
    fixture.componentRef.setInput('maximum', null);
    fixture.componentRef.setInput('description', 'Limit unavailable');
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    expect(await harness.meter()).toBeNull();
    fixture.componentRef.setInput('maximum', 0);
    expect(await harness.meter()).toBeNull();
    fixture.componentRef.setInput('maximum', 10);
    expect(await (await harness.meter())?.getAttribute('value')).toBe('10');
    expect(await harness.text()).toContain('Limit unavailable');
    fixture.componentRef.setInput('value', -1);
    expect(await harness.meter()).toBeNull();
  });

  it('exposes all trend values as text and preserves neutral undecided segments', async () => {
    const fixture = TestBed.createComponent(DecisionTrendComponent);
    fixture.componentRef.setInput('label', 'Recent decisions');
    fixture.componentRef.setInput('points', [
      {
        id: '2026-11-23',
        label: '23/11/2026',
        segments: [
          { id: 'pass', label: 'PASS', value: 2, tone: 'success' },
          { id: 'fail', label: 'FAIL', value: 0, tone: 'danger' },
          { id: 'pending', label: 'Not evaluated', value: 3, tone: 'neutral' },
        ],
      },
    ]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    expect(await harness.text()).toContain('Not evaluated');
    expect(await harness.text()).toContain('3');
    const segments = await harness.bars();
    expect(segments).toHaveLength(2);
    expect(await segments[1]?.getAttribute('data-tone')).toBe('neutral');
    expect(await segments[1]?.getAttribute('data-pattern')).toBe('2');
    const region = await harness.scrollRegion();
    expect(await region.getAttribute('tabindex')).toBe('0');
    expect(await region.getAttribute('aria-label')).toBe('Recent decisions');
    expect(await segments[0]?.getAttribute('aria-hidden')).toBe('true');
  });

  it('scales dated stacks against the largest total and labels zero without drawing a bar', async () => {
    const fixture = TestBed.createComponent(DecisionTrendComponent);
    fixture.componentRef.setInput('label', 'Recent decisions');
    fixture.componentRef.setInput(
      'points',
      [
        [0, 0],
        [3, 0],
        [2, 4],
      ].map(([pass, fail], i) => ({
        id: String(i),
        label: String(i),
        segments: [
          { id: 'pass', label: 'PASS', value: pass, tone: 'success' },
          { id: 'fail', label: 'FAIL', value: fail, tone: 'danger' },
        ],
      })),
    );
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardHarness);
    const heights = await Promise.all(
      (await harness.stacks()).map(
        async (el) => (await el.getProperty<CSSStyleDeclaration>('style')).height,
      ),
    );
    expect(heights).toEqual(['0%', '50%', '100%']);
    const labels = await Promise.all((await harness.totals()).map((el) => el.text()));
    expect(labels).toEqual(['0', '3', '6']);
    const weights = await Promise.all(
      (await harness.bars()).map(
        async (el) => (await el.getProperty<CSSStyleDeclaration>('style')).flexGrow,
      ),
    );
    expect(weights).toEqual(['3', '2', '4']);
  });
});
