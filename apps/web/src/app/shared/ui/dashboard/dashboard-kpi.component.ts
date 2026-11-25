import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { HlmIconName } from '@evidgate/ui/icon';
import { NumberValueComponent } from '../values/number-value.component';

export type DashboardTone = 'neutral' | 'success' | 'danger' | 'warning' | 'info';

export interface DashboardMetric {
  readonly id: string;
  readonly label: string;
  readonly value: number | string | null;
  /** A known count beside a numeric value; percentages carry their sample in detail. */
  readonly denominator?: number | null;
  readonly detail: string;
  readonly unavailableLabel?: string;
}

@Component({
  selector: 'app-dashboard-kpi',
  imports: [NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <dl class="metric-list" [class.metric-strip]="metrics() !== null" [attr.aria-label]="label()">
      @for (metric of items(); track metric.id) {
        <div class="metric">
          <dt>{{ metric.label }}</dt>
          <dd>
            <span class="reading" [class.unknown]="metric.value === null">
              @if (typeof metric.value === 'string') {
                {{ metric.value }}
              } @else if (metric.value === null && metric.unavailableLabel) {
                {{ metric.unavailableLabel }}
              } @else {
                <app-number-value [value]="metric.value" />
              }
              @if (
                typeof metric.value === 'number' &&
                metric.denominator !== null &&
                metric.denominator !== undefined
              ) {
                <small>/ <app-number-value [value]="metric.denominator" /></small>
              }
            </span>
            @if (metric.detail) {
              <span class="scope">{{ metric.detail }}</span>
            }
          </dd>
        </div>
      }
    </dl>
  `,
  styleUrl: './dashboard-kpi.component.css',
})
export class DashboardKpiComponent {
  readonly label = input.required<string>();
  readonly metrics = input<readonly DashboardMetric[] | null>(null);
  readonly value = input<string | number | null>(null);
  readonly detail = input('');
  readonly unavailableLabel = input('');
  readonly tone = input<DashboardTone>('neutral');
  readonly icon = input<HlmIconName | null>(null);
  protected readonly items = computed<readonly DashboardMetric[]>(
    () =>
      this.metrics() ?? [
        {
          id: 'single',
          label: this.label(),
          value: this.value(),
          detail: this.detail(),
          unavailableLabel: this.unavailableLabel(),
        },
      ],
  );
}
