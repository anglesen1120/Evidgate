import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { DecisionTrendComponent, type DashboardTrendPoint } from './decision-trend.component';

class TrendHarness extends ComponentHarness {
  static hostSelector = 'app-decision-trend';
  readonly days = this.locatorForAll('button');
  readonly tooltip = this.locatorForOptional('[role="tooltip"]');
  readonly panel = this.locatorFor('[popover]');
  readonly tableToggle = this.locatorFor('summary');
}

const days: readonly DashboardTrendPoint[] = [
  {
    id: '2026-11-23',
    label: '11/23/2026',
    segments: [
      { id: 'pass', label: 'PASS', value: 2, tone: 'success' },
      { id: 'fail', label: 'FAIL', value: 0, tone: 'danger' },
      { id: 'inconclusive', label: 'INCONCLUSIVE', value: 3, tone: 'neutral' },
      { id: 'pending', label: 'Not evaluated', value: 1, tone: 'neutral' },
    ],
  },
  {
    id: '2026-11-24',
    label: '11/24/2026',
    segments: [
      { id: 'pass', label: 'PASS', value: 0, tone: 'success' },
      { id: 'fail', label: 'FAIL', value: 0, tone: 'danger' },
      { id: 'inconclusive', label: 'INCONCLUSIVE', value: 0, tone: 'neutral' },
      { id: 'pending', label: 'Not evaluated', value: 0, tone: 'neutral' },
    ],
  },
];

async function setup() {
  const fixture = TestBed.createComponent(DecisionTrendComponent);
  fixture.componentRef.setInput('label', 'Recent decisions');
  fixture.componentRef.setInput('points', days);
  const chart = await TestbedHarnessEnvironment.harnessForFixture(fixture, TrendHarness);
  const [first, second] = await chart.days();
  if (!first || !second) throw new Error('Expected a day button for each UTC date');
  return { chart, first, second };
}

function expectFirstDay(text: string | null | undefined): void {
  for (const value of [
    '11/23/2026',
    'UTC',
    'PASS: 2',
    'FAIL: 0',
    'INCONCLUSIVE: 3',
    'Not evaluated: 1',
    'Total: 6',
  ]) {
    expect(text).toContain(value);
  }
}

describe('Decision trend day inspection', () => {
  it('shows the complete day on hover and dismisses it on pointer leave', async () => {
    const { chart, first } = await setup();
    expect(await chart.tooltip()).toBeNull();
    await first.dispatchEvent('pointerenter', { pointerType: 'mouse' });
    const tooltip = await chart.tooltip();
    expectFirstDay(await tooltip?.text());
    expect(await tooltip?.getCssValue('display')).not.toBe('none');
    await first.dispatchEvent('pointerleave', { pointerType: 'mouse' });
    expect(await chart.tooltip()).toBeNull();
    expect(await (await chart.panel()).getCssValue('display')).toBe('none');
  });

  it('names the button before focus, shows its values on focus, and dismisses with Escape without moving focus', async () => {
    const { chart, first } = await setup();
    expectFirstDay(await first.getAttribute('aria-label'));
    await first.focus();
    expectFirstDay(await (await chart.tooltip())?.text());
    await first.sendKeys(TestKey.ESCAPE);
    expect(await chart.tooltip()).toBeNull();
    expect(await first.isFocused()).toBe(true);
  });

  it('uses one tab stop for the dates and follows arrow, End and Home navigation', async () => {
    const { chart, first, second } = await setup();
    expect(await first.getProperty<number>('tabIndex')).toBe(0);
    expect(await second.getProperty<number>('tabIndex')).toBe(-1);
    await first.focus();
    await first.sendKeys(TestKey.RIGHT_ARROW);
    expect(await second.isFocused()).toBe(true);
    expect(await first.getProperty<number>('tabIndex')).toBe(-1);
    expect(await second.getProperty<number>('tabIndex')).toBe(0);
    const text = await (await chart.tooltip())?.text();
    expect(text).toContain('11/24/2026');
    expect(text).toContain('Total: 0');
    await second.sendKeys(TestKey.HOME);
    expect(await first.isFocused()).toBe(true);
    await first.sendKeys(TestKey.END);
    expect(await second.isFocused()).toBe(true);
    await (await chart.tableToggle()).focus();
    expect(await chart.tooltip()).toBeNull();
  });

  it('opens on touch activation rather than touch hover and survives finger lift', async () => {
    const { chart, first } = await setup();
    await first.dispatchEvent('pointerenter', { pointerType: 'touch' });
    expect(await chart.tooltip()).toBeNull();
    await first.click();
    await first.dispatchEvent('pointerleave', { pointerType: 'touch' });
    expectFirstDay(await (await chart.tooltip())?.text());
    await (await chart.tableToggle()).dispatchEvent('pointerdown', { pointerType: 'touch' });
    expect(await chart.tooltip()).toBeNull();
  });
});
