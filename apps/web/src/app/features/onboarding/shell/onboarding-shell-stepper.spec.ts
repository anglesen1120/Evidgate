// The stepper's `aria-current` contract on its own: exactly one step is current, and a move moves
// the marker. A second marker — or a stale one after a move — would tell a screen reader that two
// steps of one process are current, so both halves are asserted on the real shell.
import { mountShell } from './onboarding-shell.spec-support';

/** The steps carrying an `aria-current` marker, in stepper order. */
function currentSteps(root: HTMLElement): (string | null)[] {
  return Array.from(
    root.querySelectorAll(
      '[data-testid="onboarding-stepper"] [data-testid^="onboarding-step-"][aria-current="step"]',
    ),
  ).map((step) => step.getAttribute('data-testid'));
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
  it('derives numeric progress only from confirmed resources, not the viewed step', async () => {
    const { fixture, root, store } = await mountShell();
    const progress = root.querySelector('[role="progressbar"]')!;
    expect(progress.getAttribute('aria-valuenow')).toBe('0');
    expect(progress.getAttribute('aria-valuemax')).toBe('5');
    store.viewStep.set(4);
    await fixture.whenStable();
    expect(progress.getAttribute('aria-valuenow')).toBe('0');
    store.done.update((done) => ({ ...done, 1: true, 2: true }));
    await fixture.whenStable();
    expect(progress.getAttribute('aria-valuenow')).toBe('2');
    expect(root.querySelector('[data-testid="onboarding-progress-count"]')?.textContent).toContain(
      '2 of 5',
    );
    const details = root.querySelector('[aria-labelledby="onboarding-details-title"]')!;
    const ledger = root.querySelector('app-onboarding-evidence-panel')!;
    expect(details.contains(ledger)).toBe(false);
    expect(details.nextElementSibling).toBe(ledger);
    expect(root.querySelector('hlm-select')).not.toBeNull();
    expect(root.querySelectorAll('nav button[aria-current="step"]')).toHaveLength(1);
  });
});
