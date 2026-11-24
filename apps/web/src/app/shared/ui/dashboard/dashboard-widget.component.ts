import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import type { HlmIconName } from '@evidgate/ui/icon';
import type { ApiError } from '../../../core/http/api-error';
import { retryWithDashboardFocus } from './dashboard-retry-focus';
import { EmptyPatternComponent } from '../states/empty-pattern.component';
import { ErrorPatternComponent } from '../states/error-pattern.component';
import { LoadingPatternComponent } from '../states/loading-pattern.component';

@Component({
  selector: 'app-dashboard-widget',
  imports: [HlmCardImports, EmptyPatternComponent, ErrorPatternComponent, LoadingPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="h-full min-w-0 gap-0 overflow-hidden py-0"
      [attr.aria-label]="title()"
      [attr.aria-busy]="tableContent() ? null : loading()"
    >
      <header
        hlmCardHeader
        class="gap-(--eg-space-2) border-b border-border px-(--eg-space-5) py-(--eg-space-4)"
      >
        <h2 #retryHeading hlmCardTitle [attr.tabindex]="-1" class="m-0 text-lg">{{ title() }}</h2>
        @if (description()) {
          <p hlmCardDescription class="m-0">{{ description() }}</p>
        }
        <div hlmCardAction><ng-content select="[dashboardActions]" /></div>
      </header>
      <div hlmCardContent class="min-w-0 p-(--eg-space-5)">
        @if (loading() && !tableContent()) {
          <app-loading-pattern [bare]="true" />
        } @else if (error(); as failure) {
          <app-error-pattern
            [bare]="true"
            [headingLevel]="3"
            [error]="failure"
            (retry)="retryWithFocus(retryHeading, retry)"
          />
        } @else if (empty() && !loading()) {
          <app-empty-pattern [bare]="true" [icon]="emptyIcon()" [title]="emptyTitle()" />
        } @else {
          <ng-content />
        }
      </div>
    </section>
  `,
})
export class DashboardWidgetComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly tableContent = input(false);
  readonly loading = input(false);
  readonly error = input<ApiError | null>(null);
  readonly empty = input(false);
  readonly emptyTitle = input('');
  readonly emptyIcon = input<HlmIconName>('lucideInbox');
  readonly retry = output<void>();

  protected readonly retryWithFocus = retryWithDashboardFocus;
}
