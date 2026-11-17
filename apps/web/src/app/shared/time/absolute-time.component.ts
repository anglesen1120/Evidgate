import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
} from '@angular/core';

import { rfc3339InstantMs } from './rfc3339-instant';
import { shortDateFormat } from './short-date-format';

@Component({
  selector: 'app-absolute-time',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (display(); as label) {
      <time [attr.datetime]="value()"
        >{{ label }} <span>{{ utcLabel }}</span></time
      >
    } @else {
      <span>{{ unavailable }}</span>
    }
  `,
  styles: `
    :host {
      min-inline-size: 0;
      font-variant-numeric: tabular-nums;
    }
  `,
})
export class AbsoluteTimeComponent {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input.required<string | null | undefined>();
  private readonly dateTimeFormat = shortDateFormat(this.locale, true, 'medium');
  protected readonly utcLabel = $localize`:@@shared.values.utc:UTC`;
  protected readonly unavailable = $localize`:@@shared.values.time-unavailable:Time unavailable`;
  protected readonly display = computed(() => {
    const raw = this.value();
    if (typeof raw !== 'string') return null;
    const instant = rfc3339InstantMs(raw);
    return instant === null ? null : formatDate(instant, this.dateTimeFormat, this.locale, 'UTC');
  });
}
