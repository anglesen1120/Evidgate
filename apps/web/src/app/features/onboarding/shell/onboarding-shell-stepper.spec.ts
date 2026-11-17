import { DOWN_ARROW, END, HOME, LEFT_ARROW, RIGHT_ARROW } from '@angular/cdk/keycodes';
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
});
