import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import { mountShell } from './onboarding-shell.spec-support';

class StepperHarness extends ComponentHarness {
  static hostSelector = 'app-onboarding-shell';
  readonly toggle = this.locatorFor('[data-testid="onboarding-step-toggle"]');
  readonly list = this.locatorFor('[data-testid="onboarding-stepper"]');
  readonly current = this.locatorForAll('[data-testid="onboarding-stepper"] [aria-current="step"]');
  readonly tabs = this.locatorForAll('[role="tab"], [role="tablist"]');
  readonly blocked = this.locatorFor('[data-testid="onboarding-select-step-5"]');
  readonly second = this.locatorFor('[data-testid="onboarding-select-step-2"]');
}

describe('Onboarding narrow horizontal stepper boundaries', () => {
  it.each([599, 600, 767, 768, 900])(
    'preserves one current step and a non-wrapping local strip at %ipx',
    async (width) => {
      const viewport = new OnboardingViewport();
      viewport.setWidth(width);
      const { fixture, store } = await mountShell({ viewport });
      const ui = await TestbedHarnessEnvironment.harnessForFixture(fixture, StepperHarness);
      if (width < 768) await (await ui.toggle()).click();
      const list = await ui.list();
      expect(await list.hasClass('flex-nowrap')).toBe(true);
      expect(await list.hasClass('overflow-x-auto')).toBe(true);
      expect(await ui.tabs()).toHaveLength(0);
      expect(await ui.current()).toHaveLength(1);
      expect(await (await ui.current())[0]?.getAttribute('data-testid')).toBe(
        'onboarding-select-step-1',
      );
      const blocked = await ui.blocked();
      expect(await blocked.getAttribute('aria-disabled')).toBe('true');
      await blocked.click();
      expect(store.selectStep).not.toHaveBeenCalled();
      expect(store.viewStep()).toBe(1);
      expect(await ui.current()).toHaveLength(1);
    },
  );

  it.each([599, 600])('returns focus after narrow disclosure Escape at %ipx', async (width) => {
    const viewport = new OnboardingViewport();
    viewport.setWidth(width);
    const { fixture, store } = await mountShell({ viewport });
    const ui = await TestbedHarnessEnvironment.harnessForFixture(fixture, StepperHarness);
    const toggle = await ui.toggle();
    await toggle.click();
    const blocked = await ui.blocked();
    await blocked.focus();
    await blocked.sendKeys(TestKey.ESCAPE);
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await toggle.isFocused()).toBe(true);
    expect(store.viewStep()).toBe(1);
    expect(store.selectStep).not.toHaveBeenCalled();
  });

  it('changes the viewed aria-current marker without claiming completion', async () => {
    const { fixture, store } = await mountShell();
    const ui = await TestbedHarnessEnvironment.harnessForFixture(fixture, StepperHarness);
    await (await ui.second()).click();
    expect(await ui.current()).toHaveLength(1);
    expect(await (await ui.current())[0]?.getAttribute('data-testid')).toBe(
      'onboarding-select-step-2',
    );
    expect(store.done()[2]).toBe(false);
    expect(store.currentStep()).toBe(2);
  });
});
