// State sheet: docs/design/web/state-sheet.md#error-pattern
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import type { ApiError } from '../../../core/http/api-error';
import { RetryAfterComponent } from '../conflict';
import { EmptyPatternComponent } from './empty-pattern.component';
import { ErrorPatternComponent } from './error-pattern.component';
import { ForbiddenPatternComponent } from './forbidden-pattern.component';
import { LoadingPatternComponent } from './loading-pattern.component';
import { NotFoundPatternComponent } from './not-found-pattern.component';
import { patternForError } from './state-pattern';

@Component({
  selector: 'app-request-state',
  standalone: true,
  imports: [
    LoadingPatternComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    NotFoundPatternComponent,
    RetryAfterComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './request-state.component.css',
  template: `
    @switch (status()) {
      @case ('loading') {
        <app-loading-pattern />
      }
      @case ('error') {
        @switch (pattern()) {
          @case ('forbidden') {
            <app-forbidden-pattern />
          }
          @case ('not_found') {
            <app-not-found-pattern />
          }
          @default {
            @if (retryAfterError(); as failure) {
              <app-retry-after
                [kind]="failure.kind === 'unavailable' ? 'unavailable' : 'rate_limited'"
                [retryAfterSeconds]="failure.retryAfterSeconds"
                [retryEligible]="retryEligible()"
                [busy]="retryBusy()"
                [code]="failure.code"
                [requestId]="failure.requestId"
                (retry)="retry.emit()"
              />
            } @else {
              <app-error-pattern
                [error]="error()"
                [retryDisabled]="!retryEligible() || retryBusy()"
                (retry)="retry.emit()"
              />
            }
          }
        }
      }
      @case ('ready') {
        @if (empty()) {
          <app-empty-pattern
            [title]="emptyTitle()"
            [description]="emptyDescription()"
            [actionLabel]="emptyActionLabel()"
            [actionHref]="emptyActionHref()"
            (action)="emptyAction.emit()"
          />
        } @else {
          <ng-content />
        }
      }
    }
  `,
})
export class RequestStateComponent {
  readonly status = input<'loading' | 'ready' | 'error'>('loading');
  readonly error = input<ApiError | null>(null);
  readonly empty = input(false);
  readonly emptyTitle = input('');
  readonly emptyDescription = input<string | undefined>();
  readonly emptyActionLabel = input<string | undefined>();
  readonly emptyActionHref = input<string | undefined>();
  readonly retryEligible = input(false);
  readonly retryBusy = input(false);
  readonly retry = output<void>();
  readonly emptyAction = output<void>();

  protected readonly pattern = computed(() => patternForError(this.error()));
  protected readonly retryAfterError = computed(() => {
    const failure = this.error();
    return failure?.kind === 'rate_limited' ||
      (failure?.kind === 'unavailable' && failure.retryAfterSeconds !== null)
      ? failure
      : null;
  });
}
