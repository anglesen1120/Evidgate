import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { hlm } from '@evidgate/ui/utils';

import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from '../chips/badge-classes';
import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';

export type LabelKind = 'flaky' | 'healed';
export type HealingMode = 'shadow' | 'official';

@Component({
  selector: 'app-label-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge, HlmIcon, CopyableIdentifierComponent],
  host: {
    class:
      'inline-flex min-w-0 max-w-full flex-col items-start gap-(--eg-space-3) font-sans text-sm text-foreground whitespace-normal wrap-anywhere',
  },
  template: `
    @if (kind() === 'flaky') {
      <span hlmBadge [variant]="'secondary'" class="label label-flaky" [class]="flakyBadgeClasses">
        <hlm-icon [name]="icons.warning" />
        <span i18n="@@shared.label-chip.flaky">Flaky</span>
      </span>
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
      <div class="candidate-block flex min-w-0 max-w-full flex-col items-start gap-(--eg-space-3)">
        @if (candidateHash().trim()) {
          <span
            hlmBadge
            [variant]="'secondary'"
            class="label label-healed"
            [class]="healedBadgeClasses"
          >
            <hlm-icon [name]="icons.info" />
            <span i18n="@@shared.label-chip.short-healed">Healed</span>
          </span>
          <p class="candidate-description m-0" i18n="@@shared.label-chip.healed">
            Healed with candidate
          </p>
          <app-copyable-identifier
            class="candidate-hash w-full"
            [value]="candidateHash()"
            [label]="candidateHashLabel"
            [truncate]="false"
          />
        } @else {
          <span
            hlmBadge
            [variant]="'secondary'"
            class="label label-unavailable"
            [class]="unavailableBadgeClasses"
          >
            <hlm-icon [name]="icons.neutral" />
            <span i18n="@@shared.label-chip.short-unavailable">Unavailable</span>
          </span>
          <p class="candidate-unavailable m-0" i18n="@@shared.label-chip.hash-unavailable">
            Candidate hash unavailable
          </p>
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
        class="label label-unavailable"
        [class]="unavailableBadgeClasses"
      >
        <hlm-icon [name]="icons.neutral" />
        <span i18n="@@shared.label-chip.unavailable">Label unavailable</span>
      </span>
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
  protected readonly flakyBadgeClasses = hlm(BADGE_CLASSES, BADGE_TONE_CLASSES.warning);
  protected readonly healedBadgeClasses = hlm(BADGE_CLASSES, BADGE_TONE_CLASSES.info);
  protected readonly unavailableBadgeClasses = hlm(BADGE_CLASSES, BADGE_TONE_CLASSES.neutral);
  protected readonly icons = BADGE_ICON_NAMES;
  protected readonly candidateHashLabel = $localize`:@@shared.label-chip.candidate-hash:Candidate hash`;
}
