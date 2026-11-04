import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { IDENTITY_BADGE_CLASSES } from './badge-classes';

@Component({
  selector: 'app-hash-chip',
  imports: [HlmBadge],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span
      hlmBadge
      [variant]="'outline'"
      class="identity-chip hash-chip"
      [class]="badgeClasses"
      [attr.data-full-value]="hasValue() ? value() : null"
    >
      @if (hasValue()) {
        @if (isAbbreviated()) {
          <span
            class="identity-chip__value min-w-0 whitespace-normal [overflow-wrap:anywhere]"
            aria-hidden="true"
            >{{ displayValue() }}</span
          >
          <span class="hash-chip__full-value sr-only">{{ value() }}</span>
        } @else {
          <span class="identity-chip__value min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
            value()
          }}</span>
        }
      } @else {
        <span
          class="identity-chip__label min-w-0 whitespace-normal [overflow-wrap:anywhere]"
          i18n="@@shared.hash-chip.unavailable"
          >Hash unavailable</span
        >
      }
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HashChipComponent {
  readonly value = input.required<string>();
  /** Leading character count; zero/non-positive/non-finite values show the full identifier. */
  readonly abbreviate = input(12);
  protected readonly badgeClasses = IDENTITY_BADGE_CLASSES;

  protected readonly hasValue = computed(() => Boolean(this.value()?.trim()));
  protected readonly displayValue = computed(() => {
    const value = this.value();
    const length = this.abbreviate();
    if (!value || !Number.isFinite(length) || length <= 0) {
      return value;
    }
    const limit = Math.max(1, Math.floor(length));
    return value.length > limit ? `${value.slice(0, limit)}…` : value;
  });
  protected readonly isAbbreviated = computed(() => this.displayValue() !== this.value());
}
