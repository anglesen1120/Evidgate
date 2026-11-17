import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
} from '@angular/core';

import { type ExactNumber, ExactNumberFormatter } from './exact-number';

@Component({
  selector: 'app-number-value',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (parts(); as groups) {
      @for (group of groups; track $index) {
        <span class="number-group">{{ group }}</span>
        @if (!$last) {
          <wbr />
        }
      }
    } @else {
      <span>{{ unavailable }}</span>
    }
  `,
  styles: `
    :host {
      min-inline-size: 0;
      max-inline-size: 100%;
      font-variant-numeric: tabular-nums;
      overflow-wrap: normal;
      word-break: normal;
      white-space: normal;
    }
    .number-group {
      display: inline-block;
      white-space: nowrap;
      overflow-wrap: normal;
      word-break: normal;
    }
  `,
})
export class NumberValueComponent {
  private readonly formatter = new ExactNumberFormatter(inject(LOCALE_ID));

  readonly value = input.required<ExactNumber>();
  readonly unit = input('');
  protected readonly unavailable = $localize`:@@shared.values.number-unavailable:Number unavailable`;
  protected readonly parts = computed(() => {
    const formatted = this.formatter.format(this.value());
    if (formatted === null) return null;
    const groups = formatted.split(this.formatter.group);
    const unit = this.unit();
    // Break only after complete digit groups; the final group, fraction and unit stay together.
    return groups.map((group, index) => {
      if (index < groups.length - 1) return `${group}${this.formatter.group}`;
      return unit ? `${group}\u00a0${unit.replace(/\s/g, '\u00a0')}` : group;
    });
  });
}
