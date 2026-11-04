// The stepper's `aria-current` contract on its own: exactly one step is current, and a move moves
// the marker. A second marker — or a stale one after a move — would tell a screen reader that two
// steps of one process are current, so both halves are asserted on the real shell.
import { mountShell } from './onboarding-shell.spec-support';

/** The steps carrying an `aria-current` marker, in stepper order. */
function currentSteps(root: HTMLElement): (string | null)[] {
  return Array.from(root.querySelectorAll('[data-testid="onboarding-stepper"] > li'))
    .filter((step) => step.hasAttribute('aria-current'))
    .map((step) => step.getAttribute('data-testid'));
}

describe('the onboarding stepper current step', () => {
  it('marks exactly the viewed step with aria-current="step"', async () => {
    const { fixture, root, store } = await mountShell();

    expect(currentSteps(root)).toEqual(['onboarding-step-1']);
    expect(
      root.querySelector('[data-testid="onboarding-step-1"]')?.getAttribute('aria-current'),
    ).toBe('step');

    store.viewStep.set(3);
    await fixture.whenStable();
    expect(currentSteps(root)).toEqual(['onboarding-step-3']);
    expect(
      root.querySelector('[data-testid="onboarding-step-1"]')?.hasAttribute('aria-current'),
    ).toBe(false);
  });
});
