import { DOWN_ARROW, END, HOME, LEFT_ARROW, RIGHT_ARROW } from '@angular/cdk/keycodes';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import type { StepNumber } from '../data/step-derivation';
import { mountShell } from './onboarding-shell.spec-support';

function stepButton(root: HTMLElement, step: StepNumber): HTMLButtonElement {
  const button = root.querySelector<HTMLButtonElement>(
    `[data-testid="onboarding-select-step-${step}"]`,
  );
  if (!button) throw new Error(`Missing step ${step}`);
  return button;
}

function currentSteps(root: HTMLElement): (string | null)[] {
  return Array.from(
    root.querySelectorAll('[data-testid="onboarding-stepper"] button[aria-current="step"]'),
  ).map((step) => step.getAttribute('data-testid'));
}

class NavigatorHarness extends ComponentHarness {
  static hostSelector = 'app-onboarding-shell';
  readonly toggle = this.locatorFor('[data-testid="onboarding-step-toggle"]');
  readonly viewed = this.locatorFor('[data-testid="onboarding-viewed-summary"]');
  readonly next = this.locatorFor('[data-testid="onboarding-next-summary"]');
  readonly context = this.locatorForOptional('[data-testid="onboarding-project-context"]');
  readonly heading = this.locatorFor('#onboarding-step-title');
  readonly list = this.locatorFor('[data-testid="onboarding-stepper"]');
  readonly resources = this.locatorFor('[data-testid="onboarding-project-context"] a');
  readonly ledger = this.locatorFor('#onboarding-resources');

  step(number: StepNumber) {
    return this.locatorFor(`[data-testid="onboarding-select-step-${number}"]`)();
  }
}

describe('the onboarding step navigator', () => {
  it('provides one ordered navigator and a full-width body with one meaningful heading', async () => {
    const { root } = await mountShell();
    expect(root.querySelectorAll('nav')).toHaveLength(1);
    const stepper = root.querySelector('[data-testid="onboarding-stepper"]');
    expect(stepper?.tagName).toBe('OL');
    expect(stepper?.querySelectorAll(':scope > li')).toHaveLength(5);
    expect(stepper?.querySelectorAll('button')).toHaveLength(5);
    expect(root.querySelector('hlm-select')).toBeNull();
    expect(root.querySelector('#onboarding-details-title')).toBeNull();
    const body = root.querySelector('#onboarding-step-body');
    const ledger = root.querySelector('app-onboarding-evidence-panel');
    expect(body?.getAttribute('aria-labelledby')).toBe('onboarding-step-title');
    expect(body?.querySelectorAll('h2')).toHaveLength(1);
    expect(body?.contains(ledger)).toBe(false);
    expect(body?.nextElementSibling).toBe(ledger);
  });

  it('keeps completion, the next required step and the viewed step independent', async () => {
    const { fixture, root, store } = await mountShell();
    store.done.update((done) => ({ ...done, 1: true }));
    store.viewStep.set(2);
    await fixture.whenStable();
    const progress = root.querySelector('[role="progressbar"]');
    expect(progress?.getAttribute('aria-valuenow')).toBe('1');
    expect(progress?.getAttribute('aria-valuemax')).toBe('5');
    expect(currentSteps(root)).toEqual(['onboarding-select-step-2']);
    expect(
      stepButton(root, 1).querySelector('[data-step-status="complete"]')?.textContent,
    ).toContain('Complete');
    expect(
      stepButton(root, 2).querySelector('[data-step-status="required"]')?.textContent,
    ).toContain('Next required');
    expect(
      stepButton(root, 3).querySelector('[data-step-status="upcoming"]')?.textContent,
    ).toContain('Upcoming');
    for (const step of [1, 2, 3] as const) {
      expect(stepButton(root, step).querySelector('hlm-icon[aria-hidden="true"]')).not.toBeNull();
    }
    stepButton(root, 1).click();
    await fixture.whenStable();
    expect(currentSteps(root)).toEqual(['onboarding-select-step-1']);
    expect(stepButton(root, 1).textContent).toContain('Viewing');
    expect(store.currentStep()).toBe(2);
    expect(store.done()[2]).toBe(false);
    expect(progress?.getAttribute('aria-valuenow')).toBe('1');
    expect(root.querySelector('[data-testid="onboarding-progress-count"]')?.textContent).toContain(
      '1 of 5',
    );
    expect(stepButton(root, 2).querySelector('[data-step-status="required"]')).not.toBeNull();
    expect(document.activeElement?.id).toBe('onboarding-step-title');
  });

  it('keeps blocked steps focusable, explains the prerequisite and refuses activation', async () => {
    const { fixture, root, store } = await mountShell();
    const blocked = stepButton(root, 5);
    expect(blocked.disabled).toBe(false);
    expect(blocked.tabIndex).toBe(0);
    expect(blocked.getAttribute('aria-disabled')).toBe('true');
    const reason = root.querySelector(`#${blocked.getAttribute('aria-describedby')}`);
    expect(reason?.textContent?.trim()).toBe('Finish earlier steps to open later steps.');
    blocked.focus();
    expect(document.activeElement).toBe(blocked);
    blocked.click();
    await fixture.whenStable();
    expect(store.selectStep).not.toHaveBeenCalled();
    expect(store.viewStep()).toBe(1);
    expect(currentSteps(root)).toEqual(['onboarding-select-step-1']);
  });

  it('moves keyboard focus through every step without selecting or completing one', async () => {
    const { root, store } = await mountShell();
    // CDK consumes native keyCode; jsdom does not derive it from the event's key.
    const keys = {
      ArrowRight: RIGHT_ARROW,
      ArrowDown: DOWN_ARROW,
      End: END,
      ArrowLeft: LEFT_ARROW,
      Home: HOME,
    };
    const move = (step: StepNumber, key: keyof typeof keys, target: StepNumber): void => {
      const source = stepButton(root, step);
      source.focus();
      source.dispatchEvent(
        new KeyboardEvent('keydown', { key, keyCode: keys[key], bubbles: true, cancelable: true }),
      );
      expect(document.activeElement).toBe(stepButton(root, target));
    };
    move(1, 'ArrowRight', 2);
    move(2, 'ArrowDown', 3);
    move(3, 'End', 5);
    move(5, 'ArrowRight', 1);
    move(1, 'ArrowLeft', 5);
    move(5, 'Home', 1);
    expect(store.selectStep).not.toHaveBeenCalled();
    expect(store.viewStep()).toBe(1);
    expect(Object.values(store.done())).toEqual([false, false, false, false, false]);
  });

  it('retains the completion count when opening a different confirmed step', async () => {
    const { fixture, root, store } = await mountShell();
    store.currentStep.set('enabled');
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: true });
    await fixture.whenStable();
    expect(root.querySelector('#onboarding-locked-reason')).toBeNull();
    stepButton(root, 5).click();
    await fixture.whenStable();
    expect(store.selectStep).toHaveBeenCalledExactlyOnceWith(5);
    expect(currentSteps(root)).toEqual(['onboarding-select-step-5']);
    expect(root.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('5');
    expect(stepButton(root, 5).getAttribute('aria-controls')).toBe('onboarding-step-body');
  });

  it('rebuilds keyboard navigation after the ready region is replaced', async () => {
    const { fixture, root, store } = await mountShell();
    store.status.set('loading');
    await fixture.whenStable();
    expect(root.querySelector('nav')).toBeNull();
    store.status.set('ready');
    await fixture.whenStable();
    const first = stepButton(root, 1);
    first.focus();
    first.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowRight', keyCode: RIGHT_ARROW, bubbles: true }),
    );
    expect(document.activeElement).toBe(stepButton(root, 2));
  });

  it('discloses the same ordered list and returns focus on Escape without changing the step', async () => {
    const viewport = new OnboardingViewport();
    viewport.setWidth(390);
    const { fixture, store } = await mountShell({ viewport });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    const toggle = await harness.toggle();
    expect(await toggle.getAttribute('aria-controls')).toBe('onboarding-step-list');
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    await toggle.click();
    expect(await toggle.getAttribute('aria-expanded')).toBe('true');
    await toggle.sendKeys(TestKey.ESCAPE);
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await toggle.isFocused()).toBe(true);
    await toggle.click();
    const blocked = await harness.step(5);
    await blocked.focus();
    await blocked.click();
    expect(await toggle.getAttribute('aria-expanded')).toBe('true');
    expect(store.selectStep).not.toHaveBeenCalled();
    await blocked.sendKeys(TestKey.ESCAPE);
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await toggle.isFocused()).toBe(true);
    expect(store.viewStep()).toBe(1);
  });

  it('focuses a chosen step only once its heading shows that step', async () => {
    const viewport = new OnboardingViewport();
    viewport.setWidth(390);
    const { fixture, root } = await mountShell({ viewport });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    const heading = root.querySelector<HTMLElement>('#onboarding-step-title');
    if (!heading) throw new Error('Missing step heading');
    const announced: string[] = [];
    heading.addEventListener('focus', () => announced.push(heading.textContent?.trim() ?? ''));
    await (await harness.toggle()).click();
    await (await harness.step(2)).click();
    await fixture.whenStable();
    expect(announced).toEqual(['Connect CI']);
    expect(await (await harness.heading()).isFocused()).toBe(true);
  });

  it('hands the disclosure focus to the viewed step when the wide navigator replaces it', async () => {
    const viewport = new OnboardingViewport();
    viewport.setWidth(767);
    const { fixture, root, store } = await mountShell({ viewport });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    const toggle = await harness.toggle();
    await toggle.focus();
    viewport.setWidth(768);
    await fixture.whenStable();
    expect(await toggle.hasClass('eg-wide-only-hidden')).toBe(true);
    expect(await (await harness.step(store.viewStep())).isFocused()).toBe(true);

    viewport.setWidth(767);
    await fixture.whenStable();
    expect(await toggle.hasClass('eg-wide-only-hidden')).toBe(false);
    await toggle.click();
    expect(await toggle.getAttribute('aria-expanded')).toBe('true');
    viewport.setWidth(768);
    await fixture.whenStable();
    expect(await toggle.hasClass('eg-wide-only-hidden')).toBe(true);
    expect(await (await harness.step(store.viewStep())).isFocused()).toBe(true);
    expect(await toggle.getAttribute('aria-expanded')).toBe('true');
    expect(store.viewStep()).toBe(1);

    viewport.setWidth(767);
    await fixture.whenStable();
    expect(await toggle.hasClass('eg-wide-only-hidden')).toBe(false);
    const link = root.querySelector<HTMLAnchorElement>(
      '[data-testid="onboarding-project-context"] a',
    );
    if (!link) throw new Error('Missing ledger link');
    link.focus();
    viewport.setWidth(768);
    await fixture.whenStable();
    expect(document.activeElement).toBe(link);
  });

  it('collapses on a step choice and focuses its heading, including the already viewed step', async () => {
    const { fixture, store } = await mountShell();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    const toggle = await harness.toggle();
    for (const step of [2, 2, 1] as const) {
      await toggle.click();
      await (await harness.step(step)).click();
      await fixture.whenStable();
      expect(await toggle.getAttribute('aria-expanded')).toBe('false');
      expect(await (await harness.heading()).isFocused()).toBe(true);
      expect(store.viewStep()).toBe(step);
    }
  });

  it('preserves viewing and disclosure state across the 767/768 boundary', async () => {
    const viewport = new OnboardingViewport();
    viewport.setWidth(767);
    const { fixture, store } = await mountShell({ viewport });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    const toggle = await harness.toggle();
    await toggle.click();
    for (const width of [768, 767, 1440, 390]) {
      viewport.setWidth(width);
      await fixture.whenStable();
      expect(await toggle.getAttribute('aria-expanded')).toBe('true');
      expect(store.viewStep()).toBe(1);
    }
    await (await harness.step(2)).click();
    viewport.setWidth(768);
    await fixture.whenStable();
    await (await harness.step(1)).focus();
    viewport.setWidth(767);
    await fixture.whenStable();
    expect(await toggle.isFocused()).toBe(true);
    expect(store.viewStep()).toBe(2);
  });

  it.each([
    [1, 1, 'Viewing 1 of 5: Create project', 'Next required 1 of 5: Create project.'],
    [1, 3, 'Viewing 1 of 5: Create project', 'Next required 3 of 5: Sample run.'],
    [5, 'enabled', 'Viewing 5 of 5: Enable collection', 'All 5 steps complete.'],
  ] as const)(
    'summarizes viewed %s and required %s independently',
    async (viewed, next, viewingText, nextText) => {
      const { fixture, store } = await mountShell();
      store.viewStep.set(viewed);
      store.currentStep.set(next);
      await fixture.whenStable();
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
      expect(await (await harness.viewed()).text()).toBe(viewingText);
      expect(await (await harness.next()).text()).toContain(nextText);
    },
  );

  it('shows only the confirmed project context and links to the single ledger', async () => {
    const { fixture, root, store } = await mountShell();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigatorHarness);
    expect(await (await harness.context())?.text()).toContain('Storefront web');
    // The compact statements are separate blocks: the next-required sentence closes before the
    // project context starts, and the ledger link carries this wizard's own address.
    const next = await harness.next();
    expect((await next.text()).trim().endsWith('.')).toBe(true);
    const nextBlock = root.querySelector('[data-testid="onboarding-next-summary"]');
    const contextBlock = root.querySelector('[data-testid="onboarding-project-context"]');
    expect(nextBlock?.querySelector('[data-testid="onboarding-project-context"]')).toBeNull();
    expect(contextBlock?.parentElement).toBe(nextBlock?.parentElement);
    expect(contextBlock?.tagName).toBe('P');
    const link = root.querySelector<HTMLAnchorElement>(
      '[data-testid="onboarding-project-context"] a',
    );
    expect(link?.getAttribute('href')).toBe('/vi/orgs/org-1/onboarding#onboarding-resources');
    expect(root.querySelectorAll('#onboarding-resources')).toHaveLength(1);
    // A modified click stays the browser's: nothing is prevented and focus does not move.
    if (!link) throw new Error('Missing ledger link');
    const modified = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
      ctrlKey: true,
    });
    expect(link.dispatchEvent(modified)).toBe(true);
    expect(document.activeElement).not.toBe(root.querySelector('#onboarding-resources'));
    await (await harness.resources()).click();
    expect(await (await harness.ledger()).isFocused()).toBe(true);
    store.project.set({ id: 'p1', name: 'Project\u202Ename' });
    await fixture.whenStable();
    expect(await (await harness.context())?.text()).toContain('Project⟨U+202E⟩name');
    store.project.set(null);
    await fixture.whenStable();
    expect(await harness.context()).toBeNull();
  });
});
