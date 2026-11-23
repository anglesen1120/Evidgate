import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { NumberValueComponent } from '../values/number-value.component';

export type DashboardTone = 'neutral' | 'success' | 'danger' | 'warning' | 'info';

@Component({
  selector: 'app-dashboard-kpi',
  imports: [HlmCard, NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="h-full min-w-0 gap-(--eg-space-2) p-(--eg-space-5)"
      [attr.aria-label]="label()"
      [attr.data-tone]="tone()"
    >
      <h3 class="m-0 text-sm font-medium text-muted-foreground">{{ label() }}</h3>
      <p class="m-0 text-3xl font-semibold tabular-nums wrap-anywhere">
        @if (numericValue(); as current) {
          <app-number-value [value]="current" />
        } @else if (value() === 0) {
          <app-number-value [value]="0" />
        } @else {
          {{ value() ?? unavailableLabel() }}
        }
      </p>
      @if (detail()) {
        <p class="m-0 text-xs text-muted-foreground">{{ detail() }}</p>
      }
    </section>
  `,
})
export class DashboardKpiComponent {
  readonly label = input.required<string>();
  readonly value = input<string | number | null>(null);
  readonly detail = input('');
  readonly unavailableLabel = input('');
  readonly tone = input<DashboardTone>('neutral');
  protected readonly numericValue = computed(() =>
    typeof this.value() === 'number' ? this.value() : null,
  );
}
