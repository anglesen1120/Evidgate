import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmIcon } from '@evidgate/ui/icon';
import { ReviewStatusChipComponent } from './review-status-chip.component';

class ReviewChipHarness extends ComponentHarness {
  static hostSelector = 'app-review-status-chip';
  readonly badge = this.locatorFor('[data-slot="badge"]');
  readonly icon = this.locatorFor('hlm-icon[aria-hidden="true"] svg');
}

const receipt = {
  reviewer: 'Minh Trần',
  at: '2026-10-12T10:00:00Z',
  candidateHash: 'ch-8af2…3e20',
};

describe('ReviewStatusChipComponent', () => {
  it.each([
    ['waiting', 'Awaiting review', 'pending'],
    ['over-sla', 'Over SLA', 'warning'],
    ['validated', 'Validated', 'info'],
    [{ approval: receipt }, 'Approved', 'success'],
    [{ approval: { ...receipt, reviewer: ' ' } }, 'Unavailable', 'neutral'],
    [{ approval: { ...receipt, candidateHash: '' } }, 'Unavailable', 'neutral'],
    [{ approval: { ...receipt, at: '' } }, 'Unavailable', 'neutral'],
    [{ approval: { ...receipt, reviewer: false } }, 'Unavailable', 'neutral'],
    ['approved', 'Unavailable', 'neutral'],
    [null, 'Unavailable', 'neutral'],
  ] as const)(
    'requires text and an icon for %j; only a recorded approval is green',
    async (state, label, tone) => {
      const fixture = TestBed.createComponent(ReviewStatusChipComponent);
      fixture.componentRef.setInput('state', state);
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewChipHarness);
      const badge = await harness.badge();
      expect(await badge.text()).toBe(label);
      expect(await badge.getAttribute('data-tone')).toBe(tone);
      expect(await harness.icon()).toBeDefined();
    },
  );

  it('updates the text, icon and success styling when approval evidence is lost', async () => {
    const fixture = TestBed.createComponent(ReviewStatusChipComponent);
    fixture.componentRef.setInput('state', { approval: receipt });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewChipHarness);
    for (const [state, label, icon, tone] of [
      [{ approval: receipt }, 'Approved', 'lucideCircleCheck', 'success'],
      ['waiting', 'Awaiting review', 'lucideClock', 'pending'],
      ['approved', 'Unavailable', 'lucideCircle', 'neutral'],
    ] as const) {
      fixture.componentRef.setInput('state', state);
      const badge = await harness.badge();
      expect(await badge.text()).toBe(label);
      expect(await badge.getAttribute('data-tone')).toBe(tone);
      expect(await harness.icon()).toBeDefined();
      expect(fixture.debugElement.query(By.directive(HlmIcon)).injector.get(HlmIcon).name()).toBe(
        icon,
      );
      expect(await badge.hasClass('bg-[var(--eg-success-bg)]')).toBe(tone === 'success');
      expect(await badge.hasClass('text-[var(--eg-success-fg)]')).toBe(tone === 'success');
    }
  });
});
