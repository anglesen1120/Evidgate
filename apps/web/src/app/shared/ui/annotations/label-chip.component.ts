import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type LabelKind = 'flaky' | 'healed';
export type HealingMode = 'shadow' | 'official';

@Component({
  selector: 'app-label-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './label-chip.component.css',
  template: `
    @if (kind() === 'flaky') {
      <span class="label label-flaky" i18n="@@shared.label-chip.flaky">Flaky</span>
      <p class="label-explanation" i18n="@@shared.label-chip.flaky-explanation">
        Flaky is a case label. It never overrides attempt #1 or the stored has_failures outcome.
      </p>
      @if (originalFailure().trim()) {
        <div class="original-failure">{{ originalFailure() }}</div>
      }
    } @else if (kind() === 'healed') {
      <div class="original-block">
        <strong i18n="@@shared.label-chip.original-failure">Original failure</strong>
        @if (originalFailure().trim()) {
          <div class="original-failure">{{ originalFailure() }}</div>
        } @else {
          <p i18n="@@shared.label-chip.original-unavailable">Original failure unavailable</p>
        }
      </div>
      <div class="candidate-block">
        @if (candidateHash().trim()) {
          <span class="label label-healed" i18n="@@shared.label-chip.healed"
            >Healed with candidate</span
          >
          <code class="candidate-hash">{{ candidateHash() }}</code>
        } @else {
          <span class="label label-unavailable" i18n="@@shared.label-chip.hash-unavailable"
            >Candidate hash unavailable</span
          >
        }
        @switch (mode()) {
          @case ('shadow') {
            <span class="label-mode" i18n="@@shared.label-chip.shadow">Shadow evaluation</span>
          }
          @case ('official') {
            <span class="label-mode" i18n="@@shared.label-chip.official">Official evaluation</span>
          }
          @default {
            <span class="label-mode" i18n="@@shared.label-chip.mode-unavailable"
              >Evaluation group unavailable</span
            >
          }
        }
      </div>
    } @else {
      <span class="label label-unavailable" i18n="@@shared.label-chip.unavailable"
        >Label unavailable</span
      >
    }
  `,
})
export class LabelChip {
  readonly kind = input.required<LabelKind>();
  /** Required for healed presentation; empty metadata renders explicit unavailable copy. */
  readonly candidateHash = input('');
  readonly mode = input<HealingMode>();
  /** Raw pre-healing failure, never rewritten or inferred from the candidate. */
  readonly originalFailure = input('');
}
