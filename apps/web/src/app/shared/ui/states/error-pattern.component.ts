import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmAlert, HlmAlertTitle } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';

import type { ApiError } from '../../../core/http/api-error';

@Component({
  selector: 'app-error-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    HlmAlert,
    HlmAlertTitle,
    HlmButton,
    HlmCard,
    HlmCardDescription,
    HlmCardTitle,
  ],
  host: { class: 'block min-w-0' },
  template: `
    @if (bare()) {
      <section
        class="state-panel state-panel--error state-panel--bare grid min-w-0 justify-items-start gap-(--eg-space-3) wrap-anywhere"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    } @else {
      <section
        hlmCard
        class="state-panel state-panel--error min-w-0 min-h-[calc(var(--eg-space-12)*3+var(--eg-space-8))] items-center justify-center gap-(--eg-space-3) px-(--eg-space-5) py-(--eg-space-8) text-center wrap-anywhere"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    }
    <ng-template #content>
      @for (failure of failures(); track failure) {
        <div
          hlmAlert
          [variant]="'destructive'"
          class="w-auto max-w-full border-0 bg-transparent p-0"
          [class.text-center]="!bare()"
          [class.text-start]="bare()"
          aria-atomic="true"
        >
          @switch (headingLevel()) {
            @case (3) {
              <h3
                hlmAlertTitle
                class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
                [textContent]="heading()"
              ></h3>
            }
            @case (4) {
              <h4
                hlmAlertTitle
                class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
                [textContent]="heading()"
              ></h4>
            }
            @default {
              <h2
                hlmAlertTitle
                class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
                [textContent]="heading()"
              ></h2>
            }
          }
        </div>
      } @empty {
        @switch (headingLevel()) {
          @case (3) {
            <h3
              hlmCardTitle
              class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
              [textContent]="heading()"
            ></h3>
          }
          @case (4) {
            <h4
              hlmCardTitle
              class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
              [textContent]="heading()"
            ></h4>
          }
          @default {
            <h2
              hlmCardTitle
              class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
              [textContent]="heading()"
            ></h2>
          }
        }
      }
      @if (error(); as failure) {
        <p
          hlmCardDescription
          class="state-panel__description m-0 max-w-full"
          [textContent]="failure.message"
        ></p>
        <p class="state-panel__metadata m-0 max-w-full text-muted-foreground">
          <code class="font-mono text-xs select-text">{{ failure.code }}</code>
        </p>
        @if (failure.requestId; as requestId) {
          <p
            class="state-panel__metadata error-pattern__request-id m-0 max-w-full font-mono text-xs text-muted-foreground select-text"
            [attr.data-testid]="'error-request-id'"
            i18n="@@shared.states.error.request-id"
          >
            Request ID: {{ requestId }}
          </p>
        }
        @if (failure.retryable) {
          <button
            hlmBtn
            [variant]="'outline'"
            class="state-panel__action h-auto min-h-(--eg-control-md) min-w-0 max-w-full whitespace-normal py-(--eg-space-2) pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
            type="button"
            [disabled]="retryDisabled()"
            (click)="retry.emit()"
            i18n="@@shared.states.error.retry"
          >
            Retry
          </button>
        }
      }
    </ng-template>
  `,
})
export class ErrorPatternComponent {
  readonly error = input<ApiError | null>(null);
  readonly retryDisabled = input(false);
  /** The containing card owns its border and inset when this is true. */
  readonly bare = input(false);
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
