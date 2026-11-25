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
      @if (columnParts(); as parts) {
        <time class="column-time" [attr.datetime]="value()" [attr.title]="label + ' ' + utcLabel"
          ><span>{{ parts[0] }}</span
          >&ngsp;<span>{{ parts[1] }}</span></time
        >
      } @else {
        <time [attr.datetime]="value()"
          >{{ label }} <span>{{ utcLabel }}</span></time
        >
      }
    } @else {
      <span>{{ unavailable }}</span>
    }
  `,
  styles: `
    :host {
      min-inline-size: 0;
      font-variant-numeric: tabular-nums;
    }
    @media (min-width: 768px) {
      .column-time {
        display: inline-block;
        white-space: nowrap;
      }
    }
    .column-time span {
      white-space: nowrap;
    }
    @media (max-width: 767px) {
      .column-time {
        white-space: normal;
        overflow-wrap: normal;
        word-break: normal;
      }
    }
  `,
})
export class AbsoluteTimeComponent {
  private readonly locale = inject(LOCALE_ID);

  readonly value = input.required<string | null | undefined>();
  readonly utcColumn = input(false);
  private readonly dateTimeFormat = shortDateFormat(this.locale, true, 'medium');
  protected readonly utcLabel = $localize`:@@shared.values.utc:UTC`;
  protected readonly unavailable = $localize`:@@shared.values.time-unavailable:Time unavailable`;
  protected readonly display = computed(() => {
    const raw = this.value();
    if (typeof raw !== 'string') return null;
    const instant = rfc3339InstantMs(raw);
    return instant === null ? null : formatDate(instant, this.dateTimeFormat, this.locale, 'UTC');
  });
  protected readonly columnParts = computed(() => {
    const label = this.utcColumn() ? this.display() : null;
    if (!label) return null;
    // shortDateFormat separates its calendar and clock with an ASCII space in both supported locales.
    const boundary = label.indexOf(' ');
    return [label.slice(0, boundary), label.slice(boundary + 1)] as const;
  });
}
