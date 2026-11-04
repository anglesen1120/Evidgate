import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

export type LabelKind = 'flaky' | 'healed';
export type HealingMode = 'shadow' | 'official';

@Component({
  selector: 'app-label-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge],
  host: {
    class:
      'inline-flex min-w-0 max-w-full flex-col items-start gap-(--eg-space-3) font-sans text-sm text-foreground whitespace-normal wrap-anywhere',
  },
  template: `
    @if (kind() === 'flaky') {
      <span
        hlmBadge
        [variant]="'secondary'"
        class="label label-flaky max-w-full whitespace-normal bg-(--eg-warning-bg) text-(--eg-warning-fg)"
        i18n="@@shared.label-chip.flaky"
        >Flaky</span
      >
      <p
        class="label-explanation m-0 text-muted-foreground"
        i18n="@@shared.label-chip.flaky-explanation"
      >
        Flaky is a case label. It never overrides attempt #1 or the stored has_failures outcome.
      </p>
      @if (originalFailure().trim()) {
        <div
          class="original-failure whitespace-pre-wrap wrap-anywhere"
          [textContent]="originalFailure()"
        ></div>
      }
    } @else if (kind() === 'healed') {
      <div class="original-block flex min-w-0 max-w-full flex-col gap-(--eg-space-1)">
        <strong i18n="@@shared.label-chip.original-failure">Original failure</strong>
        @if (originalFailure().trim()) {
          <div
            class="original-failure whitespace-pre-wrap wrap-anywhere"
            [textContent]="originalFailure()"
          ></div>
        } @else {
          <p class="m-0" i18n="@@shared.label-chip.original-unavailable">
            Original failure unavailable
          </p>
        }
      </div>
      <div
        class="candidate-block flex min-w-0 max-w-full flex-wrap items-center gap-(--eg-space-3)"
      >
        @if (candidateHash().trim()) {
          <span
            hlmBadge
            [variant]="'secondary'"
            class="label label-healed max-w-full whitespace-normal bg-(--eg-info-bg) text-(--eg-info-fg)"
            i18n="@@shared.label-chip.healed"
            >Healed with candidate</span
          >
          <code
            hlmBadge
            [variant]="'outline'"
            class="candidate-hash min-w-0 max-w-full rounded-sm bg-secondary font-mono text-xs whitespace-pre-wrap wrap-anywhere"
            >{{ candidateHash() }}</code
          >
        } @else {
          <span
            hlmBadge
            [variant]="'secondary'"
            class="label label-unavailable max-w-full whitespace-normal"
            i18n="@@shared.label-chip.hash-unavailable"
            >Candidate hash unavailable</span
          >
        }
        @switch (mode()) {
          @case ('shadow') {
            <span class="label-mode text-xs" i18n="@@shared.label-chip.shadow"
              >Shadow evaluation</span
            >
          }
          @case ('official') {
            <span class="label-mode text-xs" i18n="@@shared.label-chip.official"
              >Official evaluation</span
            >
          }
          @default {
            <span class="label-mode text-xs" i18n="@@shared.label-chip.mode-unavailable"
              >Evaluation group unavailable</span
            >
          }
        }
      </div>
    } @else {
      <span
        hlmBadge
        [variant]="'secondary'"
        class="label label-unavailable max-w-full whitespace-normal"
        i18n="@@shared.label-chip.unavailable"
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
