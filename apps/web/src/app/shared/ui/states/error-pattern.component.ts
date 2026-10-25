import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import type { ApiError } from '../../../core/http/api-error';

@Component({
  selector: 'app-error-pattern',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./state-panel.css', './error-pattern.component.css'],
  template: `
    <section class="state-panel state-panel--error">
      @for (failure of failures(); track failure) {
        <div role="alert" aria-atomic="true">
          @switch (headingLevel()) {
            @case (3) {
              <h3 class="state-panel__title">{{ heading() }}</h3>
            }
            @case (4) {
              <h4 class="state-panel__title">{{ heading() }}</h4>
            }
            @default {
              <h2 class="state-panel__title">{{ heading() }}</h2>
            }
          }
        </div>
      } @empty {
        @switch (headingLevel()) {
          @case (3) {
            <h3 class="state-panel__title">{{ heading() }}</h3>
          }
          @case (4) {
            <h4 class="state-panel__title">{{ heading() }}</h4>
          }
          @default {
            <h2 class="state-panel__title">{{ heading() }}</h2>
          }
        }
      }
      @if (error(); as failure) {
        <p class="state-panel__description">{{ failure.message }}</p>
        <p class="state-panel__metadata">
          <code>{{ failure.code }}</code>
        </p>
        @if (failure.requestId; as requestId) {
          <p
            class="state-panel__metadata error-pattern__request-id"
            data-testid="error-request-id"
            i18n="@@shared.states.error.request-id"
          >
            Request ID: {{ requestId }}
          </p>
        }
        @if (failure.retryable) {
          <button
            class="state-panel__action"
            type="button"
            [disabled]="retryDisabled()"
            (click)="retry.emit()"
            i18n="@@shared.states.error.retry"
          >
            Retry
          </button>
        }
      }
    </section>
  `,
})
export class ErrorPatternComponent {
  readonly error = input<ApiError | null>(null);
  readonly retryDisabled = input(false);
  /**
   * Document heading level for the state title, so a panel embedded inside a page
   * section keeps a valid outline. Defaults to 2, the standalone render.
   */
  readonly headingLevel = input<2 | 3 | 4>(2);
  readonly retry = output<void>();

  // Key only the announcement to the immutable typed error, not retry state or change detection.
  // A new failure recreates its alert wrapper; unrelated renders retain the same live-region node.
  protected readonly failures = computed(() => {
    const failure = this.error();
    return failure ? [failure] : [];
  });

  protected readonly heading = computed(() => {
    switch (this.error()?.kind) {
      case 'network':
        return $localize`:@@shared.states.error.network:Network unavailable`;
      case 'timeout':
        return $localize`:@@shared.states.error.timeout:Request timed out`;
      case 'unavailable':
        return $localize`:@@shared.states.error.unavailable:Service unavailable`;
      default:
        return $localize`:@@shared.states.error.heading:Something went wrong`;
    }
  });
}
