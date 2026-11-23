import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { ApiError } from '../../../core/http/api-error';
import { retryWithDashboardFocus } from '../../../shared/ui/dashboard/dashboard-retry-focus';
import { ErrorPatternComponent } from '../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';

@Component({
  selector: 'app-dashboard-metric',
  imports: [ErrorPatternComponent, LoadingPatternComponent, NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 border-b border-border py-(--eg-space-3) last:border-b-0' },
  template: `
    <section #retryHeading [attr.tabindex]="-1" [attr.aria-label]="label()">
      <dl class="m-0 min-w-0" [attr.aria-busy]="loading()">
        <dt class="font-medium text-sm">{{ label() }}</dt>
        <dd class="m-0 mt-(--eg-space-2)">
          @if (loading()) {
            <app-loading-pattern [bare]="true" />
          } @else if (error(); as failure) {
            <app-error-pattern
              [bare]="true"
              [headingLevel]="3"
              [error]="failure"
              (retry)="retryWithFocus(retryHeading, retry)"
            />
          } @else {
            <strong class="text-2xl tabular-nums"><app-number-value [value]="value()" /></strong>
            <p class="m-0 text-xs text-muted-foreground">{{ description() }}</p>
          }
        </dd>
      </dl>
    </section>
  `,
})
export class DashboardMetricComponent {
  readonly label = input.required<string>();
  readonly value = input<number | null>(null);
  readonly description = input('');
  readonly loading = input(false);
  readonly error = input<ApiError | null>(null);
  readonly retry = output<void>();

  protected readonly retryWithFocus = retryWithDashboardFocus;
}
