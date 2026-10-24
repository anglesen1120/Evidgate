import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-hash-chip',
  template: `
    <span class="identity-chip hash-chip" [attr.data-full-value]="hasValue() ? value() : null">
      @if (hasValue()) {
        @if (isAbbreviated()) {
          <span class="identity-chip__value" aria-hidden="true">{{ displayValue() }}</span>
          <span class="hash-chip__full-value">{{ value() }}</span>
        } @else {
          <span class="identity-chip__value">{{ value() }}</span>
        }
      } @else {
        <span class="identity-chip__label" i18n="@@shared.hash-chip.unavailable"
          >Hash unavailable</span
        >
      }
    </span>
  `,
  styleUrls: ['./identity-chip.css', './hash-chip.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HashChipComponent {
  readonly value = input.required<string>();
  /** Leading character count; zero/non-positive/non-finite values show the full identifier. */
  readonly abbreviate = input(12);

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
