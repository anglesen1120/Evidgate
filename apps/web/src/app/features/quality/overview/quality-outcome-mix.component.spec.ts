import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { QualityOutcomeMixComponent } from './quality-outcome-mix.component';
import { qualityFixture } from './quality-overview.fixture';
import { QualityReviewHarness } from './quality-review.harness';

// The chart only describes the complete-run counts on reported days, never the release verdict.
describe('QualityOutcomeMixComponent', () => {
  it('labels each supplied outcome and its share, excluding the missing day', async () => {
    const fixture = TestBed.createComponent(QualityOutcomeMixComponent);
    fixture.componentRef.setInput('days', qualityFixture.days);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.text('strong')).toBe('40');
    const legend = await panel.text('dl');
    expect(legend).toMatch(/Passed\s*34 · 85%/);
    expect(legend).toMatch(/Failed\s*5 · 12.5%/);
    expect(legend).toMatch(/Other\s*1 · 2.5%/);
    expect(await panel.exists('svg circle')).toBe(true);
  });

  it('distinguishes no observation from an observed zero, without drawing a zero-total ring', async () => {
    const fixture = TestBed.createComponent(QualityOutcomeMixComponent);
    fixture.componentRef.setInput('days', [{ date: '2026-10-10', observation: null }]);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.content()).toContain('No data');
    expect(await panel.exists('circle, dl')).toBe(false);
    fixture.componentRef.setInput('days', [
      {
        date: '2026-10-11',
        observation: { percent: 0, passed: 0, failed: 0, other: 0, complete: 0, summary: '' },
      },
    ]);
    expect(await panel.content()).toContain('No complete runs on reported days.');
    expect(await panel.exists('circle, dl')).toBe(false);
  });
});
