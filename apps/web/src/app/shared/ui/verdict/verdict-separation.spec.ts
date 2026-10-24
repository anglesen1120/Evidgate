// State sheet: docs/design/web/state-sheet.md#evidencebadge
// State sheet: docs/design/web/state-sheet.md#status-pills
// State sheet: docs/design/web/state-sheet.md#decisionchip
import {
  ChangeDetectionStrategy,
  Component,
  input,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';

import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
  type Completeness,
  type DecisionValue,
  type Outcome,
} from './index';

@Component({
  selector: 'app-verdict-separation-test',
  standalone: true,
  imports: [EvidenceBadgeComponent, OutcomePillComponent, DecisionChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-evidence-badge [completeness]="completeness()" />
    <app-outcome-pill [outcome]="outcome()" />
    <app-decision-chip [decision]="decision()" />
  `,
})
class VerdictSeparationTestComponent {
  readonly completeness = input<Completeness | null | undefined>();
  readonly outcome = input<Outcome | null | undefined>();
  readonly decision = input<DecisionValue | null | undefined>();
}

const completenessCases = [
  { value: 'complete', tone: 'success', text: 'Evidence complete' },
  { value: 'incomplete', tone: 'warning', text: 'Evidence incomplete' },
  { value: undefined, tone: 'neutral', text: 'Evidence status unavailable' },
  { value: null, tone: 'neutral', text: 'Evidence status unavailable' },
  { value: 'unknown', tone: 'neutral', text: 'Evidence status unavailable' },
] as const;
const outcomeCases = [
  { value: 'all_passed', tone: 'success', text: 'All tests passed' },
  { value: 'has_failures', tone: 'danger', text: 'Has failures' },
  { value: 'no_results', tone: 'neutral', text: 'No results' },
  { value: undefined, tone: 'neutral', text: 'Outcome unavailable' },
  { value: null, tone: 'neutral', text: 'Outcome unavailable' },
  { value: 'unknown', tone: 'neutral', text: 'Outcome unavailable' },
] as const;
const decisionCases = [
  { value: 'PASS', tone: 'success', text: 'PASS' },
  { value: 'FAIL', tone: 'danger', text: 'FAIL' },
  { value: 'INCONCLUSIVE', tone: 'warning', text: 'INCONCLUSIVE' },
  { value: undefined, tone: 'pending', text: 'pending' },
  { value: null, tone: 'pending', text: 'pending' },
  { value: 'pending', tone: 'pending', text: 'pending' },
  { value: 'unknown', tone: 'neutral', text: 'Decision unavailable' },
] as const;

const cases = completenessCases.flatMap((completeness) =>
  outcomeCases.flatMap((outcome) =>
    decisionCases.map((decision) => ({
      name: `${String(completeness.value)} / ${String(outcome.value)} / ${String(decision.value)}`,
      completeness,
      outcome,
      decision,
    })),
  ),
);

describe('every combination of completeness, outcome and decision rendered side by side (210 combinations; AC8 / FR-09-2)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VerdictSeparationTestComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it.each(cases)(
    'renders $name side by side without deriving another dimension',
    ({ completeness, outcome, decision }) => {
      const fixture = TestBed.createComponent(VerdictSeparationTestComponent);
      // Runtime unknown values intentionally cross the typed template boundary, as stale input can.
      fixture.componentRef.setInput('completeness', completeness.value);
      fixture.componentRef.setInput('outcome', outcome.value);
      fixture.componentRef.setInput('decision', decision.value);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const evidence = root.querySelector('app-evidence-badge');
      const testOutcome = root.querySelector('app-outcome-pill');
      const releaseDecision = root.querySelector('app-decision-chip');

      for (const [element, expected] of [
        [evidence, completeness],
        [testOutcome, outcome],
        [releaseDecision, decision],
      ] as const) {
        expect(element?.querySelector('.verdict__value')?.textContent?.trim()).toBe(expected.text);
        expect(element?.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe(
          expected.tone,
        );
        expect(element?.querySelector('[tabindex], [role="button"], [aria-disabled]')).toBeNull();
      }

      expect(root.textContent?.includes('PASS')).toBe(decision.value === 'PASS');
      expect(root.textContent?.includes('FAIL')).toBe(decision.value === 'FAIL');
      expect(evidence?.textContent).not.toMatch(/PASS|FAIL|Passed|passed/);
      expect(testOutcome?.textContent).not.toMatch(/PASS|FAIL/);

      if (completeness.value !== 'complete') {
        expect(evidence?.querySelector('[data-tone]')?.getAttribute('data-tone')).not.toBe(
          'success',
        );
        expect(evidence?.querySelector('[data-tone="success"]')).toBeNull();
      }
      if (outcome.value !== 'all_passed') {
        expect(testOutcome?.querySelector('[data-tone]')?.getAttribute('data-tone')).not.toBe(
          'success',
        );
        expect(testOutcome?.querySelector('[data-tone="success"]')).toBeNull();
      }
      if (decision.value !== 'PASS') {
        expect(releaseDecision?.querySelector('[data-tone]')?.getAttribute('data-tone')).not.toBe(
          'success',
        );
        expect(releaseDecision?.querySelector('[data-tone="success"]')).toBeNull();
      }
      if (decision.tone === 'pending' || decision.tone === 'neutral') {
        expect(releaseDecision?.querySelector('.verdict__value')?.textContent?.trim()).toBe(
          decision.text,
        );
        for (const node of releaseDecision?.querySelectorAll('*') ?? []) {
          expect(node.textContent).not.toMatch(/PASS|FAIL/);
          expect(node.getAttribute('aria-label') ?? '').not.toMatch(/PASS|FAIL/);
          expect(node.getAttribute('title') ?? '').not.toMatch(/PASS|FAIL/);
        }
      }
      if (decision.value === 'FAIL' && completeness.value === 'complete') {
        expect(root.querySelector('app-decision-chip .verdict__value')?.textContent).toBe('FAIL');
        expect(root.textContent).not.toContain('PASS');
      }
    },
  );

  it('matches the actual success-tone selector for complete, all_passed and PASS', () => {
    const fixture = TestBed.createComponent(VerdictSeparationTestComponent);
    fixture.componentRef.setInput('completeness', 'complete');
    fixture.componentRef.setInput('outcome', 'all_passed');
    fixture.componentRef.setInput('decision', 'PASS');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    for (const selector of ['app-evidence-badge', 'app-outcome-pill', 'app-decision-chip']) {
      const element = root.querySelector(selector);
      expect(element?.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('success');
      expect(element?.querySelector('[data-tone="success"]')).not.toBeNull();
    }
    expect(root.querySelectorAll('[data-tone="success"]')).toHaveLength(3);
  });
});
