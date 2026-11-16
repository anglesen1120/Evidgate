import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { CopyValueButtonComponent } from '../values/copy-value-button.component';

import { IDENTITY_BADGE_CLASSES } from './badge-classes';

@Component({
  selector: 'app-hash-chip',
  imports: [HlmBadge, CopyValueButtonComponent],
  host: {
    class: 'inline-flex min-w-0 max-w-full items-start gap-[var(--eg-space-2)]',
  },
  template: `
    <span
      hlmBadge
      [variant]="'outline'"
      class="identity-chip hash-chip"
      [class]="badgeClasses"
      [fullValue]="hasValue() ? value() : null"
      [attr.data-full-value]="hasValue() ? value() : null"
    >
      @if (hasValue()) {
        <span class="identity-chip__value min-w-0 whitespace-nowrap">{{ displayValue() }}</span>
      } @else {
        <span class="identity-chip__label" i18n="@@shared.hash-chip.unavailable"
          >Hash unavailable</span
        >
      }
    </span>
    @if (hasValue()) {
      <app-copy-value-button [value]="value()" [label]="hashLabel" [copyLabel]="copyLabel()" />
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HashChipComponent {
  readonly value = input.required<string>();
  /** Visible character budget split between both ends; non-positive/non-finite skips elision. */
  readonly abbreviate = input(12);
  readonly copyLabel = input($localize`:@@shared.hash-chip.copy:Copy hash`);
  protected readonly hashLabel = $localize`:@@shared.hash-chip.label:Hash`;
  protected readonly badgeClasses = IDENTITY_BADGE_CLASSES;
  protected readonly hasValue = computed(() => Boolean(this.value()?.trim()));
  protected readonly displayValue = computed(() => {
    const value = this.value();
    const length = this.abbreviate();
    if (!value || !Number.isFinite(length) || length <= 0) return value;
    const limit = Math.max(2, Math.floor(length));
    if (value.length <= limit) return value;
    return `${value.slice(0, Math.ceil(limit / 2))}…${value.slice(-Math.floor(limit / 2))}`;
  });
}
