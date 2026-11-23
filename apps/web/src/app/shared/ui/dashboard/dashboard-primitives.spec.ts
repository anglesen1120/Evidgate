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
  readonly bars = this.locatorForAll('[data-segment]');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern');
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
    expect(await segments[0]?.getAttribute('aria-hidden')).toBe('true');
  });
});
